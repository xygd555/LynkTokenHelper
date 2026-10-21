using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

[assembly: InternalsVisibleTo("WpfTests")]

namespace LynkSignTokenHelper
{
    public interface IMiniProgramRuntime
    {
        Task<bool> TryLaunchAsync(CancellationToken cancellationToken);
    }

    public sealed class WeChatMiniProgramRuntime : IMiniProgramRuntime
    {
        private const string AppId = "wx4e8c1172fe132106";
        private static readonly string EndpointPath = Path.Combine(Path.GetTempPath(), "xweb_use_endpoint.json");

        public Task<bool> TryLaunchAsync(CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var gate = new Mutex(false, @"Local\LynkTokenHelper.MiniProgramLaunch"))
            {
                bool acquired;
                try { acquired = gate.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("另一个工具窗口正在打开小程序，请稍候重试。");
                try { return LaunchAsync(cancellationToken).GetAwaiter().GetResult(); }
                finally { gate.ReleaseMutex(); }
            }
        }, cancellationToken);

        private static async Task<bool> LaunchAsync(CancellationToken cancellationToken)
        {
            WeChatRuntimeTarget[] targets = WeChatRuntimeBridge.FindTargets();
            // Only unavailable or occupied runtimes use the protocol path; addresses are resolved from the loaded image.
            if (targets.Length != 1 || targets[0].ServerState != 0 || File.Exists(EndpointPath)) return false;
            WeChatRuntimeTarget target = targets[0];
            WeChatRuntimeBridge.CheckQueue(target);
            bool attempted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted = true;
                WeChatRuntimeBridge.SetServer(target, true);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        Uri endpoint = await ReadEndpointAsync(target.ProcessId, timeout.Token).ConfigureAwait(false);
                        using (var socket = new ClientWebSocket())
                        {
                            // The capture proxy must not receive the runtime connection token.
                            socket.Options.Proxy = null;
                            try
                            {
                                await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                                var serializer = new JavaScriptSerializer { MaxJsonLength = 65536 };
                                byte[] request = Encoding.UTF8.GetBytes(serializer.Serialize(new
                                {
                                    id = 1, method = "XWeb.LaunchApplet",
                                    @params = new { app_id = AppId, headless = 0, prefer_hot_launch = false },
                                    timeout_ms = 15000
                                }));
                                await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                                await ReadReplyAsync(socket, serializer, timeout.Token).ConfigureAwait(false);
                            }
                            finally { socket.Abort(); }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException("微信打开小程序超时，请确认微信已登录且未锁定后重试。");
                    }
                }
                return true;
            }
            finally
            {
                // Cleanup deliberately ignores caller cancellation.
                if (attempted) await RestoreAsync(target).ConfigureAwait(false);
            }
        }

        private static async Task<Uri> ReadEndpointAsync(int processId, CancellationToken cancellationToken)
        {
            var waiting = Stopwatch.StartNew();
            while (waiting.Elapsed < TimeSpan.FromSeconds(5))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(EndpointPath))
                    {
                        var file = new FileInfo(EndpointPath);
                        if (file.Length > 8192) throw new InvalidOperationException("微信启动连接校验失败，已停止打开小程序。");
                        return ParseEndpoint(File.ReadAllText(EndpointPath), processId);
                    }
                }
                catch (IOException) { }
                catch (ArgumentException) { }
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("微信启动服务未就绪，请稍候重试。");
        }

        internal static Uri ParseEndpoint(string json, int processId)
        {
            var value = new JavaScriptSerializer { MaxJsonLength = 8192 }.Deserialize<Dictionary<string, object>>(json);
            object pid, address;
            Uri endpoint;
            if (value == null || !value.TryGetValue("pid", out pid) || !(pid is int) || (int)pid != processId ||
                !value.TryGetValue("endpoint", out address) || !(address is string) ||
                !Uri.TryCreate((string)address, UriKind.Absolute, out endpoint) || endpoint.Scheme != "ws" ||
                endpoint.Host != "127.0.0.1" || endpoint.Port < 1 || endpoint.AbsolutePath != "/xweb-use/v1" ||
                endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
                !endpoint.Query.StartsWith("?token=", StringComparison.Ordinal) || endpoint.Query.Length <= 7)
                throw new InvalidOperationException("微信启动连接校验失败，已停止打开小程序。");
            return endpoint;
        }

        private static async Task ReadReplyAsync(ClientWebSocket socket, JavaScriptSerializer serializer, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            while (true)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                        if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 65536)
                            throw new InvalidOperationException("微信启动响应无效，请重试。");
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    var reply = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                    object id, result, appId;
                    if (reply == null || !reply.TryGetValue("id", out id) || !(id is int) || (int)id != 1) continue;
                    var values = reply.TryGetValue("result", out result) ? result as Dictionary<string, object> : null;
                    if (reply.ContainsKey("error") || values == null || !values.TryGetValue("app_id", out appId) || !AppId.Equals(appId))
                        throw new InvalidOperationException("微信未能打开领克小程序，请确认微信已登录且未锁定后重试。");
                    return;
                }
            }
        }

        private static async Task RestoreAsync(WeChatRuntimeTarget target)
        {
            try
            {
                WeChatRuntimeTarget current = WeChatRuntimeBridge.FindTargets().SingleOrDefault(t => t.ProcessId == target.ProcessId && t.ModuleBase == target.ModuleBase);
                if (current == null)
                {
                    using (Process process = Process.GetProcessById(target.ProcessId))
                        if (!process.HasExited) throw new InvalidOperationException("微信运行环境已变化，请重新打开微信以恢复启动服务。");
                    return;
                }
                WeChatRuntimeBridge.SetServer(current, false);
                var waiting = Stopwatch.StartNew();
                while (waiting.Elapsed < TimeSpan.FromSeconds(5))
                {
                    current = WeChatRuntimeBridge.FindTargets().SingleOrDefault(t => t.ProcessId == target.ProcessId && t.ModuleBase == target.ModuleBase);
                    if (current != null && current.ServerState == 0 && !File.Exists(EndpointPath)) return;
                    await Task.Delay(80).ConfigureAwait(false);
                }
                throw new InvalidOperationException("微信临时启动服务未能关闭，请重新打开微信后重试。");
            }
            catch (ArgumentException)
            {
                // Only remove our dead process's stale handshake; never touch another owner.
                if (File.Exists(EndpointPath))
                {
                    try { ParseEndpoint(File.ReadAllText(EndpointPath), target.ProcessId); }
                    catch (InvalidOperationException) { return; }
                    File.Delete(EndpointPath);
                }
            }
        }
    }
}
