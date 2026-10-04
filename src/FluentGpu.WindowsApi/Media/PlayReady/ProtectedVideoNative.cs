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
    /// <summary>A representation switch was spliced into the buffer; <c>a</c> = the index now being downloaded. The new
    /// picture shows later, at <see cref="EvRepresentation"/>.</summary>
    internal const int EvRepresentationQueued = 45;
    /// <summary>FORMATCHANGE reported a new natural size (<c>a</c> = width, <c>b</c> = height): a size report only, the
    /// snapshot already carries it and the pump re-asserts the stream size from it.</summary>
    internal const int EvSizeChanged = 46;
    /// <summary>A segment GET failed and the feeder is retrying it with a capped back-off (never an end of track):
    /// <c>a</c> = the segment index, <c>b</c> = the HTTP status (0 = transport failure; 401/403 = an expired signed URL).
    /// Raised once per stall; <see cref="EvFeedRecovered"/> ends it.</summary>
    internal const int EvFeedStalled = 47;
    /// <summary>The <see cref="EvFeedStalled"/> stall is over: a segment landed, or a seek started the feeder afresh.</summary>
    internal const int EvFeedRecovered = 48;
    /// <summary>The engine reported WAITING / STALLED / BUFFERINGSTARTED while playing: its clock stopped for want of data
    /// (<c>a</c> = position ms). The snapshot still reads Playing, so only this event can say the picture is frozen.</summary>
    internal const int EvWaiting = 49;
    /// <summary>The <see cref="EvWaiting"/> wait ended: PLAYING, SEEKED, BUFFERINGENDED or the clock advancing
    /// (<c>a</c> = position ms).</summary>
    internal const int EvResumed = 50;
    /// <summary>The native license table's LRU closed this license's key session to admit another KID (never one an attached
    /// session uses): the event's session handle is the LICENSE handle. The native table is the single eviction authority, so the
    /// managed cache drops the row that holds that handle.</summary>
    internal const int EvLicenseEvicted = 51;
    /// <summary>The CDM restricted the license's key output: <c>a</c> = the restriction now in force (7 = OUTPUT_RESTRICTED,
    /// 2 = OUTPUT_DOWNSCALED, 0 = lifted), <c>b</c> = the previous one. The key still decrypts.</summary>
    internal const int EvLicenseRestricted = 52;
    /// <summary>A USABLE license's key went INTERNAL_ERROR / RELEASED / OUTPUT_NOT_ALLOWED and will never decrypt again:
    /// <c>a</c> = the failing HRESULT (0x8004800N, N = the status), <c>b</c> = the MF_MEDIAKEY_STATUS. Native has evicted it.</summary>
    internal const int EvLicenseRevoked = 53;

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
        public int DownloadingRepresentation;
        public long PositionMs;
        public long PositionQpc;
        public long DurationMs;
        public long BufferedAheadMs;
        public long RetainedBehindMs;
        public long FirstFrameQpc;
        public ulong BytesDownloaded;
        public ulong DownloadElapsedMs;
        public ulong StoreBytes;
        public int StreamWidth;
        public int StreamHeight;
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

    /// <summary><paramref name="adapterLuid"/> is the DXGI adapter LUID packed <c>(HighPart &lt;&lt; 32) | LowPart</c> the D3D11
    /// video device is created on (0 = the default adapter).</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FgPrRuntimeCreateOnAdapter(string storePath,
        delegate* unmanaged[Stdcall]<nint, ulong, int, long, long, char*, void> cb, nint ctx, long adapterLuid, ulong* @out);

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

    /// <summary><paramref name="hostWindow"/> is the presenting window's HWND and the rect its video's client-area rect in device
    /// pixels; native moves its hidden OPM window over it (screen space). S_FALSE = nothing moved (not the attached session).</summary>
    [LibraryImport(LibraryName)]
    internal static partial int FgPrSessionPlaceOpmWindow(ulong rt, ulong s, ulong hostWindow, int left, int top, int right, int bottom);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FgPrSessionSelectRepresentation(ulong rt, ulong s, int index, string initUrl,
        string @base, string prefix, string suffix, int retainMs);

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

/// <summary>
/// The managed form of <c>FgPrOpenDesc</c>: every value <see cref="ProtectedVideoSession.Create"/> DECIDED for one
/// source (defaults applied, the KID normalised, the start position clamped). The production seam marshals it into the
/// blittable <see cref="PrNative.OpenDesc"/> for exactly the duration of the create call; a fake reads it as it is.
/// </summary>
internal sealed record PrOpenDescription
{
    public string? InitUrl { get; init; }
    public string? SegmentBaseUrl { get; init; }
    public string? SegmentPrefix { get; init; }
    public string? SegmentSuffix { get; init; }
    public int StartNumber { get; init; }
    public int SegmentCount { get; init; }
    public int SegmentStrideSeconds { get; init; }
    public int SegmentLengthMs { get; init; }
    public string? AudioInitUrl { get; init; }
    public string? AudioSegmentBaseUrl { get; init; }
    public string? AudioSegmentPrefix { get; init; }
    public string? AudioSegmentSuffix { get; init; }
    public ReadOnlyMemory<byte> Pssh { get; init; }
    public string? KeyIdHex { get; init; }
    public string? HttpHeaders { get; init; }
    public long DurationMs { get; init; }
    public long StartPositionMs { get; init; }
    public bool StartPaused { get; init; }
    public long RetainBehindMs { get; init; }
    public long BufferAheadMs { get; init; }
    public long StoreBudgetBytes { get; init; }
}

/// <summary>
/// The session half of the native seam: every <c>FgPrSession*</c> export <see cref="ProtectedVideoSession"/> calls, in
/// managed shapes (spans, <c>out</c> structs, a <see cref="PrOpenDescription"/>). The real DLL in production
/// (<see cref="PrSessionNative"/>); a recording fake in the engine's tests, so the session's create / start / pump /
/// seek / teardown logic runs with no CDM, no GPU, no network and no window. Every method returns the export's HRESULT
/// (or its count) unchanged.
/// </summary>
internal interface IPrSessionNative
{
    int SessionCreate(ulong runtime, PrOpenDescription desc, out ulong session);
    int SessionPrefetch(ulong runtime, ulong session, long aroundMs, int segments);
    int SessionAttach(ulong runtime, ulong session, ulong license);
    int SessionDetach(ulong runtime, ulong session);
    void SessionDestroy(ulong runtime, ulong session);
    int SessionPlay(ulong runtime, ulong session);
    int SessionPause(ulong runtime, ulong session);
    int SessionSeek(ulong runtime, ulong session, long targetMs, int mode, long keyframeMs);
    int SessionSetVolume(ulong runtime, ulong session, double volume);
    int SessionSetRate(ulong runtime, ulong session, double rate);
    int SessionSetStreamSize(ulong runtime, ulong session, int width, int height);
    /// <summary><c>FgPrSessionPlaceOpmWindow</c>: move the runtime's hidden OPM window over a video at the client-area rect
    /// (<paramref name="left"/>, <paramref name="top"/>, <paramref name="right"/>, <paramref name="bottom"/>, device px) of the
    /// presenting window <paramref name="hostWindow"/>. S_OK = posted; S_FALSE = nothing moved (the session is not the one
    /// attached to the engine, or the runtime has no window).</summary>
    int SessionPlaceOpmWindow(ulong runtime, ulong session, ulong hostWindow, int left, int top, int right, int bottom);
    /// <summary><paramref name="retainMs"/>: negative appends after the last buffered segment (nothing discarded), 0 lands
    /// at the boundary after the playhead, positive lands that many ms ahead of the playhead.</summary>
    int SessionSelectRepresentation(ulong runtime, ulong session, int index, string initUrl, string? baseUrl,
                                    string? prefix, string? suffix, int retainMs);
    /// <summary><c>FgPrSessionSnapshot</c>; <paramref name="snapshot"/> is written only on success.</summary>
    int SessionSnapshot(ulong runtime, ulong session, ref PrNative.Snapshot snapshot);
    /// <summary>Fills <paramref name="into"/>; returns the TOTAL keyframe count (negative = HRESULT).</summary>
    int SessionGetKeyframes(ulong runtime, ulong session, Span<long> into);
    /// <summary>Fills <paramref name="pairs"/> (2 longs per pair); returns the TOTAL pair count (negative = HRESULT).</summary>
    int SessionGetBuffered(ulong runtime, ulong session, Span<long> pairs);
    /// <summary>Fills <paramref name="pssh"/> and the NUL-terminated KID into <paramref name="kid"/>; returns the PSSH
    /// length (0 = not parsed yet / none; negative = HRESULT).</summary>
    int SessionGetInitProtection(ulong runtime, ulong session, Span<byte> pssh, Span<char> kid);
}

/// <summary>The production <see cref="IPrSessionNative"/>: <c>FluentGpu.PlayReady.Native.dll</c>.</summary>
internal sealed unsafe class PrSessionNative : IPrSessionNative
{
    internal static readonly PrSessionNative Instance = new();

    public int SessionCreate(ulong runtime, PrOpenDescription d, out ulong session)
    {
        ulong handle = 0;
        var strings = new NativeStrings();
        try
        {
            var desc = new PrNative.OpenDesc
            {
                StructSize = (uint)sizeof(PrNative.OpenDesc),
                InitUrl = strings.Add(d.InitUrl),
                SegmentBaseUrl = strings.Add(d.SegmentBaseUrl),
                SegmentPrefix = strings.Add(d.SegmentPrefix),
                SegmentSuffix = strings.Add(d.SegmentSuffix),
                StartNumber = d.StartNumber,
                SegmentCount = d.SegmentCount,
                SegmentStrideSeconds = d.SegmentStrideSeconds,
                SegmentLengthMs = d.SegmentLengthMs,
                AudioInitUrl = strings.Add(d.AudioInitUrl),
                AudioSegmentBaseUrl = strings.Add(d.AudioSegmentBaseUrl),
                AudioSegmentPrefix = strings.Add(d.AudioSegmentPrefix),
                AudioSegmentSuffix = strings.Add(d.AudioSegmentSuffix),
                Pssh = strings.AddBytes(d.Pssh.Span),
                PsshLen = d.Pssh.Length,
                KeyIdHex = strings.Add(d.KeyIdHex),
                HttpHeaders = strings.Add(d.HttpHeaders),
                DurationMs = d.DurationMs,
                StartPositionMs = d.StartPositionMs,
                StartPaused = d.StartPaused ? 1 : 0,
                RetainBehindMs = d.RetainBehindMs,
                BufferAheadMs = d.BufferAheadMs,
                StoreBudgetBytes = d.StoreBudgetBytes,
            };
            int hr = PrNative.FgPrSessionCreate(runtime, &desc, &handle);
            session = handle;
            return hr;
        }
        finally
        {
            strings.Free();   // native COPIED everything it keeps during FgPrSessionCreate
        }
    }

    public int SessionPrefetch(ulong runtime, ulong session, long aroundMs, int segments)
        => PrNative.FgPrSessionPrefetch(runtime, session, aroundMs, segments);
    public int SessionAttach(ulong runtime, ulong session, ulong license) => PrNative.FgPrSessionAttach(runtime, session, license);
    public int SessionDetach(ulong runtime, ulong session) => PrNative.FgPrSessionDetach(runtime, session);
    public void SessionDestroy(ulong runtime, ulong session) => PrNative.FgPrSessionDestroy(runtime, session);
    public int SessionPlay(ulong runtime, ulong session) => PrNative.FgPrSessionPlay(runtime, session);
    public int SessionPause(ulong runtime, ulong session) => PrNative.FgPrSessionPause(runtime, session);
    public int SessionSeek(ulong runtime, ulong session, long targetMs, int mode, long keyframeMs)
        => PrNative.FgPrSessionSeek(runtime, session, targetMs, mode, keyframeMs);
    public int SessionSetVolume(ulong runtime, ulong session, double volume) => PrNative.FgPrSessionSetVolume(runtime, session, volume);
    public int SessionSetRate(ulong runtime, ulong session, double rate) => PrNative.FgPrSessionSetRate(runtime, session, rate);
    public int SessionSetStreamSize(ulong runtime, ulong session, int width, int height)
        => PrNative.FgPrSessionSetStreamSize(runtime, session, width, height);
    public int SessionPlaceOpmWindow(ulong runtime, ulong session, ulong hostWindow, int left, int top, int right, int bottom)
        => PrNative.FgPrSessionPlaceOpmWindow(runtime, session, hostWindow, left, top, right, bottom);
    public int SessionSelectRepresentation(ulong runtime, ulong session, int index, string initUrl, string? baseUrl,
                                           string? prefix, string? suffix, int retainMs)
        => PrNative.FgPrSessionSelectRepresentation(runtime, session, index, initUrl, baseUrl!, prefix!, suffix!, retainMs);

    public int SessionSnapshot(ulong runtime, ulong session, ref PrNative.Snapshot snapshot)
    {
        PrNative.Snapshot n = default;
        n.StructSize = (uint)sizeof(PrNative.Snapshot);
        int hr = PrNative.FgPrSessionSnapshot(runtime, session, &n);
        if (hr >= 0) snapshot = n;
        return hr;
    }

    public int SessionGetKeyframes(ulong runtime, ulong session, Span<long> into)
    {
        fixed (long* p = into) return PrNative.FgPrSessionGetKeyframes(runtime, session, p, into.Length);
    }

    public int SessionGetBuffered(ulong runtime, ulong session, Span<long> pairs)
    {
        fixed (long* p = pairs) return PrNative.FgPrSessionGetBuffered(runtime, session, p, pairs.Length / 2);
    }

    public int SessionGetInitProtection(ulong runtime, ulong session, Span<byte> pssh, Span<char> kid)
    {
        fixed (byte* pp = pssh)
        fixed (char* kp = kid)
            return PrNative.FgPrSessionGetInitProtection(runtime, session, pp, pssh.Length, kp, kid.Length);
    }

    /// <summary>HGlobal copies of the descriptor's strings for the duration of ONE native call (native copies what it
    /// keeps). Cold path: one session create.</summary>
    private struct NativeStrings
    {
        private nint[]? _owned;
        private int _count;

        public nint Add(string? s)
        {
            if (s is null) return 0;
            nint p = Marshal.StringToHGlobalUni(s);
            Track(p);
            return p;
        }

        public nint AddBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return 0;
            nint p = Marshal.AllocHGlobal(bytes.Length);
            bytes.CopyTo(new Span<byte>((void*)p, bytes.Length));
            Track(p);
            return p;
        }

        private void Track(nint p)
        {
            _owned ??= new nint[16];
            if (_count == _owned.Length) Array.Resize(ref _owned, _owned.Length * 2);
            _owned[_count++] = p;
        }

        public void Free()
        {
            if (_owned is null) return;
            for (int i = 0; i < _count; i++) Marshal.FreeHGlobal(_owned[i]);
            _count = 0;
        }
    }
}
