using System.Runtime.InteropServices;

namespace WClop.Results
{
    internal static class WindowInterop
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WDA_NONE = 0x0;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        /// <summary>
        /// Clicking the window never takes focus from the app you're working in, and it stays out of Alt+Tab
        /// (project.md §26.1: layered, topmost, tool window with WS_EX_NOACTIVATE).
        /// </summary>
        public static void MakeNonActivating(IntPtr hwnd) => SetNonActivating(hwnd, true);

        /// <summary>
        /// Turns WS_EX_NOACTIVATE off while the window needs the keyboard (typing a new name or a crop size), and back on.
        /// </summary>
        public static void SetNonActivating(IntPtr hwnd, bool nonActivating)
        {
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;
            style = nonActivating ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }

        /// <summary>Hide the window from screenshots and screen recordings (Windows 10 2004+).</summary>
        public static void SetExcludedFromCapture(IntPtr hwnd, bool excluded) =>
            SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

        /// <summary>The window you were working in, to give the keyboard back to after typing on a card.</summary>
        public static IntPtr ForegroundWindow() => GetForegroundWindow();

        public static void GiveFocusTo(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }

        /// <summary>
        /// Windows' "Open with" dialog for a file, opening it in the app picked (without making that app the default).
        /// Returns false if it was cancelled.
        /// </summary>
        public static bool ShowOpenWith(IntPtr owner, string path)
        {
            const int OAIF_EXEC = 0x4, OAIF_HIDE_REGISTRATION = 0x20;
            const int ERROR_CANCELLED = unchecked((int)0x800704C7);
            var info = new OpenAsInfo { File = path, Flags = OAIF_EXEC | OAIF_HIDE_REGISTRATION };
            var hr = SHOpenWithDialog(owner, ref info);
            if (hr == ERROR_CANCELLED)
                return false;
            Marshal.ThrowExceptionForHR(hr);
            return true;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenAsInfo
        {
            public string File;
            public string? Class;
            public int Flags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo info);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
