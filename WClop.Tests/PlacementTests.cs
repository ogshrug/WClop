using WClop.Core.Placement;
using WClop.Core.Settings;
using WClop.Core.Storage;

namespace WClop.Tests;

public class NameTemplateTests
{
    private static readonly DateTime Now = new(2026, 3, 7, 14, 5, 9);
    private const string Source = @"C:\Shots\photo.png";

    [Theory]
    [InlineData("%f-optimised", @"C:\Shots\photo-optimised")]
    [InlineData("%P/optimised/%f", @"C:\Shots\optimised\photo")]
    [InlineData("%f.%e", @"C:\Shots\photo.png")]
    [InlineData("%F-copy", @"C:\Shots\photo-copy")]
    [InlineData("%y-%m-%d %H.%M.%S %f", @"C:\Shots\2026-03-07 14.05.09 photo")]
    [InlineData("%n %w", @"C:\Shots\March Saturday")]
    [InlineData("%p", @"C:\Shots\PM")]
    [InlineData("100%% %f", @"C:\Shots\100% photo")]
    [InlineData(@"D:\Out\%f", @"D:\Out\photo")]
    public void RendersTokens(string template, string expected) =>
        Assert.Equal(expected, NameTemplate.Render(template, Source, Now));

    [Fact]
    public void CounterAndRandom()
    {
        var counter = 41;
        Assert.Equal(@"C:\Shots\photo-42", NameTemplate.Render("%f-%i", Source, Now, () => ++counter));
        Assert.Matches(@"^C:\\Shots\\photo-[a-z0-9]{5}$", NameTemplate.Render("%f-%r", Source, Now));
    }

    [Fact]
    public void ExpandsEnvironmentVariables()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(
            Path.Combine(profile, "Out", "photo"),
            NameTemplate.Render("%USERPROFILE%/Out/%f", Source, Now));
    }

    [Theory]
    [InlineData(@"C:\Shots\photo-optimised.png", "%f-optimised", true)]
    [InlineData(@"C:\Shots\PHOTO-OPTIMISED.png", "%f-optimised", true)]
    [InlineData(@"C:\Shots\photo.png", "%f-optimised", false)]
    [InlineData(@"C:\Shots\2026-photo.png", "%y-%f", true)]
    [InlineData(@"C:\Shots\photo.png", "%f", false)]
    [InlineData(@"C:\Shots\photo-optimised.png", "%P/optimised/%f", false)]
    public void DetectsAlreadyTemplatedNames(string path, string template, bool expected) =>
        Assert.Equal(expected, NameTemplate.IsAlreadyTemplated(path, template));
}

public sealed class FilePlacerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly OptimisationMarkers _markers;
    private readonly FilePlacer _placer;

    public FilePlacerTests()
    {
        _markers = new OptimisationMarkers(new OptimisationDatabase(_dir.File("test.db")));
        _placer = new FilePlacer(_markers);
        Directory.CreateDirectory(_dir.File("work"));
    }

    public void Dispose() => _dir.Dispose();

    private string Output(string ext, string content = "optimised")
    {
        var path = Path.Combine(_dir.File("work"), Guid.NewGuid().ToString("N") + ext);
        File.WriteAllText(path, content);
        return path;
    }

    private string Original(string name, string content = "original")
    {
        var path = _dir.File(name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void InPlaceReplacesOriginalAndMarksIt()
    {
        var original = Original("photo.png");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".png"), OriginalPath = original, Behaviour = OutputBehaviour.InPlace,
        });

        Assert.Equal(original, placed);
        Assert.Equal("optimised", File.ReadAllText(placed));
        Assert.Equal(MarkerStatus.Optimised, _markers.Get(placed));
    }

    [Fact]
    public void InPlaceKeepsTheUsersExtensionSpelling()
    {
        var original = Original("photo.jpeg");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".jpg"), OriginalPath = original, Behaviour = OutputBehaviour.InPlace,
        });

        Assert.Equal(original, placed);
    }

    [Fact]
    public void InPlaceWithNewFormatRemovesOldFile()
    {
        var original = Original("photo.png");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".jpg"), OriginalPath = original, Behaviour = OutputBehaviour.InPlace,
        });

        Assert.Equal(_dir.File("photo.jpg"), placed);
        Assert.False(File.Exists(original));
    }

    [Fact]
    public void InPlaceWithNewFormatNeverOverwritesAnUnrelatedFile()
    {
        var original = Original("photo.png");
        var bystander = Original("photo.jpg", "someone else's");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".jpg"), OriginalPath = original, Behaviour = OutputBehaviour.InPlace,
        });

        Assert.Equal(_dir.File("photo (2).jpg"), placed);
        Assert.Equal("someone else's", File.ReadAllText(bystander));
    }

    [Fact]
    public void SameFolderUsesTemplateAndKeepsOriginal()
    {
        var original = Original("photo.png");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".png"), OriginalPath = original, Behaviour = OutputBehaviour.SameFolder,
            Template = "%f-optimised",
        });

        Assert.Equal(_dir.File("photo-optimised.png"), placed);
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public void SameFolderDoesNotApplyTemplateTwice()
    {
        var original = Original("photo-optimised.png");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".png"), OriginalPath = original, Behaviour = OutputBehaviour.SameFolder,
            Template = "%f-optimised",
        });

        Assert.Equal(original, placed);
    }

    [Fact]
    public void SameFolderOverwritesItsOwnPreviousOutput()
    {
        var original = Original("photo.png");
        var request = new PlacementRequest
        {
            OutputPath = Output(".png", "first"), OriginalPath = original, Behaviour = OutputBehaviour.SameFolder,
            Template = "%f-optimised",
        };
        _placer.Place(request);

        var placed = _placer.Place(request with { OutputPath = Output(".png", "second") });

        Assert.Equal(_dir.File("photo-optimised.png"), placed);
        Assert.Equal("second", File.ReadAllText(placed));
    }

    [Fact]
    public void SpecificFolderCreatesFolders()
    {
        var original = Original("photo.png");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".png"), OriginalPath = original, Behaviour = OutputBehaviour.SpecificFolder,
            Template = "%P/optimised/%f",
        });

        Assert.Equal(_dir.File(@"optimised\photo.png"), placed);
    }

    [Fact]
    public void TemporaryLeavesOutputInWorkDir()
    {
        var original = Original("photo.png");
        var output = Output(".png");

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = output, OriginalPath = original, Behaviour = OutputBehaviour.Temporary,
        });

        Assert.Equal(output, placed);
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public void PreservesTimes()
    {
        var original = Original("photo.png");
        var times = new FileTimes(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc));

        var placed = _placer.Place(new PlacementRequest
        {
            OutputPath = Output(".png"), OriginalPath = original, Behaviour = OutputBehaviour.InPlace,
            PreserveTimes = times,
        });

        Assert.Equal(times, FileTimes.Of(placed));
    }
}
