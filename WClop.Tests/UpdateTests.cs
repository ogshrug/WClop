using System.Net;
using System.Security.Cryptography;
using System.Text;
using WClop.Core.Updates;

namespace WClop.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Release(string tag, bool versioned = true, string? digest = null, bool fixedName = true) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/ogshrug/WClop/releases/tag/{{tag}}",
          "body": "Fixes",
          "assets": [
            {{(versioned ? $$"""{ "name": "WClop-{{tag.TrimStart('v')}}-x64.msi", "size": 10, "browser_download_url": "https://example.test/versioned.msi"{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} },""" : "")}}
            {{(fixedName ? """{ "name": "WClop-x64.msi", "size": 10, "browser_download_url": "https://example.test/fixed.msi" },""" : "")}}
            { "name": "WClop-{{tag.TrimStart('v')}}-x64.msi.sha256", "size": 90, "browser_download_url": "https://example.test/sum" }
          ]
        }
        """;

    [Theory]
    [InlineData("0.13.0", "0.12.1", true)]
    [InlineData("0.12.10", "0.12.9", true)]
    [InlineData("1.0", "0.99.99", true)]
    [InlineData("v0.12.1", "0.12.1", false)]
    [InlineData("0.12.0", "0.12.1", false)]
    [InlineData("0.13.0-beta", "0.12.0", true)]
    [InlineData("nonsense", "0.12.0", false)]
    public void ComparesVersions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void ReadsTheLatestRelease()
    {
        var update = UpdateChecker.Parse(Release("v0.13.0", digest: new string('a', 64)), "0.12.1");
        Assert.NotNull(update);
        Assert.Equal("0.13.0", update.Version);
        Assert.Equal("https://example.test/versioned.msi", update.Installer.AbsoluteUri);
        Assert.Equal(new string('a', 64), update.Sha256);
        Assert.Equal("https://example.test/sum", update.ChecksumFile!.AbsoluteUri);
        Assert.Equal("https://github.com/ogshrug/WClop/releases/tag/v0.13.0", update.ReleasePage.AbsoluteUri);
    }

    [Fact]
    public void FallsBackToTheFixedNameInstaller() =>
        Assert.Equal("https://example.test/fixed.msi", UpdateChecker.Parse(Release("v0.13.0", versioned: false), "0.12.1")!.Installer.AbsoluteUri);

    [Fact]
    public void NothingWhenUpToDateOrNoInstaller()
    {
        Assert.Null(UpdateChecker.Parse(Release("v0.12.1"), "0.12.1"));
        Assert.Null(UpdateChecker.Parse(Release("v0.13.0", versioned: false, fixedName: false), "0.12.1"));
    }

    [Fact]
    public async Task DownloadsAndVerifiesAgainstTheChecksumFile()
    {
        var installer = Encoding.ASCII.GetBytes("pretend msi");
        var hash = Convert.ToHexStringLower(SHA256.HashData(installer));
        var client = new HttpClient(new FakeHandler(new()
        {
            ["https://example.test/versioned.msi"] = installer,
            ["https://example.test/sum"] = Encoding.ASCII.GetBytes($"{hash}  WClop-0.13.0-x64.msi\n"),
        }));
        var update = UpdateChecker.Parse(Release("v0.13.0"), "0.12.1")!;

        var path = await new UpdateChecker(client).DownloadAsync(update, _dir.Path);

        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.EndsWith("WClop-0.13.0-x64.msi", path);
    }

    [Fact]
    public async Task RejectsATamperedDownload()
    {
        var client = new HttpClient(new FakeHandler(new()
        {
            ["https://example.test/versioned.msi"] = Encoding.ASCII.GetBytes("tampered"),
        }));
        var update = UpdateChecker.Parse(Release("v0.13.0", digest: new string('b', 64)), "0.12.1")!;

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(client).DownloadAsync(update, _dir.Path));
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task NoReleasesYetMeansNoUpdate()
    {
        var client = new HttpClient(new FakeHandler([]));
        Assert.Null(await new UpdateChecker(client, "someone/nothing").CheckAsync("0.12.0"));
    }

    private sealed class FakeHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
