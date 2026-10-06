using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

namespace OICQStickerManager.Services;

/// <summary>Watches future QQ launches and captures a key using temporary hardware breakpoints.
/// Existing QQ processes are not attached; cancellation restores registers and detaches.</summary>
public sealed class QqKeyWatchService(Action<string?> onKey, Action<string>? log = null) : IDisposable
{
    private readonly Action<string> _log = log ?? (_ => { });
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _poll;
    private bool _disposed;
    private int _captured;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _poll != null) return;
            var existing = EnumerateProcesses().ToHashSet();
            _poll = Task.Run(() => PollAsync(existing, _stop.Token));
        }
    }

    private static List<(int Pid, long Started)> EnumerateProcesses()
    {
        var result = new List<(int, long)>();
        foreach (var process in Process.GetProcessesByName("QQ"))
        {
            using (process)
            {
                try { result.Add((process.Id, process.StartTime.ToUniversalTime().Ticks)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }

    private async Task PollAsync(HashSet<(int Pid, long Started)> tried, CancellationToken ct)
    {
        var sessions = new List<Task>();
        _log($"watcher started, {tried.Count} existing QQ processes skipped");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var process in EnumerateProcesses())
                {
                    if (ct.IsCancellationRequested) break;
                    if (!tried.Add(process)) continue;
                    // Debug APIs require attach, event handling and detach on the same OS thread.
                    sessions.Add(Task.Factory.StartNew(() => Capture(process, ct), CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }
                sessions.RemoveAll(t => t.IsCompleted);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(sessions).ConfigureAwait(false); }
    }

    private void Capture((int Pid, long Started) identity, CancellationToken ct)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != identity.Started) return;
            ulong address = 0;
            ulong Resolve()
            {
                if (address != 0) return address;
                process.Refresh();
                foreach (ProcessModule module in process.Modules)
                {
                    if (!module.ModuleName.Equals("wrapper.node", StringComparison.OrdinalIgnoreCase)) continue;
                    address = checked((ulong)module.BaseAddress.ToInt64() + StaticAnalysis.GetKeyFunctionRva(module.FileName));
                    break;
                }
                return address;
            }
            var key = HardwareKeyCapture.Capture(identity.Pid, Resolve, TimeSpan.FromSeconds(45), ct, _log);
            if (key == null || Interlocked.CompareExchange(ref _captured, 1, 0) != 0) return;
            _stop.Cancel();
            onKey(key);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log("key capture failed: " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        Task? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            pending = _poll;
        }
        try { pending?.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }

    internal static class StaticAnalysis
    {
        private static readonly byte[] Pattern = Encoding.ASCII.GetBytes("nt_sqlite3_key_v2: db=%p zDb=%s");
        private static readonly ConcurrentDictionary<(string Path, long Length, long Modified), ulong> Cache = new();

        internal static ulong GetKeyFunctionRva(string path)
        {
            var file = new FileInfo(path);
            if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("Module is too large.");
            return Cache.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc.Ticks), _ => Analyze(path));
        }

        private static ulong Analyze(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var headers = pe.PEHeaders;
                if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus)
                    throw new InvalidDataException("Module is not AMD64 PE32+.");
                var strings = new HashSet<long>();
                foreach (var section in headers.SectionHeaders.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0))
                {
                    var bytes = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
                    for (int offset = 0; offset <= bytes.Length - Pattern.Length; offset++)
                        if (bytes.AsSpan(offset, Pattern.Length).SequenceEqual(Pattern)) strings.Add((long)section.VirtualAddress + offset);
                }
                var references = new HashSet<uint>();
                foreach (var section in headers.SectionHeaders.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
                {
                    var bytes = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
                    for (int offset = 0; offset <= bytes.Length - 7; offset++)
                    {
                        if ((bytes[offset] & 0xf8) != 0x48 || bytes[offset + 1] != 0x8d || (bytes[offset + 2] & 0xc7) != 5) continue;
                        long rva = (long)section.VirtualAddress + offset;
                        if (strings.Contains(rva + 7 + BitConverter.ToInt32(bytes, offset + 3))) references.Add(checked((uint)rva));
                    }
                }
                var directory = headers.PEHeader!.ExceptionTableDirectory;
                if (directory.Size == 0 || directory.Size % 12 != 0) throw new InvalidDataException("Unsupported exception directory.");
                var functions = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
                var matches = new HashSet<uint>();
                for (int offset = 0; offset < functions.Length; offset += 12)
                {
                    uint begin = BitConverter.ToUInt32(functions, offset), end = BitConverter.ToUInt32(functions, offset + 4);
                    if (begin != 0 && references.Any(rva => rva >= begin && rva < end)) matches.Add(begin);
                }
                if (matches.Count != 1) throw new InvalidDataException("Key function is absent or ambiguous.");
                return matches.Single();
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or OverflowException)
            { throw new InvalidDataException("Malformed module layout.", ex); }
        }
    }
}
