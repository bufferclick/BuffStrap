using BuffStrap.Release;
using Bloxstrap.UI.Elements.Dialogs;
using System.Windows;

namespace Bloxstrap
{
    internal static class AppUpdater
    {
        /// <summary>
        /// Checks before opening the menu, settings, or Roblox. Returns true only
        /// after the verified update has started and the current process should exit.
        /// </summary>
        public static async Task<bool> CheckForUpdatesAsync()
        {
            const string LOG_IDENT = "AppUpdater::CheckForUpdates";

            // Windows cannot replace the installed executable while another instance uses it.
            // Try again on a later launch instead of disrupting a watcher or active session.
            if (Process.GetProcessesByName(App.ProjectName).Length > 1)
            {
                App.Logger.WriteLine(LOG_IDENT, "Another BuffStrap instance is running; postponing the update.");
                return false;
            }

            var client = new ReleaseClient(App.HttpClient);
            ReleaseInfo release;
            try
            {
                using var lookupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                release = await client.GetLatestReleaseAsync(lookupTimeout.Token);
                if (!ReleaseClient.IsNewer(release, new Version(App.Version)))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Already on the latest version.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Being offline or a missing release must never prevent Roblox from launching.
                App.Logger.WriteLine(LOG_IDENT, "Could not check for a release; continuing with the installed version.");
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }

            using var updateLock = new InterProcessLock("AutoUpdater");
            if (!updateLock.IsAcquired)
            {
                App.Logger.WriteLine(LOG_IDENT, "Another updater is already running.");
                return false;
            }

            using var cancellation = new CancellationTokenSource();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            UpdateWindow? window = null;
            string? downloadedPath = null;
            bool started = false;

            try
            {
                if (!App.LaunchSettings.QuietFlag.Active)
                {
                    window = new UpdateWindow(release.TagName, cancellation);
                    window.Show();
                }

                var progress = new Progress<double>(value =>
                {
                    if (window?.IsVisible == true)
                        window.SetProgress(value);
                });
                downloadedPath = await client.DownloadAsync(release, Paths.TempUpdates, progress, timeout.Token);
                ReleaseClient.VerifyExecutableVersion(downloadedPath, release);
                timeout.Token.ThrowIfCancellationRequested();

                var startInfo = new ProcessStartInfo { FileName = downloadedPath, UseShellExecute = false };
                // A Roblox protocol URL must stay first for LaunchSettings to recognise it.
                foreach (string arg in App.LaunchSettings.Args)
                    startInfo.ArgumentList.Add(arg);
                startInfo.ArgumentList.Add("-upgrade");

                App.Settings.Save();
                if (Process.Start(startInfo) is null)
                    throw new InvalidOperationException("Windows could not start the update.");

                started = true;
                App.Logger.WriteLine(LOG_IDENT, $"Started verified update {release.TagName}.");
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                App.Logger.WriteLine(LOG_IDENT, "The update was cancelled; continuing with the installed version.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The update failed; continuing with the installed version.");
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox(string.Format(Strings.Bootstrapper_AutoUpdateFailed, release.TagName), MessageBoxImage.Information);
            }
            finally
            {
                if (window?.IsVisible == true)
                    window.Finish();
                if (!started && downloadedPath is not null)
                {
                    try { Directory.Delete(Path.GetDirectoryName(downloadedPath)!, true); } catch { }
                }
            }

            return false;
        }
    }
}
