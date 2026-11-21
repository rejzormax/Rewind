using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rewind.Services;

/// <summary>Windows closes this handle on process termination, including crashes, killing orphan capture engines.</summary>
internal static class ChildProcessJob
{
    private static readonly SafeFileHandle? Job = Create();

    public static void Attach(nint process)
    {
        if (Job is { IsInvalid: false, IsClosed: false }) AssignProcessToJobObject(Job, process);
    }

    private static SafeFileHandle? Create()
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid) { job.Dispose(); return null; }
        var information = new ExtendedLimits();
        information.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
        job.Dispose(); return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
