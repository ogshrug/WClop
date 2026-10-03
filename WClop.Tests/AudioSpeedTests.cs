using WClop.Core;
using WClop.Core.Audio;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

/// <summary>Speed changes for audio (atempo) and video frame handling (<c>changeSpeed(frames: keep | drop)</c>).</summary>
public sealed class AudioSpeedTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly PipelineRunner _runner;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public AudioSpeedTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
        _runner = new PipelineRunner(_service, _settings);
    }

    public void Dispose() => _dir.Dispose();

    private string Mp3(string name, int seconds = 6)
    {
        var path = _dir.File(name);
        var made = ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=f=440:d={seconds}", "-c:a", "libmp3lame", "-b:a", "192k", path])
            .GetAwaiter().GetResult();
        Assert.True(made.Succeeded, made.StdErr);
        return path;
    }

    private async Task<double> SecondsAsync(string path) =>
        (await AudioInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), path))!.Duration.TotalSeconds;

    private static AudioInfo Info => new(TimeSpan.FromSeconds(6), "mp3", 192, 44100, HasCoverArt: false);

    [Theory]
    [InlineData(1.5, "atempo=1.5")]
    [InlineData(4, "atempo=2.0,atempo=2")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    public void AudioSpeedChainsAtempo(double speed, string filter)
    {
        var args = AudioOptimiser.Arguments("in.mp3", "out.mp3", FileFormat.Mp3, 128, Info, new AudioEncodeOptions(Speed: speed));

        Assert.Equal(filter, args[args.ToList().IndexOf("-af") + 1]);
        Assert.Contains("libmp3lame", args);
    }

    [Fact]
    public void NormalSpeedHasNoFilter() =>
        Assert.DoesNotContain("-af", AudioOptimiser.Arguments("in.mp3", "out.mp3", FileFormat.Mp3, 128, Info));

    [Fact]
    public void ChangeSpeedTakesAudioAndAFramesChoice()
    {
        var step = PipelineCatalog.Compile("changeSpeed(factor: 2, frames: drop)").Steps[0];

        Assert.True(step.Spec.AppliesTo(MediaKind.Audio));
        Assert.True(step.Spec.AppliesTo(MediaKind.Video));
        Assert.Equal("drop", step.Get<string>("frames"));
        Assert.False(PipelineCatalog.TryCompile("changeSpeed(2, frames: some)", out _, out _));
    }

    [ToolsFact]
    public async Task SpeedsUpAudio()
    {
        var input = Mp3("talk.mp3");

        var result = await _service.OptimiseAsync(input, new FileOptimisationRequest { Speed = 2 });

        Assert.Equal(2, result.Speed);
        Assert.InRange(await SecondsAsync(result.OutputPath), 2.8, 3.2);
    }

    [ToolsFact]
    public async Task SpeedChangesStartFromTheOriginal()
    {
        var input = Mp3("talk.mp3");
        var doubled = await _service.OptimiseAsync(input, new FileOptimisationRequest { Speed = 2 });

        // 2× then 1.5× is 1.5× of the original, not 3×.
        var adjusted = await _service.AdjustAsync(doubled, new FileOptimisationRequest { Speed = 1.5 });

        Assert.Equal(1.5, adjusted.Speed);
        Assert.InRange(await SecondsAsync(adjusted.OutputPath), 3.8, 4.2);
    }

    [ToolsFact]
    public async Task PipelineSpeedIsRelativeToTheOriginal()
    {
        var input = Mp3("talk.mp3");

        var outcome = await _runner.RunFileAsync("test", PipelineCatalog.Compile("changeSpeed(2) -> changeSpeed(1.5)"), input,
            skipOptimisation: true, new PipelineContext());

        Assert.InRange(await SecondsAsync(outcome.Result.OutputPath), 3.8, 4.2);
    }

    [ToolsFact]
    public async Task SpeedsBeyondTwoAreChained()
    {
        var input = Mp3("talk.mp3", seconds: 8);

        var outcome = await _runner.RunFileAsync("test", PipelineCatalog.Compile("changeSpeed(4)"), input, skipOptimisation: true, new PipelineContext());

        Assert.InRange(await SecondsAsync(outcome.Result.OutputPath), 1.8, 2.2);
    }

    // Video frames

    private static VideoEncodeSpec Spec(double speed, bool dropFrames) => new()
    {
        Input = "in.mp4",
        Output = "out.mp4",
        Info = new VideoInfo(TimeSpan.FromSeconds(2), 640, 360, 30, 30, "h264", "aac", null),
        Encoder = VideoEncoder.X264,
        Speed = speed,
        DropFrames = dropFrames,
        FpsCap = 60,
    };

    [Fact]
    public void KeepingFramesRaisesTheFrameRate()
    {
        var args = VideoArguments.Build(Spec(2, dropFrames: false))[0];

        Assert.Equal("setpts=PTS/2", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.Contains("passthrough", args);
    }

    [Fact]
    public void DroppingFramesStaysAtTheSourceRate()
    {
        var args = VideoArguments.Build(Spec(2, dropFrames: true))[0];

        Assert.Equal("setpts=PTS/2,fps=30", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.DoesNotContain("passthrough", args);
    }

    [Fact]
    public void KeptFramesStillMeetTheCap()
    {
        // 30 fps at 3× would be 90 fps: capped at 60.
        var args = VideoArguments.Build(Spec(3, dropFrames: false))[0];

        Assert.Equal("setpts=PTS/3,fps=60", args[args.ToList().IndexOf("-vf") + 1]);
    }

    [ToolsTheory]
    [InlineData("keep", 60)]
    [InlineData("drop", 30)]
    public async Task VideoFramesAreKeptOrDropped(string frames, double fps)
    {
        var input = _dir.File($"clip-{frames}.mp4");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=2", "-c:v", "libx264", "-preset", "ultrafast", input]);
        Assert.True(made.Succeeded, made.StdErr);

        var outcome = await _runner.RunFileAsync("test", PipelineCatalog.Compile($"changeSpeed(factor: 2, frames: {frames})"), input,
            skipOptimisation: true, new PipelineContext());

        var info = (await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), outcome.Result.OutputPath))!;
        Assert.InRange(info.Duration.TotalSeconds, 0.9, 1.15);
        Assert.InRange(info.AverageFrameRate, fps - 2, fps + 2);
    }
}
