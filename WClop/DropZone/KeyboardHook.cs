using System.Runtime.InteropServices;
using WClop.Core.DropZone;
using WClop.Core.Logging;

namespace WClop.DropZone
{
    /// <summary>
    /// A low-level keyboard hook (<c>WH_KEYBOARD_LL</c>, project.md §26.1) that reports modifier taps, for showing
    /// the drop zone under the cursor. It's only installed while a drag is under way (<see cref="Watch"/> /
    /// <see cref="StopWatching"/>), so WClop isn't in the keyboard path the rest of the time. Keys are never
    /// swallowed: the app being dragged from still sees every press.
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012, WM_APP_INSTALL = 0x8001, WM_APP_REMOVE = 0x8002;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();
        private readonly ModifierTapDetector _detector = new();
        private HookProc? _proc; // kept alive while the hook is installed
        private IntPtr _hook;
        private uint _threadId;

        public KeyboardHook()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "WClop keyboard hook" };
        }

        /// <summary>Raised on the hook thread when the watched modifier was tapped.</summary>
        public event Action? Tapped;

        public void Start()
        {
            _thread.Start();
            _started.Wait();
        }

        /// <summary>Install the hook and report taps of <paramref name="modifier"/> until <see cref="StopWatching"/>.</summary>
        public void Watch(TapModifier modifier)
        {
            if (modifier != TapModifier.None && _threadId != 0)
                PostThreadMessage(_threadId, WM_APP_INSTALL, (IntPtr)(int)modifier, IntPtr.Zero);
        }

        public void StopWatching()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_APP_REMOVE, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            _proc = HookCallback;
            // Make sure the thread has a message queue before anyone posts to it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _started.Set();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                switch (message.Message)
                {
                    case WM_APP_INSTALL:
                        _detector.Modifier = (TapModifier)(int)message.WParam;
                        _detector.Reset();
                        if (_hook == IntPtr.Zero)
                        {
                            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                            if (_hook == IntPtr.Zero)
                                Log.Warn($"Keyboard hook couldn't be installed (error {Marshal.GetLastWin32Error()}); tapping a key won't show the drop zone");
                        }

                        break;

                    case WM_APP_REMOVE:
                        Remove();
                        break;

                    default:
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                        break;
                }
            }

            Remove();
        }

        private void Remove()
        {
            _detector.Reset();
            if (_hook == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    switch ((int)wParam)
                    {
                        case WM_KEYDOWN or WM_SYSKEYDOWN:
                            _detector.KeyDown((int)info.VkCode, info.Time);
                            break;
                        case WM_KEYUP or WM_SYSKEYUP:
                            if (_detector.KeyUp((int)info.VkCode, info.Time))
                                Tapped?.Invoke();
                            break;
                    }
                }
                catch (Exception e)
                {
                    // Never let an exception escape into the hook chain.
                    Log.Error("Keyboard hook handler failed", e);
                }
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
