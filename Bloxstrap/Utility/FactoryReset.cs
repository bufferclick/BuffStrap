namespace Bloxstrap.Utility
{
    /// <summary>
    /// Wipes BuffStrap's own data folder back to a first-run state. Only files the
    /// app itself created are removed: the running executable (and the folder that
    /// holds it) is left alone so the app can be reopened straight away, and no
    /// Roblox or Windows file is touched.
    /// </summary>
    public static class FactoryReset
    {
        /// <summary>Deletes BuffStrap data and returns how many entries were removed.</summary>
        public static int Clear()
        {
            string root = Paths.Base;
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root))
                return 0;

            string running = Path.GetFullPath(Paths.Process);
            int removed = 0;

            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                string full = Path.GetFullPath(entry);

                // Never delete the copy of BuffStrap that is running, or the folder
                // containing it when the app was copied there by hand.
                if (full.Equals(running, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (running.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (Directory.Exists(full))
                    {
                        ClearAttributes(full);
                        Directory.Delete(full, true);
                    }
                    else
                    {
                        File.SetAttributes(full, FileAttributes.Normal);
                        File.Delete(full);
                    }

                    removed++;
                }
                catch (Exception ex)
                {
                    // A file still held open by another BuffStrap window stays behind;
                    // the folder is cleaned on the next reset.
                    App.Logger?.WriteLine("FactoryReset::Clear", $"Could not remove {Path.GetFileName(full)}: {ex.GetType().Name}");
                }
            }

            return removed;
        }

        private static void ClearAttributes(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception) { }
        }

        /// <summary>Starts a fresh copy of BuffStrap after a reset.</summary>
        public static bool Reopen()
        {
            try
            {
                var start = new ProcessStartInfo(Paths.Process) { UseShellExecute = true, WorkingDirectory = Paths.Base };
                Process.Start(start);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("FactoryReset::Reopen", ex.GetType().Name);
                return false;
            }
        }
    }
}
