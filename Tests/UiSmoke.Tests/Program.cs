using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            // Exercise templates and bindings without showing an interactive window.
            var visual = (UIElement)favorites.Content;
            visual.Measure(new Size(900, 650));
            visual.Arrange(new Rect(0, 0, 900, 650));
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(900, 650, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Assert(ContainsText(visual, "Alpha game"), "A favorite row did not render its bound name.");
            ((TextBox)favorites.FindName("AccountNameBox")).Text = "Second_User";
            typeof(FavoritesWindow).GetMethod("AddAccount_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(favorites, new object[] { favorites, new RoutedEventArgs() });
            Assert(store.Load().Accounts.Count == 2, "Adding an account label did not persist it.");
            Assert(((TextBox)editor.FindName("NameBox")).Text == "Alpha game", "The favorite editor did not load the game.");
            var gameShortcutType = typeof(App).Assembly.GetType("Bloxstrap.Utility.GameShortcut", throwOnError: true)!;
            var parsePlace = gameShortcutType.GetMethod("ParsePlaceId", BindingFlags.Public | BindingFlags.Static)!;
            Assert((long?)parsePlace.Invoke(null, new object[] { "https://www.roblox.com/games/1234/example" }) == 1234,
                "A Roblox game link did not resolve to its place ID.");
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
            byte[] originalLink = File.ReadAllBytes(linkPath);
            try
            {
                create.Invoke(null, new object[] { first, new List<string> { linkPath }, false });
                throw new InvalidOperationException("A shortcut was overwritten without consent.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            Assert(File.ReadAllBytes(linkPath).SequenceEqual(originalLink), "The existing shortcut was changed without consent.");
            create.Invoke(null, new object[] { first, new List<string> { linkPath }, true });
            Assert(ShellLink.Shortcut.ReadFromFile(linkPath).StringData.CommandLineArguments == $"-favorite {first.Id:D}",
                "Consented shortcut replacement failed.");
            var getPaths = shortcutType.GetMethod("GetPaths", BindingFlags.Public | BindingFlags.Static)!;
            var choices = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, true, true })!;
            var desktop = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, true, false })!;
            var startMenu = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, false, true })!;
            Assert(choices.Count == 2 && choices[0] == desktop.Single() && choices[1] == startMenu.Single() && choices[0] != choices[1],
                "Desktop, Start menu and both shortcut choices did not work.");

            var repairType = typeof(App).Assembly.GetType("Bloxstrap.InstallationRepair", throwOnError: true)!;
            repairType.GetMethod("SchedulePlayerReinstall", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "State.json")));
            Assert(state.RootElement.GetProperty("ForcePlayerRepair").GetBoolean(), "The Player repair was not scheduled.");

            // A Studio or background update must not consume a Player-only repair.
            var launchSettings = new LaunchSettings(Array.Empty<string>());
            typeof(App).GetProperty("LaunchSettings")!.GetSetMethod(true)!.Invoke(null, new object[] { launchSettings });
            App.Settings.Prop.StaticDirectory = true;
            var playerData = App.Distribution.RobloxPlayerData;
            var studioData = App.Distribution.RobloxStudioData;
            playerData.State.VersionGuid = "version-player";
            studioData.State.VersionGuid = "version-studio";
            foreach (var client in new[] { playerData, studioData })
            {
                Directory.CreateDirectory(client.Directory);
                File.WriteAllText(client.ExecutablePath, "test");
            }
            var mustUpgrade = typeof(Bootstrapper).GetProperty("_mustUpgrade", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var studioBootstrapper = new Bootstrapper(LaunchMode.Studio);
            var playerBootstrapper = new Bootstrapper(LaunchMode.Player);
            Assert(!(bool)mustUpgrade.GetValue(studioBootstrapper)!, "A Player repair unexpectedly forced Studio to reinstall.");
            Assert((bool)mustUpgrade.GetValue(playerBootstrapper)!, "A Player repair did not force a Player reinstall.");
            launchSettings.BackgroundUpdaterFlag.Active = true;
            Assert(!(bool)mustUpgrade.GetValue(playerBootstrapper)!, "A background update consumed a Player repair.");
            launchSettings.BackgroundUpdaterFlag.Active = false;

            // Older releases already honor CheckForUpdates, so this persisted flag
            // must be false before starting a deliberate rollback.
            var updater = typeof(App).Assembly.GetType("Bloxstrap.AppUpdater", throwOnError: true)!;
            App.Settings.Prop.CheckForUpdates = true;
            updater.GetMethod("SaveSettingsForInstall", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { true });
            using var savedSettings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Settings.json")));
            Assert(!savedSettings.RootElement.GetProperty("CheckForUpdates").GetBoolean(), "Rollback did not persist the auto-update guard.");
            Console.WriteLine("PASS WPF pages, favorites, shortcuts, repair scheduling and rollback guard");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL UI smoke test: " + ex);
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                string details = ex.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
                Console.WriteLine("::error title=WPF smoke test failed::" + details);
            }
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool ContainsText(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsText(VisualTreeHelper.GetChild(root, i), text)) return true;
        return false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
