using Bloxstrap.UI.Elements.Base;
using BuffStrap.Cursors;
using Microsoft.Web.WebView2.Core;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Bloxstrap.UI.Elements.Dialogs
{
    /// <summary>
    /// A small browser for the cursor library. Downloads are redirected into
    /// BuffStrap and imported immediately, so nothing lands in Downloads and the
    /// user never has to pick a file by hand.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public partial class CursorLibraryWindow : WpfUiWindow
    {
        private readonly string _profile = Path.Combine(Paths.Base, "CursorBrowser", Guid.NewGuid().ToString("N"));
        private readonly string _downloads = Path.Combine(Paths.Base, "CursorBrowser", Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _cancel = new();
        private bool _closed;
        private bool _imported;

        /// <summary>Set when a cursor package was imported successfully.</summary>
        public string? ImportedSetId { get; private set; }

        public CursorLibraryWindow()
        {
            InitializeComponent();
            Loaded += InitializeBrowser;
            Closed += CloseBrowser;
        }

        private async void InitializeBrowser(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_downloads);
                StatusText.Text = "Opening the cursor library...";
                var environment = await CoreWebView2Environment.CreateAsync(null, _profile);
                if (_closed)
                {
                    _ = DeleteWhenReleasedAsync(_profile);
                    return;
                }

                var options = environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                await Browser.EnsureCoreWebView2Async(environment, options);
                if (_closed) return;

                var web = Browser.CoreWebView2;
                web.Settings.AreDevToolsEnabled = false;
                web.Settings.IsPasswordAutosaveEnabled = false;
                web.Settings.IsGeneralAutofillEnabled = false;
                web.Settings.AreDefaultContextMenusEnabled = true;

                web.NavigationStarting += (_, args) =>
                {
                    if (IsLibraryUrl(args.Uri)) return;
                    // Never browse the web inside BuffStrap; hand other links over.
                    args.Cancel = true;
                    try { Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true }); } catch { }
                };

                web.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    if (IsLibraryUrl(args.Uri)) web.Navigate(args.Uri);
                    else try { Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true }); } catch { }
                };

                web.DownloadStarting += OnDownloadStarting;
                web.Navigate(CursorPackage.LibraryUrl);
                StatusText.Text = "Pick a cursor set and press Download. BuffStrap imports the zip itself.";
            }
            catch
            {
                if (!_closed)
                    StatusText.Text = "Could not start the built-in browser. Install the Microsoft Edge WebView2 Runtime, or use Import with a downloaded zip.";
            }
        }

        private static bool IsLibraryUrl(string? uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
            (parsed.Host.Equals("rw-designer.com", StringComparison.OrdinalIgnoreCase) ||
             parsed.Host.EndsWith(".rw-designer.com", StringComparison.OrdinalIgnoreCase));

        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            string name = "cursor";
            try
            {
                var uri = new Uri(e.DownloadOperation.Uri);
                string leaf = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                if (!String.IsNullOrWhiteSpace(leaf)) name = Uri.UnescapeDataString(leaf);
            }
            catch { }

            string path = Path.Combine(_downloads, Guid.NewGuid().ToString("N") + ".zip");
            e.ResultFilePath = path;
            e.Handled = true;   // no save dialog, no Downloads folder

            var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) => OnDownloadStateChanged(operation, path, name);
            StatusText.Text = $"Downloading {name}...";
        }

        private void OnDownloadStateChanged(CoreWebView2DownloadOperation operation, string path, string name)
        {
            if (_closed) return;
            if (operation.State == CoreWebView2DownloadOperationState.Completed)
            {
                Import(path, name);
            }
            else if (operation.State == CoreWebView2DownloadOperationState.Interrupted)
            {
                StatusText.Text = "That download did not finish. Try the set again, or use Import with a saved zip.";
            }
        }

        private void Import(string path, string name)
        {
            if (_imported) return;
            try
            {
                if (!File.Exists(path)) throw new InvalidDataException("The download was empty.");
                var set = Bloxstrap.Utility.CustomCursors.Import(File.ReadAllBytes(path), name);
                ImportedSetId = set.Id;
                _imported = true;

                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(set.ImagePath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    Preview.Source = image;
                }
                catch { }

                StatusText.Text = $"Added \"{set.Name}\". It is selected already and applies the next time Roblox starts.";
                // The Mods page refreshes once this closes.
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += (_, _) => { timer.Stop(); if (!_closed) DialogResult = true; };
                timer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = "That package did not contain a usable cursor. " + ex.Message;
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private void OpenExternal_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(CursorPackage.LibraryUrl) { UseShellExecute = true }); } catch { }
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        private void CloseBrowser(object? sender, EventArgs e)
        {
            _closed = true;
            _cancel.Cancel();
            try { Browser.Dispose(); } catch { }
            _ = DeleteWhenReleasedAsync(_profile);
            _ = DeleteWhenReleasedAsync(_downloads);
        }

        private static async Task DeleteWhenReleasedAsync(string directory)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(250);
            }
        }
    }
}
