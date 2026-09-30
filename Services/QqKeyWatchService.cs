using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OICQStickerManager.Services;

/// <summary>
/// QQ 密钥静默抓取器（2026-09-30 定案）：监视 QQ.exe 进程启动，在新进程刚出现时附加调试器，
/// wrapper.node 加载后于密钥函数入口下断点，用户登录（自动登录也算）瞬间从 R8 读出 16 字符
/// passphrase，然后**恢复原指令并干净分离**——QQ 继续正常运行，用户无感知。
/// 与官方脚本同一套调试逻辑（qq-win-db-key），差别仅在触发方式（附加 vs 启动）与结束方式（分离 vs 终止）。
/// 安全红线：附加后第一时间 DebugSetProcessKillOnExit(false)，飞鸟退出/崩溃绝不连带杀 QQ。
/// 错过窗口（QQ 已登录后才附加）→ 超时静默分离，等下次 QQ 重启再试。
/// </summary>
public sealed class QqKeyWatchService : IDisposable
{
    private readonly Action<string?> _onKey;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly object _attachLock = new();
    private volatile bool _captured;

    public QqKeyWatchService(Action<string?> onKey, Action<string>? log = null)
    {
        _onKey = onKey;
        _log = log ?? (_ => { });
    }

    public void Start()
    {
        lock (_attachLock)
        {
            if (_pollTask != null) return;
            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }
    }

    public void Dispose()
    {
        lock (_attachLock)
        {
            _cts?.Cancel();
            _pollTask = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ———— 进程轮询：发现新的 QQ.exe 就尝试附加 ————

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var tried = new HashSet<int>();
        // 已经在跑的 QQ 大概率已完成登录（错过派生窗口）；只盯新出现的进程
        foreach (var p in Process.GetProcessesByName("QQ")) tried.Add(p.Id);

        _log($"watcher started, {tried.Count} running QQ process(es) skipped");

        while (!ct.IsCancellationRequested && !_captured)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("QQ"))
                {
                    if (ct.IsCancellationRequested || _captured) return;
                    if (tried.Contains(p.Id)) continue;
                    tried.Add(p.Id);
                    var pid = p.Id;
                    _ = Task.Run(() => TryAttachAsync(pid, ct), ct);
                }
            }
            catch { /* 枚举竞态无害 */ }

            try { await Task.Delay(600, ct); } catch { return; }
        }
    }

    private async Task TryAttachAsync(int pid, CancellationToken ct)
    {
        try
        {
            _log($"attaching to new QQ process {pid}");
            var session = new DebugSession(pid, _log);
            var key = await Task.Run(() => session.AttachAndCapture(TimeSpan.FromSeconds(45), ct), ct);
            if (key != null)
            {
                _captured = true;
                _log("key captured, detaching, watcher done");
                _onKey(key);
            }
            else
            {
                _log($"no key from {pid} (already logged in or not the UI process); detached");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log("attach failed: " + ex.Message);
        }
    }

    // ———— 调试会话（移植自官方脚本 DebugApi.KeyExtractor，改为附加式 + 干净分离） ————

    private sealed class DebugSession
    {
        private readonly int _pid;
        private readonly Action<string> _log;
        private IntPtr _hProcess = IntPtr.Zero;
        private ulong _wrapperBase;
        private ulong _breakpointAddress;
        private byte _originalByte;
        private bool _breakpointActive;
        private readonly Dictionary<uint, ulong> _steppingThreads = new();
        private ulong _functionRva;

        public DebugSession(int pid, Action<string> log)
        {
            _pid = pid;
            _log = log;
        }

        public string? AttachAndCapture(TimeSpan timeout, CancellationToken ct)
        {
            if (!Native.DebugActiveProcess((uint)_pid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // 红线：调试器退出绝不连带杀 QQ
            Native.DebugSetProcessKillOnExit(false);

            _hProcess = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, _pid);
            if (_hProcess == IntPtr.Zero)
            {
                Native.DebugActiveProcessStop((uint)_pid);
                return null;
            }

            try
            {
                // 附加后先查 wrapper.node 是否已加载（Electron 启动需数秒，通常未加载 → 等 LOAD_DLL 事件）
                var wrapperPath = FindWrapperModulePath();
                if (wrapperPath != null)
                {
                    _functionRva = StaticAnalysis.GetKeyFunctionRva(wrapperPath);
                    _wrapperBase = GetModuleBaseAddress("wrapper.node");
                    SetBreakpoint();
                }

                return DebugLoop(timeout, ct);
            }
            finally
            {
                Detach();
            }
        }

        private string? FindWrapperModulePath()
        {
            var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, (uint)_pid);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;
            try
            {
                var entry = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
                if (Native.Module32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (string.Equals(entry.szModule, "wrapper.node", StringComparison.OrdinalIgnoreCase))
                            return entry.szExePath;
                    } while (Native.Module32NextW(snapshot, ref entry));
                }
            }
            finally { Native.CloseHandle(snapshot); }
            return null;
        }

        private ulong GetModuleBaseAddress(string moduleName)
        {
            var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, (uint)_pid);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;
            try
            {
                var entry = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
                if (Native.Module32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                            return (ulong)entry.modBaseAddr;
                    } while (Native.Module32NextW(snapshot, ref entry));
                }
            }
            finally { Native.CloseHandle(snapshot); }
            return 0;
        }

        private void SetBreakpoint()
        {
            _breakpointAddress = _wrapperBase + _functionRva;
            var buffer = new byte[1];
            if (!Native.ReadProcessMemory(_hProcess, (IntPtr)_breakpointAddress, buffer, (UIntPtr)1, out _) ||
                !Native.WriteProcessMemory(_hProcess, (IntPtr)_breakpointAddress, new byte[] { 0xCC }, (UIntPtr)1, out _))
            {
                _log("breakpoint write failed");
                return;
            }
            _originalByte = buffer[0]; // 拆断点时必须还原这条原始指令，写 0 会损坏 QQ 代码段
            Native.FlushInstructionCache(_hProcess, (IntPtr)_breakpointAddress, (UIntPtr)1);
            _breakpointActive = true;
            _log($"breakpoint set at 0x{_breakpointAddress:X}");
        }

        private void RestoreOriginalByte()
        {
            Native.WriteProcessMemory(_hProcess, (IntPtr)_breakpointAddress, new[] { _originalByte }, (UIntPtr)1, out _);
            Native.FlushInstructionCache(_hProcess, (IntPtr)_breakpointAddress, (UIntPtr)1);
        }

        private void ReinstallBreakpoint()
        {
            Native.WriteProcessMemory(_hProcess, (IntPtr)_breakpointAddress, new byte[] { 0xCC }, (UIntPtr)1, out _);
            Native.FlushInstructionCache(_hProcess, (IntPtr)_breakpointAddress, (UIntPtr)1);
            _breakpointActive = true;
        }

        private string? DebugLoop(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            string? extractedKey = null;

            while (!ct.IsCancellationRequested && extractedKey == null)
            {
                if (DateTime.UtcNow > deadline) break; // 已登录过/非目标进程：超时分离

                if (!Native.WaitForDebugEvent(out var dbg, 1000))
                {
                    if (Marshal.GetLastWin32Error() == 121) continue; // ERROR_SEM_TIMEOUT 正常
                    break;
                }

                uint continueStatus = Native.DBG_CONTINUE;
                switch (dbg.dwDebugEventCode)
                {
                    case Native.CREATE_PROCESS_DEBUG_EVENT:
                    case Native.LOAD_DLL_DEBUG_EVENT:
                    {
                        // 关闭事件自带的文件句柄
                        var hFile = (IntPtr)BitConverter.ToInt64(dbg.u, 0);
                        if (hFile != IntPtr.Zero && hFile != new IntPtr(-1)) Native.CloseHandle(hFile);

                        if (dbg.dwDebugEventCode == Native.LOAD_DLL_DEBUG_EVENT && _wrapperBase == 0)
                        {
                            var wrapperPath = FindWrapperModulePath();
                            if (wrapperPath != null)
                            {
                                _functionRva = StaticAnalysis.GetKeyFunctionRva(wrapperPath);
                                _wrapperBase = GetModuleBaseAddress("wrapper.node");
                                if (_wrapperBase != 0) SetBreakpoint();
                            }
                        }
                        break;
                    }

                    case Native.EXCEPTION_DEBUG_EVENT:
                    {
                        uint code = BitConverter.ToUInt32(dbg.u, 0);
                        ulong address = BitConverter.ToUInt64(dbg.u, 16);

                        if (code == Native.EXCEPTION_BREAKPOINT)
                        {
                            if (_breakpointActive && address == _breakpointAddress)
                            {
                                RestoreOriginalByte();
                                var key = HandleBreakpoint(dbg.dwThreadId);
                                if (key != null)
                                {
                                    ReinstallBreakpoint();
                                    extractedKey = key; // 不终止进程：走 finally 分离
                                }
                                else
                                {
                                    SetSingleStep(dbg.dwThreadId);
                                }
                            }
                            // 系统初始断点或其他断点：直接继续
                        }
                        else if (code == Native.EXCEPTION_SINGLE_STEP)
                        {
                            if (_steppingThreads.Remove(dbg.dwThreadId))
                            {
                                ClearTrapFlag(dbg.dwThreadId);
                                ReinstallBreakpoint();
                            }
                        }
                        else
                        {
                            continueStatus = Native.DBG_EXCEPTION_NOT_HANDLED;
                        }
                        break;
                    }

                    case Native.EXIT_PROCESS_DEBUG_EVENT:
                        // 进程自己退出：调试关系随之结束
                        return extractedKey;
                }

                Native.ContinueDebugEvent(dbg.dwProcessId, dbg.dwThreadId, continueStatus);
            }

            return extractedKey;
        }

        private string? HandleBreakpoint(uint threadId)
        {
            var hThread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
            if (hThread == IntPtr.Zero) return null;
            try
            {
                var ctx = new Native.CONTEXT64 { ContextFlags = Native.CONTEXT_ALL, FltSave = new byte[512], VectorRegister = new ulong[26] };
                if (!Native.GetThreadContext(hThread, ref ctx)) return null;

                ctx.Rip -= 1; // 回到原指令
                var buffer = new byte[256];
                if (!Native.ReadProcessMemory(_hProcess, (IntPtr)ctx.R8, buffer, (UIntPtr)256, out var read) || read == UIntPtr.Zero)
                {
                    Native.SetThreadContext(hThread, ref ctx); // RIP 已回退，单步越过
                    SetSingleStep(threadId);
                    return null;
                }

                var nullIndex = Array.IndexOf(buffer, (byte)0);
                if (nullIndex < 0) nullIndex = (int)read;
                var keyString = Encoding.ASCII.GetString(buffer, 0, nullIndex);

                bool valid = keyString.Length == 16;
                if (valid)
                {
                    foreach (var c in keyString)
                    {
                        if (c is < (char)32 or > (char)126) { valid = false; break; }
                    }
                }

                if (valid)
                {
                    _log("key found at breakpoint");
                    // RIP 已回退到原指令：单步执行原指令后继续，QQ 无感
                    Native.SetThreadContext(hThread, ref ctx);
                    SetSingleStep(threadId);
                    return keyString;
                }

                Native.SetThreadContext(hThread, ref ctx);
                SetSingleStep(threadId);
                return null;
            }
            finally
            {
                Native.CloseHandle(hThread);
            }
        }

        private void SetSingleStep(uint threadId)
        {
            var hThread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
            if (hThread == IntPtr.Zero) return;
            try
            {
                var ctx = new Native.CONTEXT64 { ContextFlags = Native.CONTEXT_ALL, FltSave = new byte[512], VectorRegister = new ulong[26] };
                if (Native.GetThreadContext(hThread, ref ctx))
                {
                    ctx.EFlags |= 0x100;
                    Native.SetThreadContext(hThread, ref ctx);
                }
            }
            finally { Native.CloseHandle(hThread); }
            _steppingThreads[threadId] = _breakpointAddress;
            _breakpointActive = false;
        }

        private void ClearTrapFlag(uint threadId)
        {
            var hThread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
            if (hThread == IntPtr.Zero) return;
            try
            {
                var ctx = new Native.CONTEXT64 { ContextFlags = Native.CONTEXT_ALL, FltSave = new byte[512], VectorRegister = new ulong[26] };
                if (Native.GetThreadContext(hThread, ref ctx))
                {
                    ctx.EFlags &= ~0x100u;
                    Native.SetThreadContext(hThread, ref ctx);
                }
            }
            finally { Native.CloseHandle(hThread); }
        }

        private void Detach()
        {
            // 恢复断点原字节后分离，QQ 完全恢复正常执行
            if (_breakpointActive) RestoreOriginalByte();
            Native.DebugActiveProcessStop((uint)_pid);
            if (_hProcess != IntPtr.Zero) { Native.CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
            _log($"detached from {_pid}");
        }
    }

    // ———— 静态 PE 分析（移植自官方脚本：字符串 RVA → LEA → 函数 RVA） ————

    internal static class StaticAnalysis
    {
        private static readonly byte[] TargetPattern = Encoding.ASCII.GetBytes("nt_sqlite3_key_v2: db=%p zDb=%s");

        public static ulong GetKeyFunctionRva(string wrapperNodePath)
        {
            var bytes = File.ReadAllBytes(wrapperNodePath);
            ushort eLfanew = BitConverter.ToUInt16(bytes, 0x3C);
            uint numberOfSections = BitConverter.ToUInt16(bytes, eLfanew + 6);
            ushort sizeOfOptionalHeader = BitConverter.ToUInt16(bytes, eLfanew + 20);
            int optionalHeaderOffset = eLfanew + 24;

            string? rdataName = null, textName = null;
            uint rdataVa = 0, rdataRaw = 0, rdataSize = 0, textVa = 0, textRaw = 0, textSize = 0;
            int sectionHeadersOffset = optionalHeaderOffset + sizeOfOptionalHeader;
            for (int i = 0; i < numberOfSections; i++)
            {
                int s = sectionHeadersOffset + i * 40;
                var name = Encoding.ASCII.GetString(bytes, s, 8).TrimEnd('\0');
                uint va = BitConverter.ToUInt32(bytes, s + 12);
                uint rawSize = BitConverter.ToUInt32(bytes, s + 16);
                uint rawPtr = BitConverter.ToUInt32(bytes, s + 20);
                if (name == ".rdata") { rdataName = name; rdataVa = va; rdataRaw = rawPtr; rdataSize = rawSize; }
                if (name == ".text") { textName = name; textVa = va; textRaw = rawPtr; textSize = rawSize; }
            }
            if (rdataName == null || textName == null) throw new InvalidDataException("PE 节缺失");

            // 1. 字符串在 .rdata 的 RVA
            int patternOffset = IndexOf(bytes, TargetPattern, (int)rdataRaw, (int)(rdataRaw + rdataSize));
            if (patternOffset < 0) throw new InvalidDataException("目标字符串不在 .rdata");
            ulong stringRva = rdataVa + (uint)(patternOffset - rdataRaw);

            // 2. .text 里找 rip 相对 LEA（REX.W=0x48 前缀 + 0x8D + modrm=xx000101）
            ulong leaRva = 0;
            for (int i = 1; i < (int)textSize - 6; i++)
            {
                int fileOffset = (int)textRaw + i;
                if (bytes[fileOffset] != 0x8D) continue;
                if ((bytes[fileOffset - 1] & 0xF8) != 0x48) continue;
                if ((bytes[fileOffset + 1] & 0xC7) != 0x05) continue;
                int disp = BitConverter.ToInt32(bytes, fileOffset + 2);
                ulong instrRva = textVa + (uint)(i - 1);
                ulong targetRva = instrRva + 7 + (ulong)disp; // LEA reg,[rip+disp32] 长 7 字节
                if (targetRva == stringRva) { leaRva = instrRva; break; }
            }
            if (leaRva == 0) throw new InvalidDataException("LEA 引用未找到");

            // 3. .pdata（异常目录）二分找包含 LEA 的 RUNTIME_FUNCTION → 函数起始 RVA
            uint functionBegin = 0;
            int pdataRaw = (int)rdataRaw; // .pdata 通常在 .rdata 附近；按节扫描所有节更稳：
            // 简化：官方实现按异常目录所在节处理；此处直接全文件找 .pdata 节
            uint pdataVa = 0, pdataRawPtr = 0, pdataSize = 0;
            for (int i = 0; i < numberOfSections; i++)
            {
                int s = sectionHeadersOffset + i * 40;
                var name = Encoding.ASCII.GetString(bytes, s, 8).TrimEnd('\0');
                if (name != ".pdata") continue;
                pdataVa = BitConverter.ToUInt32(bytes, s + 12);
                pdataSize = BitConverter.ToUInt32(bytes, s + 16);
                pdataRawPtr = BitConverter.ToUInt32(bytes, s + 20);
            }
            if (pdataSize == 0) throw new InvalidDataException(".pdata 缺失");

            int entryCount = (int)(pdataSize / 12);
            int left = 0, right = entryCount - 1;
            uint target = (uint)leaRva;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                int e = (int)pdataRawPtr + mid * 12;
                uint begin = BitConverter.ToUInt32(bytes, e);
                uint end = BitConverter.ToUInt32(bytes, e + 4);
                if (target < begin) right = mid - 1;
                else if (target >= end) left = mid + 1;
                else { functionBegin = begin; break; }
            }
            if (functionBegin == 0) throw new InvalidDataException("函数起始未找到");
            return functionBegin;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
        {
            for (int i = start; i <= end - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }

    internal static class Native
    {
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint TH32CS_SNAPMODULE = 0x00000008;
        public const uint TH32CS_SNAPMODULE32 = 0x00000010;
        public const uint THREAD_ALL_ACCESS = 0x1FFFFF;

        public const uint EXCEPTION_DEBUG_EVENT = 1;
        public const uint CREATE_THREAD_DEBUG_EVENT = 2;
        public const uint CREATE_PROCESS_DEBUG_EVENT = 3;
        public const uint EXIT_THREAD_DEBUG_EVENT = 4;
        public const uint EXIT_PROCESS_DEBUG_EVENT = 5;
        public const uint LOAD_DLL_DEBUG_EVENT = 6;
        public const uint UNLOAD_DLL_DEBUG_EVENT = 7;

        public const uint EXCEPTION_BREAKPOINT = 0x80000003;
        public const uint EXCEPTION_SINGLE_STEP = 0x80000004;
        public const uint DBG_CONTINUE = 0x00010002;
        public const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

        public const uint CONTEXT_AMD64 = 0x00100000;
        public const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x0001;
        public const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x0002;
        public const uint CONTEXT_FULL = CONTEXT_CONTROL | CONTEXT_INTEGER | (CONTEXT_AMD64 | 0x0008);
        public const uint CONTEXT_ALL = CONTEXT_FULL | (CONTEXT_AMD64 | 0x0004) | (CONTEXT_AMD64 | 0x0010);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DebugActiveProcess(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DebugActiveProcessStop(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DebugSetProcessKillOnExit(bool killOnExit);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WaitForDebugEvent(out DEBUG_EVENT debugEvent, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ContinueDebugEvent(uint processId, uint threadId, uint continueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, UIntPtr size, out UIntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, UIntPtr size, out UIntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, UIntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadContext(IntPtr thread, ref CONTEXT64 context);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadContext(IntPtr thread, ref CONTEXT64 context);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32W entry);

        [StructLayout(LayoutKind.Sequential)]
        public struct DEBUG_EVENT
        {
            public uint dwDebugEventCode;
            public uint dwProcessId;
            public uint dwThreadId;
            private uint _padding;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
            public byte[] u;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MODULEENTRY32W
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExePath;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        public struct CONTEXT64
        {
            public ulong P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;
            public uint ContextFlags;
            public uint MxCsr;
            public ushort SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;
            public uint EFlags;
            public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
            public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi;
            public ulong R8, R9, R10, R11, R12, R13, R14, R15, Rip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
            public byte[] FltSave;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
            public ulong[] VectorRegister;
            public ulong VectorControl, DebugControl, LastBranchToRip, LastBranchFromRip, LastExceptionToRip, LastExceptionFromRip;
        }
    }
}
