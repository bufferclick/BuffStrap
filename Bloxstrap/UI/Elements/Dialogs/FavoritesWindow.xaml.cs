using BuffStrap.Favorites;
using System.Windows;
using System.Windows.Controls;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class FavoritesWindow
    {
        private FavoritesData _data = new();
        private bool _refreshing;
        private bool _ready;

        public FavoritesWindow()
        {
            InitializeComponent();
            _ready = true;
            Reload();
        }

        private sealed class FavoriteRow
        {
            public FavoriteGame Game { get; init; } = null!;
            public string Name => Game.Name;
            public string Detail { get; init; } = "";
        }

        private sealed class RecentRow
        {
            public RecentGame Game { get; init; } = null!;
            public string Name => Game.Name;
            public string Detail => $"Place {Game.PlaceId} • {Game.PlayedAtUtc.ToLocalTime():g}";
        }

        private void Reload()
        {
            try
            {
                _data = FavoritesLauncher.Store.Load();
                string selection = FolderFilter.SelectedItem as string ?? "All folders";
                var folders = new List<string> { "All folders", "Unfiled" };
                folders.AddRange(_data.Games.Select(x => x.Folder.Trim()).Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                    .Select(x => "Folder: " + x));
                _refreshing = true;
                FolderFilter.ItemsSource = folders;
                FolderFilter.SelectedItem = folders.Contains(selection) ? selection : "All folders";
                _refreshing = false;
                AccountsList.ItemsSource = _data.Accounts.OrderBy(x => x.Username, StringComparer.CurrentCultureIgnoreCase).ToList();
                RecentList.ItemsSource = _data.Recent.OrderByDescending(x => x.PlayedAtUtc).Select(x => new RecentRow { Game = x }).ToList();
                RefreshGames();
            }
            catch (Exception ex) { ShowError("Could not read favorites", ex); }
        }

        private void RefreshGames()
        {
            if (!_ready || _refreshing) return;
            string filter = FolderFilter.SelectedItem as string ?? "All folders";
            string search = SearchBox.Text.Trim();
            string AccountName(FavoriteGame game) => _data.Accounts.FirstOrDefault(x => x.Id == game.AccountId)?.Username ?? "Current Roblox session";
            var rows = _data.Games
                .Where(game => filter == "All folders" || (filter == "Unfiled" ? String.IsNullOrWhiteSpace(game.Folder) :
                    filter.StartsWith("Folder: ", StringComparison.Ordinal) && game.Folder.Equals(filter[8..], StringComparison.OrdinalIgnoreCase)))
                .Where(game => String.IsNullOrEmpty(search) || game.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    game.Folder.Contains(search, StringComparison.OrdinalIgnoreCase) || game.PlaceId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    AccountName(game).Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(game => game.Folder, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(game => new FavoriteRow
                {
                    Game = game,
                    Detail = $"Place {game.PlaceId} • {(String.IsNullOrWhiteSpace(game.Folder) ? "Unfiled" : game.Folder)} • {AccountName(game)}"
                }).ToList();
            GamesList.ItemsSource = rows;
        }

        private void Filter_Changed(object sender, EventArgs e) => RefreshGames();

        private void AddGame_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FavoriteEditWindow(null, _data.Accounts) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Game is null) return;
            try
            {
                var game = dialog.Game;
                FavoritesLauncher.Store.Update(data => data.Games.Add(game));
                Reload();
            }
            catch (Exception ex) { ShowError("Could not add the game", ex); }
        }

        private void EditGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FavoriteRow row) return;
            var dialog = new FavoriteEditWindow(row.Game, _data.Accounts) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Game is null) return;
            try
            {
                var changed = dialog.Game;
                FavoritesLauncher.Store.Update(data =>
                {
                    int index = data.Games.FindIndex(x => x.Id == changed.Id);
                    if (index < 0) throw new InvalidOperationException("This favorite was removed in another window.");
                    data.Games[index] = changed;
                });
                Reload();
            }
            catch (Exception ex) { ShowError("Could not save the game", ex); }
        }

        private void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FavoriteRow row) return;
            if (Frontend.ShowMessageBox($"Remove {row.Name} from favorites? Existing shortcuts for it will no longer work.",
                    MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { FavoritesLauncher.Store.Update(data => data.Games.RemoveAll(x => x.Id == row.Game.Id)); Reload(); }
            catch (Exception ex) { ShowError("Could not remove the game", ex); }
        }

        private void PlayGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FavoriteRow row) return;
            try { FavoritesLauncher.StartFavorite(row.Game.Id); }
            catch (Exception ex) { ShowError("Could not start the game", ex); }
        }

        private void PlayRecent_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not RecentRow row) return;
            try
            {
                var matches = _data.Games.Where(x => x.PlaceId == row.Game.PlaceId).ToList();
                if (matches.Count > 1)
                {
                    Frontend.ShowMessageBox("This game has multiple favorites. Choose the one you want on the Favorites tab.", MessageBoxImage.Information);
                    return;
                }
                if (matches.Count == 1) FavoritesLauncher.StartFavorite(matches[0].Id);
                else FavoritesLauncher.StartPlace(row.Game.PlaceId);
            }
            catch (Exception ex) { ShowError("Could not start the game", ex); }
        }

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FavoriteRow row) return;
            var dialog = new FavoriteShortcutWindow { Owner = this };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var paths = FavoriteShortcut.GetPaths(row.Game, dialog.Desktop, dialog.StartMenu);
                bool replace = paths.Any(File.Exists);
                if (replace && Frontend.ShowMessageBox("A shortcut already exists at one of these locations. Replace it?",
                        MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                FavoriteShortcut.Create(row.Game, paths, replace);
                Frontend.ShowMessageBox("Shortcut created:\n\n" + String.Join("\n", paths), MessageBoxImage.Information);
            }
            catch (Exception ex) { ShowError("Could not create all shortcuts", ex); }
        }

        private void AddAccount_Click(object sender, RoutedEventArgs e)
        {
            string username = AccountNameBox.Text.Trim();
            if (!FavoritesStore.IsValidUsername(username))
            {
                Frontend.ShowMessageBox("Enter a Roblox username (3–20 letters, numbers or underscores).", MessageBoxImage.Warning);
                return;
            }
            try
            {
                FavoritesLauncher.Store.Update(data =>
                {
                    if (data.Accounts.Any(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("That username is already in the list.");
                    data.Accounts.Add(new FavoriteAccount { Username = username });
                });
                AccountNameBox.Text = "";
                Reload();
            }
            catch (Exception ex) { ShowError("Could not add the account", ex); }
        }

        private void RemoveAccount_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FavoriteAccount account) return;
            if (Frontend.ShowMessageBox($"Remove {account.Username}? Games assigned to it will use the current Roblox session instead.",
                    MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try
            {
                FavoritesLauncher.Store.Update(data =>
                {
                    data.Accounts.RemoveAll(x => x.Id == account.Id);
                    foreach (var game in data.Games.Where(x => x.AccountId == account.Id)) game.AccountId = null;
                });
                Reload();
            }
            catch (Exception ex) { ShowError("Could not remove the account", ex); }
        }

        private static void ShowError(string title, Exception ex)
        {
            App.Logger.WriteException("FavoritesWindow", ex);
            Frontend.ShowMessageBox(title + ".\n\n" + ex.Message, MessageBoxImage.Warning);
        }
    }
}
