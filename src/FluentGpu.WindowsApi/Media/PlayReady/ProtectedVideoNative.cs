using System;
using System.Runtime.InteropServices;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The blittable mirror of <c>ops/tools/playready-native/FgPlayReady.h</c> — the ONE place the native ABI is declared
/// managed-side, shared by <see cref="ProtectedVideoRuntime"/> (runtime + license) and <see cref="ProtectedVideoSession"/>
/// (session + transport + snapshot). Every struct is blittable (pointers are hand-marshaled, never a marshalled
/// <c>string</c> field) so <c>LibraryImport</c> emits a direct call with no marshalling stub, and every entry point is
/// NativeAOT-clean: no reflection, no delegate marshalling, callbacks are <c>UnmanagedCallersOnly</c> function pointers.
/// <para><b>Layout is load-bearing.</b> The native header ships with this file; a <c>structSize</c> mismatch is an
/// <c>E_INVALIDARG</c> from the DLL, not silent corruption — the old per-field <c>offsetof</c> ABI probing is gone
/// because header and DLL are built and shipped together.</para>
/// </summary>
internal static unsafe partial class PrNative
{
    internal const string LibraryName = "FluentGpu.PlayReady.Native.dll";

    // ── events (FgPrEvent) ─────────────────────────────────────────────────────────────────────────────────────────

    internal const int EvRuntimeReady = 1;
    internal const int EvRuntimeFailed = 2;
    internal const int EvLicenseUsable = 10;
    internal const int EvLicenseFailed = 11;
    internal const int EvLicenseExpired = 12;
    internal const int EvBytes = 20;
    internal const int EvBuffered = 21;
    internal const int EvKeyframes = 22;
    internal const int EvMetadata = 30;
    internal const int EvCanPlay = 31;
    internal const int EvFirstFrame = 32;
    internal const int EvHandle = 33;
    internal const int EvPosition = 34;
    internal const int EvSeeking = 35;
    internal const int EvSeeked = 36;
    internal const int EvPlaying = 37;
    internal const int EvPaused = 38;
    internal const int EvEnded = 39;
    internal const int EvError = 40;
    internal const int EvRepresentation = 41;
    internal const int EvAttached = 42;
    internal const int EvDetached = 43;
    internal const int EvLog = 44;

    // ── state (FgPrState) ──────────────────────────────────────────────────────────────────────────────────────────

    internal const int StateIdle = 0;
    internal const int StateLoading = 1;
    internal const int StatePlaying = 2;
    internal const int StatePaused = 3;
    internal const int StateStopped = 4;
    internal const int StateError = 5;
    internal const int StateEnded = 6;

    // ── seek modes (FgPrSeekMode) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>SetCurrentTime — decode from the keyframe to the exact PTS (a committed seek).</summary>
    internal const int SeekExact = 0;
    /// <summary>SetCurrentTimeEx(APPROXIMATE) — present the keyframe ≤ target (a scrub preview).</summary>
    internal const int SeekKeyframe = 1;

    /// <summary>"Let the native side find the keyframe itself" — the planner has no table yet.</summary>
    internal const long NoKeyframeHint = -1;

    internal const int EHandle = unchecked((int)0x80070006);
    internal const int EInvalidArg = unchecked((int)0x80070057);
    internal const int EFail = unchecked((int)0x80004005);

    /// <summary>Blittable mirror of <c>FgPrOpenDesc</c>. Every <c>nint</c> is an HGlobal UTF-16 string (or the PSSH
    /// bytes) the caller owns for the duration of the <c>FgPrSessionCreate</c> call only — native COPIES everything it
    /// keeps.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OpenDesc
    {
        public uint StructSize;
        public nint InitUrl;
        public nint SegmentBaseUrl;
        public nint SegmentPrefix;
        public nint SegmentSuffix;
        public int StartNumber;
        public int SegmentCount;
        public int SegmentStrideSeconds;
        public int SegmentLengthMs;
        public nint AudioInitUrl;
        public nint AudioSegmentBaseUrl;
        public nint AudioSegmentPrefix;
        public nint AudioSegmentSuffix;
        public nint Pssh;
        public int PsshLen;
        public nint KeyIdHex;
        public nint HttpHeaders;
        public long DurationMs;
        public long StartPositionMs;
        public int StartPaused;
        public long RetainBehindMs;
        public long BufferAheadMs;
        public long StoreBudgetBytes;
    }

    /// <summary>Blittable mirror of <c>FgPrSnapshot</c> — one atomic read of a session's observable state, the same
    /// shape the clear path's <c>VideoEngineSnapshot</c> has.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        public uint StructSize;
        public int State;
        public int ErrorHr;
        public int ReadyState;
        public ulong Handle;
        public int Width;
        public int Height;
        public int Seeking;
        public int ActiveRepresentation;
        public long PositionMs;
        public long PositionQpc;
        public long DurationMs;
        public long BufferedAheadMs;
        public long RetainedBehindMs;
        public long FirstFrameQpc;
        public ulong BytesDownloaded;
        public ulong DownloadElapsedMs;
        public ulong StoreBytes;
    }

    /// <summary>Blittable mirror of <c>FgPrProbeResult</c> — what the demuxer found in a local fragmented MP4.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProbeResult
    {
        public uint StructSize;
        public int KeyframeCount;
        public int SampleCount;
        public int Width;
        public int Height;
        public int NalLengthSize;
        public int Encrypted;
        public int SubsampleCount;
        public long DurationMs;
    }

    // ── runtime ────────────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FgPrRuntimeCreate(string storePath,
        delegate* unmanaged[Stdcall]<nint, ulong, int, long, long, char*, void> cb, nint ctx, ulong* @out);

    [LibraryImport(LibraryName)]
    internal static partial void FgPrRuntimeDestroy(ulong rt);

    [LibraryImport(LibraryName)]
    internal static partial long FgPrRuntimeUptimeMs(ulong rt);

    // ── license ────────────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FgPrLicenseAcquire(ulong rt, byte* pssh, int psshLen, string keyIdHex,
        delegate* unmanaged[Stdcall]<nint, ulong, byte*, int, char*, delegate* unmanaged[Stdcall]<nint, byte*, int, int, void>, nint, int> relay,
        nint relayCtx, ulong* @out);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrLicenseState(ulong rt, ulong lic);

    [LibraryImport(LibraryName)]
    internal static partial void FgPrLicenseRelease(ulong rt, ulong lic);

    // ── sessions ───────────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionCreate(ulong rt, OpenDesc* desc, ulong* @out);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionPrefetch(ulong rt, ulong s, long aroundMs, int segments);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionAttach(ulong rt, ulong s, ulong lic);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionDetach(ulong rt, ulong s);

    [LibraryImport(LibraryName)]
    internal static partial void FgPrSessionDestroy(ulong rt, ulong s);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionPlay(ulong rt, ulong s);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionPause(ulong rt, ulong s);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionSeek(ulong rt, ulong s, long targetMs, int mode, long keyframeMs);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionSetVolume(ulong rt, ulong s, double volume);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionSetRate(ulong rt, ulong s, double rate);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionSetStreamSize(ulong rt, ulong s, int width, int height);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FgPrSessionSelectRepresentation(ulong rt, ulong s, int index, string initUrl,
        string @base, string prefix, string suffix);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionSnapshot(ulong rt, ulong s, Snapshot* @out);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionGetKeyframes(ulong rt, ulong s, long* @out, int cap);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionGetBuffered(ulong rt, ulong s, long* outPairs, int capPairs);

    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionGetInitProtection(ulong rt, ulong s, byte* psshOut, int psshCap, char* kidOut, int kidCap);

    // ── test seam ──────────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FgPrProbeFile(string path, long* outKeyframes, int cap, ProbeResult* @out);
}
