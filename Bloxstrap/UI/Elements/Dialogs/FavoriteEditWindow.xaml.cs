using BuffStrap.Favorites;
using System.Windows;
using System.Windows.Controls;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class FavoriteEditWindow
    {
        private readonly Guid _id;
        private CancellationTokenSource? _previewCancellation;
        private string? _suggestedName;
        private bool _loaded;
        public FavoriteGame? Game { get; private set; }

        private sealed class AccountChoice
        {
            public Guid? Id { get; init; }
            public string Name { get; init; } = "";
            public override string ToString() => Name;
        }

        public FavoriteEditWindow(FavoriteGame? game, IEnumerable<FavoriteAccount> accounts)
        {
            _id = game?.Id ?? Guid.NewGuid();
            InitializeComponent();
            NameBox.Text = game?.Name ?? "";
            PlaceBox.Text = game?.PlaceId.ToString() ?? "";
            FolderBox.Text = game?.Folder ?? "";
            var choices = new List<AccountChoice> { new() { Name = "Current Roblox session" } };
            choices.AddRange(accounts.OrderBy(x => x.Username, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => new AccountChoice { Id = x.Id, Name = x.ToString() }));
            AccountBox.ItemsSource = choices;
            AccountBox.SelectedItem = choices.FirstOrDefault(x => x.Id == game?.AccountId) ?? choices[0];
            if (game is not null) EditorHeading.Text = "Edit your favorite";
            Loaded += (_, _) => { _loaded = true; StartPreview(); };
            Closed += (_, _) => { _loaded = false; _previewCancellation?.Cancel(); };
        }

        private void PlaceBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loaded) StartPreview();
        }

        private void StartPreview()
        {
            _previewCancellation?.Cancel();
            PreviewIcon.Source = null;
            long? place = GameShortcut.ParsePlaceId(PlaceBox.Text);
            if (!place.HasValue)
            {
                PreviewTitle.Text = "Game preview";
                PreviewDescription.Text = "Enter a game link to load its details.";
                return;
            }
            PreviewTitle.Text = "Looking up this game…";
            PreviewDescription.Text = "Fetching public Roblox details.";
            var cancellation = new CancellationTokenSource();
            _previewCancellation = cancellation;
            _ = LoadPreviewAsync(place.Value, cancellation);
        }

        private async Task LoadPreviewAsync(long placeId, CancellationTokenSource cancellation)
        {
            try
            {
                await Task.Delay(500, cancellation.Token); // Wait until the user finishes typing.
                var preview = await GamePreviews.GetAsync(placeId, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                PreviewIcon.Source = GamePreviews.MakeImage(preview?.IconPng);
                if (preview is null || !preview.DetailsAvailable)
                {
                    PreviewTitle.Text = "Preview unavailable";
                    PreviewDescription.Text = "You can still add the game by entering a name.";
                    return;
                }
                PreviewTitle.Text = String.IsNullOrWhiteSpace(preview.Name) ? $"Place {placeId}" : preview.Name;
                PreviewDescription.Text = String.IsNullOrWhiteSpace(preview.Description) ? "No description provided by the creator." : preview.Description;
                // Never overwrite a name the user has edited or an existing favorite's name.
                if (!String.IsNullOrWhiteSpace(preview.Name) &&
                    (String.IsNullOrWhiteSpace(NameBox.Text) || (_suggestedName is not null && NameBox.Text == _suggestedName)))
                {
                    NameBox.Text = preview.Name;
                    _suggestedName = preview.Name;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch
            {
                PreviewTitle.Text = "Preview unavailable";
                PreviewDescription.Text = "You can still add the game by entering a name.";
            }
            finally
            {
                if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
                cancellation.Dispose();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            string folder = FolderBox.Text.Trim();
            long? place = GameShortcut.ParsePlaceId(PlaceBox.Text);
            if (String.IsNullOrWhiteSpace(name) || name.Length > 120 || folder.Length > 80 || place is null)
            {
                Frontend.ShowMessageBox("Enter a name (up to 120 characters), a valid Roblox game link or place ID, and an optional folder (up to 80 characters).", MessageBoxImage.Warning);
                return;
            }
            Game = new FavoriteGame
            {
                Id = _id, PlaceId = place.Value, Name = name, Folder = folder,
                AccountId = (AccountBox.SelectedItem as AccountChoice)?.Id
            };
            DialogResult = true;
        }
    }
}
