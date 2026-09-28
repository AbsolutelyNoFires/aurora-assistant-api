using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AuroraPatch;
using HarmonyLib;

namespace AuroraAssistantApi
{
    /// <summary>
    /// Exposes Aurora's UI as text, a stream of UI events, and player-like actions over HTTP, for
    /// assistants and other tools.
    /// </summary>
    public class AuroraAssistantApi : AuroraPatch.Patch
    {
        public static string Version =>
            typeof(AuroraAssistantApi).Assembly.GetName().Version.ToString(3);

        public override string Description =>
            $"Aurora Assistant API {Version}: HTTP API on {(Settings.Lan ? "all interfaces" : "127.0.0.1")}:{Settings.Port}. " +
            "Use Change settings for network access, port and a command to launch with the game.";

        internal Settings Settings { get; private set; } = new Settings();

        public override IEnumerable<string> Dependencies => new[] { "Lib" };

        internal Lib.Lib Lib { get; private set; }

        private HttpServer server;

        internal EventRecorder Recorder { get; private set; }

        protected override void Loaded(Harmony harmony)
        {
            Settings = LoadSettings();
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
            var address = Settings.Lan ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
            server = new HttpServer(address, Settings.Port, api.Handle, LogError);
            server.Start();
            LogInfo($"Version {Version} listening on http://{address}:{Settings.Port}/");

            RunLaunchCommand();
        }

        protected override void ChangeSettings()
        {
            using (var form = new SettingsForm(LoadSettings(), Version))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    Settings = form.Result;
                    Serialize("settings", Settings);
                }
            }
        }

        private Settings LoadSettings()
        {
            // Deserialize logs an error when the file does not exist yet, so check first.
            var path = System.IO.Path.Combine(Folder, "settings.json");
            return System.IO.File.Exists(path) ? Deserialize<Settings>("settings") ?? new Settings() : new Settings();
        }

        /// <summary>Start the optional companion command (e.g. the assistant bridge) through cmd.exe.</summary>
        private void RunLaunchCommand()
        {
            if (string.IsNullOrWhiteSpace(Settings.LaunchCommand))
                return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + Settings.LaunchCommand)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Folder,
                });
                LogInfo("Launched: " + Settings.LaunchCommand);
            }
            catch (Exception e)
            {
                LogError("Launch command failed: " + e.Message);
            }
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
