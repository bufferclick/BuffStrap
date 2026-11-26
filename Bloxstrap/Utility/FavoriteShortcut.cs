using BuffStrap.Favorites;

namespace Bloxstrap.Utility
{
    internal static class FavoriteShortcut
    {
        private static string SafeName(string name)
        {
            // Windows also reserves device names even when followed by an extension.
            string clean = new(name.Select(c => Char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
            clean = clean.Trim().TrimEnd('.', ' ');
            if (clean.Length > 120) clean = clean[..120].TrimEnd('.', ' ');
            if (clean.Length > 0 && Char.IsHighSurrogate(clean[^1])) clean = clean[..^1];
            if (String.IsNullOrWhiteSpace(clean)) clean = "Roblox game";
            string stem = clean.Split('.')[0];
            if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                clean = "_" + clean;
            return clean;
        }

        // Use the favorite's name when possible. An unrelated file or another
        // favorite is never replaced: use a small numeric suffix on collision.
        public static string GetAvailablePath(string directory, FavoriteGame game)
        {
            string name = SafeName(game.Name);
            for (int number = 1; number <= 10000; number++)
            {
                string suffix = number == 1 ? "" : $" ({number})";
                string path = Path.Combine(directory, name + suffix + ".lnk");
                if ((!File.Exists(path) && !Directory.Exists(path)) || IsManagedShortcut(path, game.Id)) return path;
            }
            throw new IOException("Too many shortcuts share this name. Rename the favorite and try again.");
        }

        public static IReadOnlyList<string> GetPaths(FavoriteGame game, bool desktop, bool startMenu)
        {
            if (!desktop && !startMenu) throw new ArgumentException("Choose at least one shortcut location.");
            var paths = new List<string>();
            if (desktop) paths.Add(GetAvailablePath(Paths.Desktop, game));
            if (startMenu) paths.Add(GetAvailablePath(Path.Combine(Paths.WindowsStartMenu, "BuffStrap Favorites"), game));
            return paths;
        }

        public static void Create(FavoriteGame game, IReadOnlyList<string> paths, bool replace)
        {
            // Check every destination before writing any of them. A user can
            // replace a shortcut for this favorite, never an unrelated file.
            foreach (string path in paths)
            {
                if (Directory.Exists(path) || (File.Exists(path) && (!replace || !IsManagedShortcut(path, game.Id))))
                    throw new IOException("A different shortcut or file already exists: " + path);
            }
            foreach (string path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                bool exists = File.Exists(path);
                if (exists && (!replace || !IsManagedShortcut(path, game.Id)))
                    throw new IOException("The shortcut changed. Please try again: " + path);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    // An external test build may be newer than the installed
                    // executable. Target the running build, as Play does, and
                    // avoid triggering an installation/update from the link.
                    Shortcut.Create(Paths.Process, $"-favorite {game.Id:D} -childlaunch", temporary);
                    if (!File.Exists(temporary)) throw new IOException("Could not create shortcut: " + path);
                    File.Move(temporary, path, exists && replace);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        // Old versions included the favorite ID in the name. Only offer to
        // remove links that are verifiably ours and point to this favorite.
        public static IReadOnlyList<string> FindLegacyPaths(FavoriteGame game, IReadOnlyList<string> newPaths)
        {
            var found = new List<string>();
            foreach (string directory in newPaths.Select(x => Path.GetDirectoryName(x)!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string path in Directory.EnumerateFiles(directory, "*.lnk"))
                    if (IsLegacyName(path, game.Id) && IsManagedShortcut(path, game.Id)) found.Add(path);
            }
            return found;
        }

        public static int RemoveLegacyPaths(FavoriteGame game, IReadOnlyList<string> paths)
        {
            int removed = 0;
            foreach (string path in paths)
            {
                // Check again after confirmation in case a file changed.
                if (IsLegacyName(path, game.Id) && IsManagedShortcut(path, game.Id))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            return removed;
        }

        private static bool IsLegacyName(string path, Guid id) =>
            Path.GetFileName(path).EndsWith($" (BuffStrap-{id:N}).lnk", StringComparison.OrdinalIgnoreCase);

        private static bool IsManagedShortcut(string path, Guid id)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var link = ShellLink.Shortcut.ReadFromFile(path);
                string? target = link.ExtraData?.EnvironmentVariableDataBlock?.TargetUnicode;
                string arguments = link.StringData?.CommandLineArguments?.Trim() ?? "";
                bool ourTarget = String.Equals(target, Paths.Process, StringComparison.OrdinalIgnoreCase) ||
                                 String.Equals(target, Paths.Application, StringComparison.OrdinalIgnoreCase) ||
                                 (!String.IsNullOrWhiteSpace(target) && Path.IsPathFullyQualified(target) &&
                                  String.Equals(Path.GetFileName(target), App.ProjectName + ".exe", StringComparison.OrdinalIgnoreCase));
                bool ourArguments = String.Equals(arguments, $"-favorite {id:D}", StringComparison.OrdinalIgnoreCase) ||
                                    String.Equals(arguments, $"-favorite {id:D} -childlaunch", StringComparison.OrdinalIgnoreCase);
                return ourTarget && ourArguments;
            }
            catch (Exception) { return false; } // Unknown or damaged file: leave it alone.
        }
    }
}
