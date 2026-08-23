using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// A Win32 <b>job object</b> that kills every process assigned to it when the last handle to the job closes —
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. The Shell pillar's answer to "my helper processes outlive a crashed app":
/// assign each child right after <c>Process.Start</c> and the OS guarantees they die with this process, including when
/// it is killed from Task Manager (an <c>AppDomain</c>/<c>ProcessExit</c> hook cannot).
///
/// <para>Hand-declared <c>kernel32</c> P/Invoke in the house <c>[LibraryImport]</c> style (TerraFX does not project the
/// job-object family) — AOT/trim-clean, no COM, no reflection.</para>
///
/// <para><b>Nesting.</b> Windows 8+ supports nested jobs, so assigning a child that is already in another job (a CI
/// agent's, a debugger's) succeeds. On the rare refusal <see cref="Assign"/> answers false rather than throwing: a
/// module that is not job-bound still runs correctly, it just loses the die-with-the-app guarantee.</para>
///
/// <para><b>Lifetime.</b> The kill fires when the LAST handle closes, so keep the instance alive for as long as the
/// children should live and <see cref="Dispose"/> it (or let the process exit) to take them down.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ChildProcessJob : IDisposable
{
    private nint _handle;

    private ChildProcessJob(nint handle) => _handle = handle;

    /// <summary>True while the job handle is open.</summary>
    public bool IsOpen => _handle != 0;

    /// <summary>
    /// Create an unnamed job whose processes are killed when this object (the last handle) is closed. Returns null when
    /// the OS refuses — the caller then runs its children unbound rather than not at all.
    /// </summary>
    public static ChildProcessJob? CreateKillOnClose()
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0) return null;

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        unsafe
        {
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info,
                    (uint)Unsafe.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                CloseHandle(job);
                return null;
            }
        }

        return new ChildProcessJob(job);
    }

    /// <summary>
    /// Assign an already-started process to this job. Pass the process's native handle
    /// (<c>System.Diagnostics.Process.Handle</c>). Answers false when the job is closed or the OS refused.
    /// </summary>
    /// <param name="processHandle">The child's native process handle (must have <c>PROCESS_SET_QUOTA | PROCESS_TERMINATE</c>).</param>
    public bool Assign(nint processHandle)
    {
        if (_handle == 0 || processHandle == 0) return false;
        return AssignProcessToJobObject(_handle, processHandle);
    }

    /// <summary>The last-error code of the most recent failed call, for diagnostics.</summary>
    public static int LastError => Marshal.GetLastWin32Error();

    /// <summary>A human-readable form of <see cref="LastError"/>.</summary>
    /// <param name="error">The Win32 error code.</param>
    public static string DescribeError(int error) => new Win32Exception(error).Message;

    /// <summary>Close the job handle — which terminates every process still assigned to it.</summary>
    public void Dispose()
    {
        nint h = _handle;
        _handle = 0;
        if (h != 0) CloseHandle(h);
    }

    // ── kernel32 job-object ABI (hand-declared; not projected by TerraFX) ─────────────────────────────────────────────

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
