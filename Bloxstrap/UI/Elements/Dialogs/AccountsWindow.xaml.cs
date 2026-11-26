using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using BuffStrap.Favorites;
using Bloxstrap.UI.Elements.Base;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AccountsWindow : WpfUiWindow
    {
        public AccountsWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                try { await RefreshAsync(); }
                catch { Frontend.ShowMessageBox("Could not load the account list. Check that Favorites.json is accessible.", MessageBoxImage.Warning); }
            };
        }

        private async Task RefreshAsync()
        {
            Guid? selectedId = (AccountsList.SelectedItem as AccountCard)?.Account.Id;
            var accounts = FavoritesLauncher.Store.Load().Accounts;
            var cards = accounts.Select(account => new AccountCard(account)).ToList();
            AccountsList.ItemsSource = cards;
            AccountsList.SelectedItem = cards.FirstOrDefault(x => x.Account.Id == selectedId);
            EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelection();
            await Task.WhenAll(cards.Select(LoadAvatarAsync));
        }

        private static async Task LoadAvatarAsync(AccountCard card)
        {
            if (!card.Account.UserId.HasValue) return;
            try
            {
                var bytes = await AccountSessions.GetAvatarAsync(card.Account.UserId.Value);
                if (bytes is null) return;
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 150;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                card.Avatar = image;
            }
            catch { /* An unavailable thumbnail must not prevent account use. */ }
        }

        private void AccountsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelection();

        private void UpdateSelection()
        {
            // During XAML initialization the footer may not exist yet.
            if (PlayAccountButton is null || ReconnectButton is null || RemoveButton is null || SelectionHint is null) return;
            var card = AccountsList.SelectedItem as AccountCard;
            ReconnectButton.IsEnabled = RemoveButton.IsEnabled = card is not null;
            PlayAccountButton.IsEnabled = card is not null && AccountSessions.IsSaved(card.Account);
            SelectionHint.Text = card is null ? "Select an account to play or manage it." :
                "Selected for this launch: " + card.Username + ". Roblox may update its shared last-signed-in account.";
        }

        private async void Add_Click(object sender, RoutedEventArgs e) => await SignInAsync(null);

        private async void Reconnect_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsList.SelectedItem is AccountCard card)
                await SignInAsync(card.Account.Id);
        }

        private async Task SignInAsync(Guid? expectedId)
        {
            try
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                {
                    Frontend.ShowMessageBox("Account sign-in requires Windows 10 version 1809 or newer and the WebView2 Runtime.", MessageBoxImage.Warning);
                    return;
                }
                var login = new RobloxLoginWindow(expectedId) { Owner = this };
                login.ShowDialog();
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not open Roblox sign-in. " + SafeError(ex), MessageBoxImage.Error);
            }
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsList.SelectedItem is not AccountCard card) return;
            if (Frontend.ShowMessageBox("Remove " + card.Account + " from this PC? Favorites assigned to it will use the current Roblox session instead. This does not sign the account out on Roblox's servers.",
                MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try
            {
                await AccountSessions.RemoveAsync(card.Account.Id);
                await RefreshAsync();
            }
            catch (Exception ex) { Frontend.ShowMessageBox("Could not remove this account. " + SafeError(ex), MessageBoxImage.Error); }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsList.SelectedItem is not AccountCard card) return;
            try
            {
                long? placeId = GameShortcut.ParsePlaceId(PlaceBox.Text);
                if (!placeId.HasValue)
                {
                    Frontend.ShowMessageBox("Enter a valid Roblox game link or place ID first.", MessageBoxImage.Warning);
                    return;
                }
                AccountSessions.RequireSaved(card.Account.Id);
                FavoritesLauncher.StartAccountLaunch(card.Account.Id, placeId.Value);
            }
            catch (Exception ex) { Frontend.ShowMessageBox(SafeError(ex), MessageBoxImage.Warning); }
        }

        // Never display arbitrary network/JSON exceptions: they may contain response data.
        internal static string SafeError(Exception ex) => ex is BuffStrap.Accounts.RobloxAccountException
            ? ex.Message : "Please try again. Check your connection and Windows permissions.";

        private sealed class AccountCard : INotifyPropertyChanged
        {
            public FavoriteAccount Account { get; }
            public string DisplayName => String.IsNullOrWhiteSpace(Account.DisplayName) ? Account.Username : Account.DisplayName;
            public string Username => "@" + Account.Username;
            public string Initial => Account.Username[..1].ToUpperInvariant();
            public string Status => AccountSessions.IsSaved(Account) ? "Sign-in saved on this PC" : "Sign in required on this PC";
            public string AccessibleLabel => DisplayName + ", " + Username + ", " + Status;
            private BitmapImage? _avatar;
            public BitmapImage? Avatar
            {
                get => _avatar;
                set { _avatar = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar))); }
            }
            public AccountCard(FavoriteAccount account) => Account = account;
            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
