using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Native PlayReady playback hosted by the normal FluentGpu desktop process. The native backend is an in-process DLL;
/// it owns Media Foundation, the PlayReady CDM, CENC source, and windowless media engine. Windows may create its own
/// <c>mfpmp.exe</c> Protected Media Path process, but FluentGpu creates no helper, AppContainer, package, or IPC channel.
/// <para>M5: the open takes a <see cref="ProtectedVideoRequest"/> (a source descriptor + the app license relay). License
/// acquisition is bridged BACK to managed: when the native CDM raises a challenge it invokes <see cref="LicenseThunk"/>,
/// which runs the app's <c>WithDrm</c> relay (via <see cref="DrmLicenseBridge"/>, bounded) and hands the license blob to
/// the CDM. The engine never sees a key or a decrypted pixel.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed unsafe partial class DesktopProtectedVideoPlayer : IProtectedVideoPlayer
{
    private const string NativeLibraryName = "FluentGpu.PlayReady.Native.dll";

    private readonly Signal<ProtectedVideoState> _state = new(ProtectedVideoState.Idle);
    private readonly Signal<long> _positionMs = new(0);
    private readonly Signal<long> _durationMs = new(0);
    private readonly Signal<Size2> _naturalSize = new(default);
    private readonly Signal<string?> _error = new(null);
    private readonly string _dataRoot;
    private readonly int _defaultNativeMode;

    private Thread? _thread;
    private ulong _boundHandle;
    private bool _disposed;
    private volatile int _runHr;
    private volatile bool _runCompleted;
    private volatile string? _runError;

    // The active session's license bridge + a background-thread-recorded typed relay error (mirrored on the UI pump).
    private DrmLicenseBridge? _bridge;
    private volatile MediaError? _relayError;

    public DesktopProtectedVideoPlayer(ProtectedVideoOptions? options = null)
    {
        // Test/diagnostic override: Codex and CI may run the desktop app under a restricted account that cannot write
        // the interactive user's LocalAppData. Production does not set this and retains the normal per-user store.
        _dataRoot = Environment.GetEnvironmentVariable("FG_PLAYREADY_DATA_ROOT") is { Length: > 0 } overrideRoot
            ? Path.GetFullPath(overrideRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentGpu", "PlayReady");
        _defaultNativeMode = string.Equals(options?.Mode, "clear", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                if (!NativeLibrary.TryLoad(NativeLibraryName, typeof(DesktopProtectedVideoPlayer).Assembly,
                        DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.AssemblyDirectory, out nint h))
                    return false;
                NativeLibrary.Free(h);
                return true;
            }
            catch { return false; }
        }
    }

    public IReadSignal<ProtectedVideoState> State => _state;
    public IReadSignal<long> PositionMs => _positionMs;
    public IReadSignal<long> DurationMs => _durationMs;
    public IReadSignal<Size2> NaturalSize => _naturalSize;
    public IReadSignal<string?> Error => _error;
    public bool HasSurface => _boundHandle != 0;

    /// <inheritdoc/>
    public void Start(ProtectedVideoRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is { IsAlive: true }) return;
        Directory.CreateDirectory(_dataRoot);
        _error.Value = null;
        _relayError = null;
        _state.Value = ProtectedVideoState.Loading;

        var system = request.Drm?.System ?? DrmSystem.PlayReady;
        _bridge = new DrmLicenseBridge(request.LicenseRelay, system, request.LicenseTimeout,
            new MediaLocus(null, request.Source, null, null, null));

        _thread = new Thread(() => RunNative(request)) { IsBackground = true, Name = "fgpu-playready-desktop" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void RunNative(ProtectedVideoRequest request)
    {
        // Pin this instance so the static license thunk can recover the bridge across the P/Invoke boundary.
        GCHandle self = GCHandle.Alloc(this);
        IntPtr initUrl = Marshal.StringToHGlobalUni(request.InitUrl);
        IntPtr segBase = Marshal.StringToHGlobalUni(request.SegmentBaseUrl);
        IntPtr segPrefix = Marshal.StringToHGlobalUni(request.SegmentPrefix);
        IntPtr segSuffix = Marshal.StringToHGlobalUni(request.SegmentSuffix);
        IntPtr headers = Marshal.StringToHGlobalUni(request.HttpHeaders);
        IntPtr licUrl = Marshal.StringToHGlobalUni(request.Drm?.LicenseServerUri);
        IntPtr pssh = IntPtr.Zero;
        int psshLen = request.Pssh.Length;
        if (psshLen > 0) { pssh = Marshal.AllocHGlobal(psshLen); Marshal.Copy(request.Pssh.ToArray(), 0, pssh, psshLen); }

        try
        {
            int mode = string.Equals(request.Mode, "clear", StringComparison.OrdinalIgnoreCase) ? 1 : _defaultNativeMode;
            var desc = new FgOpenDescNative
            {
                StructSize = (uint)sizeof(FgOpenDescNative),
                Mode = mode,
                InitUrl = initUrl,
                SegmentBaseUrl = segBase,
                SegmentPrefix = segPrefix,
                SegmentSuffix = segSuffix,
                StartNumber = request.StartNumber,
                SegmentCount = request.SegmentCount,
                Pssh = pssh,
                PsshLen = psshLen,
                HttpHeaders = headers,
                LicenseServerUrl = licUrl,
            };

            var callback = (IntPtr)(delegate* unmanaged[Stdcall]<
                IntPtr, byte*, int, char*, delegate* unmanaged[Stdcall]<IntPtr, byte*, int, void>, IntPtr, int>)&LicenseThunk;
            IntPtr ctx = GCHandle.ToIntPtr(self);

            _runHr = Native.FgPlayReadyRunEx(_dataRoot, ref desc, callback, ctx);
        }
        catch (Exception e)
        {
            _runError = e.Message;
            _runHr = unchecked((int)0x80004005);
        }
        finally
        {
            // The native call has fully returned here, so it can no longer invoke the thunk — safe to free the handle.
            if (self.IsAllocated) self.Free();
            if (initUrl != IntPtr.Zero) Marshal.FreeHGlobal(initUrl);
            if (segBase != IntPtr.Zero) Marshal.FreeHGlobal(segBase);
            if (segPrefix != IntPtr.Zero) Marshal.FreeHGlobal(segPrefix);
            if (segSuffix != IntPtr.Zero) Marshal.FreeHGlobal(segSuffix);
            if (headers != IntPtr.Zero) Marshal.FreeHGlobal(headers);
            if (licUrl != IntPtr.Zero) Marshal.FreeHGlobal(licUrl);
            if (pssh != IntPtr.Zero) Marshal.FreeHGlobal(pssh);
            _runCompleted = true;
        }
    }

    /// <summary>The native→managed license bridge (spec §9.2). Invoked by the CDM on an MF thread with the challenge; runs
    /// the app relay (bounded) and hands the license back through <paramref name="deliver"/>. Returns 0 on success, a
    /// negative HRESULT-shaped code on failure (the native side then leaves the key unusable → a surfaced DRM error). Never
    /// writes signals (background thread) — a relay failure is stashed in <see cref="_relayError"/> and mirrored by the pump.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int LicenseThunk(IntPtr ctx, byte* challenge, int challengeLen, char* keyIdHex,
        delegate* unmanaged[Stdcall]<IntPtr, byte*, int, void> deliver, IntPtr deliverCtx)
    {
        try
        {
            if (GCHandle.FromIntPtr(ctx).Target is not DesktopProtectedVideoPlayer self || self._bridge is not { } bridge)
                return unchecked((int)0x80004005);

            // COPY the challenge out of native memory — the relay may run past our stack frame; native frees it after we return.
            var copy = new byte[challengeLen < 0 ? 0 : challengeLen];
            if (copy.Length > 0) new ReadOnlySpan<byte>(challenge, copy.Length).CopyTo(copy);
            string? keyId = keyIdHex != null ? new string(keyIdHex) : null;

            LicenseOutcome outcome = bridge.Resolve(copy, keyId);
            if (!outcome.Success)
            {
                self._relayError = outcome.Error;
                return unchecked((int)0x8004110E);   // DRM_E_CH_BAD_KEY-shaped: "no usable license"
            }

            byte[] license = outcome.License!;
            fixed (byte* p = license) deliver(deliverCtx, p, license.Length);
            return 0;
        }
        catch
        {
            return unchecked((int)0x80004005);
        }
    }

    public void Play() => Native.FgPlayReadyPlay();
    public void Pause() => Native.FgPlayReadyPause();
    public void Seek(long positionMs) => Native.FgPlayReadySeek(positionMs);
    public void SetVolume(float volume) => Native.FgPlayReadySetVolume(volume);
    public void SetRate(float rate) { /* rate control follows after the desktop playback proof */ }
    public void Stop() => Native.FgPlayReadyStop();

    public void Pump(in VideoBinding binding)
    {
        // Mirror background completion on the UI thread; signals are never written by the native worker.
        int runHr = _runHr;
        if (_runCompleted && runHr < 0 && _error.Peek() is null)
        {
            _error.Value = _runError is { Length: > 0 }
                ? "Could not start the desktop PlayReady backend: " + _runError
                : $"Desktop PlayReady backend failed (0x{unchecked((uint)runHr):X8}). See {_dataRoot}\\desktop-playready.log";
            _state.Value = ProtectedVideoState.Error;
        }

        // Mirror a background-recorded relay/license error (typed) into the error signal + state.
        if (_relayError is { } relayErr && _error.Peek() is null)
        {
            _error.Value = relayErr.Message;
            _state.Value = ProtectedVideoState.Error;
        }

        if (Native.FgPlayReadyGetSnapshot(out var s) < 0) return;

        var state = s.State switch
        {
            1 => ProtectedVideoState.Loading,
            2 => ProtectedVideoState.Playing,
            3 => ProtectedVideoState.Paused,
            4 => ProtectedVideoState.Stopped,
            5 => ProtectedVideoState.Error,
            _ => ProtectedVideoState.Idle,
        };
        if (_state.Peek() != state) _state.Value = state;
        if (_positionMs.Peek() != s.PositionMs) _positionMs.Value = s.PositionMs;
        if (_durationMs.Peek() != s.DurationMs) _durationMs.Value = s.DurationMs;
        var size = new Size2(s.Width, s.Height);
        if (s.Width > 0 && !_naturalSize.Peek().Equals(size)) _naturalSize.Value = size;
        if (s.ErrorHr < 0 && _error.Peek() is null)
            _error.Value = $"Desktop PlayReady error 0x{unchecked((uint)s.ErrorHr):X8}.";

        // The backend DLL lives in THIS process, so this is the original handle. No OpenProcess/DuplicateHandle/IPC.
        if (s.Handle != 0 && s.Handle != _boundHandle)
        {
            binding.Bind((nuint)s.Handle);
            _boundHandle = s.Handle;
        }
    }

    /// <summary>The typed error recorded by the license relay (if any) — richer than the string error signal.</summary>
    public MediaError? LastRelayError => _relayError;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Native.FgPlayReadyStop(); } catch { }
        _thread?.Join(3_000);
        _thread = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSnapshot
    {
        public int State;
        public int ErrorHr;
        public ulong Handle;
        public int Width;
        public int Height;
        public long PositionMs;
        public long DurationMs;
    }

    /// <summary>Blittable mirror of the native <c>FgPlayReadyOpenDesc</c> (all pointer fields are hand-marshaled to
    /// HGlobal so the whole struct stays blittable for <c>LibraryImport</c>). Layout must match the native struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FgOpenDescNative
    {
        public uint StructSize;
        public int Mode;
        public IntPtr InitUrl;
        public IntPtr SegmentBaseUrl;
        public IntPtr SegmentPrefix;
        public IntPtr SegmentSuffix;
        public int StartNumber;
        public int SegmentCount;
        public IntPtr Pssh;
        public int PsshLen;
        public IntPtr HttpHeaders;
        public IntPtr LicenseServerUrl;
    }

    private static partial class Native
    {
        [LibraryImport(NativeLibraryName, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int FgPlayReadyRun(string baseDir, int mode);
        [LibraryImport(NativeLibraryName, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int FgPlayReadyRunEx(string baseDir, ref FgOpenDescNative desc, IntPtr licenseCallback, IntPtr licenseCtx);
        [LibraryImport(NativeLibraryName)] internal static partial int FgPlayReadyGetSnapshot(out NativeSnapshot value);
        [LibraryImport(NativeLibraryName)] internal static partial void FgPlayReadyPlay();
        [LibraryImport(NativeLibraryName)] internal static partial void FgPlayReadyPause();
        [LibraryImport(NativeLibraryName)] internal static partial void FgPlayReadyStop();
        [LibraryImport(NativeLibraryName)] internal static partial void FgPlayReadySeek(long positionMs);
        [LibraryImport(NativeLibraryName)] internal static partial void FgPlayReadySetVolume(double volume);
    }
}
