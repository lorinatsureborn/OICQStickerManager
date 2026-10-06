using System.Diagnostics;

namespace OICQStickerManager.Services;

internal readonly record struct QqProcessBuild(int Pid, long Started, string Version);

internal sealed class QqProcessCapabilities
{
    private readonly object _gate = new();
    private Dictionary<int, (long Started, bool LegacyVersion, bool TreeUnavailable)> _builds = new();

    internal bool IsLegacy(int pid) { lock (_gate) return _builds.TryGetValue(pid, out var build) && (build.LegacyVersion || build.TreeUnavailable); }
    internal bool HasLegacy { get { lock (_gate) return _builds.Values.Any(b => b.LegacyVersion || b.TreeUnavailable); } }
    internal void MarkTreeUnavailable(int pid)
    {
        lock (_gate)
            if (_builds.TryGetValue(pid, out var build)) _builds[pid] = (build.Started, build.LegacyVersion, true);
    }
    internal void Refresh(IEnumerable<QqProcessBuild> processes)
    {
        lock (_gate)
        {
            var next = new Dictionary<int, (long, bool, bool)>();
            foreach (var process in processes)
            {
                bool legacy = Version.TryParse(process.Version.Split('-')[0], out var version)
                    && version < new Version(9, 9, 30);
                bool unavailable = _builds.TryGetValue(process.Pid, out var previous)
                    && previous.Started == process.Started && previous.TreeUnavailable;
                next[process.Pid] = (process.Started, legacy, unavailable);
            }
            _builds = next;
        }
    }

    // QQ.exe can retain the original install version after many in-place upgrades.
    // A loaded wrapper is authoritative; children without it use the active launcher
    // configuration, never the newest directory. File metadata is only a fallback.
    internal static string ResolveVersion(string executable, string? loadedWrapper, string fallback)
    {
        string? FromWrapper(string wrapper)
        {
            var app = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(wrapper)!);
            var version = app.Parent?.Parent?.Name;
            return System.IO.File.Exists(wrapper) && app.Name == "app" && app.Parent?.Name == "resources"
                && version != null && Version.TryParse(version.Split('-')[0], out _) ? version : null;
        }
        try
        {
            if (loadedWrapper != null && FromWrapper(loadedWrapper) is { } loaded) return loaded;
            return FromWrapper(QqKeyInstallation.Resolve(executable).WrapperPath) ?? fallback;
        }
        catch { return fallback; }
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
                    string? wrapper = null;
                    try
                    {
                        foreach (ProcessModule module in process.Modules)
                            if (module.ModuleName.Equals("wrapper.node", StringComparison.OrdinalIgnoreCase))
                            { wrapper = module.FileName; break; }
                    }
                    catch { /* Some renderer processes do not expose their module list. */ }
                    builds.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks,
                        path == null ? "" : ResolveVersion(path, wrapper, FileVersionInfo.GetVersionInfo(path).FileVersion ?? "")));
                }
                catch { builds.Add(new(process.Id, 0, "")); }
            }
        }
        return builds;
    }
}
