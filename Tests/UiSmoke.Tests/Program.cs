using System.Reflection;
using System.Runtime.Versioning;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
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
            var app = new App();
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

            // Loading the real BAML catches errors in names, controls, resources and event wiring.
            Mark("Loading WPF windows");
            var menu = new LaunchMenuDialog();
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
            // Exercise templates and bindings without showing an interactive window.
            var visual = (UIElement)favorites.Content;
            visual.Measure(new Size(900, 650));
            visual.Arrange(new Rect(0, 0, 900, 650));
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(900, 650, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Assert(ContainsText(visual, "Alpha game"), "A favorite row did not render its bound name.");
            // An old username label renders as disconnected, without inventing a login.
            var refresh = (Task)typeof(AccountsWindow).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(accounts, null)!;
            refresh.GetAwaiter().GetResult();
            var accountList = (ItemsControl)accounts.FindName("AccountsList");
            Assert(accountList.Items.Count == 1, "The account card was not created.");
            var accountVisual = (UIElement)accounts.Content!;
            accountVisual.Measure(new Size(860, 660));
            accountVisual.Arrange(new Rect(0, 0, 860, 660));
            accountVisual.UpdateLayout();
            new RenderTargetBitmap(860, 660, 96, 96, PixelFormats.Pbgra32).Render(accountVisual);
            Assert(ContainsText(accountVisual, "@Example_User") && ContainsText(accountVisual, "Sign in required on this PC"),
                "The migrated account did not render as disconnected.");
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
            var sessionsType = typeof(App).Assembly.GetType("Bloxstrap.AccountSessions", throwOnError: true)!;
            var requireSaved = sessionsType.GetMethod("RequireSaved", BindingFlags.Static | BindingFlags.Public)!;
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

    private static int VerifyWebViewLoader()
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (String.IsNullOrEmpty(version))
                Console.WriteLine("SKIP WebView2 loader smoke: the runtime is not installed.");
            else
                Console.WriteLine("PASS single-file WebView2 loader; runtime " + version);
            return 0;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("SKIP WebView2 loader smoke: the runtime is not installed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL single-file WebView2 loader: " + ex);
            return 1;
        }
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static void VerifyPrivateWebViews(RobloxLoginWindow first, string root)
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (String.IsNullOrEmpty(version))
            {
                Console.WriteLine("SKIP WebView2 runtime test: runtime is not installed.");
                return;
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("SKIP WebView2 runtime test: runtime is not installed.");
            return;
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

        RobloxLoginWindow? second = null;
        string? firstDir = null;
        string? secondDir = null;
        try
        {
            Mark("Opening a plain WPF window");
            var probe = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            probe.Show();
            probe.Close();
            Mark("Plain WPF window closed");
            Mark("Opening the first WebView2 window");
            first.SourceInitialized += (_, _) => Mark("First WebView2 window source initialized");
            var firstStatus = (TextBlock)first.FindName("StatusText");
            var firstReady = (Wpf.Ui.Controls.Button)first.FindName("SaveButton");
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(firstStatus, (_, _) => Mark("Sign-in: " + firstStatus.Text + "; button enabled=" + firstReady.IsEnabled));
            firstReady.IsEnabledChanged += (_, _) => Mark("Sign-in button enabled=" + firstReady.IsEnabled);
            var firstControl = (WebView2)first.FindName("Browser");
            firstControl.Loaded += (_, _) => Mark("First WebView2 control loaded");
            BlockNavigation(firstControl);
            first.Show();
            Mark("First WebView2 window shown; waiting for browser");
            WaitForBrowser(first);
            Mark("First WebView2 initialized");
            var firstBrowser = (WebView2)first.FindName("Browser");
            var firstWeb = firstBrowser.CoreWebView2;
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
            Assert(Pump(firstWeb.CookieManager.GetCookiesAsync("https://www.roblox.com/")).Any(x => x.Name == ".ROBLOSECURITY" && x.Value == "synthetic-test-session"),
                "The isolated sign-in cookie could not be read.");

            Mark("Opening the second WebView2 window");
            second = new RobloxLoginWindow();
            BlockNavigation((WebView2)second.FindName("Browser"));
            second.Show();
            WaitForBrowser(second);
            Mark("Second WebView2 initialized");
            var secondWeb = ((WebView2)second.FindName("Browser")).CoreWebView2;
            secondWeb.Stop();
            secondDir = Path.GetFullPath(secondWeb.Environment.UserDataFolder);
            Assert(secondWeb.Profile.IsInPrivateModeEnabled &&
                !secondDir.Equals(firstDir, StringComparison.OrdinalIgnoreCase),
                "Two sign-in windows shared a browser profile.");
            Assert(!Pump(secondWeb.CookieManager.GetCookiesAsync("https://www.roblox.com/")).Any(x => x.Name == ".ROBLOSECURITY"),
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
            Pump(Task.Delay(200));
        Assert(!Directory.Exists(firstDir) && !Directory.Exists(secondDir), "Closed WebView2 profiles were not cleaned up.");
        Console.WriteLine("PASS isolated InPrivate WebView2 sign-in profiles and cleanup");
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
    private static void WaitForBrowser(RobloxLoginWindow window)
    {
        Mark("Entering WebView2 readiness check");
        var ready = (Wpf.Ui.Controls.Button)window.FindName("SaveButton");
        var status = (TextBlock)window.FindName("StatusText");
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(45) };
        timer.Tick += (_, _) => frame.Continue = false;
        DependencyPropertyChangedEventHandler onReady = (_, _) =>
        {
            if (ready.IsEnabled)
            {
                Mark("WebView2 ready; ending dispatcher wait");
                frame.Continue = false;
            }
        };
        EventHandler onStatus = (_, _) =>
        {
            if (status.Text.StartsWith("Could not start", StringComparison.Ordinal)) frame.Continue = false;
        };
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        ready.IsEnabledChanged += onReady;
        descriptor.AddValueChanged(status, onStatus);
        Mark("WebView2 readiness observers attached; enabled=" + ready.IsEnabled);
        try
        {
            if (!ready.IsEnabled && !status.Text.StartsWith("Could not start", StringComparison.Ordinal))
            {
                timer.Start();
                Dispatcher.PushFrame(frame);
                Mark("WebView2 dispatcher wait ended");
            }
        }
        finally
        {
            timer.Stop();
            ready.IsEnabledChanged -= onReady;
            descriptor.RemoveValueChanged(status, onStatus);
        }
        Assert(ready.IsEnabled, "WebView2 sign-in failed to initialize: " + status.Text);
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
