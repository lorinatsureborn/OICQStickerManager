using System.Diagnostics;
using System.Runtime.InteropServices;
using OICQStickerManager.Services;

internal static class DebuggerTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Hardware key capture restores its owned debuggee after success", () => Program.RunAsync(CaptureAndRestore(true, false))),
        ("Hardware key capture restores its owned debuggee after timeout", () => Program.RunAsync(CaptureAndRestore(false, false))),
        ("Hardware key capture restores its owned debuggee after cancellation", () => Program.RunAsync(CaptureAndRestore(false, true))),
        ("Hardware key capture continues after an unreadable key argument", () => Program.RunAsync(CaptureAndRestore(true, false, true))),
    ];
    private const string Key = "audit-key-only!1";
    internal static int Debuggee()
    {
        var address = VirtualAlloc(IntPtr.Zero, 4096, 0x3000, 0x40);
        byte[] code = [0x9c, 0x58, 0xc3]; // pushfq; pop rax; ret (fixture only).
        Marshal.Copy(code, 0, address, code.Length);
        var function = Marshal.GetDelegateForFunctionPointer<KeyFunction>(address);
        var key = Marshal.StringToHGlobalAnsi(Key);
        try
        {
            Console.WriteLine(address.ToInt64());
            string? command;
            while ((command = Console.ReadLine()) != null)
            {
                if (command == "GO") function(IntPtr.Zero, IntPtr.Zero, key);
                else if (command == "GO_BAD") { function(IntPtr.Zero, IntPtr.Zero, new IntPtr(1)); Console.WriteLine("BAD_DONE"); }
                else if (command == "CHECK")
                {
                    ulong flags = function(IntPtr.Zero, IntPtr.Zero, key);
                    Console.WriteLine(!IsDebuggerPresent() && (flags & 0x100) == 0
                        && Enumerable.Range(0, 3).All(i => Marshal.ReadByte(address, i) == code[i]) ? "RESTORED" : "BROKEN");
                    break;
                }
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(key); VirtualFree(address, 0, 0x8000); }
    }
    private static async Task CaptureAndRestore(bool trigger, bool cancel, bool invalidFirst = false)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--debuggee");
        using var process = Process.Start(info)!;
        using var cancellation = new CancellationTokenSource();
        Task<string?>? capture = null;
        try
        {
            var address = ulong.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
            var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture = Task.Run(() => HardwareKeyCapture.Capture(process.Id, () => address, TimeSpan.FromMilliseconds(1000), cancellation.Token,
                message => { if (message == "hardware breakpoint installed") installed.TrySetResult(); }));
            var ready = await Task.WhenAny(installed.Task, capture);
            Program.Require(ready == installed.Task, "no hardware breakpoint was installed");
            if (invalidFirst)
            {
                await process.StandardInput.WriteLineAsync("GO_BAD");
                Program.Require(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) == "BAD_DONE", "unreadable key argument stalled the debuggee");
            }
            if (trigger) await process.StandardInput.WriteLineAsync("GO");
            if (cancel) cancellation.Cancel();
            var key = await capture.WaitAsync(TimeSpan.FromSeconds(3));
            Program.Require(trigger ? key == Key : key == null, "capture result did not match the controlled function call");
            await process.StandardInput.WriteLineAsync("CHECK");
            Program.Require(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) == "RESTORED", "debuggee retained a debugger, trap flag, or modified instructions");
            await process.WaitForExitAsync();
        }
        finally
        {
            cancellation.Cancel();
            if (capture != null) try { await capture.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ulong KeyFunction(IntPtr first, IntPtr second, IntPtr key);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll")] private static extern bool IsDebuggerPresent();
}
