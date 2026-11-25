using BuffStrap.Release;
using BuffStrap.Favorites;
using BuffStrap.Backup;
using BuffStrap.Accounts;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

// Dependency-free tests, runnable with `dotnet run --project Tests/ReleaseClient.Tests`.
var goodBytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
string hash = Convert.ToHexString(SHA256.HashData(goodBytes));
string root = Path.Combine(Path.GetTempPath(), "BuffStrap-release-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failures = 0;

string Manifest(string tag = "v1.7.2", string? url = null, long size = 6, string? digest = null,
    bool prerelease = false, bool duplicate = false, bool omitApp = false, string? notes = null)
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
    return JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease, body = notes, assets });
}

async Task Test(string name, Func<Task> body)
{
    try { await body(); Console.WriteLine("PASS " + name); }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex}");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            string details = ($"{name}: {ex}").Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
            Console.WriteLine("::error title=Automated test failed::" + details);
        }
    }
}
void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
HttpResponseMessage JsonResponse(HttpStatusCode status, string json, string? rotatedSession = null)
{
    var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    if (rotatedSession is not null)
        response.Headers.TryAddWithoutValidation("Set-Cookie", ".ROBLOSECURITY=" + rotatedSession + "; Path=/; Secure; HttpOnly");
    return response;
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
    await Test("lists only validated stable releases in version order with notes", async () =>
    {
        string list = "[" + String.Join(",", new[] {
            Manifest(tag: "v1.0.0", notes: "First release"),
            Manifest(tag: "v1.8.0", notes: "Changes"),
            Manifest(tag: "v1.7.2", prerelease: true),
            Manifest(tag: "v1.7.2", url: "https://evil.example/BuffStrap.exe"),
            Manifest(tag: "v1.0.0"), "null" }) + "]";
        using var http = new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(list)));
        var releases = await new ReleaseClient(http).GetStableReleasesAsync();
        Assert(releases.Count == 2 && releases[0].TagName == "v1.8.0" && releases[1].TagName == "v1.0.0");
        Assert(releases[0].Notes == "Changes");
    });
    await Test("assigned favorites preserve their account and never silently use another session", () =>
    {
        var account = new FavoriteAccount { Username = "Example_User" };
        var assigned = new FavoriteGame { Name = "Assigned", PlaceId = 123, AccountId = account.Id };
        var unassigned = new FavoriteGame { Name = "Current", PlaceId = 456 };
        var data = new FavoritesData { Accounts = new() { account }, Games = new() { assigned, unassigned } };
        var first = FavoriteLaunchPlan.Resolve(data, assigned.Id);
        var second = FavoriteLaunchPlan.Resolve(data, unassigned.Id);
        Assert(first.RequiresAccount && first.AccountId == account.Id && first.AssignedUsername == "Example_User" && first.PlaceId == 123);
        Assert(!second.RequiresAccount && second.AccountId is null && second.PlayerUrl == "roblox://experiences/start?placeId=456");
        // A v1.0.1 username-only entry is still a disconnected profile, never a login.
        string oldJson = $"{{\"SchemaVersion\":1,\"Accounts\":[{{\"Id\":\"{account.Id}\",\"Username\":\"Example_User\"}}],\"Games\":[{{\"Id\":\"{assigned.Id}\",\"Name\":\"Assigned\",\"Folder\":\"\",\"PlaceId\":123,\"AccountId\":\"{account.Id}\"}}],\"Recent\":[]}}";
        var migrated = FavoritesStore.Parse(oldJson);
        Assert(migrated.Accounts.Single().UserId is null && FavoriteLaunchPlan.Resolve(migrated, assigned.Id).AccountId == account.Id);
        data.Accounts.Clear();
        try { FavoriteLaunchPlan.Resolve(data, assigned.Id); throw new Exception("A missing account silently fell back to the current session."); }
        catch (InvalidDataException) { }
        return Task.CompletedTask;
    });
    await Test("Roblox identity, rotation, assertion, CSRF and one-time ticket use the selected session", async () =>
    {
        int step = 0;
        var rotations = new List<string>();
        using var http = new HttpClient(new LambdaHandler(async request =>
        {
            step++;
            Assert(request.RequestUri!.Scheme == "https");
            string cookie = request.Headers.GetValues("Cookie").Single();
            HttpResponseMessage response;
            if (step == 1)
            {
                Assert(request.Method == HttpMethod.Get && request.RequestUri.Host == "users.roblox.com" && cookie == ".ROBLOSECURITY=session-old");
                response = JsonResponse(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Example_User\",\"displayName\":\"Example Display\"}", "session-a");
            }
            else if (step == 2)
            {
                Assert(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://auth.roblox.com/v1/client-assertion/" && cookie == ".ROBLOSECURITY=session-a");
                response = JsonResponse(HttpStatusCode.OK, "{\"clientAssertion\":\"example-assertion\"}", "session-b");
            }
            else if (step == 3)
            {
                Assert(request.Method == HttpMethod.Post && cookie == ".ROBLOSECURITY=session-b" && !request.Headers.Contains("X-CSRF-TOKEN"));
                Assert((await request.Content!.ReadAsStringAsync()).Contains("example-assertion"));
                response = JsonResponse(HttpStatusCode.Forbidden, "{}", "session-c");
                response.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-example");
            }
            else if (step == 4)
            {
                Assert(request.Method == HttpMethod.Post && cookie == ".ROBLOSECURITY=session-c" && request.Headers.GetValues("X-CSRF-TOKEN").Single() == "csrf-example");
                Assert((await request.Content!.ReadAsStringAsync()).Contains("example-assertion"));
                response = JsonResponse(HttpStatusCode.OK, "{}", "session-d");
                response.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "ticket+/=example");
            }
            else throw new Exception("Unexpected Roblox request.");
            return response;
        }));
        var api = new RobloxAccountClient(http);
        var identity = await api.GetIdentityAsync("session-old", saveRotation: rotations.Add);
        Assert(identity.UserId == 42 && identity.Username == "Example_User" && identity.DisplayName == "Example Display" && identity.Session == "session-a");
        var ticket = await api.GetTicketAsync(identity.Session, saveRotation: rotations.Add);
        Assert(ticket.Value == "ticket+/=example" && ticket.Session == "session-d" && step == 4);
        Assert(rotations.SequenceEqual(new[] { "session-a", "session-b", "session-c", "session-d" }));
        var attempt = Guid.Parse("11111111-2222-4333-8444-555555555555");
        string uri = RobloxAccountClient.BuildPlayerUri(ticket.Value, 1818, 1234567890, DateTimeOffset.FromUnixTimeMilliseconds(1792839000000), attempt);
        Assert(uri.Contains("gameinfo:ticket%2B%2F%3Dexample+launchtime:1792839000000"));
        Assert(uri.Contains("placelauncherurl:https%3A%2F%2Fwww.roblox.com%2FGame%2FPlaceLauncher.ashx%3Frequest%3DRequestGame%26browserTrackerId%3D1234567890%26placeId%3D1818"));
        Assert(uri.EndsWith("+browsertrackerid:1234567890+robloxLocale:en_us+gameLocale:en_us+LaunchExp:InApp"));
        Assert(!uri.Contains("session-old") && !uri.Contains("session-d"));
    });
    await Test("Roblox rejection and missing assertions fail closed without fallback", async () =>
    {
        int calls = 0;
        var rotations = new List<string>();
        using (var http = new HttpClient(new LambdaHandler(request =>
        {
            calls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.Forbidden, "{\"private\":\"do-not-display\"}", "session-rotated"));
        })))
        {
            try { await new RobloxAccountClient(http).GetTicketAsync("session-old", saveRotation: rotations.Add); throw new Exception("Rejected session was accepted."); }
            catch (RobloxAccountException ex) { Assert(!ex.Message.Contains("do-not-display") && ex.Message.Contains("Roblox")); }
        }
        Assert(calls == 1 && rotations.SequenceEqual(new[] { "session-rotated" }));
        calls = 0;
        using (var http = new HttpClient(new LambdaHandler(request =>
        {
            calls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        })))
        {
            try { await new RobloxAccountClient(http).GetTicketAsync("session-old"); throw new Exception("A missing assertion was accepted."); }
            catch (RobloxAccountException) { }
        }
        Assert(calls == 1, "A ticket was requested after assertion failure.");
        Assert(!RobloxAccountClient.IsValidSession("bad;cookie") && !RobloxAccountClient.IsValidSession("bad\r\ncookie") && RobloxAccountClient.IsValidSession("good-session"));
    });
    await Test("login and avatar hosts are strictly validated and avatars never receive cookies", async () =>
    {
        Assert(RobloxAccountClient.IsRobloxLoginUrl("https://www.roblox.com/login"));
        foreach (string url in new[] { "http://roblox.com/login", "https://roblox.com.evil.test/", "https://roblox.com@evil.test/", "https://www.roblox.com:444/login", "file:///login" })
            Assert(!RobloxAccountClient.IsRobloxLoginUrl(url));
        Assert(RobloxAccountClient.IsRobloxImageUrl("https://tr.rbxcdn.com/avatar.png"));
        Assert(!RobloxAccountClient.IsRobloxImageUrl("https://rbxcdn.com.evil.test/avatar.png"));
        int calls = 0;
        using var http = new HttpClient(new LambdaHandler(request =>
        {
            calls++;
            Assert(!request.Headers.Contains("Cookie"));
            if (calls == 1) return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"targetId\":42,\"state\":\"Completed\",\"imageUrl\":\"https://tr.rbxcdn.com/avatar.png\"}]}"));
            Assert(request.RequestUri!.Host == "tr.rbxcdn.com");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        }));
        Assert((await new RobloxAccountClient(http).GetAvatarAsync(42))!.SequenceEqual(new byte[] { 1, 2, 3 }) && calls == 2);
    });
    await Test("favorites preserve concurrent edits and track only the newest 50 joins", async () =>
    {
        string dir = Path.Combine(root, "favorites");
        Directory.CreateDirectory(dir);
        var store = new FavoritesStore(Path.Combine(dir, "Favorites.json"));
        var account = new FavoriteAccount { Username = "Example_User" };
        store.Update(data => data.Accounts.Add(account));
        await Task.WhenAll(Enumerable.Range(1, 25).Select(id => Task.Run(() =>
            store.Update(data => data.Games.Add(new FavoriteGame { PlaceId = id, Name = "Game " + id, Folder = "Folder", AccountId = account.Id })))));
        Assert(store.Load().Games.Count == 25, "A concurrent favorite was lost.");
        for (int i = 1; i <= 55; i++) store.RecordJoin(i, DateTimeOffset.UtcNow.AddSeconds(i));
        var data = store.Load();
        Assert(data.Recent.Count == FavoritesStore.MaxRecent && data.Recent[0].PlaceId == 55);
        Assert(!data.Recent.Any(x => x.PlaceId == 1));
        store.RecordJoin(10, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert(store.Load().Recent.Count(x => x.PlaceId == 10) == 1);
        Assert(FavoritesStore.IsValidUsername("Example_User") && !FavoritesStore.IsValidUsername("bad name"));
        try { store.Update(x => x.Accounts.Add(new FavoriteAccount { Username = "example_user" })); throw new Exception("Duplicate account accepted."); }
        catch (InvalidDataException) { }
        Assert(store.Load().Accounts.Count == 1);
        File.WriteAllText(Path.Combine(dir, "Favorites.json"), "not json");
        try { store.Update(x => x.Games.Clear()); throw new Exception("Corrupt favorites were overwritten."); }
        catch (JsonException) { }
        Assert(File.ReadAllText(Path.Combine(dir, "Favorites.json")) == "not json");
    });
    await Test("backup restores settings, flags, mods and favorites without session files", () =>
    {
        string dir = Path.Combine(root, "backup-case", "install");
        Directory.CreateDirectory(Path.Combine(dir, "Modifications", "ClientSettings"));
        Directory.CreateDirectory(Path.Combine(dir, "LocalStorage"));
        Directory.CreateDirectory(Path.Combine(dir, "CustomThemes"));
        Directory.CreateDirectory(Path.Combine(dir, "AccountSessions"));
        Directory.CreateDirectory(Path.Combine(dir, "AccountLogin", "profile"));
        // Treat even an unexpectedly plaintext file as secret: the directory is never traversed.
        File.WriteAllText(Path.Combine(dir, "AccountSessions", "sample.session"), "never-export-account-session");
        File.WriteAllText(Path.Combine(dir, "AccountLogin", "profile", "browser-data"), "never-export-browser-profile");
        File.WriteAllText(Path.Combine(dir, "Settings.json"), "{\"CheckForUpdates\":false,\"AllowCookieAccess\":true,\".ROBLOSECURITY\":\"never-export\",\"nested\":{\"password\":\"secret\",\"args\":\".ROBLOSECURITY=never-export\"}}");
        File.WriteAllText(Path.Combine(dir, "LocalStorage", "RobloxCookies.dat"), "never-export");
        File.WriteAllText(Path.Combine(dir, "Modifications", "cookies.txt"), "never-export");
        File.WriteAllText(Path.Combine(dir, "Modifications", "script.lua"), new string('a', 65530) + "_|WARNING:-DO-NOT-SHARE-THIS secret");
        File.WriteAllText(Path.Combine(dir, "Modifications", "ClientSettings", "ClientAppSettings.json"), "{\"FFlagTest\":\"True\"}");
        File.WriteAllText(Path.Combine(dir, "Modifications", "mod.txt"), "original mod");
        File.WriteAllText(Path.Combine(dir, "CustomThemes", "theme.txt"), "launcher theme");
        var store = new FavoritesStore(Path.Combine(dir, "Favorites.json"));
        var account = new FavoriteAccount { Username = "Example_User", UserId = 42, DisplayName = "Example Display" };
        store.Update(data => { data.Accounts.Add(account); data.Games.Add(new FavoriteGame { Name = "Game", PlaceId = 123, AccountId = account.Id }); });
        string backup = Path.Combine(root, "backup-case", "data.zip");
        var created = BackupService.Create(dir, backup);
        Assert(created.Files >= 5 && created.Skipped.Any(x => x.EndsWith("cookies.txt")) && created.Skipped.Any(x => x.EndsWith("script.lua")));
        using (var zip = ZipFile.OpenRead(backup))
        {
            Assert(!zip.Entries.Any(x => x.FullName.Contains("cookie", StringComparison.OrdinalIgnoreCase)) && zip.GetEntry("Modifications/script.lua") is null);
            Assert(!zip.Entries.Any(x => x.FullName.StartsWith("AccountSessions/", StringComparison.OrdinalIgnoreCase) ||
                x.FullName.StartsWith("AccountLogin/", StringComparison.OrdinalIgnoreCase)), "Account secrets or browser profiles were exported.");
            using var reader = new StreamReader(zip.GetEntry("Settings.json")!.Open());
            string contents = reader.ReadToEnd();
            Assert(!contents.Contains("never-export") && !contents.Contains("secret") && contents.Contains("AllowCookieAccess"));
        }
        File.WriteAllText(Path.Combine(dir, "Settings.json"), "{\"CheckForUpdates\":true}");
        File.WriteAllText(Path.Combine(dir, "Modifications", "mod.txt"), "changed mod");
        File.WriteAllText(Path.Combine(dir, "Modifications", "extra.txt"), "keep extra");
        store.Update(data => data.Games.Clear());
        BackupService.Restore(dir, backup);
        Assert(File.ReadAllText(Path.Combine(dir, "Settings.json")).Contains("false"));
        Assert(File.ReadAllText(Path.Combine(dir, "Modifications", "mod.txt")) == "original mod");
        Assert(File.ReadAllText(Path.Combine(dir, "Modifications", "extra.txt")) == "keep extra");
        Assert(store.Load().Games.Single().AccountId == account.Id && store.Load().Accounts.Single().Username == "Example_User" &&
            store.Load().Accounts.Single().UserId == 42 && store.Load().Accounts.Single().DisplayName == "Example Display");
        Assert(File.ReadAllText(Path.Combine(dir, "LocalStorage", "RobloxCookies.dat")) == "never-export");
        string restored = Path.Combine(root, "backup-case", "another-install");
        Directory.CreateDirectory(restored);
        BackupService.Restore(restored, backup);
        Assert(!Directory.Exists(Path.Combine(restored, "AccountSessions")) && !Directory.Exists(Path.Combine(restored, "AccountLogin")));
        Assert(new FavoritesStore(Path.Combine(restored, "Favorites.json")).Load().Games.Single().AccountId == account.Id);
        return Task.CompletedTask;
    });
    await Test("restore rejects traversal and tampering before changing installed files", () =>
    {
        string dir = Path.Combine(root, "backup-case", "install");
        string settings = Path.Combine(dir, "Settings.json");
        string original = File.ReadAllText(settings);
        string malicious = Path.Combine(root, "backup-case", "malicious.zip");
        using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write("{}");
            using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("bad");
        }
        try { BackupService.Restore(dir, malicious); throw new Exception("Traversal was accepted."); }
        catch (InvalidDataException) { }
        Assert(File.ReadAllText(settings) == original && !File.Exists(Path.Combine(root, "backup-case", "escaped.txt")));
        string tampered = Path.Combine(root, "backup-case", "tampered.zip");
        File.Copy(Path.Combine(root, "backup-case", "data.zip"), tampered);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            zip.GetEntry("Modifications/mod.txt")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Modifications/mod.txt").Open());
            writer.Write("tampered mod");
        }
        try { BackupService.Restore(dir, tampered); throw new Exception("Tampered backup was accepted."); }
        catch (InvalidDataException) { }
        Assert(File.ReadAllText(settings) == original);
        return Task.CompletedTask;
    });
    await Test("failed restore rolls back earlier overwritten files", () =>
    {
        string dir = Path.Combine(root, "backup-case", "install");
        string settings = Path.Combine(dir, "Settings.json");
        string favorites = Path.Combine(dir, "Favorites.json");
        string mod = Path.Combine(dir, "Modifications", "mod.txt");
        File.WriteAllText(settings, "{\"CheckForUpdates\":true,\"Marker\":\"before\"}");
        string originalSettings = File.ReadAllText(settings);
        string originalFavorites = File.ReadAllText(favorites);
        using (new FileStream(mod, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { BackupService.Restore(dir, Path.Combine(root, "backup-case", "data.zip")); throw new Exception("Locked file did not fail restore."); }
            catch (IOException) { }
        }
        Assert(File.ReadAllText(settings) == originalSettings && File.ReadAllText(favorites) == originalFavorites,
            "An earlier overwritten file was not restored after a failed write.");
        return Task.CompletedTask;
    });
}
finally
{
    Directory.Delete(root, true);
}

return failures == 0 ? 0 : 1;

sealed class LambdaHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _send;
    public LambdaHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = send;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _send(request);
    }
}

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
