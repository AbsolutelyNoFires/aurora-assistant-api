using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AuroraAssistantApi
{
    internal class ReadOptions
    {
        public bool IncludeHidden;
        public int MaxItems = 200;
        public bool AllTreeNodes;
        public bool ComboItems;
        /// <summary>Tooltips found on the form, used to caption icon-only buttons.</summary>
        public List<ToolTip> ToolTips = new List<ToolTip>();
    }

    /// <summary>
    /// Reads a WinForms control tree as the text a screen reader would expose: control kind,
    /// name, caption/value and state, plus the contents of lists, grids and trees.
    /// Must run on the UI thread.
    /// </summary>
    internal static class UiReader
    {
        public static Dictionary<string, object> Read(Control control, ReadOptions opt)
        {
            var node = new Dictionary<string, object>
            {
                ["kind"] = Kind(control),
                ["name"] = control.Name,
                ["bounds"] = new[] { control.Left, control.Top, control.Width, control.Height },
            };

            var text = control.Text;
            if (!string.IsNullOrEmpty(text) && !(control is TextBoxBase) && !(control is ComboBox))
                node["text"] = text;
            if (!control.Enabled)
                node["enabled"] = false;
            if (!control.Visible)
                node["visible"] = false;
            if (!string.IsNullOrEmpty(control.AccessibleName))
                node["accessibleName"] = control.AccessibleName;
            foreach (var tip in opt.ToolTips)
            {
                var t = tip.GetToolTip(control);
                if (!string.IsNullOrEmpty(t)) { node["tooltip"] = t; break; }
            }

            switch (control)
            {
                case TextBoxBase tb:
                    node["value"] = tb.Text;
                    if (tb.ReadOnly)
                        node["readOnly"] = true;
                    break;
                case CheckBox cb:
                    node["checked"] = cb.Checked;
                    break;
                case RadioButton rb:
                    node["checked"] = rb.Checked;
                    break;
                case ComboBox combo:
                    node["value"] = combo.Text;
                    node["selectedIndex"] = combo.SelectedIndex;
                    node["itemCount"] = combo.Items.Count;
                    if (opt.ComboItems)
                        node["items"] = Items(combo.Items.Cast<object>(), combo, opt);
                    break;
                case ListBox lb:
                    node["selectedIndices"] = lb.SelectedIndices.Cast<int>().ToList();
                    node["items"] = Items(lb.Items.Cast<object>(), lb, opt);
                    break;
                case ListView lv:
                    ReadListView(lv, node, opt);
                    break;
                case TreeView tv:
                    node["selected"] = tv.SelectedNode?.FullPath;
                    node["nodes"] = ReadTreeNodes(tv.Nodes, opt, new int[] { 0 });
                    break;
                case DataGridView grid:
                    ReadGrid(grid, node, opt);
                    break;
                case TabControl tabs:
                    node["tabs"] = tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();
                    node["selectedTab"] = tabs.SelectedTab?.Text;
                    break;
                case NumericUpDown num:
                    node["value"] = num.Value;
                    break;
                case TrackBar track:
                    node["value"] = track.Value;
                    break;
            }

            var visible = control.Controls.Cast<Control>()
                .Where(c => opt.IncludeHidden || c.Visible)
                .OrderBy(c => c.Top).ThenBy(c => c.Left)
                .ToList();
            var captions = Captions.Pair(visible);
            var children = new List<Dictionary<string, object>>();
            foreach (var c in visible)
            {
                var child = Read(c, opt);
                if (captions.TryGetValue(c, out var caption))
                    child["label"] = caption.Text.Trim();
                else if (captions.Values.Contains(c))
                    child["captionOf"] = captions.First(kv => kv.Value == c).Key.Name;
                children.Add(child);
            }
            if (children.Count > 0)
                node["children"] = children;
            return node;
        }

        /// <summary>ToolTip components live in private fields of the form.</summary>
        public static List<ToolTip> FindToolTips(Form form)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            return form.GetType().GetFields(flags)
                .Where(f => typeof(ToolTip).IsAssignableFrom(f.FieldType))
                .Select(f => f.GetValue(form) as ToolTip)
                .Where(t => t != null)
                .ToList();
        }

        /// <summary>The nearest System.Windows.Forms base type, since Aurora subclasses controls.</summary>
        public static string Kind(Control control)
        {
            for (var t = control.GetType(); t != null; t = t.BaseType)
                if (t.Namespace == "System.Windows.Forms")
                    return t.Name;
            return control.GetType().Name;
        }

        private static List<string> Items(IEnumerable<object> items, ListControl owner, ReadOptions opt)
        {
            return items.Take(opt.MaxItems).Select(i => owner.GetItemText(i)).ToList();
        }

        private static void ReadListView(ListView lv, Dictionary<string, object> node, ReadOptions opt)
        {
            node["columns"] = lv.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList();
            node["rowCount"] = lv.Items.Count;
            node["rows"] = lv.Items.Cast<ListViewItem>().Take(opt.MaxItems)
                .Select(i => i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToList())
                .ToList();
            var selected = lv.SelectedIndices.Cast<int>().ToList();
            if (selected.Count > 0)
                node["selectedIndices"] = selected;
            if (lv.CheckBoxes)
                node["checkedIndices"] = lv.CheckedIndices.Cast<int>().ToList();
        }

        private static void ReadGrid(DataGridView grid, Dictionary<string, object> node, ReadOptions opt)
        {
            node["columns"] = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToList();
            node["rowCount"] = grid.Rows.Count;
            node["rows"] = grid.Rows.Cast<DataGridViewRow>().Take(opt.MaxItems)
                .Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => c.FormattedValue?.ToString() ?? "").ToList())
                .ToList();
        }

        private static List<Dictionary<string, object>> ReadTreeNodes(TreeNodeCollection nodes, ReadOptions opt, int[] budget)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (TreeNode tn in nodes)
            {
                if (budget[0]++ >= opt.MaxItems)
                    break;
                var n = new Dictionary<string, object> { ["text"] = tn.Text };
                if (tn.IsSelected)
                    n["selected"] = true;
                if (tn.Checked)
                    n["checked"] = true;
                if (tn.Nodes.Count > 0)
                {
                    if (tn.IsExpanded || opt.AllTreeNodes)
                        n["nodes"] = ReadTreeNodes(tn.Nodes, opt, budget);
                    else
                        n["collapsedChildren"] = tn.Nodes.Count;
                }
                list.Add(n);
            }
            return list;
        }

        /// <summary>Compact, indented text rendering of a node tree for LLM consumption.</summary>
        public static string ToText(Dictionary<string, object> node)
        {
            var sb = new StringBuilder();
            WriteText(node, sb, 0);
            return sb.ToString();
        }

        private static void WriteText(Dictionary<string, object> node, StringBuilder sb, int depth)
        {
            var kind = (string)node["kind"];
            bool container = kind == "Panel" || kind == "FlowLayoutPanel" || kind == "TableLayoutPanel" ||
                             kind == "SplitContainer" || kind == "SplitterPanel" || kind == "GroupBox" && !node.ContainsKey("text");
            // Skip anonymous layout containers: they add depth without meaning.
            if (container && depth > 0)
            {
                foreach (var c in Children(node))
                    WriteText(c, sb, depth);
                return;
            }

            // Captions are printed with the control they label.
            if (node.ContainsKey("captionOf"))
                return;

            var pad = new string(' ', depth * 2);
            sb.Append(pad).Append(kind);
            if (!string.IsNullOrEmpty(node["name"] as string))
                sb.Append(' ').Append(node["name"]);
            if (node.TryGetValue("label", out var label))
                sb.Append(" \"").Append(OneLine(label)).Append("\":");
            if (node.TryGetValue("text", out var text))
                sb.Append(" \"").Append(OneLine(text)).Append('"');
            if (node.TryGetValue("tooltip", out var tooltip))
                sb.Append(" tip=\"").Append(OneLine(tooltip)).Append('"');
            string block = null;
            if (node.TryGetValue("value", out var value))
            {
                var v = value?.ToString() ?? "";
                if (v.Contains("\n"))
                    block = v; // Multi-line values (e.g. design summaries) print as an indented block below.
                else
                    sb.Append(" = \"").Append(v).Append('"');
            }
            if (node.TryGetValue("checked", out var chk))
                sb.Append((bool)chk ? " [x]" : " [ ]");
            if (node.TryGetValue("selectedTab", out var tab))
                sb.Append(" tab=\"").Append(tab).Append("\" of [").Append(string.Join(", ", (List<string>)node["tabs"])).Append(']');
            if (node.TryGetValue("itemCount", out var ic) && !node.ContainsKey("items"))
                sb.Append(" (").Append(ic).Append(" options)");
            if (node.ContainsKey("enabled"))
                sb.Append(" (disabled)");
            sb.Append('\n');
            if (block != null)
            {
                foreach (var line in block.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                    sb.Append(pad).Append("  │ ").Append(line.TrimEnd()).Append('\n');
            }

            if (node.TryGetValue("items", out var items))
            {
                var sel = node.TryGetValue("selectedIndices", out var s) ? (List<int>)s
                        : node.TryGetValue("selectedIndex", out var si) ? new List<int> { (int)si } : new List<int>();
                int i = 0;
                foreach (var item in (List<string>)items)
                    sb.Append(pad).Append(sel.Contains(i++) ? "  > " : "  - ").Append(OneLine(item)).Append('\n');
            }
            if (node.TryGetValue("rows", out var rows))
            {
                // Aurora draws its headers as ordinary rows and leaves the real column headers as placeholders.
                var columns = (List<string>)node["columns"];
                if (columns.Any(c => !string.IsNullOrWhiteSpace(c) && c != "ColumnHeader"))
                    sb.Append(pad).Append("  | ").Append(string.Join(" | ", columns)).Append('\n');
                var sel = node.TryGetValue("selectedIndices", out var s) ? (List<int>)s : new List<int>();
                int i = 0;
                foreach (var row in (List<List<string>>)rows)
                {
                    if (row.All(string.IsNullOrWhiteSpace)) { i++; continue; } // spacer rows
                    sb.Append(pad).Append(sel.Contains(i++) ? "  >" : "  |").Append(' ').Append(string.Join(" | ", row.Select(OneLine))).Append('\n');
                }
                if ((int)node["rowCount"] > i)
                    sb.Append(pad).Append("  … ").Append((int)node["rowCount"] - i).Append(" more rows\n");
            }
            if (node.TryGetValue("nodes", out var nodes))
                WriteTreeNodes((List<Dictionary<string, object>>)nodes, sb, pad + "  ");

            foreach (var c in Children(node))
                WriteText(c, sb, depth + 1);
        }

        private static void WriteTreeNodes(List<Dictionary<string, object>> nodes, StringBuilder sb, string pad)
        {
            foreach (var n in nodes)
            {
                sb.Append(pad).Append(n.ContainsKey("selected") ? "> " : "- ").Append(OneLine(n["text"]));
                if (n.TryGetValue("collapsedChildren", out var cc))
                    sb.Append(" [+").Append(cc).Append(']');
                sb.Append('\n');
                if (n.TryGetValue("nodes", out var sub))
                    WriteTreeNodes((List<Dictionary<string, object>>)sub, sb, pad + "  ");
            }
        }

        private static IEnumerable<Dictionary<string, object>> Children(Dictionary<string, object> node) =>
            node.TryGetValue("children", out var c) ? (List<Dictionary<string, object>>)c : Enumerable.Empty<Dictionary<string, object>>();

        private static string OneLine(object o) =>
            (o?.ToString() ?? "").Replace("\r\n", " / ").Replace('\n', ' ').Replace('\r', ' ');
    }
}
