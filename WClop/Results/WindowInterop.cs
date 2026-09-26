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
        public static void MakeNonActivating(IntPtr hwnd)
        {
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }

        /// <summary>Hide the window from screenshots and screen recordings (Windows 10 2004+).</summary>
        public static void SetExcludedFromCapture(IntPtr hwnd, bool excluded) =>
            SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
