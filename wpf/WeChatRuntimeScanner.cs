using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LynkSignTokenHelper
{
    internal sealed class RuntimePattern
    {
        internal readonly byte[] Bytes;
        internal readonly bool[] Fixed;
        private readonly int anchor, length;
        private readonly int[] shift = new int[256];

        internal RuntimePattern(string text)
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Bytes = new byte[parts.Length];
            Fixed = new bool[parts.Length];
            int run = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != "??") { Bytes[i] = Convert.ToByte(parts[i], 16); Fixed[i] = true; run++; }
                else run = 0;
                if (run > length) { length = run; anchor = i - run + 1; }
            }
            if (length == 0) throw new ArgumentException("A pattern must contain fixed bytes.");
            for (int i = 0; i < shift.Length; i++) shift[i] = length;
            for (int i = 0; i < length - 1; i++) shift[Bytes[anchor + i]] = length - i - 1;
        }

        internal bool Matches(byte[] data, int offset)
        {
            if (offset < 0 || offset > data.Length - Bytes.Length) return false;
            for (int i = 0; i < Bytes.Length; i++) if (Fixed[i] && data[offset + i] != Bytes[i]) return false;
            return true;
        }

        internal IEnumerable<int> Find(byte[] data, int offset, int count)
        {
            int end = checked(offset + count - Bytes.Length);
            for (int at = checked(offset + anchor); at <= end + anchor;)
            {
                int i = length - 1;
                while (i >= 0 && data[at + i] == Bytes[anchor + i]) i--;
                if (i < 0 && Matches(data, at - anchor)) yield return at - anchor;
                at += shift[data[at + length - 1]];
            }
        }
    }

    internal sealed class RuntimeCodeSignature
    {
        internal int Rva;
        internal RuntimePattern Pattern;
    }

    internal sealed class WeChatRuntimeLayout
    {
        internal int ImageSize, ManagerRva, ManagerVtableRva, InitializedRva, StateOffset;
        internal int ExecutorRva, QueueOffset, RunnerOffset, RunnerRefcountOffset;
        internal int SetterRva, UiRunnerRva, ReleaseRva, GuardRva;
        internal RuntimeCodeSignature[] Signatures;
    }

    internal static class WeChatRuntimeScanner
    {
        // Relative displacements and branch targets are deliberately wildcarded.
        internal const string SetterPattern = "41 57 41 56 41 55 41 54 56 57 55 53 48 81 EC 98 01 00 00 4C 89 CE 44 89 C3 48 89 D7 49 89 CE 80 7A 17 00 0F 88 ?? ?? ?? ?? 49 81 C6 88 00 00 00 48 8D 44 24 58 48 89 38 48 8D 4C 24 3F 48 89 4C 24 30 48 89 44 24 28 48 8D 05 ?? ?? ?? ?? 48 89 44 24 20 4C 8D 7C 24 68 4C 89 F9";
        // The UI executor member distinguishes this ABI from its otherwise identical IO getter.
        internal const string UiPattern = "56 57 53 48 83 EC 20 48 89 D7 48 89 CE 48 8B 1D ?? ?? ?? ?? 8B 02 FF C8 83 F8 04 76 ?? 0F B6 47 04 48 8D 0D ?? ?? ?? ?? 48 8B 0C C1 48 8B 43 08 48 8B 44 C8 ?? 48 89 06 48 85 C0 74 ?? B9 01 00 00 00 F0 0F C1 48 ?? 81 F9 FF FF FF 7F 74 ?? 48 89 F0 48 83 C4 20 5B 5F 5E C3";
        internal const string ReleasePattern = "56 57 53 48 83 EC 60 48 89 CE 48 8B 05 ?? ?? ?? ?? 48 31 E0 48 89 44 24 58 48 8B 01 48 8D 0D ?? ?? ?? ?? 48 39 48 10 0F 85 ?? ?? ?? ?? 48 85 F6 0F 84 ?? ?? ?? ?? 48 8B 40 08 48 8D 0D ?? ?? ?? ?? 48 39 C8 74 ?? 48 8D 0D ?? ?? ?? ?? 48 39 C8 0F 85 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ??";
        internal const string ManagerPattern = "56 57 48 83 EC 28 8B 05 ?? ?? ?? ?? 8B 0D ?? ?? ?? ?? 65 48 8B 14 25 58 00 00 00 48 8B 0C CA 3B 81 04 00 00 00 7F ?? 48 8D 05 ?? ?? ?? ?? 48 83 C4 28 5F 5E C3 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 83 3D ?? ?? ?? ?? FF 75 ?? 48 8D 05 ?? ?? ?? ?? 48 89 05 ?? ?? ?? ?? B9 08 00 00 00";
        private static readonly Dictionary<string, CachedLayout> cache = new Dictionary<string, CachedLayout>(StringComparer.OrdinalIgnoreCase);
        private sealed class CachedLayout { internal long Length, Stamp; internal WeChatRuntimeLayout Layout; }

        internal static WeChatRuntimeLayout Resolve(string path)
        {
            var file = new FileInfo(path);
            lock (cache)
            {
                CachedLayout item;
                if (cache.TryGetValue(path, out item) && item.Length == file.Length && item.Stamp == file.LastWriteTimeUtc.Ticks) return item.Layout;
                WeChatRuntimeLayout layout = Resolve(File.ReadAllBytes(path));
                cache[path] = new CachedLayout { Length = file.Length, Stamp = file.LastWriteTimeUtc.Ticks, Layout = layout };
                return layout;
            }
        }

        internal static WeChatRuntimeLayout Resolve(byte[] bytes)
        {
            var image = new RuntimeImage(bytes);
            int setter = image.UniqueFunction(SetterPattern, "服务开关");
            int ui = image.UniqueFunction(UiPattern, "UI 任务队列");
            int release = image.UniqueFunction(ReleasePattern, "任务引用释放");
            if (image.LogFunction("XwebUseManager::SetDebugSetting: key=") != setter) throw Unsupported("服务开关的代码引用不一致");
            int managerGetter = image.FindManagerGetter(setter);
            int manager = image.Relative(managerGetter + 39, 3, 7);
            int constructor = image.UniqueInFunction(managerGetter, "48 8D 05 ?? ?? ?? ?? 48 89 05 ?? ?? ?? ?? 31 FF 40 88 3D ?? ?? ?? ?? 0F 57 C0", "管理器构造");
            if (image.Relative(constructor + 7, 3, 7) != manager) throw Unsupported("管理器引用不一致");
            int vtable = image.Relative(constructor, 3, 7);
            int initialized = image.Relative(constructor + 16, 3, 7);
            int stop = image.LogFunction("XwebUseManager::StopWsServer:");
            int stateTest = image.UniqueInFunction(stop, "83 B9 ?? ?? ?? ?? 03 74 ??", "服务状态");
            int stateOffset = image.Int32At(stateTest + 2);
            string stateBytes = string.Join(" ", BitConverter.GetBytes(stateOffset).Select(b => b.ToString("X2")));
            int reset = image.UniqueInFunction(stop, "C7 86 " + stateBytes + " 00 00 00 00", "服务关闭状态");
            if (image.Int32At(reset + 2) != stateOffset || stateOffset < 0 || stateOffset > 4096 || initialized < manager || initialized > manager + 4096) throw Unsupported("管理器字段布局无法确认");
            int queue = image.UniqueInFunction(ui, "48 8B 43 ?? 48 8B 44 C8 ?? 48 89 06", "UI 队列字段");
            int refcount = image.UniqueInFunction(ui, "F0 0F C1 48 ?? 81 F9 FF FF FF 7F", "任务引用计数");
            int traits = image.Relative(ui + 33, 3, 7);
            long index = image.Int64At(traits);
            if (index < 0 || index > 15) throw Unsupported("UI 队列索引无法确认");
            int queueOffset = image.ByteAt(queue + 3), runnerOffset = checked(image.ByteAt(queue + 8) + (int)index * 8);
            int refcountOffset = image.ByteAt(refcount + 4);
            if (queueOffset > 127 || runnerOffset > 255 || refcountOffset == 0 || refcountOffset > 127) throw Unsupported("任务队列布局无法确认");
            var layout = new WeChatRuntimeLayout {
                ImageSize = image.ImageSize, ManagerRva = manager, ManagerVtableRva = vtable, InitializedRva = initialized, StateOffset = stateOffset,
                ExecutorRva = image.Relative(ui + 13, 3, 7), QueueOffset = queueOffset, RunnerOffset = runnerOffset, RunnerRefcountOffset = refcountOffset,
                SetterRva = setter, UiRunnerRva = ui, ReleaseRva = release, GuardRva = image.GuardDispatch(),
                Signatures = new[] {
                    new RuntimeCodeSignature { Rva = setter, Pattern = image.ExactPattern(setter, new RuntimePattern(SetterPattern).Bytes.Length) },
                    new RuntimeCodeSignature { Rva = ui, Pattern = image.ExactPattern(ui, new RuntimePattern(UiPattern).Bytes.Length) },
                    new RuntimeCodeSignature { Rva = release, Pattern = image.ExactPattern(release, new RuntimePattern(ReleasePattern).Bytes.Length) },
                    new RuntimeCodeSignature { Rva = managerGetter, Pattern = image.ExactPattern(managerGetter, new RuntimePattern(ManagerPattern).Bytes.Length) },
                    new RuntimeCodeSignature { Rva = constructor, Pattern = image.ExactPattern(constructor, 26) },
                    new RuntimeCodeSignature { Rva = traits, Pattern = image.ExactPattern(traits, 8) },
                    new RuntimeCodeSignature { Rva = stateTest, Pattern = image.ExactPattern(stateTest, 8) },
                    new RuntimeCodeSignature { Rva = reset, Pattern = image.ExactPattern(reset, 10) }
                }
            };
            image.RequireData(manager, checked(stateOffset + 4), true);
            image.RequireData(initialized, 1, true);
            image.RequireData(layout.ExecutorRva, 8, true);
            image.RequireData(vtable, 8, false);
            image.RequireData(layout.GuardRva, 8, false);
            return layout;
        }

        internal static InvalidOperationException Unsupported(string reason) => new InvalidOperationException("无法可靠识别当前微信的小程序启动接口：" + reason + "。\n当前版本暂无法自动打开小程序。");
    }

    internal sealed class RuntimeImage
    {
        private readonly byte[] data;
        private readonly List<Section> sections = new List<Section>();
        private readonly SortedList<int, int> functions = new SortedList<int, int>();
        private readonly long imageBase;
        private readonly int optional;
        internal readonly int ImageSize;
        private sealed class Section { internal int Rva, Size, VirtualSize, Raw, Flags; }

        internal RuntimeImage(byte[] bytes)
        {
            data = bytes;
            if (data.Length < 64 || data[0] != 'M' || data[1] != 'Z') throw WeChatRuntimeScanner.Unsupported("运行时不是有效的 PE 文件");
            int pe = Int32(60);
            if (pe < 64 || pe > data.Length - 264 || Int32(pe) != 0x4550 || UInt16(pe + 4) != 0x8664 || UInt16(pe + 24) != 0x20b) throw WeChatRuntimeScanner.Unsupported("运行时不是受支持的 64 位 PE 文件");
            optional = pe + 24;
            int count = UInt16(pe + 6), optionalSize = UInt16(pe + 20);
            ImageSize = Int32(optional + 56); imageBase = Int64(optional + 24);
            if (count == 0 || count > 96 || optionalSize < 240 || ImageSize <= 0 || imageBase <= 0) throw WeChatRuntimeScanner.Unsupported("PE 节表无效");
            for (int i = 0; i < count; i++)
            {
                int at = checked(optional + optionalSize + i * 40);
                var section = new Section { VirtualSize = Int32(at + 8), Rva = Int32(at + 12), Size = Int32(at + 16), Raw = Int32(at + 20), Flags = Int32(at + 36) };
                if (section.Rva < 0 || section.Size < 0 || section.VirtualSize < 0 || section.Raw < 0 || (long)section.Raw + section.Size > data.Length || (long)section.Rva + Math.Max(section.Size, section.VirtualSize) > ImageSize) throw WeChatRuntimeScanner.Unsupported("PE 节范围无效");
                sections.Add(section);
            }
            int directory = Int32(optional + 112 + 3 * 8), length = Int32(optional + 116 + 3 * 8);
            if (directory <= 0 || length <= 0 || length % 12 != 0) throw WeChatRuntimeScanner.Unsupported("缺少函数边界表");
            int table = Offset(directory, length);
            for (int i = 0; i < length; i += 12)
            {
                int begin = Int32(table + i), end = Int32(table + i + 4);
                // Only usable contiguous ranges can certify a scanned function.
                if (begin == 0 || end <= begin) continue;
                if (begin < 0 || end > ImageSize || functions.ContainsKey(begin)) throw WeChatRuntimeScanner.Unsupported("函数边界表无效");
                functions.Add(begin, end);
            }
        }

        private int Int32(int at) { CheckFile(at, 4); return BitConverter.ToInt32(data, at); }
        private int UInt16(int at) { CheckFile(at, 2); return BitConverter.ToUInt16(data, at); }
        private long Int64(int at) { CheckFile(at, 8); return BitConverter.ToInt64(data, at); }
        private void CheckFile(int at, int size) { if (at < 0 || size < 0 || at > data.Length - size) throw WeChatRuntimeScanner.Unsupported("文件数据越界"); }
        private Section SectionAt(int rva, int size, bool file = true) => sections.SingleOrDefault(s => rva >= s.Rva && size >= 0 && (long)rva + size <= (long)s.Rva + (file ? s.Size : Math.Max(s.Size, s.VirtualSize)));
        private int Offset(int rva, int size = 1) { Section section = SectionAt(rva, size); if (section == null) throw WeChatRuntimeScanner.Unsupported("代码引用越界"); return checked(section.Raw + rva - section.Rva); }
        internal int Int32At(int rva) => Int32(Offset(rva, 4));
        internal long Int64At(int rva) => Int64(Offset(rva, 8));
        internal byte ByteAt(int rva) => data[Offset(rva)];
        internal int Relative(int rva, int displacement, int length) => checked(rva + length + Int32At(rva + displacement));
        internal void RequireData(int rva, int size, bool writable)
        {
            Section section = SectionAt(rva, size, false);
            if (section == null || (section.Flags & 0x20000000) != 0 || (writable && (section.Flags & unchecked((int)0x80000000)) == 0)) throw WeChatRuntimeScanner.Unsupported("运行时数据引用无效");
        }

        private int FunctionAt(int rva)
        {
            int low = 0, high = functions.Count - 1;
            while (low <= high) { int mid = low + (high - low) / 2; if (functions.Keys[mid] <= rva) low = mid + 1; else high = mid - 1; }
            return high >= 0 && rva < functions.Values[high] ? functions.Keys[high] : -1;
        }

        private IEnumerable<int> FindCode(RuntimePattern pattern)
        {
            foreach (Section section in sections.Where(s => (s.Flags & 0x20000000) != 0))
                foreach (int raw in pattern.Find(data, section.Raw, section.Size)) yield return section.Rva + raw - section.Raw;
        }

        internal int UniqueFunction(string text, string name)
        {
            var pattern = new RuntimePattern(text);
            int[] hits = FindCode(pattern).Where(rva => FunctionAt(rva) == rva && functions[rva] - rva >= pattern.Bytes.Length).Take(2).ToArray();
            if (hits.Length != 1) throw WeChatRuntimeScanner.Unsupported(name + (hits.Length == 0 ? "未匹配" : "匹配不唯一"));
            return hits[0];
        }

        internal int UniqueInFunction(int begin, string text, string name)
        {
            int end;
            if (!functions.TryGetValue(begin, out end) || end - begin > 65536) throw WeChatRuntimeScanner.Unsupported(name + "函数边界无效");
            var pattern = new RuntimePattern(text);
            int raw = Offset(begin, end - begin);
            int[] hits = pattern.Find(data, raw, end - begin).Take(2).ToArray();
            if (hits.Length != 1) throw WeChatRuntimeScanner.Unsupported(name + "无法唯一匹配");
            return checked(begin + hits[0] - raw);
        }

        internal int LogFunction(string text)
        {
            byte[] needle = Encoding.ASCII.GetBytes(text);
            var references = new HashSet<int>();
            foreach (int at in FindCode(new RuntimePattern("48 8D 15 ?? ?? ?? ??")))
            {
                long address = (long)at + 7 + Int32At(at + 3);
                if (address < 0 || address > int.MaxValue) continue;
                int target = (int)address;
                Section section = SectionAt(target, needle.Length);
                if (section == null || (section.Flags & 0x20000000) != 0) continue;
                int raw = Offset(target, needle.Length);
                if (!needle.Where((b, i) => data[raw + i] != b).Any())
                {
                    int function = FunctionAt(at);
                    if (function >= 0) references.Add(function);
                }
            }
            if (references.Count != 1) throw WeChatRuntimeScanner.Unsupported("服务日志引用无法唯一匹配");
            return references.Single();
        }

        internal int FindManagerGetter(int setter)
        {
            var getters = new HashSet<int>(FindCode(new RuntimePattern(WeChatRuntimeScanner.ManagerPattern)).Where(rva => FunctionAt(rva) == rva));
            var candidates = new HashSet<int>();
            var lastGetter = new Dictionary<int, Tuple<int, int>>();
            foreach (Section section in sections.Where(s => (s.Flags & 0x20000000) != 0))
                for (int raw = section.Raw; raw <= section.Raw + section.Size - 5; raw++)
                {
                    if (data[raw] != 0xe8) continue;
                    int at = section.Rva + raw - section.Raw;
                    long address = (long)at + 5 + Int32(raw + 1);
                    if (address < 0 || address > int.MaxValue) continue;
                    int target = (int)address;
                    if (target != setter && !getters.Contains(target)) continue;
                    int caller = FunctionAt(at);
                    if (caller < 0) continue;
                    if (getters.Contains(target)) lastGetter[caller] = Tuple.Create(at, target);
                    else
                    {
                        Tuple<int, int> preceding;
                        if (!lastGetter.TryGetValue(caller, out preceding) || at - preceding.Item1 > 1024 ||
                            !new RuntimePattern("48 89 C6").Matches(data, Offset(preceding.Item1 + 5, 3)) ||
                            !new RuntimePattern("48 89 F1 48 89 FA 41 89 D8").Matches(data, Offset(at - 9, 9)))
                            throw WeChatRuntimeScanner.Unsupported("管理器调用链无法确认");
                        candidates.Add(preceding.Item2);
                    }
                }
            if (candidates.Count != 1) throw WeChatRuntimeScanner.Unsupported("管理器调用链匹配不唯一");
            return candidates.Single();
        }

        internal int GuardDispatch()
        {
            int directory = Int32(optional + 112 + 10 * 8), length = Int32(optional + 116 + 10 * 8);
            if (directory <= 0 || length < 128 || Int32At(directory) < 128) throw WeChatRuntimeScanner.Unsupported("缺少安全调度元数据");
            long address = Int64At(directory + 120) - imageBase;
            if (address <= 0 || address > int.MaxValue) throw WeChatRuntimeScanner.Unsupported("安全调度地址无效");
            return (int)address;
        }

        internal RuntimePattern ExactPattern(int rva, int size)
        {
            int raw = Offset(rva, size);
            return new RuntimePattern(string.Join(" ", Enumerable.Range(0, size).Select(i => data[raw + i].ToString("X2"))));
        }
    }
}
