using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Bloxstrap.UI.ViewModels.Dialogs;
using Bloxstrap.UI.ViewModels.Installer;
using Wpf.Ui.Mvvm.Interfaces;

namespace Bloxstrap.UI.Elements.Dialogs
{
    /// <summary>
    /// Interaction logic for LaunchMenuDialog.xaml
    /// </summary>
    public partial class LaunchMenuDialog
    {
        public NextAction CloseAction = NextAction.Terminate;

        public LaunchMenuDialog()
        {
            var viewModel = new LaunchMenuViewModel();
            viewModel.CloseWindowRequest += (_, closeAction) =>
            {
                CloseAction = closeAction;
                Close();
            };

            viewModel.FactoryResetRequested += (_, _) => ConfirmFactoryReset();

            DataContext = viewModel;

            InitializeComponent();

            ApplyPreReleaseIdentity();
        }

        /// <summary>
        /// Test builds wear the alpha mark: the hourglass replaces the usual icon on
        /// the launch menu and in the taskbar.
        /// </summary>
        private void ApplyPreReleaseIdentity()
        {
            if (!App.IsPreReleaseBuild) return;

            try
            {
                var icon = new BitmapImage(new Uri("pack://application:,,,/Resources/BuffStrapAlpha.png", UriKind.Absolute));
                icon.Freeze();
                Icon = icon;
                AlphaLogo.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LaunchMenuDialog::ApplyPreReleaseIdentity", ex.Message);
            }
        }

        private void ConfirmFactoryReset()
        {
            var result = Frontend.ShowMessageBox(
                "Factory reset BuffStrap?\n\n" +
                "This deletes every BuffStrap setting, saved account session, favorite, imported cursor, log " +
                "and installed client copy on this Windows user, then reopens BuffStrap as if it were new.\n\n" +
                "Roblox itself, your games and Windows files are not touched.",
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes)
                return;

            int removed;
            try
            {
                removed = Bloxstrap.Utility.FactoryReset.Clear();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("The reset could not finish.\n\n" + ex.Message, MessageBoxImage.Error);
                return;
            }

            App.Logger.WriteLine("LaunchMenuDialog::ConfirmFactoryReset", $"Removed {removed} data entries");

            if (!Bloxstrap.Utility.FactoryReset.Reopen())
            {
                Frontend.ShowMessageBox("BuffStrap was reset but could not reopen itself. Start it again from its shortcut.", MessageBoxImage.Information);
            }

            // Exit without saving settings again, so nothing is written back.
            App.Terminate();
        }
    }
}
