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
/// (on the native notifier thread, never the runtime thread or an MF thread) and is dispatched to the owning session,
/// which only flips POD fields and asks for ONE coalesced UI pump — no signal is ever written off the pump. No native
/// call is ever made while the managed gate is held.</para>
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

    /// <summary>
    /// Where the protected runtime's D3D11 video device lands: returns the renderer's adapter LUID packed
    /// <c>(HighPart &lt;&lt; 32) | LowPart</c>, or 0 when no renderer device exists yet. The host installs it once at startup
    /// (the engine's <c>GpuAdapterInfo</c> is the usual source), exactly like the clear path's engine pins its decode device
    /// to the renderer's adapter. Read at every Acquire (and so at every native bring-up); a runtime that no longer sits on the renderer's adapter
    /// (the user switched GPUs) is rebuilt by the next <see cref="Acquire"/> that finds no session on it. Unset ⇒ the
    /// default adapter, as before.
    /// </summary>
    public static Func<long>? AdapterLuidProvider;

    /// <summary>How long after a runtime BRING-UP failure the next <see cref="Acquire"/> still refuses to rebuild. A rebuild
    /// is MFStartup + a D3D11 device + the CDM and an <c>mfpmp.exe</c> process: a deterministic failure must not pay that on
    /// every open. A reset-class failure of a runtime that WAS working (device removed, hardware-DRM context reset)
    /// rebuilds at once — it carries no cooldown.</summary>
    public const int BringUpRetryCooldownMs = 2_000;

    private static ProtectedVideoRuntime? s_shared;
    private static readonly object s_sharedGate = new();

    private readonly IPrRuntimeNative _native;
    private readonly IPrSessionNative _sessionNative;
    private readonly string _storePath;
    private readonly int _idleMs;
    private readonly Func<long> _adapterLuidSource;
    private readonly int _rebuildCooldownMs;
    private readonly object _gate = new();
    // Serialises native bring-up against native teardown. FgPrRuntimeDestroy blocks (it joins the runtime thread) and runs
    // OUTSIDE _gate; a create that raced it would get ERROR_BUSY from the DLL and fail the video that asked. Lock order:
    // _lifecycleGate, then _gate. Only the create and the destroy take it — every other call stays on _gate alone.
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, LicenseEntry> _licenses = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, ProtectedVideoSession> _sessions = new();
    private readonly Dictionary<ulong, BufferedWait> _bufferedWaits = new();
    private readonly Dictionary<ulong, RelayCall> _relays = new();   // in-flight license relays by native license handle
    // Native license handles this runtime has dropped or replaced (stale, expired, failed, evicted, trimmed, orphaned). An event that
    // still names one - a superseded relay's late failure - is about a key session no row owns any more: it is never adopted
    // by KID onto the fresh row that took the KID. Bounded (RetiredHandleCap); cleared with the rest of the tables on teardown.
    private readonly HashSet<ulong> _retiredLicenses = new();
    private const int RetiredHandleCap = 256;

    private GCHandle _self;
    private ulong _rt;
    private int _refs;
    private Timer? _idle;
    private bool _disposed;
    private long _createdTimestamp;

    // The runtime is POISONED when its engine + CDM + D3D11 device can no longer be trusted: bring-up failed
    // (FgPrEvent_RuntimeFailed) or a session reported a reset-class HRESULT (device removed/reset, a hardware-DRM context
    // reset, MF_E_SHUTDOWN). A poisoned runtime is never handed out again: the next Acquire destroys it and brings up a fresh
    // one even while a keep-alive token still holds a reference. Guarded by _gate.
    private bool _poisoned;
    private long _noRebuildBeforeTick;       // Environment.TickCount64 before which a bring-up failure is not retried
    private long _createdAdapterLuid;        // the LUID the live native runtime was created for (0 = default adapter)
    // The idle teardown's pre-detach window: an Acquire that lands in it (even one that releases again at once) sets the
    // wanted flag, and the teardown, which re-checks it right before it detaches, stands down. Guarded by _gate.
    private bool _idleTeardown, _teardownWanted;
    // Interlocked/Volatile: the native destroy joins the runtime thread (bounded), and an Acquire that arrives while it runs
    // can only wait it out and then pay a cold bring-up. Counted so runtime.destroy can say how often that happened.
    private int _destroying, _racingAcquires;

    /// <summary>Test seam: runs on the idle teardown's thread between its decision to destroy and the re-check that can still
    /// stand it down (the window an <see cref="Acquire"/> can rescue the runtime in). Null in production.</summary>
    internal Action? IdleTeardownProbe { get; set; }

    /// <summary>How long ONE licence-relay attempt may run before it is cancelled through <see cref="LicenseRequest.Cancel"/>
    /// (and retried once). A licence POST normally takes well under a second; the app's HTTP client waits 30 s, which is longer
    /// than the 10 s start budget and the <see cref="LicenseCachePolicy.PendingDeadlineMs"/> after which a retry re-acquires, so
    /// a hung POST is cut here instead. 0 or less = no timeout.</summary>
    public const int LicenseRelayAttemptTimeoutMs = 10_000;

    /// <summary>The base of the jittered pause before the one retry of a failed relay attempt: the wait is
    /// <c>base/2 + random(0..base)</c> ms (Shaka's fuzzed back-off), so a server that just dropped a request is not hit by
    /// every client at the same instant.</summary>
    public const int LicenseRelayRetryBackoffMs = 500;

    /// <summary>Test seam: the millisecond clock the license cache reads (rows' acquire / last-use / expiry stamps and the Pending
    /// deadline). Null = <see cref="Environment.TickCount64"/>.</summary>
    internal Func<long>? Clock { get; set; }

    private long NowMs() => Clock?.Invoke() ?? Environment.TickCount64;

    /// <summary>The relay attempt timeout in force (tests shorten it).</summary>
    internal int RelayAttemptTimeoutMs { get; set; } = LicenseRelayAttemptTimeoutMs;

    /// <summary>The relay retry back-off base in force (tests set 0 to retry at once).</summary>
    internal int RelayRetryBackoffMs { get; set; } = LicenseRelayRetryBackoffMs;

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
        // F226 / F216: the acquisition's stamps (Stopwatch timestamps, 0 = not yet), the relay's own optional breakdown.
        public long AcquiredTs, ChallengeTs, RelayDoneTs, UsableTs;
        public bool HasRelayTiming;
        public long RelayQueuedMs, RelayHttpMs;
    }

    /// <summary>Where one key's acquisition spent its time (F226), as the runtime saw it: <see cref="ToChallengeMs"/> is acquire to the
    /// CDM's challenge reaching the relay (the runtime queue's wait, which on a cold switch is the bring-up, plus CreateSession and
    /// GenerateRequest; the native <c>[cdm]</c> lines split it), <see cref="RelayMs"/> the relay call (queue wait plus HTTP),
    /// <see cref="QueuedMs"/> / <see cref="HttpMs"/> the relay's own split of that when it reports one (-1 otherwise),
    /// <see cref="DeliverMs"/> the hand-over to the CDM until the key was usable (runtime queue, Update, key status) and
    /// <see cref="TotalMs"/> acquire to usable. -1 = a stage not stamped yet. The stamps are on the Stopwatch clock.</summary>
    internal readonly record struct LicenseTimings(long AcquiredTimestamp, long ChallengeTimestamp, long RelayDoneTimestamp, long UsableTimestamp,
                                                   long QueuedMs, long HttpMs)
    {
        internal long ToChallengeMs => ProtectedVideoSession.StageMs(AcquiredTimestamp, ChallengeTimestamp);
        internal long RelayMs => ProtectedVideoSession.StageMs(ChallengeTimestamp, RelayDoneTimestamp);
        internal long DeliverMs => ProtectedVideoSession.StageMs(RelayDoneTimestamp, UsableTimestamp);
        internal long TotalMs => ProtectedVideoSession.StageMs(AcquiredTimestamp, UsableTimestamp);
    }

    private static LicenseTimings TimingsOf(in LicenseEntry e)
        => new(e.AcquiredTs, e.ChallengeTs, e.RelayDoneTs, e.UsableTs, e.HasRelayTiming ? e.RelayQueuedMs : -1, e.HasRelayTiming ? e.RelayHttpMs : -1);

    /// <summary>The acquisition stamps of <paramref name="kid"/>'s cached key (a switch's <c>switch.budget</c> reads the licence stages
    /// from it), or false when there is no row.</summary>
    internal bool TryGetLicenseTimings(string? kid, out LicenseTimings timings)
    {
        timings = default;
        if (string.IsNullOrEmpty(kid)) return false;
        lock (_gate)
        {
            if (!_licenses.TryGetValue(kid, out LicenseEntry e)) return false;
            timings = TimingsOf(in e);
            return true;
        }
    }

    // F216: when the native runtime reported ready (Stopwatch timestamp, 0 = not yet).
    private long _readyTimestamp;

    /// <summary>The Stopwatch timestamp at which the live native runtime reported ready (0 until it has): a switch that began before
    /// it paid the difference as bring-up on its critical path.</summary>
    internal long RuntimeReadyTimestamp => Volatile.Read(ref _readyTimestamp);

    /// <summary>A runtime over <paramref name="native"/> — the real DLL in production (<see cref="Shared"/>), a fake in
    /// the engine's own tests. <paramref name="idleMs"/> is the warm-idle window (tests shorten it).
    /// <paramref name="sessionNative"/> is what every <see cref="ProtectedVideoSession"/> on this runtime calls for its
    /// own verbs (the real DLL when null). <paramref name="adapterLuid"/> is where the D3D11 device should land (packed
    /// LUID, 0 = default adapter); null reads <see cref="AdapterLuidProvider"/>. <paramref name="rebuildCooldownMs"/> is the
    /// <see cref="BringUpRetryCooldownMs"/> window (tests pass 0).</summary>
    internal ProtectedVideoRuntime(IPrRuntimeNative native, string storePath, int idleMs = WarmIdleDisposeMs,
                                   IPrSessionNative? sessionNative = null, Func<long>? adapterLuid = null,
                                   int rebuildCooldownMs = BringUpRetryCooldownMs)
    {
        _native = native;
        _sessionNative = sessionNative ?? PrSessionNative.Instance;
        _storePath = storePath;
        _idleMs = idleMs > 0 ? idleMs : WarmIdleDisposeMs;
        _adapterLuidSource = adapterLuid ?? ProviderAdapterLuid;
        _rebuildCooldownMs = Math.Max(0, rebuildCooldownMs);
    }

    /// <summary><see cref="AdapterLuidProvider"/>'s answer; 0 when none is installed or it throws.</summary>
    private static long ProviderAdapterLuid()
    {
        try { return AdapterLuidProvider?.Invoke() ?? 0; }
        catch { return 0; }
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

    /// <summary>Whether the live native runtime failed (bring-up, or a reset-class HRESULT) and is waiting to be replaced by
    /// the next <see cref="Acquire"/>.</summary>
    internal bool IsPoisoned { get { lock (_gate) return _poisoned; } }

    /// <summary>No runtime is up, or the one that is must be replaced first: one read under the gate, so a poisoned runtime
    /// that is detached between two reads cannot be mistaken for a usable one.</summary>
    private bool NeedsBringUp() { lock (_gate) return _rt == 0 || _poisoned; }

    /// <summary>The bring-up failure, if the runtime could not be created (surfaced as a typed DRM error by the
    /// session that asked for it).</summary>
    internal string? StartupError { get; private set; }

    /// <summary>The HRESULT behind <see cref="StartupError"/> when the failure was a runtime bring-up one (a failed create,
    /// or FgPrEvent_RuntimeFailed), else 0. A session that cannot start because of it reports a retryable error carrying
    /// this code: reopening after the cooldown brings the runtime up afresh.</summary>
    internal int StartupHr { get; private set; }

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
        long luid = _adapterLuidSource();   // outside every lock: the host's provider is foreign code

        // Fast path: the runtime is up and trustworthy — take a reference and cancel a pending idle teardown.
        lock (_gate)
        {
            if (_disposed) return false;
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_rt != 0 && !NeedsRebuildLocked(luid)) { TakeReferenceLocked(); return true; }
        }
        // Slow path: bring it up, after any teardown that is still joining the old runtime thread has finished.
        if (Volatile.Read(ref _destroying) != 0) Interlocked.Increment(ref _racingAcquires);
        long waitStart = Stopwatch.GetTimestamp();
        lock (_lifecycleGate)
        {
            double waitedMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            ulong stale = 0;
            ulong[] staleLicenses = [];
            string? staleWhy = null;
            lock (_gate)
            {
                if (_disposed) return false;
                if (_rt != 0)
                {
                    if (!NeedsRebuildLocked(luid)) { TakeReferenceLocked(); return true; }   // another caller brought it up while this one waited
                    // A poisoned runtime (or one on the wrong adapter) is replaced even while references are held: they stay
                    // counted (their owners release them as ever) and now simply pin the new runtime.
                    staleWhy = _poisoned ? "poisoned" : "adapter";
                    stale = DetachLocked(out staleLicenses);
                }
            }
            if (stale != 0)
            {
                Log($"runtime.rebuild reason={staleWhy}");
                DestroyNativeTimed(stale, staleLicenses);   // blocking (bounded native join) — outside _gate, inside the lifecycle lock
            }

            lock (_gate)
            {
                if (_disposed) return false;
                if (_rt != 0) { TakeReferenceLocked(); return true; }   // cannot happen under the lifecycle lock; kept as the guard it always was

                if (!_native.IsAvailable)
                {
                    StartupError = "The protected-video component (" + PrNative.LibraryName +
                                   ") is missing, or is a stale build that does not export the runtime ABI.";
                    StartupHr = 0;
                    return false;
                }
                if (Environment.TickCount64 < _noRebuildBeforeTick)
                {
                    // The last bring-up failed moments ago: StartupError still says why. Not rebuilt on every open.
                    Log("runtime.create deferred (bring-up failed moments ago)");
                    return false;
                }
                try { Directory.CreateDirectory(_storePath); } catch { /* the CDM reports a store failure itself */ }
                if (!_self.IsAllocated) _self = GCHandle.Alloc(this, GCHandleType.Normal);

                // FgPrRuntimeCreateOnAdapter is non-blocking by contract (it returns once the runtime thread is up; bring-up completion
                // is an event), so creating under the gate cannot stall a caller for the CDM's bring-up time.
                long t0 = Stopwatch.GetTimestamp();
                int hr;
                ulong rt;
                try
                {
                    // F249: the engine's output format is read once, at bring-up: NV12 only behind --fg video-nv12 and only where the output's
                    // overlay probe said NV12 can take a plane (an unprobed output, or the switch off, keeps BGRA).
                    VideoOutputFormat outputFormat = VideoOverlayCaps.ChooseOutputFormat(FluentGpu.Hosting.EngineSwitches.Nv12VideoOutput, VideoOverlayCaps.Latest);
                    _native.SetVideoOutputFormat((int)outputFormat);
                    hr = _native.RuntimeCreate(_storePath, GCHandle.ToIntPtr(_self), luid, out rt);
                }
                catch (Exception e)
                {
                    StartupError = "The protected-video runtime could not start: " + e.Message;
                    StartupHr = 0;
                    return false;
                }
                if (hr < 0 || rt == 0)
                {
                    StartupError = $"The protected-video runtime could not start (0x{unchecked((uint)hr):X8}).";
                    StartupHr = hr < 0 ? hr : PrNative.EFail;
                    Log($"runtime.create FAILED hr=0x{unchecked((uint)hr):X8}");
                    return false;
                }
                Volatile.Write(ref _createdTimestamp, t0);
                Volatile.Write(ref _rt, rt);
                if (ReferenceEquals(s_shared, this)) MediaCensus.NoteProtectedRuntime(up: true);   // F197: the process runtime's up/down is a census line
                _createdAdapterLuid = luid;
                _refs++;
                StartupError = null;
                StartupHr = 0;
                Log($"runtime.create ok ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} waitedMs={waitedMs:F0} " +
                    $"adapter={(luid != 0 ? $"0x{luid:X}" : "default")}");
                return true;
            }
        }
    }

    /// <summary>Whether the live native runtime must be replaced rather than handed out: it is poisoned, or it was created for
    /// another adapter than the renderer's now AND nothing is using it (an adapter switch never ends a playing video; the
    /// next open that finds the runtime idle moves it). Either LUID being 0 means "unknown" - never a reason. Caller holds
    /// <see cref="_gate"/>.</summary>
    private bool NeedsRebuildLocked(long currentLuid)
        => _poisoned
           || (currentLuid != 0 && _createdAdapterLuid != 0 && currentLuid != _createdAdapterLuid && _sessions.Count == 0);

    /// <summary>One more reference on the live runtime. One taken while the idle teardown is deciding tells it the runtime is
    /// wanted, even if the reference is given back before the teardown looks. Caller holds <see cref="_gate"/>.</summary>
    private void TakeReferenceLocked()
    {
        _refs++;
        if (_idleTeardown) _teardownWanted = true;
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

    /// <summary>
    /// Hold the native runtime — and with it the whole KID→license cache — warm with NO session attached. Dispose the
    /// result to give the reference back.
    /// <para><b>Why this exists (D15, the app's warm keeper).</b> <see cref="_licenses"/> holds native CDM key-session
    /// handles: they are owned by the runtime instance that is about to be destroyed and cannot be replayed into the
    /// next one (the native ABI has no license-import entry point — only <c>Acquire</c>/<c>State</c>/<c>Release</c>),
    /// so <see cref="IdleElapsed"/> clearing them on teardown is correct, not a bug to route around with a second
    /// ledger. What WAS missing is a way for a caller who is not opening a session — the app's warm keeper, which
    /// knows a video-capable row is current or next well before any surface opens one — to say "not yet, keep this
    /// license cache". Before this, the keeper's only lever was re-acquiring a license every beat
    /// (<c>EnsureLicense</c>'s own <c>Acquire</c>+<c>Release</c> pair), which cancels a PENDING idle teardown but does
    /// nothing across a gap wider than <see cref="WarmIdleDisposeMs"/> (a surface closed, the beat stopped, the next
    /// open is more than 30 s later) — the exact gap that paid a full ~630 ms cold challenge for a KID whose license
    /// was otherwise still good.</para>
    /// <para>Returns null when the native component is missing or bring-up failed (<see cref="StartupError"/> says
    /// why) — same failure the fast path of <see cref="Acquire"/> reports, just surfaced as "no token" instead of
    /// <c>false</c> so a caller cannot forget to check it.</para>
    /// </summary>
    public IDisposable? TakeKeepAlive() => Acquire() ? new KeepAliveRef(this) : null;

    /// <summary>The <see cref="TakeKeepAlive"/> token. Idempotent disposal (a double-dispose from a racing shed and a
    /// new request must never double-release).</summary>
    private sealed class KeepAliveRef : IDisposable
    {
        private ProtectedVideoRuntime? _owner;
        internal KeepAliveRef(ProtectedVideoRuntime owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private void IdleElapsed()
    {
        lock (_lifecycleGate)
        {
            ulong rt;
            ulong[] licenses;
            lock (_gate)
            {
                if (_refs != 0 || _rt == 0 || _disposed) return;
                _teardownWanted = false;
                _idleTeardown = true;
            }
            Log($"runtime.destroy idleMs={_idleMs}");   // the app's log sink can be slow: the wanted check below follows it
            IdleTeardownProbe?.Invoke();
            lock (_gate)
            {
                _idleTeardown = false;
                // Re-checked right before the detach, which is the point of no return: an Acquire that came in since the
                // check above (and may already have released again) wants this runtime and its license cache. Once the
                // native destroy has begun an Acquire can only wait it out, so nothing wanted is torn down.
                if (_refs != 0 || _teardownWanted || _disposed)
                {
                    Log("runtime.destroy stood down (acquired meanwhile)");
                    if (_refs == 0 && _rt != 0 && !_disposed)
                    {
                        _idle ??= new Timer(static s => ((ProtectedVideoRuntime)s!).IdleElapsed(), this, Timeout.Infinite, Timeout.Infinite);
                        _idle.Change(_idleMs, Timeout.Infinite);   // wanted, then released again: a fresh warm window
                    }
                    return;
                }
                rt = DetachLocked(out licenses);
            }
            DestroyNativeTimed(rt, licenses);   // blocking (bounded native join) — outside _gate, inside the lifecycle lock
        }
    }

    /// <summary><see cref="DestroyNative"/> with its cost on the record: how long the native join took and how many
    /// <see cref="Acquire"/> calls arrived meanwhile (each one waited it out and then paid a cold bring-up).</summary>
    private void DestroyNativeTimed(ulong rt, ulong[] licenses)
    {
        long t0 = Stopwatch.GetTimestamp();
        Volatile.Write(ref _destroying, 1);
        try { DestroyNative(rt, licenses); }
        finally { Volatile.Write(ref _destroying, 0); }
        Log($"runtime.destroy done ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} " +
            $"racingAcquires={Interlocked.Exchange(ref _racingAcquires, 0)}");
    }

    /// <summary>
    /// Mark the live runtime poisoned and start its replacement: the next <see cref="Acquire"/> (or <see cref="TakeKeepAlive"/>)
    /// destroys it and brings up a fresh one even while keep-alive tokens still hold references — the idle teardown that used
    /// to be the only way a dead runtime was ever recreated never runs under a keeper. Every live session is told
    /// (a typed, retryable failure carrying <paramref name="hr"/>), every pending buffered wait ends, and the old native
    /// handle is destroyed off-thread: this is called on the native notifier thread, which must never destroy the runtime itself: FgPrRuntimeDestroy from inside the callback abandons the notifier.
    /// </summary>
    private void PoisonRuntime(int hr, string reason, bool bringUpFailure)
    {
        ProtectedVideoSession[] live;
        BufferedWait[] waits;
        lock (_gate)
        {
            if (_rt == 0 || _poisoned || _disposed) return;
            _poisoned = true;
            if (bringUpFailure) _noRebuildBeforeTick = Environment.TickCount64 + _rebuildCooldownMs;
            live = new ProtectedVideoSession[_sessions.Count];
            _sessions.Values.CopyTo(live, 0);
            waits = new BufferedWait[_bufferedWaits.Count];
            _bufferedWaits.Values.CopyTo(waits, 0);
            _bufferedWaits.Clear();
        }
        Log($"runtime.poisoned reason={reason} hr=0x{unchecked((uint)hr):X8} sessions={live.Length}");
        for (int i = 0; i < live.Length; i++) live[i].OnRuntimeLost(hr);
        for (int i = 0; i < waits.Length; i++) waits[i].Finish(false);
        ThreadPool.UnsafeQueueUserWorkItem(static s => ((ProtectedVideoRuntime)s!).RecycleIfPoisoned(), this);
    }

    /// <summary>The off-thread half of <see cref="PoisonRuntime"/>: destroy the poisoned native runtime (unless an
    /// <see cref="Acquire"/> already replaced it) so a dead engine + CDM + D3D11 device does not sit in memory until the next
    /// open. Never creates: the next <see cref="Acquire"/> does, honouring the bring-up cooldown.</summary>
    private void RecycleIfPoisoned()
    {
        lock (_lifecycleGate)
        {
            ulong rt;
            ulong[] licenses;
            lock (_gate)
            {
                if (!_poisoned || _rt == 0 || _disposed) return;
                rt = DetachLocked(out licenses);
            }
            Log("runtime.rebuild reason=poisoned (old runtime destroyed off-thread)");
            DestroyNativeTimed(rt, licenses);
        }
    }

    /// <summary>Clear the managed tables and hand back what the native side must release — outside the gate. The runtime
    /// is no longer poisoned afterwards: what was wrong with it goes with it.</summary>
    private ulong DetachLocked(out ulong[] licenses)
    {
        _poisoned = false;
        var handles = new List<ulong>(_licenses.Count);
        foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
            if (kv.Value.Handle != 0) handles.Add(kv.Value.Handle);
        licenses = handles.ToArray();
        _licenses.Clear();
        _retiredLicenses.Clear();   // handles are per native runtime: the next one may reuse the numbers
        _sessions.Clear();
        foreach (KeyValuePair<ulong, BufferedWait> kv in _bufferedWaits) kv.Value.Finish(false);
        _bufferedWaits.Clear();
        ulong rt = _rt;
        Volatile.Write(ref _rt, 0);
        Volatile.Write(ref _createdTimestamp, 0);
        if (rt != 0 && ReferenceEquals(s_shared, this)) MediaCensus.NoteProtectedRuntime(up: false);
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
        if (NeedsBringUp())
        {
            // Manifest-time acquisition brings the runtime up (or replaces a poisoned one). The reference is handed straight
            // back: the warm-idle window keeps everything alive until the session that follows takes its own.
            if (!Acquire()) return LicenseCacheState.Failed;
            Release();
        }

        ulong rt;
        ulong deadHandle = 0;
        ulong trimmedHandle = 0;
        ulong vouched = 0;   // a cached handle the native side has just confirmed still names a live key session
        while (true)
        {
            ulong probe = 0;
            lock (_gate)
            {
                rt = _rt;
                if (rt == 0) return LicenseCacheState.Failed;
                long now = NowMs();

                if (_licenses.TryGetValue(kid, out LicenseEntry existing))
                {
                    LicenseCacheAction what = LicenseCachePolicy.Decide(existing.State, existing.ExpiresAtMs, now, existing.AcquiredMs);
                    if (what is LicenseCacheAction.Reuse or LicenseCacheAction.Await)
                    {
                        if (existing.Handle == 0 || existing.Handle == vouched)
                        {
                            existing.LastUsedMs = now;
                            if (relay is not null) existing.Relay = relay;
                            _licenses[kid] = existing;
                            // The age is what says a join is onto an attempt that has been running too long.
                            Log($"license.cache kid={kid} state={existing.State} action={what} cached=true ageMs={now - existing.AcquiredMs}");
                            return existing.State;
                        }
                        probe = existing.Handle;   // ask the native table before trusting its handle (outside the gate)
                    }
                    else
                    {
                        if (existing.State == LicenseCacheState.Pending)
                            Log($"license.pending.stale kid={kid} ageMs={now - existing.AcquiredMs}: re-acquiring");
                        deadHandle = existing.Handle;   // expired, failed or stale: its key session is closed below, outside the gate
                        RetireLocked(deadHandle);
                        _licenses.Remove(kid);
                    }
                }

                if (probe == 0)
                {
                    trimmedHandle = TrimDeadIfFullLocked(kid);

                    // Published BEFORE the acquire, with Handle = 0: the CDM raises KeyMessage on its own thread and the relay
                    // entry finds the relay by KID. A completion that races the handle assignment below is matched by KID (the
                    // native side puts the KID in the license events' text) and adopts the handle then.
                    _licenses[kid] = new LicenseEntry
                    {
                        State = LicenseCacheState.Pending,
                        AcquiredMs = now,
                        AcquiredTs = Stopwatch.GetTimestamp(),
                        LastUsedMs = now,
                        Relay = relay,
                        System = system,
                    };
                    break;
                }
            }

            if (NativeKeepsLicense(rt, probe)) { vouched = probe; continue; }

            // The native table no longer holds this key session (its LRU closed it, a kill evicted it): drop the row and go
            // round again, which now acquires.
            bool dropped = false;
            lock (_gate)
            {
                if (_licenses.TryGetValue(kid, out LicenseEntry current) && current.Handle == probe)
                {
                    _licenses.Remove(kid);
                    RetireLocked(probe);
                    dropped = true;
                }
            }
            if (dropped)
            {
                Log($"license.stale kid={kid} lic={probe}: the native table no longer holds a live key session");
                // Release BEFORE cancelling: the cancelled relay still delivers its failure, and an old entry that is already
                // gone from the native table has nothing left to raise it on (a bound one raises it, which the retired set drops).
                try { _native.LicenseRelease(rt, probe); } catch { }
                CancelRelay(probe);
            }
        }

        if (deadHandle != 0) { try { _native.LicenseRelease(rt, deadHandle); } catch { } CancelRelay(deadHandle); }
        if (trimmedHandle != 0) { try { _native.LicenseRelease(rt, trimmedHandle); } catch { } }

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
                RetireLocked(orphan);
                result = LicenseCacheState.None;
            }
            else if (hr < 0 || lic == 0)
            {
                entry.State = LicenseCacheState.Failed;
                entry.FailureMessage ??= $"The PlayReady key session could not be opened (0x{unchecked((uint)hr):X8}).";
                _licenses[kid] = entry;
                Log($"license.fail kid={kid} hr=0x{unchecked((uint)hr):X8} ms={NowMs() - entry.AcquiredMs}");
                result = LicenseCacheState.Failed;
            }
            else
            {
                if (entry.Handle == 0) entry.Handle = lic;   // a racing completion may already have adopted it
                _retiredLicenses.Remove(lic);   // native joined an entry this side had already retired: the row owns it again
                _licenses[kid] = entry;
                Log($"license.acquire kid={kid} lic={lic} cached=false");
                result = entry.State;
            }
        }
        if (orphan != 0) { try { _native.LicenseRelease(rt, orphan); } catch { } CancelRelay(orphan); }
        return result;
    }

    /// <summary>Whether the native table still answers for <paramref name="handle"/> with a key session that can decrypt:
    /// <c>FgPrLicenseState</c> says unknown (evicted, released) or failed/expired as a negative HRESULT / 2. An unanswerable
    /// probe (the call throws) never condemns a license. Never called under <see cref="_gate"/>.</summary>
    private bool NativeKeepsLicense(ulong rt, ulong handle)
    {
        try
        {
            int state = _native.LicenseState(rt, handle);
            return state >= 0 && state != 2;
        }
        catch { return true; }
    }

    /// <summary>
    /// Make room by trimming a DEAD row (Failed / Expired, not in use) when the cache is full; returns the native handle to
    /// release (outside the gate), or 0. A LIVE row is never evicted from here: the native license table is the single
    /// eviction authority (its LRU closes the key session and raises <c>EvLicenseEvicted</c>, see <see cref="OnLicenseEvicted"/>),
    /// so this cache can never close or forget a key session the native side still holds, nor keep one it has closed.
    /// </summary>
    private ulong TrimDeadIfFullLocked(string keepKid)
    {
        if (LicenseCachePolicy.HasRoom(_licenses.Count)) return 0;
        var rows = new LicenseCacheEntry[_licenses.Count];
        int i = 0;
        foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
            rows[i++] = new LicenseCacheEntry(kv.Key, kv.Value.State, kv.Value.LastUsedMs, kv.Value.ExpiresAtMs, kv.Value.InUse > 0);
        int victim = LicenseCachePolicy.ChooseDeadEviction(rows, keepKid);
        if (victim < 0) return 0;
        string kid = rows[victim].Kid;
        ulong handle = _licenses.TryGetValue(kid, out LicenseEntry dead) ? dead.Handle : 0;
        _licenses.Remove(kid);
        RetireLocked(handle);
        Log($"license.trim kid={kid} (dead row)");
        return handle;
    }

    /// <summary>Remember that <paramref name="handle"/> no longer belongs to any row (see <see cref="_retiredLicenses"/>). Under
    /// <see cref="_gate"/>. A full set starts over: the cap only bounds memory, and a handle retired long ago has stopped raising events.</summary>
    private void RetireLocked(ulong handle)
    {
        if (handle == 0) return;
        if (_retiredLicenses.Count >= RetiredHandleCap) _retiredLicenses.Clear();
        _retiredLicenses.Add(handle);
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

    /// <summary><see cref="LicenseHandleFor"/>, after asking the native table whether it still holds that key session. A handle
    /// the native side closed (its LRU, a kill) is dropped from the cache and 0 is returned, so the attach re-acquires instead of
    /// binding a dead handle (a no-op in native) and stalling until the start deadline. The native call is made outside the gate.</summary>
    internal ulong ValidatedLicenseHandleFor(string? kid)
    {
        if (string.IsNullOrEmpty(kid)) return 0;
        ulong rt, handle;
        lock (_gate)
        {
            rt = _rt;
            handle = rt != 0 && _licenses.TryGetValue(kid, out LicenseEntry e) ? e.Handle : 0;
        }
        if (handle == 0 || NativeKeepsLicense(rt, handle)) return handle;

        bool dropped = false;
        lock (_gate)
        {
            if (_licenses.TryGetValue(kid, out LicenseEntry current) && current.Handle == handle)
            {
                _licenses.Remove(kid);
                RetireLocked(handle);
                dropped = true;
            }
        }
        if (!dropped) return 0;
        Log($"license.stale kid={kid} lic={handle}: the native table no longer holds a live key session");
        try { _native.LicenseRelease(rt, handle); } catch { }
        CancelRelay(handle);
        return 0;
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
            e.LastUsedMs = NowMs();
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

    /// <summary>The ONE native→managed event entry point (<c>FgPrEventCallback</c>). Native calls it from a single notifier
    /// thread that drains an event ring in batches: never from the runtime thread or an MF/CDM thread, never under a native
    /// lock. A managed GC or a slow <see cref="LogSink"/> therefore delays later events but stalls no native work.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static void EventThunk(nint ctx, ulong session, int ev, long a, long b, char* text)
    {
        try
        {
            if (ctx == 0 || GCHandle.FromIntPtr(ctx).Target is not ProtectedVideoRuntime self) return;
            // Only the license and log events need the text as a string; every other event passes the pointer through.
            string? line = text != null && ev is PrNative.EvLicenseUsable or PrNative.EvLicenseFailed
                                                  or PrNative.EvLicenseExpired or PrNative.EvLicenseRevoked or PrNative.EvLog
                ? new string(text) : null;
            self.OnNativeEvent(session, ev, a, b, line);
        }
        catch { /* a native callback may never throw across the ABI */ }
    }

    /// <summary>Dispatch one native event: update the license cache, complete a buffered wait, hand the event to the
    /// owning session. Runs on the native notifier thread (events arrive in order, in batches) and does almost nothing — the session flips POD fields and
    /// asks for ONE coalesced UI pump. Internal so the engine's tests can drive the runtime without the DLL.</summary>
    internal void OnNativeEvent(ulong session, int ev, long a, long b, string? text)
    {
        switch (ev)
        {
            case PrNative.EvLicenseUsable:
            case PrNative.EvLicenseFailed:
            case PrNative.EvLicenseExpired:
            case PrNative.EvLicenseRevoked:
                OnLicenseEvent(session, ev, a, b, text);
                return;

            case PrNative.EvLicenseEvicted:
                OnLicenseEvicted(session);
                return;

            case PrNative.EvLicenseRestricted:
                OnLicenseRestricted(session, a, b);
                return;

            case PrNative.EvRuntimeReady:
                Volatile.Write(ref _readyTimestamp, Stopwatch.GetTimestamp());
                Log($"runtime.ready ms={a}");
                return;

            case PrNative.EvRuntimeFailed:
                int failedHr = a != 0 ? unchecked((int)a) : PrNative.EFail;
                lock (_gate)
                {
                    StartupError = $"The protected-video runtime failed (0x{unchecked((uint)a):X8}).";
                    StartupHr = failedHr;
                }
                Log($"runtime.failed hr=0x{unchecked((uint)a):X8}");
                // Bring-up failures arrive ONLY here (FgPrRuntimeCreateOnAdapter is non-blocking), so this is where a dead runtime is
                // recognised: it is never handed out again.
                PoisonRuntime(failedHr, "bring-up failed", bringUpFailure: true);
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
                // A device removed/reset or hardware-DRM context reset takes the whole engine + CDM with it, not just this
                // session: replace the runtime (Chromium restarts its pipeline on the same HRESULTs).
                if (ProtectedRuntimeFaults.IsRuntimeReset(unchecked((int)b)))
                    PoisonRuntime(unchecked((int)b), "session error", bringUpFailure: false);
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
        LicenseTimings budget = default;
        lock (_gate)
        {
            foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
                if (kv.Value.Handle == licenseHandle) { kid = kv.Key; break; }

            // The acquire call may not have returned yet (the handle is recorded after it does): match by the KID the
            // native side carries in the event text, and adopt the handle — but only onto a row still waiting for one. A
            // revocation is about a key session that was usable, so its row has had its handle for a while: never adopted. A
            // RETIRED handle (a replaced or dropped licence, e.g. a superseded relay's late failure) is never adopted either: the
            // fresh row of the same KID waits for ITS handle, not for the old one's news.
            if (kid is null && ev != PrNative.EvLicenseRevoked && !string.IsNullOrEmpty(kidText)
                && !_retiredLicenses.Contains(licenseHandle)
                && _licenses.TryGetValue(kidText, out LicenseEntry byKid)
                && byKid.Handle == 0 && byKid.State == LicenseCacheState.Pending)
            {
                kid = kidText;
                byKid.Handle = licenseHandle;
                _licenses[kid] = byKid;
            }
            if (kid is null) return;   // a completion for an evicted/released row: dropped (LicenseCachePolicy)

            LicenseEntry e = _licenses[kid];
            sinceMs = NowMs() - e.AcquiredMs;
            if (ev == PrNative.EvLicenseExpired)
            {
                if (!LicenseCachePolicy.AcceptExpiry(rowExists: true, e.State, e.Handle, licenseHandle)) return;
                e.State = LicenseCacheState.Expired;
            }
            else if (ev == PrNative.EvLicenseRevoked)
            {
                if (!LicenseCachePolicy.AcceptRevocation(rowExists: true, e.State, e.Handle, licenseHandle)) return;
                e.State = LicenseCacheState.Failed;
                e.FailureMessage ??= $"The PlayReady license was revoked by the CDM (key status {b}, 0x{unchecked((uint)a):X8}).";
            }
            else if (LicenseCachePolicy.AcceptCompletion(rowExists: true, e.State, e.Handle, licenseHandle))
            {
                if (ev == PrNative.EvLicenseUsable)
                {
                    e.State = LicenseCacheState.Usable;
                    e.ExpiresAtMs = b > 0 ? NowMs() + b : 0;
                    e.UsableTs = Stopwatch.GetTimestamp();
                    budget = TimingsOf(in e);
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
                : ev == PrNative.EvLicenseRevoked
                    ? $"license.revoked kid={kid} status={b} hr=0x{unchecked((uint)a):X8} ms={sinceMs}"
                    : $"license.fail kid={kid} hr=0x{unchecked((uint)a):X8} ms={sinceMs}");

        if (ev == PrNative.EvLicenseUsable) LogLicenseBudget(kid!, a, in budget);

        ProtectedVideoSession[] live;
        lock (_gate)
        {
            live = new ProtectedVideoSession[_sessions.Count];
            _sessions.Values.CopyTo(live, 0);
        }
        for (int i = 0; i < live.Length; i++) live[i].OnLicenseEvent(licenseHandle, ev, a);

        // A hardware-DRM reset can surface first as a failed key session.
        if (ev == PrNative.EvLicenseFailed && ProtectedRuntimeFaults.IsRuntimeReset(unchecked((int)a)))
            PoisonRuntime(unchecked((int)a), "license error", bringUpFailure: false);
    }

    /// <summary>F226: the ONE <c>license.budget</c> line of an acquisition, written when its key turns usable, so "where did the licence time
    /// go" is read, not reconstructed. <c>totalMs</c> is native's acquire-to-usable (the same number as <c>license.ok</c>);
    /// <c>bringUpWaitMs</c> is the part of acquire-to-challenge spent waiting for the native runtime to finish coming up (the item sits
    /// behind it in the runtime queue), <c>cdmMs</c> the rest of it (queue, CreateSession, GenerateRequest; the native
    /// <c>[cdm]</c> lines carry waitedMs / ms for each), <c>relayMs</c> the app relay call and, when it reports them,
    /// <c>queuedMs</c> (its api-queue wait) and <c>httpMs</c> (the round trip), <c>deliverMs</c> hand-over to usable (runtime queue,
    /// Update, key status). -1 = not reported. Skipped for a key whose acquisition this side did not stamp.</summary>
    private void LogLicenseBudget(string kid, long totalMs, in LicenseTimings t)
    {
        if (t.AcquiredTimestamp == 0) return;
        long toChallenge = t.ToChallengeMs;
        long bringUpWait = 0;
        long ready = Volatile.Read(ref _readyTimestamp);
        if (ready == 0) bringUpWait = -1;
        else if (ready > t.AcquiredTimestamp)
            bringUpWait = ProtectedVideoSession.StageMs(t.AcquiredTimestamp, t.ChallengeTimestamp != 0 && t.ChallengeTimestamp < ready ? t.ChallengeTimestamp : ready);
        long cdm = toChallenge < 0 ? -1 : bringUpWait < 0 ? toChallenge : Math.Max(0, toChallenge - bringUpWait);
        Log($"license.budget kid={kid} totalMs={totalMs} bringUpWaitMs={bringUpWait} cdmMs={cdm} relayMs={t.RelayMs} " +
            $"queuedMs={t.QueuedMs} httpMs={t.HttpMs} deliverMs={t.DeliverMs}");
    }

    /// <summary>The native table's LRU closed <paramref name="licenseHandle"/>'s key session to admit another KID: drop the row
    /// that holds that handle, so the next <see cref="EnsureLicense"/> acquires afresh instead of reusing a handle native no
    /// longer knows. Matched by handle ONLY (never by KID: the same KID may already have a fresh row), and a handle no row
    /// carries is a no-op.</summary>
    private void OnLicenseEvicted(ulong licenseHandle)
    {
        if (licenseHandle == 0) return;
        string? kid = null;
        lock (_gate)
        {
            foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
                if (kv.Value.Handle == licenseHandle) { kid = kv.Key; break; }
            if (kid is null) return;
            _licenses.Remove(kid);
            RetireLocked(licenseHandle);
        }
        CancelRelay(licenseHandle);
        Log($"license.evicted kid={kid} lic={licenseHandle} (native table)");
    }

    /// <summary>The CDM restricted (OUTPUT_RESTRICTED 7 / OUTPUT_DOWNSCALED 2) or un-restricted (0) the key behind
    /// <paramref name="licenseHandle"/>. The key still decrypts, so the cache row is untouched; the sessions decoding with it are told
    /// (<see cref="ProtectedVideoSession.LicenseRestriction"/>) so the app can say why the picture is reduced.</summary>
    private void OnLicenseRestricted(ulong licenseHandle, long status, long previous)
    {
        if (licenseHandle == 0) return;
        string? kid = null;
        ProtectedVideoSession[] live;
        lock (_gate)
        {
            foreach (KeyValuePair<string, LicenseEntry> kv in _licenses)
                if (kv.Value.Handle == licenseHandle) { kid = kv.Key; break; }
            if (kid is null) return;
            live = new ProtectedVideoSession[_sessions.Count];
            _sessions.Values.CopyTo(live, 0);
        }
        Log($"license.restricted kid={kid} status={status} previous={previous}");
        for (int i = 0; i < live.Length; i++) live[i].OnLicenseRestricted(licenseHandle, unchecked((int)status));
    }

    /// <summary>Stop the relay in flight for <paramref name="license"/> (its row was replaced, dropped or evicted): the attempt's
    /// token is cancelled so the POST frees its connection, and a retry is not started. The call finishes quietly: what it
    /// reports no longer belongs on any row. A handle with no relay in flight is a no-op. Never called under <see cref="_gate"/>.</summary>
    private void CancelRelay(ulong license)
    {
        if (license == 0) return;
        RelayCall? call;
        lock (_gate) _relays.Remove(license, out call);
        call?.Supersede();
    }

    private void ForgetRelay(ulong license, RelayCall call)
    {
        lock (_gate)
            if (_relays.TryGetValue(license, out RelayCall? current) && ReferenceEquals(current, call))
                _relays.Remove(license);
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
            if (e.ChallengeTs == 0) { e.ChallengeTs = Stopwatch.GetTimestamp(); _licenses[kid] = e; }
        }
        if (relay is null)
        {
            RecordRelayFailure(kid, "No DRM license relay is configured for the protected source (call WithDrm).", lic);
            return PrNative.EFail;
        }

        var call = new RelayCall(this, lic, kid, delivery, relay,
            new LicenseRequest(system, challenge, kid.Length > 0 ? kid : null, default));
        lock (_gate) _relays[lic] = call;
        Log($"license.challenge kid={kid} bytes={challenge.Length}");
        call.Start();
        return 0;
    }

    /// <summary>Whether a relay failure is worth the one retry: a transport failure (no HTTP status: DNS, connect, TLS, reset), a
    /// timeout, or a 5xx / 408 / 429 answer. A rejected challenge (any other status) or a relay that simply threw is final: asking
    /// the same server the same question again will not change the answer. The app's relay reports a transient HTTP failure as an
    /// <see cref="System.Net.Http.HttpRequestException"/> carrying the status (null for none), which is what this reads.</summary>
    internal static bool IsTransientRelayFailure(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException h => h.StatusCode is null or >= System.Net.HttpStatusCode.InternalServerError
                                                     or System.Net.HttpStatusCode.RequestTimeout
                                                     or System.Net.HttpStatusCode.TooManyRequests,
        IOException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>One in-flight relay: up to <see cref="MaxRelayAttempts"/> attempts, each under its own timeout token, the retry after a
    /// jittered pause. Never touches a signal (it completes on a pool thread).</summary>
    private sealed class RelayCall
    {
        private const int MaxRelayAttempts = 2;

        private readonly ProtectedVideoRuntime _owner;
        private readonly ulong _license;
        private readonly string _kid;
        private readonly ILicenseDelivery _delivery;
        private readonly Func<LicenseRequest, ValueTask<LicenseResponse>> _relay;
        private readonly LicenseRequest _request;
        private CancellationTokenSource? _attemptCts;
        private int _attempts, _done, _superseded;

        internal RelayCall(ProtectedVideoRuntime owner, ulong license, string kid, ILicenseDelivery delivery,
                           Func<LicenseRequest, ValueTask<LicenseResponse>> relay, LicenseRequest request)
        { _owner = owner; _license = license; _kid = kid; _delivery = delivery; _relay = relay; _request = request; }

        internal void Start() => Attempt();

        /// <summary>The license this call belongs to was replaced, dropped or evicted: cancel the attempt in flight (the relay frees
        /// its connection), start no retry, and report nothing onto the cache row, which belongs to the replacement now.</summary>
        internal void Supersede()
        {
            Volatile.Write(ref _superseded, 1);
            try { Volatile.Read(ref _attemptCts)?.Cancel(); }
            catch (ObjectDisposedException) { /* the attempt finished while this ran */ }
        }

        private void Attempt()
        {
            if (Volatile.Read(ref _superseded) != 0) { Fail("The DRM license relay was canceled."); return; }
            _attempts++;
            int timeoutMs = _owner.RelayAttemptTimeoutMs;
            var cts = new CancellationTokenSource(timeoutMs > 0 ? timeoutMs : Timeout.Infinite);
            Volatile.Write(ref _attemptCts, cts);
            if (Volatile.Read(ref _superseded) != 0) cts.Cancel();   // superseded while this attempt was being set up
            try
            {
                _relay(_request with { Cancel = cts.Token })
                    .AsTask()
                    .ContinueWith(static (t, s) =>
                    {
                        var (call, attemptCts) = ((RelayCall, CancellationTokenSource))s!;
                        call.Complete(t, attemptCts);
                    }, (this, cts), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _attemptCts, null, cts);
                bool timedOut = cts.IsCancellationRequested && Volatile.Read(ref _superseded) == 0;
                cts.Dispose();
                Failed(ex, timedOut);
            }
        }

        private void Complete(Task<LicenseResponse> task, CancellationTokenSource cts)
        {
            // Our own timeout fired (not a supersede, not the relay's own cancellation): the attempt stalled.
            bool timedOut = cts.IsCancellationRequested && Volatile.Read(ref _superseded) == 0;
            Interlocked.CompareExchange(ref _attemptCts, null, cts);
            cts.Dispose();
            if (task.IsFaulted)
            {
                Failed(task.Exception?.GetBaseException() ?? new InvalidOperationException("unknown"), timedOut);
                return;
            }
            if (task.IsCanceled) { Failed(new OperationCanceledException(), timedOut); return; }

            ReadOnlyMemory<byte> license = task.Result.License;
            if (license.IsEmpty) { Fail("The DRM license relay returned an empty license."); return; }
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            _owner.ForgetRelay(_license, this);
            _owner.NoteRelayDone(_kid, _license, task.Result);
            try { _delivery.Deliver(license.Span, 0); } catch { /* a destroyed runtime ignores a late delivery */ }
            _owner.Log($"license.delivered kid={_kid} lic={_license} bytes={license.Length}");
        }

        /// <summary>An attempt ended in <paramref name="e"/>: retry ONCE (after a jittered pause) when it was transient or timed out
        /// and the license is still wanted; otherwise report it.</summary>
        private void Failed(Exception e, bool timedOut)
        {
            if (Volatile.Read(ref _superseded) == 0 && _attempts < MaxRelayAttempts && (timedOut || IsTransientRelayFailure(e)))
            {
                int delay = _owner.NextRelayRetryDelayMs();
                _owner.Log($"license.relay.retry kid={_kid} attempt={_attempts + 1} afterMs={delay} " +
                           $"reason={(timedOut ? "timeout" : e.GetType().Name + ": " + e.Message)}");
                if (delay <= 0) Attempt();
                else
                    Task.Delay(delay).ContinueWith(static (_, s) => ((RelayCall)s!).Attempt(), this,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
            Fail(timedOut ? "The DRM license relay timed out."
                : e is OperationCanceledException ? "The DRM license relay was canceled."
                : "The DRM license relay failed: " + e.Message);
        }

        internal void Fail(string message)
        {
            if (Volatile.Read(ref _superseded) == 0) _owner.RecordRelayFailure(_kid, message, _license);
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            _owner.ForgetRelay(_license, this);
            try { _delivery.Deliver(ReadOnlySpan<byte>.Empty, LicenseRelayFailedHr); } catch { }
        }
    }

    /// <summary>The relay answered with a license: stamp when, and keep the relay's own queue / HTTP split when it reported one (F226).
    /// Matched by KID and handle, so a late answer of a replaced license never stamps the fresh row.</summary>
    private void NoteRelayDone(string kid, ulong license, LicenseResponse response)
    {
        lock (_gate)
        {
            if (!_licenses.TryGetValue(kid, out LicenseEntry e) || (e.Handle != 0 && e.Handle != license)) return;
            e.RelayDoneTs = Stopwatch.GetTimestamp();
            if (response.QueuedMs >= 0 || response.HttpMs >= 0)
            {
                e.HasRelayTiming = true;
                e.RelayQueuedMs = response.QueuedMs;
                e.RelayHttpMs = response.HttpMs;
            }
            _licenses[kid] = e;
        }
    }

    /// <summary>The jittered pause before a relay's one retry: <c>base/2 + random(0..base)</c> ms, 0 when the base is 0.</summary>
    private int NextRelayRetryDelayMs()
    {
        int b = RelayRetryBackoffMs;
        return b <= 0 ? 0 : b / 2 + Random.Shared.Next(b + 1);
    }

    /// <summary>The HRESULT a failed relay delivers (DRM_E_CH_BAD_KEY-shaped: "no usable license").</summary>
    internal const int LicenseRelayFailedHr = unchecked((int)0x8004110E);

    /// <summary>Record <paramref name="message"/> on <paramref name="kid"/>'s row. <paramref name="license"/> (0 = any) is the native
    /// handle of the acquisition the message is about: a late failure of a replaced license never stains the fresh row that took
    /// its place (a row whose handle is not yet known still takes it).</summary>
    private void RecordRelayFailure(string kid, string message, ulong license = 0)
    {
        lock (_gate)
        {
            if (_licenses.TryGetValue(kid, out LicenseEntry e) && (license == 0 || e.Handle == 0 || e.Handle == license))
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

/// <summary>
/// Which HRESULTs mean the process's single engine + CDM + D3D11 device can no longer be trusted — the ones the runtime is
/// REPLACED for rather than retried on. Chromium maps the first two to <c>PIPELINE_ERROR_HARDWARE_CONTEXT_RESET</c> and
/// restarts the pipeline with a fresh CDM; the DXGI pair is the D3D11 device itself gone; MF_E_SHUTDOWN is a Media Foundation
/// object that was shut down under a live engine.
/// </summary>
internal static class ProtectedRuntimeFaults
{
    /// <summary>DRM_E_TEE_INVALID_HWDRM_STATE — the hardware-DRM context was reset (sleep/resume, display change).</summary>
    internal const int TeeInvalidHwDrmState = unchecked((int)0x8004CD12);
    /// <summary>DRM_OEM_E_ASD_ACTIVE_DISPLAY_FAIL — what old AMD drivers return instead of the code above.</summary>
    internal const int AsdActiveDisplayFail = unchecked((int)0x8004DD2E);
    /// <summary>DXGI_ERROR_DEVICE_REMOVED.</summary>
    internal const int DxgiDeviceRemoved = unchecked((int)0x887A0005);
    /// <summary>DXGI_ERROR_DEVICE_RESET.</summary>
    internal const int DxgiDeviceReset = unchecked((int)0x887A0007);
    /// <summary>MF_E_SHUTDOWN.</summary>
    internal const int MfShutdown = unchecked((int)0xC00D3E85);
    /// <summary>HRESULT_FROM_WIN32(ERROR_TIMEOUT) - what the native runtime reports (as MF_MEDIA_ENGINE_ERR_DECODE) when a source was
    /// playing with NO frame rendered by the engine for 10 s (F066): a dead swap-chain handle, a stuck topology or a lost device that
    /// never raised an error. The engine is shared by every session, so the runtime is rebuilt rather than the source retried on it.</summary>
    internal const int NoRenderedFrame = unchecked((int)0x800705B4);

    /// <summary>True for an HRESULT after which the runtime must be rebuilt, not retried.</summary>
    internal static bool IsRuntimeReset(int hr)
        => hr is TeeInvalidHwDrmState or AsdActiveDisplayFail or DxgiDeviceRemoved or DxgiDeviceReset or MfShutdown or NoRenderedFrame;
}

/// <summary>Where a license relay's answer goes: the native <c>FgPrLicenseDeliver</c> in production, a recorder in the
/// engine's tests. Called exactly once per challenge, from a pool thread.</summary>
internal interface ILicenseDelivery
{
    /// <summary>Hand <paramref name="license"/> (empty on failure) and <paramref name="hr"/> (0 on success) to the CDM.</summary>
    void Deliver(ReadOnlySpan<byte> license, int hr);
}

/// <summary>The five native calls the runtime itself makes — the real DLL in production (<see cref="PrRuntimeNative"/>),
/// a fake in the engine's tests, so the license cache, the relay and the warm-idle lifetime are testable with no CDM,
/// no GPU, no license server and no window. Sessions call the other half, <see cref="IPrSessionNative"/>.</summary>
internal interface IPrRuntimeNative
{
    /// <summary>Whether the native component can be loaded AND exports the whole runtime ABI (a stale build does not).</summary>
    bool IsAvailable { get; }
    /// <summary><c>FgPrRuntimeCreateOnAdapter</c> with the runtime's event thunk and <paramref name="ctx"/>. <paramref name="adapterLuid"/>
    /// (packed <c>(HighPart &lt;&lt; 32) | LowPart</c>, 0 = default adapter) is the adapter the D3D11 video device is created on.</summary>
    int RuntimeCreate(string storePath, nint ctx, long adapterLuid, out ulong runtime);
    /// <summary><c>FgPrRuntimeSetVideoOutputFormat</c> (F249): the media engine's output format for the runtime the NEXT
    /// <see cref="RuntimeCreate"/> brings up - 0 = BGRA (the default), 1 = NV12 (<see cref="VideoOutputFormat"/>).</summary>
    void SetVideoOutputFormat(int format);
    /// <summary><c>FgPrRuntimeDestroy</c>.</summary>
    void RuntimeDestroy(ulong runtime);
    /// <summary><c>FgPrLicenseAcquire</c> with the runtime's relay thunk and <paramref name="ctx"/>.</summary>
    int LicenseAcquire(ulong runtime, ReadOnlySpan<byte> pssh, string kid, nint ctx, out ulong license);
    /// <summary><c>FgPrLicenseState</c>: 0 pending, 1 usable, 2 expired, a negative HRESULT when failed or when the handle is unknown
    /// (evicted, released). Also marks the license most recently used in the native table.</summary>
    int LicenseState(ulong runtime, ulong license);
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
        "FgPrRuntimeCreateOnAdapter", "FgPrRuntimeDestroy", "FgPrRuntimeUptimeMs", "FgPrRuntimeSetVideoOutputFormat",
        "FgPrLicenseAcquire", "FgPrLicenseState", "FgPrLicenseRelease",
        "FgPrSessionCreate", "FgPrSessionPrefetch", "FgPrSessionAttach", "FgPrSessionDetach", "FgPrSessionDestroy",
        "FgPrSessionPlay", "FgPrSessionPause", "FgPrSessionSeek", "FgPrSessionSetVolume", "FgPrSessionSetRate",
        "FgPrSessionSetStreamSize", "FgPrSessionPlaceOpmWindow", "FgPrSessionSelectRepresentation", "FgPrSessionSnapshot",
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

    public int RuntimeCreate(string storePath, nint ctx, long adapterLuid, out ulong runtime)
    {
        ulong rt = 0;
        int hr = PrNative.FgPrRuntimeCreateOnAdapter(storePath, &ProtectedVideoRuntime.EventThunk, ctx, adapterLuid, &rt);
        runtime = rt;
        return hr;
    }

    public void SetVideoOutputFormat(int format) => PrNative.FgPrRuntimeSetVideoOutputFormat(format);

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

    public int LicenseState(ulong runtime, ulong license) => PrNative.FgPrLicenseState(runtime, license);

    public void LicenseRelease(ulong runtime, ulong license) => PrNative.FgPrLicenseRelease(runtime, license);
}
