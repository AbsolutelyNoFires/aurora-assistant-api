using System;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace Companion
{
    /// <summary>Routes HTTP requests to UI-thread reads of Aurora's state.</summary>
    internal class Api
    {
        private readonly Companion patch;
        private readonly Actions actions;

        public Api(Companion patch)
        {
            this.patch = patch;
            actions = new Actions(patch, patch.Recorder);
        }

        public HttpResponse Handle(HttpRequest req)
        {
            var path = req.Path.TrimEnd('/');
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (path == "" || path == "/health")
                return Json(Health());
            if (path == "/forms")
                return Json(Forms());
            if (segments.Length == 2 && segments[0] == "forms")
                return FormTree(segments[1], req);
            if (segments.Length == 5 && segments[0] == "forms" && segments[2] == "controls" && req.Method == "POST")
                return ControlAction(segments[1], segments[3], segments[4], req);
            if (path == "/events")
                return Events(req);
            if (path == "/dialogs")
                return Json(Dialogs.List());
            if (segments.Length == 3 && segments[0] == "dialogs" && segments[2] == "click" && req.Method == "POST")
            {
                var err = Dialogs.Click(segments[1], Args(req).TryGetValue("button", out var b) ? b : null);
                return err == null ? Json(new { ok = true }) : Json(new { error = err }, 400);
            }

            return Json(new { error = "not found", path = req.Path }, 404);
        }

        private object Health()
        {
            return patch.OnUi(() => new
            {
                ok = true,
                patch = typeof(Companion).Assembly.GetName().Version.ToString(),
                aurora = patch.AuroraChecksum,
                // The tactical map title carries race, game date and wealth.
                title = patch.TacticalMap?.Text,
            });
        }

        private object Forms()
        {
            var known = patch.KnownFormNames();
            return patch.OnUi(() =>
            {
                var active = Form.ActiveForm;
                return patch.OpenForms().Select(f => new
                {
                    id = FormId(f),
                    title = f.Text,
                    known = known.TryGetValue(f.GetType().Name, out var k) ? k : null,
                    type = f.GetType().Name,
                    visible = f.Visible,
                    active = f == active,
                    minimized = f.WindowState == FormWindowState.Minimized,
                }).ToList();
            });
        }

        /// <summary>GET /forms/{id|known name|title}?format=text|json&amp;hidden=1&amp;max=200&amp;alltree=1</summary>
        private HttpResponse FormTree(string key, HttpRequest req)
        {
            var known = patch.KnownFormNames();
            var opt = new ReadOptions
            {
                IncludeHidden = req.Q("hidden") == "1",
                AllTreeNodes = req.Q("alltree") == "1",
                ComboItems = req.Q("items") == "1",
                MaxItems = int.TryParse(req.Q("max"), out var m) ? m : 200,
            };

            var tree = patch.OnUi(() =>
            {
                var form = FindForm(key, known);
                if (form == null)
                    return null;
                opt.ToolTips = UiReader.FindToolTips(form);
                return UiReader.Read(form, opt);
            });

            if (tree == null)
                return Json(new { error = "form not open", key }, 404);
            if (req.Q("format", "text") == "json")
                return Json(tree);
            return new HttpResponse { ContentType = "text/plain; charset=utf-8", Body = UiReader.ToText(tree) };
        }

        /// <summary>
        /// POST /forms/{form}/controls/{name}/{click|set|select|doubleclick} with value/index/wait
        /// as query parameters or a JSON object body.
        /// </summary>
        private HttpResponse ControlAction(string formKey, string controlName, string action, HttpRequest req)
        {
            var args = Args(req);
            var known = patch.KnownFormNames();
            var form = patch.OnUi(() => FindForm(formKey, known));
            if (form == null)
                return Json(new { error = "form not open", form = formKey }, 404);
            int wait = args.TryGetValue("wait", out var w) && int.TryParse(w, out var ms) ? ms : 3000;
            var result = actions.Run(form, controlName, action, args, wait);
            return Json(result, result.Status == "error" ? 400 : 200);
        }

        /// <summary>GET /events?since=N&amp;wait=ms&amp;limit=N — long-polls until events after N exist.</summary>
        private HttpResponse Events(HttpRequest req)
        {
            long since = long.TryParse(req.Q("since"), out var s) ? s : patch.Recorder.LatestSeq;
            int wait = int.TryParse(req.Q("wait"), out var w) ? Math.Min(w, 60000) : 0;
            int limit = int.TryParse(req.Q("limit"), out var l) ? l : 500;
            var events = patch.Recorder.Since(since, wait, limit);
            return Json(new { latest = patch.Recorder.LatestSeq, events });
        }

        private static System.Collections.Generic.Dictionary<string, string> Args(HttpRequest req)
        {
            var args = new System.Collections.Generic.Dictionary<string, string>(req.Query, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(req.Body) && req.Body.TrimStart().StartsWith("{"))
            {
                var body = Newtonsoft.Json.Linq.JObject.Parse(req.Body);
                foreach (var prop in body.Properties())
                    args[prop.Name] = prop.Value.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)prop.Value : prop.Value.ToString();
            }
            return args;
        }

        private Form FindForm(string key, System.Collections.Generic.Dictionary<string, string> known) =>
            patch.OpenForms().FirstOrDefault(f =>
                FormId(f) == key ||
                (known.TryGetValue(f.GetType().Name, out var k) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(f.Text, key, StringComparison.OrdinalIgnoreCase));

        private static string FormId(Form f) => f.Handle.ToInt64().ToString("x");

        private static HttpResponse Json(object o, int status = 200) =>
            new HttpResponse { Status = status, Body = JsonConvert.SerializeObject(o, Formatting.Indented, JsonSettings) };

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        };
    }
}
