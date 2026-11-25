using System.Windows;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class FavoriteShortcutWindow
    {
        public bool Desktop => DesktopCheck.IsChecked == true;
        public bool StartMenu => StartMenuCheck.IsChecked == true;

        public FavoriteShortcutWindow() => InitializeComponent();

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!Desktop && !StartMenu)
            {
                Frontend.ShowMessageBox("Choose Desktop, Start menu, or both.", MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        }
    }
}
