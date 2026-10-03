using WClop.Core;
using WClop.Core.Audio;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

/// <summary>What the result cards build on: the format bar, rename, Edit with, the compact list, audio conversion.</summary>
public sealed class ResultCardTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();
    private readonly FileOptimisationService _service;

    public ResultCardTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    private static string Labels(MediaKind kind, FileFormat current, string? codec) =>
        string.Join(", ", FormatChoices.For(kind, current, codec).Select(c => c.Label));

    [Fact]
    public void ImagesNeverOfferTheirOwnFormat()
    {
        Assert.Equal("png, webp, avif, gif", Labels(MediaKind.Image, FileFormat.Jpeg, null));
        Assert.Equal("jpg, png, webp, avif, gif", Labels(MediaKind.Image, FileFormat.Heic, null));
    }

    [Fact]
    public void VideoChoicesGoByCodec()
    {
        Assert.Equal("gif, webm · VP9, mp4 · HEVC", Labels(MediaKind.Video, FileFormat.Mp4, "h264"));
        Assert.Equal("gif, webm · VP9", Labels(MediaKind.Video, FileFormat.Mp4, "hevc"));
        Assert.Equal("gif, mp4 · HEVC", Labels(MediaKind.Video, FileFormat.WebM, "vp9"));
        // VP8 WebM is still offered VP9.
        Assert.Equal("gif, webm · VP9, mp4 · HEVC", Labels(MediaKind.Video, FileFormat.WebM, "vp8"));
        // A video turned into a GIF converts back from the original.
        Assert.Equal("webm · VP9, mp4 · HEVC", Labels(MediaKind.Video, FileFormat.Gif, null));
    }

    [Fact]
    public void AudioChoicesGoByCodec()
    {
        Assert.Equal("m4a · AAC, ogg · Opus", Labels(MediaKind.Audio, FileFormat.Mp3, "mp3"));
        Assert.Equal("mp3, ogg · Opus", Labels(MediaKind.Audio, FileFormat.M4a, "aac"));
        Assert.Equal("mp3, m4a · AAC", Labels(MediaKind.Audio, FileFormat.Ogg, "opus"));
        Assert.Equal("mp3, m4a · AAC, ogg · Opus", Labels(MediaKind.Audio, FileFormat.Ogg, "vorbis"));
        // ALAC in an .m4a is offered AAC.
        Assert.Equal("mp3, m4a · AAC, ogg · Opus", Labels(MediaKind.Audio, FileFormat.M4a, "alac"));
        Assert.Equal("mp3, ogg · Opus", Labels(MediaKind.Audio, FileFormat.M4a, null));
        Assert.Empty(FormatChoices.For(MediaKind.Pdf, FileFormat.Pdf, null));
    }

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("hevc", "HEVC")]
    [InlineData("pcm_s16le", "PCM")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void CodecLabels(string? codec, string? label) => Assert.Equal(label, FormatChoices.CodecLabel(codec));

    [Theory]
    [InlineData(@"C:\x\shot.png", "holiday", "holiday.png")]
    [InlineData(@"C:\x\shot.png", "  holiday.PNG  ", "holiday.PNG")]
    [InlineData(@"C:\x\shot.jpeg", "holiday.jpg", "holiday.jpg")]
    [InlineData(@"C:\x\shot.png", "v1.2", "v1.2.png")]
    [InlineData(@"C:\x\shot.png", "holiday.jpg", "holiday.jpg.png")]
    [InlineData(@"C:\x\shot.png", "trailing. ", "trailing.png")]
    public void RenamedNamesKeepTheFormat(string current, string typed, string expected) =>
        Assert.Equal(expected, FileOptimisationService.RenamedFileName(current, typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData("con")]
    public void BadNamesAreRefused(string typed) =>
        Assert.Throws<ArgumentException>(() => FileOptimisationService.RenamedFileName(@"C:\x\shot.png", typed));

    [Fact]
    public void RenameKeepsTheMarkerAndNeverReplaces()
    {
        var file = TestImages.SavePng(TestImages.Flat(), _dir.File("shot.png"));
        TestImages.SavePng(TestImages.Flat(), _dir.File("taken.png"));
        _service.Markers.Set(file, MarkerStatus.Optimised);

        var renamed = _service.RenameFile(file, "holiday");
        Assert.Equal(_dir.File("holiday.png"), renamed);
        Assert.False(File.Exists(file));
        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(renamed));

        Assert.Throws<IOException>(() => _service.RenameFile(renamed, "taken"));
        // Only the case changes: still allowed.
        Assert.Equal(_dir.File("Holiday.png"), _service.RenameFile(renamed, "Holiday"));
    }

    [Fact]
    public async Task JobsFollowARename()
    {
        var file = TestImages.SavePng(TestImages.Flat(), _dir.File("a.png"));
        var manager = new OptimisationManager();
        var result = new FileOptimisationResult(file, file, file, FileFormat.Png, FileFormat.Png, 10, 5, false);
        var job = manager.Start(file, JobSource.File, "a.png", file, (_, _) => Task.FromResult(result));
        Assert.True(await TestSetup.WaitUntil(() => job.IsFinished));

        var moved = _dir.File("b.png");
        job.MarkMoved(file, moved);
        Assert.Equal(moved, job.CurrentPath);
        Assert.Equal(moved, job.Result!.OutputPath);
        Assert.Equal(moved, job.SourcePath);
        Assert.Equal("b.png", job.DisplayName);
    }

    [Fact]
    public void CompactListAndEditorSettings()
    {
        var cards = new ResultCardSettings();
        Assert.False(cards.IsCompact(5));
        Assert.True(cards.IsCompact(6));
        cards.CompactListThreshold = 0; // nonsense still means "more than one"
        Assert.True(cards.IsCompact(2));

        Assert.Null(cards.EditorFor(MediaKind.Image));
        cards.EditVideosWith = "\"%WINDIR%\\notepad.exe\"";
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%WINDIR%\notepad.exe"), cards.EditorFor(MediaKind.Video));
        Assert.Null(cards.EditorFor(MediaKind.Image));

        Assert.Contains("resultCards.compactListThreshold", SettingsPath.All());
    }

    [ToolsFact]
    public async Task ConvertsAudio()
    {
        var input = _dir.File("tone.wav");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=2", input]);
        Assert.True(made.Succeeded, made.StdErr);

        var start = await _service.DescribeAsync(input);
        var converted = await _service.ConvertAsync(start, FileFormat.M4a);
        Assert.Equal(FileFormat.M4a, converted.OutputFormat);
        Assert.Equal(_dir.File("tone.m4a"), converted.OutputPath);
        var info = await AudioInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), converted.OutputPath);
        Assert.Equal("aac", info!.Codec);
        Assert.True(File.Exists(input), "the original stays");
    }
}
