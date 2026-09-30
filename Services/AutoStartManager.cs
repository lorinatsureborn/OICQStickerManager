using Microsoft.Win32;

namespace OICQStickerManager.Services;

/// <summary>
/// 开机自启动：写 HKCU 的 Run 键，注册表本身就是持久化（不经 config.json）。
/// 值指向当前 exe 绝对路径（Environment.ProcessPath 对单文件发布安全）；
/// 重新开启时会覆盖为当前路径，程序移动/重装后自愈。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Asuka";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                throw new InvalidOperationException("无法定位当前可执行文件路径");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
