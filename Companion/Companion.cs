using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AuroraPatch;
using HarmonyLib;

namespace Companion
{
    /// <summary>
    /// Exposes Aurora's UI (as text) and, later, game state and actions over a local HTTP API
    /// for the companion bridge.
    /// </summary>
    public class Companion : AuroraPatch.Patch
    {
        public const int Port = 47100;

        public override string Description => "HTTP API for the LLM companion bridge (127.0.0.1:" + Port + ").";

        public override IEnumerable<string> Dependencies => new[] { "Lib" };

        internal Lib.Lib Lib { get; private set; }

        private HttpServer server;

        internal EventRecorder Recorder { get; private set; }

        protected override void Loaded(Harmony harmony)
        {
            Lib = GetDependency<Lib.Lib>("Lib");
            harmony.Patch(
                AccessTools.Method(typeof(Button), "OnClick"),
                prefix: new HarmonyMethod(typeof(EventRecorder), nameof(EventRecorder.ButtonClickPrefix)));
        }

        protected override void Started()
        {
            Recorder = new EventRecorder(this);
            EventRecorder.Instance = Recorder;
            OnUi(() =>
            {
                Dialogs.UiThreadId = Dialogs.GetCurrentThreadId();
                Recorder.Start();
                // The AuroraPatch launcher stays open after starting and covers the map's toolbar.
                foreach (var f in OpenForms().Where(f => f.GetType().Name == "AuroraPatchForm"))
                    f.WindowState = FormWindowState.Minimized;
                return true;
            });

            var api = new Api(this);
            server = new HttpServer(Port, api.Handle, LogError);
            server.Start();
            LogInfo("Listening on http://127.0.0.1:" + Port + "/");
        }

        /// <summary>Run a function on Aurora's UI thread and return its result.</summary>
        internal T OnUi<T>(Func<T> func)
        {
            var map = TacticalMap;
            if (map == null || !map.InvokeRequired)
                return func();

            T result = default;
            Exception error = null;
            map.Invoke((Action)(() =>
            {
                try { result = func(); }
                catch (Exception e) { error = e; }
            }));
            if (error != null)
                throw new InvalidOperationException(error.Message, error);
            return result;
        }

        /// <summary>Map Aurora's obfuscated form class names to Lib's known form types.</summary>
        internal Dictionary<string, string> KnownFormNames()
        {
            var known = new Dictionary<string, string>();
            if (Lib == null)
                return known;
            foreach (global::Lib.AuroraType t in Enum.GetValues(typeof(global::Lib.AuroraType)))
            {
                try
                {
                    var type = Lib.SignatureManager.Get(t);
                    if (type != null && !known.ContainsKey(type.Name))
                        known[type.Name] = t.ToString();
                }
                catch { }
            }
            return known;
        }

        internal List<Form> OpenForms() => Application.OpenForms.Cast<Form>().ToList();
    }
}
