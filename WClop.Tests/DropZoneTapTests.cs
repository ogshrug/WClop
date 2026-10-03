using WClop.Core.DropZone;
using WClop.Core.Settings;

namespace WClop.Tests;

public class DropZoneTapTests
{
    private const int Alt = 0x12, LAlt = 0xA4, RAlt = 0xA5, LCtrl = 0xA2, RCtrl = 0xA3, LShift = 0xA0, KeyA = 0x41, Escape = 0x1B;

    private static ModifierTapDetector Detector(TapModifier modifier = TapModifier.Alt) => new() { Modifier = modifier };

    [Fact]
    public void QuickPressAndReleaseIsATap()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 1000);

        Assert.True(detector.KeyUp(LAlt, 1120));
    }

    [Fact]
    public void HoldingTheModifierIsNotATap()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 1000);
        // Auto-repeat keeps arriving while it's held; the first press decides.
        for (var t = 1030; t < 1600; t += 30)
            detector.KeyDown(LAlt, t);

        Assert.False(detector.KeyUp(LAlt, 1600));
    }

    [Fact]
    public void ExactlyTheLimitStillCounts()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 0);
        Assert.True(detector.KeyUp(LAlt, 300));

        detector.KeyDown(LAlt, 1000);
        Assert.False(detector.KeyUp(LAlt, 1301));
    }

    [Fact]
    public void AnotherKeyInBetweenMakesItAShortcut()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 1000);
        detector.KeyDown(KeyA, 1050); // Alt+A
        detector.KeyUp(KeyA, 1080);

        Assert.False(detector.KeyUp(LAlt, 1100));
    }

    [Fact]
    public void AnotherModifierInBetweenMakesItAShortcut()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 1000);
        detector.KeyDown(LShift, 1040); // Alt+Shift switches keyboard layout
        detector.KeyUp(LShift, 1070);

        Assert.False(detector.KeyUp(LAlt, 1090));
    }

    [Fact]
    public void AltGrIsNotACtrlTap()
    {
        // AltGr arrives as left Ctrl then right Alt.
        var detector = Detector(TapModifier.Ctrl);
        detector.KeyDown(LCtrl, 1000);
        detector.KeyDown(RAlt, 1000);
        detector.KeyUp(RAlt, 1050);

        Assert.False(detector.KeyUp(LCtrl, 1050));
    }

    [Fact]
    public void EitherSideAndTheGenericCodeCount()
    {
        var detector = Detector();
        foreach (var key in new[] { Alt, LAlt, RAlt })
        {
            detector.KeyDown(key, 0);
            Assert.True(detector.KeyUp(key, 50));
        }

        var ctrl = Detector(TapModifier.Ctrl);
        ctrl.KeyDown(RCtrl, 0);
        Assert.True(ctrl.KeyUp(RCtrl, 50));
    }

    [Fact]
    public void OtherModifiersDontTap()
    {
        var detector = Detector(TapModifier.Shift);
        detector.KeyDown(LAlt, 0);

        Assert.False(detector.KeyUp(LAlt, 50));
    }

    [Fact]
    public void NoneNeverTaps()
    {
        var detector = Detector(TapModifier.None);
        detector.KeyDown(LAlt, 0);

        Assert.False(detector.KeyUp(LAlt, 50));
    }

    [Fact]
    public void AReleaseWithoutAPressIsNotATap()
    {
        // The modifier was already held when the drag (and the hook) started.
        Assert.False(Detector().KeyUp(LAlt, 50));
    }

    [Fact]
    public void ResetForgetsAHalfFinishedTap()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 0);
        detector.Reset(); // e.g. the drop happened while Alt was held

        Assert.False(detector.KeyUp(LAlt, 50));
    }

    [Fact]
    public void TwoTapsInARowBothCount()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 0);
        Assert.True(detector.KeyUp(LAlt, 80));
        detector.KeyDown(LAlt, 400);
        Assert.True(detector.KeyUp(LAlt, 470));
    }

    [Fact]
    public void CopesWithTheTickCountWrapping()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, uint.MaxValue - 40);

        Assert.True(detector.KeyUp(LAlt, 60));
    }

    [Fact]
    public void EscapeCancelsTheTapToo()
    {
        var detector = Detector();
        detector.KeyDown(LAlt, 0);
        detector.KeyDown(Escape, 20);

        Assert.False(detector.KeyUp(LAlt, 60));
    }

    [Fact]
    public void AltIsTheDefault() => Assert.Equal(TapModifier.Alt, new UiSettings().DropZoneTapKey);

    [Fact]
    public void CursorZoneIsCentredOnTheCursor()
    {
        var area = new ScreenRect(0, 0, 1920, 1040);

        Assert.Equal(new ScreenRect(900, 415, 1120, 585), DropZoneGeometry.AtCursor(1010, 500, 220, 170, area));
    }

    [Fact]
    public void CursorZoneStaysOnScreen()
    {
        var area = new ScreenRect(1920, 0, 3840, 1040);

        Assert.Equal(new ScreenRect(3620, 0, 3840, 170), DropZoneGeometry.AtCursor(3835, 5, 220, 170, area));
        Assert.Equal(new ScreenRect(1920, 870, 2140, 1040), DropZoneGeometry.AtCursor(1921, 1039, 220, 170, area));
    }
}
