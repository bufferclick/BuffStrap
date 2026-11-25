using BuffStrap.Backup;
using BuffStrap.Release;
using Bloxstrap.UI.Elements.Dialogs;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    public partial class UpdateCenterPage
    {
        private bool _busy;
        private TextBlock InstalledVersionText => (TextBlock)InstalledVersionOption.InnerContent;
        private TextBlock LastCheckText => (TextBlock)LastCheckOption.InnerContent;
        private Wpf.Ui.Controls.ToggleSwitch AutoUpdateSwitch => (Wpf.Ui.Controls.ToggleSwitch)AutoUpdateOption.InnerContent;
        private Wpf.Ui.Controls.Button BackupButton => (Wpf.Ui.Controls.Button)BackupOption.InnerContent;
        private Wpf.Ui.Controls.Button RestoreButton => (Wpf.Ui.Controls.Button)RestoreOption.InnerContent;

        public UpdateCenterPage()
        {
            InitializeComponent();
            InstalledVersionText.Text = "v" + App.Version;
            ShowCachedCheck();
            Loaded += (_, _) => AutoUpdateSwitch.IsChecked = App.Settings.Prop.CheckForUpdates;
        }

        private void ShowCachedCheck()
        {
            var cache = UpdateCheckCache.Load();
            LastCheckText.Text = cache is null ? "Never" : cache.CheckedAtUtc.ToLocalTime().ToString("g");
            if (cache is not null)
            {
                LatestText.Text = "Latest checked: " + cache.TagName;
                NotesText.Text = String.IsNullOrWhiteSpace(cache.Notes) ? "No release notes were provided." : cache.Notes;
            }
        }

        private void AutoUpdate_Click(object sender, RoutedEventArgs e) => App.Settings.Prop.CheckForUpdates = AutoUpdateSwitch.IsChecked == true;

        private async void Check_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            CheckButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            CheckStatusText.Text = "Checking GitHub releases…";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var client = new ReleaseClient(App.HttpClient);
                var latest = await client.GetLatestReleaseAsync(timeout.Token);
                UpdateCheckCache.Record(latest);
                ShowCachedCheck();
                IReadOnlyList<ReleaseInfo> releases;
                bool listLoaded = true;
                try { releases = await client.GetStableReleasesAsync(timeout.Token); }
                catch (Exception ex)
                {
                    App.Logger.WriteException("UpdateCenter::ReleaseList", ex);
                    releases = new[] { latest };
                    listLoaded = false;
                    CheckStatusText.Text = "The latest release was found, but the full version list could not be loaded.";
                }
                var all = releases.Append(latest).GroupBy(x => x.TagName, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First()).OrderByDescending(x => x.Version).ToList();
                ReleaseList.ItemsSource = all;
                ReleaseList.SelectedItem = all.FirstOrDefault(x => x.TagName == latest.TagName);
                if (listLoaded)
                    CheckStatusText.Text = ReleaseClient.IsNewer(latest, new Version(App.Version)) ? "A newer release is available." : "You have the latest release or a newer version.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("UpdateCenter::Check", ex);
                CheckStatusText.Text = "Could not check releases. Check your connection and try again.";
                Frontend.ShowMessageBox("Could not check for updates.\n\n" + ex.Message, MessageBoxImage.Warning);
            }
            finally
            {
                _busy = false;
                CheckButton.IsEnabled = true;
                InstallButton.IsEnabled = ReleaseList.SelectedItem is ReleaseInfo;
            }
        }

        private void Release_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ReleaseList.SelectedItem is not ReleaseInfo release) return;
            NotesText.Text = String.IsNullOrWhiteSpace(release.Notes) ? "No release notes were provided." : release.Notes;
            InstallButton.IsEnabled = !_busy;
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || ReleaseList.SelectedItem is not ReleaseInfo release) return;
            bool rollback = release.Version < new Version(App.Version);
            string warning = rollback
                ? "This is an older release. Newer settings may not be compatible or may be lost. Back up your data first. Automatic updates will be turned off, and the older installer may ask you to confirm the downgrade again."
                : "BuffStrap will download and verify the selected release, then close and reopen. Save any settings changes first.";
            if (Frontend.ShowMessageBox($"Install BuffStrap {release.TagName}?\n\n{warning}", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _busy = true;
            CheckButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            try
            {
                if (await AppUpdater.InstallReleaseAsync(release, manual: true))
                {
                    App.Terminate();
                    return;
                }
            }
            finally
            {
                _busy = false;
                CheckButton.IsEnabled = true;
                InstallButton.IsEnabled = ReleaseList.SelectedItem is ReleaseInfo;
                AutoUpdateSwitch.IsChecked = App.Settings.Prop.CheckForUpdates;
            }
        }

        private async void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save BuffStrap backup", Filter = "BuffStrap backup (*.zip)|*.zip", DefaultExt = ".zip",
                FileName = "BuffStrap-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip", OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return;
            _busy = true;
            BackupButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            try
            {
                BackupResult result = await Task.Run(() => BackupService.Create(Paths.Base, dialog.FileName));
                string skipped = result.Skipped.Count == 0 ? "" : $"\n\n{result.Skipped.Count} sensitive-named files or links were skipped.";
                Frontend.ShowMessageBox($"Backup created with {result.Files} files.{skipped}\n\nReview custom mods and settings before sharing; they may contain personal information.", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("UpdateCenter::Backup", ex);
                Frontend.ShowMessageBox("Backup failed.\n\n" + ex.Message, MessageBoxImage.Warning);
            }
            finally { _busy = false; BackupButton.IsEnabled = true; RestoreButton.IsEnabled = true; }
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dialog = new OpenFileDialog { Title = "Select BuffStrap backup", Filter = "BuffStrap backup (*.zip)|*.zip", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            if (AppUpdater.OtherInstanceRunning() || Utilities.IsRobloxRunning() || Process.GetProcessesByName(Path.GetFileNameWithoutExtension(App.RobloxStudioAppName)).Length > 0)
            {
                Frontend.ShowMessageBox("Close Roblox, Roblox Studio, and any other BuffStrap windows before restoring.", MessageBoxImage.Warning);
                return;
            }
            if (Frontend.ShowMessageBox("Restore this backup?\n\nFiles present in the backup will replace your current settings, favorites and matching mods. Extra files are kept. Unsaved changes will be lost, and BuffStrap will close after restoring. Back up your current data first.",
                    MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _busy = true;
            BackupButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            try
            {
                BackupResult result = await Task.Run(() => BackupService.Restore(Paths.Base, dialog.FileName, ValidateStagedSettings));
                Frontend.ShowMessageBox($"Restored {result.Files} files. BuffStrap will close now; reopen it to use the restored data.", MessageBoxImage.Information);
                App.Terminate();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("UpdateCenter::Restore", ex);
                Frontend.ShowMessageBox("Restore failed.\n\n" + ex.Message, MessageBoxImage.Warning);
            }
            finally { _busy = false; BackupButton.IsEnabled = true; RestoreButton.IsEnabled = true; }
        }

        private static void ValidateStagedSettings(string directory)
        {
            var settings = JsonSerializer.Deserialize<Bloxstrap.Models.Persistable.Settings>(File.ReadAllText(Path.Combine(directory, "Settings.json")));
            if (settings is null) throw new InvalidDataException("The backup settings are invalid.");
            string flags = Path.Combine(directory, "Modifications", "ClientSettings", "ClientAppSettings.json");
            if (File.Exists(flags) && JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(flags)) is null)
                throw new InvalidDataException("The backup FastFlags are invalid.");
        }

        private void Repair_Click(object sender, RoutedEventArgs e) => new RepairWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
