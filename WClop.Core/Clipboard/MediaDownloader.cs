using System.Net.Http.Headers;
using WClop.Core.Images;
using WClop.Core.Media;

namespace WClop.Core.Clipboard;

/// <summary>Downloads media from a copied URL into the working directory's downloads folder (project.md §3.8).</summary>
public sealed class MediaDownloader(AppPaths paths, HttpClient? httpClient = null)
{
    public const long MaxBytes = 200L * 1024 * 1024;

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WClop", AppPaths.Version));
        return client;
    });

    private HttpClient Client => httpClient ?? SharedClient.Value;

    /// <summary>
    /// Downloads <paramref name="url"/> and returns the local path, named after the content's real format.
    /// Throws <see cref="UnsupportedFormatException"/> if it isn't media WClop can handle.
    /// </summary>
    public async Task<string> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxBytes)
            throw new UnsupportedFormatException("The download is too large");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new UnsupportedFormatException("The URL points to a web page, not an image or video");

        Directory.CreateDirectory(paths.Downloads);
        var temp = AppPaths.NewTempPath(paths.Downloads, ".download");
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                        throw new UnsupportedFormatException("The download is too large");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            var format = FileTypeSniffer.Sniff(FileTypeSniffer.ReadHeader(temp));
            if (format.Kind() == MediaKind.Unknown)
                throw new UnsupportedFormatException("The URL didn't return an image or video");

            // Keep the URL's name where there is one, so the result card and outputs are recognisable.
            var name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(url.AbsolutePath));
            var safeName = string.Concat((name.Length > 0 ? name : "download").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var final = Path.Combine(paths.Downloads, $"{safeName[..Math.Min(safeName.Length, 60)]}-{Path.GetFileNameWithoutExtension(temp)[..6]}{format.Extension()}");
            File.Move(temp, final);
            return final;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
