using BuffStrap.Favorites;
using System.Windows;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class FavoriteEditWindow
    {
        private readonly Guid _id;
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
