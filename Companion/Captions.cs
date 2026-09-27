using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Companion
{
    /// <summary>
    /// Pairs caption labels with the controls they describe, the way a screen reader's
    /// "labelled by" relation would: a label on the same row to the left, else directly above.
    /// Aurora also uses Labels to display values (named txt*), so those are values, not captions.
    /// </summary>
    internal static class Captions
    {
        private const int MaxGapLeft = 250;
        private const int MaxGapAbove = 24;

        /// <summary>Aurora shows values in Labels named txt*/lbl*; other labels (label12, …) are captions.</summary>
        private static bool IsValueLabel(Control c) =>
            c is Label && (c.Name.StartsWith("txt", StringComparison.Ordinal) || c.Name.StartsWith("lbl", StringComparison.Ordinal));

        public static bool IsCaption(Control c) =>
            c is Label && !string.IsNullOrWhiteSpace(c.Text) && !IsValueLabel(c);

        public static bool IsValue(Control c) =>
            c is TextBoxBase || c is ComboBox || c is NumericUpDown || c is ListBox || c is ListView ||
            c is TreeView || c is DateTimePicker || c is TrackBar ||
            IsValueLabel(c);

        /// <summary>value control → caption label, among sibling controls.</summary>
        public static Dictionary<Control, Control> Pair(IList<Control> siblings)
        {
            var result = new Dictionary<Control, Control>();
            var captions = siblings.Where(IsCaption).ToList();
            if (captions.Count == 0)
                return result;

            // Score every (value, caption) candidate; take the closest pairs greedily so each caption is used once.
            var candidates = new List<Tuple<int, Control, Control>>();
            foreach (var v in siblings.Where(IsValue))
            {
                foreach (var cap in captions)
                {
                    int score = Score(v, cap);
                    if (score >= 0)
                        candidates.Add(Tuple.Create(score, v, cap));
                }
            }
            var usedCaptions = new HashSet<Control>();
            foreach (var cand in candidates.OrderBy(t => t.Item1))
            {
                if (result.ContainsKey(cand.Item2) || usedCaptions.Contains(cand.Item3))
                    continue;
                result[cand.Item2] = cand.Item3;
                usedCaptions.Add(cand.Item3);
            }
            return result;
        }

        /// <summary>Caption text for a single control, or null.</summary>
        public static string For(Control control)
        {
            if (control?.Parent == null)
                return null;
            var siblings = control.Parent.Controls.Cast<Control>().Where(c => c.Visible || c == control).ToList();
            return Pair(siblings).TryGetValue(control, out var cap) ? cap.Text.Trim() : null;
        }

        /// <summary>Distance-like score, or -1 if the caption cannot label the value.</summary>
        private static int Score(Control v, Control cap)
        {
            int vMid = v.Top + Math.Min(v.Height, 24) / 2;
            int capMid = cap.Top + cap.Height / 2;

            // Same row, caption to the left.
            int gapLeft = v.Left - cap.Right;
            if (gapLeft >= -4 && gapLeft <= MaxGapLeft && Math.Abs(vMid - capMid) <= Math.Max(8, cap.Height / 2 + 2))
                return gapLeft;

            // Caption directly above, roughly left-aligned or overlapping horizontally.
            int gapAbove = v.Top - cap.Bottom;
            bool overlaps = cap.Left < v.Right && cap.Right > v.Left;
            if (gapAbove >= -2 && gapAbove <= MaxGapAbove && (overlaps || Math.Abs(cap.Left - v.Left) <= 30))
                return 1000 + gapAbove;

            return -1;
        }
    }
}
