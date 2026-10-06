using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace OICQStickerManager.Services;

internal sealed record QqKeyInstallation(string ExecutablePath, string WrapperPath)
{
    internal static QqKeyInstallation Resolve(string executable, string? loadedWrapper = null)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("QQ executable not found.", executable);
        var root = Path.GetDirectoryName(executable)!;
        var versions = Path.Combine(root, "versions");
        string? wrapper = null;
        foreach (var config in new[] { Path.Combine(versions, "config.json"), Path.Combine(root, "config.json") })
        {
            if (!File.Exists(config)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(config));
                if (!document.RootElement.TryGetProperty("curVersion", out var value)) continue;
                var version = value.GetString();
                if (string.IsNullOrWhiteSpace(version) || version is "." or ".."
                    || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Invalid QQ launcher version.");
                wrapper = Path.Combine(versions, version, "resources", "app", "wrapper.node");
                if (!File.Exists(wrapper)) throw new InvalidDataException("Active QQ launcher module is missing.");
                break;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { throw new InvalidDataException("Invalid QQ launcher configuration.", ex); }
        }
        if (wrapper == null)
        {
            var candidates = Directory.Exists(versions)
                ? Directory.EnumerateDirectories(versions).Select(dir => Path.Combine(dir, "resources", "app", "wrapper.node"))
                    .Where(File.Exists).ToArray()
                : [];
            if (candidates.Length != 1) throw new InvalidDataException("QQ launcher version is absent or ambiguous.");
            wrapper = candidates[0];
        }
        if (loadedWrapper != null && !Path.GetFullPath(loadedWrapper).Equals(wrapper, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Running QQ and launcher versions differ. Restart QQ before immediate key capture.");
        return new(executable, wrapper);
    }

    internal static QqKeyInstallation Discover()
    {
        var installations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("QQ"))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable == null) continue;
                    if (!installations.TryGetValue(executable, out var modules))
                        installations.Add(executable, modules = new(StringComparer.OrdinalIgnoreCase));
                    foreach (ProcessModule module in process.Modules)
                        if (module.ModuleName.Equals("wrapper.node", StringComparison.OrdinalIgnoreCase)) modules.Add(module.FileName);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        if (installations.Count > 1) throw new InvalidDataException("Multiple running QQ installations: close the non-target QQ first.");
        if (installations.Count == 1)
        {
            var installation = installations.Single();
            if (installation.Value.Count > 1) throw new InvalidDataException("Multiple running QQ module versions.");
            return Resolve(installation.Key, installation.Value.SingleOrDefault());
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var name in new[] { "QQ", "NTQQ" })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
            if (key?.GetValue("UninstallString") is not string uninstall) continue;
            int end = uninstall.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) continue;
            var path = Path.Combine(Path.GetDirectoryName(uninstall[..(end + 4)].Trim().Trim('"'))!, "QQ.exe");
            if (File.Exists(path)) paths.Add(Path.GetFullPath(path));
        }
        if (paths.Count != 1) throw new InvalidDataException("QQ installation is absent or ambiguous.");
        return Resolve(paths.Single());
    }
}
