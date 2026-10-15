using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LynkSignTokenHelper
{
    public static class UpdateInstaller
    {
        public sealed class Plan
        {
            public string Target { get; set; }
            public string Staged { get; set; }
            public string Backup { get; set; }
            public string Download { get; set; }
            public string Directory { get; set; }
            public string Version { get; set; }
            public string Sha256 { get; set; }
            public string OriginalSha256 { get; set; }
            public int ParentId { get; set; }
            public long ParentStarted { get; set; }
        }
        public static PreparedUpdate Prepare(string download, UpdateRelease release, string directory, string target = null)
        {
            target = Path.GetFullPath(target ?? Assembly.GetExecutingAssembly().Location);
            string folder = Path.GetDirectoryName(Path.GetFullPath(download));
            string id = Guid.NewGuid().ToString("N");
            var plan = new Plan
            {
                Target = target, Staged = Path.Combine(Path.GetDirectoryName(target), ".lynk-update-" + id + ".exe"),
                Backup = Path.Combine(Path.GetDirectoryName(target), ".lynk-previous-" + id + ".exe"),
                Download = Path.GetFullPath(download), Directory = Path.GetFullPath(directory), Version = release.Version,
                Sha256 = release.Sha256, OriginalSha256 = GitHubToolUpdater.HashFile(target),
                ParentId = Process.GetCurrentProcess().Id, ParentStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks
            };
            var prepared = new PreparedUpdate { PlanPath = Path.Combine(folder, "update.json"), WorkerPath = Path.Combine(folder, "updater.exe") };
            try
            {
                ValidateDownload(plan.Download, plan);
                if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException();
                File.Copy(download, plan.Staged, false);
                File.Copy(Assembly.GetExecutingAssembly().Location, prepared.WorkerPath, false);
                File.WriteAllText(prepared.PlanPath, new JavaScriptSerializer().Serialize(plan), new UTF8Encoding(false));
                return prepared;
            }
            catch
            {
                GitHubToolUpdater.DeleteFile(plan.Staged);
                GitHubToolUpdater.DeleteFile(prepared.WorkerPath);
                GitHubToolUpdater.DeleteFile(prepared.PlanPath);
                throw;
            }
        }
        private static void ValidateDownload(string file, Plan plan)
        {
            GitHubToolUpdater.ParseVersion(plan.Version);
            if (GitHubToolUpdater.HashFile(file) != plan.Sha256 || FileVersionInfo.GetVersionInfo(file).FileVersion != plan.Version.Substring(1) + ".0")
                throw new InvalidDataException("更新文件校验或版本验证失败，已保留当前版本。");
        }
        public static void Launch(PreparedUpdate update)
        {
            Process process = Process.Start(new ProcessStartInfo(update.WorkerPath, "--apply-update " + CoreClient.Quote(update.PlanPath))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(update.WorkerPath) });
            if (process == null) throw new IOException("无法启动更新程序。");
            process.Dispose();
        }
        public static void Discard(PreparedUpdate update)
        {
            if (update == null) return;
            try
            {
                if (File.Exists(update.PlanPath)) GitHubToolUpdater.DeleteFile(ReadPlan(update.PlanPath).Staged);
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (ArgumentException) { }
            GitHubToolUpdater.DeleteFile(update.PlanPath);
            GitHubToolUpdater.DeleteFile(update.WorkerPath);
        }
        private static Plan ReadPlan(string path)
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("更新计划无效。");
            Plan plan = new JavaScriptSerializer().Deserialize<Plan>(File.ReadAllText(path));
            if (plan == null || string.IsNullOrWhiteSpace(plan.Target) || string.IsNullOrWhiteSpace(plan.Staged) || string.IsNullOrWhiteSpace(plan.Backup) ||
                Path.GetDirectoryName(plan.Target) != Path.GetDirectoryName(plan.Staged) || Path.GetDirectoryName(plan.Target) != Path.GetDirectoryName(plan.Backup) ||
                !Path.GetFileName(plan.Staged).StartsWith(".lynk-update-", StringComparison.Ordinal) || !Path.GetFileName(plan.Backup).StartsWith(".lynk-previous-", StringComparison.Ordinal))
                throw new InvalidDataException("更新计划无效。");
            return plan;
        }
        public static void Apply(string planPath, Action<string> report = null, Func<ProcessStartInfo, Process> start = null, bool waitForParent = true)
        {
            Plan plan = ReadPlan(planPath);
            bool replaced = false;
            try
            {
                if (waitForParent)
                {
                    if (Path.GetFullPath(planPath) != Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.json") ||
                        string.Equals(plan.Target, Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请通过工具中的更新按钮更新。");
                    report?.Invoke("正在等待工具退出…");
                    try
                    {
                        using (Process parent = Process.GetProcessById(plan.ParentId))
                        {
                            if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStarted && !parent.WaitForExit(30000)) throw new IOException("工具尚未退出，更新已取消。");
                        }
                    }
                    catch (ArgumentException) { }
                }
                report?.Invoke("正在校验并替换程序…");
                ValidateDownload(plan.Staged, plan);
                if (GitHubToolUpdater.HashFile(plan.Target) != plan.OriginalSha256) throw new IOException("程序文件已发生变化，更新已取消，请重新打开工具检查版本。");
                var retry = Stopwatch.StartNew();
                while (true)
                {
                    try { File.Replace(plan.Staged, plan.Target, plan.Backup); replaced = true; break; }
                    catch (IOException) when (retry.Elapsed < TimeSpan.FromSeconds(8)) { Thread.Sleep(200); }
                }
                ValidateDownload(plan.Target, plan);
                report?.Invoke("正在启动新版工具…");
                var info = new ProcessStartInfo(plan.Target, "--data-dir " + CoreClient.Quote(plan.Directory))
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target) };
                Process process = (start ?? Process.Start)(info);
                if (process == null) throw new IOException("无法启动新版工具。");
                process.Dispose();
                GitHubToolUpdater.DeleteFile(plan.Backup);
                GitHubToolUpdater.DeleteFile(plan.Download);
                GitHubToolUpdater.DeleteFile(planPath);
            }
            catch
            {
                if (replaced && File.Exists(plan.Backup))
                {
                    try { File.Replace(plan.Backup, plan.Target, null); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { throw new IOException("更新未完成，旧版已保存在：" + plan.Backup + "\n请关闭其他工具窗口后，将旧版移回原位置。"); }
                }
                throw;
            }
            finally { GitHubToolUpdater.DeleteFile(plan.Staged); }
        }
        public static void CleanCache(string directory)
        {
            string root = Path.Combine(directory, "updates");
            if (!System.IO.Directory.Exists(root)) return;
            try
            {
                foreach (string folder in System.IO.Directory.GetDirectories(root))
                {
                    Guid id;
                    if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out id) || System.IO.Directory.GetLastWriteTimeUtc(folder) > DateTime.UtcNow.AddDays(-1)) continue;
                    foreach (string name in new[] { "download.exe", "updater.exe", "update.json" }) GitHubToolUpdater.DeleteFile(Path.Combine(folder, name));
                    try { System.IO.Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
