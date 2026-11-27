using BuffStrap.Favorites;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class FavoritesWindow
    {
        private FavoritesData _data = new();
        private List<FavoriteRow> _allRows = new();
        private readonly CancellationTokenSource _lifetime = new();
        private bool _refreshing;
        private bool _ready;

        public FavoritesWindow()
        {
            InitializeComponent();
            _ready = true;
            Closed += (_, _) => _lifetime.Cancel();
            Reload();
        }

        private sealed class FavoriteRow : INotifyPropertyChanged
        {
            public FavoriteGame Game { get; init; } = null!;
            public string Name => Game.Name;
            public string Detail { get; init; } = "";
            public bool PreviewRequested { get; set; }
            public string Metadata { get; private set; } = "Loading Roblox details…";
            public string Description { get; private set; } = "Loading game description…";
            public BitmapSource? Icon { get; private set; }
            public event PropertyChangedEventHandler? PropertyChanged;

            public void ApplyPreview(RobloxGamePreview? preview)
            {
                if (preview is null || !preview.DetailsAvailable)
                {
                    Metadata = $"Place {Game.PlaceId}";
                    Description = "Game details unavailable right now. Reopen Favorites to retry.";
                }
                else
                {
                    var parts = new List<string>();
                    if (!String.IsNullOrWhiteSpace(preview.Name) && !preview.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)) parts.Add(preview.Name);
                    if (!String.IsNullOrWhiteSpace(preview.Creator)) parts.Add("By " + preview.Creator);
                    if (preview.Playing.HasValue) parts.Add(preview.Playing.Value.ToString("N0") + " playing");
                    Metadata = parts.Count > 0 ? String.Join("  •  ", parts) : $"Place {Game.PlaceId}";
                    Description = String.IsNullOrWhiteSpace(preview.Description) ? "No description provided by the creator." : preview.Description;
                }
                Icon = GamePreviews.MakeImage(preview?.IconPng);
                foreach (string name in new[] { nameof(Metadata), nameof(Description), nameof(Icon) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
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
                RecentList.ItemsSource = _data.Recent.OrderByDescending(x => x.PlayedAtUtc).Select(x => new RecentRow { Game = x }).ToList();
                string AccountName(FavoriteGame game) => _data.Accounts.FirstOrDefault(x => x.Id == game.AccountId) is FavoriteAccount account
                    ? "@" + account.Username : "Current Roblox session";
                _allRows = _data.Games.Select(game => new FavoriteRow
                {
                    Game = game,
                    Detail = $"Place {game.PlaceId}  •  {(String.IsNullOrWhiteSpace(game.Folder) ? "Unfiled" : game.Folder)}  •  {AccountName(game)}"
                }).ToList();
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
            var rows = _allRows
                .Where(row => filter == "All folders" || (filter == "Unfiled" ? String.IsNullOrWhiteSpace(row.Game.Folder) :
                    filter.StartsWith("Folder: ", StringComparison.Ordinal) && row.Game.Folder.Equals(filter[8..], StringComparison.OrdinalIgnoreCase)))
                .Where(row => String.IsNullOrEmpty(search) || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.Game.Folder.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Game.PlaceId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    AccountName(row.Game).Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.Game.Folder, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            EmptyGames.Text = _allRows.Count == 0 ? "No favorites yet. Add a game to start your collection." : "No favorites match this search or folder.";
            EmptyGames.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GamesList.ItemsSource = rows;
        }

        private void Filter_Changed(object sender, EventArgs e) => RefreshGames();

        // Fetch only realized cards; a large collection should not issue thousands
        // of requests at once. Recycled list items can change their DataContext.
        private void FavoriteCard_Loaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FavoriteRow row) LoadPreview(row);
        }

        private void FavoriteCard_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement { IsLoaded: true } && e.NewValue is FavoriteRow row) LoadPreview(row);
        }

        private async void LoadPreview(FavoriteRow row)
        {
            if (row.PreviewRequested || _lifetime.IsCancellationRequested) return;
            row.PreviewRequested = true;
            try { row.ApplyPreview(await GamePreviews.GetAsync(row.Game.PlaceId, _lifetime.Token)); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { row.ApplyPreview(null); } // An unavailable preview must never block Play.
        }

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

        private void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            if (Frontend.ShowMessageBox("Clear the Recently Played list? Your favorites will be kept.",
                    MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { FavoritesLauncher.Store.Update(data => data.Recent.Clear()); Reload(); }
            catch (Exception ex) { ShowError("Could not clear Recently Played", ex); }
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
                if (replace && Frontend.ShowMessageBox("A shortcut for this favorite already exists. Update it to use this copy of BuffStrap?\n\n" +
                        String.Join("\n", paths.Where(File.Exists)), MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                FavoriteShortcut.Create(row.Game, paths, replace);
                var oldLinks = FavoriteShortcut.FindLegacyPaths(row.Game, paths);
                if (oldLinks.Count > 0 && Frontend.ShowMessageBox(
                        "Older GUID-named shortcuts for this favorite were found. Remove those old shortcuts now?\n\n" +
                        "If an old shortcut was pinned to Start or the taskbar, unpin it and pin the new one instead.\n\n" +
                        String.Join("\n", oldLinks), MessageBoxImage.Question, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    FavoriteShortcut.RemoveLegacyPaths(row.Game, oldLinks);
                Frontend.ShowMessageBox("Shortcut created:\n\n" + String.Join("\n", paths) +
                    "\n\nIt opens this BuffStrap executable:\n" + Paths.Process +
                    "\n\nKeep this executable in place. If you move or remove it, recreate the shortcut.", MessageBoxImage.Information);
            }
            catch (Exception ex) { ShowError("Could not create all shortcuts", ex); }
        }

        private void ManageAccounts_Click(object sender, RoutedEventArgs e)
        {
            new AccountsWindow { Owner = this }.ShowDialog();
            Reload();
        }

        private static void ShowError(string title, Exception ex)
        {
            App.Logger.WriteException("FavoritesWindow", ex);
            Frontend.ShowMessageBox(title + ".\n\n" + ex.Message, MessageBoxImage.Warning);
        }
    }
}
