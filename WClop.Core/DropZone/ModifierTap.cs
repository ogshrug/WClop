namespace WClop.Core.DropZone;

/// <summary>The modifier whose tap during a drag shows the drop zone under the cursor (Clop 3.0).</summary>
public enum TapModifier
{
    None,
    Alt,
    Ctrl,
    Shift,
}

/// <summary>
/// Recognises a modifier "tap" (project.md §18.2): the key pressed and released within <see cref="MaxTapDuration"/>,
/// with no other key in between. Holding the modifier (e.g. Alt to keep the original on drop), using it in a
/// shortcut, or auto-repeat while it's held never count. Fed from the keyboard hook thread, one call per key event,
/// with Windows virtual-key codes and the event's millisecond timestamp.
/// </summary>
public sealed class ModifierTapDetector
{
    public static readonly TimeSpan MaxTapDuration = TimeSpan.FromMilliseconds(300);

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    private long? _pressedAt;

    public TapModifier Modifier { get; set; }

    /// <summary>A key went down. Taps complete on release.</summary>
    public void KeyDown(int virtualKey, long timeMs)
    {
        if (Modifier != TapModifier.None && Is(virtualKey, Modifier))
        {
            // Auto-repeat while held keeps the first press's time.
            _pressedAt ??= timeMs;
            return;
        }

        // Any other key (including another modifier) makes this a combination, not a tap.
        _pressedAt = null;
    }

    /// <summary>A key came up. True if that completed a tap of <see cref="Modifier"/>.</summary>
    public bool KeyUp(int virtualKey, long timeMs)
    {
        if (Modifier == TapModifier.None || !Is(virtualKey, Modifier) || _pressedAt is not { } pressedAt)
            return false;

        _pressedAt = null;
        // Unsigned difference copes with the tick count wrapping around.
        var held = unchecked((uint)(timeMs - pressedAt));
        return held <= MaxTapDuration.TotalMilliseconds;
    }

    /// <summary>Forget a half-finished tap (a mouse click or the end of the drag in between).</summary>
    public void Reset() => _pressedAt = null;

    public static bool Is(int virtualKey, TapModifier modifier) => modifier switch
    {
        TapModifier.Alt => virtualKey is VkMenu or VkLMenu or VkRMenu,
        TapModifier.Ctrl => virtualKey is VkControl or VkLControl or VkRControl,
        TapModifier.Shift => virtualKey is VkShift or VkLShift or VkRShift,
        _ => false,
    };
}
