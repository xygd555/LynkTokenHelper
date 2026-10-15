using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LynkSignTokenHelper;

internal static partial class WpfTests
{
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
    private static string TestHash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static T AwaitUpdate<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void RejectUpdate<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception(message);
    }
    private static UpdateRelease SyntheticRelease(byte[] bytes = null) => new UpdateRelease
    {
        Version = "v9.1.0", Notes = "新增自动检测与一键更新。\n\n修复连接问题，并保留证书。\n" + string.Concat(Enumerable.Repeat("更新说明支持换行，长内容可滚动查看。\n", 12)),
        DownloadUrl = "https://github.com/xygd555/LynkTokenHelper/releases/download/v9.1.0/LynkSignTokenHelper-windows-x64.exe",
        Sha256 = TestHash(bytes ?? new byte[128]), Size = (bytes ?? new byte[128]).Length
    };
    private static string ReleaseJson(UpdateRelease value, bool draft = false, bool prerelease = false) => new JavaScriptSerializer().Serialize(new
    {
        tag_name = value.Version, body = value.Notes, draft, prerelease,
        assets = new[] { new { name = GitHubToolUpdater.AssetName, state = "uploaded", size = value.Size, digest = "sha256:" + value.Sha256, browser_download_url = value.DownloadUrl } }
    });
    private sealed class UpdateHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request, cancellationToken);
    }
    private static void UpdateServiceTests()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 180000));
        UpdateRelease value = SyntheticRelease(bytes);
        Check(GitHubToolUpdater.ParseRelease(ReleaseJson(value), "v1.9.0").Version == "v9.1.0", "new release not detected");
        Check(GitHubToolUpdater.ParseVersion("v1.10.0") > GitHubToolUpdater.ParseVersion("v1.9.0"), "versions compared lexically");
        Check(GitHubToolUpdater.ParseRelease(ReleaseJson(value), "v9.1.0") == null, "same version offered");
        Check(GitHubToolUpdater.ParseRelease(ReleaseJson(value), "v10.0.0") == null, "downgrade offered");
        Check(GitHubToolUpdater.ParseRelease(ReleaseJson(value, prerelease: true), "v1.0.0") == null, "prerelease offered");
        Check(GitHubToolUpdater.ParseRelease(ReleaseJson(value, draft: true), "v1.0.0") == null, "draft offered");
        RejectUpdate<InvalidDataException>(() => GitHubToolUpdater.ParseRelease("{", "v1.0.0"), "invalid metadata accepted");
        foreach (string address in new[] { value.DownloadUrl.Replace("https:", "http:"), value.DownloadUrl.Replace("github.com/", "github.com.evil.example/"), value.DownloadUrl.Replace("LynkTokenHelper/releases", "other/releases"), value.DownloadUrl + "?source=other" })
        {
            var invalid = SyntheticRelease(bytes); invalid.DownloadUrl = address;
            RejectUpdate<InvalidDataException>(() => GitHubToolUpdater.ParseRelease(ReleaseJson(invalid), "v1.0.0"), "unsafe release accepted");
        }
        var noHash = SyntheticRelease(); noHash.Sha256 = "";
        RejectUpdate<InvalidDataException>(() => GitHubToolUpdater.ParseRelease(ReleaseJson(noHash), "v1.0.0"), "missing digest accepted");
        string root = Path.Combine(output, "update-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (bool fail in new[] { false, true })
        {
            var handler = new UpdateHttpHandler { Respond = (request, token) =>
            {
                Check(request.RequestUri.AbsoluteUri == "https://api.github.com/repos/xygd555/LynkTokenHelper/releases/latest", "update check used a different repository");
                return Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(value)) });
            } };
            var service = new GitHubToolUpdater(root, () => new HttpClient(handler));
            if (fail) RejectUpdate<IOException>(() => AwaitUpdate(service.CheckAsync(CancellationToken.None)), "GitHub API failure not reported");
            else Check(AwaitUpdate(service.CheckAsync(CancellationToken.None)).Version == value.Version, "GitHub API metadata not parsed");
        }
        foreach (string scenario in new[] { "success", "checksum", "size", "redirect", "unsafe-redirect", "cancel" })
        {
            int requests = 0, progress = 0;
            var handler = new UpdateHttpHandler { Respond = async (request, token) =>
            {
                requests++;
                Check(request.Headers.UserAgent.ToString().StartsWith("LynkTokenHelper/"), "missing GitHub user agent");
                Check(request.Headers.Authorization == null, "credentials sent to updater");
                if (scenario == "cancel") { await Task.Delay(Timeout.Infinite, token); }
                if (scenario.Contains("redirect") && requests == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri(scenario == "unsafe-redirect" ? "https://evil.example/payload.exe" : "https://release-assets.githubusercontent.com/test/signed?key=synthetic");
                    return redirect;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(scenario == "size" ? bytes.Take(3).ToArray() : bytes) };
            } };
            var updater = new GitHubToolUpdater(root, () => new HttpClient(handler));
            var release = SyntheticRelease(bytes);
            if (scenario == "checksum") release.Sha256 = new string('0', 64);
            using (var cancel = new CancellationTokenSource())
            {
                var task = updater.DownloadAsync(release, new Progress<UpdateProgress>(p => { Check(p.Received <= p.Total, "download exceeds expected total"); progress++; }), cancel.Token);
                if (scenario == "cancel") { cancel.Cancel(); RejectUpdate<OperationCanceledException>(() => AwaitUpdate(task), "download was not cancelled"); }
                else if (scenario == "success" || scenario == "redirect")
                {
                    string file = AwaitUpdate(task); Pump();
                    Check(File.ReadAllBytes(file).SequenceEqual(bytes) && progress > 0, "download or progress is invalid");
                    Check(requests == (scenario == "redirect" ? 2 : 1), "redirect handling changed");
                    File.Delete(file);
                }
                else if (scenario == "unsafe-redirect") RejectUpdate<IOException>(() => AwaitUpdate(task), "unsafe redirect accepted");
                else RejectUpdate<InvalidDataException>(() => AwaitUpdate(task), "invalid download accepted");
            }
        }
        Check(!Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Any(), "partial download retained");
        foreach (string scenario in new[] { "success", "rollback", "tamper", "changed-target", "read-only" })
        {
            string folder = Path.Combine(root, "install-" + scenario); Directory.CreateDirectory(folder);
            string source = typeof(MainWindow).Assembly.Location;
            string download = Path.Combine(folder, "download.exe"); File.Copy(source, download);
            string target = Path.Combine(folder, "用户工具.exe"); File.WriteAllText(target, "synthetic old executable");
            string oldHash = TestHash(File.ReadAllBytes(target));
            var release = new UpdateRelease { Version = "v" + FileVersionInfo.GetVersionInfo(source).FileVersion.Substring(0, FileVersionInfo.GetVersionInfo(source).FileVersion.Length - 2), Sha256 = TestHash(File.ReadAllBytes(source)) };
            if (scenario == "read-only")
            {
                File.SetAttributes(target, FileAttributes.ReadOnly);
                RejectUpdate<UnauthorizedAccessException>(() => UpdateInstaller.Prepare(download, release, folder, target), "read-only target accepted");
                File.SetAttributes(target, FileAttributes.Normal); continue;
            }
            PreparedUpdate prepared = UpdateInstaller.Prepare(download, release, folder + "\\", target);
            Check(TestHash(File.ReadAllBytes(target)) == oldHash, "prepare overwrote running program");
            var plan = new JavaScriptSerializer().Deserialize<UpdateInstaller.Plan>(File.ReadAllText(prepared.PlanPath));
            if (scenario == "tamper") File.WriteAllText(plan.Staged, "tampered staged executable");
            if (scenario == "changed-target") File.WriteAllText(target, "other instance updated this executable");
            string beforeApply = TestHash(File.ReadAllBytes(target));
            Func<ProcessStartInfo, Process> start = info =>
            {
                Check(info.FileName == target && info.Arguments.Contains("--data-dir") && info.Arguments.EndsWith("\\\\\""), "restart path or quoting changed");
                Check(TestHash(File.ReadAllBytes(target)) == release.Sha256, "new executable not installed before restart");
                if (scenario == "rollback") throw new Win32Exception("synthetic launch failure");
                return Process.GetCurrentProcess();
            };
            if (scenario == "success") { UpdateInstaller.Apply(prepared.PlanPath, start: start, waitForParent: false); Check(TestHash(File.ReadAllBytes(target)) == release.Sha256, "replacement failed"); }
            else
            {
                RejectUpdate<Exception>(() => UpdateInstaller.Apply(prepared.PlanPath, start: start, waitForParent: false), "invalid install succeeded");
                Check(TestHash(File.ReadAllBytes(target)) == beforeApply, "failed install did not preserve previous file");
            }
            Check(!File.Exists(plan.Staged) && !File.Exists(plan.Backup), "staged executable or backup left after test");
            UpdateInstaller.Discard(prepared);
        }
    }
    private sealed class FakeUpdater : IToolUpdater
    {
        public readonly TaskCompletionSource<UpdateRelease> CheckResult = new TaskCompletionSource<UpdateRelease>();
        public TaskCompletionSource<string> DownloadResult;
        public IProgress<UpdateProgress> Progress;
        public int Checks, Downloads, Prepared, Launched, Discarded;
        public bool FailPrepare, FailLaunch, Cancelled;
        public Task<UpdateRelease> CheckAsync(CancellationToken token) { Checks++; token.Register(() => CheckResult.TrySetCanceled()); return CheckResult.Task; }
        public Task<string> DownloadAsync(UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken token)
        {
            Downloads++; Progress = progress; DownloadResult = new TaskCompletionSource<string>();
            token.Register(() => { Cancelled = true; DownloadResult.TrySetCanceled(); }); return DownloadResult.Task;
        }
        public PreparedUpdate Prepare(string download, UpdateRelease release) { Prepared++; if (FailPrepare) throw new UnauthorizedAccessException(); return new PreparedUpdate { WorkerPath = "synthetic-updater" }; }
        public void Launch(PreparedUpdate update) { Launched++; if (FailLaunch) throw new IOException("synthetic launch failure"); }
        public void Discard(PreparedUpdate update) { Discarded++; }
    }
    private static void CaptureUpdateWindow(UpdateWindow window, string name, double scale = 1)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
        FrameworkElement button = (FrameworkElement)window.FindName("UpdateButton");
        Rect bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
        Check(bounds.Bottom < window.ActualHeight && bounds.Right < window.ActualWidth && bounds.Top >= 0, "update action outside dialog");
    }
    private static void ActualUpdateWorker()
    {
        string folder = Path.Combine(output, "update-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "Future.cs");
        File.WriteAllText(source, "using System; using System.IO; using System.Threading; using System.Reflection; [assembly: AssemblyVersion(\"9.1.0.0\")] [assembly: AssemblyFileVersion(\"9.1.0.0\")] static class Future { static void Main(string[] args) { if(args.Length > 0 && args[0] == \"--wait\") { Thread.Sleep(3000); return; } File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"started.txt\"), string.Join(\"|\", args)); } }", new UTF8Encoding(false));
        string future = Path.Combine(folder, "download.exe");
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        var compile = Process.Start(new ProcessStartInfo(compiler, "/nologo /target:winexe /out:\"" + future + "\" \"" + source + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        Check(compile.WaitForExit(15000) && compile.ExitCode == 0, "synthetic future executable compile failed"); compile.Dispose();
        string target = Path.Combine(folder, "领克工具 测试.exe"); File.Copy(typeof(MainWindow).Assembly.Location, target);
        string oldHash = TestHash(File.ReadAllBytes(target));
        var release = SyntheticRelease(File.ReadAllBytes(future));
        var prepared = UpdateInstaller.Prepare(future, release, folder, target);
        var plan = new JavaScriptSerializer().Deserialize<UpdateInstaller.Plan>(File.ReadAllText(prepared.PlanPath));
        using (var parent = Process.Start(new ProcessStartInfo(future, "--wait") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
        {
            plan.ParentId = parent.Id; plan.ParentStarted = parent.StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(prepared.PlanPath, new JavaScriptSerializer().Serialize(plan));
            var watch = Stopwatch.StartNew();
            UpdateInstaller.Launch(prepared);
            ObserveFor(TimeSpan.FromMilliseconds(200));
            Check(TestHash(File.ReadAllBytes(target)) == oldHash, "worker replaced executable before parent exit");
            Until(() => File.Exists(Path.Combine(folder, "started.txt")));
            Check(parent.HasExited && watch.ElapsedMilliseconds >= 2000, "worker did not wait for original process");
        }
        Check(TestHash(File.ReadAllBytes(target)) == release.Sha256, "real worker did not replace executable");
        Check(File.ReadAllText(Path.Combine(folder, "started.txt")) == "--data-dir|" + folder, "real worker did not preserve state directory");
        Until(() => !File.Exists(prepared.PlanPath));
        Console.WriteLine("真实更新进程已等待旧进程退出、替换 EXE 并启动合成新版。");
    }
    private static void UpdateWorkflowTests()
    {
        foreach (string scenario in new[] { "later", "cancel", "success", "prepare-error", "close-error", "launch-error", "deferred" })
        {
            var core = new FakeCore { FailFirstClose = scenario == "close-error" };
            var updater = new FakeUpdater { FailPrepare = scenario == "prepare-error", FailLaunch = scenario == "launch-error" };
            var window = new MainWindow(core, updater: updater);
            int step = 0; Exception error = null;
            var dialogWatch = Stopwatch.StartNew();
            var observe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            observe.Tick += (sender, args) =>
            {
                UpdateWindow dialog = Application.Current.Windows.OfType<UpdateWindow>().FirstOrDefault(value => value.IsVisible && value.Owner == window);
                if (dialog == null) return;
                try
                {
                    Button confirm = (Button)dialog.FindName("UpdateButton"), later = (Button)dialog.FindName("LaterButton");
                    Check(dialogWatch.Elapsed < TimeSpan.FromSeconds(12), "update dialog workflow timed out");
                    if (step == 0)
                    {
                        Check(updater.Downloads == 0 && updater.Prepared == 0 && updater.Launched == 0, "update started before confirmation");
                        Check(((TextBlock)dialog.FindName("Description")).Text.Contains("Token") && ((TextBlock)dialog.FindName("VersionLabel")).Text.Contains("v9.1.0"), "confirmation misses version or token notice");
                        Check(!IsWindowEnabled(new WindowInteropHelper(window).Handle) && FocusManager.GetFocusedElement(dialog) == later, "update dialog is not modal or lacks safe initial focus");
                        CaptureUpdateWindow(dialog, "wpf-update-confirm-100");
                        CaptureUpdateWindow(dialog, "wpf-update-confirm-150", 1.5);
                        if (scenario == "later" || scenario == "deferred") { observe.Stop(); later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); step = 9; return; }
                        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); step = 1;
                    }
                    else if (step == 1)
                    {
                        Check(updater.Downloads == 1 && !confirm.IsEnabled, "download duplicate click guard missing");
                        updater.Progress.Report(new UpdateProgress { Received = 8 * 1048576, Total = 20 * 1048576 }); step = 2;
                    }
                    else if (step == 2)
                    {
                        Check(((ProgressBar)dialog.FindName("DownloadProgress")).Value == 40, "download progress not displayed");
                        CaptureUpdateWindow(dialog, "wpf-update-download-100");
                        if (scenario == "cancel") { observe.Stop(); later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); step = 9; }
                        else { updater.DownloadResult.SetResult(Path.Combine(output, "synthetic-download.exe")); step = 3; }
                    }
                    else if (step == 3 && scenario == "prepare-error" && confirm.IsEnabled)
                    {
                        Check(((TextBlock)dialog.FindName("ProgressLabel")).Text.Contains("不可写"), "write permission error missing");
                        dialog.Close();
                        Check(dialog.IsVisible && (string)later.Content == "确认", "update failure dismissed without acknowledgement");
                        CaptureUpdateWindow(dialog, "wpf-update-error-100");
                        observe.Stop(); later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); step = 9;
                    }
                }
                catch (Exception problem) { error = problem; observe.Stop(); ((Button)dialog.FindName("LaterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            };
            window.Show(); core.Emit(); Until(() => Control<Button>(window, "LoginUpdateButton").IsEnabled == false && updater.Checks == 1);
            if (scenario == "deferred") { core.Snapshot.state.capturing = true; core.Emit(); Pump(); }
            observe.Start();
            updater.CheckResult.SetResult(SyntheticRelease());
            if (scenario == "deferred")
            {
                ObserveFor(TimeSpan.FromMilliseconds(120));
                Check(!Application.Current.Windows.OfType<UpdateWindow>().Any(value => value.Owner == window), "update interrupted active capture");
                Check(!Control<Button>(window, "LoginUpdateButton").IsEnabled, "manual update enabled while capturing");
                core.Snapshot.state.capturing = false; core.Emit();
            }
            try
            {
                Until(() => error != null || step == 9 || !window.IsVisible || Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible);
                if (error != null) throw error;
                if (scenario == "success") Check(!window.IsVisible && updater.Launched == 1 && core.Disposed, "successful download did not close core then launch updater");
                else if (scenario == "close-error" || scenario == "launch-error")
                {
                    Check(window.IsVisible && updater.Discarded == 1, "failed shutdown or launch did not discard update");
                    Check(updater.Launched == (scenario == "launch-error" ? 1 : 0), "updater ran before safe shutdown"); Acknowledge(window);
                }
                else Check(window.IsVisible && updater.Launched == 0, "declined or failed update closed tool");
                if (scenario == "cancel") Check(updater.Cancelled && updater.Prepared == 0, "cancelled download prepared an install");
            }
            finally { observe.Stop(); if (window.IsVisible) { window.Close(); Until(() => !window.IsVisible); } }
        }
        foreach (bool fail in new[] { false, true })
        {
            var core = new FakeCore(); var updater = new FakeUpdater(); var window = new MainWindow(core, updater: updater);
            window.Show(); core.Emit();
            if (fail) updater.CheckResult.SetException(new IOException("无法连接 GitHub")); else updater.CheckResult.SetResult(null);
            Until(() => Control<Button>(window, "LoginUpdateButton").IsEnabled);
            Check(Control<Grid>(window, "ImportantOverlay").Visibility != Visibility.Visible, "automatic check interrupted startup");
            Capture(window, "wpf-update-login-100");
            Click(window, "LoginUpdateButton");
            Until(() => Control<Grid>(window, "ImportantOverlay").Visibility == Visibility.Visible);
            Check(Text(window, "ImportantTitle") == (fail ? "无法检查更新" : "已是最新版本"), "manual check did not report result");
            Acknowledge(window);
            core.Snapshot.state.logged_in = true; core.Snapshot.state.trusted = true;
            core.Snapshot.state.username = "更新测试用户"; core.Snapshot.state.server_address = "https://sign.example.com"; core.Emit();
            Until(() => Control<FrameworkElement>(window, "MainView").IsVisible);
            Check(Control<Button>(window, "MainUpdateButton").IsEnabled, "main manual update unavailable");
            Capture(window, "wpf-update-main-100");
            window.Close(); Until(() => !window.IsVisible);
        }
    }
}
