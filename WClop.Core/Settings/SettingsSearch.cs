using System.Globalization;
using System.Text;

namespace WClop.Core.Settings;

/// <summary>
/// One searchable thing in the Settings window: a setting's label, the tab and section it's in, and any hint text
/// under it. The window builds these from its own controls, so new settings are found without being listed here.
/// </summary>
public sealed record SettingsSearchEntry(string Label, string Tab, string? Section = null, string? Hint = null)
{
    /// <summary>"Tab › Section", for showing where a result is.</summary>
    public string Location => Section is { Length: > 0 } section && section != Label ? $"{Tab} › {section}" : Tab;
}

public sealed record SettingsSearchResult(SettingsSearchEntry Entry, int Index, int Score);

/// <summary>
/// The Settings window's search: every word typed has to match somewhere in an entry, by whole word, word start,
/// part of a word, a typo or two ("clipbaord", and "optimize" / "color" for "optimise" / "colour"), or letters in
/// order within a word ("wtrmrk"). Matches in the label count most, then the section and tab, then the hint.
/// </summary>
public static class SettingsSearch
{
    private const int LabelWeight = 3, SectionWeight = 2, HintWeight = 1;

    /// <summary>The best matches for <paramref name="query"/>, best first; empty for a blank query.</summary>
    public static IReadOnlyList<SettingsSearchResult> Find(IReadOnlyList<SettingsSearchEntry> entries, string query, int max = 12)
    {
        var terms = Words(query);
        if (terms.Count == 0)
            return [];

        var phrase = Normalise(query).Trim();
        var results = new List<SettingsSearchResult>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (Score(entries[i], terms, phrase) is { } score)
                results.Add(new SettingsSearchResult(entries[i], i, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Entry.Label.Length)
            .ThenBy(r => r.Index)
            .Take(max)
            .ToList();
    }

    private static int? Score(SettingsSearchEntry entry, IReadOnlyList<string> terms, string phrase)
    {
        var label = Words(entry.Label);
        var section = Words($"{entry.Section} {entry.Tab}");
        var hint = Words(entry.Hint ?? "");

        var total = 0;
        foreach (var term in terms)
        {
            var best = Math.Max(
                LabelWeight * BestWordScore(term, label),
                Math.Max(SectionWeight * BestWordScore(term, section), HintWeight * BestWordScore(term, hint)));
            if (best == 0)
                return null;
            total += best;
        }

        // Typing (the start of) a label word for word is the strongest signal.
        if (terms.Count > 1 && Normalise(entry.Label).Contains(phrase, StringComparison.Ordinal))
            total += 150;
        return total;
    }

    private static int BestWordScore(string term, IReadOnlyList<string> words)
    {
        var best = 0;
        foreach (var word in words)
            best = Math.Max(best, WordScore(term, word));
        return best;
    }

    /// <summary>How well one typed word matches one word of the text: 0 for no match, 100 for the same word.</summary>
    public static int WordScore(string term, string word)
    {
        if (term.Length == 0 || word.Length == 0)
            return 0;
        if (word == term)
            return 100;
        if (word.StartsWith(term, StringComparison.Ordinal))
            return 70 + 20 * term.Length / word.Length;
        if (term.Length >= 3 && word.Contains(term, StringComparison.Ordinal))
            return 50;

        if (term.Length >= 4)
        {
            // Compare against the word cut to about the term's length too, so a typo in a word start still counts.
            var allowed = term.Length >= 8 ? 2 : 1;
            var distance = Math.Min(
                EditDistance(term, word),
                word.Length > term.Length ? EditDistance(term, word[..term.Length]) : int.MaxValue);
            if (distance <= allowed)
                return distance == 1 ? 40 : 25;
        }

        if (term.Length >= 3 && term[0] == word[0] && IsSubsequence(term, word))
            return 20;
        return 0;
    }

    /// <summary>Optimal string alignment distance: insertions, deletions, substitutions and swapped neighbours.</summary>
    public static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }

        return d[a.Length, b.Length];
    }

    private static bool IsSubsequence(string term, string word)
    {
        var at = 0;
        foreach (var c in word)
        {
            if (at < term.Length && term[at] == c)
                at++;
        }

        return at == term.Length;
    }

    /// <summary>Lower-case words with accents removed; punctuation separates words.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalise(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString();
    }
}
