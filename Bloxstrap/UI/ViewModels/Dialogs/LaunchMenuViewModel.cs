using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using Bloxstrap.UI.Elements.Dialogs;

namespace Bloxstrap.UI.ViewModels.Installer
{
    public class LaunchMenuViewModel
    {
        public string Version
        {
            get
            {
                string version = string.Format(Strings.Menu_About_Version, App.Version);
                if (App.IsPreReleaseBuild) return version + " (pre-release)";
                if (!App.IsActionBuild || App.IsProductionBuild) return version;
                string commit = App.BuildMetadata.CommitHash;
                string revision = String.IsNullOrEmpty(commit) ? "" : " " + commit[..Math.Min(commit.Length, 7)];
                return version + " · test build" + revision;
            }
        }

        public ICommand LaunchSettingsCommand => new RelayCommand(LaunchSettings);

        public ICommand LaunchRobloxCommand => new RelayCommand(LaunchRoblox);

        public ICommand LaunchFavoritesCommand => new RelayCommand(LaunchFavorites);

        public ICommand LaunchAccountsCommand => new RelayCommand(LaunchAccounts);

        public event EventHandler<NextAction>? CloseWindowRequest;

        /// <summary>Raised when Global settings is pressed with Alt+Shift+Control held.</summary>
        public event EventHandler? FactoryResetRequested;

        private void LaunchSettings()
        {
            // Hidden gesture. Holding all three modifiers turns this one action
            // into a factory reset, so the destructive option has no button of its own.
            const ModifierKeys resetKeys = ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Control;

            if ((Keyboard.Modifiers & resetKeys) == resetKeys)
            {
                FactoryResetRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);
        }

        private void LaunchRoblox() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);

        private void LaunchFavorites() => new FavoritesWindow().ShowDialog();

        private void LaunchAccounts() => new AccountsWindow().ShowDialog();
    }
}
