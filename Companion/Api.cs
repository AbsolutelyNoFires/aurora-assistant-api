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

        public Api(Companion patch)
        {
            this.patch = patch;
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
            if (segments.Length == 5 && segments[0] == "forms" && segments[2] == "controls" && segments[4] == "click" && req.Method == "POST")
                return Click(segments[1], segments[3]);

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

        /// <summary>POST /forms/{form}/controls/{name}/click — PerformClick on a named button.</summary>
        private HttpResponse Click(string formKey, string controlName)
        {
            var known = patch.KnownFormNames();
            var result = patch.OnUi(() =>
            {
                var form = FindForm(formKey, known);
                if (form == null)
                    return "form not open";
                var control = Lib.UIManager.IterateControls(form).FirstOrDefault(c => c.Name == controlName);
                if (control == null)
                    return "control not found";
                if (!control.Visible || !control.Enabled)
                    return "control not visible/enabled";
                if (!(control is IButtonControl button))
                    return "control is not clickable";
                button.PerformClick();
                return null;
            });
            return result == null ? Json(new { ok = true }) : Json(new { error = result }, 400);
        }

        private Form FindForm(string key, System.Collections.Generic.Dictionary<string, string> known) =>
            patch.OpenForms().FirstOrDefault(f =>
                FormId(f) == key ||
                (known.TryGetValue(f.GetType().Name, out var k) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(f.Text, key, StringComparison.OrdinalIgnoreCase));

        private static string FormId(Form f) => f.Handle.ToInt64().ToString("x");

        private static HttpResponse Json(object o, int status = 200) =>
            new HttpResponse { Status = status, Body = JsonConvert.SerializeObject(o, Formatting.Indented) };
    }
}
