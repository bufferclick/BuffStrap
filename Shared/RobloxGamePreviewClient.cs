using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BuffStrap.Favorites;

/// <summary>Public game information only; never contains an account session.</summary>
public sealed class RobloxGamePreview
{
    public long PlaceId { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Creator { get; init; } = "";
    public bool DetailsAvailable { get; init; }
    public long? Playing { get; init; }
    public byte[]? IconPng { get; init; }
}

/// <summary>Fetches a game's public details and icon from fixed Roblox endpoints.</summary>
public sealed class RobloxGamePreviewClient
{
    private const int MaxJsonBytes = 512 * 1024;
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private readonly HttpClient _http;

    public RobloxGamePreviewClient(HttpClient http) => _http = http;

    public static HttpClient CreateHttpClient()
    {
        // Game previews are public; never attach the user's Roblox cookies or
        // follow a response to an untrusted host.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BuffStrap/1.0");
        return http;
    }

    public async Task<RobloxGamePreview?> GetAsync(long placeId, CancellationToken cancellationToken = default)
    {
        if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
        string place = placeId.ToString(CultureInfo.InvariantCulture);
        using var universeJson = await TryGetJsonAsync($"https://apis.roblox.com/universes/v1/places/{place}/universe", cancellationToken).ConfigureAwait(false);
        if (universeJson is null || universeJson.RootElement.ValueKind != JsonValueKind.Object ||
            !universeJson.RootElement.TryGetProperty("universeId", out var universeNode) || universeNode.ValueKind != JsonValueKind.Number ||
            !universeNode.TryGetInt64(out long universeId) || universeId <= 0) return null;

        var detailsTask = GetDetailsAsync(universeId, cancellationToken);
        var iconTask = GetIconAsync(universeId, cancellationToken);
        await Task.WhenAll(detailsTask, iconTask).ConfigureAwait(false);
        var details = await detailsTask.ConfigureAwait(false);
        byte[]? icon = await iconTask.ConfigureAwait(false);
        if (details is null && icon is null) return null;
        return new RobloxGamePreview
        {
            PlaceId = placeId,
            Name = details?.Name ?? "",
            Description = details?.Description ?? "",
            Creator = details?.Creator ?? "",
            DetailsAvailable = details is not null,
            Playing = details?.Playing,
            IconPng = icon
        };
    }

    private sealed record Details(string Name, string Description, string Creator, long? Playing);

    private async Task<Details?> GetDetailsAsync(long universeId, CancellationToken token)
    {
        string id = universeId.ToString(CultureInfo.InvariantCulture);
        using var json = await TryGetJsonAsync($"https://games.roblox.com/v1/games?universeIds={id}", token).ConfigureAwait(false);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var game in data.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.Number ||
                !idNode.TryGetInt64(out long found) || found != universeId) continue;
            string creator = "";
            if (game.TryGetProperty("creator", out var c) && c.ValueKind == JsonValueKind.Object)
                creator = Text(c, "name", 100);
            long? playing = game.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.Number &&
                p.TryGetInt64(out long n) && n >= 0 ? n : null;
            return new Details(Text(game, "name", 120), Text(game, "description", 600), creator, playing);
        }
        return null;
    }

    private async Task<byte[]?> GetIconAsync(long universeId, CancellationToken token)
    {
        string id = universeId.ToString(CultureInfo.InvariantCulture);
        using var json = await TryGetJsonAsync($"https://thumbnails.roblox.com/v1/games/icons?universeIds={id}&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false", token).ConfigureAwait(false);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("targetId", out var target) || target.ValueKind != JsonValueKind.Number ||
                !target.TryGetInt64(out long found) || found != universeId ||
                !item.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "Completed" ||
                !item.TryGetProperty("imageUrl", out var image) || image.ValueKind != JsonValueKind.String) continue;
            string? url = image.GetString();
            if (!BuffStrap.Accounts.RobloxAccountClient.IsRobloxImageUrl(url)) return null;
            byte[]? bytes = await TryGetBytesAsync(url!, MaxIconBytes, token).ConfigureAwait(false);
            return bytes is not null && bytes.Length >= PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
                ? bytes : null;
        }
        return null;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string url, CancellationToken token)
    {
        byte[]? bytes = await TryGetBytesAsync(url, MaxJsonBytes, token).ConfigureAwait(false);
        if (bytes is null) return null;
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { return null; }
    }

    private async Task<byte[]?> TryGetBytesAsync(string url, int limit, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is long length && length > limit) return null;
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
    }

    private static string Text(JsonElement element, string name, int limit)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return "";
        string value = property.GetString() ?? "";
        var clean = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!Char.IsControl(c)) clean.Append(c);
            else if (Char.IsWhiteSpace(c)) clean.Append(' ');
        }
        value = Regex.Replace(clean.ToString(), @"\s+", " ").Trim();
        if (value.Length <= limit) return value;
        int end = limit - 1; // Leave room for the ellipsis within the limit.
        if (Char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end].TrimEnd() + "…";
    }
}
