using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LynkSignTokenHelper
{
    public partial class MainWindow : Window
    {
        private readonly ICoreClient client;
        private readonly bool restoreOnly;
        private readonly Func<Rect> workArea;
        private readonly IMiniProgramLauncher miniProgramLauncher;
        private CancellationTokenSource miniProgramOperation;
        private bool openingMiniProgram, closeAfterMiniProgram;
        private CoreSnapshot snapshot;
        private bool ready, alive, editLogin, showPassword, rendering, allowClose, closeRequested, started;
        private long pendingId;
        private string lastAction;
        private string candidateSignature = "";
        private readonly DispatcherTimer toastTimer;
        private readonly DispatcherTimer miniProgramCaptureTimer;
        private string miniProgramCaptureAt, miniProgramCaptureServer;
        private int miniProgramCaptureGeneration;
        private readonly Queue<ImportantNotice> importantNotices = new Queue<ImportantNotice>();
        private ImportantNotice currentImportantNotice;
        private IInputElement previousNoticeFocus;
        private sealed class ImportantNotice
        {
            public string Title, Message;
            public bool Warning;
            public Action Confirmed;
        }
        public Action<string> CopyText { get; set; } = Clipboard.SetText;
        public Action<string> OpenBrowser { get; set; } = address => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });

        public MainWindow(ICoreClient client, bool restoreOnly = false, Func<Rect> workArea = null, IMiniProgramLauncher miniProgramLauncher = null, TimeSpan? miniProgramCaptureDelay = null, IToolUpdater updater = null)
        {
            this.client = client;
            this.restoreOnly = restoreOnly;
            this.updater = updater;
            this.miniProgramLauncher = miniProgramLauncher ?? new LynkMiniProgramLauncher();
            this.workArea = workArea ?? (() => SystemParameters.WorkArea);
            InitializeComponent();
            Title = "领克凭据助手 " + BuildInfo.Version;
            AppVersion.Text = "  " + BuildInfo.Version;
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/LynkSignTokenHelper-windows-x64;component/Branding/lynk-icon.ico"));
            Address.Text = BuildInfo.DefaultServer;
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.1) };
            toastTimer.Tick += (sender, args) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
            miniProgramCaptureTimer = new DispatcherTimer { Interval = miniProgramCaptureDelay ?? TimeSpan.FromSeconds(10) };
            miniProgramCaptureTimer.Tick += (sender, args) =>
            {
                miniProgramCaptureTimer.Stop();
                if (ShouldPromptForMiniProgramLogin()) ShowImportantNotice("请登录领克账号", "小程序已打开，暂未获取到新的凭据。\n\n如果小程序尚未登录，请登录领克账号；如果已经登录，请退出账号后重新登录。\n\n" + (snapshot?.state.local_only == true ? "工具会继续抓取，获取凭据后可在本地复制，不会上传。" : "工具会继续抓取，获取凭据后自动提交。"), false);
            };
            client.Updated += value => Dispatcher.BeginInvoke(new Action(() =>
            {
                ready = alive = true;
                bool browserAuthorized = lastAction == "login/browser" && pendingId != 0 && pendingId <= value.request_id && !value.pending && value.state.logged_in && string.IsNullOrEmpty(value.error);
                if (pendingId != 0 && pendingId <= value.request_id && !value.pending)
                {
                    if (((lastAction == "login" || lastAction == "login/browser") && value.state.logged_in || lastAction == "capture/local" && value.state.local_only) && string.IsNullOrEmpty(value.error)) editLogin = false;
                    pendingId = 0;
                    lastAction = "";
                }
                if (closeRequested && !value.closing && !string.IsNullOrEmpty(value.error)) { closeRequested = false; DiscardPendingUpdate(); }
                bool openAuthorization = !string.IsNullOrEmpty(value.state.authorization_url) && value.state.authorization_url != snapshot?.state.authorization_url;
                bool newError = !string.IsNullOrEmpty(value.error) && (value.error != snapshot?.error || value.request_id != snapshot?.request_id);
                bool submissionFailed = value.state.submission == "failed" && snapshot?.state.submission != "failed";
                bool captureWarning = value.state.message != snapshot?.state.message && (value.state.message?.StartsWith("等待已超时") == true || value.state.message?.StartsWith("领克连接失败") == true);
                snapshot = value;
                if (!ShouldPromptForMiniProgramLogin()) miniProgramCaptureTimer.Stop();
                Render();
                if (browserAuthorized) BringToFront();
                if (openAuthorization) TryOpenAuthorization();
                if (newError) ShowImportantNotice("操作未完成", value.error);
                else if (submissionFailed) ShowImportantNotice("提交失败，Token 已保留", "本次获取的 Token 已保留，可以在主界面复制备用，也可以检查服务器状态后重试提交。\n\n详细原因见主界面的状态提示。");
                else if (captureWarning) ShowImportantNotice("抓取需要处理", value.state.message);
            }));
            client.Faulted += message => Dispatcher.BeginInvoke(new Action(() =>
            {
                alive = false;
                DiscardPendingUpdate();
                CancelMiniProgramReminder();
                closeRequested = false;
                pendingId = 0;
                if (snapshot != null) { snapshot.error = message; snapshot.pending = snapshot.closing = false; snapshot.state.busy = false; Render(); }
                else { LoginNotice.Text = message; LoginNotice.Visibility = Visibility.Visible; LoginButtonText.Text = "无法连接"; }
                ShowImportantNotice("工具连接已断开", message + "\n请重新打开工具，检查代理是否已恢复。");
            }));
            client.Closed += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (pendingInstall != null)
                {
                    PreparedUpdate install = pendingInstall;
                    pendingInstall = null;
                    try { updater.Launch(install); }
                    catch (Exception)
                    {
                        updater.Discard(install);
                        alive = closeRequested = false; Render();
                        ShowImportantNotice("无法启动更新程序", "代理已恢复，当前程序文件未修改。请关闭工具后重新打开，再尝试更新。");
                        return;
                    }
                }
                allowClose = true;
                if (restoreOnly) ShowImportantNotice("代理已恢复", "原代理配置已恢复，可以关闭工具。", false, Close);
                else Close();
            }));
            Closed += (sender, args) => { updateLifetime.Cancel(); updateDialog?.Close(); miniProgramOperation?.Cancel(); CancelMiniProgramReminder(); toastTimer.Stop(); client.Dispose(); };
            ApplyWindowSize(true);
            RenderUpdateButtons();
            Activated += (sender, args) => ScheduleUpdatePrompt();
        }

        private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value);
        private bool Busy => pendingId != 0 || closeRequested || (snapshot != null && (snapshot.pending || snapshot.closing || snapshot.state.busy));
        private void WindowLoaded(object sender, RoutedEventArgs args)
        {
            if (started) return;
            started = true;
            Fade(LoginView);
            client.Start();
            Address.Focus();
            if (updater != null && !restoreOnly) _ = CheckForUpdatesAsync(false);
        }
        private void ApplyWindowSize(bool login)
        {
            Rect area = workArea();
            Width = Math.Min(login ? 520 : 1040, Math.Max(360, area.Width - 24));
            Height = Math.Min(login ? 700 : 720, Math.Max(360, area.Height - 24));
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            bool narrow = !login && Width < 800;
            Workbench.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(248);
            Workbench.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 28);
            Workbench.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ResultPanel, narrow ? 0 : 2);
            Grid.SetRow(ResultPanel, narrow ? 1 : 0);
            ResultPanel.Margin = new Thickness(0, narrow ? 24 : 2, 0, 0);
            ImportantBodyHost.MaxHeight = Math.Max(24, Math.Min(220, Height - 384));
            LoginCardHost.Width = Math.Min(388, Math.Max(280, Width - 64));
            bool compactLogin = Height < 650;
            LoginCardHost.Padding = compactLogin ? new Thickness(18, 10, 18, 10) : new Thickness(24, 20, 24, 20);
            LoginCardHost.Margin = new Thickness(20, compactLogin ? 10 : 16, 20, compactLogin ? 10 : 16);
            LoginHeader.Margin = new Thickness(0, 0, 0, compactLogin ? 8 : 14);
            LoginSubtitle.Visibility = Show(!compactLogin);
            AddressLabel.Margin = new Thickness(0, 0, 0, compactLogin ? 4 : 6);
            UsernameLabel.Margin = new Thickness(0, compactLogin ? 6 : 12, 0, compactLogin ? 4 : 6);
            PasswordLabel.Margin = new Thickness(0, compactLogin ? 6 : 12, 0, compactLogin ? 4 : 6);
            LoginActions.Height = compactLogin ? 40 : 44;
            BrowserLoginHint.Visibility = Show(!compactLogin);
            Address.Height = Username.Height = Password.Height = VisiblePassword.Height = compactLogin ? 40 : 42;
            AllowHttp.Margin = new Thickness(0, compactLogin ? 6 : 14, 0, compactLogin ? 8 : 14);
            LoginNoticeHost.Height = 36;
        }
        private static void Fade(UIElement element)
        {
            if (SystemParameters.ClientAreaAnimation)
                element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
        }
        private void Render()
        {
            if (snapshot == null) return;
            rendering = true;
            try
            {
                AppState state = snapshot.state;
                bool loggedIn = (state.logged_in || state.local_only) && !editLogin;
                bool captured = !string.IsNullOrEmpty(snapshot.token);
                bool busy = Busy;
                bool saved = state.submission == "saved" && snapshot.selected == state.selected_candidate;
                CandidateInfo[] values = state.candidates ?? new CandidateInfo[0];
                if ((MainView.Visibility == Visibility.Visible) != loggedIn)
                {
                    MainView.Visibility = Show(loggedIn);
                    LoginView.Visibility = Show(!loggedIn);
                    ApplyWindowSize(!loggedIn);
                    Fade(loggedIn ? (UIElement)MainView : LoginView);
                    if (!loggedIn) Address.Focus();
                }
                Address.IsEnabled = Username.IsEnabled = Password.IsEnabled = VisiblePassword.IsEnabled = AllowHttp.IsEnabled = TogglePassword.IsEnabled = !busy && alive;
                LoginButtonText.Text = closeRequested || snapshot.closing ? "正在退出…" : busy && lastAction == "login" ? "正在登录…" : "账号密码登录";
                UpdateLoginButton();
                bool authorizing = state.authorizing || (busy && lastAction == "login/browser");
                BrowserLoginButtonText.Text = closeRequested ? "正在退出…" : authorizing ? "等待授权…" : !ready ? "正在准备…" : "网页授权登录";
                BrowserLoginHint.Text = authorizing ? "请在浏览器中确认授权，授权请求有效期为 5 分钟。" : "网页授权只需服务器地址；也可填写面板账号密码直接登录。";
                BrowserLoginActions.Visibility = authorizing ? Visibility.Visible : Visibility.Hidden;
                CancelBrowserLoginButton.IsEnabled = authorizing && !closeRequested && alive;
                ReopenAuthorizationButton.IsEnabled = authorizing && !string.IsNullOrEmpty(state.authorization_url) && !closeRequested;
                BackButton.Visibility = state.logged_in || state.local_only ? Visibility.Visible : Visibility.Hidden;
                BackButton.IsEnabled = !busy;
                bool loginError = !loggedIn && !busy && !state.logged_in && !state.local_only && !string.IsNullOrEmpty(state.message) && !state.message.StartsWith("请先登录服务器");
                LoginNotice.Visibility = loginError || (!alive && !string.IsNullOrEmpty(snapshot.error)) ? Visibility.Visible : Visibility.Hidden;
                LoginNotice.Text = !alive ? snapshot.error : loginError ? state.message : "";
                ServerAddress.Text = state.local_only ? "仅本地抓包 · 凭据不会上传" : state.server_address ?? "";
                ServerAddress.ToolTip = ServerAddress.Text;
                AccountName.Text = state.local_only ? "" : state.username ?? "";
                SwitchButton.Content = state.local_only ? "登录服务器" : "切换服务器";
                SwitchButton.IsEnabled = alive && !busy && !state.capturing;
                CaptureTitle.Text = state.capturing ? "正在抓取" : captured ? "抓取完成" : "准备获取";
                CaptureBadge.Text = state.capturing ? "监听中" : captured ? "已获取" : "待开始";
                CaptureBadge.Foreground = Brush(state.capturing ? "#A7CEFF" : captured ? "#98D4B1" : "#B2BBC9");
                StatusIcon.Data = (Geometry)FindResource("Icon_" + (state.capturing ? "arrow_sync" : captured ? "checkmark_circle" : state.trusted ? "key" : "shield_checkmark"));
                if (state.capturing && SystemParameters.ClientAreaAnimation)
                {
                    if (!((RotateTransform)StatusIcon.RenderTransform).HasAnimatedProperties)
                        ((RotateTransform)StatusIcon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever });
                }
                else ((RotateTransform)StatusIcon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
                string captureTime = state.trusted ? "等待开始抓取" : "先安装抓包证书";
                if (captured && DateTimeOffset.TryParse(state.captured_at, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)) captureTime = "捕获于 " + time.LocalDateTime.ToString("HH:mm:ss");
                if (state.capturing) captureTime = "等待小程序登录流量";
                CaptureTime.Text = captureTime;
                Connections.Text = state.connections.ToString();
                LynkRequests.Text = state.lynk_requests.ToString();
                StartButton.Visibility = state.capturing ? Visibility.Hidden : Visibility.Visible;
                StopButton.Visibility = state.capturing ? Visibility.Visible : Visibility.Hidden;
                StartButtonText.Text = captured ? "重新抓取" : "开始抓取";
                StartButton.Style = (Style)FindResource(captured ? typeof(Button) : (object)"Primary");
                StartButton.IsEnabled = alive && !busy && state.trusted && !state.confirmation_required;
                StopButton.IsEnabled = alive && !busy;
                MiniProgramButton.IsEnabled = alive && !busy && !openingMiniProgram;
                MiniProgramButtonText.Text = openingMiniProgram ? "正在打开…" : "打开领克小程序";
                CertificateStatus.Text = state.trusted ? "已安装 · 当前用户" : "尚未安装";
                CertificateStatus.ToolTip = state.trusted ? "证书已安装到当前 Windows 用户的受信任根证书存储区" : "安装抓包证书后即可开始抓取";
                InstallButton.Visibility = Show(!state.trusted);
                InstallButton.IsEnabled = alive && !busy && !state.capturing;
                CaptureGuide.Text = !state.trusted ? "安装证书并登录电脑微信，再开始抓取并点击打开小程序。" : state.capturing ? "点击打开小程序；未获取到凭据时，登录或退出账号后重新登录。" : captured ? "可在抓取结果中复制 Token；重新抓取可更新凭据。" : "先登录电脑微信，开始抓取后点击打开领克小程序。";
                ResultHint.Visibility = Show(state.local_only || !captured || state.capturing);
                ResultHint.Text = state.local_only ? "凭据仅保留在本机内存中，可复制使用" : state.capturing ? "抓到凭据后会自动提交" : "获取后自动提交到服务器";
                string signature = string.Join("\n", values.Select(value => value.id + "|" + value.device_id + "|" + value.source));
                if (signature != candidateSignature)
                {
                    candidateSignature = signature;
                    Candidates.ItemsSource = values.Select((value, index) => (index + 1) + " · " + value.source + " · " + (string.IsNullOrEmpty(value.device_id) ? "设备 ID 未获取" : value.device_id)).ToArray();
                }
                Candidates.SelectedIndex = snapshot.selected;
                Candidates.IsEnabled = alive && !busy && !state.capturing && !state.confirmation_required;
                CandidateField.Visibility = Show(values.Length > 1 && !state.confirmation_required);
                TokenLabel.Text = captured && state.capturing ? "上次抓取的 Refresh Token" : "Refresh Token";
                TokenSource.Text = captured && snapshot.selected < values.Length ? values[snapshot.selected].source : "";
                if (Token.Text != (snapshot.token ?? "")) Token.Text = snapshot.token ?? "";
                TokenPlaceholder.Visibility = Show(!captured);
                Device.Text = !string.IsNullOrEmpty(snapshot.device_id) ? snapshot.device_id : !captured ? "尚未获取" : state.local_only ? "未获取" : "未获取，使用服务器已有设备 ID";
                CopyTokenButton.IsEnabled = captured && !closeRequested;
                CopyDeviceButton.IsEnabled = !string.IsNullOrEmpty(snapshot.device_id) && !closeRequested;
                RenderNotice(state, captured, saved, busy, values.Length);
                Confirmation.Visibility = Show(!state.local_only && state.confirmation_required && !closeRequested);
                NewAccount.Text = state.account?.display_name ?? "领克账号";
                NewAccountId.Text = state.account?.id ?? "";
                OldAccountId.Text = state.previous_account_id ?? "未知";
                ConfirmButton.IsEnabled = CancelButton.IsEnabled = alive && !busy;
                SubmitActions.Visibility = Show(!state.local_only && !state.confirmation_required);
                SubmitButton.IsEnabled = !state.local_only && alive && !busy && captured && !state.capturing && !saved;
                SubmitButton.Content = saved ? "已保存到服务器" : (state.submission == "failed" && snapshot.selected == state.selected_candidate) || state.submission == "cancelled" ? "重新提交" : state.submission == "submitting" ? "正在提交…" : "提交服务器";
                BackupHint.Visibility = Show(captured);
                BackupHint.Text = state.local_only ? "点击“复制 Token”即可使用；关闭工具后不保留凭据。" : saved ? "本次抓取的 Token 仍保留在界面中，可复制备用。" : "提交失败时，可复制 Token 手动填写。";
                ProxyState.Text = state.capturing ? "系统代理正在使用" : "代理已恢复";
                ProxyDot.Fill = Brush(state.capturing ? "#85BAFF" : "#98D4B1");
                RestoreButton.IsEnabled = alive && !busy;
                RemoveButton.IsEnabled = alive && !busy && state.trusted;
                ExitButton.IsEnabled = !closeRequested;
            }
            finally { rendering = false; RenderUpdateButtons(); ScheduleUpdatePrompt(); }
        }
        private void RenderNotice(AppState state, bool captured, bool saved, bool busy, int count)
        {
            string title = state.trusted ? "可以开始抓取" : "先安装抓包证书";
            string body = state.trusted ? "先打开并登录电脑微信，再点击左侧开始抓取和打开领克小程序。" : "点击左侧证书旁的“安装”，完成后即可开始抓取。";
            string kind = "", icon = state.trusted ? "key" : "shield_checkmark";
            if (captured) { title = "凭据已获取"; body = state.local_only ? count > 1 ? "选择对应的凭据后复制，仅在本地保留，不会上传。" : "凭据仅在本地保留，可复制使用，不会上传。" : count > 1 ? "选择对应的凭据后提交。" : state.message; icon = "checkmark_circle"; }
            if (state.capturing) { title = "正在等待新的登录凭据"; body = state.message; icon = "arrow_sync"; }
            if (state.submission == "failed" && snapshot.selected == state.selected_candidate) { title = "提交失败，Token 已保留"; body = state.message; kind = "warning"; icon = "dismiss_circle"; }
            if (saved) { title = "已保存到服务器"; body = (state.account?.display_name ?? "领克账号") + " · " + state.account?.id; kind = "success"; icon = "checkmark_circle"; }
            if (state.submission == "saved" && !saved) { title = "当前选择尚未提交"; body = "已保存的是第 " + (state.selected_candidate + 1) + " 份凭据。"; }
            if (state.submission == "cancelled") { title = "已取消换绑"; body = "服务器原账号未更改，抓取的 Token 仍可复制。"; }
            if (!string.IsNullOrEmpty(snapshot.error)) { title = state.confirmation_required ? "换绑确认未完成" : state.submission == "failed" ? "提交失败，Token 已保留" : "操作未完成"; body = snapshot.error; kind = "warning"; icon = "dismiss_circle"; }
            if (!captured && state.trusted && !state.capturing && !string.IsNullOrEmpty(state.message) && !state.message.StartsWith("服务器登录成功") && string.IsNullOrEmpty(snapshot.error)) { title = "操作提示"; body = state.message; }
            if (busy) { title = closeRequested || snapshot.closing ? "正在恢复代理并退出" : state.submission == "submitting" ? "正在提交服务器" : "正在处理"; body = "请稍候…"; kind = ""; icon = "arrow_sync"; }
            NoticeTitle.Text = title;
            NoticeBody.Text = body ?? "";
            MainNotice.Background = Brush(kind == "warning" ? "#302A31" : kind == "success" ? "#25312D" : "#282C33");
            NoticeTitle.Foreground = Brush(kind == "warning" ? "#F0C2C8" : kind == "success" ? "#A9DCBD" : "#F2F4F8");
            NoticeIcon.Fill = Brush(kind == "warning" ? "#EDAFB8" : kind == "success" ? "#98D4B1" : "#85BAFF");
            NoticeIcon.Data = (Geometry)FindResource("Icon_" + icon);
            MainNotice.Visibility = Show(!state.confirmation_required || busy || !string.IsNullOrEmpty(snapshot.error));
        }
        private void UpdateLoginButton()
        {
            if (LoginButton == null || Address == null || Username == null || Password == null) return;
            if (LocalCaptureButton != null) LocalCaptureButton.IsEnabled = ready && alive && !Busy;
            if (BrowserLoginButton != null) BrowserLoginButton.IsEnabled = ready && alive && !Busy && !string.IsNullOrWhiteSpace(Address.Text);
            LoginButton.IsEnabled = ready && alive && !Busy && !string.IsNullOrWhiteSpace(Address.Text) && !string.IsNullOrWhiteSpace(Username.Text) && Password.Password.Length > 0;
        }
        private void Send(string action, object input = null)
        {
            if (!alive || Busy || ImportantOverlay.Visibility == Visibility.Visible) return;
            if (action == "start" || action == "stop" || action == "restore" || action == "login" || action == "login/browser" || action == "capture/local") CancelMiniProgramReminder();
            try { lastAction = action; pendingId = client.Command(action, input); Render(); }
            catch (Exception) { ShowToast("窗口连接已断开，请重新打开工具。", false); }
        }
        private void CancelMiniProgramReminder()
        {
            miniProgramCaptureTimer.Stop();
            miniProgramCaptureGeneration++;
        }
        private bool ShouldPromptForMiniProgramLogin() =>
            alive && !closeRequested && IsVisible && (snapshot?.state.logged_in == true || snapshot?.state.local_only == true) && snapshot.state.capturing &&
            snapshot.state.server_address == miniProgramCaptureServer && snapshot.state.captured_at == miniProgramCaptureAt;
        private void ShowImportantNotice(string title, string message, bool warning = true, Action confirmed = null)
        {
            if (!IsVisible || closeRequested || currentImportantNotice?.Message == message || importantNotices.Any(notice => notice.Message == message)) return;
            importantNotices.Enqueue(new ImportantNotice { Title = title, Message = message, Warning = warning, Confirmed = confirmed });
            if (currentImportantNotice == null)
            {
                previousNoticeFocus = Keyboard.FocusedElement;
                ShowNextImportantNotice();
            }
        }
        private void ShowNextImportantNotice()
        {
            if (importantNotices.Count == 0)
            {
                ImportantOverlay.Visibility = Visibility.Collapsed;
                LoginView.IsEnabled = MainView.IsEnabled = true;
                if (previousNoticeFocus is UIElement element && element.IsVisible && element.IsEnabled) Keyboard.Focus(previousNoticeFocus);
                previousNoticeFocus = null;
                ScheduleUpdatePrompt();
                return;
            }
            currentImportantNotice = importantNotices.Dequeue();
            ImportantTitle.Text = currentImportantNotice.Title;
            ImportantBody.Text = currentImportantNotice.Message;
            ImportantIcon.Data = (Geometry)FindResource(currentImportantNotice.Warning ? "Icon_dismiss_circle" : "Icon_key");
            ImportantIcon.Fill = Brush(currentImportantNotice.Warning ? "#EDAFB8" : "#85BAFF");
            ImportantIconTile.Background = Brush(currentImportantNotice.Warning ? "#45323D" : "#263D59");
            ImportantBodyHost.ScrollToTop();
            LoginView.IsEnabled = MainView.IsEnabled = false;
            Toast.Visibility = Visibility.Collapsed;
            ImportantOverlay.Visibility = Visibility.Visible;
            BringToFront();
            ImportantConfirmButton.Focus();
        }
        private void BringToFront()
        {
            if (!IsVisible || closeRequested) return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            bool previousTopmost = (GetWindowLong(handle, -20) & 8) != 0;
            SetTopmost(handle, true);
            Activate();
            SetTopmost(handle, previousTopmost);
        }
        private void ImportantConfirmed(object sender, RoutedEventArgs args)
        {
            if (currentImportantNotice == null) return;
            Action confirmed = currentImportantNotice.Confirmed;
            currentImportantNotice = null;
            ShowNextImportantNotice();
            confirmed?.Invoke();
        }
        private void ShowToast(string message, bool success = true)
        {
            if (!success) { ShowImportantNotice("操作未完成", message); return; }
            ToastText.Text = message;
            ToastText.Foreground = Brush(success ? "#BCE6CC" : "#F0C2C8");
            Toast.Visibility = Visibility.Visible;
            Fade(Toast);
            toastTimer.Stop();
            toastTimer.Start();
        }
        private void Copy(string value, string label)
        {
            if (string.IsNullOrEmpty(value)) return;
            try { CopyText(value); ShowToast(label + "已复制"); }
            catch (Exception) { ShowToast("剪贴板正在被其他程序使用，请稍后重试。", false); }
        }
        private void LoginInputChanged(object sender, TextChangedEventArgs args) => UpdateLoginButton();
        private void PasswordChanged(object sender, RoutedEventArgs args) => UpdateLoginButton();
        private void VisiblePasswordChanged(object sender, TextChangedEventArgs args)
        {
            if (showPassword && Password != null) Password.Password = VisiblePassword.Text;
        }
        private void TogglePasswordClicked(object sender, RoutedEventArgs args)
        {
            showPassword = !showPassword;
            if (showPassword) VisiblePassword.Text = Password.Password;
            Password.Visibility = Show(!showPassword);
            VisiblePassword.Visibility = Show(showPassword);
            PasswordIcon.Data = (Geometry)FindResource(showPassword ? "Icon_eye_off" : "Icon_eye");
            TogglePassword.ToolTip = showPassword ? "隐藏密码" : "显示密码";
            if (showPassword) VisiblePassword.Focus(); else { VisiblePassword.Clear(); Password.Focus(); }
        }
        private void BrowserLoginClicked(object sender, RoutedEventArgs args)
        {
            if (!BrowserLoginButton.IsEnabled) return;
            Password.Clear(); VisiblePassword.Clear();
            Send("login/browser", new { Address = Address.Text.Trim(), AllowHTTP = AllowHttp.IsChecked == true });
        }
        private void TryOpenAuthorization()
        {
            string address = snapshot?.state.authorization_url;
            if (string.IsNullOrEmpty(address)) return;
            try { OpenBrowser(address); }
            catch (Exception)
            {
                CancelBrowserLoginClicked(this, new RoutedEventArgs());
                ShowImportantNotice("无法打开浏览器", "请确认 Windows 已设置默认浏览器，再重新发起授权；也可以使用账号密码登录。");
            }
        }
        private void ReopenAuthorizationClicked(object sender, RoutedEventArgs args) => TryOpenAuthorization();
        private void CancelBrowserLoginClicked(object sender, RoutedEventArgs args)
        {
            if (!CancelBrowserLoginButton.IsEnabled) return;
            try { client.Command("login/cancel"); }
            catch (Exception) { ShowImportantNotice("取消授权失败", "工具连接已断开，请关闭后重新打开。"); }
        }
        private void LoginClicked(object sender, RoutedEventArgs args)
        {
            if (!LoginButton.IsEnabled) return;
            var input = new { Address = Address.Text.Trim(), Username = Username.Text.Trim(), Password = Password.Password, AllowHTTP = AllowHttp.IsChecked == true };
            Send("login", input);
            Password.Clear();
            VisiblePassword.Clear();
            UpdateLoginButton();
        }
        private void LocalCaptureClicked(object sender, RoutedEventArgs args)
        {
            if (!LocalCaptureButton.IsEnabled) return;
            miniProgramOperation?.Cancel();
            Send("capture/local");
            Password.Clear();
            VisiblePassword.Clear();
        }
        private void SwitchClicked(object sender, RoutedEventArgs args)
        {
            CancelMiniProgramReminder();
            miniProgramOperation?.Cancel();
            showPassword = false;
            Password.Visibility = Visibility.Visible;
            VisiblePassword.Visibility = Visibility.Collapsed;
            PasswordIcon.Data = (Geometry)FindResource("Icon_eye");
            Password.Clear(); VisiblePassword.Clear(); editLogin = true; Render();
        }
        private void BackClicked(object sender, RoutedEventArgs args) { editLogin = false; Render(); }
        private void CandidateChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!rendering && Candidates.SelectedIndex >= 0 && !Busy)
                try { client.Select(Candidates.SelectedIndex); } catch (Exception) { ShowToast("选择失败，请重新打开工具。", false); }
        }
        private void CopyTokenClicked(object sender, RoutedEventArgs args) => Copy(snapshot?.token, "Token ");
        private void CopyDeviceClicked(object sender, RoutedEventArgs args) => Copy(snapshot?.device_id, "设备 ID ");
        private void InstallClicked(object sender, RoutedEventArgs args) => Send("certificate/install");
        private void RemoveClicked(object sender, RoutedEventArgs args) => Send("certificate/remove");
        private async void MiniProgramClicked(object sender, RoutedEventArgs args)
        {
            if (!MiniProgramButton.IsEnabled || openingMiniProgram) return;
            CancelMiniProgramReminder();
            int captureGeneration = miniProgramCaptureGeneration;
            miniProgramCaptureAt = snapshot?.state.captured_at;
            miniProgramCaptureServer = snapshot?.state.server_address;
            openingMiniProgram = true;
            bool? reopened = null;
            string failure = null;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            bool previousTopmost = (GetWindowLong(handle, -20) & 8) != 0;
            using (var operation = new CancellationTokenSource())
            {
                miniProgramOperation = operation;
                Render();
                try
                {
                    if (!SetTopmost(handle, true)) throw new InvalidOperationException("无法调整窗口层级，请重新打开工具后重试。");
                    Activate();
                    reopened = await miniProgramLauncher.OpenAsync(operation.Token);
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException error)
                {
                    failure = error.Message;
                }
                catch (Exception)
                {
                    failure = "打开失败，请确认电脑版微信已安装、打开并完成登录后重试。";
                }
                finally
                {
                    if (!closeRequested && IsVisible && WindowState != WindowState.Minimized) Activate();
                    SetTopmost(handle, previousTopmost);
                    miniProgramOperation = null;
                    openingMiniProgram = false;
                    if (IsVisible) Render();
                }
            }
            if (closeAfterMiniProgram)
            {
                closeAfterMiniProgram = false;
                if (failure != null) ShowImportantNotice("打开小程序失败", failure, false, Close);
                else _ = Dispatcher.BeginInvoke(new Action(Close));
                return;
            }
            if (closeRequested || !IsVisible) return;
            if (failure != null) ShowImportantNotice("无法打开领克小程序", failure);
            else if (reopened.HasValue)
            {
                ShowToast(reopened.Value ? "领克小程序已重新打开，正在等待凭据。" : "领克小程序已打开，正在等待凭据。");
                if (captureGeneration != miniProgramCaptureGeneration) return;
                if (ShouldPromptForMiniProgramLogin()) miniProgramCaptureTimer.Start();
                else if (snapshot?.state.capturing == false && snapshot.state.captured_at == miniProgramCaptureAt)
                    ShowImportantNotice("请先开始抓取", "小程序已打开，但工具尚未开始抓取。\n\n请先安装抓包证书并点击“开始抓取”，再点击“打开领克小程序”。", false);
            }
        }
        private void StartClicked(object sender, RoutedEventArgs args) => Send("start");
        private void StopClicked(object sender, RoutedEventArgs args) => Send("stop");
        private void RestoreClicked(object sender, RoutedEventArgs args) => Send("restore");
        private void SubmitClicked(object sender, RoutedEventArgs args) => Send("submit");
        private void ConfirmClicked(object sender, RoutedEventArgs args) => Send("confirm");
        private void CancelClicked(object sender, RoutedEventArgs args) => Send("cancel");
        private void ExitClicked(object sender, RoutedEventArgs args) => Close();
        private void WindowClosing(object sender, CancelEventArgs args)
        {
            if (!allowClose && ImportantOverlay.Visibility == Visibility.Visible) { args.Cancel = true; ImportantConfirmButton.Focus(); return; }
            if (openingMiniProgram)
            {
                args.Cancel = true;
                closeAfterMiniProgram = true;
                miniProgramOperation?.Cancel();
                return;
            }
            if (allowClose || !ready || !alive) return;
            args.Cancel = true;
            if (closeRequested) return;
            closeRequested = true;
            CancelMiniProgramReminder();
            miniProgramOperation?.Cancel();
            Render();
            try { client.Close(); }
            catch (Exception) { DiscardPendingUpdate(); closeRequested = false; alive = false; ShowToast("连接已断开，请重新打开工具检查代理恢复。", false); Render(); }
        }
        private void WindowKeyDown(object sender, KeyEventArgs args)
        {
            if (ImportantOverlay.Visibility == Visibility.Visible)
            {
                if (args.Key == Key.Enter) { args.Handled = true; ImportantConfirmed(sender, args); }
                else if (args.Key == Key.Escape) args.Handled = true;
                return;
            }
            if (args.Key == Key.Enter && LoginView.Visibility == Visibility.Visible)
            {
                IInputElement focused = FocusManager.GetFocusedElement(this);
                if (focused is Button) return;
                bool credentialFocus = focused == Username || focused == Password || focused == VisiblePassword;
                if (LoginButton.IsEnabled) { LoginClicked(sender, args); args.Handled = true; }
                else if (!credentialFocus && BrowserLoginButton.IsEnabled) { BrowserLoginClicked(sender, args); args.Handled = true; }
            }
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            if (snapshot?.state.authorizing == true) CancelBrowserLoginClicked(sender, args);
            else if (snapshot?.state.confirmation_required == true && !Busy) Send("cancel");
            else if (editLogin && (snapshot?.state.logged_in == true || snapshot?.state.local_only == true)) { editLogin = false; Render(); }
            else Close();
        }
        private static bool SetTopmost(IntPtr handle, bool value) =>
            SetWindowPos(handle, new IntPtr(value ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        private void WindowSourceInitialized(object sender, EventArgs args)
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
    }
}
