using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WClop.Core.Placement;

/// <summary>
/// Output name templates (project.md §13.2). The result has no extension; the caller appends the output's.
/// <list type="table">
/// <item><term>%f</term><description>filename without extension</description></item>
/// <item><term>%e</term><description>extension without the dot</description></item>
/// <item><term>%P</term><description>parent folder</description></item>
/// <item><term>%F</term><description>full path without extension</description></item>
/// <item><term>%y %m %n %d %w</term><description>year, month number, month name, day, weekday</description></item>
/// <item><term>%H %M %S %p</term><description>hour (24h), minutes, seconds, AM/PM</description></item>
/// <item><term>%r</term><description>5 random characters</description></item>
/// <item><term>%i</term><description>auto-incrementing number</description></item>
/// <item><term>%%</term><description>a literal %</description></item>
/// </list>
/// Environment variables such as <c>%USERPROFILE%</c> are expanded first. A relative result is
/// resolved against the source file's folder, and <c>/</c> may be used as the separator.
/// </summary>
public static class NameTemplate
{
    private const string TokenChars = "fePFymndwHMSpri%";

    public static string Render(string template, string sourcePath, DateTime now, Func<int>? nextCounter = null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var expanded = Environment.ExpandEnvironmentVariables(template);

        var result = new StringBuilder();
        for (var i = 0; i < expanded.Length; i++)
        {
            var c = expanded[i];
            if (c != '%' || i + 1 >= expanded.Length || !TokenChars.Contains(expanded[i + 1]))
            {
                result.Append(c);
                continue;
            }

            i++;
            result.Append(expanded[i] switch
            {
                'f' => stem,
                'e' => Path.GetExtension(sourcePath).TrimStart('.'),
                'P' => parent,
                'F' => Path.Combine(parent, stem),
                'y' => now.ToString("yyyy", CultureInfo.InvariantCulture),
                'm' => now.ToString("MM", CultureInfo.InvariantCulture),
                'n' => now.ToString("MMMM", CultureInfo.InvariantCulture),
                'd' => now.ToString("dd", CultureInfo.InvariantCulture),
                'w' => now.ToString("dddd", CultureInfo.InvariantCulture),
                'H' => now.ToString("HH", CultureInfo.InvariantCulture),
                'M' => now.ToString("mm", CultureInfo.InvariantCulture),
                'S' => now.ToString("ss", CultureInfo.InvariantCulture),
                'p' => now.ToString("tt", CultureInfo.InvariantCulture),
                'r' => RandomChars(5),
                'i' => (nextCounter?.Invoke() ?? 1).ToString(CultureInfo.InvariantCulture),
                _ => "%",
            });
        }

        var rendered = result.ToString().Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(rendered) ? rendered : Path.Combine(parent, rendered));
    }

    /// <summary>
    /// True if <paramref name="sourcePath"/>'s name already looks like the output of <paramref name="template"/>'s
    /// filename part, so the template isn't applied twice (<c>photo-optimised-optimised</c>).
    /// Only meaningful for templates that add something around <c>%f</c>.
    /// </summary>
    public static bool IsAlreadyTemplated(string sourcePath, string template)
    {
        var namePart = template.Replace('\\', '/').Split('/')[^1];
        if (!namePart.Contains("%f") || namePart == "%f")
            return false;

        var pattern = new StringBuilder("^");
        for (var i = 0; i < namePart.Length; i++)
        {
            if (namePart[i] == '%' && i + 1 < namePart.Length && TokenChars.Contains(namePart[i + 1]))
            {
                i++;
                pattern.Append(namePart[i] switch
                {
                    'f' => ".+",
                    '%' => "%",
                    _ => ".+?",
                });
            }
            else
            {
                pattern.Append(Regex.Escape(namePart[i].ToString()));
            }
        }

        pattern.Append('$');
        return Regex.IsMatch(Path.GetFileNameWithoutExtension(sourcePath), pattern.ToString(), RegexOptions.IgnoreCase);
    }

    private static string RandomChars(int count)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(count, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = chars[Random.Shared.Next(chars.Length)];
        });
    }
}
