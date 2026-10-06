using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OICQStickerManager.Services;

internal static class HardwareKeyCapture
{
    internal static string? Capture(int pid, Func<ulong> resolveAddress, TimeSpan timeout, CancellationToken ct, Action<string> log)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Key capture requires an x64 debugger.");
        using var session = new Session(pid, log);
        return session.Run(resolveAddress, timeout, ct);
    }

    private sealed record Registers(ulong[] Addresses, ulong Dr6, ulong Dr7, int Slot);

    private sealed class Session(int pid, Action<string> log) : IDisposable
    {
        private readonly Dictionary<uint, IntPtr> _threads = new();
        private readonly Dictionary<uint, Registers> _saved = new();
        private readonly HashSet<uint> _resumeFlags = new();
        private IntPtr _process;
        private IntPtr _eventProcess;
        private bool _attached;
        private bool _exited;
        private ulong _address;

        internal string? Run(Func<ulong> resolveAddress, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _process = Native.OpenProcess(0x410, false, pid);
            if (_process == IntPtr.Zero) throw new Win32Exception();
            if (!Native.IsWow64Process2(_process, out ushort machine, out ushort native) || machine != 0 || native != 0x8664)
                throw new PlatformNotSupportedException("Only native AMD64 QQ processes are supported.");
            if (!Native.DebugActiveProcess((uint)pid)) throw new Win32Exception();
            _attached = true;
            if (!Native.DebugSetProcessKillOnExit(false)) throw new Win32Exception();
            var watch = Stopwatch.StartNew();
            bool initialBreakpoint = true;
            string? key = null;
            while (!ct.IsCancellationRequested && watch.Elapsed < timeout && key == null && !_exited)
            {
                if (!Native.WaitForDebugEvent(out var ev, 100))
                {
                    if (Marshal.GetLastWin32Error() == 121) continue;
                    throw new Win32Exception();
                }
                uint status = 0x10002;
                try
                {
                    switch (ev.Code)
                    {
                        case 3:
                            CloseFile(ev.Data);
                            _eventProcess = ReadHandle(ev.Data, 8);
                            _threads.Add(ev.ThreadId, ReadHandle(ev.Data, 16));
                            break;
                        case 2:
                            _threads.Add(ev.ThreadId, ReadHandle(ev.Data, 0));
                            break;
                        case 4:
                            // ContinueDebugEvent closes the event's thread handle on exit.
                            _threads.Remove(ev.ThreadId);
                            _saved.Remove(ev.ThreadId);
                            _resumeFlags.Remove(ev.ThreadId);
                            break;
                        case 5:
                            _exited = true;
                            _threads.Clear();
                            _saved.Clear();
                            _eventProcess = IntPtr.Zero;
                            break;
                        case 6:
                            CloseFile(ev.Data);
                            break;
                        case 1:
                            uint code = BitConverter.ToUInt32(ev.Data, 0);
                            status = 0x80010001;
                            if (code == 0x80000003 && initialBreakpoint)
                            {
                                initialBreakpoint = false;
                                status = 0x10002;
                            }
                            else if (code == 0x80000004 && _saved.TryGetValue(ev.ThreadId, out var registers))
                            {
                                using var context = new Context(0x100013);
                                Check(Native.GetThreadContext(_threads[ev.ThreadId], context.Pointer));
                                if ((context.Read64(104) & (1UL << registers.Slot)) != 0 && context.Read64(248) == _address)
                                {
                                    status = 0x10002;
                                    var bytes = new byte[17];
                                    if (Native.ReadProcessMemory(_process, (IntPtr)context.Read64(184), bytes, 17, out var read)
                                        && read == 17 && bytes[16] == 0 && bytes.Take(16).All(b => b is >= 32 and <= 126))
                                    {
                                        key = Encoding.ASCII.GetString(bytes, 0, 16);
                                        RestoreAll();
                                    }
                                    else
                                    {
                                        if ((context.Read32(68) & 0x10000) == 0) _resumeFlags.Add(ev.ThreadId);
                                        context.Write32(68, context.Read32(68) | 0x10000);
                                        context.Write64(104, context.Read64(104) & ~(1UL << registers.Slot));
                                        Check(Native.SetThreadContext(_threads[ev.ThreadId], context.Pointer));
                                    }
                                }
                            }
                            break;
                    }
                    if (!_exited && (ev.Code is 2 or 3 or 6))
                    {
                        if (_address == 0) _address = resolveAddress();
                        if (_address != 0) Install();
                    }
                }
                finally { Check(Native.ContinueDebugEvent(ev.ProcessId, ev.ThreadId, status)); }
            }
            return key;
        }

        private void Install()
        {
            foreach (var (id, thread) in _threads)
            {
                if (_saved.ContainsKey(id)) continue;
                using var context = new Context(0x100010);
                Check(Native.GetThreadContext(thread, context.Pointer));
                ulong dr7 = context.Read64(112);
                int slot = Enumerable.Range(0, 4).FirstOrDefault(i => (dr7 & (3UL << (2 * i))) == 0, -1);
                if (slot < 0) throw new InvalidOperationException("All hardware breakpoint slots are already in use.");
                var original = new Registers(Enumerable.Range(0, 4).Select(i => context.Read64(72 + 8 * i)).ToArray(), context.Read64(104), dr7, slot);
                context.Write64(72 + 8 * slot, _address);
                context.Write64(112, (dr7 & ~(15UL << (16 + 4 * slot))) | (1UL << (2 * slot)));
                Check(Native.SetThreadContext(thread, context.Pointer));
                _saved.Add(id, original);
                log("hardware breakpoint installed");
            }
        }

        private void RestoreAll()
        {
            Exception? error = null;
            foreach (var (id, registers) in _saved.ToArray())
            {
                var thread = _threads[id];
                if (Native.WaitForSingleObject(thread, 0) == 0) { _saved.Remove(id); continue; }
                bool suspended = false;
                try
                {
                    if (Native.SuspendThread(thread) == uint.MaxValue) throw new Win32Exception();
                    suspended = true;
                    using var context = new Context(_resumeFlags.Contains(id) ? 0x100011u : 0x100010u);
                    Check(Native.GetThreadContext(thread, context.Pointer));
                    for (int i = 0; i < 4; i++) context.Write64(72 + 8 * i, registers.Addresses[i]);
                    context.Write64(104, registers.Dr6);
                    context.Write64(112, registers.Dr7);
                    if (_resumeFlags.Remove(id)) context.Write32(68, context.Read32(68) & ~0x10000u);
                    Check(Native.SetThreadContext(thread, context.Pointer));
                    _saved.Remove(id);
                }
                catch (Exception ex) { error ??= ex; }
                finally { if (suspended && Native.ResumeThread(thread) == uint.MaxValue) error ??= new Win32Exception(); }
            }
            if (error != null) throw new InvalidOperationException("Could not restore debugger registers.", error);
        }

        public void Dispose()
        {
            try { if (_attached && !_exited) RestoreAll(); }
            finally
            {
                try
                {
                    if (_attached && !_exited) Check(Native.DebugActiveProcessStop((uint)pid));
                }
                finally
                {
                    foreach (var thread in _threads.Values) Native.CloseHandle(thread);
                    if (_eventProcess != IntPtr.Zero) Native.CloseHandle(_eventProcess);
                    if (_process != IntPtr.Zero) Native.CloseHandle(_process);
                    _attached = false;
                }
            }
        }
        private static void Check(bool success) { if (!success) throw new Win32Exception(); }
        private static IntPtr ReadHandle(byte[] bytes, int offset) => (IntPtr)BitConverter.ToInt64(bytes, offset);
        private static void CloseFile(byte[] bytes)
        {
            var handle = ReadHandle(bytes, 0);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) Native.CloseHandle(handle);
        }
    }

    // Windows AMD64 CONTEXT is 1232 bytes and requires actual 16-byte pointer alignment.
    private sealed class Context : IDisposable
    {
        private readonly IntPtr _allocation = Marshal.AllocHGlobal(1247);
        internal IntPtr Pointer { get; }
        internal Context(uint flags)
        {
            Pointer = (IntPtr)((_allocation.ToInt64() + 15) & ~15L);
            Marshal.Copy(new byte[1232], 0, Pointer, 1232);
            Write32(48, flags);
        }
        internal ulong Read64(int offset) => unchecked((ulong)Marshal.ReadInt64(Pointer, offset));
        internal uint Read32(int offset) => unchecked((uint)Marshal.ReadInt32(Pointer, offset));
        internal void Write64(int offset, ulong value) => Marshal.WriteInt64(Pointer, offset, unchecked((long)value));
        internal void Write32(int offset, uint value) => Marshal.WriteInt32(Pointer, offset, unchecked((int)value));
        public void Dispose() => Marshal.FreeHGlobal(_allocation);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DebugEvent
        {
            internal uint Code, ProcessId, ThreadId, Padding;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)] internal byte[] Data;
        }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DebugActiveProcess(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DebugActiveProcessStop(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DebugSetProcessKillOnExit(bool kill);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WaitForDebugEvent(out DebugEvent ev, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort nativeMachine);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, nuint size, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    }
}
