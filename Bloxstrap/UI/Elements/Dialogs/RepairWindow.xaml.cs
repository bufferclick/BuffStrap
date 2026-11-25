using System.Windows;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class RepairWindow
    {
        public RepairWindow()
        {
            InitializeComponent();
            Scan();
        }

        private void Scan()
        {
            try
            {
                var findings = InstallationRepair.Scan();
                var lines = new List<string>();
                if (findings.AppMissingOrInvalid) lines.Add("• BuffStrap executable is missing or has an unexpected identity. Reinstall it using Update Center or the official setup.");
                if (findings.PlayerProtocol) lines.Add("• Roblox Player links do not point to this BuffStrap installation (this may be intentional).");
                if (findings.ApiRegistration) lines.Add("• BuffStrap integration registration is missing or incorrect.");
                if (findings.UninstallRegistration) lines.Add("• BuffStrap installation registration is missing or incorrect.");
                if (findings.DesktopShortcut) lines.Add("• Desktop shortcut is missing or points elsewhere (optional).");
                if (findings.StartMenuShortcut) lines.Add("• Start menu shortcut is missing or points elsewhere (optional).");
                if (findings.PlayerMissing) lines.Add("• The installed Roblox Player executable is missing.");
                if (findings.PlayerNeverInstalled) lines.Add("• Roblox Player has not been installed through BuffStrap yet.");
                if (lines.Count == 0) lines.Add("No common issues were found. This is not a full file-integrity check.");
                FindingsText.Text = String.Join("\n", lines);
                ProtocolCheck.IsChecked = findings.PlayerProtocol;
                ApiCheck.IsChecked = findings.ApiRegistration;
                UninstallCheck.IsChecked = findings.UninstallRegistration;
                DesktopCheck.IsChecked = false;
                StartCheck.IsChecked = false;
                PlayerCheck.IsChecked = findings.PlayerMissing;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RepairWindow::Scan", ex);
                FindingsText.Text = "The check could not be completed: " + ex.Message;
            }
        }

        private void Scan_Click(object sender, RoutedEventArgs e) => Scan();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Repair_Click(object sender, RoutedEventArgs e)
        {
            var work = new List<(string Name, Action Run)>();
            if (ProtocolCheck.IsChecked == true) work.Add(("Roblox Player links", InstallationRepair.RestorePlayerProtocols));
            if (ApiCheck.IsChecked == true) work.Add(("Integration registration", InstallationRepair.RestoreApiRegistration));
            if (UninstallCheck.IsChecked == true) work.Add(("Installation registration", InstallationRepair.RestoreUninstallRegistration));
            if (DesktopCheck.IsChecked == true) work.Add(("Desktop shortcut", () => InstallationRepair.RestoreShortcut(true)));
            if (StartCheck.IsChecked == true) work.Add(("Start menu shortcut", () => InstallationRepair.RestoreShortcut(false)));
            if (PlayerCheck.IsChecked == true) work.Add(("Roblox Player reinstall on next Player launch", InstallationRepair.SchedulePlayerReinstall));
            if (work.Count == 0)
            {
                Frontend.ShowMessageBox("Select at least one repair.", MessageBoxImage.Information);
                return;
            }
            if (PlayerCheck.IsChecked == true && (Utilities.IsRobloxRunning() || AppUpdater.OtherInstanceRunning()))
            {
                Frontend.ShowMessageBox("Close Roblox and other BuffStrap processes before scheduling a Player reinstall, then try again.", MessageBoxImage.Information);
                return;
            }
            string summary = String.Join("\n", work.Select(x => "• " + x.Name));
            if (Frontend.ShowMessageBox("Apply these repairs?\n\n" + summary +
                "\n\nPlayer links may replace another launcher's links. Roblox reinstall needs a connection and may close a running player.",
                MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            var report = new List<string>();
            foreach (var item in work)
            {
                try { item.Run(); report.Add("✓ " + item.Name + (item.Name.StartsWith("Roblox Player reinstall", StringComparison.Ordinal) ? " scheduled" : " repaired")); }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RepairWindow::Repair", ex);
                    report.Add("✗ " + item.Name + ": " + ex.Message);
                }
            }
            FindingsText.Text = String.Join("\n", report) + "\n\nA scheduled Roblox Player reinstall completes only after a successful Player launch.";
        }
    }
}
