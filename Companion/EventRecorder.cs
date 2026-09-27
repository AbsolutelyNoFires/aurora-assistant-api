using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Companion
{
    internal class UiEvent
    {
        public long Seq;
        public DateTime Time;
        public string GameTime;
        /// <summary>user (real player input), api (companion action), or game (window lifecycle).</summary>
        public string Source;
        /// <summary>form_open, form_close, form_focus, click, check, select, text, tab, dialog_open, dialog_close.</summary>
        public string Type;
        public string Form;
        public string FormTitle;
        public string Control;
        public string Kind;
        public string Label;
        public string Value;
        public string From;
    }

    /// <summary>
    /// Records what happens in Aurora's UI as a stream of semantic events. Handlers are attached to
    /// every control of every open form; a message filter tells real player input apart from
    /// Aurora changing its own controls. Keystrokes in a text box are coalesced into one edit.
    /// </summary>
    internal class EventRecorder : IMessageFilter
    {
        private const int Capacity = 5000;
        private const int UserInputWindowMs = 1000;
        private const int TextIdleFlushMs = 1500;

        private readonly Companion patch;
        private readonly LinkedList<UiEvent> events = new LinkedList<UiEvent>();
        private readonly object sync = new object();
        private long nextSeq = 1;

        // UI-thread state.
        private readonly HashSet<Form> hookedForms = new HashSet<Form>();
        private readonly HashSet<Control> hookedControls = new HashSet<Control>();
        private readonly Dictionary<Control, PendingText> pendingText = new Dictionary<Control, PendingText>();
        // Text box contents when the box last gained focus or was last flushed, for "from" values.
        private readonly Dictionary<Control, string> textBefore = new Dictionary<Control, string>();
        private Dictionary<string, Dialogs.Dialog> openDialogs = new Dictionary<string, Dialogs.Dialog>();
        private IntPtr lastInputHwnd;
        private int lastInputTick = int.MinValue / 2;
        private Form lastActiveForm;
        private System.Windows.Forms.Timer timer;

        /// <summary>The control the companion is acting on through the API, while the action runs.</summary>
        public Control ApiTarget;

        private class PendingText
        {
            public string From;
            public string Source;
            public int LastTick;
        }

        public EventRecorder(Companion patch)
        {
            this.patch = patch;
        }

        /// <summary>Must be called on the UI thread.</summary>
        public void Start()
        {
            Application.AddMessageFilter(this);
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (s, e) => Poll();
            timer.Start();
            Poll();
        }

        // ---- Reading ------------------------------------------------------------------------

        public long LatestSeq
        {
            get { lock (sync) return nextSeq - 1; }
        }

        /// <summary>Events with Seq &gt; since, waiting up to waitMs for at least one.</summary>
        public List<UiEvent> Since(long since, int waitMs, int limit)
        {
            var deadline = Environment.TickCount + waitMs;
            lock (sync)
            {
                while (nextSeq - 1 <= since)
                {
                    int remaining = deadline - Environment.TickCount;
                    if (remaining <= 0)
                        break;
                    Monitor.Wait(sync, remaining);
                }
                return events.Where(e => e.Seq > since).Take(limit).ToList();
            }
        }

        // ---- Input tracking -----------------------------------------------------------------

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A, WM_CHAR = 0x0102;
            switch (m.Msg)
            {
                case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_CHAR:
                case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_RBUTTONDOWN: case WM_MBUTTONDOWN: case WM_MOUSEWHEEL:
                    lastInputHwnd = m.HWnd;
                    lastInputTick = Environment.TickCount;
                    break;
            }
            return false;
        }

        /// <summary>Who caused a change on this control, or null if it was Aurora itself.</summary>
        private string SourceFor(Control c)
        {
            // Only the targeted control counts as the companion's action; everything else that
            // changes meanwhile is Aurora reacting to it.
            var api = ApiTarget;
            if (api != null && api == c)
                return "api";
            if (Environment.TickCount - lastInputTick > UserInputWindowMs)
                return null;
            var target = Control.FromChildHandle(lastInputHwnd);
            // The input went to this control or one of its own parts (e.g. the edit box inside a combo);
            // not merely to a container around it, which would make Aurora's side effects look like the player's.
            if (target != null && (target == c || (c.Contains(target) && !(c is ContainerControl) && !(c is TabControl) && !(c is Panel) && !(c is GroupBox))))
                return "user";
            if (c.Focused)
                return "user";
            // Combo drop-down lists are separate native windows; the combo keeps focus state.
            if (c is ComboBox combo && (combo.DroppedDown || target == null))
                return "user";
            return null;
        }

        // ---- Hooking --------------------------------------------------------------------------

        private void Poll()
        {
            try
            {
                var open = patch.OpenForms();
                foreach (var form in open.Where(f => !hookedForms.Contains(f)))
                    HookForm(form);

                var active = Form.ActiveForm;
                if (active != null && active != lastActiveForm && hookedForms.Contains(active))
                {
                    lastActiveForm = active;
                    Add("game", "form_focus", active, null);
                }

                PollDialogs();
                FlushText(idleOnly: true);
            }
            catch (Exception e)
            {
                patch.LogError("EventRecorder poll failed: " + e);
            }
        }

        private void HookForm(Form form)
        {
            hookedForms.Add(form);
            form.FormClosed += (s, e) =>
            {
                FlushText(idleOnly: false);
                hookedForms.Remove(form);
                foreach (var c in hookedControls.Where(c => c.FindForm() == form || c.IsDisposed).ToList())
                {
                    hookedControls.Remove(c);
                    textBefore.Remove(c);
                }
                Add("game", "form_close", form, null);
            };
            HookTree(form);
            Add("game", "form_open", form, null);
        }

        private void HookTree(Control root)
        {
            foreach (var c in Lib.UIManager.IterateControls(root))
                HookControl(c);
        }

        private void HookControl(Control c)
        {
            if (!hookedControls.Add(c))
                return;

            c.ControlAdded += (s, e) => HookTree(e.Control);

            switch (c)
            {
                case CheckBox cb:
                    cb.CheckedChanged += (s, e) => Changed(cb, "check", cb.Checked ? "checked" : "unchecked");
                    break;
                case RadioButton rb:
                    rb.CheckedChanged += (s, e) => { if (rb.Checked) Changed(rb, "check", "selected"); };
                    break;
                case ComboBox combo:
                    combo.SelectedIndexChanged += (s, e) => Changed(combo, "select", combo.Text);
                    break;
                case ListBox lb:
                    lb.SelectedIndexChanged += (s, e) => Changed(lb, "select", lb.SelectedItem == null ? null : lb.GetItemText(lb.SelectedItem));
                    break;
                case ListView lv:
                    lv.SelectedIndexChanged += (s, e) =>
                    {
                        // Fires once for the deselect and once for the select; only report the selection.
                        if (lv.SelectedItems.Count > 0)
                            Changed(lv, "select", RowText(lv.SelectedItems[0]));
                    };
                    lv.ItemChecked += (s, e) => Changed(lv, "check", (e.Item.Checked ? "checked: " : "unchecked: ") + RowText(e.Item));
                    break;
                case TreeView tv:
                    tv.AfterSelect += (s, e) => Changed(tv, "select", e.Node?.FullPath);
                    tv.AfterCheck += (s, e) => Changed(tv, "check", (e.Node.Checked ? "checked: " : "unchecked: ") + e.Node.FullPath);
                    break;
                case TabControl tabs:
                    tabs.SelectedIndexChanged += (s, e) => Changed(tabs, "tab", tabs.SelectedTab?.Text);
                    break;
                case NumericUpDown num:
                    num.ValueChanged += (s, e) => Changed(num, "text", num.Value.ToString());
                    break;
                case TextBoxBase tb:
                    if (!tb.ReadOnly)
                    {
                        tb.Enter += (s, e) => { if (!pendingText.ContainsKey(tb)) textBefore[tb] = tb.Text; };
                        tb.TextChanged += (s, e) => TextEdited(tb);
                        tb.Leave += (s, e) => FlushText(idleOnly: false, only: tb);
                    }
                    break;
            }
        }

        private static string RowText(ListViewItem item) =>
            string.Join(" | ", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(si => si.Text));

        // ---- Recording ------------------------------------------------------------------------

        internal static EventRecorder Instance;

        /// <summary>
        /// Harmony prefix on Button.OnClick: records the click before Aurora's handler runs, so any
        /// dialog or window the click opens is journaled after it.
        /// </summary>
        internal static void ButtonClickPrefix(Button __instance)
        {
            try
            {
                var r = Instance;
                if (r != null && r.hookedControls.Contains(__instance))
                    r.Changed(__instance, "click", null);
            }
            catch { }
        }

        private void Changed(Control c, string type, string value)
        {
            var source = SourceFor(c);
            if (source == null)
                return; // Aurora updating its own controls.
            FlushText(idleOnly: false);
            Add(source, type, c.FindForm(), c, value);
        }

        private void TextEdited(TextBoxBase tb)
        {
            if (pendingText.TryGetValue(tb, out var pending))
            {
                pending.LastTick = Environment.TickCount;
                return;
            }
            var source = SourceFor(tb);
            if (source == null)
                return;
            FlushText(idleOnly: false);
            textBefore.TryGetValue(tb, out var before);
            pendingText[tb] = new PendingText { From = before, Source = source, LastTick = Environment.TickCount };
        }

        private void FlushText(bool idleOnly, Control only = null)
        {
            if (pendingText.Count == 0)
                return;
            foreach (var kv in pendingText.ToList())
            {
                if (only != null && kv.Key != only)
                    continue;
                if (idleOnly && Environment.TickCount - kv.Value.LastTick < TextIdleFlushMs)
                    continue;
                pendingText.Remove(kv.Key);
                if (kv.Key.IsDisposed)
                    continue;
                textBefore[kv.Key] = kv.Key.Text;
                Add(kv.Value.Source, "text", kv.Key.FindForm(), kv.Key, kv.Key.Text, kv.Value.From);
            }
        }

        private void PollDialogs()
        {
            var now = Dialogs.List().ToDictionary(d => d.Id);
            foreach (var d in now.Values.Where(d => !openDialogs.ContainsKey(d.Id)))
                AddRaw(new UiEvent { Source = "game", Type = "dialog_open", FormTitle = d.Title, Control = d.Id, Value = d.Message, Label = string.Join(", ", d.Buttons) });
            foreach (var d in openDialogs.Values.Where(d => !now.ContainsKey(d.Id)))
                AddRaw(new UiEvent { Source = "game", Type = "dialog_close", FormTitle = d.Title, Control = d.Id, Value = d.Message });
            openDialogs = now;
        }

        private void Add(string source, string type, Form form, Control c, string value = null, string from = null)
        {
            AddRaw(new UiEvent
            {
                Source = source,
                Type = type,
                Form = form?.Name,
                FormTitle = form == null ? null : Api.WindowName(form),
                Control = c?.Name,
                Kind = c == null ? null : UiReader.Kind(c),
                Label = c == null ? null : Label(c),
                Value = value,
                From = from,
            });
        }

        private static string Label(Control c)
        {
            var caption = Captions.For(c);
            if (caption != null)
                return caption;
            if (!(c is TextBoxBase) && !(c is ComboBox) && !string.IsNullOrWhiteSpace(c.Text))
                return c.Text.Trim();
            return null;
        }

        private void AddRaw(UiEvent e)
        {
            e.Time = DateTime.UtcNow;
            e.GameTime = GameTime();
            lock (sync)
            {
                e.Seq = nextSeq++;
                events.AddLast(e);
                while (events.Count > Capacity)
                    events.RemoveFirst();
                Monitor.PulseAll(sync);
            }
        }

        /// <summary>The tactical map title reads "Race   Saturday, 8 August 2144 05:41:05   Racial Wealth …".</summary>
        private string GameTime()
        {
            var title = patch.TacticalMap?.Text;
            if (title == null)
                return null;
            var parts = title.Split(new[] { "   " }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1].Trim() : null;
        }
    }
}
