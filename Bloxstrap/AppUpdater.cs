using BuffStrap.Release;
using Bloxstrap.UI.Elements.Dialogs;
using System.Windows;

namespace Bloxstrap
{
    internal static class AppUpdater
    {
        /// <summary>Checks at startup; a failed check never blocks Roblox.</summary>
        public static async Task<bool> CheckForUpdatesAsync()
        {
            const string LOG_IDENT = "AppUpdater::CheckForUpdates";
            if (OtherInstanceRunning())
            {
                App.Logger.WriteLine(LOG_IDENT, "Another BuffStrap instance is running; postponing the update.");
                return false;
            }
            try
            {
                using var lookupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                ReleaseInfo release = await new ReleaseClient(App.HttpClient).GetLatestReleaseAsync(lookupTimeout.Token);
                UpdateCheckCache.Record(release);
                if (!ReleaseClient.IsNewer(release, new Version(App.Version))) return false;
                return await InstallReleaseAsync(release, manual: false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not check for a release; continuing with the installed version.");
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        public static bool OtherInstanceRunning() => Process.GetProcessesByName(App.ProjectName).Length > 1;

        /// <summary>
        /// Uses the same verified download path for manual upgrades, reinstalls and
        /// rollbacks. Older versions know CheckForUpdates, so disabling that persisted
        /// setting before a rollback also prevents v1.0.0 from immediately undoing it.
        /// </summary>
        public static async Task<bool> InstallReleaseAsync(ReleaseInfo release, bool manual)
        {
            const string LOG_IDENT = "AppUpdater::InstallRelease";
            if (OtherInstanceRunning())
            {
                if (manual) Frontend.ShowMessageBox("Close other BuffStrap windows and any active Roblox session, then try again.", MessageBoxImage.Information);
                return false;
            }

            using var updateLock = new InterProcessLock("AutoUpdater");
            if (!updateLock.IsAcquired)
            {
                if (manual) Frontend.ShowMessageBox("Another update is already in progress.", MessageBoxImage.Information);
                return false;
            }

            using var cancellation = new CancellationTokenSource();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            UpdateWindow? window = null;
            string? downloadedPath = null;
            bool started = false;
            bool previousAutoUpdate = App.Settings.Prop.CheckForUpdates;
            bool rollback = manual && release.Version < new Version(App.Version);
            bool changedAutoUpdate = false;
            try
            {
                if (manual || !App.LaunchSettings.QuietFlag.Active)
                {
                    window = new UpdateWindow(release.TagName, cancellation);
                    window.Show();
                }
                var progress = new Progress<double>(value =>
                {
                    if (window?.IsVisible == true) window.SetProgress(value);
                });
                var client = new ReleaseClient(App.HttpClient);
                downloadedPath = await client.DownloadAsync(release, Paths.TempUpdates, progress, timeout.Token);
                ReleaseClient.VerifyExecutableVersion(downloadedPath, release);
                timeout.Token.ThrowIfCancellationRequested();

                var startInfo = new ProcessStartInfo { FileName = downloadedPath, UseShellExecute = false };
                if (manual)
                    startInfo.ArgumentList.Add("-settings");
                else
                    foreach (string arg in App.LaunchSettings.Args) startInfo.ArgumentList.Add(arg);
                startInfo.ArgumentList.Add("-upgrade");

                changedAutoUpdate = rollback;
                SaveSettingsForInstall(rollback);
                if (Process.Start(startInfo) is null)
                    throw new InvalidOperationException("Windows could not start the selected release.");
                started = true;
                App.Logger.WriteLine(LOG_IDENT, $"Started verified release {release.TagName}.");
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                App.Logger.WriteLine(LOG_IDENT, "The update was cancelled.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                string message = manual ? $"Could not install {release.TagName}.\n\n{ex.Message}" : string.Format(Strings.Bootstrapper_AutoUpdateFailed, release.TagName);
                Frontend.ShowMessageBox(message, MessageBoxImage.Warning);
            }
            finally
            {
                if (window?.IsVisible == true) window.Finish();
                if (!started && changedAutoUpdate)
                {
                    App.Settings.Prop.CheckForUpdates = previousAutoUpdate;
                    App.Settings.Save();
                }
                if (!started && downloadedPath is not null)
                {
                    try { Directory.Delete(Path.GetDirectoryName(downloadedPath)!, true); } catch { }
                }
            }
            return false;
        }

        internal static void SaveSettingsForInstall(bool rollback)
        {
            if (rollback) App.Settings.Prop.CheckForUpdates = false;
            App.Settings.Save();
            if (rollback)
            {
                // JsonManager.Save reports errors rather than throwing. Do not start
                // a rollback unless even v1.0.0 will see automatic updates disabled.
                var persisted = JsonSerializer.Deserialize<Settings>(File.ReadAllText(App.Settings.FileLocation));
                if (persisted is null || persisted.CheckForUpdates)
                    throw new IOException("Could not save the setting that prevents automatic updates after rollback.");
            }
        }
    }
}
