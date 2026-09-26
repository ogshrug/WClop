using System.Security.Cryptography;
using System.Text.Json;

namespace WClop.Core.Processes;

/// <summary>
/// SHA-256 of every bundled tool, written by <c>scripts/package.ps1</c> as <c>tools/tools-manifest.json</c>
/// (project.md §21.3). At startup WClop checks the tools it ships still match, so a damaged install or a swapped
/// binary is reported instead of run. Dev checkouts have no manifest and skip the check.
/// </summary>
public static class ToolManifest
{
    public const string FileName = "tools-manifest.json";

    public sealed record Entry(string Path, string Sha256);

    public static IReadOnlyList<Entry> Build(string toolsDir) =>
        Directory.EnumerateFiles(toolsDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new Entry(Path.GetRelativePath(toolsDir, f).Replace('\\', '/'), Hash(f)))
            .ToList();

    public static void Write(string toolsDir) =>
        File.WriteAllText(Path.Combine(toolsDir, FileName),
            JsonSerializer.Serialize(Build(toolsDir), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    /// <summary>
    /// Problems with the tools in <paramref name="toolsDir"/> ("missing: …", "changed: …"), or null if there's no
    /// manifest to check against.
    /// </summary>
    public static IReadOnlyList<string>? Verify(string toolsDir)
    {
        var manifestPath = Path.Combine(toolsDir, FileName);
        if (!File.Exists(manifestPath))
            return null;

        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifestPath),
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? [];
        var problems = new List<string>();
        foreach (var entry in entries)
        {
            var path = Path.Combine(toolsDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                problems.Add("missing: " + entry.Path);
            else if (!Hash(path).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add("changed: " + entry.Path);
        }

        return problems;
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
