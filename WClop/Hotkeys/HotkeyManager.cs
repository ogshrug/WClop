using System.Runtime.InteropServices;
using System.Windows.Interop;
using WClop.Core.Hotkeys;
using WClop.Core.Logging;
using WClop.Core.Settings;

namespace WClop.Hotkeys
{
    /// <summary>
    /// Registers WClop's global hotkeys (project.md §17, §26.1 <c>RegisterHotKey</c>) on a message-only window
    /// on the UI thread, from <see cref="HotkeyCatalog"/> and the user's remapping. Keys another app already owns
    /// fail to register; they're reported rather than fatal.
    /// </summary>
    internal sealed class HotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        private readonly HotkeySettings _settings;
        private readonly Action<HotkeyBinding> _onHotkey;
        private readonly HwndSource _window;
        private readonly Dictionary<int, HotkeyBinding> _registered = [];
        private readonly List<HotkeyBinding> _taken = [];
        private IReadOnlyList<HotkeyProblem> _problems = [];

        public HotkeyManager(HotkeySettings settings, Action<HotkeyBinding> onHotkey)
        {
            _settings = settings;
            _onHotkey = onHotkey;
            _window = new HwndSource(new HwndSourceParameters("WClop hotkeys")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            });
            _window.AddHook(WndProc);
            Register();
        }

        /// <summary>Raised after (re-)registering, so menus and the settings window can refresh.</summary>
        public event Action? Changed;

        public IReadOnlyCollection<HotkeyBinding> Registered => _registered.Values;

        /// <summary>Keys that couldn't be registered because another app already uses them.</summary>
        public IReadOnlyList<HotkeyBinding> Taken => _taken;

        public string ModifierText => string.Join("+", ModifierNames(_settings.Modifiers));

        /// <summary>A short status for an action, for menus and the settings window.</summary>
        public string StatusOf(HotkeyAction action)
        {
            if (!_settings.Enabled)
                return "Hotkeys are off";
            if (!HotkeyCatalog.IsEnabled(action, _settings))
                return "Off";
            if (_problems.FirstOrDefault(p => p.Action == action) is { } problem)
                return char.ToUpperInvariant(problem.Reason[0]) + problem.Reason[1..];
            var taken = _taken.Where(b => b.Action == action).ToList();
            if (taken.Count > 0)
                return "Taken by another app" + (action == HotkeyAction.ScaleTo ? $" ({string.Join(", ", taken.Select(b => b.Key))})" : "");
            return "Active";
        }

        public bool IsWorking(HotkeyAction action) => StatusOf(action) == "Active";

        /// <summary>(Re-)register from the current settings.</summary>
        public void Register()
        {
            Unregister();
            if (_settings.Enabled)
            {
                var (bindings, problems) = HotkeyCatalog.Build(_settings);
                _problems = problems;

                var modifiers = ParseModifiers(_settings.Modifiers) | MOD_NOREPEAT;
                var id = 1;
                foreach (var binding in bindings)
                {
                    if (RegisterHotKey(_window.Handle, id, modifiers, binding.VirtualKey))
                        _registered[id] = binding;
                    else
                        _taken.Add(binding);
                    id++;
                }

                Log.Info($"Hotkeys: {ModifierText} + {string.Join(" ", _registered.Values.Select(b => b.Key))}" +
                         (_taken.Count > 0 ? $"; taken by other apps: {string.Join(" ", _taken.Select(b => b.Key))}" : "") +
                         (problems.Count > 0 ? $"; not bound: {string.Join("; ", problems.Select(p => $"{p.Action} ({p.Reason})"))}" : ""));
            }

            Changed?.Invoke();
        }

        public void Dispose()
        {
            Unregister();
            _window.RemoveHook(WndProc);
            _window.Dispose();
        }

        private void Unregister()
        {
            foreach (var id in _registered.Keys)
                UnregisterHotKey(_window.Handle, id);
            _registered.Clear();
            _taken.Clear();
            _problems = [];
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var binding))
            {
                handled = true;
                _onHotkey(binding);
            }

            return IntPtr.Zero;
        }

        /// <summary>Normalised modifier names; falls back to Ctrl+Alt+Shift if fewer than two are given,
        /// since a single modifier would swallow ordinary shortcuts.</summary>
        public static IReadOnlyList<string> ModifierNames(string text)
        {
            var names = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => "Ctrl",
                    "alt" => "Alt",
                    "shift" => "Shift",
                    "win" or "windows" => "Win",
                    _ => null,
                })
                .OfType<string>()
                .Distinct()
                .OrderBy(n => Array.IndexOf(["Ctrl", "Alt", "Shift", "Win"], n))
                .ToList();
            return names.Count >= 2 ? names : ["Ctrl", "Alt", "Shift"];
        }

        private static uint ParseModifiers(string text) =>
            ModifierNames(text).Aggregate(0u, (mods, name) => mods | name switch
            {
                "Ctrl" => MOD_CONTROL,
                "Alt" => MOD_ALT,
                "Shift" => MOD_SHIFT,
                _ => MOD_WIN,
            });

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
