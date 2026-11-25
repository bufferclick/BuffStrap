using BuffStrap.Release;
using System.Diagnostics;
using System.Net.Http;

namespace BuffStrapSetup;

/// <summary>
/// A small first stage: always fetch the latest stable app, then let its existing
/// installation wizard offer the desktop and Start menu shortcut choices.
/// </summary>
internal sealed class SetupWindow : Form
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _closing = new();
    private readonly Label _status;
    private readonly Label _detail;
    private readonly ProgressBar _progress;
    private readonly Button _retry;
    private bool _busy;

    public SetupWindow()
    {
        Text = "BuffStrap Setup";
        ClientSize = new Size(560, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(29, 27, 28);
        ForeColor = Color.FromArgb(245, 242, 242);
        Font = new Font("Segoe UI", 9F);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BuffStrapSetup/1.0");

        var logo = new PictureBox { Location = new Point(26, 23), Size = new Size(58, 58), SizeMode = PictureBoxSizeMode.Zoom };
        using (var stream = typeof(SetupWindow).Assembly.GetManifestResourceStream("BuffStrapSetup.BuffStrapLogo.png"))
        {
            if (stream is not null)
            {
                using var image = Image.FromStream(stream);
                logo.Image = new Bitmap(image);
            }
        }
        Controls.Add(logo);
        Controls.Add(new Label { Text = "BuffStrap Setup", AutoSize = true, Location = new Point(98, 23), Font = new Font("Segoe UI", 18F, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Made by Buffer", AutoSize = true, Location = new Point(101, 59), ForeColor = Color.FromArgb(187, 179, 181) });

        _status = new Label { Text = "Checking for the latest release...", AutoSize = true, Location = new Point(28, 111), Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        _detail = new Label
        {
            Text = "Next, choose your install location and whether to add Desktop and Start menu shortcuts.",
            Location = new Point(30, 143),
            Size = new Size(495, 48),
            ForeColor = Color.FromArgb(202, 193, 195)
        };
        _progress = new ProgressBar { Location = new Point(30, 194), Size = new Size(500, 10), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25 };
        Controls.AddRange(new Control[] { _status, _detail, _progress });

        _retry = new Button
        {
            Text = "Try again",
            Location = new Point(333, 247),
            Size = new Size(96, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(153, 69, 76),
            Visible = false
        };
        _retry.FlatAppearance.BorderSize = 0;
        _retry.Click += async (_, _) => await DownloadLatestAsync();
        var cancel = new Button { Text = "Cancel", Location = new Point(436, 247), Size = new Size(94, 34), FlatStyle = FlatStyle.Flat };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(100, 91, 93);
        cancel.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { _retry, cancel });

        Shown += async (_, _) => await DownloadLatestAsync();
        FormClosing += (_, _) => _closing.Cancel();
    }

    private async Task DownloadLatestAsync()
    {
        if (_busy) return;
        _busy = true;
        _retry.Visible = false;
        _status.Text = "Checking for the latest release...";
        _detail.Text = "Next, choose your install location and whether to add Desktop and Start menu shortcuts.";
        _progress.Style = ProgressBarStyle.Marquee;
        string? downloadedPath = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            var client = new ReleaseClient(_http);
            var release = await client.GetLatestReleaseAsync(timeout.Token);
            _status.Text = $"Downloading BuffStrap {release.TagName}...";
            _progress.Style = ProgressBarStyle.Continuous;
            var progress = new Progress<double>(value =>
            {
                if (!IsDisposed)
                    _progress.Value = Math.Clamp((int)(value * 100), 0, 100);
            });
            downloadedPath = await client.DownloadAsync(release, Path.Combine(Path.GetTempPath(), "BuffStrap", "Setup"), progress, timeout.Token);
            ReleaseClient.VerifyExecutableVersion(downloadedPath, release);
            timeout.Token.ThrowIfCancellationRequested();

            _status.Text = "Opening the installation wizard...";
            if (Process.Start(new ProcessStartInfo(downloadedPath) { UseShellExecute = true }) is null)
                throw new InvalidOperationException("Windows could not start the installer.");
            Close();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The user closed setup. A partial download is discarded by ReleaseClient.
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _status.Text = "Could not download BuffStrap";
                _detail.Text = "Check your connection and try again. " + ex.Message;
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = 0;
                _retry.Visible = true;
            }
        }
        finally
        {
            // Successful launches need the file until the wizard has finished running.
            if (downloadedPath is not null && !_closing.IsCancellationRequested)
            {
                try { Directory.Delete(Path.GetDirectoryName(downloadedPath)!, true); } catch { }
            }
            _busy = false;
        }
    }
}
