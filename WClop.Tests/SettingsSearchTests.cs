using WClop.Core.Settings;

namespace WClop.Tests;

public class SettingsSearchTests
{
    // A slice of the real Settings window, the way its index is built from the controls.
    private static readonly SettingsSearchEntry[] Entries =
    [
        new("General", "General"),
        new("Start WClop when I sign in", "General"),
        new("Check for new versions automatically", "General", "Updates"),
        new("Working folder", "General", "Working folder",
            "Optimised clipboard images, backups of originals and temporary files live here."),
        new("Clipboard", "Clipboard"),
        new("Optimise images when they're copied", "Clipboard"),
        new("Show optimised images in clipboard history (Win+V)", "Clipboard"),
        new("Ignore clipboards from Remote Desktop and virtual machines", "Clipboard"),
        new("Collect copied results, so they all paste at once as files", "Clipboard"),
        new("Apps to ignore", "Clipboard", "Apps to ignore", "Copies made in these apps are never touched (password managers mark their copies anyway)."),
        new("Keep colour profiles when removing metadata", "Compression", "Metadata"),
        new("Limit the frame rate to", "Compression", "Videos"),
        new("Show it under the cursor by tapping", "Results", "Drop zone",
            "While dragging, press and release the key on its own to bring the drop zone to the cursor."),
        new("Watermark", "Watermark"),
        new("Position", "Watermark", "Placement"),
    ];

    private static IReadOnlyList<string> Labels(string query) =>
        SettingsSearch.Find(Entries, query).Select(r => r.Entry.Label).ToList();

    [Fact]
    public void BlankQueryFindsNothing()
    {
        Assert.Empty(SettingsSearch.Find(Entries, ""));
        Assert.Empty(SettingsSearch.Find(Entries, "   "));
    }

    [Fact]
    public void WordStartsMatch() =>
        Assert.Equal("Check for new versions automatically", Labels("vers").First());

    [Fact]
    public void EveryWordMustMatch()
    {
        var labels = Labels("clipboard history");

        Assert.Equal("Show optimised images in clipboard history (Win+V)", labels.First());
        Assert.DoesNotContain("Ignore clipboards from Remote Desktop and virtual machines", labels);
    }

    [Fact]
    public void TyposAndAmericanSpellingsMatch()
    {
        Assert.Equal("Show optimised images in clipboard history (Win+V)", Labels("clipbaord histroy").First());
        Assert.Equal("Keep colour profiles when removing metadata", Labels("color profile").First());
        Assert.Contains("Optimise images when they're copied", Labels("optimize copied"));
    }

    [Fact]
    public void LettersInOrderWithinAWordMatch() =>
        Assert.Contains("Watermark", Labels("wtrmrk"));

    [Fact]
    public void HintsAreSearchedButCountLessThanLabels()
    {
        var labels = Labels("password");

        Assert.Equal(["Apps to ignore"], labels);
        Assert.Equal("Working folder", Labels("backups").Single());

        // The same word in a label beats it in a hint.
        Assert.Equal("Collect copied results, so they all paste at once as files", Labels("copied results").First());
        Assert.True(SettingsSearch.Find(Entries, "copies").All(r => r.Entry.Label == "Apps to ignore" || r.Score < 300));
    }

    [Fact]
    public void SectionsAndTabsHelpNarrowDown()
    {
        Assert.Equal("Show it under the cursor by tapping", Labels("drop zone cursor").First());
        Assert.Equal("Position", Labels("watermark position").First());
    }

    [Fact]
    public void LabelMatchesRankAboveSectionMatches()
    {
        var labels = Labels("watermark");

        Assert.Equal("Watermark", labels[0]);
        Assert.Contains("Position", labels);
    }

    [Fact]
    public void ShortGibberishMatchesNothing()
    {
        Assert.Empty(Labels("qzx"));
        Assert.Empty(Labels("clipboard qzx"));
    }

    [Fact]
    public void PunctuationAndCaseDontMatter() =>
        Assert.Equal("Show optimised images in clipboard history (Win+V)", Labels("WIN+V").First());

    [Fact]
    public void ResultsPointBackAtTheirEntries()
    {
        var result = SettingsSearch.Find(Entries, "frame rate").First();

        Assert.Same(Entries[result.Index], result.Entry);
        Assert.Equal("Compression › Videos", result.Entry.Location);
    }

    [Fact]
    public void LocationLeavesOutASectionThatIsTheLabel()
    {
        Assert.Equal("Clipboard", Entries[9].Location);
        Assert.Equal("General", Entries[0].Location);
    }

    [Fact]
    public void ResultsAreCapped() =>
        Assert.Equal(3, SettingsSearch.Find(Entries, "o", max: 3).Count);

    [Theory]
    [InlineData("optimise", "optimize", 1)]
    [InlineData("clipboard", "clipbaord", 1)]
    [InlineData("colour", "color", 1)]
    [InlineData("history", "histroy", 1)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    public void EditDistanceCountsSwapsAsOne(string a, string b, int expected) =>
        Assert.Equal(expected, SettingsSearch.EditDistance(a, b));

    [Fact]
    public void WordScoresAreOrdered()
    {
        var exact = SettingsSearch.WordScore("folder", "folder");
        var prefix = SettingsSearch.WordScore("fold", "folder");
        var inside = SettingsSearch.WordScore("old", "folder");
        var typo = SettingsSearch.WordScore("foldr", "folder");
        var scattered = SettingsSearch.WordScore("fdr", "folder");

        Assert.True(exact > prefix && prefix > inside && inside > typo && typo > scattered && scattered > 0);
        Assert.Equal(0, SettingsSearch.WordScore("xyz", "folder"));
    }
}
