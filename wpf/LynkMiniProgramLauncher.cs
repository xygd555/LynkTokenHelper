using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LynkSignTokenHelper
{
    public interface IMiniProgramLauncher
    {
        Task<bool> OpenAsync(CancellationToken cancellationToken);
    }

    public interface IMiniProgramDesktop
    {
        bool IsAvailable { get; }
        bool IsRunning { get; }
        IReadOnlyList<IntPtr> FindWindows();
        void Close(IntPtr handle);
        bool IsOpen(IntPtr handle);
        void Launch();
    }

    public sealed class LynkMiniProgramLauncher : IMiniProgramLauncher
    {
        public const string LaunchAddress = "weixin://launchapplet/?app_id=wx4e8c1172fe132106";
        private readonly IMiniProgramDesktop desktop;
        private readonly TimeSpan timeout;

        public LynkMiniProgramLauncher(IMiniProgramDesktop desktop = null, TimeSpan? timeout = null)
        {
            this.desktop = desktop ?? new WindowsMiniProgramDesktop();
            this.timeout = timeout ?? TimeSpan.FromSeconds(8);
        }

        public async Task<bool> OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!desktop.IsAvailable) throw new InvalidOperationException("未找到微信启动入口。\n\n请先安装电脑版微信，打开并完成微信登录后再重试。");
            if (!desktop.IsRunning) throw new InvalidOperationException("微信尚未打开。\n\n请先打开同一台电脑上的微信并完成登录，再点击“打开领克小程序”。");
            IReadOnlyList<IntPtr> windows = desktop.FindWindows();
            if (windows.Count > 1) throw new InvalidOperationException("检测到多个领克窗口，请先保留一个后重试。");
            bool reopened = windows.Count == 1;
            if (reopened)
            {
                IntPtr handle = windows[0];
                desktop.Close(handle);
                var closing = Stopwatch.StartNew();
                while (desktop.IsOpen(handle))
                {
                    if (closing.Elapsed >= timeout) throw new InvalidOperationException("领克小程序没有关闭，请手动关闭后重试。");
                    await Task.Delay(100, cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            desktop.Launch();
            var opening = Stopwatch.StartNew();
            while (desktop.FindWindows().Count == 0)
            {
                if (opening.Elapsed >= timeout) throw new InvalidOperationException("未能打开领克小程序。\n\n请确认电脑版微信已经打开并完成登录；如果微信停留在扫码或登录页面，请先登录微信后重试。");
                await Task.Delay(100, cancellationToken);
            }
            // WeChat activates the new window after it becomes visible.
            await Task.Delay(750, cancellationToken);
            return reopened;
        }
    }

    public sealed class WindowsMiniProgramDesktop : IMiniProgramDesktop
    {
        public bool IsAvailable
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(@"weixin\shell\open\command"))
                        return !string.IsNullOrWhiteSpace(key?.GetValue("") as string);
                }
                catch (System.Security.SecurityException) { return false; }
            }
        }

        public bool IsRunning
        {
            get
            {
                bool running = false;
                foreach (string name in new[] { "Weixin", "WeChat" })
                    foreach (Process process in Process.GetProcessesByName(name))
                        using (process) running = true;
                return running;
            }
        }

        public IReadOnlyList<IntPtr> FindWindows()
        {
            var windows = new List<IntPtr>();
            EnumWindows((handle, parameter) =>
            {
                if (!IsWindowVisible(handle)) return true;
                var title = new StringBuilder(128);
                GetWindowText(handle, title, title.Capacity);
                if (title.ToString() != "领克汽车" && title.ToString() != "领克") return true;
                GetWindowThreadProcessId(handle, out uint processId);
                try
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        string name = process.ProcessName;
                        if (name.Equals("WeChatAppEx", StringComparison.OrdinalIgnoreCase)) windows.Add(handle);
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        public void Close(IntPtr handle)
        {
            if (IsOpen(handle) && !PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("无法关闭领克小程序，请手动关闭后重试。");
        }

        public bool IsOpen(IntPtr handle) => IsWindow(handle) && IsWindowVisible(handle);

        public void Launch()
        {
            try { Process.Start(new ProcessStartInfo(LynkMiniProgramLauncher.LaunchAddress) { UseShellExecute = true }); }
            catch (Win32Exception) { throw new InvalidOperationException("无法打开微信，请确认已安装并登录电脑版微信。"); }
        }

        private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    }
}
