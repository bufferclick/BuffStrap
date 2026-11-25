using BuffStrap.Accounts;
using Microsoft.Web.WebView2.Core;
using System.Runtime.Versioning;
using System.Windows;
using Bloxstrap.UI.Elements.Base;

namespace Bloxstrap.UI.Elements.Dialogs
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public partial class RobloxLoginWindow : WpfUiWindow
    {
        private readonly Guid? _expectedId;
        private readonly CancellationTokenSource _cancel = new();
        private readonly string _profileDirectory = Path.Combine(Paths.Base, "AccountLogin", Guid.NewGuid().ToString("N"));
        private bool _closed;
        private bool _saving;

        public RobloxLoginWindow(Guid? expectedId = null)
        {
            _expectedId = expectedId;
            InitializeComponent();
            Loaded += InitializeBrowser;
            Closed += CloseBrowser;
        }

        private async void InitializeBrowser(object sender, RoutedEventArgs e)
        {
            try
            {
                CleanupOldProfiles();
                var environment = await CoreWebView2Environment.CreateAsync(null, _profileDirectory);
                if (_closed) return;
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                await Browser.EnsureCoreWebView2Async(environment, options);
                if (_closed) return;
                var web = Browser.CoreWebView2;
                web.Settings.AreDevToolsEnabled = false;
                web.Settings.AreDefaultContextMenusEnabled = false;
                web.Settings.IsPasswordAutosaveEnabled = false;
                web.Settings.IsGeneralAutofillEnabled = false;
                web.NavigationStarting += (_, args) =>
                {
                    if (!RobloxAccountClient.IsRobloxLoginUrl(args.Uri))
                    {
                        args.Cancel = true;
                        StatusText.Text = "For safety, this sign-in window only opens Roblox pages.";
                    }
                    else
                    {
                        StatusText.Text = "Official Roblox page • Sign in, then click I've signed in.";
                    }
                };
                web.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    if (RobloxAccountClient.IsRobloxLoginUrl(args.Uri)) web.Navigate(args.Uri);
                    else StatusText.Text = "For safety, this sign-in window only opens Roblox pages.";
                };
                web.Navigate("https://www.roblox.com/login");
                SaveButton.IsEnabled = true;
                StatusText.Text = "Official Roblox page • Sign in, then click I've signed in.";
            }
            catch
            {
                if (!_closed)
                    StatusText.Text = "Could not start the sign-in window. Install or repair the Microsoft Edge WebView2 Runtime, then try again.";
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_saving || _closed || Browser.CoreWebView2 is null) return;
            _saving = true;
            SaveButton.IsEnabled = false;
            StatusText.Text = "Checking this Roblox account...";
            try
            {
                if (!RobloxAccountClient.IsRobloxLoginUrl(Browser.CoreWebView2.Source))
                    throw new RobloxAccountException("Return to the official Roblox page and finish signing in first.");
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com/");
                var sessions = cookies.Where(x => x.Name == ".ROBLOSECURITY" && x.IsSecure && x.Path == "/" &&
                    (x.Domain.Equals(".roblox.com", StringComparison.OrdinalIgnoreCase) ||
                     x.Domain.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
                     x.Domain.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase)))
                    .Select(x => x.Value).Where(RobloxAccountClient.IsValidSession).Distinct(StringComparer.Ordinal).ToList();
                if (sessions.Count != 1)
                    throw new RobloxAccountException("No single Roblox login was found. Finish signing in and complete any verification, then try again.");
                await AccountSessions.ConnectAsync(sessions[0], _expectedId, _cancel.Token);
                if (!_closed) DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_closed) StatusText.Text = AccountsWindow.SafeError(ex);
            }
            finally
            {
                _saving = false;
                if (!_closed) SaveButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void CloseBrowser(object? sender, EventArgs e)
        {
            _closed = true;
            _cancel.Cancel();
            // The profile is isolated and InPrivate, so this cannot affect the user's browser.
            try { Browser.CoreWebView2?.CookieManager.DeleteAllCookies(); } catch { }
            try { Browser.Dispose(); } catch { }
            _ = DeleteWhenReleasedAsync(_profileDirectory);
        }

        private static async Task DeleteWhenReleasedAsync(string directory)
        {
            // WebView2's browser process may briefly keep files open after Dispose.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(500);
            }
        }

        private static void CleanupOldProfiles()
        {
            // A crash can leave an empty InPrivate user-data folder. Never touch a
            // recent folder that may belong to another running sign-in window.
            string root = Path.Combine(Paths.Base, "AccountLogin");
            if (!Directory.Exists(root)) return;
            try
            {
                foreach (string path in Directory.EnumerateDirectories(root))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _) ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        Directory.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) continue;
                    try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
