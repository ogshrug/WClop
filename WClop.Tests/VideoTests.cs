using System.Globalization;
using WClop.Core;
using WClop.Core.Compression;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

public class VideoInfoTests
{
    private const string ScreenRecording = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 2560, "height": 1440,
              "r_frame_rate": "60/1", "avg_frame_rate": "2300/100", "disposition": { "attached_pic": 0 } },
            { "codec_type": "audio", "codec_name": "aac" }
          ],
          "format": { "duration": "42.500000", "bit_rate": "9000000" }
        }
        """;

    [Fact]
    public void ParsesAScreenRecording()
    {
        var info = VideoInfo.Parse(ScreenRecording)!;

        Assert.Equal(TimeSpan.FromSeconds(42.5), info.Duration);
        Assert.Equal((2560, 1440), (info.Width, info.Height));
        Assert.Equal(23, info.AverageFrameRate);
        Assert.Equal(60, info.PeakFrameRate);
        Assert.True(info.IsVariableFrameRate);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(9_000_000, info.Bitrate);
    }

    [Fact]
    public void CoverArtIsNotTheVideo()
    {
        var info = VideoInfo.Parse("""
            { "streams": [ { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } },
                           { "codec_type": "audio", "codec_name": "mp3" } ],
              "format": { "duration": "180" } }
            """);

        Assert.Null(info);
    }

    [Theory]
    [InlineData("60/1", 60)]
    [InlineData("30000/1001", 29.97)]
    [InlineData("0/0", 0)]
    [InlineData("25", 25)]
    [InlineData(null, 0)]
    public void Rates(string? rate, double expected) => Assert.Equal(expected, VideoInfo.ParseRate(rate), 2);
}

public class VideoCompressionModelTests
{
    [Theory]
    [InlineData(5, 18)]
    [InlineData(50, 24)]
    [InlineData(100, 38)]
    public void X264Crf(int factor, int crf) => Assert.Equal(crf, CompressionModel.X264Crf(factor));

    [Theory]
    [InlineData(10, "veryfast")]
    [InlineData(50, "medium")]
    [InlineData(90, "slower")]
    public void X264Preset(int factor, string preset) => Assert.Equal(preset, CompressionModel.X264Preset(factor));

    [Fact]
    public void HardwareQualityAtNormal() => Assert.Equal(49, CompressionModel.HardwareQuality(50));

    [Theory]
    [InlineData(5, 60)]
    [InlineData(50, 39)]
    [InlineData(100, 15)]
    public void MediaFoundationQuality(int factor, int quality) =>
        Assert.Equal(quality, CompressionModel.MediaFoundationQuality(factor));
}

public class VideoArgumentsTests
{
    private static VideoInfo Info(double avg = 60, double real = 60, string? audio = "aac") =>
        new(TimeSpan.FromSeconds(10), 1920, 1080, avg, real, "h264", audio, null);

    private static VideoEncodeSpec Spec(VideoInfo? info = null, VideoEncoder encoder = VideoEncoder.MediaFoundation) => new()
    {
        Input = "in.mov", Output = "out.mp4", Info = info ?? Info(), Encoder = encoder, FpsCap = 60,
    };

    private static string First(VideoEncodeSpec spec) => string.Join(" ", VideoArguments.Build(spec)[0]);

    [Fact]
    public void CapsA120FpsRecording()
    {
        var args = First(Spec(Info(120, 120)));

        Assert.Contains("-vf fps=60", args);
        Assert.DoesNotContain("passthrough", args);
    }

    [Fact]
    public void VariableFrameRateIsPassedThroughNotResampled()
    {
        var args = First(Spec(Info(avg: 23, real: 60)));

        Assert.DoesNotContain("fps=", args);
        Assert.Contains("-fps_mode passthrough -enc_time_base demux", args);
    }

    [Fact]
    public void NoCapWhenTheSourceIsAtOrBelowIt() => Assert.DoesNotContain("fps=", First(Spec(Info(59.94, 59.94))));

    [Fact]
    public void SpeedUpChangesTimestampsAndReencodesAudio()
    {
        var args = First(Spec() with { Speed = 3 });

        Assert.Contains("setpts=PTS/3", args);
        Assert.Contains("-af atempo=2.0,atempo=1.5 -c:a aac", args);
        // 60 fps × 3 would exceed the cap.
        Assert.Contains("fps=60", args);
    }

    [Fact]
    public void ScaleIsEven() => Assert.Contains("scale=960:540", First(Spec() with { Scale = 0.5 }));

    [Theory]
    [InlineData("aac", "-c:a copy")]
    [InlineData("opus", "-c:a aac -b:a 192k")]
    [InlineData(null, "-an")]
    public void AudioHandling(string? codec, string expected) => Assert.Contains(expected, First(Spec(Info(audio: codec))));

    [Fact]
    public void RemoveAudio() => Assert.Contains("-an", First(Spec() with { RemoveAudio = true }));

    [Fact]
    public void FallbacksEndWithSoftware()
    {
        var sets = VideoArguments.Build(Spec());

        Assert.Contains("-map", sets[0]);
        Assert.DoesNotContain("-map", sets[1]);
        Assert.Contains("h264_mf", sets[0]);
        Assert.Contains("libx264", sets[^1]);
    }

    [Fact]
    public void LosslessUsesX264Crf17()
    {
        var args = First(Spec() with { Lossless = true });

        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-crf 17", args);
    }

    [Theory]
    [InlineData(1.25, "atempo=1.25")]
    [InlineData(4, "atempo=2.0,atempo=2")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    public void AtempoChains(double speed, string expected) => Assert.StartsWith(expected, VideoArguments.AtempoChain(speed));

    [Fact]
    public void ProgressParsing()
    {
        var reported = new List<double>();
        VideoOptimiser.ReportProgress("out_time_us=5000000", 10, reported.Add);
        VideoOptimiser.ReportProgress("frame=300", 10, reported.Add);
        VideoOptimiser.ReportProgress("out_time_us=20000000", 10, reported.Add);

        Assert.Equal([0.5, 0.99], reported);
    }
}

/// <summary>Real encodes of generated clips.</summary>
public sealed class VideoOptimisationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public VideoOptimisationTests()
    {
        _paths = new AppPaths(_dir.File("cache"));
        _paths.EnsureCreated();
        // Software keeps results the same on every machine.
        _settings.Compression.VideoTier = VideoTier.Smaller;
        _service = new FileOptimisationService(_settings, _paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>A barely compressed test clip (so re-encoding shrinks it).</summary>
    private string MakeClip(string name, double seconds = 3, int fps = 60, int width = 1280, int height = 720, bool audio = true)
    {
        var path = _dir.File(name);
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=s={width}x{height}:r={fps}:d={seconds}"),
        };
        if (audio)
            args.AddRange(["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"sine=f=440:d={seconds}"), "-c:a", "aac"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-qp", "8", "-pix_fmt", "yuv420p", "-shortest", path]);
        var result = ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg), args).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, result.StdErr);
        return path;
    }

    private async Task<VideoInfo> Probe(string path) => (await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), path))!;

    [ToolsFact]
    public async Task ShrinksAndCapsFrameRate()
    {
        var input = MakeClip("fast.mp4", fps: 120);
        var before = new FileInfo(input).Length;
        var progress = new List<double>();

        var result = await _service.OptimiseVideoAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace }, progress.Add);

        var info = await Probe(result.OutputPath);
        Assert.Equal(input, result.OutputPath);
        Assert.True(result.NewSize < before);
        Assert.InRange(info.PeakFrameRate, 59, 61);
        Assert.InRange(info.Duration.TotalSeconds, 2.8, 3.2);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Contains(progress, p => p > 0);
        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(input));
    }

    [ToolsFact]
    public async Task MovBecomesMp4InPlace()
    {
        var input = MakeClip("recording.mov");

        var result = await _service.OptimiseVideoAsync(input, new FileOptimisationRequest());

        Assert.Equal(_dir.File("recording.mp4"), result.OutputPath);
        Assert.False(File.Exists(input)); // backed up, then replaced by the MP4
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(FileFormat.Mov, result.InputFormat);
        Assert.Equal(FileFormat.Mp4, result.OutputFormat);
    }

    [ToolsFact]
    public async Task SpeedAndScaleAdjustFromTheOriginal()
    {
        var input = MakeClip("tutorial.mp4", seconds: 4);
        var first = await _service.OptimiseVideoAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.InPlace });

        var faster = await _service.AdjustAsync(first, new FileOptimisationRequest { Speed = 2 });
        var fasterInfo = await Probe(faster.OutputPath);
        var smaller = await _service.AdjustAsync(faster, new FileOptimisationRequest { Scale = 0.5 });
        var smallerInfo = await Probe(smaller.OutputPath);

        Assert.InRange(fasterInfo.Duration.TotalSeconds, 1.8, 2.2);
        Assert.Equal((640, 360), (smallerInfo.Width, smallerInfo.Height));
        Assert.InRange(smallerInfo.Duration.TotalSeconds, 1.8, 2.2); // keeps the 2× speed
        Assert.Equal(2, smaller.Speed);
    }

    [ToolsFact]
    public async Task RestoreBringsBackTheOriginalMov()
    {
        var input = MakeClip("clip.mov");
        var original = File.ReadAllBytes(input);
        var result = await _service.OptimiseVideoAsync(input, new FileOptimisationRequest());

        var restored = _service.Restore(result);

        Assert.Equal(input, restored);
        Assert.Equal(original, File.ReadAllBytes(restored));
        Assert.False(File.Exists(result.OutputPath));
    }

    [ToolsFact]
    public async Task HardwareTierWorksOrFallsBackToSoftware()
    {
        _settings.Compression.VideoTier = VideoTier.Fast;
        var input = MakeClip("hw.mp4", audio: false);

        var result = await _service.OptimiseVideoAsync(input, new FileOptimisationRequest { Behaviour = OutputBehaviour.Temporary });

        var info = await Probe(result.OutputPath);
        Assert.Equal("h264", info.VideoCodec);
        Assert.False(info.HasAudio);
    }

    [ToolsFact]
    public async Task NotAVideoIsRejected()
    {
        var input = _dir.File("fake.mp4");
        File.WriteAllText(input, "not a video");

        await Assert.ThrowsAnyAsync<Exception>(() => _service.OptimiseVideoAsync(input, new FileOptimisationRequest()));
        Assert.Equal("not a video", File.ReadAllText(input));
    }
}
