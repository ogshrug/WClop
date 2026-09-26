using System.Diagnostics;
using System.Runtime.InteropServices;
using WClop.Core.Logging;

namespace WClop.DropZone
{
    /// <summary>
    /// A global low-level mouse hook (<c>WH_MOUSE_LL</c>, project.md §26.1) on its own thread, reporting only what
    /// drag detection needs: left button down, movement while it's held (throttled), and release.
    /// Events are raised on the hook thread; handlers must hand work off quickly, or Windows drops the hook.
    /// </summary>
    internal sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A, WM_QUIT = 0x0012;
        private static readonly long MoveThrottleTicks = Stopwatch.Frequency / 40; // ~25 ms

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();
        private HookProc? _proc; // kept alive while the hook is installed
        private uint _threadId;
        private bool _leftDown;
        private long _lastMove;

        public MouseHook()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "WClop mouse hook" };
        }

        public event Action<int, int>? LeftDown;
        public event Action<int, int>? DragMoved;
        public event Action<int, int>? LeftUp;

        /// <summary>Wheel movement (120 per notch, positive = away from you), only while <see cref="SwallowWheel"/> is on.</summary>
        public event Action<int>? Wheel;

        /// <summary>While true, wheel input is reported through <see cref="Wheel"/> and kept from other apps.</summary>
        public volatile bool SwallowWheel;

        public void Start()
        {
            _thread.Start();
            _started.Wait();
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
            var hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                Log.Error($"Mouse hook couldn't be installed (error {Marshal.GetLastWin32Error()}); the drop zone won't appear");
            _started.Set();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (hook != IntPtr.Zero)
                UnhookWindowsHookEx(hook);
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                try
                {
                    switch ((int)wParam)
                    {
                        case WM_LBUTTONDOWN:
                            _leftDown = true;
                            LeftDown?.Invoke(info.X, info.Y);
                            break;
                        case WM_LBUTTONUP:
                            _leftDown = false;
                            LeftUp?.Invoke(info.X, info.Y);
                            break;
                        case WM_MOUSEWHEEL when SwallowWheel:
                            // Over the open drop zone the wheel picks a compression preset; the app underneath
                            // (often the drag source) must not scroll too.
                            Wheel?.Invoke((short)(info.MouseData >> 16));
                            return (IntPtr)1;

                        case WM_MOUSEMOVE when _leftDown:
                            var now = Stopwatch.GetTimestamp();
                            if (now - _lastMove >= MoveThrottleTicks)
                            {
                                _lastMove = now;
                                DragMoved?.Invoke(info.X, info.Y);
                            }

                            break;
                    }
                }
                catch (Exception e)
                {
                    // Never let an exception escape into the hook chain.
                    Log.Error("Mouse hook handler failed", e);
                }
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MsLlHookStruct
        {
            public int X;
            public int Y;
            public uint MouseData;
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
        private static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
