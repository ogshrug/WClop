using WClop.Core;
using WClop.Core.Audio;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

/// <summary>Cover art in audio files: kept, optimised (a smaller JPEG at the same resolution) or removed.</summary>
public sealed class CoverArtTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public CoverArtTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>A song with a large, high-quality cover (a PNG, or a JPEG at quality 100).</summary>
    private string Song(string name, string audio, bool pngCover = false)
    {
        var photo = TestImages.Photo(800, 800);
        var art = pngCover ? TestImages.SavePng(photo, _dir.File("cover.png")) : TestImages.SaveJpeg(photo, _dir.File("cover.jpg"), quality: 100);
        var path = _dir.File(name);
        List<string> args =
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=4", "-i", art,
            "-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v:0", "attached_pic", .. audio.Split(' '), path,
        ];
        var made = ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg), args).GetAwaiter().GetResult();
        Assert.True(made.Succeeded, made.StdErr);
        return path;
    }

    /// <summary>The cover stream's codec, size in pixels and bytes; null if there's none.</summary>
    private async Task<(string Codec, int Width, int Height, long Bytes)?> CoverAsync(string path)
    {
        var probe = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffprobe),
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height", "-of", "csv=p=0", path]);
        var fields = probe.StdOut.Trim().Split(',');
        if (fields.Length < 3)
            return null;

        var picture = _dir.File(Guid.NewGuid().ToString("N") + ".bin");
        await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg), ["-hide_banner", "-v", "error", "-y", "-i", path, "-map", "0:v:0", "-c", "copy", "-f", "image2", picture]);
        return (fields[0], int.Parse(fields[1]), int.Parse(fields[2]), new FileInfo(picture).Length);
    }

    [Fact]
    public void KeepIsTheDefault() => Assert.Equal(CoverArtMode.Keep, new AppSettings().Compression.AudioCoverArt);

    [Theory]
    [InlineData(CoverArtMode.Keep, "copy")]
    [InlineData(CoverArtMode.Optimise, "mjpeg")]
    public void CoverArtArguments(CoverArtMode mode, string codec)
    {
        var info = new AudioInfo(TimeSpan.FromSeconds(4), "mp3", 320, 44100, HasCoverArt: true);

        var args = AudioOptimiser.Arguments("in.mp3", "out.mp3", FileFormat.Mp3, 128, info, new AudioEncodeOptions(CoverArt: mode)).ToList();

        Assert.Equal(codec, args[args.IndexOf("-c:v") + 1]);
        Assert.Contains("attached_pic", args);
        Assert.DoesNotContain("0:v:0", AudioOptimiser.Arguments("in.mp3", "out.mp3", FileFormat.Mp3, 128, info,
            new AudioEncodeOptions(CoverArt: CoverArtMode.Remove)));
    }

    [ToolsFact]
    public async Task KeepLeavesTheArtAlone()
    {
        var input = Song("song.mp3", "-c:a libmp3lame -b:a 320k");
        var before = await CoverAsync(input);

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        Assert.Equal(before, await CoverAsync(result.OutputPath));
    }

    [ToolsTheory]
    [InlineData("song.mp3", "-c:a libmp3lame -b:a 320k", false)]
    [InlineData("song.m4a", "-c:a aac -b:a 256k", true)]
    public async Task OptimiseMakesASmallerJpegOfTheSameSize(string name, string audio, bool pngCover)
    {
        _settings.Compression.AudioCoverArt = CoverArtMode.Optimise;
        var input = Song(name, audio, pngCover);
        var before = (await CoverAsync(input))!.Value;

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        var after = (await CoverAsync(result.OutputPath))!.Value;
        Assert.Equal("mjpeg", after.Codec);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
        Assert.True(after.Bytes < before.Bytes / 2, $"{after.Bytes} vs {before.Bytes}");
    }

    [ToolsFact]
    public async Task RemoveDropsTheArt()
    {
        _settings.Compression.AudioCoverArt = CoverArtMode.Remove;
        var input = Song("song.mp3", "-c:a libmp3lame -b:a 320k");

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        Assert.False((await AudioInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), result.OutputPath))!.HasCoverArt);
    }

    [ToolsFact]
    public async Task ShrinkingOnlyTheArtCopiesTheSound()
    {
        // At 96 kbps there's no bitrate to gain, but the cover is most of the file.
        _settings.Compression.AudioCoverArt = CoverArtMode.Optimise;
        var input = Song("podcast.mp3", "-c:a libmp3lame -b:a 96k", pngCover: true);
        var before = new FileInfo(input).Length;

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest());

        Assert.True(result.NewSize < before / 2, $"{result.NewSize} vs {before}");
        var info = (await AudioInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), result.OutputPath))!;
        Assert.InRange(info.BitrateKbps!.Value, 88, 104); // the sound wasn't re-encoded
        Assert.True(info.HasCoverArt);
    }
}
