using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Companion
{
    /// <summary>
    /// Performs player-like actions on named controls. Actions run asynchronously on the UI thread:
    /// a click that opens a modal dialog does not return until the dialog closes, so the caller
    /// waits a bounded time and is told the action is still running.
    /// </summary>
    internal class Actions
    {
        private readonly Companion patch;
        private readonly EventRecorder recorder;

        public Actions(Companion patch, EventRecorder recorder)
        {
            this.patch = patch;
            this.recorder = recorder;
        }

        public class Result
        {
            /// <summary>done, running (blocked in a modal dialog), or error.</summary>
            public string Status;
            public string Error;
            public List<Dialogs.Dialog> Dialogs;
        }

        public Result Run(Form form, string controlName, string action, Dictionary<string, string> args, int waitMs)
        {
            // Resolve and validate synchronously so errors come back immediately.
            Action perform = null;
            Control target = null;
            var error = patch.OnUi(() =>
            {
                var control = Lib.UIManager.IterateControls(form).FirstOrDefault(c => c.Name == controlName);
                if (control == null)
                    return "control not found";
                if (!control.Visible || !control.Enabled)
                    return "control is not visible and enabled";
                target = control;
                return Prepare(control, action, args, out perform);
            });
            if (error != null)
                return new Result { Status = "error", Error = error };

            var done = new ManualResetEventSlim();
            string failure = null;
            patch.TacticalMap.BeginInvoke((Action)(() =>
            {
                var previous = recorder.ApiTarget;
                recorder.ApiTarget = target;
                try { perform(); }
                catch (Exception e) { failure = e.Message; }
                finally { recorder.ApiTarget = previous; done.Set(); }
            }));

            if (!done.Wait(waitMs))
                return new Result { Status = "running", Dialogs = global::Companion.Dialogs.List() };
            if (failure != null)
                return new Result { Status = "error", Error = failure };
            return new Result { Status = "done", Dialogs = NonEmpty(global::Companion.Dialogs.List()) };
        }

        private static List<Dialogs.Dialog> NonEmpty(List<Dialogs.Dialog> d) => d.Count == 0 ? null : d;

        /// <summary>Validate the action for this control and return the work to do, or an error.</summary>
        private static string Prepare(Control c, string action, Dictionary<string, string> args, out Action perform)
        {
            perform = null;
            args.TryGetValue("value", out var value);
            args.TryGetValue("index", out var indexText);
            int index = int.TryParse(indexText, out var i) ? i : -1;

            switch (action)
            {
                case "click":
                    if (c is IButtonControl button)
                    {
                        perform = () => { c.FindForm()?.Activate(); button.PerformClick(); };
                        return null;
                    }
                    return "control is not a button";

                case "set":
                    if (value == null)
                        return "missing value";
                    switch (c)
                    {
                        case TextBoxBase tb:
                            if (tb.ReadOnly) return "text box is read-only";
                            perform = () => { tb.Focus(); tb.Text = value; };
                            return null;
                        case NumericUpDown num:
                            if (!decimal.TryParse(value, out var d)) return "value is not a number";
                            perform = () => num.Value = Math.Max(num.Minimum, Math.Min(num.Maximum, d));
                            return null;
                        case CheckBox cb:
                            var on = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("checked", StringComparison.OrdinalIgnoreCase);
                            perform = () => cb.Checked = on;
                            return null;
                        case RadioButton rb:
                            perform = () => rb.Checked = true;
                            return null;
                        case ComboBox combo when combo.DropDownStyle != ComboBoxStyle.DropDownList:
                            perform = () => combo.Text = value;
                            return null;
                    }
                    return "control does not accept a value; try select";

                case "select":
                    switch (c)
                    {
                        case ComboBox combo:
                            index = index >= 0 ? index : Find(combo.Items.Cast<object>().Select(o => combo.GetItemText(o)), value);
                            if (index < 0 || index >= combo.Items.Count) return "option not found";
                            perform = () => combo.SelectedIndex = index;
                            return null;
                        case ListBox lb:
                            index = index >= 0 ? index : Find(lb.Items.Cast<object>().Select(o => lb.GetItemText(o)), value);
                            if (index < 0 || index >= lb.Items.Count) return "item not found";
                            perform = () => { lb.ClearSelected(); lb.SelectedIndex = index; };
                            return null;
                        case ListView lv:
                            index = index >= 0 ? index : Find(lv.Items.Cast<ListViewItem>().Select(it => it.Text), value);
                            if (index < 0 || index >= lv.Items.Count) return "row not found";
                            perform = () =>
                            {
                                lv.SelectedItems.Cast<ListViewItem>().ToList().ForEach(it => it.Selected = false);
                                var item = lv.Items[index];
                                item.Selected = true;
                                item.Focused = true;
                                item.EnsureVisible();
                            };
                            return null;
                        case TreeView tv:
                            var node = FindNode(tv.Nodes, value);
                            if (node == null) return "node not found (use a path like \"Parent > Child\")";
                            perform = () => { node.EnsureVisible(); tv.SelectedNode = node; };
                            return null;
                        case TabControl tabs:
                            index = index >= 0 ? index : Find(tabs.TabPages.Cast<TabPage>().Select(p => p.Text), value);
                            if (index < 0 || index >= tabs.TabPages.Count) return "tab not found";
                            perform = () => tabs.SelectedIndex = index;
                            return null;
                    }
                    return "control is not selectable";

                case "doubleclick":
                    // Several Aurora lists act on double-click (e.g. adding a component to a class).
                    // With a value, the item is selected first, as a player's double-click would.
                    Action select = null;
                    if (value != null || index >= 0)
                    {
                        var err = Prepare(c, "select", args, out select);
                        if (err != null)
                            return err;
                    }
                    var method = typeof(Control).GetMethod("OnDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    perform = () =>
                    {
                        select?.Invoke();
                        method.Invoke(c, new object[] { EventArgs.Empty });
                    };
                    return null;
            }
            return "unknown action (click, set, select, doubleclick)";
        }

        /// <summary>
        /// Exact match first, then case-insensitive, then prefix; runs of whitespace are treated as one
        /// space since Aurora's names often contain double spaces.
        /// </summary>
        private static int Find(IEnumerable<string> items, string value)
        {
            if (value == null)
                return -1;
            var list = items.Select(Normalize).ToList();
            var v = Normalize(value);
            int i = list.IndexOf(v);
            if (i < 0) i = list.FindIndex(s => s.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = list.FindIndex(s => s.StartsWith(v, StringComparison.OrdinalIgnoreCase));
            return i;
        }

        private static string Normalize(string s) =>
            System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " ");

        /// <summary>
        /// Path segments separated by " > " (node texts can contain '/' or '\'). Parents are expanded
        /// on the way down because Aurora fills some trees' children only when a node is expanded.
        /// </summary>
        private static TreeNode FindNode(TreeNodeCollection nodes, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var parts = path.Split(new[] { " > " }, StringSplitOptions.None);
            TreeNode found = null;
            for (int p = 0; p < parts.Length; p++)
            {
                var candidates = nodes.Cast<TreeNode>().ToList();
                int i = Find(candidates.Select(n => n.Text), parts[p]);
                if (i < 0)
                    return null;
                found = candidates[i];
                if (p < parts.Length - 1 && !found.IsExpanded)
                    found.Expand();
                nodes = found.Nodes;
            }
            return found;
        }
    }
}
