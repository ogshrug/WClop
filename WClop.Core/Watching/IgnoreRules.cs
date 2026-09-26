using System.Text;
using System.Text.RegularExpressions;

namespace WClop.Core.Watching;

/// <summary>
/// Gitignore-style rules from a per-folder ignore file (project.md §4.7): <c>.wclopignore-image</c>,
/// <c>-video</c>, <c>-pdf</c>, <c>-audio</c> (Clop's <c>.clopignore-*</c> names are honoured too).
/// Supports blank lines, <c>#</c> comments, <c>!</c> negation (last match wins), <c>*</c>, <c>?</c>, <c>**</c>,
/// a leading <c>/</c> to anchor to the watched folder, and a trailing <c>/</c> for directories.
/// A pattern without a slash matches a name at any depth.
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<(Regex Pattern, bool Negated, bool DirectoryOnly)> _rules = [];

    public static readonly IgnoreRules Empty = new([]);

    public IgnoreRules(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var negated = line.StartsWith('!');
            if (negated)
                line = line[1..];

            var directoryOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
                continue;

            var anchored = line.StartsWith('/') || line.Contains('/');
            line = line.TrimStart('/');

            // File rules may also name a parent folder ("drafts" ignores drafts/x.png); folder-only rules are
            // tested against the file's parent folders alone.
            var regex = new StringBuilder(anchored ? "^" : "(^|/)");
            regex.Append(GlobToRegex(line));
            regex.Append(directoryOnly ? "$" : "(/|$)");
            _rules.Add((new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), negated, directoryOnly));
        }
    }

    public static string FileName(string kind) => $".wclopignore-{kind}";
    public static string ClopFileName(string kind) => $".clopignore-{kind}";

    /// <summary>Loads the rules for <paramref name="kind"/> from a watched folder, or <see cref="Empty"/>.</summary>
    public static IgnoreRules Load(string root, string kind)
    {
        var lines = new List<string>();
        foreach (var name in new[] { FileName(kind), ClopFileName(kind) })
        {
            var path = Path.Combine(root, name);
            try
            {
                if (File.Exists(path))
                    lines.AddRange(File.ReadAllLines(path));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return lines.Count == 0 ? Empty : new IgnoreRules(lines);
    }

    /// <summary>Whether <paramref name="relativePath"/> (relative to the watched folder) is ignored.</summary>
    public bool IsIgnored(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var folders = new List<string>();
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            folders.Add(path[..slash]);

        var ignored = false;
        foreach (var (pattern, negated, directoryOnly) in _rules)
        {
            var matches = directoryOnly ? folders.Any(pattern.IsMatch) : pattern.IsMatch(path);
            if (matches)
                ignored = !negated;
        }

        return ignored;
    }

    private static string GlobToRegex(string glob)
    {
        var result = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" matches zero or more folders; a bare "**" matches anything.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    result.Append("(.*/)?");
                    i += 2;
                }
                else
                {
                    result.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                result.Append("[^/]*");
            }
            else if (c == '?')
            {
                result.Append("[^/]");
            }
            else
            {
                result.Append(Regex.Escape(c.ToString()));
            }
        }

        return result.ToString();
    }
}
