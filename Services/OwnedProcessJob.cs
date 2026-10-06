using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OICQStickerManager.Services;

internal sealed class OwnedProcessJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private OwnedProcessJob() : base(true) { }
    internal static OwnedProcessJob Attach(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            || !AssignProcessToJobObject(job, process.Handle))
        {
            int error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error);
        }
        return job;
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses;
        public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern OwnedProcessJob CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(OwnedProcessJob job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(OwnedProcessJob job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
