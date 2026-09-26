using WClop.Core;
using WClop.Core.Hotkeys;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

public class HotkeyCatalogTests
{
    private static HotkeyBinding? Bound(HotkeySettings settings, HotkeyAction action) =>
        HotkeyCatalog.Build(settings).Bindings.FirstOrDefault(b => b.Action == action);

    [Fact]
    public void DefaultsBindEveryActionWithoutConflicts()
    {
        var (bindings, problems) = HotkeyCatalog.Build(new HotkeySettings());

        Assert.Empty(problems);
        Assert.Equal("Z", Bound(new HotkeySettings(), HotkeyAction.OptimiseClipboard)!.Key);
        Assert.Equal("U", Bound(new HotkeySettings(), HotkeyAction.Restore)!.Key);
        Assert.Equal(9, bindings.Count(b => b.Action == HotkeyAction.ScaleTo));
        Assert.Equal(bindings.Count, bindings.Select(b => b.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void KeysCanBeRemapped()
    {
        var settings = new HotkeySettings { Keys = { ["Restore"] = "Backspace" } };

        var restore = Bound(settings, HotkeyAction.Restore)!;

        Assert.Equal("Backspace", restore.Key);
        Assert.Equal(0x08u, restore.VirtualKey);
    }

    [Fact]
    public void DuplicateKeyIsReportedAndOnlyTheFirstKeepsIt()
    {
        var settings = new HotkeySettings { Keys = { ["Aggressive"] = "Z" } };

        var (bindings, problems) = HotkeyCatalog.Build(settings);

        Assert.Equal(HotkeyAction.OptimiseClipboard, bindings.Single(b => b.Key == "Z").Action);
        var problem = Assert.Single(problems);
        Assert.Equal(HotkeyAction.Aggressive, problem.Action);
        Assert.Contains("Optimise the clipboard now", problem.Reason);
    }

    [Fact]
    public void DigitsBelongToDownscaleWhileItsOn()
    {
        var settings = new HotkeySettings { Keys = { ["Preview"] = "5" } };
        Assert.Single(HotkeyCatalog.Build(settings).Problems);

        settings.DisabledActions.Add("ScaleTo");
        Assert.Empty(HotkeyCatalog.Build(settings).Problems);
        Assert.Equal("5", Bound(settings, HotkeyAction.Preview)!.Key);
    }

    [Fact]
    public void DisabledActionsAreNotBound()
    {
        var settings = new HotkeySettings { DisabledActions = ["Preview", "ClearAll"] };

        var bindings = HotkeyCatalog.Build(settings).Bindings;

        Assert.DoesNotContain(bindings, b => b.Action is HotkeyAction.Preview or HotkeyAction.ClearAll);
    }

    [Fact]
    public void UnknownKeyIsAProblemNotACrash()
    {
        var settings = new HotkeySettings { Keys = { ["Restore"] = "NotAKey" } };

        var problem = Assert.Single(HotkeyCatalog.Build(settings).Problems);
        Assert.Equal(HotkeyAction.Restore, problem.Action);
    }

    [Fact]
    public void RemappingSurvivesSaveAndLoad()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var settings = new AppSettings();
        settings.Hotkeys.Keys["Restore"] = "Backspace";
        settings.Hotkeys.DisabledActions.Add("Preview");

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal("Backspace", loaded.Hotkeys.Keys["Restore"]);
        Assert.Equal(["Preview"], loaded.Hotkeys.DisabledActions);
    }
}

public sealed class WorkDirCleanupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;

    public WorkDirCleanupTests()
    {
        _paths = new AppPaths(_dir.File("cache"));
        _paths.EnsureCreated();
    }

    public void Dispose() => _dir.Dispose();

    private string Aged(string folder, string name, TimeSpan age)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        var time = DateTime.UtcNow - age;
        File.SetCreationTimeUtc(path, time);
        File.SetLastWriteTimeUtc(path, time);
        return path;
    }

    [Fact]
    public void DeletesOldFilesButNotBatchBackups()
    {
        var old = Aged(_paths.Images, "old.png", TimeSpan.FromDays(4));
        var fresh = Aged(_paths.Images, "fresh.png", TimeSpan.FromHours(1));
        var batch = Aged(_paths.BatchBackups, "keep.png", TimeSpan.FromDays(30));

        var deleted = new WorkDirCleanup(_paths, new FileSettings()).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(batch));
    }

    [Fact]
    public void AgeCountsFromArrivalNotTheOriginalsDate()
    {
        // A fresh backup of an old screenshot: copied today, but modified years ago.
        var backup = Path.Combine(_paths.Backups, "old-screenshot.png");
        File.WriteAllText(backup, "x");
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddYears(-3));

        new WorkDirCleanup(_paths, new FileSettings()).RunOnce(DateTime.UtcNow);

        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void NeverMeansNever()
    {
        var old = Aged(_paths.Images, "old.png", TimeSpan.FromDays(400));

        new WorkDirCleanup(_paths, new FileSettings { CleanupInterval = CleanupInterval.Never }).RunOnce(DateTime.UtcNow);

        Assert.True(File.Exists(old));
    }
}
