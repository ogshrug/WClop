using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WClop.Results
{
    /// <summary>Keys that act on the results only while the mouse is over them (project.md §17, "while hovering").</summary>
    internal enum HoverKey
    {
        /// <summary>Ctrl+E: edit the hovered result with its editor.</summary>
        Edit = 1,

        /// <summary>Ctrl+A: select every result in the compact list.</summary>
        SelectAll,

        /// <summary>Esc: clear the compact list's selection.</summary>
        ClearSelection,
    }

    /// <summary>
    /// The results window never has the keyboard (it doesn't activate), so its shortcuts are hotkeys registered on it
    /// for as long as the mouse is over it, and released the moment it leaves, so they never get in another app's way.
    /// </summary>
    internal sealed class HoverKeys(HwndSource window, Action<HoverKey> pressed)
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        private const uint VK_ESCAPE = 0x1B, VK_A = 0x41, VK_E = 0x45;

        private readonly HashSet<HoverKey> _registered = [];
        private bool _hooked;

        /// <summary>Registers <paramref name="keys"/> (and only those).</summary>
        public void Set(IReadOnlyCollection<HoverKey> keys)
        {
            if (!_hooked)
            {
                window.AddHook(WndProc);
                _hooked = true;
            }

            foreach (var key in _registered.Where(k => !keys.Contains(k)).ToList())
            {
                UnregisterHotKey(window.Handle, (int)key);
                _registered.Remove(key);
            }

            foreach (var key in keys.Where(k => !_registered.Contains(k)))
            {
                var (modifiers, vk) = key switch
                {
                    HoverKey.Edit => (MOD_CONTROL, VK_E),
                    HoverKey.SelectAll => (MOD_CONTROL, VK_A),
                    _ => (0u, VK_ESCAPE),
                };
                // Taken by another app: the button and menu still do the same thing.
                if (RegisterHotKey(window.Handle, (int)key, modifiers | MOD_NOREPEAT, vk))
                    _registered.Add(key);
            }
        }

        public void Clear() => Set([]);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && Enum.IsDefined(typeof(HoverKey), wParam.ToInt32()) && _registered.Contains((HoverKey)wParam.ToInt32()))
            {
                handled = true;
                pressed((HoverKey)wParam.ToInt32());
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
