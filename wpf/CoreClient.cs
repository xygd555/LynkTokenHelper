using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LynkSignTokenHelper
{
    public sealed class CoreClient : ICoreClient
    {
        private readonly string directory;
        private readonly bool restoreOnly;
        private readonly object writeLock = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private Process process;
        private StreamWriter pipeWriter;
        private long sequence;
        private bool ended, disposed;
        public event Action<CoreSnapshot> Updated;
        public event Action<string> Faulted;
        public event Action Closed;

        public CoreClient(string directory, bool restoreOnly = false)
        {
            this.directory = directory;
            this.restoreOnly = restoreOnly;
        }
        public void Start()
        {
            Task.Run(() =>
            {
                try
                {
                    string executable = ExtractCore();
                    if (Volatile.Read(ref disposed)) return;
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo(executable, "--stdio --data-dir " + Quote(directory) + (restoreOnly ? " --restore-proxy" : ""))
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(executable)
                        }
                    };
                    lock (writeLock)
                    {
                        if (disposed) return;
                        process.Start();
                        pipeWriter = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    // Never write pipe contents or native error output to disk.
                    StreamReader errors = process.StandardError;
                    Task.Run(() => { try { errors.ReadToEnd(); } catch (IOException) { } catch (ObjectDisposedException) { } });
                    string line;
                    var readerSerializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (Volatile.Read(ref disposed)) continue;
                        CoreSnapshot snapshot = readerSerializer.Deserialize<CoreSnapshot>(line);
                        if (snapshot.type == "closed") { ended = true; Closed?.Invoke(); }
                        else if (snapshot.type == "fatal") { ended = true; Faulted?.Invoke(snapshot.error); }
                        else if (snapshot.type == "snapshot" && snapshot.protocol == 1) Updated?.Invoke(snapshot);
                        else throw new InvalidDataException();
                    }
                    process.WaitForExit();
                    if (!ended && !disposed) Faulted?.Invoke("抓包内核已退出，请重新打开工具。启动时会检查代理恢复。");
                }
                catch (Exception)
                {
                    if (!ended && !disposed) Faulted?.Invoke("无法启动抓包内核，请检查状态目录权限或安全软件拦截。");
                }
                finally
                {
                    lock (writeLock)
                    {
                        try { pipeWriter?.Close(); } catch (IOException) { }
                        pipeWriter = null;
                    }
                    process?.Dispose();
                }
            });
        }
        private string ExtractCore()
        {
            byte[] data;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LynkSignTokenHelper.Core.exe.gz"))
            using (var compressed = new GZipStream(resource, CompressionMode.Decompress))
            using (var bytes = new MemoryStream()) { compressed.CopyTo(bytes); data = bytes.ToArray(); }
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            string cache = Path.Combine(directory, "runtime");
            Directory.CreateDirectory(cache);
            string executable = Path.Combine(cache, "core-" + hash.Substring(0, 16) + ".exe");
            if (File.Exists(executable))
            {
                using (var sha = SHA256.Create())
                using (var file = File.OpenRead(executable))
                    if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant() == hash) return executable;
            }
            string temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, data);
                if (File.Exists(executable)) File.Delete(executable);
                File.Move(temporary, executable);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return executable;
        }
        private static string Quote(string value)
        {
            // Windows command-line quoting, including trailing backslashes.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                slashes = 0;
                result.Append(character);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        private void Send(object request)
        {
            lock (writeLock)
            {
                if (disposed || process == null || pipeWriter == null || process.HasExited) throw new IOException();
                pipeWriter.WriteLine(serializer.Serialize(request));
            }
        }
        public long Command(string action, object input = null)
        {
            long id = Interlocked.Increment(ref sequence);
            Send(new { type = "command", id, action, input });
            return id;
        }
        public void Select(int index) { Send(new { type = "select", selected = index }); }
        public void Close() { Send(new { type = "close" }); }
        public void Dispose()
        {
            // Closing stdin also restores the proxy if the desktop exits unexpectedly.
            lock (writeLock)
            {
                disposed = true;
                if (pipeWriter == null) return;
                try { pipeWriter.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
            }
        }
    }
}
