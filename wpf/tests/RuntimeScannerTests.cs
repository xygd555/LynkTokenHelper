using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LynkSignTokenHelper;

internal static partial class WpfTests
{
    private static void RuntimeScannerTests()
    {
        var wildcard = new RuntimePattern("AA ?? AA");
        Check(wildcard.Find(new byte[] { 0xaa, 0, 0xaa, 1, 0xaa }, 0, 5).SequenceEqual(new[] { 0, 2 }), "overlapping wildcard matches were lost");
        Check(!wildcard.Find(new byte[] { 0xaa, 0, 0xaa }, 0, 2).Any(), "signature crossed the requested section boundary");
        byte[] callback = WeChatRuntimeBridge.BuildCode(12);
        Check(new RuntimePattern("F0 FF 49 0C").Find(callback, 0, callback.Length).Count() == 1, "callback ignored the discovered reference-count field");
        foreach (int shift in new[] { 0, 0x3000 })
        {
            var fixture = new RuntimeScanFixture(shift);
            WeChatRuntimeLayout layout = WeChatRuntimeScanner.Resolve(fixture.Data);
            Check(layout.SetterRva == fixture.Setter && layout.UiRunnerRva == fixture.Ui && layout.ReleaseRva == fixture.Release, "function RVAs were not derived from the shuffled image");
            Check(layout.ManagerRva == fixture.Manager && layout.ExecutorRva == fixture.Executor && layout.GuardRva == fixture.Guard, "global references were not resolved");
            Check(layout.InitializedRva == fixture.Manager + 0x24 && layout.StateOffset == 0x98 && layout.RunnerOffset == 0x48 && layout.RunnerRefcountOffset == 12, "object fields or task index were hardcoded");
            Check(layout.Signatures.All(s => s.Pattern.Matches(fixture.Data, fixture.Raw(s.Rva))), "live-image verification did not retain the scanned bytes");
            fixture = new RuntimeScanFixture(shift);
            fixture.Put(fixture.Release, new byte[16]);
            RejectRuntimeImage(fixture.Data, "missing function signature was accepted");
            fixture = new RuntimeScanFixture(shift);
            fixture.Pattern(fixture.Setter + 0x200, WeChatRuntimeScanner.SetterPattern);
            fixture.AddFunction(fixture.Setter + 0x200, 0x100);
            fixture.Seal();
            RejectRuntimeImage(fixture.Data, "ambiguous function signature was accepted");
            fixture = new RuntimeScanFixture(shift);
            fixture.Relative(fixture.Caller + 0xe7, 1, 5, fixture.Release);
            RejectRuntimeImage(fixture.Data, "missing manager-to-setter call chain was accepted");
            fixture = new RuntimeScanFixture(shift);
            fixture.Put(fixture.Caller + 0x25, new byte[3]);
            RejectRuntimeImage(fixture.Data, "unconfirmed manager argument flow was accepted");
            fixture = new RuntimeScanFixture(shift);
            fixture.Int32(fixture.Raw(fixture.Stop + 0x32), 0x9c);
            RejectRuntimeImage(fixture.Data, "conflicting server-state fields were accepted");
        }
        RejectRuntimeImage(new byte[24], "truncated PE was accepted");
        var malformed = new RuntimeScanFixture(0);
        malformed.Int32(0x80 + 24 + 0xf0 + 20, int.MaxValue);
        RejectRuntimeImage(malformed.Data, "invalid section range was accepted");
        Console.WriteLine("特征扫描的地址移动、相似 IO 队列、重复候选、引用链、状态字段及 PE 边界验证通过。");
    }

    private static void RejectRuntimeImage(byte[] bytes, string message)
    {
        try { WeChatRuntimeScanner.Resolve(bytes); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private static void ActualRuntimeScan(string[] paths)
    {
        RuntimeScannerTests();
        foreach (string path in paths)
        {
            var watch = Stopwatch.StartNew();
            WeChatRuntimeLayout layout = WeChatRuntimeScanner.Resolve(path);
            Console.WriteLine("运行时 " + FileVersionInfo.GetVersionInfo(path).FileVersion + " 扫描通过，耗时 " + watch.ElapsedMilliseconds + "ms；未执行启动调用。");
            Console.WriteLine("setter={0:x} ui={1:x} manager={2:x} executor={3:x}", layout.SetterRva, layout.UiRunnerRva, layout.ManagerRva, layout.ExecutorRva);
        }
    }

    private sealed class RuntimeScanFixture
    {
        internal readonly byte[] Data = new byte[0x6400];
        internal readonly int Setter, Ui, Release, Manager, Executor, Guard, Caller, Stop;
        private readonly int shift, getter, otherGetter, rdata;
        private readonly List<Tuple<int, int>> functions = new List<Tuple<int, int>>();
        internal RuntimeScanFixture(int addressShift)
        {
            shift = addressShift; rdata = 0x5000 + shift;
            Setter = 0x1200 + shift; getter = 0x1800 + shift; otherGetter = 0x1c00 + shift;
            Ui = 0x2000 + shift; Release = 0x2400 + shift; Stop = 0x2900 + shift; Caller = 0x2d00 + shift;
            Manager = 0x8100 + shift; Executor = 0x8300 + shift; Guard = rdata + 0x900;
            Data[0] = (byte)'M'; Data[1] = (byte)'Z'; Int32(60, 0x80); Int32(0x80, 0x4550);
            Word(0x84, 0x8664); Word(0x86, 4); Word(0x94, 0xf0); Word(0x98, 0x20b);
            Int64(0x98 + 24, 0x180000000); Int32(0x98 + 56, 0xc000 + shift);
            Section(0, 0x1000 + shift, 0x3000, 0x400, 0x60000020);
            Section(1, rdata, 0x1000, 0x3400, 0x40000040);
            Section(2, 0x7000 + shift, 0x1000, 0x4400, unchecked((int)0xc0000040), 0x2000);
            Section(3, 0xa000 + shift, 0x1000, 0x5400, 0x40000040);
            Pattern(Setter, WeChatRuntimeScanner.SetterPattern); AddFunction(Setter, 0x200);
            Pattern(Ui, WeChatRuntimeScanner.UiPattern); AddFunction(Ui, 0x100);
            Pattern(Release, WeChatRuntimeScanner.ReleasePattern); AddFunction(Release, 0x100);
            Pattern(getter, WeChatRuntimeScanner.ManagerPattern); AddFunction(getter, 0x200);
            Pattern(otherGetter, WeChatRuntimeScanner.ManagerPattern); AddFunction(otherGetter, 0x100);
            Pattern(0x2200 + shift, WeChatRuntimeScanner.UiPattern); AddFunction(0x2200 + shift, 0x100);
            Data[Raw(0x2200 + shift + 47)] = 0x18;
            AddFunction(Stop, 0x200); AddFunction(Caller, 0x200);
            Relative(getter + 39, 3, 7, Manager);
            Pattern(getter + 0xa0, "48 8D 05 ?? ?? ?? ?? 48 89 05 ?? ?? ?? ?? 31 FF 40 88 3D ?? ?? ?? ?? 0F 57 C0");
            Relative(getter + 0xa0, 3, 7, rdata + 0x600); Relative(getter + 0xa7, 3, 7, Manager); Relative(getter + 0xb0, 3, 7, Manager + 0x24);
            Relative(Ui + 13, 3, 7, Executor); Relative(Ui + 33, 3, 7, rdata + 0x700);
            Int64(Raw(rdata + 0x700), 2);
            int uiFields = new RuntimePattern("48 8B 43 08 48 8B 44 C8 ?? 48 89 06").Find(Data, Raw(Ui), 0x100).Single();
            Data[uiFields + 8] = 0x38;
            int refcount = new RuntimePattern("F0 0F C1 48 ?? 81 F9 FF FF FF 7F").Find(Data, Raw(Ui), 0x100).Single();
            Data[refcount + 4] = 12;
            Log(Setter + 0x100, rdata, "XwebUseManager::SetDebugSetting: key=");
            Log(Stop + 0x100, rdata + 0x100, "XwebUseManager::StopWsServer:");
            Pattern(Stop + 0x10, "83 B9 98 00 00 00 03 74 05"); Pattern(Stop + 0x30, "C7 86 98 00 00 00 00 00 00 00");
            Pattern(Caller + 0x10, "E8 ?? ?? ?? ??"); Relative(Caller + 0x10, 1, 5, otherGetter);
            Pattern(Caller + 0x20, "E8 ?? ?? ?? ??"); Relative(Caller + 0x20, 1, 5, getter);
            Pattern(Caller + 0x25, "48 89 C6"); Pattern(Caller + 0xde, "48 89 F1 48 89 FA 41 89 D8");
            Pattern(Caller + 0xe7, "E8 ?? ?? ?? ??"); Relative(Caller + 0xe7, 1, 5, Setter);
            Int32(0x98 + 112 + 10 * 8, rdata + 0x800); Int32(0x98 + 116 + 10 * 8, 144);
            Int32(Raw(rdata + 0x800), 144); Int64(Raw(rdata + 0x800) + 120, 0x180000000L + Guard);
            Seal();
        }
        internal int Raw(int rva)
        {
            rva -= shift;
            if (rva >= 0xa000) return rva - 0xa000 + 0x5400;
            if (rva >= 0x7000) return rva - 0x7000 + 0x4400;
            if (rva >= 0x5000) return rva - 0x5000 + 0x3400;
            return rva - 0x1000 + 0x400;
        }
        internal void Int32(int raw, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, Data, raw, 4);
        private void Int64(int raw, long value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, Data, raw, 8);
        private void Word(int raw, ushort value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, Data, raw, 2);
        private void Section(int index, int rva, int size, int raw, int flags, int virtualSize = 0)
        {
            int at = 0x98 + 0xf0 + index * 40;
            Int32(at + 8, virtualSize == 0 ? size : virtualSize); Int32(at + 12, rva); Int32(at + 16, size); Int32(at + 20, raw); Int32(at + 36, flags);
        }
        internal void Put(int rva, byte[] bytes) => Buffer.BlockCopy(bytes, 0, Data, Raw(rva), bytes.Length);
        internal void Pattern(int rva, string pattern) => Put(rva, new RuntimePattern(pattern).Bytes);
        internal void Relative(int rva, int field, int size, int target) => Int32(Raw(rva) + field, target - rva - size);
        private void Log(int rva, int textRva, string text)
        {
            Pattern(rva, "48 8D 15 ?? ?? ?? ??"); Relative(rva, 3, 7, textRva); Put(textRva, Encoding.ASCII.GetBytes(text + "\0"));
        }
        internal void AddFunction(int rva, int size) => functions.Add(Tuple.Create(rva, rva + size));
        internal void Seal()
        {
            Int32(0x98 + 136, 0xa000 + shift); Int32(0x98 + 140, functions.Count * 12);
            int i = 0;
            foreach (var function in functions.OrderBy(f => f.Item1)) { Int32(0x5400 + i, function.Item1); Int32(0x5404 + i, function.Item2); i += 12; }
        }
    }
}
