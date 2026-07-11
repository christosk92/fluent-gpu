using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wavee.SpotifyLive.Audio;

/// <summary>A duplex message channel to the audio host. Abstracts process+pipe for tests.</summary>
public interface IIpcChannel : IDisposable
{
    Task SendAsync<T>(string type, long id, T payload, CancellationToken ct);
    Task<(string Type, long Id, System.Text.Json.JsonElement? Payload)> ReadAsync(CancellationToken ct);
}

/// <summary>The real channel: self-relaunches the current signed Wavee binary with --audio-host.</summary>
public sealed class ProcessIpcChannel : IIpcChannel
{
    readonly Process _process;
    readonly IpcPipeTransport _ipc;
    readonly AudioHostJobObject? _job;

    ProcessIpcChannel(Process process, IpcPipeTransport ipc, AudioHostJobObject? job)
    {
        _process = process;
        _ipc = ipc;
        _job = job;
    }

    public static async Task<IIpcChannel> SpawnAsync(string launchToken, WaveeLogger log, CancellationToken ct)
    {
        string pipeName = "WaveeAudio_" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "_" + Guid.NewGuid().ToString("N");
        string exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine Wavee executable path for audio host self-relaunch.");

        if (!File.Exists(exePath)) throw new FileNotFoundException("Wavee executable not found", exePath);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--audio-host");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--token");
        psi.ArgumentList.Add(launchToken);
        psi.ArgumentList.Add("--parent");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Wavee audio host");
        AudioHostJobObject? job = null;
        try
        {
            job = AudioHostJobObject.TryAssign(process, log);
            var ipc = await IpcPipeTransport.ConnectClientAsync(pipeName, ct).ConfigureAwait(false);
            log.Info("Audio host self-relaunch started pid=" + process.Id + " exe=" + exePath);
            return new ProcessIpcChannel(process, ipc, job);
        }
        catch (Exception ex)
        {
            log.Warn("audio host spawn/connect failed: " + ex.Message, ex);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { job?.Dispose(); } catch { }
            process.Dispose();
            throw;
        }
    }

    public Task SendAsync<T>(string type, long id, T payload, CancellationToken ct) =>
        _ipc.SendAsync(type, id, payload, ct);

    public Task<(string Type, long Id, System.Text.Json.JsonElement? Payload)> ReadAsync(CancellationToken ct) =>
        _ipc.ReadAsync(ct);

    public void Dispose()
    {
        try { _ipc.Dispose(); } catch { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        try { _job?.Dispose(); } catch { }
    }
}

sealed class AudioHostJobObject : IDisposable
{
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const int JobObjectExtendedLimitInformation = 9;

    readonly IntPtr _handle;

    AudioHostJobObject(IntPtr handle) => _handle = handle;

    public static AudioHostJobObject? TryAssign(Process process, WaveeLogger log)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            log.Info("audio host job object create failed win32=" + Marshal.GetLastWin32Error());
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)length))
        {
            log.Info("audio host job object configure failed win32=" + Marshal.GetLastWin32Error());
            CloseHandle(job);
            return null;
        }

        if (!AssignProcessToJobObject(job, process.Handle))
        {
            log.Info("audio host job object assign failed win32=" + Marshal.GetLastWin32Error());
            CloseHandle(job);
            return null;
        }

        log.Info("audio host job object assigned pid=" + process.Id);
        return new AudioHostJobObject(job);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
}
