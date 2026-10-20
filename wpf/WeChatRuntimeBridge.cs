using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LynkSignTokenHelper
{
    internal sealed class WeChatRuntimeTarget {
        public int ProcessId;
        public long ModuleBase;
        public string ModulePath;
        public long Manager;
        public long UiTaskRunner;
        public int ServerState;
        public bool Initialized;
        public string RuntimeVersion;
    }

    internal static class WeChatRuntimeBridge {
        const long ManagerRva = 0xdb85368;
        const long ExecutorRva = 0xdbd2800;
        const long SetterRva = 0x26a38a0;
        const long UiRunnerRva = 0x527fd30;
        const long ReleaseRva = 0x4ac0b40;
        const long QueryRva = 0x3c7d810;
        const long GuardRva = 0xda2e608;
        const uint WaitObject = 0;
        static readonly string ExpectedRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Tencent\xwechat\XPlugin\Plugins\RadiumWMPF\25510\extracted\runtime\flue.dll");
        internal const string SupportedWeChatVersion = "4.1.13.12";
        internal const string SupportedRuntimeVersion = "2.5.6.25510";

        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr count);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr count);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protection, out uint oldProtection);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeThread(IntPtr thread, out uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] static extern int GetThreadDescription(IntPtr thread, out IntPtr name);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

        static string ThreadName(uint id) {
            IntPtr thread=OpenThread(0x800, false, id), text=IntPtr.Zero;
            if (thread == IntPtr.Zero) return "";
            try { return GetThreadDescription(thread, out text) >= 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text) : ""; }
            finally { if (text != IntPtr.Zero) LocalFree(text); CloseHandle(thread); }
        }
        static void Check(bool value, string action) {
            if (!value) throw new Win32Exception(Marshal.GetLastWin32Error(), action);
        }
        static byte[] Read(IntPtr process, long address, int size) {
            var bytes = new byte[size];
            UIntPtr count;
            Check(ReadProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)size, out count) && count.ToUInt64() == (ulong)size, "读取限定的运行时控制状态失败");
            return bytes;
        }
        static long Pointer(IntPtr process, long address) { return BitConverter.ToInt64(Read(process, address, 8), 0); }
        static void Put64(byte[] bytes, int offset, long value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 8); }
        static void Put32(byte[] bytes, int offset, int value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        static void VerifyCode(IntPtr process, long moduleBase, long rva, string expected) {
            byte[] bytes = Hex(expected);
            if (!Read(process, moduleBase + rva, bytes.Length).SequenceEqual(bytes)) throw new InvalidOperationException("当前微信运行环境不匹配，已停止打开小程序");
        }
        public static byte[] Hex(string text) {
            text = text.Replace(" ", "");
            return Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
        }

        static bool IsSupportedWeChat() {
            var versions = new List<string>();
            foreach (Process process in Process.GetProcessesByName("Weixin")) {
                using (process) {
                    try {
                        ProcessModule module = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => string.Equals(m.ModuleName, "Weixin.dll", StringComparison.OrdinalIgnoreCase));
                        if (module != null) versions.Add(FileVersionInfo.GetVersionInfo(module.FileName).FileVersion);
                    } catch (Win32Exception) { return false; }
                    catch (InvalidOperationException) { return false; }
                }
            }
            return versions.Count == 1 && versions[0] == SupportedWeChatVersion;
        }

        public static WeChatRuntimeTarget[] FindTargets() {
            var found = new List<WeChatRuntimeTarget>();
            if (!IsSupportedWeChat()) return found.ToArray();
            foreach (Process process in Process.GetProcessesByName("WeChatAppEx")) {
                IntPtr handle = IntPtr.Zero;
                try {
                    ProcessModule module = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => string.Equals(m.FileName, ExpectedRuntime, StringComparison.OrdinalIgnoreCase));
                    if (module == null || FileVersionInfo.GetVersionInfo(module.FileName).FileVersion != SupportedRuntimeVersion) continue;
                    handle = OpenProcess(0x410, false, process.Id);
                    Check(handle != IntPtr.Zero, "读取候选小程序进程失败");
                    long moduleBase = module.BaseAddress.ToInt64();
                    long executor = Pointer(handle, moduleBase + ExecutorRva);
                    long manager = moduleBase + ManagerRva;
                    if (executor == 0 || Pointer(handle, manager) != moduleBase + 0xbe09c38) continue;
                    bool initialized = Read(handle, manager + 0x18, 1)[0] == 1;
                    if (!initialized) continue;
                    long queues = Pointer(handle, executor + 8);
                    long runner = queues == 0 ? 0 : Pointer(handle, queues + 0x28);
                    int state = BitConverter.ToInt32(Read(handle, manager + 0xe4, 4), 0);
                    VerifyCode(handle, moduleBase, SetterRva, "41 57 41 56 41 55 41 54 56 57 55 53 48 81 ec 98 01 00 00");
                    VerifyCode(handle, moduleBase, UiRunnerRva, "56 57 53 48 83 ec 20 48 89 d7 48 89 ce");
                    VerifyCode(handle, moduleBase, QueryRva, "89 d0 c3");
                    if (runner == 0) throw new InvalidOperationException("UI 任务队列尚未准备好");
                    found.Add(new WeChatRuntimeTarget { ProcessId=process.Id, ModuleBase=moduleBase, ModulePath=module.FileName, Manager=manager, UiTaskRunner=runner, ServerState=state, Initialized=initialized, RuntimeVersion=FileVersionInfo.GetVersionInfo(module.FileName).FileVersion });
                } catch (InvalidOperationException) { throw; }
                catch (Win32Exception) { throw; }
                finally { if (handle != IntPtr.Zero) CloseHandle(handle); process.Dispose(); }
            }
            return found.ToArray();
        }

        // Version-specific Chromium task ABI. The starter only posts; Invoke runs on CrBrowserMain.
        // Destroy tail-calls SetEvent so the callback page is no longer executing when released.
        static byte[] BuildCode() {
            var code = Enumerable.Repeat((byte)0xcc, 0x220).ToArray();
            byte[] starter = Hex(
                "48 83 ec 58 48 89 4c 24 40 49 89 ca 48 8d 4c 24 20 " +
                "49 8b 52 48 41 ff 52 30 48 8b 4c 24 20 48 89 4c 24 48 " +
                "4c 8b 54 24 40 49 8b 52 50 4d 8b 42 58 45 31 c9 " +
                "48 8b 01 48 8b 00 4d 8b 5a 40 41 ff 13 " +
                "4c 8b 54 24 40 41 88 42 28 48 8b 4c 24 48 f0 ff 49 08 " +
                "75 09 4c 8b 54 24 40 41 ff 52 38 31 c0 48 83 c4 58 c3");
            byte[] invoke = Hex(
                "48 83 ec 28 48 8b 49 20 48 89 4c 24 20 49 89 ca " +
                "41 ff 52 68 4c 8b 54 24 20 41 89 42 2c 41 83 7a 20 ff 74 13 " +
                "49 8b 0a 49 8b 52 10 45 8b 42 20 4d 8b 4a 18 41 ff 52 08 " +
                "4c 8b 54 24 20 41 c7 42 24 01 00 00 00 48 83 c4 28 c3");
            byte[] destroy = Hex("48 8b 41 30 48 8b 49 28 ff e0");
            Buffer.BlockCopy(starter, 0, code, 0, starter.Length);
            Buffer.BlockCopy(invoke, 0, code, 0x100, invoke.Length);
            Buffer.BlockCopy(destroy, 0, code, 0x200, destroy.Length);
            return code;
        }

        public static void SetServer(WeChatRuntimeTarget target, bool enabled, Action onPosted = null) {
            if (CallTarget(target, enabled ? 1 : 0, onPosted) != "CrBrowserMain") throw new InvalidOperationException("微信启动任务的线程校验失败，请重新打开微信后重试。");
        }
        public static void CheckQueue(WeChatRuntimeTarget target) {
            if (CallTarget(target, -1) != "CrBrowserMain") throw new InvalidOperationException("当前微信运行环境不匹配，已停止打开小程序。");
        }
        static string CallTarget(WeChatRuntimeTarget target, int desired, Action onPosted = null) {
            IntPtr process = IntPtr.Zero, memory = IntPtr.Zero, executable = IntPtr.Zero;
            IntPtr completion = IntPtr.Zero, remoteCompletion = IntPtr.Zero, thread = IntPtr.Zero;
            bool started = false, threadFinished = false, closureFinished = false;
            try {
                WeChatRuntimeTarget current = FindTargets().Single(t => t.ProcessId == target.ProcessId);
                if (current.ModuleBase != target.ModuleBase || (desired != 0 && current.ServerState != 0)) throw new InvalidOperationException("运行时状态已改变，已停止");
                process = OpenProcess(0x47a, false, target.ProcessId);
                Check(process != IntPtr.Zero, "打开已核对的小程序进程失败");
                completion = CreateEvent(IntPtr.Zero, true, false, null);
                Check(completion != IntPtr.Zero, "创建本地启动完成事件失败");
                Check(DuplicateHandle(GetCurrentProcess(), completion, process, out remoteCompletion, 0, false, 2), "复制启动完成事件失败");
                memory = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
                executable = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
                Check(memory != IntPtr.Zero && executable != IntPtr.Zero, "分配启动临时页失败");
                long data = memory.ToInt64(), codeAddress = executable.ToInt64(), moduleBase = target.ModuleBase;
                var bytes = new byte[4096];
                // Control: manager, setter, string*, completion*, desired (-1 probes), status, runner getter.
                // BindStateBase: refcount, Invoke, Destroy, query callback, control*, event, SetEvent.
                Put64(bytes, 0, target.Manager); Put64(bytes, 8, moduleBase + SetterRva);
                Put64(bytes, 0x10, data + 0x160); Put64(bytes, 0x18, data + 0x1c0); Put32(bytes, 0x20, desired);
                Put64(bytes, 0x30, moduleBase + UiRunnerRva); Put64(bytes, 0x38, moduleBase + ReleaseRva);
                Put64(bytes, 0x40, moduleBase + GuardRva); Put64(bytes, 0x48, data + 0x100);
                Put64(bytes, 0x50, data + 0x120); Put64(bytes, 0x58, data + 0x1e0);
                Put64(bytes, 0x120, data + 0x300); Put64(bytes, 0x128, data + 0x320);
                Put32(bytes, 0x130, 1); Put64(bytes, 0x138, codeAddress + 0x100);
                byte[] key = Encoding.ASCII.GetBytes("xweb_use_enable_ws_server\0");
                Put64(bytes, 0x160, data + 0x180); Put64(bytes, 0x168, key.Length - 1);
                Put64(bytes, 0x170, unchecked((long)0x8000000000000020UL));
                Buffer.BlockCopy(key, 0, bytes, 0x180, key.Length);
                Put64(bytes, 0x1e0, data + 0x200);
                Put32(bytes, 0x200, 1); Put64(bytes, 0x208, codeAddress + 0x100);
                Put64(bytes, 0x210, codeAddress + 0x200); Put64(bytes, 0x218, moduleBase + QueryRva);
                Put64(bytes, 0x220, data); Put64(bytes, 0x228, remoteCompletion.ToInt64());
                IntPtr localModule = GetModuleHandle("kernelbase.dll");
                long localSetEvent = GetProcAddress(localModule, "SetEvent").ToInt64();
                long localGetThreadId = GetProcAddress(localModule, "GetCurrentThreadId").ToInt64();
                long remoteKernelBase;
                using (Process targetProcess = Process.GetProcessById(target.ProcessId)) {
                    remoteKernelBase = targetProcess.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, "kernelbase.dll", StringComparison.OrdinalIgnoreCase)).BaseAddress.ToInt64();
                }
                if (localSetEvent == 0 || localGetThreadId == 0 || localModule == IntPtr.Zero) throw new InvalidOperationException("系统完成事件地址无效");
                Put64(bytes, 0x230, remoteKernelBase + localSetEvent - localModule.ToInt64());
                Put64(bytes, 0x68, remoteKernelBase + localGetThreadId - localModule.ToInt64());
                byte[] name = Encoding.ASCII.GetBytes("LynkTokenHelper\0");
                Buffer.BlockCopy(name, 0, bytes, 0x300, name.Length); Buffer.BlockCopy(name, 0, bytes, 0x320, name.Length);
                UIntPtr written;
                Check(WriteProcessMemory(process, memory, bytes, (UIntPtr)bytes.Length, out written) && written.ToUInt64() == (ulong)bytes.Length, "写入启动参数失败");
                byte[] code = BuildCode();
                Check(WriteProcessMemory(process, executable, code, (UIntPtr)code.Length, out written) && written.ToUInt64() == (ulong)code.Length, "写入启动回调失败");
                uint previous;
                Check(VirtualProtectEx(process, executable, (UIntPtr)4096, 0x20, out previous), "设置启动回调执行权限失败");
                Check(FlushInstructionCache(process, executable, (UIntPtr)code.Length), "刷新启动回调失败");
                uint threadId;
                thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, executable, memory, 0, out threadId);
                Check(thread != IntPtr.Zero, "投递启动任务失败"); started = true;
                onPosted?.Invoke();
                threadFinished = WaitForSingleObject(thread, 5000) == WaitObject;
                closureFinished = WaitForSingleObject(completion, 5000) == WaitObject;
                if (!threadFinished || !closureFinished) throw new InvalidOperationException("微信未响应启动任务，请重新打开微信后重试。");
                uint exitCode;
                Check(GetExitCodeThread(thread, out exitCode), "读取启动投递结果失败");
                byte[] marker = Read(process, data + 0x24, 5);
                if (exitCode != 0 || marker[4] != 1 || BitConverter.ToInt32(marker, 0) != 1) throw new InvalidOperationException("启动任务未正常完成");
                uint runtimeThread=BitConverter.ToUInt32(Read(process, data + 0x2c, 4),0);
                return ThreadName(runtimeThread);
            } finally {
                bool mayFree = !started || (threadFinished && closureFinished);
                if (!mayFree && process != IntPtr.Zero) { uint status; if (GetExitCodeProcess(process, out status) && status != 259) mayFree = true; }
                try {
                    if (mayFree && process != IntPtr.Zero) {
                        if (!ReleaseResources(process, memory, executable, remoteCompletion))
                            throw new InvalidOperationException("微信启动资源未能完全清理，请重新打开微信后重试。");
                    } else if (process != IntPtr.Zero) {
                        // A queued callback owns these pages until Destroy signals. Never free live code.
                        IntPtr pendingProcess=process, pendingThread=thread, pendingEvent=completion;
                        IntPtr pendingData=memory, pendingCode=executable, pendingRemoteEvent=remoteCompletion;
                        var recovery = new Thread(() => {
                            try {
                                while (!HasExited(pendingProcess) && (WaitForSingleObject(pendingThread, 0) != WaitObject || WaitForSingleObject(pendingEvent, 0) != WaitObject)) Thread.Sleep(100);
                                ReleaseResources(pendingProcess, pendingData, pendingCode, pendingRemoteEvent);
                            } finally { CloseHandle(pendingThread); CloseHandle(pendingEvent); CloseHandle(pendingProcess); }
                    }) { IsBackground=false, Name="LynkRuntimeCleanup" };
                    recovery.Start();
                    process=thread=completion=IntPtr.Zero;
                }
                } finally {
                    if (thread != IntPtr.Zero) CloseHandle(thread);
                    if (completion != IntPtr.Zero) CloseHandle(completion);
                    if (process != IntPtr.Zero) CloseHandle(process);
                }
            }
        }
        static bool HasExited(IntPtr process) {
            uint status;
            return GetExitCodeProcess(process, out status) && status != 259;
        }
        static bool ReleaseResources(IntPtr process, IntPtr data, IntPtr code, IntPtr remoteEvent) {
            if (HasExited(process)) return true;
            bool dataFreed = data == IntPtr.Zero || VirtualFreeEx(process, data, UIntPtr.Zero, 0x8000);
            bool codeFreed = code == IntPtr.Zero || VirtualFreeEx(process, code, UIntPtr.Zero, 0x8000);
            bool eventClosed = remoteEvent == IntPtr.Zero;
            if (!eventClosed) {
                IntPtr copied;
                if (DuplicateHandle(process, remoteEvent, GetCurrentProcess(), out copied, 0, false, 3)) { CloseHandle(copied); eventClosed=true; }
            }
            return (dataFreed && codeFreed && eventClosed) || HasExited(process);
        }
    }
}
