using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LynkSignTokenHelper;

internal static partial class WpfTests
{
    private static void UpdateMirrorLiveTest()
    {
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        using (var client = new HttpClient())
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LynkTokenHelper-Test");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = client.GetStringAsync("https://api.github.com/repos/" + GitHubToolUpdater.Repository + "/releases/latest").GetAwaiter().GetResult();
            UpdateRelease release = GitHubToolUpdater.ParseRelease(json, "v0.0.0");
            Check(release != null, "no official release is available for the opt-in network test");
            string root = Path.Combine(output, "update-mirror-live-" + Guid.NewGuid().ToString("N"));
            var updater = new GitHubToolUpdater(root);
            string file = null;
            try
            {
                file = updater.DownloadAsync(release, null, cancellation.Token).GetAwaiter().GetResult();
                Check(File.Exists(file) && new FileInfo(file).Length == release.Size, "real mirror download was incomplete");
                Console.WriteLine("默认加速线路实际下载 " + release.Version + " 成功，文件大小与 GitHub SHA256 校验通过；未安装更新。");
            }
            finally
            {
                cancellation.Cancel();
                if (file != null) File.Delete(file);
            }
        }
    }

    private static void UpdateMirrorServiceTests()
    {
        string root = Path.Combine(output, "update-mirrors-" + Guid.NewGuid().ToString("N"));
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("synthetic verified update");
        UpdateRelease release = SyntheticRelease(bytes);
        string[] prefixes = { "https://v4.gh-proxy.org/", "https://ghproxy.net/", "https://ghproxy.homeboyc.cn/", "" };
        foreach (UpdateDownloadSource source in Enum.GetValues(typeof(UpdateDownloadSource)))
        {
            int requests = 0;
            var handler = new UpdateHttpHandler { Respond = (request, token) =>
            {
                requests++;
                Check(request.RequestUri.AbsoluteUri == prefixes[(int)source] + release.DownloadUrl, "update used a different panel download route");
                Check(request.Headers.Authorization == null && !request.Headers.Contains("Cookie"), "credentials reached a mirror");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            } };
            var updater = new GitHubToolUpdater(root, () => new HttpClient(handler));
            string file = AwaitUpdate(updater.DownloadAsync(release, null, CancellationToken.None, source));
            Check(File.ReadAllBytes(file).SequenceEqual(bytes) && requests == 1, "mirror download was not verified or fetched more than once");
            File.Delete(file);
        }
        foreach (string scenario in new[] { "canonical", "relative", "cross-mirror", "direct-mirror", "http", "user-info", "port", "fragment", "loop", "http-error" })
        {
            UpdateDownloadSource source = scenario == "relative" ? UpdateDownloadSource.Backup1 : scenario == "direct-mirror" ? UpdateDownloadSource.GitHub : UpdateDownloadSource.Default;
            int requests = 0;
            var handler = new UpdateHttpHandler { Respond = (request, token) =>
            {
                requests++;
                if (scenario == "http-error") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (requests == 1 || scenario == "loop")
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    string address = scenario == "canonical" ? "https://gh-proxy.com/" + release.DownloadUrl :
                        scenario == "relative" ? "/" + release.DownloadUrl :
                        scenario == "cross-mirror" ? "https://ghproxy.net/" + release.DownloadUrl :
                        scenario == "direct-mirror" ? "https://v4.gh-proxy.org/" + release.DownloadUrl :
                        scenario == "http" ? "http://v4.gh-proxy.org/" + release.DownloadUrl :
                        scenario == "user-info" ? "https://user@v4.gh-proxy.org/" + release.DownloadUrl :
                        scenario == "port" ? "https://v4.gh-proxy.org:8443/" + release.DownloadUrl :
                        scenario == "fragment" ? "https://v4.gh-proxy.org/" + release.DownloadUrl + "#fragment" :
                        "https://v4.gh-proxy.org/" + release.DownloadUrl;
                    response.Headers.Location = new Uri(address, UriKind.RelativeOrAbsolute);
                    return Task.FromResult(response);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            } };
            var updater = new GitHubToolUpdater(root, () => new HttpClient(handler));
            if (scenario == "canonical" || scenario == "relative")
            {
                string file = AwaitUpdate(updater.DownloadAsync(release, null, CancellationToken.None, source));
                Check(requests == 2 && File.ReadAllBytes(file).SequenceEqual(bytes), "mirror redirect was not followed correctly");
                File.Delete(file);
            }
            else
            {
                RejectUpdate<IOException>(() => AwaitUpdate(updater.DownloadAsync(release, null, CancellationToken.None, source)), "invalid mirror redirect or response was accepted");
                Check(requests == (scenario == "loop" ? 6 : 1), "invalid mirror redirect performed another request");
            }
        }
        var invalidMetadata = SyntheticRelease(bytes); invalidMetadata.DownloadUrl = prefixes[0] + release.DownloadUrl;
        RejectUpdate<InvalidDataException>(() => GitHubToolUpdater.ParseRelease(ReleaseJson(invalidMetadata), "v1.0.0"), "mirror metadata replaced the official asset URL");
        var invalidSource = new GitHubToolUpdater(root, () => throw new Exception("invalid route sent a request"));
        RejectUpdate<InvalidDataException>(() => AwaitUpdate(invalidSource.DownloadAsync(release, null, CancellationToken.None, (UpdateDownloadSource)99)), "unknown mirror was accepted");
        Check(!Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Any(), "mirror failure left a partial executable");
        Console.WriteLine("面板同款四条下载线路、代理跳转限制、网络失败与文件校验通过。");
    }
}
