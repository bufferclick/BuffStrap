using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BuffStrap.Release;

/// <summary>
/// The setup program and the installed app use the same release and download rules.
/// Only stable, three-part versions and the specifically named app asset are accepted.
/// </summary>
public sealed class ReleaseClient
{
    public const string Repository = "bufferclick/BuffStrap";
    public const string ExecutableName = "BuffStrap.exe";
    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases";
    private const long MaximumDownloadSize = 512L * 1024 * 1024;
    private static readonly Regex VersionTag = new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;

    public ReleaseClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseRelease(json);
    }

    /// <summary>Lists up to 300 recent releases, ignoring drafts, previews and releases
    /// without a valid official app asset. The same validation applies to rollback downloads.</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetStableReleasesAsync(CancellationToken cancellationToken = default)
    {
        var releases = new List<ReleaseInfo>();
        for (int page = 1; page <= 3; page++)
        {
            using var response = await _httpClient.GetAsync($"{ReleasesUrl}?per_page=100&page={page}", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The release list was not an array.");

            foreach (var item in document.RootElement.EnumerateArray())
            {
                try { releases.Add(ParseRelease(item.GetRawText())); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { }
            }
            if (document.RootElement.GetArrayLength() < 100)
                break;
        }
        return releases.GroupBy(x => x.TagName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).OrderByDescending(x => x.Version).ToArray();
    }

    public static ReleaseInfo ParseRelease(string json)
    {
        var release = JsonSerializer.Deserialize<GitHubRelease>(json)
            ?? throw new InvalidDataException("The release information was empty.");

        if (release.Draft || release.Prerelease || !VersionTag.IsMatch(release.TagName ?? "") ||
            !Version.TryParse(release.TagName![1..], out var version))
            throw new InvalidDataException("The latest release does not have a stable vX.Y.Z version.");

        var matches = (release.Assets ?? new List<GitHubAsset>())
            .Where(asset => asset.Name == ExecutableName).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The release must contain exactly one BuffStrap.exe asset.");

        var asset = matches[0];
        string expectedPath = $"/{Repository}/releases/download/{release.TagName}/{ExecutableName}";
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The app asset is not an official BuffStrap release download.");

        if (asset.Size < 2 || asset.Size > MaximumDownloadSize)
            throw new InvalidDataException("The app asset has an invalid size.");

        string? sha256 = null;
        if (!String.IsNullOrEmpty(asset.Digest))
        {
            if (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(asset.Digest[7..], "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("The app asset has an invalid SHA-256 digest.");
            sha256 = asset.Digest[7..];
        }

        // Keep notes bounded: release notes are displayed as plain text, never executed.
        string notes = release.Body ?? "";
        if (notes.Length > 32000)
            notes = notes[..32000];
        return new ReleaseInfo(release.TagName!, version, uri, asset.Size, sha256, notes, release.PublishedAt);
    }

    /// <summary>
    /// Downloads into a new directory on every attempt; partial or stale executables are never reused.
    /// The caller owns the returned file and its parent directory after a successful download.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string destinationRoot,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(destinationRoot, Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, ExecutableName);
        Directory.CreateDirectory(directory);

        try
        {
            using var response = await _httpClient.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
                throw new InvalidDataException("The download size does not match the release information.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > release.Size)
                        throw new InvalidDataException("The download was larger than expected.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)total / release.Size);
                }

                if (total != release.Size)
                    throw new InvalidDataException("The download was incomplete.");
            }

            if (release.Sha256 is not null &&
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download did not match its SHA-256 digest.");

            using (var executable = File.OpenRead(path))
            {
                if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z')
                    throw new InvalidDataException("The downloaded file is not a Windows executable.");
            }

            return path;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* Keep the original error. */ }
            throw;
        }
    }

    public static bool IsNewer(ReleaseInfo release, Version installedVersion) => release.Version > installedVersion;

    public static bool MatchesVersion(string? fileVersion, Version releaseVersion)
    {
        return Version.TryParse(fileVersion, out var actual) &&
            actual.Major == releaseVersion.Major && actual.Minor == releaseVersion.Minor &&
            actual.Build == releaseVersion.Build && (actual.Revision == -1 || actual.Revision == 0);
    }

    public static void VerifyExecutableVersion(string path, ReleaseInfo release)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != "BuffStrap" || !MatchesVersion(info.FileVersion, release.Version))
            throw new InvalidDataException("The downloaded app's identity or version does not match the release.");
    }

    private sealed class GitHubRelease
    {
        public GitHubRelease() { }
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        public GitHubAsset() { }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

public sealed record ReleaseInfo(string TagName, Version Version, Uri DownloadUri, long Size, string? Sha256,
    string Notes = "", DateTimeOffset? PublishedAt = null)
{
    public override string ToString() => TagName;
}
