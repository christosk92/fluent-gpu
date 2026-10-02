using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentGpu.Media;

namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// The Windows MMCSS "Pro Audio" registration (spec §7.9/§13) — the <see cref="IRtThreadCharacteristics"/> leaf. The RT
/// feed thread calls <see cref="Enter"/> once at start-up to join the <c>Pro Audio</c> MMCSS task, so the scheduler treats
/// it as a glitch-sensitive real-time thread (and DEMOTES it if a callback overruns — the spec's bounded-work guard).
/// Disposing the token reverts the characteristics on the SAME thread. <see cref="EnterDecode"/> joins the lower <c>Audio</c>
/// task for the decode-ahead producers and the clock thread. AOT-clean via <c>[LibraryImport]</c> over
/// <c>avrt.dll</c> — no COM, no ComWrappers. On-box only (no automated gate registers a real MMCSS thread).
/// </summary>
public sealed partial class MmcssProAudio : IRtThreadCharacteristics
{
    /// <summary>True after the last <see cref="Enter"/> succeeded and false after one failed (false until the first call) — the
    /// app's "Playback health" diagnostics card reads it. Written once per RT-thread start, read from any thread.</summary>
    public static volatile bool ProAudioRegistered;

    /// <inheritdoc/>
    public IDisposable? Enter()
    {
        uint taskIndex = 0;
        nint handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        if (handle == 0)
        {
            int win32Err = Marshal.GetLastWin32Error();
            ProAudioRegistered = false;
            // Always-on trace (house rule: diagnostics are never env-gated). Debug.WriteLine below is
            // [Conditional("DEBUG")]-erased from a shipping build, so on its own it makes a failed MMCSS registration
            // INVISIBLE in a user's production log — the RT thread silently runs without glitch-sensitive scheduling
            // protection and the only symptom is the scattered hiccup we're trying to diagnose. FormatSink is invoked
            // once here at thread start (not on the RT path itself), so building the string is fine.
            WasapiAudioDevice.FormatSink?.Invoke($"mmcss Pro Audio registration failed (win32 {win32Err})");
            Debug.WriteLine($"MmcssProAudio: AvSetMmThreadCharacteristics failed (win32 {win32Err}).");
            return null;   // fall back to normal priority — never crash the RT thread over a scheduling hint
        }
        ProAudioRegistered = true;
        WasapiAudioDevice.FormatSink?.Invoke($"mmcss Pro Audio registration ok taskIndex={taskIndex}");
        return new Token(handle);
    }

    /// <summary>Register the CURRENT thread in the MMCSS <c>Audio</c> task — the class for decode-ahead producers and the clock
    /// thread: scheduled above every normal-class thread (the reporter compiles while listening), below the RT feed's
    /// <c>Pro Audio</c>. Disposing the token reverts the registration on the SAME thread. A failed registration is logged and
    /// returns null (the thread keeps its plain priority — never crash a worker over a scheduling hint). Deliberately does NOT
    /// touch <see cref="ProAudioRegistered"/>, which tracks the RT thread only.</summary>
    public IDisposable? EnterDecode()
    {
        uint taskIndex = 0;
        nint handle = AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
        if (handle == 0)
        {
            WasapiAudioDevice.FormatSink?.Invoke($"mmcss Audio registration failed (win32 {Marshal.GetLastWin32Error()})");
            return null;
        }
        WasapiAudioDevice.FormatSink?.Invoke($"mmcss Audio registration ok taskIndex={taskIndex}");
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
