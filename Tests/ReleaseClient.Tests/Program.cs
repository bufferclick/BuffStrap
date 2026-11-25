using BuffStrap.Release;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

// Dependency-free tests, runnable with `dotnet run --project Tests/ReleaseClient.Tests`.
var goodBytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
string hash = Convert.ToHexString(SHA256.HashData(goodBytes));
string root = Path.Combine(Path.GetTempPath(), "BuffStrap-release-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failures = 0;

string Manifest(string tag = "v1.7.2", string? url = null, long size = 6, string? digest = null,
    bool prerelease = false, bool duplicate = false, bool omitApp = false)
{
    url ??= $"https://github.com/{ReleaseClient.Repository}/releases/download/{tag}/BuffStrap.exe";
    var assets = new List<object>
    {
        // The setup file deliberately comes first: the updater must never choose it.
        new { name = "BuffStrapSetup.exe", browser_download_url = "https://example.com/setup", size = 123 }
    };
    if (!omitApp)
        assets.Add(new { name = "BuffStrap.exe", browser_download_url = url, size, digest });
    if (duplicate)
        assets.Add(new { name = "BuffStrap.exe", browser_download_url = url, size, digest });
    return JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease, assets });
}

async Task Test(string name, Func<Task> body)
{
    try { await body(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
void Reject(string json)
{
    try { ReleaseClient.ParseRelease(json); }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected an invalid release to be rejected.");
}
async Task RejectDownload(ReleaseInfo release, byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
{
    using var http = new HttpClient(new FakeHandler(bytes, status));
    try { await new ReleaseClient(http).DownloadAsync(release, root); }
    catch (Exception ex) when (ex is InvalidDataException || ex is HttpRequestException)
    {
        Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "A failed download left files behind.");
        return;
    }
    throw new Exception("Expected the download to fail.");
}

try
{
    await Test("selects the app by name, not asset order", () =>
    {
        var release = ReleaseClient.ParseRelease(Manifest(digest: "sha256:" + hash));
        Assert(release.TagName == "v1.7.2" && release.Version == new Version(1, 7, 2));
        Assert(release.DownloadUri.AbsolutePath.EndsWith("/BuffStrap.exe"));
        Assert(release.Sha256 == hash);
        return Task.CompletedTask;
    });
    await Test("compares patch releases without downgrading or reinstalling equal versions", () =>
    {
        var release = ReleaseClient.ParseRelease(Manifest());
        Assert(ReleaseClient.IsNewer(release, new Version(1, 7, 1)));
        Assert(!ReleaseClient.IsNewer(release, new Version(1, 7, 2)));
        Assert(!ReleaseClient.IsNewer(release, new Version(1, 8, 0)));
        Assert(ReleaseClient.MatchesVersion("1.7.2.0", release.Version));
        Assert(!ReleaseClient.MatchesVersion("1.7.3.0", release.Version));
        return Task.CompletedTask;
    });
    await Test("rejects malformed versions, prereleases, and missing or duplicate app assets", () =>
    {
        foreach (string tag in new[] { "v1", "1.0.0", "v1.0", "v1.0.0-beta", "v01.0.0", "v1.0.0.1" })
            Reject(Manifest(tag: tag));
        Reject(Manifest(prerelease: true));
        Reject(Manifest(omitApp: true));
        Reject(Manifest(duplicate: true));
        Reject(Manifest(size: 0));
        Reject(Manifest(digest: "sha256:not-a-hash"));
        return Task.CompletedTask;
    });
    await Test("rejects unofficial asset URLs", () =>
    {
        Reject(Manifest(url: "https://evil.example/BuffStrap.exe"));
        Reject(Manifest(url: "http://github.com/bufferclick/BuffStrap/releases/download/v1.7.2/BuffStrap.exe"));
        Reject(Manifest(url: "https://github.com/other/repo/releases/download/v1.7.2/BuffStrap.exe"));
        Reject(Manifest(url: "https://github.com/bufferclick/BuffStrap/releases/download/v1.7.2/BuffStrap.exe?redirect=evil"));
        return Task.CompletedTask;
    });
    await Test("downloads and verifies the exact asset in a fresh directory", async () =>
    {
        var release = ReleaseClient.ParseRelease(Manifest(digest: "sha256:" + hash));
        using var http = new HttpClient(new FakeHandler(goodBytes));
        var client = new ReleaseClient(http);
        string first = await client.DownloadAsync(release, root);
        string second = await client.DownloadAsync(release, root);
        Assert(first != second && File.ReadAllBytes(first).SequenceEqual(goodBytes));
        Directory.Delete(Path.GetDirectoryName(first)!, true);
        Directory.Delete(Path.GetDirectoryName(second)!, true);
    });
    await Test("rejects HTTP errors, incomplete files, bad hashes and non-executables", async () =>
    {
        await RejectDownload(ReleaseClient.ParseRelease(Manifest()), goodBytes, HttpStatusCode.NotFound);
        await RejectDownload(ReleaseClient.ParseRelease(Manifest(size: 7)), goodBytes);
        await RejectDownload(ReleaseClient.ParseRelease(Manifest(digest: "sha256:" + new string('0', 64))), goodBytes);
        await RejectDownload(ReleaseClient.ParseRelease(Manifest()), new byte[] { 0, 0, 1, 2, 3, 4 });
    });
    await Test("cancellation removes the staging directory", async () =>
    {
        using var http = new HttpClient(new FakeHandler(goodBytes));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await new ReleaseClient(http).DownloadAsync(ReleaseClient.ParseRelease(Manifest()), root, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException)
        {
            Assert(!Directory.EnumerateFileSystemEntries(root).Any());
            return;
        }
        throw new Exception("Expected cancellation.");
    });
}
finally
{
    Directory.Delete(root, true);
}

return failures == 0 ? 0 : 1;

sealed class FakeHandler : HttpMessageHandler
{
    private readonly byte[] _bytes;
    private readonly HttpStatusCode _status;
    public FakeHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) { _bytes = bytes; _status = status; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(_bytes) });
    }
}
