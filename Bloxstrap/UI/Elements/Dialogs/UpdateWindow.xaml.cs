using System.ComponentModel;
using System.Windows;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class UpdateWindow
    {
        private readonly CancellationTokenSource _cancellation;
        private bool _finished;

        public UpdateWindow(string version, CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            InitializeComponent();
            StatusText.Text = $"Getting BuffStrap {version}...";
        }

        public void SetProgress(double value) => DownloadProgress.Value = Math.Clamp(value * 100, 0, 100);

        public void Finish()
        {
            _finished = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_finished)
                _cancellation.Cancel();
            base.OnClosing(e);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
