using System.Reflection;
using System.Runtime.Versioning;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using BuffStrap.Favorites;
using BuffStrap.Accounts;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.UI.Elements.Dialogs;
using Bloxstrap.UI.Elements.Settings.Pages;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        if (Environment.GetEnvironmentVariable("BUFFSTRAP_TEST_WEBVIEW2_LOADER_ONLY") == "1")
            return VerifyWebViewLoader();

        string root = Path.Combine(Path.GetTempPath(), "BuffStrap-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                throw new PlatformNotSupportedException("The account sign-in test requires Windows 10 version 1809 or newer.");
            var paths = typeof(App).Assembly.GetType("Bloxstrap.Paths", throwOnError: true)!;
            paths.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { root });
            App.Settings.Prop.Theme = Theme.Dark;
            // The test pumps a dispatcher, but must never run the real
            // installer/launcher startup on the runner's Windows profile.
            var app = CreateAppWithoutStartup();
            app.InitializeComponent();
            // The production handler opens a modal dialog, which cannot be dismissed
            // on a headless runner. Fail the test instead of hanging indefinitely.
            var globalHandler = typeof(App).GetMethod("GlobalExceptionHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
            app.DispatcherUnhandledException -= (DispatcherUnhandledExceptionEventHandler)Delegate.CreateDelegate(typeof(DispatcherUnhandledExceptionEventHandler), app, globalHandler);
            app.DispatcherUnhandledException += (_, args) =>
            {
                string details = args.Exception.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
                Console.WriteLine("::error title=Unhandled WPF exception::" + details);
                Environment.Exit(1);
            };
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var launchSettings = new LaunchSettings(Array.Empty<string>());
            typeof(App).GetProperty("LaunchSettings")!.GetSetMethod(true)!.Invoke(null, new object[] { launchSettings });

            var store = new FavoritesStore(Path.Combine(root, "Favorites.json"));
            var account = new FavoriteAccount { Username = "Example_User" };
            var first = new FavoriteGame { Name = "Alpha game", PlaceId = 123, Folder = "Friends", AccountId = account.Id };
            var second = new FavoriteGame { Name = "Beta game", PlaceId = 456, Folder = "Other" };
            store.Update(data => { data.Accounts.Add(account); data.Games.Add(first); data.Games.Add(second); });

            // A test build must not launch a different, older installed executable.
            var favoritesLauncher = typeof(App).Assembly.GetType("Bloxstrap.FavoritesLauncher", throwOnError: true)!;
            var prepareFavorite = favoritesLauncher.GetMethod("PrepareFavoriteLaunch", BindingFlags.Static | BindingFlags.NonPublic)!;
            ProcessStartInfo GetStartInfo(string name, params object[] args) => (ProcessStartInfo)favoritesLauncher
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
            var favoriteStart = GetStartInfo("FavoriteStartInfo", first.Id);
            var accountStart = GetStartInfo("AccountStartInfo", account.Id, 123L);
            var placeStart = GetStartInfo("PlaceStartInfo", 456L);
            foreach (var start in new[] { favoriteStart, accountStart, placeStart })
            {
                Assert(start.FileName == Environment.ProcessPath && start.FileName != Path.Combine(root, "BuffStrap.exe") && !start.UseShellExecute,
                    "A menu action would start the installed build instead of the running build.");
                Assert(start.ArgumentList.Last() == "-childlaunch", "The child process would repeat the launcher update check.");
            }
            var favoriteChild = new LaunchSettings(favoriteStart.ArgumentList.ToArray());
            var accountChild = new LaunchSettings(accountStart.ArgumentList.ToArray());
            var placeChild = new LaunchSettings(placeStart.ArgumentList.ToArray());
            Assert(favoriteChild.FavoriteFlag.Data == first.Id.ToString("D") && favoriteChild.ChildLaunchFlag.Active && favoriteChild.BypassUpdateCheck,
                "The favorite was not passed safely to the same build.");
            Assert(accountChild.AccountFlag.Data == account.Id.ToString("D") && accountChild.AccountPlaceFlag.Data == "123" && accountChild.ChildLaunchFlag.Active,
                "The account and game were not passed safely to the same build.");
            Assert(placeChild.RobloxLaunchMode == LaunchMode.Player && placeChild.RobloxLaunchArgs == "roblox://experiences/start?placeId=456" && placeChild.ChildLaunchFlag.Active,
                "An unassigned game did not target the Roblox Player.");

            // Loading the real BAML catches errors in names, controls, resources and event wiring.
            Mark("Loading WPF windows");
            var menu = new LaunchMenuDialog();
            var menuModel = (Bloxstrap.UI.ViewModels.Installer.LaunchMenuViewModel)menu.DataContext;
            string originalRef = App.BuildMetadata.CommitRef;
            string originalHash = App.BuildMetadata.CommitHash;
            try
            {
                App.BuildMetadata.CommitRef = "branch/test";
                App.BuildMetadata.CommitHash = "1234567890";
                Assert(menuModel.Version.Contains("test build 1234567", StringComparison.Ordinal), "A branch build was not identified as a test build.");
                App.BuildMetadata.CommitRef = "tag/v1.0.2";
                Assert(!menuModel.Version.Contains("test build", StringComparison.Ordinal), "A tagged build was incorrectly identified as a test build.");
            }
            finally
            {
                App.BuildMetadata.CommitRef = originalRef;
                App.BuildMetadata.CommitHash = originalHash;
            }
            VerifyLaunchMenu(menu, "dark");
            App.Settings.Prop.Theme = Theme.Light;
            var lightMenu = new LaunchMenuDialog();
            VerifyLaunchMenu(lightMenu, "light");
            App.Settings.Prop.Theme = Theme.Dark;
            menu.ApplyTheme();
            var favorites = new FavoritesWindow();
            var accounts = new AccountsWindow();
            var login = new RobloxLoginWindow();
            var editor = new FavoriteEditWindow(first, new[] { account });
            var shortcutDialog = new FavoriteShortcutWindow();
            var repair = new RepairWindow();
            var updates = new UpdateCenterPage();
            Assert(menu.Content is not null && accounts.Content is not null && login.Content is not null && editor.Content is not null && shortcutDialog.Content is not null && repair.Content is not null && updates.Content is not null,
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
            // Preview binding uses real metadata and a decoded icon; failures do
            // not remove the favorite or prevent Play. All data here is synthetic.
            object row = list.Items[0];
            var applyPreview = row.GetType().GetMethod("ApplyPreview")!;
            applyPreview.Invoke(row, new object?[] { null });
            Assert(((string)row.GetType().GetProperty("Description")!.GetValue(row)!).Contains("unavailable"), "Offline preview had no useful fallback.");
            var mockPreview = new RobloxGamePreview
            {
                PlaceId = first.PlaceId, Name = "Alpha game", Description = "Explore a world with your friends.",
                Creator = "Example Creator", Playing = 1234, DetailsAvailable = true,
                IconPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")
            };
            applyPreview.Invoke(row, new object[] { mockPreview });
            Assert(row.GetType().GetProperty("Icon")!.GetValue(row) is BitmapSource &&
                (string)row.GetType().GetProperty("Description")!.GetValue(row)! == mockPreview.Description,
                "The public game icon or description was not applied to the favorite.");
            // Exercise templates and bindings without showing an interactive window.
            var visual = (UIElement)favorites.Content;
            visual.Measure(new Size(980, 720));
            visual.Arrange(new Rect(0, 0, 980, 720));
            visual.UpdateLayout();
            new RenderTargetBitmap(980, 720, 96, 96, PixelFormats.Pbgra32).Render(visual);
            Assert(ContainsText(visual, "Alpha game") && ContainsText(visual, mockPreview.Description),
                "A favorite row did not render its bound title and description.");
            SaveWindowPreview(favorites, visual, 980, 720, "favorites-dark.png");
            // An old username label renders as disconnected, without inventing a login or selection.
            var refresh = (Task)typeof(AccountsWindow).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(accounts, null)!;
            refresh.GetAwaiter().GetResult();
            var accountList = (ListBox)accounts.FindName("AccountsList");
            Assert(accountList.Items.Count == 1 && accountList.SelectedItem is null, "The account list invented a selected account.");
            var accountVisual = (UIElement)accounts.Content!;
            accountVisual.Measure(new Size(620, 730));
            accountVisual.Arrange(new Rect(0, 0, 620, 730));
            accountVisual.UpdateLayout();
            new RenderTargetBitmap(620, 730, 96, 96, PixelFormats.Pbgra32).Render(accountVisual);
            Assert(ContainsText(accountVisual, "@Example_User") && ContainsText(accountVisual, "Sign in required on this PC"),
                "The migrated account did not render as disconnected.");
            SaveWindowPreview(accounts, accountVisual, 620, 730, "accounts-dark.png");
            accountList.SelectedIndex = 0;
            Assert(!((Wpf.Ui.Controls.Button)accounts.FindName("PlayAccountButton")).IsEnabled &&
                ((Wpf.Ui.Controls.Button)accounts.FindName("ReconnectButton")).IsEnabled &&
                ((TextBlock)accounts.FindName("SelectionHint")).Text.Contains("Selected for this launch"),
                "A disconnected account could play, or the selection was misrepresented.");
            var browser = login.FindName("Browser")!;
            Assert(browser.GetType().GetProperty("Source")!.GetValue(browser) is null,
                "The login browser navigated before its isolated profile was configured.");
            if (Environment.GetEnvironmentVariable("BUFFSTRAP_TEST_WEBVIEW2") == "1")
                VerifyPrivateWebViews(login, root);
            else
                Console.WriteLine("SKIP WebView2 runtime test: set BUFFSTRAP_TEST_WEBVIEW2=1 in an interactive Windows session.");
            Assert(((TextBox)editor.FindName("NameBox")).Text == "Alpha game", "The favorite editor did not load the game.");
            var gameShortcutType = typeof(App).Assembly.GetType("Bloxstrap.Utility.GameShortcut", throwOnError: true)!;
            var parsePlace = gameShortcutType.GetMethod("ParsePlaceId", BindingFlags.Public | BindingFlags.Static)!;
            Assert((long?)parsePlace.Invoke(null, new object[] { "https://www.roblox.com/games/1234/example" }) == 1234,
                "A Roblox game link did not resolve to its place ID.");
            var versionOption = (Bloxstrap.UI.Elements.Controls.OptionControl)updates.FindName("InstalledVersionOption");
            Assert(((TextBlock)versionOption.InnerContent).Text.StartsWith("v"), "Update Center did not show the version.");

            Mark("Checking DPAPI and account locking");
            // Real Windows DPAPI protects sessions and binds each ciphertext to its account ID.
            var vaultType = typeof(App).Assembly.GetType("Bloxstrap.AccountSessionVault", throwOnError: true)!;
            var vault = Activator.CreateInstance(vaultType, new object[] { root })!;
            var saveSession = vaultType.GetMethod("Save")!;
            var readSession = vaultType.GetMethod("Read")!;
            const string secret = "_|WARNING:-DO-NOT-SHARE-THIS.example-secret";
            saveSession.Invoke(vault, new object[] { account.Id, secret });
            // A selected account becomes playable only after public identity and
            // a local encrypted session both exist; selection alone is not login.
            var accountCard = accountList.Items[0];
            var displayedAccount = (FavoriteAccount)accountCard.GetType().GetProperty("Account")!.GetValue(accountCard)!;
            displayedAccount.UserId = 42;
            typeof(AccountsWindow).GetMethod("UpdateSelection", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(accounts, null);
            Assert(((Wpf.Ui.Controls.Button)accounts.FindName("PlayAccountButton")).IsEnabled,
                "A selected account with a saved session could not be launched.");
            var sessionsType = typeof(App).Assembly.GetType("Bloxstrap.AccountSessions", throwOnError: true)!;
            var requireSaved = sessionsType.GetMethod("RequireSaved", BindingFlags.Static | BindingFlags.Public)!;
            try
            {
                prepareFavorite.Invoke(null, new object[] { first.Id });
                throw new InvalidOperationException("An account without a connected session was allowed to play.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is RobloxAccountException) { }
            Assert(launchSettings.SelectedAccountId is null && launchSettings.RobloxLaunchArgs == "",
                "A disconnected favorite silently fell back to another account or a browser.");
            try
            {
                requireSaved.Invoke(null, new object[] { account.Id });
                throw new InvalidOperationException("A legacy account label was treated as a login.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is BuffStrap.Accounts.RobloxAccountException) { }
            store.Update(data => { data.Accounts.Single().UserId = 42; data.Accounts.Single().DisplayName = "Example Display"; });
            requireSaved.Invoke(null, new object[] { account.Id });
            string encryptedPath = Path.Combine(root, "AccountSessions", account.Id.ToString("N") + ".session");
            byte[] encrypted = File.ReadAllBytes(encryptedPath);
            Assert(encrypted.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(secret)) < 0, "The session was stored in plaintext.");
            Assert((string)readSession.Invoke(vault, new object[] { account.Id })! == secret, "DPAPI session round trip failed.");
            Guid otherId = Guid.NewGuid();
            File.WriteAllBytes(Path.Combine(root, "AccountSessions", otherId.ToString("N") + ".session"), encrypted);
            try
            {
                readSession.Invoke(vault, new object[] { otherId });
                throw new InvalidOperationException("A session was accepted under the wrong account ID.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is BuffStrap.Accounts.RobloxAccountException) { }
            var lockMethod = vaultType.GetMethod("LockAsync")!;
            using (var held = ((Task<FileStream>)lockMethod.Invoke(vault, new object[] { account.Id, CancellationToken.None })!).GetAwaiter().GetResult())
            {
                var waiting = (Task<FileStream>)lockMethod.Invoke(vault, new object[] { account.Id, CancellationToken.None })!;
                Assert(!waiting.IsCompleted, "Two processes could rotate the same account concurrently.");
                held.Dispose();
                using var released = waiting.GetAwaiter().GetResult();
            }
            Mark("Checking the account service with a synthetic Roblox API");
            // End-to-end account service with a fake Roblox API, the real vault,
            // and the real favorites store: reconnecting keeps favorite IDs intact.
            const string loginSession = "synthetic-signin-session";
            using (var signInHttp = new HttpClient(new CallbackHandler(request =>
            {
                Assert(request.RequestUri!.Host == "users.roblox.com" && request.Headers.GetValues("Cookie").Single() == ".ROBLOSECURITY=" + loginSession,
                    "Sign-in used the wrong session or host.");
                return JsonReply(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Example_User\",\"displayName\":\"Updated Display\"}");
            })))
            {
                var connect = sessionsType.GetMethod("ConnectAsync", BindingFlags.Static | BindingFlags.Public)!;
                Pump((Task)connect.Invoke(null, new object?[] { loginSession, account.Id, CancellationToken.None, new RobloxAccountClient(signInHttp) })!);
            }
            Assert(store.Load().Accounts.Single().DisplayName == "Updated Display" && store.Load().Games.Single(x => x.Id == first.Id).AccountId == account.Id,
                "Reconnecting lost account identity or favorite assignments.");
            Assert((string)readSession.Invoke(vault, new object[] { account.Id })! == loginSession &&
                !File.ReadAllText(Path.Combine(root, "Favorites.json")).Contains(loginSession), "The session was not kept in the protected vault.");
            int authSteps = 0;
            using (var launchHttp = new HttpClient(new CallbackHandler(request =>
            {
                authSteps++;
                if (authSteps == 1)
                {
                    Assert(request.Headers.GetValues("Cookie").Single() == ".ROBLOSECURITY=" + loginSession, "The selected account session was not used.");
                    return JsonReply(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Example_User\",\"displayName\":\"Updated Display\"}", "rotated-login-session");
                }
                if (authSteps == 2)
                {
                    Assert(request.Headers.GetValues("Cookie").Single() == ".ROBLOSECURITY=rotated-login-session", "Rotated session was not used.");
                    return JsonReply(HttpStatusCode.OK, "{\"clientAssertion\":\"synthetic-assertion\"}");
                }
                if (authSteps == 3)
                {
                    var challenge = JsonReply(HttpStatusCode.Forbidden, "{}");
                    challenge.Headers.TryAddWithoutValidation("x-csrf-token", "synthetic-csrf");
                    return challenge;
                }
                if (authSteps == 4)
                {
                    var response = JsonReply(HttpStatusCode.OK, "{}", "rotated-ticket-session");
                    response.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "synthetic-ticket");
                    return response;
                }
                throw new InvalidOperationException("Unexpected authenticated request.");
            })))
            {
                var mint = sessionsType.GetMethod("CreateLaunchTicketAsync", BindingFlags.Static | BindingFlags.Public)!;
                string ticket = Pump((Task<string>)mint.Invoke(null, new object?[] { account.Id, CancellationToken.None, new RobloxAccountClient(launchHttp) })!);
                Assert(ticket == "synthetic-ticket" && authSteps == 4, "The account did not produce the expected ticket.");
            }
            Assert((string)readSession.Invoke(vault, new object[] { account.Id })! == "rotated-ticket-session", "The rotated session was not saved.");
            int wrongSteps = 0;
            using (var wrongHttp = new HttpClient(new CallbackHandler(request =>
            {
                wrongSteps++;
                return JsonReply(HttpStatusCode.OK, "{\"id\":99,\"name\":\"Other_User\",\"displayName\":\"Other\"}");
            })))
            {
                var mint = sessionsType.GetMethod("CreateLaunchTicketAsync", BindingFlags.Static | BindingFlags.Public)!;
                try
                {
                    Pump((Task<string>)mint.Invoke(null, new object?[] { account.Id, CancellationToken.None, new RobloxAccountClient(wrongHttp) })!);
                    throw new InvalidOperationException("A different account's session was allowed to launch.");
                }
                catch (RobloxAccountException) { }
                Assert(wrongSteps == 1, "A ticket was requested for the wrong account.");
            }

            prepareFavorite.Invoke(null, new object[] { first.Id });
            Assert(launchSettings.SelectedAccountId == account.Id && launchSettings.SelectedAccountPlaceId == 123 && launchSettings.RobloxLaunchArgs == "",
                "The first favorite did not choose its assigned account and game.");
            var otherAccount = new FavoriteAccount { Username = "Other_User", UserId = 99, DisplayName = "Other" };
            var otherFavorite = new FavoriteGame { Name = "Other game", PlaceId = 789, AccountId = otherAccount.Id };
            store.Update(data => { data.Accounts.Add(otherAccount); data.Games.Add(otherFavorite); });
            const string otherSession = "synthetic-other-account-session";
            saveSession.Invoke(vault, new object[] { otherAccount.Id, otherSession });
            prepareFavorite.Invoke(null, new object[] { otherFavorite.Id });
            Assert(launchSettings.SelectedAccountId == otherAccount.Id && launchSettings.SelectedAccountPlaceId == 789 && launchSettings.RobloxLaunchArgs == "",
                "The assigned favorite did not choose its own account and game.");
            int otherSteps = 0;
            using (var otherHttp = new HttpClient(new CallbackHandler(request =>
            {
                otherSteps++;
                Assert(request.Headers.GetValues("Cookie").Single() == ".ROBLOSECURITY=" + otherSession,
                    "A different account's session was used for the favorite.");
                if (otherSteps == 1) return JsonReply(HttpStatusCode.OK, "{\"id\":99,\"name\":\"Other_User\",\"displayName\":\"Other\"}");
                if (otherSteps == 2) return JsonReply(HttpStatusCode.OK, "{\"clientAssertion\":\"other-assertion\"}");
                if (otherSteps == 3)
                {
                    var challenge = JsonReply(HttpStatusCode.Forbidden, "{}");
                    challenge.Headers.TryAddWithoutValidation("x-csrf-token", "other-csrf");
                    return challenge;
                }
                if (otherSteps == 4)
                {
                    var response = JsonReply(HttpStatusCode.OK, "{}");
                    response.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "other-ticket");
                    return response;
                }
                throw new InvalidOperationException("Unexpected selected-account request.");
            })))
            {
                var mint = sessionsType.GetMethod("CreateLaunchTicketAsync", BindingFlags.Static | BindingFlags.Public)!;
                string ticket = Pump((Task<string>)mint.Invoke(null, new object?[] { otherAccount.Id, CancellationToken.None, new RobloxAccountClient(otherHttp) })!);
                string directUri = RobloxAccountClient.BuildPlayerUri(ticket, launchSettings.SelectedAccountPlaceId!.Value, 1234567890, DateTimeOffset.UtcNow, Guid.NewGuid());
                var selectedJoin = new GameJoin().GetJoinDataByLaunchCommand(directUri);
                Assert(otherSteps == 4 && selectedJoin.JoinType == GameJoinType.RequestGame && selectedJoin.PlaceId == 789 &&
                    directUri.StartsWith("roblox-player:1+launchmode:play+gameinfo:other-ticket+", StringComparison.Ordinal) && !directUri.Contains(otherSession),
                    "The chosen account and game did not produce a direct Roblox Player launch.");
            }
            prepareFavorite.Invoke(null, new object[] { second.Id });
            Assert(launchSettings.SelectedAccountId is null && launchSettings.SelectedAccountPlaceId is null &&
                launchSettings.RobloxLaunchArgs == "roblox://experiences/start?placeId=456",
                "An unassigned favorite retained a previous account or failed to target the Player.");

            var redact = typeof(Logger).GetMethod("RedactSecrets", BindingFlags.Static | BindingFlags.NonPublic)!;
            string redacted = (string)redact.Invoke(null, new object[] { "roblox-player:1+gameinfo:ticket-secret+foo .ROBLOSECURITY=cookie-secret; x-csrf-token: csrf-secret" })!;
            Assert(!redacted.Contains("ticket-secret") && !redacted.Contains("cookie-secret") && !redacted.Contains("csrf-secret"),
                "Recognizable credentials reached the logger.");
            var accountArgs = new LaunchSettings(new[] { "-account", account.Id.ToString("D"), "-accountplace", "123" });
            Assert(accountArgs.AccountFlag.Data == account.Id.ToString("D") && accountArgs.AccountPlaceFlag.Data == "123",
                "The selected account launch arguments did not parse.");
            string accountUri = RobloxAccountClient.BuildPlayerUri("synthetic-ticket", 123, 1234567890,
                DateTimeOffset.UtcNow, Guid.NewGuid());
            var join = new GameJoin().GetJoinDataByLaunchCommand(accountUri);
            Assert(join.JoinType == GameJoinType.RequestGame && join.PlaceId == 123 && join.JoinOrigin == "PlayButton",
                "The selected account launch could not be parsed by the existing join pipeline.");

            Mark("Checking shortcuts and repair");
            // Exercise the actual shortcut writer against a temporary path, never the runner's Desktop.
            var shortcutType = typeof(App).Assembly.GetType("Bloxstrap.Utility.FavoriteShortcut", throwOnError: true)!;
            var create = shortcutType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
            string linkPath = Path.Combine(root, "favorite.lnk");
            create.Invoke(null, new object[] { first, new List<string> { linkPath }, false });
            var link = ShellLink.Shortcut.ReadFromFile(linkPath);
            Assert(link.StringData.CommandLineArguments == $"-favorite {first.Id:D} -childlaunch", "Shortcut did not target the saved favorite safely.");
            Assert(link.ExtraData.EnvironmentVariableDataBlock.TargetUnicode == Environment.ProcessPath, "Shortcut pointed to the older installed launcher instead of the running build.");
            var shortcutSettings = new LaunchSettings(link.StringData.CommandLineArguments.Split(' '));
            Assert(shortcutSettings.FavoriteFlag.Data == first.Id.ToString("D") && shortcutSettings.ChildLaunchFlag.Active && shortcutSettings.BypassUpdateCheck,
                "The shortcut could trigger an installation or lose its favorite ID.");
            byte[] originalLink = File.ReadAllBytes(linkPath);
            try
            {
                create.Invoke(null, new object[] { first, new List<string> { linkPath }, false });
                throw new InvalidOperationException("A shortcut was overwritten without consent.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            Assert(File.ReadAllBytes(linkPath).SequenceEqual(originalLink), "The existing shortcut was changed without consent.");
            create.Invoke(null, new object[] { first, new List<string> { linkPath }, true });
            Assert(ShellLink.Shortcut.ReadFromFile(linkPath).StringData.CommandLineArguments == $"-favorite {first.Id:D} -childlaunch",
                "Consented shortcut replacement failed.");
            var getPaths = shortcutType.GetMethod("GetPaths", BindingFlags.Public | BindingFlags.Static)!;
            var choices = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, true, true })!;
            var desktop = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, true, false })!;
            var startMenu = (IReadOnlyList<string>)getPaths.Invoke(null, new object[] { first, false, true })!;
            Assert(choices.Count == 2 && choices[0] == desktop.Single() && choices[1] == startMenu.Single() && choices[0] != choices[1] &&
                choices.All(x => Path.GetFileName(x) == "Alpha game.lnk"),
                "Shortcut locations or the plain favorite name were incorrect.");
            var available = shortcutType.GetMethod("GetAvailablePath", BindingFlags.Public | BindingFlags.Static)!;
            string Available(FavoriteGame game) => (string)available.Invoke(null, new object[] { root, game })!;
            Assert(Path.GetFileName(Available(new FavoriteGame { Name = "CON", PlaceId = 1 })) == "_CON.lnk" &&
                Path.GetFileName(Available(new FavoriteGame { Name = "A:B?", PlaceId = 1 })) == "A_B_.lnk",
                "Windows-invalid or reserved shortcut names were not made safe.");
            string unrelated = Path.Combine(root, "Collision.lnk");
            File.WriteAllText(unrelated, "unrelated user file");
            var collision = new FavoriteGame { Name = "Collision", PlaceId = 123 };
            string collisionPath = Available(collision);
            Assert(Path.GetFileName(collisionPath) == "Collision (2).lnk", "An unrelated file was not avoided.");
            create.Invoke(null, new object[] { collision, new List<string> { collisionPath }, false });
            Assert(Available(collision) == collisionPath, "An existing shortcut for the same favorite was not found.");
            var duplicate = new FavoriteGame { Name = "Collision", PlaceId = 456 };
            Assert(Path.GetFileName(Available(duplicate)) == "Collision (3).lnk", "Another favorite could overwrite the first one's shortcut.");
            try
            {
                create.Invoke(null, new object[] { collision, new List<string> { unrelated }, true });
                throw new InvalidOperationException("A different file was overwritten after consent to update a shortcut.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            Assert(File.ReadAllText(unrelated) == "unrelated user file", "An unrelated file was modified.");

            string oldLink = Path.Combine(root, $"Old name (BuffStrap-{first.Id:N}).lnk");
            string impostor = Path.Combine(root, $"Unrelated (BuffStrap-{first.Id:N}).lnk");
            ShellLink.Shortcut.CreateShortcut(Path.Combine(root, "BuffStrap.exe"), $"-favorite {first.Id:D}", Path.Combine(root, "BuffStrap.exe"), 0).WriteToFile(oldLink);
            ShellLink.Shortcut.CreateShortcut(Path.Combine(root, "Unrelated.exe"), $"-favorite {first.Id:D}", Path.Combine(root, "Unrelated.exe"), 0).WriteToFile(impostor);
            var findOld = shortcutType.GetMethod("FindLegacyPaths", BindingFlags.Public | BindingFlags.Static)!;
            var removeOld = shortcutType.GetMethod("RemoveLegacyPaths", BindingFlags.Public | BindingFlags.Static)!;
            var oldLinks = (IReadOnlyList<string>)findOld.Invoke(null, new object[] { first, new List<string> { linkPath } })!;
            Assert(oldLinks.Count == 1 && oldLinks.Single() == oldLink, "Legacy migration included an unrelated shortcut or missed an old one.");
            int removed = (int)removeOld.Invoke(null, new object[] { first, new List<string> { oldLink, impostor } })!;
            Assert(removed == 1 && !File.Exists(oldLink) && File.Exists(impostor), "Legacy cleanup touched an unrelated shortcut.");

            var repairType = typeof(App).Assembly.GetType("Bloxstrap.InstallationRepair", throwOnError: true)!;
            repairType.GetMethod("SchedulePlayerReinstall", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "State.json")));
            Assert(state.RootElement.GetProperty("ForcePlayerRepair").GetBoolean(), "The Player repair was not scheduled.");

            // A Studio or background update must not consume a Player-only repair.
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

    private static App CreateAppWithoutStartup()
    {
        // WPF Application's constructor queues its OnStartup callback at Send
        // priority. Capture and abort that operation before any dispatcher is
        // pumped, keeping the real App type and its compiled XAML resources.
        var dispatcher = Dispatcher.CurrentDispatcher;
        int threadId = Environment.CurrentManagedThreadId;
        var posted = new List<DispatcherOperation>();
        DispatcherHookEventHandler capture = (_, args) =>
        {
            if (Environment.CurrentManagedThreadId == threadId && args.Operation.Priority == DispatcherPriority.Send)
                posted.Add(args.Operation);
        };
        dispatcher.Hooks.OperationPosted += capture;
        App app;
        try { app = new App(); }
        finally { dispatcher.Hooks.OperationPosted -= capture; }
        if (posted.Count != 1 || !posted[0].Abort())
            throw new InvalidOperationException("Could not safely suppress the application's startup in the smoke test.");
        return app;
    }

    private static int VerifyWebViewLoader()
    {
        try
        {
            string? appBundle = Environment.GetEnvironmentVariable("BUFFSTRAP_APP_BUNDLE");
            if (!String.IsNullOrEmpty(appBundle))
                VerifyAppBundleHasWebViewLoader(appBundle);
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (String.IsNullOrEmpty(version))
                throw new InvalidOperationException("The WebView2 Runtime is required for the single-file loader test.");
            Console.WriteLine("PASS single-file test app WebView2 loader; runtime " + version);
            Mark("PASS single-file test app WebView2 loader resolved runtime " + version);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL single-file WebView2 loader: " + ex);
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                string details = ex.Message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
                Console.WriteLine("::error title=Single-file WebView2 check failed::" + details);
            }
            return 1;
        }
    }

    // Inspect the release executable's .NET bundle manifest, not merely the
    // publish directory: only BuffStrap.exe is uploaded as a release asset.
    // Format: https://github.com/dotnet/runtime/tree/v6.0.36/src/installer/managed/Microsoft.NET.HostModel/Bundle
    private static void VerifyAppBundleHasWebViewLoader(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] prefix = new byte[(int)Math.Min(stream.Length, 8L * 1024 * 1024)];
        int read = 0;
        while (read < prefix.Length)
        {
            int n = stream.Read(prefix, read, prefix.Length - read);
            if (n == 0) break;
            read += n;
        }
        // SHA-256 of ".net core bundle", as used by the .NET apphost.
        byte[] signature = Convert.FromHexString("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae");
        int marker = prefix.AsSpan(0, read).IndexOf(signature);
        if (marker < sizeof(long)) throw new InvalidDataException("The release app is not a recognized single-file bundle.");
        long headerOffset = BitConverter.ToInt64(prefix, marker - sizeof(long));
        if (headerOffset <= 0 || headerOffset >= stream.Length) throw new InvalidDataException("Invalid app bundle header.");
        stream.Position = headerOffset;
        using var reader = new BinaryReader(stream);
        uint major = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // minor
        int count = reader.ReadInt32();
        _ = reader.ReadString(); // bundle ID
        if ((major != 2 && major != 6) || count < 1 || count > 10000)
            throw new InvalidDataException("Unsupported app bundle manifest.");
        for (int i = 0; i < 5; i++) _ = reader.ReadInt64(); // config offsets/sizes and flags
        bool found = false;
        var candidates = new List<string>();
        for (int i = 0; i < count; i++)
        {
            long offset = reader.ReadInt64();
            long size = reader.ReadInt64();
            if (major >= 6) _ = reader.ReadInt64(); // compressed size
            byte type = reader.ReadByte();
            string name = reader.ReadString();
            if (name.EndsWith("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(name + " (type " + type + ")");
                if (name.Equals("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase) && type == 2 &&
                    offset > 0 && size > 0 && offset <= stream.Length - size)
                    found = true;
            }
        }
        Mark("App bundle WebView2 loader entries: " + (candidates.Count == 0 ? "none" : String.Join(", ", candidates)));
        Assert(found, "The released app bundle does not contain the native WebView2 loader at its root.");
        Mark("PASS release app bundle contains the native WebView2 loader");
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static void VerifyPrivateWebViews(RobloxLoginWindow first, string root)
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (String.IsNullOrEmpty(version))
                throw new InvalidOperationException("The WebView2 Runtime is required for the requested browser profile test.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException("The WebView2 Runtime is required for the requested browser profile test.");
        }

        // A native WebView2 or desktop message-pump stall must not leave this
        // optional test running indefinitely, even if the dispatcher stops ticking.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL WebView2 runtime test: the Windows UI thread did not finish within 90 seconds.");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Console.WriteLine("::error title=WebView2 runtime test timed out::The Windows UI thread did not finish within 90 seconds.");
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);

        // Run an ordinary dispatcher loop rather than blocking the UI thread in
        // nested frames while WebView2 is delivering COM callbacks.
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await VerifyPrivateWebViewsAsync(first, root); }
            catch (Exception ex)
            {
                failure = ex;
                Mark("WebView2 runtime test failed: " + ex.Message);
            }
            finally { Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static async Task VerifyPrivateWebViewsAsync(RobloxLoginWindow first, string root)
    {
        RobloxLoginWindow? second = null;
        string? firstDir = null;
        string? secondDir = null;
        try
        {
            Mark("Opening the first WebView2 window");
            var firstControl = (WebView2)first.FindName("Browser");
            BlockNavigation(firstControl);
            Task firstReady = WaitForBrowserAsync(first);
            first.Show();
            await firstReady;
            Mark("First WebView2 initialized");
            var firstWeb = firstControl.CoreWebView2;
            firstWeb.Stop();
            Assert(firstWeb.Profile.IsInPrivateModeEnabled, "Sign-in was not InPrivate.");
            Assert(!firstWeb.Settings.IsPasswordAutosaveEnabled && !firstWeb.Settings.IsGeneralAutofillEnabled,
                "Sign-in enabled browser password storage or autofill.");
            firstDir = Path.GetFullPath(firstWeb.Environment.UserDataFolder);
            Assert(firstDir.StartsWith(Path.Combine(root, "AccountLogin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Sign-in used the wrong browser profile directory.");
            var fakeCookie = firstWeb.CookieManager.CreateCookie(".ROBLOSECURITY", "synthetic-test-session", ".roblox.com", "/");
            fakeCookie.IsSecure = true;
            fakeCookie.IsHttpOnly = true;
            firstWeb.CookieManager.AddOrUpdateCookie(fakeCookie);
            var firstCookies = await firstWeb.CookieManager.GetCookiesAsync("https://www.roblox.com/").WaitAsync(TimeSpan.FromSeconds(15));
            Assert(firstCookies.Any(x => x.Name == ".ROBLOSECURITY" && x.Value == "synthetic-test-session"),
                "The isolated sign-in cookie could not be read.");

            Mark("Opening the second WebView2 window");
            second = new RobloxLoginWindow();
            var secondControl = (WebView2)second.FindName("Browser");
            BlockNavigation(secondControl);
            Task secondReady = WaitForBrowserAsync(second);
            second.Show();
            await secondReady;
            Mark("Second WebView2 initialized");
            var secondWeb = secondControl.CoreWebView2;
            secondWeb.Stop();
            secondDir = Path.GetFullPath(secondWeb.Environment.UserDataFolder);
            Assert(secondWeb.Profile.IsInPrivateModeEnabled &&
                !secondDir.Equals(firstDir, StringComparison.OrdinalIgnoreCase),
                "Two sign-in windows shared a browser profile.");
            var secondCookies = await secondWeb.CookieManager.GetCookiesAsync("https://www.roblox.com/").WaitAsync(TimeSpan.FromSeconds(15));
            Assert(!secondCookies.Any(x => x.Name == ".ROBLOSECURITY"),
                "One sign-in window leaked its cookie to another.");
        }
        finally
        {
            second?.Close();
            first.Close();
        }
        Mark("Waiting for WebView2 profile cleanup");
        DateTime cleanupDeadline = DateTime.UtcNow.AddSeconds(10);
        while ((Directory.Exists(firstDir) || Directory.Exists(secondDir)) && DateTime.UtcNow < cleanupDeadline)
            await Task.Delay(200);
        Assert(!Directory.Exists(firstDir) && !Directory.Exists(secondDir), "Closed WebView2 profiles were not cleaned up.");
        Console.WriteLine("PASS isolated InPrivate WebView2 sign-in profiles and cleanup");
        Mark("PASS isolated InPrivate WebView2 profiles and cleanup");
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static void BlockNavigation(WebView2 control)
    {
        // This test exercises profile isolation with synthetic cookies only; it
        // must not navigate to Roblox or depend on an external network.
        control.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
                control.CoreWebView2.NavigationStarting += (_, navigation) => navigation.Cancel = true;
        };
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static async Task WaitForBrowserAsync(RobloxLoginWindow window)
    {
        var ready = (Wpf.Ui.Controls.Button)window.FindName("SaveButton");
        var status = (TextBlock)window.FindName("StatusText");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DependencyPropertyChangedEventHandler onReady = (_, _) =>
        {
            if (ready.IsEnabled) completion.TrySetResult(true);
        };
        EventHandler onStatus = (_, _) =>
        {
            if (status.Text.StartsWith("Could not start", StringComparison.Ordinal))
                completion.TrySetException(new InvalidOperationException("WebView2 sign-in failed to initialize: " + status.Text));
        };
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        ready.IsEnabledChanged += onReady;
        descriptor.AddValueChanged(status, onStatus);
        try
        {
            if (ready.IsEnabled) return;
            if (status.Text.StartsWith("Could not start", StringComparison.Ordinal))
                throw new InvalidOperationException("WebView2 sign-in failed to initialize: " + status.Text);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(40));
        }
        finally
        {
            ready.IsEnabledChanged -= onReady;
            descriptor.RemoveValueChanged(status, onStatus);
        }
    }

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timer.Tick += (_, _) => frame.Continue = false;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted)
        {
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        if (!task.IsCompleted) throw new TimeoutException("An asynchronous UI test timed out.");
        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static HttpResponseMessage JsonReply(HttpStatusCode status, string json, string? rotatedSession = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        if (rotatedSession is not null)
            response.Headers.TryAddWithoutValidation("Set-Cookie", ".ROBLOSECURITY=" + rotatedSession + "; Path=/; Secure; HttpOnly");
        return response;
    }

    private sealed class CallbackHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        public CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_send(request));
        }
    }

    private static void VerifyLaunchMenu(LaunchMenuDialog menu, string theme)
    {
        var visual = (UIElement)menu.Content;
        visual.Measure(new Size(580, Double.PositiveInfinity));
        double height = Math.Ceiling(visual.DesiredSize.Height);
        Assert(height > 0 && height < 500, "The launch menu did not fit its compact layout.");
        visual.Arrange(new Rect(0, 0, 580, height));
        visual.UpdateLayout();

        var launch = (Wpf.Ui.Controls.CardAction)menu.FindName("LaunchRobloxAction");
        var settings = (Wpf.Ui.Controls.CardAction)menu.FindName("LaunchSettingsAction");
        var accounts = (Wpf.Ui.Controls.Button)menu.FindName("AccountsButton");
        var favorites = (Wpf.Ui.Controls.Button)menu.FindName("FavoritesButton");
        Assert(launch.Command is not null && settings.Command is not null && accounts.Command is not null && favorites.Command is not null,
            "A launch menu action was not bound.");
        Assert(accounts.Content is null && favorites.Content is null &&
            accounts.Icon == Wpf.Ui.Common.SymbolRegular.Person24 && favorites.Icon == Wpf.Ui.Common.SymbolRegular.Star24,
            "The account and favorites controls must be icon-only buttons.");
        Assert((string)accounts.ToolTip == "Accounts" && (string)favorites.ToolTip == "Favorites" &&
            AutomationProperties.GetName(accounts) == "Accounts" && AutomationProperties.GetName(favorites) == "Favorites",
            "The icon buttons need tooltips and accessible names.");
        Assert(launch.TabIndex == 0 && settings.TabIndex == 1 && accounts.TabIndex == 2 && favorites.TabIndex == 3,
            "The launch menu keyboard order changed.");

        // Optionally retain the actual WPF render for visual review in Windows CI.
        string? snapshotDirectory = Environment.GetEnvironmentVariable("BUFFSTRAP_MENU_SNAPSHOT_DIR");
        if (String.IsNullOrEmpty(snapshotDirectory)) return;
        Directory.CreateDirectory(snapshotDirectory);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, 580, height);
            context.DrawRectangle(menu.Background ?? Brushes.Black, null, bounds);
            context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        var bitmap = new RenderTargetBitmap(580, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(snapshotDirectory, "launch-menu-" + theme + ".png"));
        encoder.Save(output);
    }

    private static void SaveWindowPreview(Window window, UIElement visual, int width, int height, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("BUFFSTRAP_MENU_SNAPSHOT_DIR");
        if (String.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            context.DrawRectangle(window.Background ?? Brushes.Black, null, bounds);
            context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static void Mark(string stage)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine("::notice title=Windows smoke test::" + stage);
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
