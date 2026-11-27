using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace Bloxstrap.Utility
{
    /// <summary>
    /// Writes the pointer textures straight into every installed Roblox client,
    /// not just the one the bootstrapper happens to copy mods into. Roblox reads
    /// the texture from its own version folder at startup, so a stale copy, a
    /// second installed version, or a static-directory setup all end up with the
    /// wrong picture. This also produces the report behind the Check button, so a
    /// cursor that does not show can be diagnosed from the user's own machine.
    /// </summary>
    public static class CursorInstall
    {
        public static readonly string[] RelativePaths =
        {
            @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",
            @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png"
        };

        private static IEnumerable<string> EnumerateVersionRoots()
        {
            var roots = new List<string>();

            if (!String.IsNullOrEmpty(Paths.Versions))
                roots.Add(Paths.Versions);

            try
            {
                string stock = Path.Combine(Paths.Roblox, "Versions");
                if (!String.IsNullOrEmpty(Paths.Roblox) && !roots.Any(x => x.Equals(stock, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(stock);
            }
            catch (Exception) { }

            try
            {
                string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions");
                if (Directory.Exists(programFiles) && !roots.Any(x => x.Equals(programFiles, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(programFiles);
            }
            catch (Exception) { }

            return roots;
        }

        /// <summary>Installed Roblox client folders that can receive a texture.</summary>
        public static IReadOnlyList<string> ClientDirectories()
        {
            var directories = new List<string>();

            // BuffStrap keeps its own client copy, but the stock Roblox install can
            // still be the one that runs (app shortcut, website launch). Both are
            // written, otherwise the pointer appears to ignore the setting.
            foreach (string versions in EnumerateVersionRoots())
            {
                try
                {
                    if (Directory.Exists(versions))
                        directories.AddRange(Directory.GetDirectories(versions, "version-*"));
                }
                catch (Exception) { }
            }

            try
            {
                // A static directory is Versions\Player (or Versions\Studio), not a
                // version-* folder, so it has to be added explicitly.
                if (App.Settings.Prop.StaticDirectory)
                {
                    foreach (string binaryType in new[] { "Player", "Studio" })
                    {
                        string staticDirectory = Path.Combine(Paths.Versions, binaryType);
                        if (Directory.Exists(staticDirectory) &&
                            !directories.Any(x => x.Equals(staticDirectory, StringComparison.OrdinalIgnoreCase)))
                            directories.Add(staticDirectory);
                    }
                }
            }
            catch (Exception) { }

            return directories;
        }

        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..12];

        private static string? HashOfFile(string path)
        {
            try { return File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null; }
            catch (Exception) { return null; }
        }

        private static string BackupDirectory(string clientDirectory) =>
            Path.Combine(Paths.Base, "CursorBackup", Path.GetFileName(clientDirectory.TrimEnd(Path.DirectorySeparatorChar)));

        private static string BackupPath(string clientDirectory, string relative) =>
            Path.Combine(BackupDirectory(clientDirectory), relative);

        /// <summary>Keeps Roblox's own texture before the first replacement.</summary>
        private static void Backup(string clientDirectory, string relative, string target)
        {
            try
            {
                string backup = BackupPath(clientDirectory, relative);
                if (File.Exists(backup) || !File.Exists(target)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("CursorInstall::Backup", $"Could not back up {target}: {ex.GetType().Name}");
            }
        }

        /// <summary>Puts Roblox's original pointer textures back.</summary>
        public static int Restore()
        {
            int restored = 0;

            foreach (string directory in ClientDirectories())
            {
                foreach (string relative in RelativePaths)
                {
                    string backup = BackupPath(directory, relative);
                    if (!File.Exists(backup)) continue;

                    string target = Path.Combine(directory, relative);
                    try
                    {
                        Filesystem.AssertReadOnly(target);
                        File.Copy(backup, target, true);
                        File.Delete(backup);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine("CursorInstall::Restore", $"Could not restore {target}: {ex.GetType().Name}");
                    }
                }
            }

            return restored;
        }

        /// <summary>Copies the texture into every client folder; returns writes made.</summary>
        public static int Push(byte[] png)
        {
            int written = 0;
            string wanted = Hash(png);

            foreach (string directory in ClientDirectories())
            {
                foreach (string relative in RelativePaths)
                {
                    string target = Path.Combine(directory, relative);
                    try
                    {
                        if (HashOfFile(target) == wanted) continue;
                        Backup(directory, relative, target);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        Filesystem.AssertReadOnly(target);
                        File.WriteAllBytes(target, png);
                        written++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine("CursorInstall::Push", $"Could not write {target}: {ex.GetType().Name}");
                    }
                }
            }

            return written;
        }

        public static bool RobloxRunning()
        {
            try { return Process.GetProcessesByName("RobloxPlayerBeta").Length > 0; }
            catch (Exception) { return false; }
        }

        private static string ProtocolHandler()
        {
            try
            {
                object? value = Registry.GetValue(@"HKEY_CLASSES_ROOT\roblox-player\shell\open\command", "", null);
                return value is string command && command.Length > 0 ? command : "not registered";
            }
            catch (Exception) { return "unknown"; }
        }

        private static string ListMatches(string directory, byte[]? png)
        {
            var parts = new List<string>();
            foreach (string relative in RelativePaths)
            {
                string target = Path.Combine(directory, relative);
                string name = Path.GetFileName(target);
                string? hash = HashOfFile(target);
                if (hash is null) parts.Add($"{name}: missing");
                else if (png is not null && hash == Hash(png)) parts.Add($"{name}: ok");
                else parts.Add($"{name}: different ({hash})");
            }
            return String.Join(", ", parts);
        }

        /// <summary>
        /// Applies the selected set everywhere it can go and describes what is on
        /// disk, for the Check button in Settings.
        /// </summary>
        public static string Repair(CustomCursors.CursorSet? set)
        {
            var report = new StringBuilder();

            if (set is null)
            {
                report.AppendLine("No custom cursor set is selected, so Roblox uses its own pointer.");
                report.AppendLine("Pick a set in the list, then press Check again.");
                return report.ToString().TrimEnd();
            }

            byte[] png;
            try { png = File.ReadAllBytes(set.ImagePath); }
            catch (Exception ex)
            {
                return "The stored image for this set is missing (" + ex.GetType().Name + "). Import the set again.";
            }

            int written = Push(png);

            report.AppendLine($"Set: {set.Name}");
            report.AppendLine($"Image: {png.Length} bytes, id {Hash(png)}");
            report.AppendLine(written > 0
                ? $"Wrote the texture into {written} Roblox file(s) just now."
                : "Every Roblox folder already had this texture.");

            report.AppendLine();
            report.AppendLine("Mods folder:");
            report.AppendLine("  " + ListMatches(Paths.Modifications, png));

            var clients = ClientDirectories();
            report.AppendLine();
            report.AppendLine(clients.Count == 0
                ? "Roblox client folders: none found. Install or update Roblox once."
                : $"Roblox client folders ({clients.Count}):");
            foreach (string directory in clients)
                report.AppendLine("  " + Path.GetFileName(directory) + " -> " + ListMatches(directory, png));

            report.AppendLine();
            report.AppendLine("Roblox running: " + (RobloxRunning()
                ? "yes - fully close it (RobloxPlayerBeta.exe) before the next launch"
                : "no"));
            report.AppendLine("roblox-player handler: " + ProtocolHandler());
            report.AppendLine();
            report.AppendLine("If everything says ok and the pointer is still Roblox's own, Roblox is not reading this texture in your version, and no local launcher can force it further.");

            return report.ToString().TrimEnd();
        }
    }
}
