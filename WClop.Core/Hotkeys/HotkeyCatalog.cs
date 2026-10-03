using WClop.Core.Settings;

namespace WClop.Core.Hotkeys;

public enum HotkeyAction
{
    OptimiseClipboard,
    Aggressive,
    StepDown,
    ScaleTo,
    Restore,
    CyclePause,
    DismissNewest,
    BringBack,
    ClearAll,
    Preview,
    SpeedUp,
}

/// <summary>An action and its default key. <see cref="HotkeyAction.ScaleTo"/> always uses the digits 1–9.</summary>
public sealed record HotkeyCommand(HotkeyAction Action, string DefaultKey, string Description);

/// <param name="Digit">For <see cref="HotkeyAction.ScaleTo"/>: 1–9 means 10%–90%.</param>
public sealed record HotkeyBinding(HotkeyAction Action, string Key, uint VirtualKey, string Description, int Digit = 0);

/// <summary>A command that can't be bound as configured (unknown key, or a key already used by another command).</summary>
public sealed record HotkeyProblem(HotkeyAction Action, string Key, string Reason);

/// <summary>WClop's hotkeys (project.md §17), their default keys, and how settings remap them.</summary>
public static class HotkeyCatalog
{
    public const string DigitsKey = "1–9";

    public static readonly IReadOnlyList<HotkeyCommand> Commands =
    [
        new(HotkeyAction.OptimiseClipboard, "Z", "Optimise the clipboard now"),
        new(HotkeyAction.Aggressive, "A", "Optimise aggressively"),
        new(HotkeyAction.StepDown, "Minus", "Downscale one step (75%, 50%, 40% …)"),
        new(HotkeyAction.ScaleTo, DigitsKey, "Downscale to 10%–90%"),
        new(HotkeyAction.Restore, "U", "Restore the original"),
        new(HotkeyAction.CyclePause, "P", "Running → skip next copy → stopped"),
        new(HotkeyAction.DismissNewest, "Delete", "Dismiss the newest result"),
        new(HotkeyAction.BringBack, "Equals", "Bring back the last dismissed result"),
        new(HotkeyAction.ClearAll, "Escape", "Clear all results and stop jobs"),
        new(HotkeyAction.Preview, "Space", "Open the result in your viewer"),
        new(HotkeyAction.SpeedUp, "X", "Speed up a video or audio file (1.25×, 1.5× … 2×, 3× … 10×)"),
    ];

    private static readonly Dictionary<string, uint> VirtualKeys = BuildVirtualKeys();

    /// <summary>Key names that can be assigned in settings, in display order.</summary>
    public static IReadOnlyList<string> AssignableKeys { get; } = VirtualKeys.Keys.ToList();

    public static uint? VirtualKeyOf(string key) => VirtualKeys.TryGetValue(key, out var vk) ? vk : null;

    public static string KeyFor(HotkeyCommand command, HotkeySettings settings) =>
        command.Action != HotkeyAction.ScaleTo && settings.Keys.TryGetValue(command.Action.ToString(), out var key)
            ? key
            : command.DefaultKey;

    public static bool IsEnabled(HotkeyAction action, HotkeySettings settings) =>
        !settings.DisabledActions.Contains(action.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>How a key is shown to people: "−", "=", "Esc", …</summary>
    public static string DisplayKey(string key) => key switch
    {
        "Minus" => "−",
        "Equals" => "=",
        "Escape" => "Esc",
        "Delete" => "Del",
        "Comma" => ",",
        "Period" => ".",
        "Slash" => "/",
        "Backslash" => "\\",
        "Semicolon" => ";",
        "Quote" => "'",
        "LeftBracket" => "[",
        "RightBracket" => "]",
        _ => key,
    };

    /// <summary>
    /// The bindings to register for <paramref name="settings"/>, plus any that can't be registered as configured.
    /// The first command to claim a key keeps it; the digits belong to "downscale to" while it's enabled.
    /// </summary>
    public static (IReadOnlyList<HotkeyBinding> Bindings, IReadOnlyList<HotkeyProblem> Problems) Build(HotkeySettings settings)
    {
        var bindings = new List<HotkeyBinding>();
        var problems = new List<HotkeyProblem>();
        var claimed = new Dictionary<string, HotkeyAction>(StringComparer.OrdinalIgnoreCase);

        var scaleEnabled = IsEnabled(HotkeyAction.ScaleTo, settings);
        if (scaleEnabled)
        {
            for (var digit = 1; digit <= 9; digit++)
            {
                var key = digit.ToString();
                claimed[key] = HotkeyAction.ScaleTo;
                bindings.Add(new HotkeyBinding(HotkeyAction.ScaleTo, key, VirtualKeys[key], $"Downscale to {digit * 10}%", digit));
            }
        }

        foreach (var command in Commands.Where(c => c.Action != HotkeyAction.ScaleTo && IsEnabled(c.Action, settings)))
        {
            var key = KeyFor(command, settings);
            if (VirtualKeyOf(key) is not { } vk)
            {
                problems.Add(new HotkeyProblem(command.Action, key, $"\"{key}\" isn't a key WClop can use"));
                continue;
            }

            if (claimed.TryGetValue(key, out var owner))
            {
                var ownerName = Commands.First(c => c.Action == owner).Description;
                problems.Add(new HotkeyProblem(command.Action, key, $"already used for \"{ownerName}\""));
                continue;
            }

            claimed[key] = command.Action;
            bindings.Add(new HotkeyBinding(command.Action, key, vk, command.Description));
        }

        return (bindings, problems);
    }

    private static Dictionary<string, uint> BuildVirtualKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++)
            keys[c.ToString()] = c;
        for (var d = 0; d <= 9; d++)
            keys[d.ToString()] = (uint)('0' + d);
        foreach (var (name, vk) in new (string, uint)[]
                 {
                     ("Minus", 0xBD), ("Equals", 0xBB), ("Comma", 0xBC), ("Period", 0xBE), ("Slash", 0xBF),
                     ("Semicolon", 0xBA), ("Quote", 0xDE), ("LeftBracket", 0xDB), ("RightBracket", 0xDD),
                     ("Backslash", 0xDC), ("Space", 0x20), ("Delete", 0x2E), ("Backspace", 0x08),
                     ("Escape", 0x1B), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21),
                     ("PageDown", 0x22),
                 })
            keys[name] = vk;
        for (var f = 1; f <= 12; f++)
            keys[$"F{f}"] = (uint)(0x70 + f - 1);
        return keys;
    }
}
