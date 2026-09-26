using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace WClop.Core.Updates;

/// <summary>A newer release and the installer to download for it.</summary>
public sealed record AvailableUpdate(string Version, string Tag, Uri ReleasePage, string Notes, Uri Installer, string InstallerName, long Size)
{
    /// <summary>SHA-256 of the installer from GitHub's asset digest, or null (then the .sha256 asset is used).</summary>
    public string? Sha256 { get; init; }

    public Uri? ChecksumFile { get; init; }
}

/// <summary>
/// Looks for a newer WClop in the GitHub releases and downloads its installer, verified against the release's
/// SHA-256. Only published releases count (the "latest" endpoint skips drafts and pre-releases).
/// </summary>
public sealed class UpdateChecker(HttpClient? httpClient = null, string repository = UpdateChecker.DefaultRepository)
{
    public const string DefaultRepository = "ogshrug/WClop";

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WClop", AppPaths.Version));
        return client;
    });

    private HttpClient Client => httpClient ?? SharedClient.Value;

    /// <summary>The newest release if it's newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // no releases yet
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), currentVersion);
    }

    /// <summary>Reads a GitHub "latest release" response. Null if it isn't newer or has no installer.</summary>
    public static AvailableUpdate? Parse(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, currentVersion))
            return null;

        var assets = root.TryGetProperty("assets", out var list) ? list.EnumerateArray().ToList() : [];
        JsonElement? Asset(string name) =>
            assets.FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) is
                { ValueKind: JsonValueKind.Object } found ? found : null;

        var installer = Asset($"WClop-{version}-x64.msi") ?? Asset("WClop-x64.msi");
        if (installer is not { } msi)
            return null;

        var name = msi.GetProperty("name").GetString()!;
        var digest = msi.TryGetProperty("digest", out var d) && d.GetString() is { } value && value.StartsWith("sha256:", StringComparison.Ordinal)
            ? value["sha256:".Length..]
            : null;
        var checksum = Asset($"WClop-{version}-x64.msi.sha256");

        return new AvailableUpdate(
            version,
            tag,
            new Uri(root.GetProperty("html_url").GetString()!),
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            new Uri(msi.GetProperty("browser_download_url").GetString()!),
            name,
            msi.TryGetProperty("size", out var size) ? size.GetInt64() : 0)
        {
            Sha256 = digest,
            ChecksumFile = checksum is { } c ? new Uri(c.GetProperty("browser_download_url").GetString()!) : null,
        };
    }

    /// <summary>Whether <paramref name="candidate"/> (like "0.13.0") is a later version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(Normalise(candidate), out var a) && System.Version.TryParse(Normalise(current), out var b) && a > b;

    private static string Normalise(string version)
    {
        var core = version.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return core.Count(c => c == '.') switch
        {
            0 => core + ".0.0",
            1 => core + ".0",
            _ => core,
        };
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and checks its SHA-256; a file that doesn't match is
    /// deleted and an <see cref="InvalidDataException"/> thrown. Returns the installer's path.
    /// </summary>
    public async Task<string> DownloadAsync(
        AvailableUpdate update, string folder, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var expected = update.Sha256;
        if (expected is null && update.ChecksumFile is { } checksumUrl)
        {
            var line = await Client.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
            expected = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        if (expected is not { Length: 64 })
            throw new InvalidDataException("The release has no checksum for its installer, so it can't be verified");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"WClop-{update.Version}-x64.msi");
        var temp = path + ".part";
        using (var response = await Client.GetAsync(update.Installer, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(temp);
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                if (total > 0)
                    onProgress?.Invoke((double)written / total);
            }
        }

        string actual;
        await using (var stream = File.OpenRead(temp))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException("The downloaded installer doesn't match the release's checksum");
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }
}
