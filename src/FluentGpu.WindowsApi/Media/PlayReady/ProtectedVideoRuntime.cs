using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The PROCESS-LIFETIME PlayReady runtime: Media Foundation, the D3D11 video device + DXGI manager, ONE
/// <c>IMFMediaEngine</c> in windowless swap-chain mode, ONE CDM with its PMP host, the MTA runtime thread — and the
/// KID-keyed license cache in front of them. It is the managed half of <c>FgPrRuntime</c>
/// (<c>ops/tools/playready-native/FgPlayReady.h</c>).
/// <para><b>Why it exists.</b> The previous shape built and tore ALL of that down around every single source: a
/// song→video switch paid MFStartup, D3D11CreateDevice, CDM + <c>mfpmp.exe</c> bring-up and a fresh license challenge
/// before the first byte of video was even requested, and a process-global latch answered ERROR_BUSY while it ran.
/// Here the expensive things are created once, on first use, and survive every switch; a switch is one
/// <see cref="ProtectedVideoSession"/> attach (a <c>SetSource</c>) on an engine that is already alive with a license
/// that is already usable.</para>
/// <para><b>Lifetime.</b> Reference-counted by live sessions. When the last one goes away the native runtime is kept
/// warm for <see cref="WarmIdleDisposeMs"/> and then destroyed — the same policy, and for the same memory reason, as
/// the clear path's warm-engine lease: the D3D11 device, the media engine, its swap chain and the CDM are together
/// worth tens to well over a hundred megabytes, and nothing in a census can name them.</para>
/// <para><b>Threading.</b> Every member is callable from any thread. Native state arrives through the event callback
/// (on the runtime thread or an MF thread) and is dispatched to the owning session, which only flips POD fields and
/// asks for ONE coalesced UI pump — no signal is ever written off the pump. No native call is ever made while the
/// managed gate is held.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed unsafe class ProtectedVideoRuntime : IDisposable
{
    /// <summary>How long the native runtime stays warm after its last session detaches. Matches the clear path's
    /// warm-engine idle budget; the memory argument is the same one.</summary>
    public const int WarmIdleDisposeMs = 30_000;

    /// <summary>
    /// Where every <c>[video]</c> / <c>[video.native]</c> line goes. ALWAYS ON and never behind an environment switch
    /// (a diagnostic only the person who knows the variable name can turn on is not one) — the host sets this once at
    /// startup so the native lifecycle interleaves with the app's own lines in ONE file. Unset, lines go to
    /// <see cref="Console.Error"/>. Lines are LIFECYCLE only (bring-up, license, attach, first frame, seek landed,
    /// error): the high-rate events (bytes, position, buffered, keyframes) never allocate a string and never reach here.
    /// </summary>
    public static Action<string>? LogSink;

    private static ProtectedVideoRuntime? s_shared;
    private static readonly object s_sharedGate = new();

    private readonly IPrRuntimeNative _native;
    private readonly IPrSessionNative _sessionNative;
    private readonly string _storePath;
    private readonly int _idleMs;
    private readonly object _gate = new();
    // Serialises native bring-up against native teardown. FgPrRuntimeDestroy blocks (it joins the runtime thread) and runs
    // OUTSIDE _gate; a create that raced it would get ERROR_BUSY from the DLL and fail the video that asked. Lock order:
    // _lifecycleGate, then _gate. Only the create and the destroy take it — every other call stays on _gate alone.
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, LicenseEntry> _licenses = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, ProtectedVideoSession> _sessions = new();
    private readonly Dictionary<ulong, BufferedWait> _bufferedWaits = new();

    private GCHandle _self;
    private ulong _rt;
    private int _refs;
    private Timer? _idle;
    private bool _disposed;
    private long _createdTimestamp;

    /// <summary>One cached KID. The handle is the native CDM key session; the relay is kept so a re-acquisition after
    /// an expiry does not need the caller to supply it again.</summary>
    private struct LicenseEntry
    {
        public ulong Handle;
        public LicenseCacheState State;
        public long AcquiredMs, LastUsedMs, ExpiresAtMs;
        public int InUse;                 // attached sessions decoding with this key — never evicted while > 0
        public string? FailureMessage;
        public Func<LicenseRequest, ValueTask<LicenseResponse>>? Relay;
        public DrmSystem System;
    }

    /// <summary>A runtime over <paramref name="native"/> — the real DLL in production (<see cref="Shared"/>), a fake in
    /// the engine's own tests. <paramref name="idleMs"/> is the warm-idle window (tests shorten it).
    /// <paramref name="sessionNative"/> is what every <see cref="ProtectedVideoSession"/> on this runtime calls for its
    /// own verbs (the real DLL when null).</summary>
    internal ProtectedVideoRuntime(IPrRuntimeNative native, string storePath, int idleMs = WarmIdleDisposeMs,
                                   IPrSessionNative? sessionNative = null)
    {
        _native = native;
        _sessionNative = sessionNative ?? PrSessionNative.Instance;
        _storePath = storePath;
        _idleMs = idleMs > 0 ? idleMs : WarmIdleDisposeMs;
    }

    /// <summary>The session half of the native seam — what <see cref="ProtectedVideoSession"/> calls.</summary>
    internal IPrSessionNative SessionNative => _sessionNative;

    /// <summary>The process runtime, created on first use. The native bring-up happens on the first session or license
    /// request, not here.</summary>
    public static ProtectedVideoRuntime Shared
    {
        get
        {
            lock (s_sharedGate)
                return s_shared ??= new ProtectedVideoRuntime(PrRuntimeNative.Instance, DefaultStorePath());
        }
    }

    /// <summary>The CDM's <c>MF_CONTENTDECRYPTIONMODULE_STOREPATH</c> directory. A per-user path under LocalAppData —
    /// no environment override: a store the app cannot predict is a store it cannot clean up.</summary>
    private static string DefaultStorePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentGpu", "PlayReady");

    /// <summary>Whether <c>FluentGpu.PlayReady.Native.dll</c> (and its MF/PlayReady import chain) can be loaded. A missing
    /// component degrades to a typed <see cref="MediaErrorCategory.Drm"/> error rather than a
    /// <see cref="DllNotFoundException"/> out of the frame loop.</summary>
    public static bool IsAvailable => PrRuntimeNative.Instance.IsAvailable;

    private static int s_warmupStarted;

    /// <summary>Idempotent, non-blocking preload of the native component, so the first protected open does not pay an
    /// implicit <c>LoadLibrary</c> of the DLL plus its whole MF/PlayReady dependency chain on the caller's thread. The
    /// handle is DELIBERATELY kept loaded (freeing it drops the loader refcount and forfeits the warmup).</summary>
    public static void Warmup()
    {
        if (Interlocked.Exchange(ref s_warmupStarted, 1) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static _ =>
        {
            try
            {
                NativeLibrary.TryLoad(PrNative.LibraryName, typeof(ProtectedVideoRuntime).Assembly,
                    DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.AssemblyDirectory, out nint _);
            }
            catch { /* best-effort: absence is handled at the open */ }
        }, null);
    }

    /// <summary>The native runtime handle, 0 when bring-up has not run or failed.</summary>
    internal ulong Handle => Volatile.Read(ref _rt);

    /// <summary>Whether the native runtime is up right now (false before first use and after the idle teardown).</summary>
    public bool IsRunning => Handle != 0;

    /// <summary>The bring-up failure, if the runtime could not be created (surfaced as a typed DRM error by the
    /// session that asked for it).</summary>
    internal string? StartupError { get; private set; }

    /// <summary>Milliseconds since the runtime came up — the timeline every <c>[video.native]</c> line carries.</summary>
    internal long UptimeMs
    {
        get
        {
            long t = Volatile.Read(ref _createdTimestamp);
            return t == 0 ? 0 : (long)Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
    }

    // ── bring-up / teardown ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Create the native runtime if it is not already up, and take one reference on it. Returns false when
    /// the native component is missing or bring-up failed (<see cref="StartupError"/> says why).</summary>
    internal bool Acquire()
    {
        // Fast path: the runtime is up — take a reference and cancel a pending idle teardown.
        lock (_gate)
        {
            if (_disposed) return false;
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_rt != 0) { _refs++; return true; }
        }
        // Slow path: bring it up, after any teardown that is still joining the old runtime thread has finished.
        lock (_lifecycleGate)
        lock (_gate)
        {
            if (_disposed) return false;
            if (_rt != 0) { _refs++; return true; }   // another caller brought it up while this one waited

            if (!_native.IsAvailable)
            {
                StartupError = "The protected-video component (" + PrNative.LibraryName +
                               ") is missing, or is a stale build that does not export the runtime ABI.";
                return false;
            }
            try { Directory.CreateDirectory(_storePath); } catch { /* the CDM reports a store failure itself */ }
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this, GCHandleType.Normal);

            // FgPrRuntimeCreate is non-blocking by contract (it returns once the runtime thread is up; bring-up completion
            // is an event), so creating under the gate cannot stall a caller for the CDM's bring-up time.
            long t0 = Stopwatch.GetTimestamp();
            int hr;
            ulong rt;
            try { hr = _native.RuntimeCreate(_storePath, GCHandle.ToIntPtr(_self), out rt); }
            catch (Exception e)
            {
                StartupError = "The protected-video runtime could not start: " + e.Message;
                return false;
            }
            if (hr < 0 || rt == 0)
            {
                StartupError = $"The protected-video runtime could not start (0x{unchecked((uint)hr):X8}).";
                Log($"runtime.create FAILED hr=0x{unchecked((uint)hr):X8}");
                return false;
            }
            Volatile.Write(ref _createdTimestamp, t0);
            Volatile.Write(ref _rt, rt);
            _refs++;
            StartupError = null;
            Log($"runtime.create ok ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1}");
            return true;
        }
    }

    /// <summary>Give one reference back. The last one starts the warm-idle timer; a new <see cref="Acquire"/> inside the
    /// window cancels it and reuses everything.</summary>
    internal void Release()
    {
        lock (_gate)
        {
            if (_refs > 0) _refs--;
            if (_refs != 0 || _rt == 0 || _disposed) return;
            _idle ??= new Timer(static s => ((ProtectedVideoRuntime)s!).IdleElapsed(), this, Timeout.Infinite, Timeout.Infinite);
            _idle.Change(_idleMs, Timeout.Infinite);
        }
    }

    /// <summary>How many live references hold the runtime (sessions; a manifest-time license takes and returns one).</summary>
    internal int References { get { lock (_gate) return _refs; } }

    private void IdleElapsed()
    {
        lock (_lifecycleGate)
        {
            ulong rt;
            ulong[] licenses;
            lock (_gate)
            {
                if (_refs != 0 || _rt == 0) return;
                Log($"runtime.destroy idleMs={_idleMs}");
                rt = DetachLocked(out licenses);
            }
            DestroyNative(rt, licenses);   // blocking (bounded native join) — outside _gate, inside the lifecycle lock
        }
    }

    /// <summary>Clear the managed tables and hand back what the native side must release — outside the gate.</summary>
    private ulong DetachLocked(out ulong[] licenses)
    {
        var handles = new List<ulong>(_licenses.Count);
        foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
            if (kv.Value.Handle != 0) handles.Add(kv.Value.Handle);
        licenses = handles.ToArray();
        _licenses.Clear();
        _sessions.Clear();
        foreach (KeyValuePair<ulong, BufferedWait> kv in _bufferedWaits) kv.Value.Finish(false);
        _bufferedWaits.Clear();
        ulong rt = _rt;
        Volatile.Write(ref _rt, 0);
        Volatile.Write(ref _createdTimestamp, 0);
        return rt;
    }

    private void DestroyNative(ulong rt, ulong[] licenses)
    {
        if (rt == 0) return;
        for (int i = 0; i < licenses.Length; i++) { try { _native.LicenseRelease(rt, licenses[i]); } catch { } }
        try { _native.RuntimeDestroy(rt); } catch { }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            ulong rt;
            ulong[] licenses;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _idle?.Dispose();
                _idle = null;
                _refs = 0;
                rt = DetachLocked(out licenses);
            }
            DestroyNative(rt, licenses);
            lock (_gate)
                if (_self.IsAllocated) _self.Free();
        }
    }

    // ── the license cache ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Make sure a license for <paramref name="kid"/> is usable or on its way. Returns IMMEDIATELY; the outcome
    /// arrives as a native event. This is PlayReady's "proactive acquisition" and it is called at MANIFEST time, not
    /// at attach time — by the moment the user asks for the video the key is already usable and the switch costs no
    /// round trip at all. It brings the runtime up if it is not (the CDM owns the key sessions).
    /// <para>A KID that is already usable returns <see cref="LicenseCacheState.Usable"/> and does nothing; one that is
    /// already pending returns <see cref="LicenseCacheState.Pending"/> and does NOT issue a second challenge (two
    /// challenges for one KID create a second content binding whose ITA proxy the CDM rejects).</para>
    /// </summary>
    public LicenseCacheState EnsureLicense(ReadOnlySpan<byte> pssh, string kid,
        Func<LicenseRequest, ValueTask<LicenseResponse>>? relay, DrmSystem system = DrmSystem.PlayReady)
    {
        if (string.IsNullOrEmpty(kid)) return LicenseCacheState.Failed;
        if (Handle == 0)
        {
            // Manifest-time acquisition brings the runtime up. The reference is handed straight back: the warm-idle window
            // keeps everything alive until the session that follows takes its own.
            if (!Acquire()) return LicenseCacheState.Failed;
            Release();
        }

        ulong rt;
        ulong deadHandle = 0;
        ulong evictedHandle = 0;
        lock (_gate)
        {
            rt = _rt;
            if (rt == 0) return LicenseCacheState.Failed;
            long now = Environment.TickCount64;

            if (_licenses.TryGetValue(kid, out LicenseEntry existing))
            {
                LicenseCacheAction what = LicenseCachePolicy.Decide(existing.State, existing.ExpiresAtMs, now);
                if (what is LicenseCacheAction.Reuse or LicenseCacheAction.Await)
                {
                    existing.LastUsedMs = now;
                    if (relay is not null) existing.Relay = relay;
                    _licenses[kid] = existing;
                    Log($"license.cache kid={kid} state={existing.State} action={what} cached=true");
                    return existing.State;
                }
                deadHandle = existing.Handle;   // expired or failed: its key session is closed below, outside the gate
                _licenses.Remove(kid);
            }

            evictedHandle = EvictIfFullLocked(kid);

            // Published BEFORE the acquire, with Handle = 0: the CDM raises KeyMessage on its own thread and the relay
            // entry finds the relay by KID. A completion that races the handle assignment below is matched by KID (the
            // native side puts the KID in the license events' text) and adopts the handle then.
            _licenses[kid] = new LicenseEntry
            {
                State = LicenseCacheState.Pending,
                AcquiredMs = now,
                LastUsedMs = now,
                Relay = relay,
                System = system,
            };
        }

        if (deadHandle != 0) { try { _native.LicenseRelease(rt, deadHandle); } catch { } }
        if (evictedHandle != 0) { try { _native.LicenseRelease(rt, evictedHandle); } catch { } }

        ulong lic = 0;
        int hr;
        try { hr = _native.LicenseAcquire(rt, pssh, kid, GCHandle.ToIntPtr(_self), out lic); }
        catch (Exception e)
        {
            hr = PrNative.EFail;
            RecordRelayFailure(kid, "The PlayReady key session could not be opened: " + e.Message);
        }

        ulong orphan = 0;
        LicenseCacheState result;
        lock (_gate)
        {
            if (!_licenses.TryGetValue(kid, out LicenseEntry entry))
            {
                orphan = lic;   // released or evicted while the call was in flight — the key session is nobody's
                result = LicenseCacheState.None;
            }
            else if (hr < 0 || lic == 0)
            {
                entry.State = LicenseCacheState.Failed;
                entry.FailureMessage ??= $"The PlayReady key session could not be opened (0x{unchecked((uint)hr):X8}).";
                _licenses[kid] = entry;
                Log($"license.fail kid={kid} hr=0x{unchecked((uint)hr):X8} ms={Environment.TickCount64 - entry.AcquiredMs}");
                result = LicenseCacheState.Failed;
            }
            else
            {
                if (entry.Handle == 0) entry.Handle = lic;   // a racing completion may already have adopted it
                _licenses[kid] = entry;
                Log($"license.acquire kid={kid} lic={lic} cached=false");
                result = entry.State;
            }
        }
        if (orphan != 0) { try { _native.LicenseRelease(rt, orphan); } catch { } }
        return result;
    }

    /// <summary>Evict one row when the cache is full; returns the native handle to release (outside the gate), or 0.</summary>
    private ulong EvictIfFullLocked(string keepKid)
    {
        if (LicenseCachePolicy.HasRoom(_licenses.Count)) return 0;
        var rows = new LicenseCacheEntry[_licenses.Count];
        int i = 0;
        foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
            rows[i++] = new LicenseCacheEntry(kv.Key, kv.Value.State, kv.Value.LastUsedMs, kv.Value.ExpiresAtMs, kv.Value.InUse > 0);
        int victim = LicenseCachePolicy.ChooseEviction(rows, keepKid);
        if (victim < 0) return 0;
        string kid = rows[victim].Kid;
        ulong handle = _licenses.TryGetValue(kid, out LicenseEntry dead) ? dead.Handle : 0;
        _licenses.Remove(kid);
        Log($"license.evict kid={kid}");
        return handle;
    }

    /// <summary>The cached native license handle for <paramref name="kid"/>, or 0. A pending handle is returned too:
    /// an attach on a pending license is legal and the engine's own key-needed path waits for the key, which is
    /// strictly faster than making the OPEN wait for the license.</summary>
    internal ulong LicenseHandleFor(string? kid)
    {
        if (string.IsNullOrEmpty(kid)) return 0;
        lock (_gate)
            return _licenses.TryGetValue(kid, out LicenseEntry e) ? e.Handle : 0;
    }

    /// <summary>Where <paramref name="kid"/>'s acquisition stands (never a native call — the cache is authoritative).</summary>
    public LicenseCacheState LicenseStateFor(string? kid)
    {
        if (string.IsNullOrEmpty(kid)) return LicenseCacheState.None;
        lock (_gate)
            return _licenses.TryGetValue(kid, out LicenseEntry e) ? e.State : LicenseCacheState.None;
    }

    /// <summary>The failure text recorded for <paramref name="kid"/>, or null. Richer than an HRESULT: it is what the
    /// app's own relay said went wrong.</summary>
    internal string? LicenseFailureFor(string? kid)
    {
        if (string.IsNullOrEmpty(kid)) return null;
        lock (_gate)
            return _licenses.TryGetValue(kid, out LicenseEntry e) ? e.FailureMessage : null;
    }

    /// <summary>How many KIDs the cache holds right now.</summary>
    internal int LicenseCount { get { lock (_gate) return _licenses.Count; } }

    /// <summary>Mark/unmark a KID as decoding right now, so the LRU never closes a key session under a live decoder.</summary>
    internal void PinLicense(string? kid, bool pinned)
    {
        if (string.IsNullOrEmpty(kid)) return;
        lock (_gate)
        {
            if (!_licenses.TryGetValue(kid, out LicenseEntry e)) return;
            e.InUse = Math.Max(0, e.InUse + (pinned ? 1 : -1));
            e.LastUsedMs = Environment.TickCount64;
            _licenses[kid] = e;
        }
    }

    // ── session registry + waits ───────────────────────────────────────────────────────────────────────────────────

    internal void RegisterSession(ulong handle, ProtectedVideoSession session)
    {
        if (handle == 0) return;
        lock (_gate) _sessions[handle] = session;
    }

    internal void UnregisterSession(ulong handle)
    {
        if (handle == 0) return;
        BufferedWait? waiter;
        lock (_gate)
        {
            _sessions.Remove(handle);
            _bufferedWaits.Remove(handle, out waiter);
        }
        waiter?.Finish(false);
    }

    private static readonly Task<bool> s_nothingToWaitFor = Task.FromResult(false);

    /// <summary>
    /// Complete TRUE when the session's store reports buffered media (the <c>Buffered</c> event with media ahead), and
    /// FALSE when it cannot: the token cancels, the session reports an error or goes away, a newer wait for the same
    /// session supersedes this one, or the runtime is torn down. Never faults, never throws. One waiter per session.
    /// <para>Register the wait BEFORE issuing the native call it waits for: a store that already holds the window
    /// answers from the feeder thread at once, and a waiter registered after that answer would never see it.</para>
    /// <para>A cancelled waiter is removed from the table at the moment it cancels (not left behind until something
    /// replaces it), and its token registration is released when it completes by any route.</para>
    /// </summary>
    internal Task<bool> WaitBufferedAsync(ulong handle, CancellationToken ct)
    {
        if (handle == 0) return s_nothingToWaitFor;
        var wait = new BufferedWait(this, handle);
        BufferedWait? superseded;
        lock (_gate)
        {
            _bufferedWaits.Remove(handle, out superseded);
            _bufferedWaits[handle] = wait;
        }
        superseded?.Finish(false);
        if (ct.CanBeCanceled)
            wait.Registration = ct.UnsafeRegister(static s => ((BufferedWait)s!).Cancel(), wait);
        return wait.Task;
    }

    /// <summary>Complete <paramref name="handle"/>'s pending buffered wait with <paramref name="buffered"/>, if any.</summary>
    internal void CompleteBufferedWait(ulong handle, bool buffered)
    {
        BufferedWait? waiter;
        lock (_gate) _bufferedWaits.Remove(handle, out waiter);
        waiter?.Finish(buffered);
    }

    /// <summary>How many buffered waits are registered (the engine's tests check a cancelled one does not linger).</summary>
    internal int PendingBufferedWaits { get { lock (_gate) return _bufferedWaits.Count; } }

    /// <summary>One session's pending buffered wait.</summary>
    private sealed class BufferedWait : TaskCompletionSource<bool>
    {
        private readonly ProtectedVideoRuntime _owner;
        private readonly ulong _session;
        internal CancellationTokenRegistration Registration;

        internal BufferedWait(ProtectedVideoRuntime owner, ulong session)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            _owner = owner;
            _session = session;
        }

        internal void Finish(bool result)
        {
            if (!TrySetResult(result)) return;
            Registration.Unregister();   // never blocks, even when called from inside the cancellation callback
        }

        internal void Cancel()
        {
            lock (_owner._gate)
                if (_owner._bufferedWaits.TryGetValue(_session, out BufferedWait? current) && ReferenceEquals(current, this))
                    _owner._bufferedWaits.Remove(_session);
            TrySetResult(false);
        }
    }

    // ── the native event sink ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE native→managed event entry point (<c>FgPrEventCallback</c>).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static void EventThunk(nint ctx, ulong session, int ev, long a, long b, char* text)
    {
        try
        {
            if (ctx == 0 || GCHandle.FromIntPtr(ctx).Target is not ProtectedVideoRuntime self) return;
            // Only the license and log events need the text as a string; every other event passes the pointer through.
            string? line = text != null && ev is PrNative.EvLicenseUsable or PrNative.EvLicenseFailed
                                                  or PrNative.EvLicenseExpired or PrNative.EvLog
                ? new string(text) : null;
            self.OnNativeEvent(session, ev, a, b, line);
        }
        catch { /* a native callback may never throw across the ABI */ }
    }

    /// <summary>Dispatch one native event: update the license cache, complete a buffered wait, hand the event to the
    /// owning session. Runs on the runtime or an MF thread and does almost nothing — the session flips POD fields and
    /// asks for ONE coalesced UI pump. Internal so the engine's tests can drive the runtime without the DLL.</summary>
    internal void OnNativeEvent(ulong session, int ev, long a, long b, string? text)
    {
        switch (ev)
        {
            case PrNative.EvLicenseUsable:
            case PrNative.EvLicenseFailed:
            case PrNative.EvLicenseExpired:
                OnLicenseEvent(session, ev, a, b, text);
                return;

            case PrNative.EvRuntimeReady:
                Log($"runtime.ready ms={a}");
                return;

            case PrNative.EvRuntimeFailed:
                lock (_gate) StartupError = $"The protected-video runtime failed (0x{unchecked((uint)a):X8}).";
                Log($"runtime.failed hr=0x{unchecked((uint)a):X8}");
                break;

            case PrNative.EvBuffered:
                if (a > 0) CompleteBufferedWait(session, buffered: true);
                break;

            case PrNative.EvError:
            {
                // A session that failed (an init segment that never parsed, a network end) will never report media: its
                // prepare must end now, not at a token that may never cancel. The session records the error FIRST, so
                // whatever continues from that wait already sees it.
                ProtectedVideoSession? failed;
                lock (_gate) _sessions.TryGetValue(session, out failed);
                failed?.OnNativeEvent(ev, a, b);
                CompleteBufferedWait(session, buffered: false);
                return;
            }

            case PrNative.EvLog:
                if (text is not null) Log($"s={session} {text}");
                return;
        }

        ProtectedVideoSession? target;
        lock (_gate) _sessions.TryGetValue(session, out target);
        target?.OnNativeEvent(ev, a, b);
    }

    private void OnLicenseEvent(ulong licenseHandle, int ev, long a, long b, string? kidText)
    {
        // The native side raises every license event with its license handle. A 0 would match — by handle — every row
        // that is still waiting for its handle, and adopt itself as that handle: never a license event.
        if (licenseHandle == 0) return;
        string? kid = null;
        long sinceMs = 0;
        lock (_gate)
        {
            foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
                if (kv.Value.Handle == licenseHandle) { kid = kv.Key; break; }

            // The acquire call may not have returned yet (the handle is recorded after it does): match by the KID the
            // native side carries in the event text, and adopt the handle — but only onto a row still waiting for one.
            if (kid is null && !string.IsNullOrEmpty(kidText) && _licenses.TryGetValue(kidText, out LicenseEntry byKid)
                && byKid.Handle == 0 && byKid.State == LicenseCacheState.Pending)
            {
                kid = kidText;
                byKid.Handle = licenseHandle;
                _licenses[kid] = byKid;
            }
            if (kid is null) return;   // a completion for an evicted/released row: dropped (LicenseCachePolicy)

            LicenseEntry e = _licenses[kid];
            sinceMs = Environment.TickCount64 - e.AcquiredMs;
            if (ev == PrNative.EvLicenseExpired)
            {
                if (!LicenseCachePolicy.AcceptExpiry(rowExists: true, e.State, e.Handle, licenseHandle)) return;
                e.State = LicenseCacheState.Expired;
            }
            else if (LicenseCachePolicy.AcceptCompletion(rowExists: true, e.State, e.Handle, licenseHandle))
            {
                if (ev == PrNative.EvLicenseUsable)
                {
                    e.State = LicenseCacheState.Usable;
                    e.ExpiresAtMs = b > 0 ? Environment.TickCount64 + b : 0;
                }
                else
                {
                    e.State = LicenseCacheState.Failed;
                    e.FailureMessage ??= $"The PlayReady license was not granted (0x{unchecked((uint)a):X8}).";
                }
            }
            else
            {
                return;   // not Pending any more (a duplicate completion) — never re-applied
            }
            _licenses[kid] = e;
        }

        Log(ev == PrNative.EvLicenseUsable
            ? $"license.ok kid={kid} ms={a} cached=false" + (b > 0 ? $" expiresInMs={b}" : string.Empty)
            : ev == PrNative.EvLicenseExpired
                ? $"license.expired kid={kid}"
                : $"license.fail kid={kid} hr=0x{unchecked((uint)a):X8} ms={sinceMs}");

        ProtectedVideoSession[] live;
        lock (_gate)
        {
            live = new ProtectedVideoSession[_sessions.Count];
            _sessions.Values.CopyTo(live, 0);
        }
        for (int i = 0; i < live.Length; i++) live[i].OnLicenseEvent(licenseHandle, ev, a);
    }

    // ── the license relay (native → managed → CDM), non-blocking on both sides ──────────────────────────────────────

    /// <summary>The CDM's challenge (<c>FgPrLicenseCallback</c>), arriving on the CDM's own thread.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static int LicenseThunk(nint ctx, ulong lic, byte* challenge, int challengeLen, char* keyIdHex,
        delegate* unmanaged[Stdcall]<nint, byte*, int, int, void> deliver, nint deliverCtx)
    {
        try
        {
            if (ctx == 0 || GCHandle.FromIntPtr(ctx).Target is not ProtectedVideoRuntime self) return PrNative.EFail;
            // COPY out of native memory: the relay outlives this stack frame and native frees the buffer on return. The ONE
            // allocation per license acquisition — per KID, never per open and never per frame.
            int len = challengeLen < 0 ? 0 : challengeLen;
            var copy = new byte[len];
            if (len > 0) new ReadOnlySpan<byte>(challenge, len).CopyTo(copy);
            string kid = keyIdHex != null ? new string(keyIdHex) : string.Empty;
            return self.OnChallenge(lic, copy, kid, new NativeDelivery((nint)deliver, deliverCtx));
        }
        catch { return PrNative.EFail; }
    }

    /// <summary>
    /// Run the app relay for one challenge and return AT ONCE — the native side never waits for a license again (the
    /// old <c>Task.Wait(30 s)</c> blocked a CDM thread for the whole round trip). The relay's answer reaches the CDM
    /// later through <paramref name="delivery"/>, exactly once: the license bytes, or a failure HRESULT with the relay's
    /// own message recorded on the KID's cache row. Returns 0 when a delivery is coming. Internal so the engine's tests
    /// drive the relay with a fake delivery.
    /// </summary>
    internal int OnChallenge(ulong lic, byte[] challenge, string kid, ILicenseDelivery delivery)
    {
        Func<LicenseRequest, ValueTask<LicenseResponse>>? relay;
        DrmSystem system;
        lock (_gate)
        {
            if (!_licenses.TryGetValue(kid, out LicenseEntry e)) return PrNative.EFail;
            relay = e.Relay;
            system = e.System;
        }
        if (relay is null)
        {
            RecordRelayFailure(kid, "No DRM license relay is configured for the protected source (call WithDrm).");
            return PrNative.EFail;
        }

        var call = new RelayCall(this, lic, kid, delivery);
        Log($"license.challenge kid={kid} bytes={challenge.Length}");
        try
        {
            relay(new LicenseRequest(system, challenge, kid.Length > 0 ? kid : null, default))
                .AsTask()
                .ContinueWith(static (t, s) => ((RelayCall)s!).Complete(t), call,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            call.Fail("The DRM license relay failed: " + ex.Message);
        }
        return 0;
    }

    /// <summary>One in-flight relay. Never touches a signal (it completes on a pool thread).</summary>
    private sealed class RelayCall
    {
        private readonly ProtectedVideoRuntime _owner;
        private readonly ulong _license;
        private readonly string _kid;
        private readonly ILicenseDelivery _delivery;
        private int _done;

        internal RelayCall(ProtectedVideoRuntime owner, ulong license, string kid, ILicenseDelivery delivery)
        { _owner = owner; _license = license; _kid = kid; _delivery = delivery; }

        internal void Complete(Task<LicenseResponse> task)
        {
            if (task.IsFaulted)
            {
                Exception e = task.Exception?.GetBaseException() ?? new InvalidOperationException("unknown");
                Fail("The DRM license relay failed: " + e.Message);
                return;
            }
            if (task.IsCanceled) { Fail("The DRM license relay was canceled."); return; }

            ReadOnlyMemory<byte> license = task.Result.License;
            if (license.IsEmpty) { Fail("The DRM license relay returned an empty license."); return; }
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { _delivery.Deliver(license.Span, 0); } catch { /* a destroyed runtime ignores a late delivery */ }
            _owner.Log($"license.delivered kid={_kid} lic={_license} bytes={license.Length}");
        }

        internal void Fail(string message)
        {
            _owner.RecordRelayFailure(_kid, message);
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { _delivery.Deliver(ReadOnlySpan<byte>.Empty, LicenseRelayFailedHr); } catch { }
        }
    }

    /// <summary>The HRESULT a failed relay delivers (DRM_E_CH_BAD_KEY-shaped: "no usable license").</summary>
    internal const int LicenseRelayFailedHr = unchecked((int)0x8004110E);

    private void RecordRelayFailure(string kid, string message)
    {
        lock (_gate)
        {
            if (_licenses.TryGetValue(kid, out LicenseEntry e))
            {
                e.FailureMessage = message;
                _licenses[kid] = e;
            }
        }
        Log($"license.relay.fail kid={kid} {message}");
    }

    /// <summary>The native <c>FgPrLicenseDeliver</c> behind <see cref="ILicenseDelivery"/>.</summary>
    private sealed class NativeDelivery : ILicenseDelivery
    {
        private readonly nint _fn;
        private readonly nint _ctx;

        internal NativeDelivery(nint fn, nint ctx) { _fn = fn; _ctx = ctx; }

        public void Deliver(ReadOnlySpan<byte> license, int hr)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, byte*, int, int, void>)_fn;
            if (fn == null) return;
            fixed (byte* p = license) { fn(_ctx, license.IsEmpty ? null : p, license.Length, hr); }
        }
    }

    // ── logging ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One always-on <c>[video.native]</c> line. Lifecycle only; never per frame, never per high-rate event.</summary>
    internal void Log(string line) => Write($"[video.native] t={UptimeMs}ms {line}");

    /// <summary>One always-on <c>[video]</c> session line — the timeline a switch and a seek are measured by:
    /// <c>prefetch.ok → attach → metadata → canplay → first.frame sinceAttachMs=…</c> and <c>seek.done … ms=…</c>.</summary>
    internal static void WriteVideoLine(string line) => Write("[video] " + line);

    private static void Write(string stamped)
    {
        Action<string>? sink = LogSink;
        try
        {
            if (sink is not null) sink(stamped);
            else Console.Error.WriteLine(stamped);
        }
        catch { /* diagnostics never disrupt playback */ }
    }
}

/// <summary>Where a license relay's answer goes: the native <c>FgPrLicenseDeliver</c> in production, a recorder in the
/// engine's tests. Called exactly once per challenge, from a pool thread.</summary>
internal interface ILicenseDelivery
{
    /// <summary>Hand <paramref name="license"/> (empty on failure) and <paramref name="hr"/> (0 on success) to the CDM.</summary>
    void Deliver(ReadOnlySpan<byte> license, int hr);
}

/// <summary>The four native calls the runtime itself makes — the real DLL in production (<see cref="PrRuntimeNative"/>),
/// a fake in the engine's tests, so the license cache, the relay and the warm-idle lifetime are testable with no CDM,
/// no GPU, no license server and no window. Sessions call the other half, <see cref="IPrSessionNative"/>.</summary>
internal interface IPrRuntimeNative
{
    /// <summary>Whether the native component can be loaded AND exports the whole runtime ABI (a stale build does not).</summary>
    bool IsAvailable { get; }
    /// <summary><c>FgPrRuntimeCreate</c> with the runtime's event thunk and <paramref name="ctx"/>.</summary>
    int RuntimeCreate(string storePath, nint ctx, out ulong runtime);
    /// <summary><c>FgPrRuntimeDestroy</c>.</summary>
    void RuntimeDestroy(ulong runtime);
    /// <summary><c>FgPrLicenseAcquire</c> with the runtime's relay thunk and <paramref name="ctx"/>.</summary>
    int LicenseAcquire(ulong runtime, ReadOnlySpan<byte> pssh, string kid, nint ctx, out ulong license);
    /// <summary><c>FgPrLicenseRelease</c>.</summary>
    void LicenseRelease(ulong runtime, ulong license);
}

/// <summary>The production <see cref="IPrRuntimeNative"/>: <c>FluentGpu.PlayReady.Native.dll</c>.</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed unsafe class PrRuntimeNative : IPrRuntimeNative
{
    internal static readonly PrRuntimeNative Instance = new();

    private int _available = -1;   // -1 unknown, 0 no, 1 yes — probed once

    /// <summary>Every export the managed side binds (<see cref="PrNative"/>). A DLL that loads but lacks one is a stale
    /// build of an older ABI: calling into it would be an <see cref="EntryPointNotFoundException"/> out of the first
    /// protected open, so it is reported as unavailable up front instead.</summary>
    internal static readonly string[] RequiredExports =
    [
        "FgPrRuntimeCreate", "FgPrRuntimeDestroy", "FgPrRuntimeUptimeMs",
        "FgPrLicenseAcquire", "FgPrLicenseState", "FgPrLicenseRelease",
        "FgPrSessionCreate", "FgPrSessionPrefetch", "FgPrSessionAttach", "FgPrSessionDetach", "FgPrSessionDestroy",
        "FgPrSessionPlay", "FgPrSessionPause", "FgPrSessionSeek", "FgPrSessionSetVolume", "FgPrSessionSetRate",
        "FgPrSessionSetStreamSize", "FgPrSessionSelectRepresentation", "FgPrSessionSnapshot",
        "FgPrSessionGetKeyframes", "FgPrSessionGetBuffered", "FgPrSessionGetInitProtection",
        "FgPrProbeFile",
    ];

    /// <summary>The first of <see cref="RequiredExports"/> that <paramref name="hasExport"/> says is missing, or null
    /// when the library exports them all. Pure (the decision behind <see cref="IsAvailable"/>).</summary>
    internal static string? FirstMissingExport(Func<string, bool> hasExport)
    {
        foreach (string name in RequiredExports)
            if (!hasExport(name)) return name;
        return null;
    }

    public bool IsAvailable
    {
        get
        {
            int v = Volatile.Read(ref _available);
            if (v >= 0) return v == 1;
            bool ok;
            try
            {
                ok = NativeLibrary.TryLoad(PrNative.LibraryName, typeof(PrRuntimeNative).Assembly,
                    DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.AssemblyDirectory, out nint h);
                if (ok)
                {
                    string? missing = FirstMissingExport(name => NativeLibrary.TryGetExport(h, name, out _));
                    NativeLibrary.Free(h);
                    if (missing is not null)
                    {
                        ok = false;
                        ProtectedVideoRuntime.WriteVideoLine(
                            $"native.unavailable dll={PrNative.LibraryName} missingExport={missing} (a stale build of an older ABI)");
                    }
                }
            }
            catch { ok = false; }
            Volatile.Write(ref _available, ok ? 1 : 0);
            return ok;
        }
    }

    public int RuntimeCreate(string storePath, nint ctx, out ulong runtime)
    {
        ulong rt = 0;
        int hr = PrNative.FgPrRuntimeCreate(storePath, &ProtectedVideoRuntime.EventThunk, ctx, &rt);
        runtime = rt;
        return hr;
    }

    public void RuntimeDestroy(ulong runtime) => PrNative.FgPrRuntimeDestroy(runtime);

    public int LicenseAcquire(ulong runtime, ReadOnlySpan<byte> pssh, string kid, nint ctx, out ulong license)
    {
        ulong lic = 0;
        int hr;
        fixed (byte* p = pssh)
            hr = PrNative.FgPrLicenseAcquire(runtime, p, pssh.Length, kid, &ProtectedVideoRuntime.LicenseThunk, ctx, &lic);
        license = lic;
        return hr;
    }

    public void LicenseRelease(ulong runtime, ulong license) => PrNative.FgPrLicenseRelease(runtime, license);
}
