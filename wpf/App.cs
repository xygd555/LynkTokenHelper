using System;
using System.IO;
using System.Windows;
using System.Threading.Tasks;

namespace LynkSignTokenHelper
{
    internal static class App
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--apply-update")
            {
                RunUpdate(args[1]);
                return;
            }
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LynkTokenHelper");
            bool restoreOnly = false;
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--data-dir" && index + 1 < args.Length) directory = Path.GetFullPath(args[++index]);
                else if (args[index] == "--restore-proxy") restoreOnly = true;
            }
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            application.DispatcherUnhandledException += (sender, eventArgs) =>
            {
                MessageBox.Show("界面操作失败，请关闭后重新打开工具。", "领克凭据助手", MessageBoxButton.OK, MessageBoxImage.Error);
                eventArgs.Handled = true;
            };
            Task.Run(() => UpdateInstaller.CleanCache(directory));
            application.Run(new MainWindow(new CoreClient(directory, restoreOnly), restoreOnly, updater: new GitHubToolUpdater(directory)));
        }
        private static void RunUpdate(string plan)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new UpdateWindow("正在更新工具", "代理已恢复，正在准备替换程序，请稍候…") { ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, InstallationRunning = true };
            window.Loaded += async (sender, args) =>
            {
                try
                {
                    await Task.Run(() => UpdateInstaller.Apply(plan, message => window.Dispatcher.BeginInvoke(new Action(() => window.ReportStatus(message)))));
                    window.InstallationRunning = false;
                    window.Close();
                }
                catch (Exception error)
                {
                    window.InstallationRunning = false;
                    window.ReportFailure((error is IOException || error is InvalidDataException) ? error.Message : "更新失败，请检查程序目录权限及其他工具窗口是否已关闭。");
                }
            };
            application.Run(window);
        }
    }
}
