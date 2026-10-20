using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using LynkSignTokenHelper;

internal static partial class WpfTests
{
    private sealed class FakeMiniProgramRuntime : IMiniProgramRuntime
    {
        public FakeMiniProgramDesktop Desktop;
        public bool Supported = true, Fail, Cancel;
        public int Calls;
        public Task<bool> TryLaunchAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new InvalidOperationException("模拟微信启动失败");
            if (Supported) { Desktop.Calls.Add("runtime"); Desktop.Count = 1; }
            return Task.FromResult(Supported);
        }
    }

    private static void RuntimeWorkflow()
    {
        var desktop = new FakeMiniProgramDesktop();
        var runtime = new FakeMiniProgramRuntime { Desktop = desktop };
        var launcher = new LynkMiniProgramLauncher(desktop, runtime: runtime);
        Check(!OpenMiniProgram(launcher), "direct launch reported a reopen");
        Check(desktop.Calls.SequenceEqual(new[] { "runtime" }), "direct launch called the protocol");
        desktop.Calls.Clear();
        Check(OpenMiniProgram(launcher), "direct reopen lost the existing-window state");
        Check(desktop.Calls.SequenceEqual(new[] { "close:1", "runtime" }), "direct reopen did not close the previous window first");
        desktop.Count = 0; desktop.Calls.Clear(); runtime.Supported = false;
        Check(!OpenMiniProgram(launcher), "protocol fallback reported a reopen");
        Check(desktop.Calls.SequenceEqual(new[] { "launch" }), "unsupported runtime did not use the protocol once");
        desktop.Count = 0; desktop.Calls.Clear(); runtime.Supported = true; runtime.Fail = true;
        int calls = runtime.Calls;
        try { OpenMiniProgram(launcher); throw new Exception("runtime failure was ignored"); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("模拟"), "runtime failure was lost"); }
        Check(runtime.Calls == calls + 1 && desktop.Calls.Count == 0, "failed runtime triggered a second launch");
        runtime.Fail = false; runtime.Cancel = true;
        try { OpenMiniProgram(launcher); throw new Exception("runtime cancellation was ignored"); }
        catch (OperationCanceledException) { }
        Check(desktop.Calls.Count == 0, "cancelled runtime fell back to the protocol");

        var serializer = new JavaScriptSerializer();
        string valid = serializer.Serialize(new { pid = 42, endpoint = "ws://127.0.0.1:12345/xweb-use/v1?token=synthetic" });
        Check(WeChatMiniProgramRuntime.ParseEndpoint(valid, 42).Port == 12345, "valid local endpoint was rejected");
        foreach (string address in new[] {
            "ws://example.com:12345/xweb-use/v1?token=synthetic",
            "ws://127.0.0.1.example.com:12345/xweb-use/v1?token=synthetic",
            "wss://127.0.0.1:12345/xweb-use/v1?token=synthetic",
            "ws://user@127.0.0.1:12345/xweb-use/v1?token=synthetic",
            "ws://127.0.0.1:0/xweb-use/v1?token=synthetic",
            "ws://127.0.0.1:12345/other?token=synthetic",
            "ws://127.0.0.1:12345/xweb-use/v1",
            "ws://127.0.0.1:12345/xweb-use/v1?token=",
            "ws://127.0.0.1:12345/xweb-use/v1?token=synthetic#fragment"
        }) RejectRuntimeEndpoint(serializer.Serialize(new { pid = 42, endpoint = address }), 42);
        RejectRuntimeEndpoint(valid, 43);
        RejectRuntimeEndpoint("{}", 42);
        Console.WriteLine("直接启动、协议回退、失败、取消与本地连接校验通过。");
    }

    private static void RejectRuntimeEndpoint(string json, int processId)
    {
        try { WeChatMiniProgramRuntime.ParseEndpoint(json, processId); }
        catch (InvalidOperationException) { return; }
        throw new Exception("unsafe runtime endpoint was accepted");
    }

    private static void RuntimeCloseWait()
    {
        var core = new FakeCore();
        var launcher = new FakeMiniProgramLauncher { DeferCancellation = true };
        var window = new MainWindow(core, miniProgramLauncher: launcher);
        window.Show();
        Until(() => core.Started);
        core.Snapshot.state.local_only = core.Snapshot.state.trusted = true;
        core.Emit();
        Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
        Click(window, "MiniProgramButton");
        window.Close(); Pump();
        Check(window.IsVisible && !launcher.Pending.Task.IsCompleted, "helper exited before runtime restoration completed");
        launcher.Pending.SetCanceled();
        Until(() => !window.IsVisible);
        core = new FakeCore();
        launcher = new FakeMiniProgramLauncher { DeferCancellation = true };
        window = new MainWindow(core, miniProgramLauncher: launcher);
        window.Show(); Until(() => core.Started);
        core.Snapshot.state.local_only = core.Snapshot.state.trusted = true;
        core.Emit(); Until(() => Control<Button>(window, "MiniProgramButton").IsEnabled);
        Click(window, "MiniProgramButton"); window.Close();
        launcher.Pending.SetException(new InvalidOperationException("模拟恢复失败，请重新打开微信。"));
        CheckImportantNotice(window, "恢复失败");
        Check(window.IsVisible && Text(window, "ImportantBody").Contains("恢复失败"), "restoration failure was hidden during exit");
        Acknowledge(window); Until(() => !window.IsVisible);
        Console.WriteLine("退出工具会等待启动恢复完成，恢复失败会明确提示。");
    }

    private static void ActualRuntimeLaunch()
    {
        string endpointPath = Path.Combine(Path.GetTempPath(), "xweb_use_endpoint.json");
        string[] desktops = { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        foreach (string folder in desktops.Where(Directory.Exists))
            Check(!Directory.EnumerateFiles(folder, "*.lnk").Any(path => Path.GetFileName(path).Contains("领克") || Path.GetFileName(path).StartsWith("LynkTokenHelper", StringComparison.OrdinalIgnoreCase)), "desktop contains a Lynk test shortcut");
        Check(!File.Exists(endpointPath), "runtime service was already active");
        Check(WeChatRuntimeBridge.FindTargets().Length == 1, "supported runtime is unavailable");
        var desktop = new WindowsMiniProgramDesktop();
        foreach (IntPtr handle in desktop.FindWindows()) desktop.Close(handle);
        Until(() => desktop.FindWindows().Count == 0);
        var launcher = new LynkMiniProgramLauncher(desktop);
        for (int run = 1; run <= 2; run++)
        {
            Task<bool> pending = launcher.OpenAsync(CancellationToken.None);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!pending.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(50)) { Pump(); Thread.Sleep(10); }
            Check(pending.IsCompleted, "real runtime launch timed out");
            Check(pending.GetAwaiter().GetResult() == (run == 2), "real runtime reopen result was wrong");
            Check(desktop.FindWindows().Count == 1, "real runtime launch did not show one Lynk window");
            Check(!File.Exists(endpointPath) && WeChatRuntimeBridge.FindTargets().Single().ServerState == 0, "real launch left the local service active");
            Console.WriteLine("第 " + run + " 次无快捷方式启动通过，领克窗口为 1，临时服务与握手文件已清理。");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            Task<bool> pending = new WeChatMiniProgramRuntime().TryLaunchAsync(cancellation.Token);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(endpointPath) && !pending.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(1); }
            bool observed = File.Exists(endpointPath) && !pending.IsCompleted;
            cancellation.Cancel();
            while (!pending.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { Pump(); Thread.Sleep(10); }
            Check(pending.IsCompleted, "cancelled real runtime did not finish restoration");
            bool cancelled = false;
            try { pending.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(observed && cancelled, "real cancellation was not exercised while the service was active");
            Check(!File.Exists(endpointPath) && WeChatRuntimeBridge.FindTargets().Single().ServerState == 0, "real cancellation left the local service active");
            Console.WriteLine("真实本地服务开启期间取消启动通过，临时服务与握手文件已清理。");
        }
    }
}
