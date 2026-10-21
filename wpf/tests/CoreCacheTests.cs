using System;
using System.IO;
using System.Security.Cryptography;
using LynkSignTokenHelper;

internal static partial class WpfTests
{
    private static void CoreCacheWorkflow()
    {
        string state = Path.Combine(output, "core-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        string certificate = Path.Combine(state, "capture-ca.cer"), key = Path.Combine(state, "capture-key.dpapi");
        File.WriteAllText(certificate, "synthetic certificate marker");
        File.WriteAllText(key, "synthetic encrypted key marker");
        using (var client = new CoreClient(state))
        {
            string current = client.ExtractCore();
            string cache = Path.GetDirectoryName(current);
            Check(File.Exists(current) && HashCore(current) == BuildInfo.CoreSha256, "initial core extraction failed integrity verification");
            File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-3));
            DateTime written = File.GetLastWriteTimeUtc(current);
            string obsolete = Path.Combine(cache, "core-1111111111111111.exe");
            string locked = Path.Combine(cache, "core-2222222222222222.exe");
            string unrelated = Path.Combine(cache, "core-manual.exe");
            string nested = Path.Combine(cache, "nested", "core-3333333333333333.exe");
            File.WriteAllText(obsolete, "synthetic obsolete core");
            File.WriteAllText(locked, "synthetic locked core");
            File.WriteAllText(unrelated, "synthetic unrelated executable");
            Directory.CreateDirectory(Path.GetDirectoryName(nested));
            File.WriteAllText(nested, "synthetic nested executable");
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(client.ExtractCore() == current, "valid core cache was not reused");
                Check(File.GetLastWriteTimeUtc(current) == written, "valid core cache was rewritten");
                Check(!File.Exists(obsolete) && File.Exists(locked), "cache cleanup deleted a locked core or retained an obsolete one");
                Check(File.Exists(unrelated) && File.Exists(nested), "cache cleanup touched unrelated or nested files");
            }
            Check(client.ExtractCore() == current && !File.Exists(locked), "cleanup did not retry an unlocked obsolete core");
            Check(File.GetLastWriteTimeUtc(current) == written, "cleanup modified the current core");
            File.WriteAllText(current, "synthetic corrupt cache");
            Check(client.ExtractCore() == current && HashCore(current) == BuildInfo.CoreSha256, "corrupt cache was reused instead of repaired");
            Check(Directory.GetFiles(cache, "*.exe", SearchOption.TopDirectoryOnly).Length == 2, "obsolete core caches accumulated");
        }
        Check(File.ReadAllText(certificate) == "synthetic certificate marker" && File.ReadAllText(key) == "synthetic encrypted key marker", "core cleanup changed certificate state");
        Console.WriteLine("内核缓存复用、损坏修复、旧文件清理、占用后重试及证书目录保留验证通过。");
    }

    private static string HashCore(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
