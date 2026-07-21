using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentGpu.Media;

namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// The Windows MMCSS "Pro Audio" registration (spec §7.9/§13) — the <see cref="IRtThreadCharacteristics"/> leaf. The RT
/// feed thread calls <see cref="Enter"/> once at start-up to join the <c>Pro Audio</c> MMCSS task, so the scheduler treats
/// it as a glitch-sensitive real-time thread (and DEMOTES it if a callback overruns — the spec's bounded-work guard).
/// Disposing the token reverts the characteristics on the SAME thread. AOT-clean via <c>[LibraryImport]</c> over
/// <c>avrt.dll</c> — no COM, no ComWrappers. On-box only (no automated gate registers a real MMCSS thread).
/// </summary>
public sealed partial class MmcssProAudio : IRtThreadCharacteristics
{
    /// <inheritdoc/>
    public IDisposable? Enter()
    {
        uint taskIndex = 0;
        nint handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        if (handle == 0)
        {
            Debug.WriteLine($"MmcssProAudio: AvSetMmThreadCharacteristics failed (win32 {Marshal.GetLastWin32Error()}).");
            return null;   // fall back to normal priority — never crash the RT thread over a scheduling hint
        }
        return new Token(handle);
    }

    private sealed class Token : IDisposable
    {
        private nint _handle;
        public Token(nint handle) => _handle = handle;
        public void Dispose()
        {
            if (_handle == 0) return;
            _ = AvRevertMmThreadCharacteristics(_handle);
            _handle = 0;
        }
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint handle);
}
