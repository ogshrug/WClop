using System.Text.Json;
using WClop.Core.Ipc;
using WClop.Core.Media;
using WClop.Core.Settings;

namespace WClop.Tests;

public class IpcTests
{
    private static string Instance() => "test" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task RoundTripsACommandAndReply()
    {
        var instance = Instance();
        OptimiseCommand? received = null;
        using var server = new IpcServer(IpcChannel.Optimise, (json, _) =>
        {
            received = JsonSerializer.Deserialize<OptimiseCommand>(json, IpcNames.Json);
            return Task.FromResult(JsonSerializer.Serialize(
                new OptimiseReply([new FileOutcome(@"C:\a.png", @"C:\a.min.png", 100, 40, "Done", true)], "ok"), IpcNames.Json));
        }, instance);
        server.Start();

        var reply = await IpcClient.SendAsync<OptimiseCommand, OptimiseReply>(
            IpcChannel.Optimise,
            new OptimiseCommand { Paths = [@"C:\a.png"], Source = IpcSource.SendTo, TargetBytes = 10, Output = OutputBehaviour.InPlace },
            TimeSpan.FromSeconds(5), instance: instance);

        Assert.NotNull(reply);
        Assert.Equal("ok", reply.Message);
        Assert.Equal(40, Assert.Single(reply.Files).NewSize);
        Assert.Equal(IpcSource.SendTo, received!.Source);
        Assert.Equal(10, received.TargetBytes);
        Assert.Equal(OutputBehaviour.InPlace, received.Output);
    }

    [Fact]
    public async Task ServesSeveralClientsAtOnce()
    {
        var instance = Instance();
        using var server = new IpcServer(IpcChannel.Settings, async (json, _) =>
        {
            await Task.Delay(200);
            var command = JsonSerializer.Deserialize<SettingsCommand>(json, IpcNames.Json)!;
            return JsonSerializer.Serialize(new SettingsReply(true, command.Key), IpcNames.Json);
        }, instance);
        server.Start();

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            IpcClient.SendAsync<SettingsCommand, SettingsReply>(
                IpcChannel.Settings, new SettingsCommand("get", $"k{i}"), TimeSpan.FromSeconds(5), instance: instance)));

        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"k{i}"), replies.Select(r => r!.Value));
    }

    [Fact]
    public async Task ReturnsNullWhenNothingIsListening()
    {
        var reply = await IpcClient.SendAsync<SettingsCommand, SettingsReply>(
            IpcChannel.Settings, new SettingsCommand("get"), TimeSpan.FromMilliseconds(100), instance: Instance());
        Assert.Null(reply);
    }

    [Fact]
    public void ChannelsHaveSeparatePerUserNames()
    {
        var names = Enum.GetValues<IpcChannel>().Select(c => IpcNames.PipeName(c)).ToList();
        Assert.Equal(3, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches(@"^WClop\.[0-9a-f]{12}\.\w+$", n));
    }
}

public class SettingsPathTests
{
    [Fact]
    public void GetsAndSetsNestedValuesCaseInsensitively()
    {
        var settings = new AppSettings();
        SettingsPath.Set(settings, "COMPRESSION.imagefactor", "55");
        SettingsPath.Set(settings, "clipboard.enabled", "off");
        SettingsPath.Set(settings, "files.cleanupInterval", "never");
        SettingsPath.Set(settings, "clipboard.ignoredApps", "KeePassXC.exe, 1Password.exe");

        Assert.Equal(55, settings.Compression.ImageFactor);
        Assert.False(settings.Clipboard.Enabled);
        Assert.Equal(CleanupInterval.Never, settings.Files.CleanupInterval);
        Assert.Equal(["KeePassXC.exe", "1Password.exe"], settings.Clipboard.IgnoredApps);
        Assert.Equal("55", SettingsPath.Get(settings, "compression.imageFactor"));
        Assert.Equal("false", SettingsPath.Get(settings, "clipboard.enabled"));
    }

    [Fact]
    public void TextValuesCanBeEmptied()
    {
        var settings = new AppSettings { Files = { WorkDir = @"D:\x" } };
        SettingsPath.Set(settings, "files.workDir", "");
        Assert.Equal("", settings.Files.WorkDir);
        SettingsPath.Set(settings, "files.workDir", @"D:\y");
        Assert.Equal(@"D:\y", settings.Files.WorkDir);
    }

    [Theory]
    [InlineData("compression.nope", "x", "Unknown setting 'compression.nope'")]
    [InlineData("compression.imageFactor", "lots", "isn't a valid value")]
    [InlineData("clipboard.enabled", "maybe", "true or false")]
    [InlineData("compression", "1", "group of settings")]
    [InlineData("", "1", "No setting name")]
    public void RejectsBadInput(string path, string value, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => SettingsPath.Set(new AppSettings(), path, value));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void ListsEveryLeafAndEachOneResolves()
    {
        var all = SettingsPath.All().ToList();
        Assert.Contains("compression.imageFactor", all);
        Assert.Contains("hotkeys.modifiers", all);
        Assert.DoesNotContain("compression", all);
        var settings = new AppSettings();
        Assert.All(all, path => SettingsPath.Get(settings, path));
    }
}

public class OptimiseCommandTests
{
    [Theory]
    [InlineData("10MB", 10L * 1024 * 1024)]
    [InlineData("10 mb", 10L * 1024 * 1024)]
    [InlineData("1.5G", 1536L * 1024 * 1024)]
    [InlineData("500k", 500L * 1024)]
    [InlineData("2048", 2048L)]
    public void ParsesSizes(string text, long bytes)
    {
        Assert.True(SizeText.TryParse(text, out var parsed));
        Assert.Equal(bytes, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MB")]
    [InlineData("10 parsecs")]
    [InlineData("0")]
    [InlineData("-5MB")]
    public void RejectsBadSizes(string text) => Assert.False(SizeText.TryParse(text, out _));

    [Fact]
    public void PresetThenExplicitValues()
    {
        var builder = new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"], Preset = "aggressive", Scale = 0.5 });
        var request = builder.Build();
        Assert.Null(builder.Error);
        Assert.Equal("Aggressive", builder.Preset.Name);
        Assert.Equal(builder.Preset.Factor, request.Factor);
        Assert.Equal(100, request.PdfDpi);
        Assert.Equal(0.5, request.Scale);
        Assert.True(request.Force);
        Assert.True(builder.HasOptions);
    }

    [Fact]
    public void ExplicitFactorBeatsThePreset()
    {
        var request = new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"], Preset = "Maximum", Factor = 20 }).Build();
        Assert.Equal(20, request.Factor);
    }

    [Fact]
    public void KeepSavesNextToTheOriginalUnlessOutputIsGiven()
    {
        Assert.Equal(OutputBehaviour.SameFolder,
            new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"], KeepOriginals = true }).Build().Behaviour);
        Assert.Equal(OutputBehaviour.Temporary,
            new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"], KeepOriginals = true, Output = OutputBehaviour.Temporary })
                .Build().Behaviour);
        Assert.Null(new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"] }).Build().Behaviour);
    }

    [Fact]
    public void ResolvesConversionTargets()
    {
        Assert.Equal(FileFormat.WebP, new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"], ConvertTo = ".webp" }).ConvertTo);
        Assert.Equal(FileFormat.Jpeg, new OptimisationRequestBuilder(new OptimiseCommand { Paths = ["a"], ConvertTo = "jpeg" }).ConvertTo);
    }

    [Theory]
    [InlineData("Ultra", null, null, null, "Unknown preset")]
    [InlineData(null, "xyz", null, null, "Unknown format")]
    [InlineData(null, null, 0, null, "factor")]
    [InlineData(null, null, null, 1.5, "scale")]
    public void ReportsBadOptions(string? preset, string? convertTo, int? factor, double? scale, string message)
    {
        var builder = new OptimisationRequestBuilder(new OptimiseCommand
        {
            Paths = ["a"], Preset = preset, ConvertTo = convertTo, Factor = factor, Scale = scale,
        });
        Assert.Contains(message, builder.Error);
    }

    [Fact]
    public void KnownExtensionsCoverTheCommonFormats()
    {
        var extensions = FileFormats.KnownExtensions.Select(e => e.Extension.ToLowerInvariant()).ToHashSet();
        Assert.Superset(new HashSet<string> { "png", "jpg", "jpeg", "mp4", "pdf", "mp3", "heic" }, extensions);
    }
}

public class RecentCopiesTests
{
    [Fact]
    public async Task MatchesCopiesWithinTheWindowOnly()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var copies = new WClop.Core.Watching.RecentCopies(TimeSpan.FromSeconds(30), () => now);
        copies.Add("abc");
        Assert.True(copies.Contains("abc"));
        Assert.False(copies.Contains("def"));
        now = now.AddSeconds(31);
        Assert.False(await copies.WaitForAsync("abc", TimeSpan.Zero));
    }

    [Fact]
    public async Task WaitsForACopyThatArrivesShortlyAfter()
    {
        var copies = new WClop.Core.Watching.RecentCopies(TimeSpan.FromSeconds(30));
        _ = Task.Delay(150).ContinueWith(_ => copies.Add("late"));
        Assert.True(await copies.WaitForAsync("late", TimeSpan.FromSeconds(2)));
    }
}
