using System;
using System.Runtime.Versioning;
using System.Threading;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>Media Foundation's answer to "can the displays the video is on carry HDCP?" (<see cref="OutputProtection.QueryHdcp"/>).</summary>
public enum HdcpAnswer : byte
{
    /// <summary>No answer: no protected runtime is up, or the query itself failed (the HRESULT says which).</summary>
    Unknown = 0,
    /// <summary>A display the video intersects cannot carry HDCP at the level asked (MF_MEDIA_ENGINE_CANPLAY_NOT_SUPPORTED).</summary>
    NotSupported = 1,
    /// <summary>The handshake is still running (MF_MEDIA_ENGINE_CANPLAY_MAYBE): ask again in 500 ms; it settles within ~10 s.</summary>
    Pending = 2,
    /// <summary>Every display the video intersects carries HDCP at that level (MF_MEDIA_ENGINE_CANPLAY_PROBABLY).</summary>
    Supported = 3,
}

/// <summary>Which signal said the output protection did not take.</summary>
public enum OutputProtectionCause : byte
{
    /// <summary>MF_MEDIA_ENGINE_EVENT_OPMINFO: the OPM (HDCP) failed and the trust authority let playback fall back.</summary>
    OpmInfo,
    /// <summary>The licence's key went OUTPUT_RESTRICTED: it decrypts, but the picture is withheld from this output.</summary>
    KeyOutputRestricted,
    /// <summary>The media engine failed with MF_E_POLICY_UNSUPPORTED: the licence's output policy cannot be met here.</summary>
    PolicyUnsupported,
}

/// <summary>One "the output protection did not take" report, with the engine's own numbers.</summary>
public readonly record struct OutputProtectionNotice(OutputProtectionCause Cause, long A, long B);

/// <summary>
/// Output protection (HDCP) for the protected picture, as the app needs it to keep a display that cannot carry HDCP from blanking:
/// the window the picture is in, Media Foundation's HDCP query, and the engine's own "it did not take" reports.
/// <para><b>Nothing here is a passive pre-check.</b> No Windows API reports whether a SINK (a monitor behind a dock, an adapter) can
/// carry HDCP without running the handshake: the GPU only knows its own connector until HDCP is turned on. <see cref="QueryHdcp"/>
/// turns HDCP on to answer (its documentation says so), so it is asked only once protected video is already engaged on that display,
/// where it reports the engagement instead of starting a new one.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public static class OutputProtection
{
    /// <summary>MF_E_POLICY_UNSUPPORTED (0xC00D7159).</summary>
    internal const int PolicyUnsupported = unchecked((int)0xC00D7159);

    /// <summary>MF_MEDIAKEY_STATUS_OUTPUT_RESTRICTED.</summary>
    internal const int KeyOutputRestricted = 7;

    private static long s_window;

    /// <summary>The window the protected picture is presented in (the host of the engine's OPM window), 0 before the first placement.
    /// Kept after the session ends: it is where the next one will be. Any thread.</summary>
    public static nuint VideoWindow => (nuint)Volatile.Read(ref s_window);

    /// <summary>Raised when the engine reports that output protection did not take on the video's display (<see cref="OutputProtectionCause"/>).
    /// Runs on the native notifier thread: a handler only records and posts, never touches UI state inline.</summary>
    public static event Action<OutputProtectionNotice>? Failed;

    /// <summary>Media Foundation's HDCP feature query at <paramref name="level"/> (1 = HDCP, 2 = HDCP 2.2 Type 1) for the displays the
    /// process's video intersects, under the key system the runtime negotiated. Synchronous and possibly slow on its first call after a
    /// display change (the OS caches the answer until the topology changes): call it on a worker thread, at most every 500 ms. It RUNS
    /// the HDCP handshake to answer, so call it only while protected video is engaged on that display. <paramref name="hr"/> is the
    /// native result (the CANPLAY value, or the failing HRESULT).</summary>
    public static HdcpAnswer QueryHdcp(int level, out int hr)
    {
        hr = PrNative.EHandle;
        if (!ProtectedVideoRuntime.IsAvailable) return HdcpAnswer.Unknown;
        ulong rt = ProtectedVideoRuntime.Shared.Handle;
        if (rt == 0) return HdcpAnswer.Unknown;
        try { hr = PrNative.FgPrRuntimeQueryHdcp(rt, level); }
        catch (Exception) { return HdcpAnswer.Unknown; }   // a stale DLL without the export: no answer, never a crash
        return hr switch
        {
            0 => HdcpAnswer.NotSupported,
            1 => HdcpAnswer.Pending,
            2 => HdcpAnswer.Supported,
            _ => HdcpAnswer.Unknown,
        };
    }

    /// <summary>The session placed the OPM window over its picture in <paramref name="hostWindow"/> (every pump: one volatile write).</summary>
    internal static void NoteVideoWindow(nuint hostWindow)
    {
        if (hostWindow != 0) Volatile.Write(ref s_window, (long)hostWindow);
    }

    internal static void Raise(in OutputProtectionNotice notice)
    {
        try { Failed?.Invoke(notice); } catch { /* a handler must never throw into the native notifier */ }
    }
}
