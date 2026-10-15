using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LynkSignTokenHelper
{
    public partial class MainWindow
    {
        private readonly IToolUpdater updater;
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private bool checkingUpdates, updatePromptScheduled;
        private string updateCheckError;
        private UpdateRelease availableUpdate;
        private UpdateWindow updateDialog;
        private PreparedUpdate pendingInstall;
        private bool UpdateIdle => ready && alive && !Busy && !openingMiniProgram && snapshot?.state.capturing != true &&
            snapshot?.state.authorizing != true && snapshot?.state.confirmation_required != true && currentImportantNotice == null &&
            ImportantOverlay.Visibility != Visibility.Visible && !closeRequested && !restoreOnly;
        private void RenderUpdateButtons()
        {
            string label = checkingUpdates ? "检查中…" : availableUpdate != null ? "发现新版" : "检查更新";
            string tip = "当前版本 " + BuildInfo.Version + (availableUpdate != null ? "，可更新至 " + availableUpdate.Version : "") +
                (!string.IsNullOrEmpty(updateCheckError) ? "\n" + updateCheckError : "") + (!UpdateIdle ? "\n等待当前操作结束后可检查更新。" : "");
            foreach (Button button in new[] { LoginUpdateButton, MainUpdateButton })
            {
                if (button == null) continue;
                button.Content = label;
                button.ToolTip = tip;
                button.IsEnabled = updater != null && UpdateIdle && !checkingUpdates && pendingInstall == null;
            }
        }
        private async void CheckUpdatesClicked(object sender, RoutedEventArgs args)
        {
            if (!UpdateIdle || checkingUpdates || updater == null) return;
            if (availableUpdate != null) OfferUpdate();
            else await CheckForUpdatesAsync(true);
        }
        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (checkingUpdates || updater == null) return;
            checkingUpdates = true;
            updateCheckError = null;
            RenderUpdateButtons();
            try
            {
                availableUpdate = await updater.CheckAsync(updateLifetime.Token);
                if (updateLifetime.IsCancellationRequested || !IsVisible) return;
                if (availableUpdate == null && manual) ShowImportantNotice("已是最新版本", "当前版本为 " + BuildInfo.Version + "，暂无更新的正式版本。", false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                updateCheckError = (error is System.IO.IOException || error is System.IO.InvalidDataException) ? error.Message : "检查更新失败，请稍后重试。";
                if (manual && !updateLifetime.IsCancellationRequested) ShowImportantNotice("无法检查更新", updateCheckError + "\n\n当前版本仍可正常使用。");
            }
            finally { checkingUpdates = false; RenderUpdateButtons(); ScheduleUpdatePrompt(); }
        }
        private void ScheduleUpdatePrompt()
        {
            if (updatePromptScheduled || availableUpdate == null || updateDialog != null || !UpdateIdle || !IsVisible || WindowState == WindowState.Minimized) return;
            updatePromptScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { updatePromptScheduled = false; OfferUpdate(); }));
        }
        private void OfferUpdate()
        {
            if (availableUpdate == null || updateDialog != null || !UpdateIdle || !IsVisible || WindowState == WindowState.Minimized) return;
            UpdateRelease release = availableUpdate;
            availableUpdate = null;
            updateDialog = new UpdateWindow(release, updater) { Owner = this };
            PreparedUpdate prepared = null;
            try { if (updateDialog.ShowDialog() == true) prepared = updateDialog.Prepared; }
            finally { updateDialog = null; RenderUpdateButtons(); }
            if (prepared == null) return;
            if (!UpdateIdle) { updater.Discard(prepared); ShowImportantNotice("更新已暂缓", "当前操作尚未结束，请完成后重新检查更新。"); return; }
            pendingInstall = prepared;
            Close();
        }
        private void DiscardPendingUpdate()
        {
            if (pendingInstall == null) return;
            updater?.Discard(pendingInstall);
            pendingInstall = null;
        }
    }
}
