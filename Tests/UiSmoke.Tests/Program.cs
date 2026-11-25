using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BuffStrap.Favorites;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.UI.Elements.Dialogs;
using Bloxstrap.UI.Elements.Settings.Pages;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "BuffStrap-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = typeof(App).Assembly.GetType("Bloxstrap.Paths", throwOnError: true)!;
            paths.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { root });
            App.Settings.Prop.Theme = Theme.Dark;
            var app = new App();
            app.InitializeComponent();

            var store = new FavoritesStore(Path.Combine(root, "Favorites.json"));
            var account = new FavoriteAccount { Username = "Example_User" };
            var first = new FavoriteGame { Name = "Alpha game", PlaceId = 123, Folder = "Friends", AccountId = account.Id };
            var second = new FavoriteGame { Name = "Beta game", PlaceId = 456, Folder = "Other" };
            store.Update(data => { data.Accounts.Add(account); data.Games.Add(first); data.Games.Add(second); });

            // Loading the real BAML catches errors in names, controls, resources and event wiring.
            var menu = new LaunchMenuDialog();
            var favorites = new FavoritesWindow();
            var editor = new FavoriteEditWindow(first, new[] { account });
            var shortcutDialog = new FavoriteShortcutWindow();
            var repair = new RepairWindow();
            var updates = new UpdateCenterPage();
            Assert(menu.Content is not null && editor.Content is not null && shortcutDialog.Content is not null && repair.Content is not null && updates.Content is not null,
                "A window or page failed to load its content.");
            var list = (ItemsControl)favorites.FindName("GamesList");
            var search = (TextBox)favorites.FindName("SearchBox");
            var folder = (ComboBox)favorites.FindName("FolderFilter");
            Assert(list.Items.Count == 2, "Favorites were not rendered in the list.");
            search.Text = "Beta";
            Assert(list.Items.Count == 1, "Searching did not filter favorites.");
            search.Text = "";
            folder.SelectedItem = "Folder: Friends";
            Assert(list.Items.Count == 1, "Folder filtering did not work.");
            Assert(((TextBox)editor.FindName("NameBox")).Text == "Alpha game", "The favorite editor did not load the game.");
            var versionOption = (Bloxstrap.UI.Elements.Controls.OptionControl)updates.FindName("InstalledVersionOption");
            Assert(((TextBlock)versionOption.InnerContent).Text.StartsWith("v"), "Update Center did not show the version.");

            // Exercise the actual shortcut writer against a temporary path, never the runner's Desktop.
            var shortcutType = typeof(App).Assembly.GetType("Bloxstrap.Utility.FavoriteShortcut", throwOnError: true)!;
            var create = shortcutType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
            string linkPath = Path.Combine(root, "favorite.lnk");
            create.Invoke(null, new object[] { first, new List<string> { linkPath }, false });
            var link = ShellLink.Shortcut.ReadFromFile(linkPath);
            Assert(link.StringData.CommandLineArguments == $"-favorite {first.Id:D}", "Shortcut did not target the saved favorite.");
            Assert(link.ExtraData.EnvironmentVariableDataBlock.TargetUnicode == Path.Combine(root, "BuffStrap.exe"), "Shortcut pointed to the wrong launcher.");
            var getPaths = shortcutType.GetMethod("GetPaths", BindingFlags.Public | BindingFlags.Static)!;
            var choices = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, true, true })!;
            Assert(choices.Count == 2 && choices[0] != choices[1], "Desktop and Start menu were not both selected.");

            var repairType = typeof(App).Assembly.GetType("Bloxstrap.InstallationRepair", throwOnError: true)!;
            repairType.GetMethod("SchedulePlayerReinstall", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "State.json")));
            Assert(state.RootElement.GetProperty("ForceReinstall").GetBoolean(), "The repair was not scheduled.");
            Console.WriteLine("PASS WPF page construction, favorite search/folders, shortcuts and repair scheduling");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL UI smoke test: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
