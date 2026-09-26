using System.Text.RegularExpressions;
using WClop.Core;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Processes;
using WClop.Core.Settings;
using WClop.Core.Storage;
using WClop.Core.Video;

namespace WClop.Tests;

public class PipelineSyntaxTests
{
    [Fact]
    public void ParsesStepsArgumentsAndComments()
    {
        var steps = PipelineSyntax.Parse("""
            # screenshots only
            if(regex: "^screen\s?shot", sizeGreaterThan: 1MB)
            -> downscale(0.5)
            -> removeAudio
            → move(to: '~/Pictures/%y/')
            """);

        Assert.Equal(["if", "downscale", "removeAudio", "move"], steps.Select(s => s.Name));
        Assert.Equal(("regex", "^screen\\s?shot"), (steps[0].Args[0].Name, steps[0].Args[0].Value.Text));
        Assert.Equal("1MB", steps[0].Args[1].Value.Text);
        Assert.Null(steps[1].Args[0].Name);
        Assert.Empty(steps[2].Args);
        Assert.Equal("~/Pictures/%y/", steps[3].Args[0].Value.Text);
    }

    [Fact]
    public void NegativeNumbersAndArrowsWithoutSpaces()
    {
        var steps = PipelineSyntax.Parse("normalize(lufs: -14)->stripExif");
        Assert.Equal("-14", steps[0].Args[0].Value.Text);
        Assert.Equal("stripExif", steps[1].Name);
    }

    [Fact]
    public void WindowsFoldersEndingInABackslash()
    {
        var steps = PipelineSyntax.Parse("""copy(to: "C:\Out\") -> move(to: "D:\Pics\", overwrite: true) -> rename("say \"hi\" now")""");
        Assert.Equal(@"C:\Out\", steps[0].Args[0].Value.Text);
        Assert.Equal(@"D:\Pics\", steps[1].Args[0].Value.Text);
        Assert.Equal("say \"hi\" now", steps[2].Args[0].Value.Text);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("optimise(", "Expected a value")]
    [InlineData("optimise -> ", "Expected a step name")]
    [InlineData("optimise convert", "Expected '->'")]
    [InlineData("move(to: \"x)", "never closed")]
    [InlineData("optimise; convert", "Unexpected ';'")]
    public void ReportsSyntaxErrors(string text, string message)
    {
        var e = Assert.Throws<PipelineSyntaxException>(() => PipelineSyntax.Parse(text));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void ErrorPositionsBecomeLinesAndColumns()
    {
        Assert.False(PipelineCatalog.TryCompile("optimise\n-> dowscale(0.5)", out _, out var error));
        Assert.StartsWith("Line 2, column 4:", error);
        Assert.Contains("did you mean downscale?", error);
    }
}

public class PipelineCatalogTests
{
    [Fact]
    public void ConvertsArgumentTypes()
    {
        var pipeline = PipelineCatalog.Compile(
            "if(sizeGreaterThan: 1.5MB, regex: \"^IMG\") -> downscale(50%) -> crop(aspectRatio: \"16:9\") -> convert(jpeg) -> changeSpeed(2x) -> targetSize(10MB)");

        Assert.Equal((long)(1.5 * 1024 * 1024), pipeline.Steps[0].Get<long>("sizeGreaterThan"));
        Assert.IsType<Regex>(pipeline.Steps[0].Values["regex"]);
        Assert.Equal(0.5, pipeline.Steps[1].Get<double>("factor"));
        Assert.Equal(16.0 / 9, pipeline.Steps[2].Get<double>("aspectRatio"), 6);
        Assert.Equal("jpg", pipeline.Steps[3].Get<string>("to"));
        Assert.Equal(2.0, pipeline.Steps[4].Get<double>("factor"));
        Assert.Equal(10L * 1024 * 1024, pipeline.Steps[5].Get<long>("size"));
        Assert.True(pipeline.Encodes);
    }

    [Fact]
    public void RegexIsCaseInsensitiveUnlessItHasCapitals()
    {
        var lower = (Regex)PipelineCatalog.Compile("if(regex: \"^screen\\S+\")").Steps[0].Values["regex"];
        var upper = (Regex)PipelineCatalog.Compile("if(regex: \"^IMG\")").Steps[0].Values["regex"];
        Assert.Matches(lower, "Screenshot1.png");
        Assert.Matches(upper, "IMG_1.jpg");
        Assert.DoesNotMatch(upper, "img_1.jpg");
    }

    [Theory]
    [InlineData("frobnicate", "Unknown step 'frobnicate'")]
    [InlineData("optimise(factor: 500)", "between 1 and 100")]
    [InlineData("optimise(quality: 5)", "optimise has no 'quality'")]
    [InlineData("convert(to: bmp)", "should be one of")]
    [InlineData("downscale", "downscale needs one of factor, width, height, longEdge")]
    [InlineData("if", "needs at least one condition")]
    [InlineData("targetSize(huge)", "a size like 10MB")]
    [InlineData("crop(aspectRatio: \"wide\")", "a ratio like")]
    [InlineData("optimise(factor: 5, factor: 6)", "given twice")]
    [InlineData("delete -> optimise", "Nothing can come after delete")]
    [InlineData("if(regex: \"[\")", "Invalid regex")]
    [InlineData("removeAudio(true)", "takes no values")]
    public void RejectsMistakes(string text, string message)
    {
        var e = Assert.Throws<PipelineSyntaxException>(() => PipelineCatalog.Compile(text));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void AliasesAndStepsWithoutEncoding()
    {
        var pipeline = PipelineCatalog.Compile("optimize -> resize(width: 800) -> fit(5MB) -> normalise");
        Assert.Equal(["optimise", "downscale", "targetSize", "normalize"], pipeline.Steps.Select(s => s.Name));
        Assert.False(PipelineCatalog.Compile("if(type: png) -> stripExif -> copy(to: \"D:/x/\")").Encodes);
    }

    [Fact]
    public void PromptDocumentsEveryStep()
    {
        var prompt = PipelineCatalog.Prompt();
        Assert.All(PipelineCatalog.Steps, s => Assert.Contains($"**{s.Name}**", prompt));
        // Every example in the reference must itself be valid.
        Assert.All(PipelineCatalog.Steps, s => PipelineCatalog.Compile(s.Example));
        foreach (var line in prompt.Split("```")[^2].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            PipelineCatalog.Compile(line);
    }

    [Theory]
    [InlineData(4000, 3000, "width: 1000", 1000, 3000, 1500, 0)]
    [InlineData(4000, 3000, "aspectRatio: \"1:1\"", 3000, 3000, 500, 0)]
    [InlineData(1080, 1920, "aspectRatio: \"16:9\"", 1080, 608, 0, 656)]
    [InlineData(4000, 3000, "aspectRatio: \"16:9\", width: 1920", 1920, 1080, 1040, 960)]
    public void CropRectangles(int width, int height, string args, int w, int h, int x, int y)
    {
        var step = PipelineCatalog.Compile($"crop({args})").Steps[0];
        Assert.Equal((w, h, x, y), PipelineRunner.CropRectangle(step, new ImageSize(width, height), even: false));
    }

    [Fact]
    public void CropToTheSameSizeDoesNothing()
    {
        var step = PipelineCatalog.Compile("crop(aspectRatio: \"4:3\")").Steps[0];
        Assert.Null(PipelineRunner.CropRectangle(step, new ImageSize(800, 600), even: true));
    }

    [Fact]
    public void DownscaleFactorsTakeTheTightestLimit()
    {
        var step = PipelineCatalog.Compile("downscale(longEdge: 1000, width: 400)").Steps[0];
        Assert.Equal(0.2, PipelineRunner.DownscaleFactor(step, new ImageSize(2000, 1000)), 6);
        Assert.Equal(1, PipelineRunner.DownscaleFactor(PipelineCatalog.Compile("downscale(longEdge: 5000)").Steps[0], new ImageSize(2000, 1000)));
    }

    [Fact]
    public void DestinationsForFoldersNamesAndTokens()
    {
        var current = @"C:\Shots\Screenshot 1.png";
        var now = new DateTime(2026, 9, 27, 10, 0, 0);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, "Pictures", "2026", "09", "Screenshot 1.png"),
            PipelineRunner.Destination("~/Pictures/%y/%m/", current, now));
        Assert.Equal(@"C:\Shots\Screenshot 1-web.png", PipelineRunner.Destination("%f-web", current, now));
        Assert.Equal(@"C:\Shots\shot.jpg", PipelineRunner.Destination("shot.jpg", current, now));
        Assert.Equal(@"D:\Out\a.png", PipelineRunner.Destination(@"D:\Out\a", current, now));
    }

    [Theory]
    [InlineData("image", FileFormat.Png, true)]
    [InlineData("video", FileFormat.Png, false)]
    [InlineData("jpg", FileFormat.Jpeg, true)]
    [InlineData(".jpeg", FileFormat.Jpeg, true)]
    [InlineData("gif", FileFormat.Png, false)]
    [InlineData("nonsense", FileFormat.Png, false)]
    public void TypeFilters(string type, FileFormat format, bool matches) =>
        Assert.Equal(matches, PipelineRunner.MatchesType(type, format));

    [Fact]
    public void SavedPipelinesBecomeDropPresets()
    {
        var settings = new PipelineSettings
        {
            Saved =
            [
                new SavedPipeline { Name = "Web", Script = "downscale(longEdge: 1920) -> convert(webp)" },
                new SavedPipeline { Name = "Hidden", Script = "optimise", ShowInDropZone = false },
                new SavedPipeline { Name = "Broken", Script = "frobnicate" },
            ],
        };
        var presets = DropPresets.WithPipelines(settings);
        Assert.Equal(DropPresets.All.Count + 1, presets.Count);
        Assert.Equal("Web", presets[^1].Pipeline);
        Assert.Contains("downscale → convert", presets[^1].Description);
        Assert.Equal(DropPresets.All.Count, DropPresets.Step(0, -1, presets.Count));
    }
}

public sealed class PipelineRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppSettings _settings = new();
    private readonly FileOptimisationService _service;
    private readonly PipelineLibrary _library;
    private readonly ToolLocator _tools = ToolLocator.CreateDefault();

    public PipelineRunnerTests()
    {
        var paths = new AppPaths(_dir.File("cache"));
        paths.EnsureCreated();
        _service = new FileOptimisationService(_settings, paths, _tools, new OptimisationDatabase(_dir.File("db.sqlite")));
        _library = new PipelineLibrary(_settings, new PipelineRunner(_service, _settings));
    }

    public void Dispose() => _dir.Dispose();

    private Task<PipelineOutcome> Run(string pipeline, string file, bool skipOptimisation = true, PipelineContext? context = null) =>
        _library.Runner.RunFileAsync("test", PipelineCatalog.Compile(pipeline), file, skipOptimisation, context ?? new PipelineContext());

    [ToolsFact]
    public async Task DownscaleConvertAndRenameThenRestore()
    {
        var input = TestImages.SavePng(TestImages.Photo(1600, 1200), _dir.File("Screenshot 7.png"));
        var original = await File.ReadAllBytesAsync(input);

        var outcome = await Run("""if(regex: "^screenshot (\d+)") -> downscale(longEdge: 800) -> convert(webp) -> rename(to: "shot-$1")""", input);

        var output = outcome.Result.OutputPath;
        Assert.Equal(_dir.File("shot-7.webp"), output);
        Assert.False(File.Exists(input));
        Assert.Equal(FileFormat.WebP, FileTypeSniffer.Detect(output));
        Assert.Equal(new ImageSize(800, 600), ImageDecoding.TryReadSize(output));
        Assert.True(outcome.Result.IsConversion);
        Assert.Equal("test", outcome.Result.Pipeline);

        // Restore puts the original back next to the output, under its own extension.
        var restored = _service.Restore(outcome.Result);
        Assert.Equal(original, await File.ReadAllBytesAsync(restored));
    }

    [ToolsFact]
    public async Task FilterStopsThePipelineButKeepsTheOptimisation()
    {
        var input = TestImages.SavePng(TestImages.Photo(600, 400), _dir.File("photo.png"));

        var outcome = await Run("ifNot(type: png) -> convert(jpeg)", input, skipOptimisation: false);

        Assert.Equal("ifNot(type: png)", outcome.StoppedBy);
        Assert.Equal(input, outcome.Result.OutputPath);
        Assert.True(outcome.Result.NewSize < outcome.Result.OldSize); // optimised before the filter
    }

    [ToolsFact]
    public async Task CropCopyAndMove()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(1200, 900), _dir.File("pic.jpg"));
        var copies = _dir.File("copies") + Path.DirectorySeparatorChar;
        var moved = _dir.File("moved") + Path.DirectorySeparatorChar;

        var outcome = await Run($"crop(aspectRatio: \"1:1\") -> copy(to: \"{copies}\") -> copy(to: \"{copies}\") -> move(to: \"{moved}\")", input);

        Assert.Equal(new ImageSize(900, 900), ImageDecoding.TryReadSize(outcome.Result.OutputPath));
        Assert.Equal(Path.Combine(moved, "pic.jpg"), outcome.Result.OutputPath);
        Assert.True(File.Exists(Path.Combine(copies, "pic.jpg")));
        Assert.True(File.Exists(Path.Combine(copies, "pic (2).jpg"))); // never overwrites without overwrite: true
        Assert.Equal(MarkerStatus.Optimised, _service.Markers.Get(Path.Combine(copies, "pic.jpg")));
    }

    [ToolsFact]
    public async Task StripExifRemovesCameraData()
    {
        var input = TestImages.SaveJpeg(TestImages.Photo(400, 300), _dir.File("cam.jpg"));
        var tag = await ProcessRunner.RunAsync(_tools.Require(Tool.Exiftool), ["-overwrite_original", "-Make=TestCam", input]);
        Assert.True(tag.Succeeded, tag.StdErr);

        var outcome = await Run("stripExif", input);

        var read = await ProcessRunner.RunAsync(_tools.Require(Tool.Exiftool), ["-Make", "-s3", outcome.Result.OutputPath]);
        Assert.DoesNotContain("TestCam", read.StdOut);
    }

    [ToolsFact]
    public async Task ScriptsGetTheFileAndCanHandOverANewOne()
    {
        var input = TestImages.SavePng(TestImages.Photo(300, 200), _dir.File("s.png"));
        var outcome = await Run(
            """runScript(code: "$out = $env:WCLOP_INPUT_FILE -replace '\.png$', '-copy.png'; Copy-Item $env:WCLOP_INPUT_FILE $out; Write-Output $out")""",
            input);

        Assert.Equal(_dir.File("s-copy.png"), outcome.Result.OutputPath);
        Assert.Contains(outcome.Log, l => l.StartsWith("script handed over", StringComparison.Ordinal));
    }

    [ToolsFact]
    public async Task FailingScriptsReportAndWriteALog()
    {
        var input = TestImages.SavePng(TestImages.Photo(300, 200), _dir.File("f.png"));
        var e = await Assert.ThrowsAsync<PipelineException>(() => Run("runScript(code: \"Write-Error nope; exit 3\")", input));
        Assert.Contains("exit code 3", e.Message);
        Assert.Single(Directory.GetFiles(_service.Paths.ProcessLogs, "script-*.log"));
    }

    [ToolsFact]
    public async Task ScriptsCanBeDisallowed()
    {
        var input = TestImages.SavePng(TestImages.Photo(300, 200), _dir.File("n.png"));
        await Assert.ThrowsAsync<PipelineException>(() =>
            Run("runScript(code: \"echo hi\")", input, context: new PipelineContext { AllowScripts = false }));
    }

    [ToolsFact]
    public async Task StepsForOtherMediaAreSkipped()
    {
        var input = TestImages.SavePng(TestImages.Photo(300, 200), _dir.File("i.png"));
        var outcome = await Run("removeAudio -> capFps(30)", input);
        Assert.False(outcome.Changed);
        Assert.All(outcome.Log, l => Assert.Contains("skipped", l));
    }

    [ToolsFact]
    public async Task VideoStepsRemoveAudioCapFpsAndCrop()
    {
        var input = _dir.File("clip.mp4");
        var made = await ProcessRunner.RunAsync(_tools.Require(Tool.Ffmpeg),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=60:d=2",
            "-f", "lavfi", "-i", "sine=f=440:d=2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", input,
        ]);
        Assert.True(made.Succeeded, made.StdErr);

        var outcome = await Run("removeAudio -> capFps(30) -> crop(aspectRatio: \"1:1\")", input);

        var info = await VideoInfo.ProbeAsync(_tools.Require(Tool.Ffprobe), outcome.Result.OutputPath);
        Assert.NotNull(info);
        Assert.False(info.HasAudio);
        Assert.Equal((360, 360), (info.Width, info.Height));
        Assert.InRange(info.AverageFrameRate, 29, 31);
    }

    [ToolsFact]
    public async Task AttachedPipelinesMatchTriggerFolderAndKind()
    {
        var folder = _dir.File("watched");
        Directory.CreateDirectory(folder);
        _settings.Pipelines.Saved.Add(new SavedPipeline { Name = "a", Script = "stripExif" });
        _settings.Pipelines.Saved.Add(new SavedPipeline { Name = "b", Script = "optimise", SkipOptimisation = true });
        _settings.Pipelines.Attached.Add(new PipelineAttachment { Pipeline = "a", Trigger = PipelineTrigger.Folder, Folder = folder });
        _settings.Pipelines.Attached.Add(new PipelineAttachment { Pipeline = "b", Trigger = PipelineTrigger.Folder, Folder = folder, Kinds = ["video"] });
        _settings.Pipelines.Attached.Add(new PipelineAttachment { Pipeline = "b", Trigger = PipelineTrigger.Clipboard });

        var inside = TestImages.SavePng(TestImages.Photo(100, 100), Path.Combine(folder, "x.png"));
        var outside = TestImages.SavePng(TestImages.Photo(100, 100), _dir.File("y.png"));

        Assert.Equal(["a"], _library.AttachedTo(PipelineTrigger.Folder, inside).Select(p => p.Name));
        Assert.Empty(_library.AttachedTo(PipelineTrigger.Folder, outside));
        Assert.Equal(["b"], _library.AttachedTo(PipelineTrigger.Clipboard, outside).Select(p => p.Name));
        Assert.True(PipelineLibrary.SkipsOptimisation(_library.AttachedTo(PipelineTrigger.Clipboard, outside)));
        Assert.False(PipelineLibrary.SkipsOptimisation(_library.AttachedTo(PipelineTrigger.Folder, inside)));

        var (result, hide) = await _library.RunAllAsync(_library.AttachedTo(PipelineTrigger.Folder, inside),
            await _service.DescribeAsync(inside), new PipelineContext());
        Assert.False(hide);
        Assert.Equal("a", result.Pipeline);
    }

    [Fact]
    public void ResolvesSavedNamesOrInlinePipelines()
    {
        _settings.Pipelines.Saved.Add(new SavedPipeline { Name = "Web", Script = "convert(webp)", HideResult = true });
        Assert.True(_library.Resolve("web").HideResult);
        var inline = _library.Resolve("downscale(0.5) -> stripExif");
        Assert.True(inline.SkipOptimisation); // it re-encodes anyway
        Assert.Contains("No pipeline called 'nope'", Assert.Throws<PipelineException>(() => _library.Resolve("nope")).Message);
    }
}
