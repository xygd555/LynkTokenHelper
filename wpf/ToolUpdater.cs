using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LynkSignTokenHelper
{
    public sealed class UpdateRelease
    {
        public string Version { get; set; }
        public string Notes { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public sealed class UpdateProgress
    {
        public long Received { get; set; }
        public long Total { get; set; }
    }
    public sealed class PreparedUpdate
    {
        public string PlanPath { get; set; }
        public string WorkerPath { get; set; }
    }
    public enum UpdateDownloadSource { Default, Backup1, Backup2, GitHub }
    public interface IToolUpdater
    {
        Task<UpdateRelease> CheckAsync(CancellationToken cancellationToken);
        Task<string> DownloadAsync(UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken, UpdateDownloadSource source = UpdateDownloadSource.Default);
        PreparedUpdate Prepare(string download, UpdateRelease release);
        void Launch(PreparedUpdate update);
        void Discard(PreparedUpdate update);
    }
    public sealed class GitHubToolUpdater : IToolUpdater
    {
        public const string Repository = "xygd555/LynkTokenHelper";
        public const string AssetName = "LynkSignTokenHelper-windows-x64.exe";
        private const long MaximumSize = 150 * 1024 * 1024;
        private readonly string directory;
        private readonly Func<HttpClient> createClient;
        public GitHubToolUpdater(string directory, Func<HttpClient> createClient = null)
        {
            this.directory = Path.GetFullPath(directory);
            this.createClient = createClient ?? (() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }));
        }
        private HttpClient CreateClient()
        {
            HttpClient client = createClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LynkTokenHelper/" + BuildInfo.Version.TrimStart('v'));
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }
        public async Task<UpdateRelease> CheckAsync(CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (HttpClient client = CreateClient())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    using (HttpResponseMessage response = await client.GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (timeout.Token.Register(() => response.Dispose()))
                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new MemoryStream())
                        {
                            byte[] buffer = new byte[8192];
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                            {
                                if (output.Length + count > 512 * 1024) throw new InvalidDataException("更新信息过大，请前往 GitHub 手动下载。");
                                output.Write(buffer, 0, count);
                            }
                            return ParseRelease(Encoding.UTF8.GetString(output.ToArray()), BuildInfo.Version);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("检查更新超时，请稍后重试。"); }
                catch (HttpRequestException) { throw new IOException("无法连接 GitHub，请检查网络后重试。"); }
            }
        }
        public static UpdateRelease ParseRelease(string json, string currentVersion)
        {
            ReleaseInfo info;
            try { info = new JavaScriptSerializer { MaxJsonLength = 512 * 1024 }.Deserialize<ReleaseInfo>(json); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException) { throw new InvalidDataException("GitHub 返回的更新信息无效。"); }
            if (info == null || info.draft || info.prerelease) return null;
            Version version = ParseVersion(info.tag_name);
            if (version <= ParseVersion(currentVersion)) return null;
            AssetInfo[] matches = info.assets?.Where(value => value != null && value.name == AssetName).ToArray();
            AssetInfo asset = matches?.Length == 1 ? matches[0] : null;
            if (asset == null || asset.state != "uploaded" || asset.size <= 0 || asset.size > MaximumSize ||
                !Regex.IsMatch(asset.digest ?? "", "^sha256:[a-fA-F0-9]{64}$") || !IsAssetUrl(asset.browser_download_url, info.tag_name))
                throw new InvalidDataException("新版缺少可校验的 Windows 程序，请前往 GitHub 手动下载。");
            return new UpdateRelease { Version = info.tag_name, Notes = string.IsNullOrWhiteSpace(info.body) ? "此版本未提供更新说明。" : info.body.Substring(0, Math.Min(info.body.Length, 12000)), DownloadUrl = asset.browser_download_url, Sha256 = asset.digest.Substring(7).ToLowerInvariant(), Size = asset.size };
        }
        public static Version ParseVersion(string value)
        {
            if (!Regex.IsMatch(value ?? "", "^v[0-9]+\\.[0-9]+\\.[0-9]+$")) throw new InvalidDataException("更新版本号无效。");
            Version version;
            if (!Version.TryParse(value.Substring(1), out version)) throw new InvalidDataException("更新版本号无效。");
            return version;
        }
        private static bool IsAssetUrl(string value, string version)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && IsHttps(uri) && uri.Host == "github.com" &&
                uri.AbsolutePath == "/" + Repository + "/releases/download/" + version + "/" + AssetName && string.IsNullOrEmpty(uri.Query);
        }
        private static bool IsHttps(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
        private static bool IsDownloadRedirect(Uri uri) => IsHttps(uri) &&
            (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
        private static string DownloadPrefix(UpdateDownloadSource source)
        {
            // Keep these routes aligned with the panel's helper download dialog.
            switch (source)
            {
                case UpdateDownloadSource.Default: return "https://v4.gh-proxy.org/";
                case UpdateDownloadSource.Backup1: return "https://ghproxy.net/";
                case UpdateDownloadSource.Backup2: return "https://ghproxy.homeboyc.cn/";
                case UpdateDownloadSource.GitHub: return "";
                default: throw new InvalidDataException("下载线路无效。");
            }
        }
        private static bool IsDownloadRedirect(Uri uri, UpdateDownloadSource source)
        {
            if (IsDownloadRedirect(uri)) return true;
            if (!IsHttps(uri) || source == UpdateDownloadSource.GitHub) return false;
            return uri.Host == new Uri(DownloadPrefix(source)).Host ||
                (source == UpdateDownloadSource.Default && uri.Host == "gh-proxy.com");
        }
        public async Task<string> DownloadAsync(UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken, UpdateDownloadSource source = UpdateDownloadSource.Default)
        {
            ParseVersion(release.Version);
            if (!IsAssetUrl(release.DownloadUrl, release.Version) || !Regex.IsMatch(release.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || release.Size <= 0 || release.Size > MaximumSize)
                throw new InvalidDataException("更新文件信息无效。");
            string prefix = DownloadPrefix(source);
            string folder = Path.Combine(directory, "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "download.exe");
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (HttpClient client = CreateClient())
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    Uri address = new Uri(prefix + release.DownloadUrl);
                    for (int redirect = 0; redirect <= 5; redirect++)
                    {
                        using (HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status <= 399)
                            {
                                Uri location = response.Headers.Location;
                                if (location == null || redirect == 5) throw new IOException("更新下载跳转失败。");
                                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                                if (!IsDownloadRedirect(address, source)) throw new IOException("更新下载地址不受信任。");
                                continue;
                            }
                            response.EnsureSuccessStatusCode();
                            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != release.Size)
                                throw new InvalidDataException("更新文件大小与发布信息不一致。");
                            using (timeout.Token.Register(() => response.Dispose()))
                            using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                            using (SHA256 hash = SHA256.Create())
                            {
                                byte[] buffer = new byte[65536];
                                long received = 0;
                                int count;
                                var report = Stopwatch.StartNew();
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                                {
                                    received += count;
                                    if (received > release.Size) throw new InvalidDataException("更新文件大小与发布信息不一致。");
                                    await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                                    if (report.ElapsedMilliseconds >= 100) { progress?.Report(new UpdateProgress { Received = received, Total = release.Size }); report.Restart(); }
                                }
                                hash.TransformFinalBlock(new byte[0], 0, 0);
                                if (received != release.Size || Hex(hash.Hash) != release.Sha256.ToLowerInvariant()) throw new InvalidDataException("更新文件校验失败，已保留当前版本，请重新下载。");
                                progress?.Report(new UpdateProgress { Received = received, Total = release.Size });
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            return file;
                        }
                    }
                    throw new IOException("更新下载失败。");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { DeleteFile(file); throw new IOException("下载更新超时，请切换线路后重试。"); }
            catch (HttpRequestException) { DeleteFile(file); throw new IOException("当前线路下载失败，请切换线路后重试。"); }
            catch { DeleteFile(file); throw; }
        }
        public PreparedUpdate Prepare(string download, UpdateRelease release) => UpdateInstaller.Prepare(download, release, directory);
        public void Launch(PreparedUpdate update) => UpdateInstaller.Launch(update);
        public void Discard(PreparedUpdate update) => UpdateInstaller.Discard(update);
        internal static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        internal static string HashFile(string file) { using (SHA256 hash = SHA256.Create()) using (Stream input = File.OpenRead(file)) return Hex(hash.ComputeHash(input)); }
        internal static void DeleteFile(string file) { try { if (!string.IsNullOrEmpty(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private sealed class ReleaseInfo
        {
            public string tag_name { get; set; }
            public string body { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public AssetInfo[] assets { get; set; }
        }
        private sealed class AssetInfo
        {
            public string name { get; set; }
            public string state { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }
    }
}
