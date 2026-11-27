using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BuffStrap.Accounts;

// This is an unofficial web-to-player flow. Roblox can change or reject it.
// No password is accepted, and the session is sent only to fixed Roblox HTTPS
// endpoints. In particular, do not log request headers, response bodies,
// sessions, launch tickets or the resulting launch URI.
public sealed class AccountIdentity
{
    public long UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Session { get; init; } = "";
    public override string ToString() => Username;
}

public sealed class AccountTicket
{
    public string Value { get; init; } = "";
    public string Session { get; init; } = "";
    public override string ToString() => "[one-time Roblox ticket]";
}

/// <summary>A safe, user-facing failure that never includes a Roblox response body or credential.</summary>
public sealed class RobloxAccountException : Exception
{
    public RobloxAccountException(string message) : base(message) { }
}

public sealed class RobloxAccountClient
{
    private const string AuthenticatedUrl = "https://users.roblox.com/v1/users/authenticated";
    private const string AssertionUrl = "https://auth.roblox.com/v1/client-assertion/";
    private const string TicketUrl = "https://auth.roblox.com/v1/authentication-ticket/";
    private const int MaxJsonBytes = 256 * 1024;
    private const int MaxAvatarBytes = 2 * 1024 * 1024;
    private readonly HttpClient _client;

    public RobloxAccountClient(HttpClient client) => _client = client;

    public static HttpClient CreateHttpClient()
    {
        // A redirect must never forward the account cookie to another host.
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BuffStrap/1.0");
        return client;
    }

    public static bool IsValidSession(string? value) => !String.IsNullOrEmpty(value) && value.Length <= 16384 &&
        value.All(c => c > 32 && c < 127 && c is not (';' or ',' or '"' or '\\'));

    private static HttpRequestMessage Request(HttpMethod method, string url, string session)
    {
        if (!IsValidSession(session)) throw new RobloxAccountException("The saved Roblox session is invalid. Sign in again.");
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + session);
        request.Headers.Referrer = new Uri("https://www.roblox.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        return request;
    }

    private static string RotatedSession(HttpResponseMessage response, string current)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return current;
        foreach (string header in headers)
        {
            const string prefix = ".ROBLOSECURITY=";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string value = header[prefix.Length..].Split(';')[0];
            if (IsValidSession(value)) return value;
        }
        return current;
    }

    private static string ErrorFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Roblox did not accept this session. Sign in to the account again.",
        HttpStatusCode.Forbidden => "Roblox rejected the session or requested additional verification. Sign in again; account session protection may prevent this unofficial launcher from working.",
        (HttpStatusCode)429 => "Roblox is rate limiting requests. Wait a little and try again.",
        _ => $"Roblox could not complete the request (HTTP {(int)status}). Try again later."
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long size && size > limit)
            throw new InvalidDataException("Roblox returned an unexpectedly large response.");
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("Roblox returned an unexpectedly large response.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string CaptureRotation(HttpResponseMessage response, string current, Action<string>? saveRotation)
    {
        string rotated = RotatedSession(response, current);
        // Save as soon as a response rotates it, including on an unsuccessful response.
        // Otherwise a subsequent failure can leave only a stale session on disk.
        if (!String.Equals(rotated, current, StringComparison.Ordinal)) saveRotation?.Invoke(rotated);
        return rotated;
    }

    public async Task<AccountIdentity> GetIdentityAsync(string session, CancellationToken cancellationToken = default, Action<string>? saveRotation = null)
    {
        using var request = Request(HttpMethod.Get, AuthenticatedUrl, session);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        session = CaptureRotation(response, session, saveRotation);
        if (!response.IsSuccessStatusCode) throw new RobloxAccountException(ErrorFor(response.StatusCode));
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaxJsonBytes, cancellationToken).ConfigureAwait(false));
        var root = json.RootElement;
        long id = root.GetProperty("id").GetInt64();
        string name = root.GetProperty("name").GetString() ?? "";
        string display = root.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;
        if (id <= 0 || !BuffStrap.Favorites.FavoritesStore.IsValidUsername(name) || String.IsNullOrWhiteSpace(display) ||
            display.Length > 100 || display.Any(Char.IsControl) || display.Contains(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) ||
            display.Contains("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Roblox returned an invalid account profile.");
        return new AccountIdentity { UserId = id, Username = name, DisplayName = display, Session = session };
    }

    public async Task<AccountTicket> GetTicketAsync(string session, CancellationToken cancellationToken = default, Action<string>? saveRotation = null)
    {
        // A ticket without the current assertion may be issued but fail to join.
        // Fail explicitly instead of silently offering a launch that cannot work.
        string body;
        using (var request = Request(HttpMethod.Get, AssertionUrl, session))
        using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            session = CaptureRotation(response, session, saveRotation);
            if (!response.IsSuccessStatusCode) throw new RobloxAccountException(ErrorFor(response.StatusCode));
            byte[] bytes = await ReadBoundedAsync(response.Content, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("clientAssertion", out var assertion) ||
                assertion.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(assertion.GetString()))
                throw new RobloxAccountException("Roblox did not provide a client assertion. Try again later.");
            body = Encoding.UTF8.GetString(bytes);
        }

        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = Request(HttpMethod.Post, TicketUrl, session);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            session = CaptureRotation(response, session, saveRotation);
            if (response.IsSuccessStatusCode && response.Headers.TryGetValues("rbx-authentication-ticket", out var tickets))
            {
                string? ticket = tickets.FirstOrDefault();
                if (IsSafeHeader(ticket)) return new AccountTicket { Value = ticket!, Session = session };
                throw new InvalidDataException("Roblox returned an invalid launch ticket.");
            }
            if (attempt == 0 && response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("x-csrf-token", out var tokens) && IsSafeHeader(tokens.FirstOrDefault()))
            {
                csrf = tokens.First();
                continue;
            }
            if (response.IsSuccessStatusCode)
                throw new RobloxAccountException("Roblox did not provide a launch ticket. Try again later.");
            throw new RobloxAccountException(ErrorFor(response.StatusCode));
        }
        throw new RobloxAccountException("Roblox did not provide a launch ticket.");
    }

    private static bool IsSafeHeader(string? value) => !String.IsNullOrWhiteSpace(value) && value.Length <= 16384 &&
        value.All(c => c >= 33 && c <= 126);

    public static bool IsRobloxLoginUrl(string? value)
    {
        return value is not null && value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            (uri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRobloxImageUrl(string? value)
    {
        return value is not null && value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.Host.Equals("rbxcdn.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase));
    }

    // Avatars are public. Never attach a session cookie to thumbnail or CDN requests.
    public async Task<byte[]?> GetAvatarAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId.ToString(CultureInfo.InvariantCulture)}&size=150x150&format=Png&isCircular=true";
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaxJsonBytes, cancellationToken).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("targetId", out var id) || !id.TryGetInt64(out long target) || target != userId ||
                !item.TryGetProperty("state", out var state) || state.GetString() != "Completed" ||
                !item.TryGetProperty("imageUrl", out var image) || image.ValueKind != JsonValueKind.String) continue;
            string? imageUrl = image.GetString();
            if (!IsRobloxImageUrl(imageUrl)) return null;
            using var picture = await _client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!picture.IsSuccessStatusCode) return null;
            return await ReadBoundedAsync(picture.Content, MaxAvatarBytes, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Launch URI for the account's Home screen: the client still receives the
    /// one-time ticket, but no place is requested.
    /// </summary>
    public static string BuildHomeUri(string ticket, long trackerId, DateTimeOffset time)
    {
        if (!IsSafeHeader(ticket) || trackerId <= 0)
            throw new ArgumentException("Invalid Roblox launch information.");
        return "roblox-player:1+launchmode:app+gameinfo:" + Uri.EscapeDataString(ticket) +
            "+launchtime:" + time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            "+browsertrackerid:" + trackerId.ToString(CultureInfo.InvariantCulture) +
            "+robloxLocale:en_us+gameLocale:en_us";
    }

    public static string BuildPlayerUri(string ticket, long placeId, long trackerId, DateTimeOffset time, Guid attemptId)
    {
        if (!IsSafeHeader(ticket) || placeId <= 0 || trackerId <= 0 || attemptId == Guid.Empty)
            throw new ArgumentException("Invalid Roblox launch information.");
        string tracker = trackerId.ToString(CultureInfo.InvariantCulture);
        string place = placeId.ToString(CultureInfo.InvariantCulture);
        string launchUrl = "https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId=" + tracker +
            "&placeId=" + place + "&isPlayTogetherGame=false&joinAttemptId=" + attemptId.ToString("D") + "&joinAttemptOrigin=PlayButton";
        return "roblox-player:1+launchmode:play+gameinfo:" + Uri.EscapeDataString(ticket) +
            "+launchtime:" + time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            "+placelauncherurl:" + Uri.EscapeDataString(launchUrl) + "+browsertrackerid:" + tracker +
            "+robloxLocale:en_us+gameLocale:en_us+LaunchExp:InApp";
    }
}
