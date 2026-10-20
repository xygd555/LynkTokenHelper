using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LynkSignTokenHelper
{
    public partial class UpdateWindow : Window
    {
        private readonly IToolUpdater updater;
        private readonly UpdateRelease release;
        private readonly CancellationTokenSource operation = new CancellationTokenSource();
        private bool downloading, finishing, requiresAcknowledgment;
        public PreparedUpdate Prepared { get; private set; }
        public bool InstallationRunning { get; set; }
        public UpdateWindow(UpdateRelease release, IToolUpdater updater)
        {
            this.release = release;
            this.updater = updater;
            InitializeComponent();
            DownloadSource.ItemsSource = new[] { "默认线路", "备用线路 1", "备用线路 2", "GitHub 源" };
            DownloadSource.SelectedIndex = 0;
            VersionLabel.Text = BuildInfo.Version + "  →  " + release.Version;
            ReleaseNotes.Text = release.Notes;
            Loaded += (sender, args) => LaterButton.Focus();
            Closed += (sender, args) => operation.Cancel();
        }
        public UpdateWindow(string title, string message)
        {
            InitializeComponent();
            Heading.Text = title;
            VersionLabel.Text = "领克凭据助手";
            Height = 420;
            DownloadSourcePanel.Visibility = Visibility.Collapsed;
            Description.Text = "正在完成更新，请稍候…";
            NotesTitle.Text = "更新状态";
            ReleaseNotes.Text = message;
            LaterButton.Visibility = Visibility.Hidden;
            UpdateButton.Content = "确认";
            Loaded += (sender, args) => UpdateButton.Focus();
        }
        public void ReportStatus(string message) { ReleaseNotes.Text = message; UpdateButton.IsEnabled = !InstallationRunning; }
        public void ReportFailure(string message) { requiresAcknowledgment = true; Heading.Text = "更新未完成"; Description.Text = "请从原位置重新打开工具。"; ReleaseNotes.Text = message; UpdateButton.IsEnabled = true; }
        private async void UpdateClicked(object sender, RoutedEventArgs args)
        {
            if (InstallationRunning) return;
            if (release == null) { requiresAcknowledgment = false; Close(); return; }
            if (downloading) return;
            downloading = true;
            requiresAcknowledgment = false;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
            ProgressLabel.Text = "正在下载…";
            ProgressLabel.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            ProgressDetail.Text = "";
            ProgressHint.Text = "下载完成后会校验文件，并恢复代理后重启。";
            Description.Text = "下载完成后自动重启，需要重新登录。本次抓取的 Token 不会保留。";
            Heading.Text = "正在下载更新";
            UpdateButton.IsEnabled = false;
            DownloadSource.IsEnabled = false;
            LaterButton.Content = "取消下载";
            UpdateButton.Content = "下载中…";
            NotesPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            string download = null;
            try
            {
                var progress = new Progress<UpdateProgress>(value =>
                {
                    if (!IsVisible) return;
                    double percent = value.Total > 0 ? 100.0 * value.Received / value.Total : 0;
                    DownloadProgress.Value = percent;
                    ProgressLabel.Text = percent.ToString("0") + "%";
                    ProgressDetail.Text = (value.Received / 1048576.0).ToString("0.0") + " / " + (value.Total / 1048576.0).ToString("0.0") + " MB";
                });
                download = await updater.DownloadAsync(release, progress, operation.Token, (UpdateDownloadSource)DownloadSource.SelectedIndex);
                operation.Token.ThrowIfCancellationRequested();
                ProgressLabel.Text = "正在验证更新文件…";
                Prepared = await System.Threading.Tasks.Task.Run(() => updater.Prepare(download, release));
                if (operation.IsCancellationRequested) { updater.Discard(Prepared); Prepared = null; return; }
                finishing = true;
                DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsVisible) return;
                downloading = false;
                requiresAcknowledgment = true;
                Heading.Text = "更新未完成";
                Description.Text = "当前版本仍可正常使用。";
                ProgressLabel.Text = error is UnauthorizedAccessException ? "程序目录不可写，请将工具移到桌面或其他个人目录后重试。" : (error is IOException || error is InvalidDataException) ? error.Message : "无法下载或准备更新，请检查网络及目录权限后重试。";
                ProgressLabel.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
                ProgressDetail.Text = error is UnauthorizedAccessException ? "调整程序位置后可点击重试。" : "可以切换下方下载线路后重试。";
                ProgressHint.Text = "本次更新尚未替换程序。";
                DownloadProgress.Visibility = Visibility.Collapsed;
                LaterButton.Content = "确认";
                UpdateButton.Content = "重试";
                UpdateButton.IsEnabled = true;
                DownloadSource.IsEnabled = true;
            }
            finally { if (download != null) GitHubToolUpdater.DeleteFile(download); }
        }
        private void LaterClicked(object sender, RoutedEventArgs args) { requiresAcknowledgment = false; operation.Cancel(); Close(); }
        private void WindowClosing(object sender, CancelEventArgs args) { if (InstallationRunning || requiresAcknowledgment) { args.Cancel = true; return; } if (!finishing) operation.Cancel(); }
        private void WindowKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape) { args.Handled = true; if (release != null && !requiresAcknowledgment) LaterClicked(sender, args); }
        }
        private void SourceReady(object sender, EventArgs args)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int dark = 1, corners = 2, caption = 0x292320, text = 0xF8F4F2;
                DwmSetWindowAttribute(handle, 20, ref dark, 4);
                DwmSetWindowAttribute(handle, 33, ref corners, 4);
                DwmSetWindowAttribute(handle, 35, ref caption, 4);
                DwmSetWindowAttribute(handle, 36, ref text, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    }
}
