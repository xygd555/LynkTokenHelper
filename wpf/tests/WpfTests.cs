using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LynkSignTokenHelper;

internal static partial class WpfTests
{
    private static string output;
    private static string expectedDefaultServer;
    [STAThread]
    public static int Main(string[] args)
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        bool interactive = args.Length > 0 && args[0] == "--interactive";
        bool browserOnly = args.Length > 0 && args[0] == "--browser-login";
        int exitCode = 0;
        Action run = () =>
        {
            try
            {
                if (args.Length > 0 && args[0] == "--updates")
                {
                    output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "wpf-native"));
                    Directory.CreateDirectory(output);
                    UpdateServiceTests(); UpdateWorkflowTests(); ActualUpdateWorker();
                    Console.WriteLine("更新服务、文件替换与原生更新界面验证通过。");
                }
                else if (args.Length > 1 && args[0] == "--authorization-live")
                {
                    output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "wpf-native"));
                    Directory.CreateDirectory(output);
                    ActualBrowserAuthorization(args[1]);
                }
                else if (args.Length > 0 && args[0] == "--wechat-smoke") ActualMiniProgram();
                else
                {
                    output = Path.GetFullPath((interactive || browserOnly) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "wpf-native") : args.Length > 0 ? args[0] : ".");
                    expectedDefaultServer = args.Length > 1 ? args[1] : "";
                    Directory.CreateDirectory(output);
                    for (int pass = 1; pass <= (interactive ? 3 : 1); pass++)
                    {
                        BrowserLoginWorkflow();
                        if (!browserOnly)
                        { UpdateServiceTests(); UpdateWorkflowTests(); ActualUpdateWorker(); }
                        if (!browserOnly)
                        {
                        MiniProgramWorkflow();
                        MiniProgramUI();
                        MiniProgramLoginReminder();
                        ImportantNoticeWorkflow();
                        }
                        LoginLayout();
                        NativeWorkflow();
                        SmallWorkArea();
                        FatalPreservesBackup();
                        ActualCorePipe();
                        ActualCoreDisconnect();
                        Console.WriteLine("WPF 第 " + pass + " 次原生交互与布局验证通过。");
                    }
                    if (interactive) ActualMiniProgram();
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { application.Shutdown(exitCode); }
        };
        if (interactive)
        {
            var start = new Button { Content = "开始验证", Height = 42, Margin = new Thickness(0, 18, 0, 0) };
            var content = new StackPanel { Margin = new Thickness(24) };
            content.Children.Add(new TextBlock { Text = "请点击开始验证，然后约一分钟内不要操作其他窗口。测试使用模拟凭据，不会启用真实抓包。", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            content.Children.Add(start);
            var gate = new Window { Title = "领克助手 · 界面验证", Width = 440, Height = 210, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = content, Topmost = true };
            start.Click += (sender, eventArgs) =>
            {
                gate.Topmost = false;
                start.IsEnabled = false;
                start.Content = "正在验证，请稍候…";
                application.Dispatcher.BeginInvoke(run);
            };
            gate.Closed += (sender, eventArgs) => { if (start.IsEnabled) application.Shutdown(1); };
            gate.Show();
        }
        else application.Dispatcher.BeginInvoke(run);
        return application.Run();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            Pump();
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new Exception("Observable UI condition timed out.");
    }
    private static T Control<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static void Click(MainWindow window, string name)
    {
        Button button = Control<Button>(window, name);
        Check(button.IsEnabled, name + " is disabled");
        var operation = window.Dispatcher.InvokeAsync(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Until(() => operation.Status == DispatcherOperationStatus.Completed);
        Pump();
    }
    private static void Acknowledge(MainWindow window)
    {
        Check(Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible, "important notice not visible");
        Click(window, "ImportantConfirmButton");
    }
    private static void CheckImportantNotice(MainWindow window, string body)
    {
        Until(() => Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible);
        Check(Text(window, "ImportantBody").Contains(body), "unexpected important notice: " + Text(window, "ImportantBody"));
        var confirm = Control<Button>(window, "ImportantConfirmButton");
        Check(FocusManager.GetFocusedElement(window) == confirm, "important notice did not focus its confirmation button");
        confirm.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Check(FocusManager.GetFocusedElement(window) == confirm, "keyboard focus escaped the important notice");
        Check(!Control<FrameworkElement>(window, "MainView").IsEnabled && !Control<FrameworkElement>(window, "LoginView").IsEnabled, "notice did not block underlying actions");
        Rect card = Bounds(window, "ImportantCard");
        Rect button = Bounds(window, "ImportantConfirmButton");
        var root = Control<Grid>(window, "Root");
        Check(card.Left >= 0 && card.Top >= 0 && card.Right <= root.ActualWidth && card.Bottom <= root.ActualHeight, "important notice exceeds window bounds");
        Check(button.Bottom <= card.Bottom && button.Right <= card.Right, "acknowledgement button exceeds notice bounds");
    }
    private static void PressKey(MainWindow window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
        Pump();
    }
    private static void ObserveFor(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration) { Pump(); Thread.Sleep(10); }
    }
    private static Rect Bounds(MainWindow window, string name)
    {
        window.UpdateLayout();
        var element = Control<FrameworkElement>(window, name);
        return element.TransformToAncestor(Control<Grid>(window, "Root")).TransformBounds(new Rect(element.RenderSize));
    }
    private static void CheckLoginFits(MainWindow window)
    {
        var root = Control<Grid>(window, "Root");
        foreach (string name in new[] { "LoginCardHost", "Address", "Username", "Password", "LoginButton", "LoginNotice", "BackButton", "BrowserLoginButton", "LoginModeButton", "BrowserLoginActions" })
        {
            if (!Control<FrameworkElement>(window, name).IsVisible) continue;
            Rect bounds = Bounds(window, name);
            Check(bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight && bounds.Left >= 0 && bounds.Right <= root.ActualWidth,
                name + " exceeds login client area: " + bounds + " / " + root.RenderSize);
        }
        var scroll = window.FindName("LoginView") as ScrollViewer;
        Check(scroll == null || scroll.ExtentHeight <= scroll.ViewportHeight + 1, "login form requires vertical scrolling");
    }
    private static void ActualBrowserAuthorization(string address)
    {
        Uri server;
        Check(Uri.TryCreate(address, UriKind.Absolute, out server) && server.Host == "127.0.0.1", "live authorization validation requires an isolated loopback panel");
        string state = Path.Combine(output, "browser-state-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(new CoreClient(state));
        window.OpenBrowser = url => File.WriteAllText(Path.Combine(output, "authorization-url.txt"), url, new UTF8Encoding(false));
        window.Show();
        try
        {
            Control<TextBox>(window, "Address").Text = address;
            Control<CheckBox>(window, "AllowHttp").IsChecked = true;
            Until(() => Control<Button>(window, "BrowserLoginButton").IsEnabled);
            Click(window, "BrowserLoginButton");
            var watch = Stopwatch.StartNew();
            while (!Control<FrameworkElement>(window, "MainView").IsVisible && watch.Elapsed < TimeSpan.FromMinutes(2))
            {
                Pump();
                Check(Control<Grid>(window, "ImportantOverlay").Visibility != Visibility.Visible, "actual authorization failed: " + Text(window, "ImportantBody"));
                Thread.Sleep(10);
            }
            Check(Control<FrameworkElement>(window, "MainView").IsVisible && Text(window, "AccountName") == "授权测试用户", "browser authorization did not enter the native main window");
            Check(Control<TextBox>(window, "Token").Text == "", "authorization test unexpectedly captured credentials");
            Capture(window, "wpf-browser-authorized-live-100");
            Console.WriteLine("真实面板网页授权、内核回调和原生主界面切换验证通过。");
        }
        finally
        {
            if (Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible) Acknowledge(window);
            window.Close(); Until(() => !window.IsVisible);
        }
    }
    private static void BrowserLoginWorkflow()
    {
        foreach (string scenario in new[] { "success", "cancel", "browser-error", "server-switch" })
        {
            var core = new FakeCore();
            var window = new MainWindow(core, workArea: () => new Rect(0, 0, 1400, 1000));
            int opened = 0;
            window.OpenBrowser = address => { opened++; Check(address.StartsWith("https://sign.example.com/helper/authorize?request_id="), "browser opened a different server"); if (scenario == "browser-error") throw new InvalidOperationException(); };
            window.Show(); Until(() => core.Started); core.Emit(); Pump();
            Check(Control<FrameworkElement>(window, "BrowserLoginPanel").IsVisible && !Control<FrameworkElement>(window, "PasswordLoginPanel").IsVisible, "browser authorization is not the primary entry");
            if (scenario == "server-switch")
            {
                core.Snapshot.state.logged_in = true; core.Emit(); Until(() => Control<FrameworkElement>(window, "MainView").IsVisible);
                Click(window, "SwitchButton");
            }
            Control<TextBox>(window, "Address").Text = "https://sign.example.com";
            Pump();
            Capture(window, "wpf-browser-ready-100");
            Rect before = Bounds(window, "LoginCardHost");
            Click(window, "BrowserLoginButton");
            Until(() => opened == 1);
            if (scenario == "browser-error")
            {
                CheckImportantNotice(window, "默认浏览器"); Acknowledge(window);
                Until(() => Control<Button>(window, "BrowserLoginButton").IsEnabled);
            }
            else
            {
                Check(!Control<Button>(window, "BrowserLoginButton").IsEnabled && !Control<Button>(window, "LoginModeButton").IsEnabled, "authorization permits conflicting duplicate actions");
                CheckLoginFits(window);
                Check(Bounds(window, "LoginCardHost") == before, "authorization pending state moved the login card");
                core.Emit(); Pump(); Check(opened == 1, "state update opened browser again");
                Click(window, "ReopenAuthorizationButton"); Check(opened == 2, "reopen authorization did not reuse the current request");
                Capture(window, "wpf-browser-waiting-100");
                if (scenario == "cancel")
                {
                    Click(window, "CancelBrowserLoginButton"); Until(() => Control<Button>(window, "BrowserLoginButton").IsEnabled);
                    Click(window, "LoginModeButton"); Check(Control<FrameworkElement>(window, "PasswordLoginPanel").IsVisible, "cancelled authorization did not allow password fallback");
                }
                else
                {
                    if (scenario == "success") window.WindowState = WindowState.Minimized;
                    core.Snapshot.pending = core.Snapshot.state.busy = core.Snapshot.state.authorizing = false;
                    core.Snapshot.state.authorization_url = null;
                    core.Snapshot.state.logged_in = true; core.Snapshot.state.username = "测试用户"; core.Snapshot.state.server_address = "https://sign.example.com";
                    core.Emit(); Until(() => Control<FrameworkElement>(window, "MainView").IsVisible);
                    Check(window.Width == 1040 && window.Height == 720, "authorization did not automatically enter the fixed main window");
                    Check(window.WindowState == WindowState.Normal && !IsTopmost(window), "authorization did not restore a minimized tool or left permanent topmost enabled");
                }
            }
            window.Close(); Until(() => !window.IsVisible);
        }
    }
    private static void LoginLayout()
    {
        foreach (Rect area in new[] { new Rect(0, 0, 1400, 1000), new Rect(0, 0, 720, 600) })
        {
            var core = new FakeCore();
            var window = new MainWindow(core, workArea: () => area);
            window.Show();
            Until(() => core.Started);
            core.Emit(); Pump();
            CheckLoginFits(window);
            Capture(window, area.Height == 600 ? "wpf-browser-login-small-100" : "wpf-browser-login-100");
            if (area.Height == 1000) Capture(window, "wpf-browser-login-150", 1.5);
            Click(window, "LoginModeButton");
            CheckLoginFits(window);
            double buttonTop = Bounds(window, "LoginButton").Top;
            core.Snapshot.state.message = "请先在面板修改初始密码，再登录工具";
            core.Emit();
            Until(() => Control<TextBlock>(window, "LoginNotice").Visibility == Visibility.Visible);
            CheckLoginFits(window);
            Check(Math.Abs(Bounds(window, "LoginButton").Top - buttonTop) < 1, "login validation moved primary action");
            Capture(window, area.Height == 600 ? "wpf-login-error-small-100" : "wpf-login-error-100");
            if (area.Height == 1000) Capture(window, "wpf-login-error-150", 1.5);
            window.Close();
            Until(() => !window.IsVisible);
        }
    }
    private static void CheckCaptureLayout(MainWindow window, Rect[] before, string action)
    {
        string[] names = { "CaptureTitle", "Metrics", "CertificateStatus", "CaptureGuide", "CapturePanel" };
        for (int i = 0; i < names.Length; i++)
            Check(Bounds(window, names[i]) == before[i], action + " moved " + names[i]);
        Check(Bounds(window, "StartButton") == Bounds(window, "StopButton"), "capture actions occupy different slots");
    }
    private static void CheckMainFits(MainWindow window)
    {
        window.UpdateLayout();
        var scroll = Control<ScrollViewer>(window, "MainView");
        Check(scroll.ExtentHeight <= scroll.ViewportHeight + 1, "standard main view requires vertical scrolling: " + scroll.ExtentHeight + " / " + scroll.ViewportHeight);
    }
    private static void WaitForRender()
    {
        int frames = 0;
        EventHandler rendering = (sender, args) => frames++;
        CompositionTarget.Rendering += rendering;
        try { Until(() => frames >= 2); } finally { CompositionTarget.Rendering -= rendering; }
    }
    private static void Capture(MainWindow window, string name, double scale = 1)
    {
        window.UpdateLayout();
        foreach (string view in new[] { "LoginView", "MainView", "Toast" })
            Control<UIElement>(window, view).BeginAnimation(UIElement.OpacityProperty, null);
        WaitForRender();
        var visual = Control<Grid>(window, "Root");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
    }
    private static void FatalPreservesBackup()
    {
        var core = new FakeCore();
        var window = new MainWindow(core);
        string copied = null;
        window.CopyText = value => copied = value;
        window.Show();
        Until(() => core.Started);
        core.Snapshot.state.logged_in = true;
        core.Capture();
        Until(() => Control<Button>(window, "CopyTokenButton").IsEnabled);
        core.Fail();
        Until(() => Text(window, "NoticeBody") == "内核连接测试失败");
        CheckImportantNotice(window, "内核连接测试失败");
        Acknowledge(window);
        Check(!Control<Button>(window, "SubmitButton").IsEnabled, "dead core allowed new command");
        Click(window, "CopyTokenButton");
        Check(copied == "rt_first_synthetic", "core failure lost backup token");
        window.Close();
        Until(() => !window.IsVisible);
    }
    private static void NativeWorkflow()
    {
        var core = new FakeCore();
        var window = new MainWindow(core, workArea: () => new Rect(0, 0, 1400, 1000));
        string copied = null;
        window.CopyText = value => copied = value;
        window.Show();
        Until(() => core.Started);
        core.Emit();
        Until(() => Text(window, "LoginButtonText") == "登录服务器");
        Check(Control<TextBox>(window, "Address").Text == expectedDefaultServer, "build-time default server was not prefilled");
        Check(window.ResizeMode == ResizeMode.CanMinimize && window.Width == 520 && window.Height == 700, "login window is not fixed");
        Click(window, "LoginModeButton");
        Control<TextBox>(window, "Address").Text = "https://sign.example.com:8443/lynk";
        Control<TextBox>(window, "Username").Text = "测试用户";
        Control<PasswordBox>(window, "Password").Password = "synthetic-password";
        window.UpdateLayout(); Pump();
        foreach (string field in new[] { "Address", "Username" })
        {
            var input = Control<TextBox>(window, field);
            var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
            Rect character = input.GetRectFromCharacterIndex(0);
            Check(host.ViewportHeight >= input.FontSize && character.Bottom <= input.ActualHeight, field + " text is visually clipped");
        }
        var passwordHost = (ScrollViewer)Control<PasswordBox>(window, "Password").Template.FindName("PART_ContentHost", Control<PasswordBox>(window, "Password"));
        Check(passwordHost.ViewportHeight >= 14, "password text is visually clipped");
        Capture(window, "wpf-login-100");
        Click(window, "TogglePassword");
        Check(Control<TextBox>(window, "VisiblePassword").Text == "synthetic-password", "password reveal lost value");
        Click(window, "TogglePassword");
        Click(window, "LoginButton");
        Until(() => Control<ScrollViewer>(window, "MainView").Visibility == Visibility.Visible);
        Check(Control<PasswordBox>(window, "Password").Password == "" && Control<TextBox>(window, "VisiblePassword").Text == "", "submitted password retained in UI");
        Check(window.Width == 1040 && window.Height == 720 && window.ResizeMode == ResizeMode.CanMinimize, "main window is not fixed");
        Check(!Control<Button>(window, "StartButton").IsEnabled && Control<Button>(window, "InstallButton").Visibility == Visibility.Visible, "certificate prerequisite not expressed");
        Capture(window, "wpf-ready-100");
        Click(window, "InstallButton");
        Until(() => Control<Button>(window, "StartButton").IsEnabled);
        Capture(window, "wpf-installed-ready-100");
        var captureLayout = new[] { Bounds(window, "CaptureTitle"), Bounds(window, "Metrics"), Bounds(window, "CertificateStatus"), Bounds(window, "CaptureGuide"), Bounds(window, "CapturePanel") };
        CheckMainFits(window);
        Click(window, "StartButton");
        Until(() => Control<Button>(window, "StopButton").Visibility == Visibility.Visible);
        CheckCaptureLayout(window, captureLayout, "start");
        Check(Text(window, "CaptureTitle") == "正在抓取", "capture status not updated");
        CheckMainFits(window);
        Capture(window, "wpf-listening-100");
        Capture(window, "wpf-listening-150", 1.5);
        Click(window, "StopButton");
        Until(() => Control<Button>(window, "StartButton").Visibility == Visibility.Visible);
        CheckCaptureLayout(window, captureLayout, "stop");
        Click(window, "StartButton");
        Until(() => Control<Button>(window, "StopButton").Visibility == Visibility.Visible);
        core.Capture();
        Until(() => Control<ComboBox>(window, "Candidates").Items.Count == 2);
        CheckCaptureLayout(window, captureLayout, "capture");
        Control<ComboBox>(window, "Candidates").SelectedIndex = 1;
        Until(() => Control<TextBox>(window, "Token").Text == core.LongToken);
        Click(window, "CopyTokenButton");
        Check(copied == core.LongToken, "copy token truncated");
        Click(window, "CopyDeviceButton");
        Check(copied == core.Snapshot.device_id, "copy device changed value");
        string large = "rt_large_" + new string('b', 24000);
        core.Snapshot.token = large;
        core.Emit();
        Until(() => Control<TextBox>(window, "Token").Text.Length == large.Length);
        Click(window, "CopyTokenButton");
        Check(copied == large, "large token copy truncated");
        core.Snapshot.token = core.LongToken;
        core.Emit();
        Until(() => Control<TextBox>(window, "Token").Text == core.LongToken);
        Click(window, "SubmitButton");
        Until(() => Text(window, "NoticeTitle").Contains("Token 已保留"));
        CheckImportantNotice(window, "HTTP 503");
        Capture(window, "wpf-submit-error-dialog-100");
        Acknowledge(window);
        Check(Control<Button>(window, "SubmitButton").IsEnabled && Control<TextBox>(window, "Token").Text == core.LongToken, "failed submit lost token or retry");
        CheckMainFits(window);
        Capture(window, "wpf-failure-100");
        Click(window, "SubmitButton");
        Until(() => Control<Border>(window, "Confirmation").Visibility == Visibility.Visible);
        Check(Control<StackPanel>(window, "CandidateField").Visibility == Visibility.Collapsed, "confirmation did not pin selection");
        CheckMainFits(window);
        Capture(window, "wpf-confirmation-100");
        Capture(window, "wpf-confirmation-200", 2);
        Click(window, "ConfirmButton");
        Until(() => Text(window, "NoticeTitle") == "换绑确认未完成");
        CheckImportantNotice(window, "HTTP 503");
        Acknowledge(window);
        Check(Control<Button>(window, "ConfirmButton").IsEnabled && Control<Button>(window, "CopyTokenButton").IsEnabled, "confirmation retry blocked");
        Click(window, "ConfirmButton");
        Until(() => Text(window, "NoticeTitle") == "已保存到服务器");
        Check(Control<TextBox>(window, "Token").Text == core.LongToken && Control<Button>(window, "CopyTokenButton").IsEnabled, "saved backup lost");
        Capture(window, "wpf-saved-100");
        Control<ComboBox>(window, "Candidates").SelectedIndex = 0;
        Until(() => Text(window, "NoticeTitle") == "当前选择尚未提交");
        Click(window, "SubmitButton");
        Until(() => Control<Border>(window, "Confirmation").Visibility == Visibility.Visible);
        Click(window, "CancelButton");
        Until(() => Text(window, "NoticeTitle") == "已取消换绑");
        Check(Control<Button>(window, "CopyTokenButton").IsEnabled && Control<TextBox>(window, "Token").Text == "rt_first_synthetic", "cancel discarded backup");
        window.CopyText = value => throw new IOException();
        Click(window, "CopyTokenButton");
        CheckImportantNotice(window, "剪贴板");
        Acknowledge(window);
        window.CopyText = value => copied = value;
        Click(window, "SwitchButton");
        Until(() => Control<FrameworkElement>(window, "LoginView").Visibility == Visibility.Visible);
        CheckLoginFits(window);
        Capture(window, "wpf-switch-server-100");
        Check(Control<TextBox>(window, "VisiblePassword").Visibility == Visibility.Collapsed && Control<PasswordBox>(window, "Password").Password == "", "switch did not reset password");
        Control<PasswordBox>(window, "Password").Password = "synthetic-password";
        Click(window, "LoginButton");
        Until(() => Control<ScrollViewer>(window, "MainView").Visibility == Visibility.Visible);
        core.FailFirstClose = true;
        window.Close();
        Until(() => Text(window, "ImportantBody").Contains("代理恢复测试失败"));
        Acknowledge(window);
        Until(() => Control<Button>(window, "RestoreButton").IsEnabled);
        Check(window.IsVisible, "restore failure closed window");
        window.Close();
        Until(() => !window.IsVisible);
        Check(core.Disposed, "window close did not dispose pipe client");
    }
    private static void SmallWorkArea()
    {
        var core = new FakeCore();
        var window = new MainWindow(core, workArea: () => new Rect(0, 0, 720, 600));
        window.Show();
        Until(() => core.Started);
        core.Snapshot.state.logged_in = core.Snapshot.state.trusted = true;
        core.Snapshot.state.username = "周末去看海";
        core.Snapshot.state.server_address = "https://sign.example.com";
        core.Capture();
        core.Snapshot.state.confirmation_required = true;
        core.Snapshot.state.account = new AccountInfo { id = "2000000000000000000", display_name = "周末去看海" };
        core.Emit();
        Until(() => Control<Border>(window, "Confirmation").Visibility == Visibility.Visible);
        window.UpdateLayout();
        Check(window.Width <= 720 && window.Height <= 600, "fixed window exceeded work area");
        var scroll = Control<ScrollViewer>(window, "MainView");
        Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "small work area horizontally overflowed");
        scroll.ScrollToBottom();
        Pump(); window.UpdateLayout();
        var button = Control<Button>(window, "ConfirmButton");
        var root = Control<Grid>(window, "Root");
        Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
        Check(bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight, "confirmation button unreachable in small work area");
        Capture(window, "wpf-small-work-area-100");
        window.Close();
        Until(() => !window.IsVisible);
    }
    private static void ActualCorePipe()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool stop = false;
        var served = new List<string>();
        var server = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                TcpClient socket;
                try { socket = listener.AcceptTcpClient(); } catch (SocketException) { break; }
                using (socket)
                using (var stream = socket.GetStream())
                {
                    var bytes = new List<byte>();
                    int value;
                    while ((value = stream.ReadByte()) >= 0)
                    {
                        bytes.Add((byte)value);
                        int size = bytes.Count;
                        if (size >= 4 && bytes[size-4] == 13 && bytes[size-3] == 10 && bytes[size-2] == 13 && bytes[size-1] == 10) break;
                    }
                    string header = Encoding.ASCII.GetString(bytes.ToArray());
                    int length = 0;
                    foreach (string line in header.Split(new[] { "\r\n" }, StringSplitOptions.None))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                    byte[] body = new byte[length];
                    for (int read = 0; read < length;) { int count = stream.Read(body, read, length-read); if (count == 0) break; read += count; }
                    string payload = Encoding.UTF8.GetString(body);
                    lock (served) served.Add(header + payload);
                    string response = header.StartsWith("POST /api/auth/login ") ? "{\"csrf_token\":\"synthetic-csrf\"}" : "{\"username\":\"测试用户\",\"entitlement\":{\"active\":true}}";
                    byte[] content = Encoding.UTF8.GetBytes(response);
                    byte[] responseHead = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nSet-Cookie: lynk_session=synthetic; Path=/; HttpOnly\r\nConnection: close\r\nContent-Length: " + content.Length + "\r\n\r\n");
                    stream.Write(responseHead,0,responseHead.Length); stream.Write(content,0,content.Length);
                }
            }
        });
        string state = Path.Combine(output, "core-state-" + Guid.NewGuid().ToString("N"));
        var client = new CoreClient(state);
        CoreSnapshot snapshot = null;
        string failure = null;
        bool closed = false;
        client.Updated += value => Volatile.Write(ref snapshot, value);
        client.Faulted += message => Volatile.Write(ref failure, message);
        client.Closed += () => Volatile.Write(ref closed, true);
        try
        {
            client.Start();
            Until(() => Volatile.Read(ref snapshot) != null || Volatile.Read(ref failure) != null);
            Check(failure == null, "actual core startup failed: " + failure);
            Check(snapshot.default_server == expectedDefaultServer, "UI and core default servers differ");
            client.Command("login", new { Address = "http://127.0.0.1:" + port, Username = "测试用户", Password = "synthetic-password", AllowHTTP = true });
            Until(() => Volatile.Read(ref snapshot).state.logged_in || Volatile.Read(ref failure) != null);
            Check(failure == null && snapshot.state.username == "测试用户", "UTF-8 core login failed");
            lock (served) Check(served.Count == 2 && served[0].Contains("测试用户") && served[1].Contains("lynk_session=synthetic"), "real pipe changed login data or cookies");
            client.Close();
            Until(() => Volatile.Read(ref closed));
        }
        finally { client.Dispose(); Volatile.Write(ref stop, true); listener.Stop(); server.Wait(3000); }
    }
    private static void ActualCoreDisconnect()
    {
        string state = Path.Combine(output, "disconnect-state-" + Guid.NewGuid().ToString("N"));
        var client = new CoreClient(state);
        CoreSnapshot snapshot = null;
        string failure = null;
        client.Updated += value => Volatile.Write(ref snapshot, value);
        client.Faulted += message => Volatile.Write(ref failure, message);
        client.Start();
        Until(() => Volatile.Read(ref snapshot) != null || Volatile.Read(ref failure) != null);
        Check(failure == null, "disconnect fixture failed to start");
        var process = (Process)typeof(CoreClient).GetField("process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(client);
        using (Process observed = Process.GetProcessById(process.Id))
        {
            client.Dispose();
            Check(observed.WaitForExit(5000), "closed desktop pipe left core process running");
        }
    }
    private static bool OpenMiniProgram(LynkMiniProgramLauncher launcher, CancellationToken token = default(CancellationToken))
    {
        Task<bool> task = launcher.OpenAsync(token);
        Until(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }
    private static void MiniProgramFailure(FakeMiniProgramDesktop desktop, string message)
    {
        try { OpenMiniProgram(new LynkMiniProgramLauncher(desktop, TimeSpan.FromMilliseconds(1))); }
        catch (InvalidOperationException error) { Check(error.Message.Contains(message), "unexpected mini program failure: " + error.Message); return; }
        throw new Exception("mini program failure was not reported");
    }
    private static void MiniProgramWorkflow()
    {
        var desktop = new FakeMiniProgramDesktop();
        Check(!OpenMiniProgram(new LynkMiniProgramLauncher(desktop)), "first open reported a restart");
        Check(desktop.Calls.SequenceEqual(new[] { "launch" }), "first open closed a window");
        desktop.Calls.Clear();
        Check(OpenMiniProgram(new LynkMiniProgramLauncher(desktop)), "existing window was not restarted");
        Check(desktop.Calls.SequenceEqual(new[] { "close:1", "launch" }), "existing window was not closed before launch");
        desktop = new FakeMiniProgramDesktop { Available = false, Count = 1 };
        MiniProgramFailure(desktop, "安装");
        Check(desktop.Calls.Count == 0, "missing WeChat closed an existing window");
        desktop = new FakeMiniProgramDesktop { Running = false, Count = 1 };
        MiniProgramFailure(desktop, "微信尚未打开");
        Check(desktop.Calls.Count == 0, "unopened WeChat changed existing windows");
        desktop = new FakeMiniProgramDesktop { Count = 2 };
        MiniProgramFailure(desktop, "多个");
        Check(desktop.Calls.Count == 0, "ambiguous WeChat windows were changed");
        desktop = new FakeMiniProgramDesktop { Count = 1, RefuseClose = true };
        MiniProgramFailure(desktop, "没有关闭");
        Check(desktop.Calls.SequenceEqual(new[] { "close:1" }), "failed close still launched another instance");
        desktop = new FakeMiniProgramDesktop { RefuseOpen = true };
        MiniProgramFailure(desktop, "登录页面");
        Check(desktop.Calls.SequenceEqual(new[] { "launch" }), "open timeout retried unexpectedly");
        desktop = new FakeMiniProgramDesktop { Count = 1 };
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { OpenMiniProgram(new LynkMiniProgramLauncher(desktop), cancellation.Token); throw new Exception("cancel was ignored"); }
            catch (OperationCanceledException) { }
        }
        Check(desktop.Calls.Count == 0, "cancelled operation changed WeChat");
    }
    private static void MiniProgramUI()
    {
        var core = new FakeCore();
        var launcher = new FakeMiniProgramLauncher();
        var window = new MainWindow(core, workArea: () => new Rect(0, 0, 1400, 1000), miniProgramLauncher: launcher);
        window.Show();
        Until(() => core.Started);
        core.Snapshot.state.logged_in = core.Snapshot.state.trusted = true;
        core.Snapshot.state.username = "测试用户";
        core.Snapshot.state.server_address = "https://sign.example.com";
        core.Emit();
        Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
        WaitForRender();
        Rect buttonBounds = Bounds(window, "MiniProgramButton");
        Rect panelBounds = Bounds(window, "CapturePanel");
        CheckMainFits(window);
        Click(window, "MiniProgramButton");
        WaitForRender();
        Check(!Control<Button>(window, "MiniProgramButton").IsEnabled && Text(window, "MiniProgramButtonText") == "正在打开…", "open action has no pending feedback");
        Check(IsTopmost(window), "helper is not kept above WeChat during launch");
        var competingWindow = new Window { Title = "窗口层级测试", Width = 300, Height = 200, ShowInTaskbar = false };
        competingWindow.Show();
        competingWindow.Activate();
        Pump();
        Check(IsAbove(window, new WindowInteropHelper(competingWindow).Handle), "launching window covered the helper");
        Control<Button>(window, "MiniProgramButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(launcher.Calls == 1, "duplicate click launched WeChat twice");
        core.Snapshot.state.capturing = true;
        core.Snapshot.state.message = "等待领克登录流量，请点击工具里的“打开领克小程序”。未获取到凭据时，请登录或退出账号后重新登录。";
        core.Emit(); Pump();
        Check(Bounds(window, "MiniProgramButton") == buttonBounds && Bounds(window, "CapturePanel") == panelBounds, "open progress or capture changed the panel layout");
        Check(IsTopmost(window), "capture state update removed temporary topmost state");
        Capture(window, "wpf-mini-program-pending-100");
        launcher.Pending.SetResult(true);
        Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
        Check(Text(window, "ToastText").Contains("重新打开"), "restart result not shown");
        Check(!window.Topmost && !IsTopmost(window) && window.IsActive, "successful launch did not restore normal foreground state");
        competingWindow.Close();
        Capture(window, "wpf-mini-program-opened-100");
        Click(window, "MiniProgramButton");
        launcher.Pending.SetException(new InvalidOperationException("请先登录电脑版微信。"));
        Until(() => Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible);
        CheckImportantNotice(window, "请先登录电脑版微信。");
        Capture(window, "wpf-mini-program-error-dialog-100");
        Acknowledge(window);
        Check(!window.Topmost && !IsTopmost(window), "failed launch left the helper permanently topmost");
        Click(window, "MiniProgramButton");
        launcher.Pending.SetCanceled();
        Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
        Check(!window.Topmost && !IsTopmost(window), "cancelled launch left the helper permanently topmost");
        SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
        Click(window, "MiniProgramButton");
        launcher.Pending.SetResult(false);
        Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
        Check(IsTopmost(window), "launch discarded the previous topmost state");
        SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13);
        Click(window, "MiniProgramButton");
        window.Close();
        Until(() => !window.IsVisible);
        Check(launcher.Pending.Task.IsCanceled, "closing helper did not cancel pending mini program operation");
    }
    private static void MiniProgramLoginReminder()
    {
        foreach (string scenario in new[] { "missing", "old-token", "captured", "captured-during-open", "stopped-during-open", "restarted-during-open", "stopped", "restarted", "server", "not-capturing" })
        {
            var core = new FakeCore();
            var launcher = new FakeMiniProgramLauncher();
            var window = new MainWindow(core, miniProgramLauncher: launcher, miniProgramCaptureDelay: TimeSpan.FromMilliseconds(150));
            window.Show();
            Until(() => core.Started);
            core.Snapshot.state.logged_in = core.Snapshot.state.trusted = true;
            core.Snapshot.state.server_address = "https://sign.example.com";
            core.Snapshot.state.capturing = scenario != "not-capturing";
            if (scenario == "old-token") { core.Snapshot.token = "rt_previous_synthetic"; core.Snapshot.state.captured_at = "2026-10-15T09:00:00+08:00"; }
            core.Emit();
            Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
            Click(window, "MiniProgramButton");
            if (scenario == "captured-during-open") { core.Capture(); Pump(); }
            if (scenario == "stopped-during-open" || scenario == "restarted-during-open")
            {
                Click(window, "StopButton");
                Until(() => Control<Button>(window, "StartButton").Visibility == Visibility.Visible);
                if (scenario == "restarted-during-open") { Click(window, "StartButton"); Until(() => Control<Button>(window, "StopButton").Visibility == Visibility.Visible); }
            }
            launcher.Pending.SetResult(false);
            Until(() => Text(window, "ToastText").Contains("已打开"));
            if (scenario == "captured") { core.Capture(); Pump(); }
            if (scenario == "stopped" || scenario == "restarted")
            {
                Click(window, "StopButton");
                Until(() => Control<Button>(window, "StartButton").Visibility == Visibility.Visible);
                if (scenario == "restarted") { Click(window, "StartButton"); Until(() => Control<Button>(window, "StopButton").Visibility == Visibility.Visible); }
            }
            if (scenario == "server") { core.Snapshot.state.server_address = "https://other.example.com"; core.Emit(); Pump(); }
            bool expected = scenario == "missing" || scenario == "old-token" || scenario == "not-capturing";
            if (expected)
            {
                CheckImportantNotice(window, scenario == "not-capturing" ? "尚未开始抓取" : "退出账号后重新登录");
                if (scenario == "missing")
                {
                    Capture(window, "wpf-login-reminder-100");
                    Capture(window, "wpf-login-reminder-150", 1.5);
                    ObserveFor(TimeSpan.FromMilliseconds(250));
                    Check(Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible, "important notice disappeared without acknowledgement");
                    PressKey(window, Key.Escape);
                    window.Close(); Pump();
                    Check(window.IsVisible && Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible, "important notice was skipped without confirmation");
                    Check(core.Snapshot.state.capturing, "login reminder stopped background capture");
                }
                Acknowledge(window);
                Check(Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Collapsed, "confirmed reminder stayed visible");
            }
            ObserveFor(TimeSpan.FromMilliseconds(220));
            Check(Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Collapsed, "stale or repeated login reminder: " + scenario);
            window.Close(); Until(() => !window.IsVisible);
        }
    }
    private static void ImportantNoticeWorkflow()
    {
        foreach (Rect area in new[] { new Rect(0, 0, 1400, 1000), new Rect(0, 0, 720, 600), new Rect(0, 0, 380, 384) })
        {
            var core = new FakeCore();
            var window = new MainWindow(core, workArea: () => area);
            window.Show(); Until(() => core.Started);
            var competingWindow = new Window { Title = "提示层级测试", Width = 300, Height = 200, ShowInTaskbar = false };
            competingWindow.Show();
            competingWindow.Activate();
            core.Snapshot.error = "未找到微信启动入口。请先安装电脑版微信，打开并完成登录后再重试。";
            core.Emit();
            CheckImportantNotice(window, "安装电脑版微信");
            Check(IsAbove(window, new WindowInteropHelper(competingWindow).Handle) && !IsTopmost(window), "important notice did not bring the helper forward normally");
            competingWindow.Close();
            core.Emit(); Pump();
            core.Snapshot.error = "微信尚未打开。请先打开并登录电脑版微信。";
            core.Emit(); Pump();
            Check(Text(window, "ImportantBody").Contains("安装电脑版微信"), "queued notice replaced an unconfirmed message");
            Capture(window, area.Width == 1400 ? "wpf-wechat-install-dialog-100" : area.Width == 720 ? "wpf-wechat-dialog-small-100" : "wpf-wechat-dialog-minimum-100");
            Acknowledge(window);
            CheckImportantNotice(window, "微信尚未打开");
            PressKey(window, Key.Enter);
            Check(Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Collapsed, "keyboard confirmation did not dismiss notice");
            Check(Control<FrameworkElement>(window, "LoginView").IsEnabled, "confirmation did not restore form interaction");
            core.Snapshot.error = null;
            core.Snapshot.token = "rt_background_synthetic";
            core.Snapshot.state.submission = "failed";
            core.Emit();
            CheckImportantNotice(window, "Token 已保留");
            core.Emit(); Pump();
            Acknowledge(window);
            Check(Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Collapsed, "repeated snapshot duplicated the submission alert");
            window.Close(); Until(() => !window.IsVisible);
        }
    }
    private static bool IsTopmost(Window window) => (GetWindowLong(new WindowInteropHelper(window).Handle, -20) & 8) != 0;
    private static bool IsAbove(Window window, IntPtr other)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        for (IntPtr previous = GetWindow(other, 3); previous != IntPtr.Zero; previous = GetWindow(previous, 3))
            if (previous == handle) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    private static void ActualMiniProgram()
    {
        var desktop = new WindowsMiniProgramDesktop();
        var core = new FakeCore();
        var window = new MainWindow(core, miniProgramLauncher: new LynkMiniProgramLauncher(desktop));
        try
        {
            window.Show();
            Until(() => core.Started);
            core.Snapshot.state.logged_in = core.Snapshot.state.trusted = true;
            core.Snapshot.state.username = "测试用户";
            core.Snapshot.state.server_address = "https://sign.example.com";
            core.Emit();
            Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
            core.Snapshot.state.capturing = true;
            core.Emit(); Pump();
            WaitForRender();
            for (int run = 1; run <= 2; run++)
            {
                window.Activate();
                bool reopening = desktop.FindWindows().Count == 1;
                Click(window, "MiniProgramButton");
                Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled || Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible);
                Check(Control<Grid>(window, "ImportantOverlay").Visibility != Visibility.Visible, "actual WeChat launch failed: " + Text(window, "ImportantBody"));
                Check(Text(window, "ToastText") == (reopening ? "领克小程序已重新打开，正在等待凭据。" : "领克小程序已打开，正在等待凭据。"), "actual WeChat launch failed: " + Text(window, "ToastText"));
                Check(desktop.FindWindows().Count == 1, "actual WeChat did not show a single Lynk window");
                Check(!window.Topmost && !IsTopmost(window), "actual launch left the helper permanently topmost");
                var settling = Stopwatch.StartNew();
                while (settling.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Pump();
                    Check(IsAbove(window, desktop.FindWindows().Single()), "actual WeChat covered the helper after launch");
                    Thread.Sleep(10);
                }
                Console.WriteLine("第 " + run + " 次：助手位于小程序前方，置顶已恢复；前台为助手：" + (GetForegroundWindow() == new WindowInteropHelper(window).Handle));
            }
            var competingWindow = new Window { Title = "窗口层级测试", Width = 300, Height = 200, ShowInTaskbar = false };
            competingWindow.Show();
            competingWindow.Activate();
            Pump();
            Check(IsAbove(competingWindow, new WindowInteropHelper(window).Handle), "helper prevented normal window switching after launch");
            competingWindow.Close();
            Console.WriteLine("真实微信打开、重开及助手窗口前台验证通过。");
        }
        finally { if (Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible) Acknowledge(window); window.Close(); Until(() => !window.IsVisible); }
    }
    private sealed class FakeMiniProgramDesktop : IMiniProgramDesktop
    {
        public bool Available = true, Running = true, RefuseClose, RefuseOpen;
        public int Count;
        public List<string> Calls = new List<string>();
        public bool IsAvailable => Available;
        public bool IsRunning => Running;
        public IReadOnlyList<IntPtr> FindWindows() => Enumerable.Range(1, Count).Select(value => new IntPtr(value)).ToArray();
        public void Close(IntPtr handle) { Calls.Add("close:" + handle); if (!RefuseClose) Count = 0; }
        public bool IsOpen(IntPtr handle) => Count > 0;
        public void Launch() { Calls.Add("launch"); if (!RefuseOpen) Count = 1; }
    }
    private sealed class FakeMiniProgramLauncher : IMiniProgramLauncher
    {
        public int Calls;
        public TaskCompletionSource<bool> Pending;
        public Task<bool> OpenAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Pending = new TaskCompletionSource<bool>();
            cancellationToken.Register(() => Pending.TrySetCanceled());
            return Pending.Task;
        }
    }
    private sealed class FakeCore : ICoreClient
    {
        public event Action<CoreSnapshot> Updated;
        public event Action<string> Faulted;
        public event Action Closed;
        public bool Started, Disposed, FailFirstClose;
        public string LongToken = "rt_synthetic_" + string.Concat(Enumerable.Repeat("a1b2c3d4",32));
        public CoreSnapshot Snapshot = new CoreSnapshot { type = "snapshot", protocol = 1, state = new AppState { message = "请先登录服务器" } };
        private long sequence;
        private int submits, confirmations;
        public void Start() { Started = true; }
        public void Fail() { Faulted?.Invoke("内核连接测试失败"); }
        public void Emit() { Updated?.Invoke(new JavaScriptSerializer().Deserialize<CoreSnapshot>(new JavaScriptSerializer().Serialize(Snapshot))); }
        public long Command(string action, object input = null)
        {
            Snapshot.request_id = ++sequence;
            Snapshot.error = null;
            switch (action)
            {
                case "login":
                    var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(new JavaScriptSerializer().Serialize(input));
                    Check((string)values["Username"] == "测试用户" && (string)values["Password"] == "synthetic-password", "native login input changed");
                    Snapshot.state.logged_in = true; Snapshot.state.username = "周末去看海"; Snapshot.state.server_address = "https://sign.example.com"; Snapshot.state.message = "服务器登录成功"; Snapshot.token = null; Snapshot.state.candidates = null; Snapshot.state.submission = null; Snapshot.state.confirmation_required = false;
                    break;
                case "login/browser":
                    var browserInput = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(new JavaScriptSerializer().Serialize(input));
                    Check(!browserInput.ContainsKey("Password") && !browserInput.ContainsKey("Username"), "browser login sent account credentials");
                    Snapshot.state.logged_in = false; Snapshot.pending = Snapshot.state.busy = Snapshot.state.authorizing = true;
                    Snapshot.state.authorization_url = "https://sign.example.com/helper/authorize?request_id=" + new string('r', 43);
                    Snapshot.state.message = "请在浏览器中确认授权。";
                    break;
                case "login/cancel":
                    Snapshot.pending = Snapshot.state.busy = Snapshot.state.authorizing = false; Snapshot.state.authorization_url = null;
                    Snapshot.state.message = "网页授权已取消，可以重新授权或使用账号密码登录。";
                    break;
                case "certificate/install": Snapshot.state.trusted = true; break;
                case "start": Snapshot.state.capturing = true; Snapshot.state.message = "等待领克登录流量，请点击工具里的“打开领克小程序”。未获取到凭据时，请登录或退出账号后重新登录。"; break;
                case "stop": Snapshot.state.capturing = false; break;
                case "submit":
                    Snapshot.state.selected_candidate = Snapshot.selected;
                    if (++submits == 1) { Snapshot.state.submission = "failed"; Snapshot.error = "服务器返回 HTTP 503，请检查面板状态"; }
                    else { Snapshot.state.submission = "confirmation"; Snapshot.state.confirmation_required = true; Snapshot.state.account = new AccountInfo { id="2000000000000000000", display_name="周末去看海" }; Snapshot.state.previous_account_id="1000000000000000000"; }
                    break;
                case "confirm":
                    if (++confirmations == 1) { Snapshot.error = "服务器返回 HTTP 503，请检查面板状态"; Snapshot.state.submission = "failed"; }
                    else { Snapshot.state.submission="saved"; Snapshot.state.confirmation_required=false; }
                    break;
                case "cancel": Snapshot.state.confirmation_required=false; Snapshot.state.submission="cancelled"; break;
            }
            Emit();
            return Snapshot.request_id;
        }
        public void Capture()
        {
            Snapshot.state.capturing = false;
            Snapshot.state.connections=46; Snapshot.state.lynk_requests=9;
            Snapshot.state.captured_at="2026-10-15T09:32:08+08:00";
            Snapshot.state.candidates=new[] { new CandidateInfo { id="0", device_id="device-one",source="响应正文" }, new CandidateInfo { id="1",device_id="4c8f20a1-37d6-4b91-a582-6e13f0c249ab",source="响应正文" } };
            Snapshot.token="rt_first_synthetic"; Snapshot.device_id="device-one";
            Emit();
        }
        public void Select(int index) { Snapshot.selected=index; Snapshot.token=index==1?LongToken:"rt_first_synthetic"; Snapshot.device_id=Snapshot.state.candidates[index].device_id; Snapshot.error=null; Emit(); }
        public void Close()
        {
            if (FailFirstClose) { FailFirstClose=false; Snapshot.error="代理恢复测试失败"; Emit(); }
            else Closed?.Invoke();
        }
        public void Dispose() { Disposed=true; }
    }
}
