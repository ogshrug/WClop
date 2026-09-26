using WClop.Core;
using WClop.Core.Settings;

namespace WClop.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wclop-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json")).Load();

        Assert.Equal(30, settings.Compression.ImageFactor);
        Assert.Equal(VideoTier.Fast, settings.Compression.VideoTier);
        Assert.Equal(4, settings.Watching.Images.MaxFilesPerBurst);
        Assert.Contains(settings.Watching.Images.Folders, f => f.EndsWith("Screenshots"));
        Assert.Equal(OutputBehaviour.SameFolder, settings.Files.Images.AutoConverted);
    }

    [Fact]
    public void RoundTrips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings();
        settings.Compression.ImageFactor = 64;
        settings.Clipboard.IgnoredApps.Add("KeePassXC.exe");
        settings.Files.Videos.Optimised = OutputBehaviour.SpecificFolder;

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(64, loaded.Compression.ImageFactor);
        Assert.Equal(["KeePassXC.exe"], loaded.Clipboard.IgnoredApps);
        Assert.Equal(OutputBehaviour.SpecificFolder, loaded.Files.Videos.Optimised);
    }

    [Fact]
    public void MissingPropertiesKeepDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "compression": { "imageFactor": 50 } }""");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(50, settings.Compression.ImageFactor);
        Assert.Equal(35, settings.Compression.AudioFactor);
        Assert.True(settings.Clipboard.Enabled);
    }

    [Fact]
    public void CorruptFileIsMovedAsideAndDefaultsReturned()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(30, settings.Compression.ImageFactor);
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void PortablePathRoundTrips()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var original = Path.Combine(profile, "Pictures", "Screenshots");

        var portable = PortablePath.Contract(original);

        Assert.StartsWith("%USERPROFILE%\\", portable);
        Assert.Equal(original, PortablePath.Expand(portable));
    }

    [Fact]
    public void PortablePathLeavesOtherPathsAlone()
    {
        Assert.Equal(@"D:\Media", PortablePath.Contract(@"D:\Media"));
        // A sibling folder that merely shares the profile path as a prefix isn't under the profile.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(profile + "2", PortablePath.Contract(profile + "2"));
    }

    [Fact]
    public void AppPathsCreatesWorkingFolders()
    {
        var paths = new AppPaths(Path.Combine(_dir, "Cache"));
        paths.EnsureCreated();

        Assert.All(paths.AllFolders, folder => Assert.True(Directory.Exists(folder)));
        Assert.DoesNotContain(paths.BatchBackups, paths.CleanableFolders);
    }

    [Fact]
    public void TempPathsAreAsciiAndUnique()
    {
        var a = AppPaths.NewTempPath(_dir, "png");
        var b = AppPaths.NewTempPath(_dir, ".png");

        Assert.NotEqual(a, b);
        Assert.EndsWith(".png", a);
        Assert.True(Path.GetFileName(a).All(char.IsAscii));
    }
}
