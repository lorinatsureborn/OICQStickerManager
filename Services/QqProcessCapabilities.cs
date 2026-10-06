using System.Diagnostics;

namespace OICQStickerManager.Services;

internal readonly record struct QqProcessBuild(int Pid, long Started, string Version);

internal sealed class QqProcessCapabilities
{
    private readonly object _gate = new();
    private Dictionary<int, (long Started, bool Legacy)> _builds = new();

    internal bool IsLegacy(int pid) { lock (_gate) return _builds.TryGetValue(pid, out var build) && build.Legacy; }
    internal bool HasLegacy { get { lock (_gate) return _builds.Values.Any(b => b.Legacy); } }
    internal void MarkTreeUnavailable(int pid)
    {
        lock (_gate)
            if (_builds.TryGetValue(pid, out var build)) _builds[pid] = (build.Started, true);
    }
    internal void Refresh(IEnumerable<QqProcessBuild> processes)
    {
        lock (_gate)
        {
            var next = new Dictionary<int, (long, bool)>();
            foreach (var process in processes)
            {
                bool legacy = Version.TryParse(process.Version.Split('-')[0], out var version)
                    && version < new Version(9, 9, 30);
                if (_builds.TryGetValue(process.Pid, out var previous) && previous.Started == process.Started)
                    legacy |= previous.Legacy;
                next[process.Pid] = (process.Started, legacy);
            }
            _builds = next;
        }
    }

    internal static IReadOnlyList<QqProcessBuild> Collect()
    {
        var builds = new List<QqProcessBuild>();
        foreach (var process in Process.GetProcessesByName("QQ"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    builds.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks,
                        path == null ? "" : FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""));
                }
                catch { builds.Add(new(process.Id, 0, "")); }
            }
        }
        return builds;
    }
}
