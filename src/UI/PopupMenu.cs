using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinMonitor.UI
{
    /// <summary>A menu entry: text with an action, a checkable entry, a submenu or a separator.</summary>
    internal sealed class MenuEntry
    {
        public string Text = "";
        public Action? Click;
        public bool Checked;
        public bool Enabled = true;
        public bool Bold;            // shown as the default item
        public bool IsSeparator;
        public List<MenuEntry>? Items;

        public static MenuEntry Separator => new() { IsSeparator = true };
        public static MenuEntry Item(string text, Action click, bool isChecked = false) => new() { Text = text, Click = click, Checked = isChecked };
        public static MenuEntry Header(string text) => new() { Text = text, Enabled = false };
        public static MenuEntry Sub(string text, List<MenuEntry> items) => new() { Text = text, Items = items };
    }

    /// <summary>Shows a menu as a native Windows popup menu (dark, like the panel).</summary>
    internal static class PopupMenu
    {
        public static bool IsOpen { get; private set; }

        public static void Show(IntPtr owner, List<MenuEntry> entries)
        {
            var actions = new List<Action?>();
            IntPtr menu = Build(entries, actions);
            try
            {
                var (x, y) = Native.Cursor();
                Native.SetForegroundWindow(owner);   // so the menu closes when clicking elsewhere
                IsOpen = true;
                int cmd = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */, x, y, owner, IntPtr.Zero);
                IsOpen = false;
                Native.PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero);   // WM_NULL: documented fix for menus that do not close
                if (cmd > 0 && cmd <= actions.Count) actions[cmd - 1]?.Invoke();
            }
            catch (Exception ex) { Program.Log(ex); }
            finally
            {
                IsOpen = false;
                DestroyMenu(menu);
            }
        }

        private static IntPtr Build(List<MenuEntry> entries, List<Action?> actions)
        {
            IntPtr menu = CreatePopupMenu();
            foreach (var e in entries)
            {
                if (e.IsSeparator) { AppendMenu(menu, MF_SEPARATOR, 0, null); continue; }
                uint flags = MF_STRING | (e.Checked ? MF_CHECKED : 0) | (e.Enabled ? 0 : MF_GRAYED);
                if (e.Items != null)
                {
                    IntPtr sub = Build(e.Items, actions);
                    AppendMenu(menu, flags | MF_POPUP, (nuint)(nint)sub, e.Text);
                }
                else
                {
                    actions.Add(e.Click);
                    uint id = (uint)actions.Count;
                    AppendMenu(menu, flags, id, e.Text);
                    if (e.Bold) SetMenuDefaultItem(menu, id, 0);
                }
            }
            return menu;
        }

        /// <summary>Dark popup menus for the whole app (uxtheme, undocumented but stable since Windows 10 1903).</summary>
        public static void UseDarkMenus()
        {
            try
            {
                SetPreferredAppMode(2 /* ForceDark */);
                FlushMenuThemes();
            }
            catch { }
        }

        private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
        [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
    }
}
