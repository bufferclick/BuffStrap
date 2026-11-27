using BuffStrap.Cursors;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bloxstrap.Utility
{
    /// <summary>
    /// Imported cursor sets. Roblox draws its pointer with two 64x64 textures,
    /// so a Windows cursor package is reduced to its normal pointer image.
    /// Nothing here touches an account session or the network beyond the
    /// library page the user opens themselves.
    /// </summary>
    public static class CustomCursors
    {
        public const int TextureSize = 64;

        /// <summary>Roblox draws the pointer 1:1, so only truly huge art is scaled.</summary>
        public const int MaximumPointerSize = 128;

        private static readonly string[] TexturePaths =
        {
            @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",
            @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png"
        };

        public static string LibraryUrl => CursorPackage.LibraryUrl;

        public static string Root => Path.Combine(Paths.Base, "CursorSets");

        public sealed class CursorSet
        {
            public string Id { get; init; } = "";
            public string Name { get; init; } = "";
            public string Source { get; init; } = "";
            public DateTime ImportedAt { get; init; }
            public string ImagePath => Path.Combine(Root, Id, "ArrowCursor.png");
        }

        private static string SafeId(string name, byte[] png)
        {
            string cleaned = new(name.Select(c => Char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            cleaned = cleaned.Trim('-');
            if (cleaned.Length > 40) cleaned = cleaned[..40].Trim('-');
            if (cleaned.Length == 0) cleaned = "cursor";
            string hash = Convert.ToHexString(SHA256.HashData(png))[..8].ToLowerInvariant();
            return cleaned.ToLowerInvariant() + "-" + hash;
        }

        public static IReadOnlyList<CursorSet> List()
        {
            var sets = new List<CursorSet>();
            if (!Directory.Exists(Root)) return sets;

            foreach (string directory in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    string image = Path.Combine(directory, "ArrowCursor.png");
                    string meta = Path.Combine(directory, "set.json");
                    if (!File.Exists(image) || !File.Exists(meta)) continue;
                    var info = JsonSerializer.Deserialize<SavedSet>(File.ReadAllText(meta));
                    if (info is null || String.IsNullOrWhiteSpace(info.Name)) continue;
                    sets.Add(new CursorSet
                    {
                        Id = Path.GetFileName(directory),
                        Name = info.Name,
                        Source = info.Source ?? "",
                        ImportedAt = info.ImportedAt
                    });
                }
                catch (Exception) { /* Skip a damaged entry; it can be removed by hand. */ }
            }

            return sets.OrderByDescending(x => x.ImportedAt).ToList();
        }

        private sealed class SavedSet
        {
            public string? Name { get; set; }
            public string? Source { get; set; }
            public DateTime ImportedAt { get; set; }
        }

        /// <summary>Imports a .zip/.cur/.ani/.inf file and returns the stored set.</summary>
        public static CursorSet Import(string packagePath)
        {
            if (!File.Exists(packagePath))
                throw new InvalidDataException("That cursor file no longer exists.");

            return Import(File.ReadAllBytes(packagePath), Path.GetFileNameWithoutExtension(packagePath));
        }

        /// <summary>
        /// Imports already-downloaded bytes (the browser window hands its download
        /// straight here, so nothing is left in the user's Downloads folder).
        /// </summary>
        public static CursorSet Import(byte[] package, string name)
        {
            if (package.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("The cursor package is too large.");

            byte[] cursor = CursorPackage.ExtractArrow(package);

            CursorImage image;
            if (CursorImageReader.IsPngFile(cursor))
            {
                // A Roblox-ready PNG is already laid out the way the client wants
                // it (art centred on the click point), so it is only re-encoded.
                image = CursorImageReader.ReadPngImage(cursor);
            }
            else
            {
                // Windows cursor: keep the native pixel size and centre the hotspot.
                image = CursorImageReader.Read(cursor).ToPointer(MaximumPointerSize);
            }

            if (Math.Max(image.Width, image.Height) > MaximumPointerSize)
                image = image.Resize(MaximumPointerSize);

            byte[] png = CursorImageReader.ToPng(image);

            name = (name ?? "").Trim();
            if (name.Length == 0) name = "Custom cursor";
            if (name.Length > 60) name = name[..60].Trim();

            string id = SafeId(name, png);
            string directory = Path.Combine(Root, id);
            Directory.CreateDirectory(directory);

            File.WriteAllBytes(Path.Combine(directory, "ArrowCursor.png"), png);
            File.WriteAllText(Path.Combine(directory, "set.json"), JsonSerializer.Serialize(new SavedSet
            {
                Name = name,
                Source = "cursor library",
                ImportedAt = DateTime.UtcNow
            }));

            return new CursorSet { Id = id, Name = name, Source = "cursor library", ImportedAt = DateTime.UtcNow };
        }

        public static void Remove(CursorSet set)
        {
            string directory = Path.Combine(Root, set.Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            if (IsActive(set)) Apply(null);
        }

        /// <summary>The imported set whose image is currently in the mods folder.</summary>
        public static CursorSet? Active()
        {
            string target = Path.Combine(Paths.Modifications, TexturePaths[0]);
            if (!File.Exists(target)) return null;
            byte[] installed = File.ReadAllBytes(target);
            foreach (var set in List())
            {
                try
                {
                    if (File.ReadAllBytes(set.ImagePath).SequenceEqual(installed)) return set;
                }
                catch (IOException) { }
            }
            return null;
        }

        public static bool IsActive(CursorSet set)
        {
            string target = Path.Combine(Paths.Modifications, TexturePaths[0]);
            if (!File.Exists(target) || !File.Exists(set.ImagePath)) return false;
            return File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(set.ImagePath));
        }

        /// <summary>
        /// Writes the pointer textures into the mods folder (applied to Roblox at
        /// the next launch), or removes them when no custom set is selected.
        /// </summary>
        public static void Apply(CursorSet? set)
        {
            if (set is null)
            {
                // Selecting none must actually give Roblox its own pointer back,
                // including the copies already written into the client folders.
                CursorInstall.Restore();
            }

            foreach (string relative in TexturePaths)
            {
                string target = Path.Combine(Paths.Modifications, relative);
                if (set is null)
                {
                    if (File.Exists(target))
                    {
                        Filesystem.AssertReadOnly(target);
                        File.Delete(target);
                    }
                    continue;
                }

                if (!File.Exists(set.ImagePath))
                    throw new InvalidDataException("The imported cursor image is missing. Import the set again.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Filesystem.AssertReadOnly(target);
                File.Copy(set.ImagePath, target, true);
            }

            if (set is not null)
            {
                // Applied at once as well, so a launch that does not re-copy mods
                // (or a client installed in another folder) still shows it.
                try { CursorInstall.Push(File.ReadAllBytes(set.ImagePath)); }
                catch (Exception ex) { App.Logger.WriteLine("CustomCursors::Apply", ex.Message); }
            }
        }

        /// <summary>
        /// Re-writes the active set into every installed client. Called on each
        /// launch because a Roblox update or repair can restore its own texture.
        /// </summary>
        public static void EnsureInstalled()
        {
            try
            {
                CursorSet? active = Active();
                if (active is null) return;
                CursorInstall.Push(File.ReadAllBytes(active.ImagePath));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("CustomCursors::EnsureInstalled", ex.Message);
            }
        }

        /// <summary>
        /// Newest cursor package in the Downloads folder, so the import dialog
        /// starts on what the user just downloaded from the cursor library.
        /// </summary>
        public static string? FindRecentDownload()
        {
            try
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!Directory.Exists(downloads)) return null;

                var candidates = Directory.EnumerateFiles(downloads)
                    .Where(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                x.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) ||
                                x.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
                    .Select(x => new FileInfo(x))
                    .OrderByDescending(x => x.LastWriteTimeUtc)
                    .Take(25);

                foreach (var file in candidates)
                {
                    try
                    {
                        // Only suggest something that really contains a cursor.
                        CursorPackage.ExtractArrow(File.ReadAllBytes(file.FullName));
                        return file.FullName;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
