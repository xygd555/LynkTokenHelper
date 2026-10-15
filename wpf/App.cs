using System;
using System.IO;
using System.Windows;

namespace LynkSignTokenHelper
{
    internal static class App
    {
        [STAThread]
        public static void Main(string[] args)
        {
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
            application.Run(new MainWindow(new CoreClient(directory, restoreOnly), restoreOnly));
        }
    }
}
