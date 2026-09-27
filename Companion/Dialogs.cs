using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Companion
{
    /// <summary>
    /// Native Win32 dialogs (MessageBox.Show) are not WinForms Forms, so they are read and
    /// answered through user32. Safe to call from any thread.
    /// </summary>
    internal static class Dialogs
    {
        public class Dialog
        {
            public string Id;
            public string Title;
            public string Message;
            public List<string> Buttons = new List<string>();
        }

        /// <summary>Native thread id of Aurora's UI thread; set at startup.</summary>
        public static uint UiThreadId;

        public static List<Dialog> List()
        {
            var dialogs = new List<Dialog>();
            if (UiThreadId == 0)
                return dialogs;
            EnumThreadWindows(UiThreadId, (hwnd, _) =>
            {
                if (IsWindowVisible(hwnd) && ClassName(hwnd) == "#32770")
                    dialogs.Add(Read(hwnd));
                return true;
            }, IntPtr.Zero);
            return dialogs;
        }

        /// <summary>Press a dialog button by its caption (ignoring '&amp;' accelerators). Returns an error or null.</summary>
        public static string Click(string id, string button)
        {
            var hwnd = new IntPtr(Convert.ToInt64(id, 16));
            if (!IsWindow(hwnd))
                return "dialog not found";
            IntPtr target = IntPtr.Zero;
            foreach (var child in Children(hwnd))
            {
                if (ClassName(child).Equals("Button", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Clean(Text(child)), button, StringComparison.OrdinalIgnoreCase))
                {
                    target = child;
                    break;
                }
            }
            if (target == IntPtr.Zero)
                return "button not found";
            // Posted, not sent: the dialog's own modal loop processes it.
            PostMessage(target, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return null;
        }

        private static Dialog Read(IntPtr hwnd)
        {
            var d = new Dialog { Id = hwnd.ToInt64().ToString("x"), Title = Text(hwnd) };
            var message = new List<string>();
            foreach (var child in Children(hwnd))
            {
                if (!IsWindowVisible(child))
                    continue;
                var cls = ClassName(child);
                var text = Text(child);
                if (cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
                    d.Buttons.Add(Clean(text));
                else if (!string.IsNullOrWhiteSpace(text))
                    message.Add(text);
            }
            d.Message = string.Join("\n", message);
            return d;
        }

        private static List<IntPtr> Children(IntPtr hwnd)
        {
            var list = new List<IntPtr>();
            EnumChildWindows(hwnd, (h, _) => { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        private static string Clean(string s) => (s ?? "").Replace("&", "");

        private static string Text(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private const uint BM_CLICK = 0x00F5;

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    }
}
