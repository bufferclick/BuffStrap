using BuffStrap.Favorites;

namespace Bloxstrap.Utility
{
    internal static class FavoriteShortcut
    {
        public static IReadOnlyList<string> GetPaths(FavoriteGame game, bool desktop, bool startMenu)
        {
            if (!desktop && !startMenu) throw new ArgumentException("Choose at least one shortcut location.");
            string clean = String.Concat(game.Name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
            if (clean.Length > 70) clean = clean[..70].Trim();
            if (clean.Length > 0 && Char.IsHighSurrogate(clean[^1])) clean = clean[..^1];
            if (String.IsNullOrWhiteSpace(clean)) clean = "Roblox game";
            // The full id prevents two favorites with the same name from replacing one another.
            string fileName = $"{clean} (BuffStrap-{game.Id:N}).lnk";
            var paths = new List<string>();
            if (desktop) paths.Add(Path.Combine(Paths.Desktop, fileName));
            if (startMenu) paths.Add(Path.Combine(Paths.WindowsStartMenu, "BuffStrap Favorites", fileName));
            return paths;
        }

        public static void Create(FavoriteGame game, IReadOnlyList<string> paths, bool replace)
        {
            foreach (string path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path))
                {
                    if (!replace) throw new IOException("A shortcut already exists: " + path);
                    File.Delete(path);
                }
                Shortcut.Create(Paths.Application, $"-favorite {game.Id:D}", path);
                if (!File.Exists(path)) throw new IOException("Could not create shortcut: " + path);
            }
        }
    }
}
