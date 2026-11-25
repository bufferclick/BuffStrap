using BuffStrap.Favorites;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuffStrap.Backup;

public sealed class BackupEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<BackupEntry> Files { get; set; } = new();
}

public sealed record BackupResult(int Files, IReadOnlyList<string> Skipped);

/// <summary>
/// An allowlisted backup: settings, sanitized favorites, mods/FastFlags,
/// profiles and custom launcher themes. It never traverses Roblox's session
/// storage, and rejects traversal, links, duplicate entries, oversized archives,
/// missing files and checksum mismatches before touching the installation.
/// Restore is an overlay and rolls back overwritten files if a write fails.
/// </summary>
public static class BackupService
{
    private const string ManifestName = "manifest.json";
    private const int MaxFiles = 10000;
    private const long MaxEntrySize = 512L * 1024 * 1024;
    private const long MaxTotalSize = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] Roots = { "Modifications", "Profiles", "CustomThemes" };

    public static BackupResult Create(string baseDirectory, string archivePath)
    {
        baseDirectory = Path.GetFullPath(baseDirectory);
        archivePath = Path.GetFullPath(archivePath);
        string settingsPath = Path.Combine(baseDirectory, "Settings.json");
        if (!File.Exists(settingsPath)) throw new FileNotFoundException("Settings.json was not found.", settingsPath);
        if (new FileInfo(settingsPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Settings are too large.");
        EnsureNoReparse(baseDirectory, settingsPath);
        string relativeArchive = Path.GetRelativePath(baseDirectory, archivePath).Replace(Path.DirectorySeparatorChar, '/');
        if (Roots.Any(root => relativeArchive.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose a backup location outside the mod, profile and theme folders.");
        byte[] settings = SanitizeSettings(File.ReadAllBytes(settingsPath));
        byte[] favorites = Encoding.UTF8.GetBytes(FavoritesStore.Serialize(new FavoritesStore(Path.Combine(baseDirectory, "Favorites.json")).Load()));
        var files = new List<(string Relative, string Source)>();
        var skipped = new List<string>();
        foreach (string root in Roots)
        {
            string path = Path.Combine(baseDirectory, root);
            if (Directory.Exists(path)) Collect(baseDirectory, path, files, skipped);
        }
        if (files.Count + 2 > MaxFiles) throw new InvalidDataException("There are too many files to back up.");

        string? parent = Path.GetDirectoryName(archivePath);
        if (String.IsNullOrEmpty(parent)) throw new InvalidDataException("Choose a valid backup location.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".buffstrap-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        var manifest = new BackupManifest();
        long total = 0;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                AddBytes(zip, manifest, "Settings.json", settings, ref total);
                AddBytes(zip, manifest, "Favorites.json", favorites, ref total);
                foreach (var file in files)
                {
                    EnsureNoReparse(baseDirectory, file.Source);
                    using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    AddStream(zip, manifest, file.Relative, input, input.Length, ref total);
                }
                var manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using var stream = manifestEntry.Open();
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
            }
            File.Move(temporary, archivePath, true);
            return new BackupResult(manifest.Files.Count, skipped);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static BackupResult Restore(string baseDirectory, string archivePath, Action<string>? validateStaged = null)
    {
        baseDirectory = Path.GetFullPath(baseDirectory);
        var favorites = new FavoritesStore(Path.Combine(baseDirectory, "Favorites.json"));
        return favorites.WithExclusiveLock(() => RestoreLocked(baseDirectory, Path.GetFullPath(archivePath), validateStaged));
    }

    private static BackupResult RestoreLocked(string baseDirectory, string archivePath, Action<string>? validateStaged)
    {
        string stage = Path.Combine(Path.GetTempPath(), "BuffStrap-Restore-" + Guid.NewGuid().ToString("N"));
        string stagedFiles = Path.Combine(stage, "incoming");
        string originals = Path.Combine(stage, "originals");
        Directory.CreateDirectory(stagedFiles);
        bool keepStage = false;
        try
        {
            List<BackupEntry> files;
            using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count < 2 || zip.Entries.Count > MaxFiles + 1)
                    throw new InvalidDataException("The backup has an invalid number of files.");
                var zipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    if (!zipNames.Add(entry.FullName) || (entry.FullName != ManifestName && !IsAllowed(entry.FullName)))
                        throw new InvalidDataException("The backup contains a duplicate or unsafe file path.");
                }
                ZipArchiveEntry meta = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("The backup manifest is missing.");
                if (meta.Length > 4 * 1024 * 1024) throw new InvalidDataException("The backup manifest is too large.");
                BackupManifest manifest;
                using (var stream = meta.Open())
                using (var bounded = new MemoryStream())
                {
                    CopyAndHash(stream, bounded, meta.Length); // enforce the limit even if ZIP metadata lies
                    bounded.Position = 0;
                    manifest = JsonSerializer.Deserialize<BackupManifest>(bounded) ?? throw new InvalidDataException("The backup manifest is empty.");
                }
                if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Count > MaxFiles || manifest.Files.Count + 1 != zip.Entries.Count)
                    throw new InvalidDataException("The backup manifest has an unsupported format.");

                files = manifest.Files;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (BackupEntry file in files)
                {
                    if (file is null || !IsAllowed(file.Path) || !seen.Add(file.Path) || file.Size < 0 || file.Size > MaxEntrySize ||
                        file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)))
                        throw new InvalidDataException("The backup manifest contains an invalid file.");
                    total = checked(total + file.Size);
                    if (total > MaxTotalSize) throw new InvalidDataException("The backup is too large to restore.");
                    var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("A backup file is missing.");
                    if (entry.Length != file.Size) throw new InvalidDataException("A backup file has the wrong size.");
                    string target = Path.Combine(stagedFiles, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = entry.Open();
                    using var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    string hash = CopyAndHash(source, dest, file.Size);
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A backup file failed its integrity check.");
                }
                if (!seen.Contains("Settings.json") || !seen.Contains("Favorites.json"))
                    throw new InvalidDataException("The backup is missing settings or favorites.");
            }

            // Validate the structured files before any installation file is changed.
            string settingsFile = Path.Combine(stagedFiles, "Settings.json");
            string favoritesFile = Path.Combine(stagedFiles, "Favorites.json");
            if (new FileInfo(settingsFile).Length > 8 * 1024 * 1024 || new FileInfo(favoritesFile).Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Settings or favorites are too large.");
            File.WriteAllBytes(settingsFile, SanitizeSettings(File.ReadAllBytes(settingsFile)));
            File.WriteAllText(favoritesFile, FavoritesStore.Serialize(FavoritesStore.Parse(File.ReadAllText(favoritesFile))));
            string flagFile = Path.Combine(stagedFiles, "Modifications", "ClientSettings", "ClientAppSettings.json");
            if (File.Exists(flagFile))
            {
                if (new FileInfo(flagFile).Length > 8 * 1024 * 1024) throw new InvalidDataException("FastFlags are too large.");
                EnsureJsonObject(File.ReadAllBytes(flagFile), "FastFlags");
            }
            validateStaged?.Invoke(stagedFiles);

            // Check every destination before starting; never follow junctions or symlinks.
            foreach (var file in files)
                EnsureNoReparse(baseDirectory, Path.Combine(baseDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar)));

            var applied = new List<(string Target, string Original, bool Existed)>();
            try
            {
                foreach (var file in files)
                {
                    string relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(baseDirectory, relative);
                    string original = Path.Combine(originals, relative);
                    bool existed = File.Exists(target);
                    if (existed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                        File.Copy(target, original, false);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Record before overwriting so a partial write is also restored.
                    applied.Add((target, original, existed));
                    File.Copy(Path.Combine(stagedFiles, relative), target, true);
                }
            }
            catch (Exception writeError)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var file in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (file.Existed) File.Copy(file.Original, file.Target, true);
                        else if (File.Exists(file.Target)) File.Delete(file.Target);
                    }
                    catch (Exception ex) { rollbackErrors.Add(ex); }
                }
                if (rollbackErrors.Count != 0)
                {
                    keepStage = true;
                    throw new IOException($"Restore failed and some originals could not be put back. They are saved in {originals}.", new AggregateException(new[] { writeError }.Concat(rollbackErrors)));
                }
                throw;
            }
            return new BackupResult(files.Count, Array.Empty<string>());
        }
        finally
        {
            if (!keepStage) { try { Directory.Delete(stage, true); } catch { } }
        }
    }

    private static void Collect(string baseDirectory, string directory, List<(string Relative, string Source)> files, List<string> skipped)
    {
        EnsureNoReparse(baseDirectory, directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            string relative = Path.GetRelativePath(baseDirectory, entry).Replace(Path.DirectorySeparatorChar, '/');
            if ((attributes & FileAttributes.ReparsePoint) != 0 || IsSensitiveFilePath(relative)) { skipped.Add(relative); continue; }
            if ((attributes & FileAttributes.Directory) != 0) Collect(baseDirectory, entry, files, skipped);
            else
            {
                if (!IsAllowed(relative)) { skipped.Add(relative); continue; }
                files.Add((relative, entry));
                if (files.Count > MaxFiles) throw new InvalidDataException("There are too many files to back up.");
            }
        }
    }

    private static bool IsAllowed(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\') || path.StartsWith('/') || IsSensitiveFilePath(path)) return false;
        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 255 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) return false;
            string stem = part.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return parts.Length == 1 && (path == "Settings.json" || path == "Favorites.json") ||
            parts.Length > 1 && Roots.Contains(parts[0], StringComparer.Ordinal);
    }

    private static bool IsSensitive(string path)
    {
        string lower = path.ToLowerInvariant();
        return lower.Contains("roblosecurity") || lower.Contains("robloxcookies") || lower.Contains("authenticationticket") ||
               lower.Contains("credential") || lower.Contains("password") || lower.Contains("sessiontoken");
    }

    private static bool IsSensitiveFilePath(string path)
    {
        string lower = path.ToLowerInvariant();
        return IsSensitive(lower) || lower.Contains("cookie") || lower.Contains("accesstoken") ||
            lower.Contains("refreshtoken") || lower.Contains("securitytoken");
    }

    private static void EnsureNoReparse(string root, string path)
    {
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new InvalidDataException("A backup path is outside the installation.");
        string current = root;
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The installation directory is a link or junction.");
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (segment == "." || segment.Length == 0) continue;
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A backup path is a link or junction: " + relative);
        }
    }

    private static byte[] SanitizeSettings(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Settings must be a JSON object.");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            WriteFiltered(writer, document.RootElement);
        return output.ToArray();
    }

    private static void WriteFiltered(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                string key = property.Name.ToLowerInvariant();
                if (IsSensitive(key) || key is "cookie" or "cookies" or "accesstoken" or "refreshtoken" or "securitytoken" or "clientsecret" or "apikey") continue;
                writer.WritePropertyName(property.Name);
                WriteFiltered(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteFiltered(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private static void EnsureJsonObject(byte[] json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(name + " must be a JSON object.");
    }

    private static void AddBytes(ZipArchive zip, BackupManifest manifest, string path, byte[] data, ref long total)
    {
        using var input = new MemoryStream(data, false);
        AddStream(zip, manifest, path, input, data.LongLength, ref total);
    }

    private static void AddStream(ZipArchive zip, BackupManifest manifest, string path, Stream input, long size, ref long total)
    {
        if (!IsAllowed(path) || size < 0 || size > MaxEntrySize) throw new InvalidDataException("A backup file is too large or invalid.");
        total = checked(total + size);
        if (total > MaxTotalSize) throw new InvalidDataException("The files are too large to back up.");
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var output = entry.Open();
        manifest.Files.Add(new BackupEntry { Path = path, Size = size, Sha256 = CopyAndHash(input, output, size) });
    }

    private static string CopyAndHash(Stream input, Stream output, long expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long count = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            count += read;
            if (count > expected) throw new InvalidDataException("A backup file grew beyond its expected size.");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        if (count != expected) throw new InvalidDataException("A backup file was incomplete.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
