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

        private void LaunchSettings() => CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);

        private void LaunchRoblox() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);

        private void LaunchFavorites() => new FavoritesWindow().ShowDialog();

        private void LaunchAccounts() => new AccountsWindow().ShowDialog();
    }
}
