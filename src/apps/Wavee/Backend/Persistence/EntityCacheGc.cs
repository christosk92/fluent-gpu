using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Persistence;

// ── the metadata-cache garbage collector (design §C, Wave C) ─────────────────────────────────────────────────────────
// The write gate (Wave B) decides what ENTERS the cache tier; this decides what LEAVES it. Two-stage victim selection
// (Firefox cache2): TTL is the FILTER, LRU is the RANKER. There is no W-TinyLFU/SIEVE machinery — a periodic SQL pass
// IS the whole engine, and every DELETE is an atomic ≤1000-row batch that maintains `cache_bytes` in its own
// transaction. UNCONDITIONAL: there is no env kill switch and no persist-everything fallback (user ruling 2026-07-27).
//
// ONE PASS, in order:
//   0. flush the pending last-access set (a page the user just scrolled must not read as stale)
//   1. MEMBERSHIP GC   — playlists ∉ rootlist ∧ ∉ recent_surfaces ∧ adopted > 14 d ago lose their playlist_items + header
//                        (critique #6). Runs FIRST so the pin table below no longer sees the pins it just retired; the
//                        entities themselves still age out through the normal TTL, exactly as locked decision 11 says.
//   2. PIN TABLE       — §A.3 P0 ∪ recent_surfaces ∪ the one-level entity_refs closure ∪ the caller's snapshot of the
//                        in-memory pins (UI-thread-affine — see PinSnapshotSource).
//   3. EXPIRED SWEEP   — extension rows past `expires_at` + the 7 d ETag-revalidation grace (recurring, not open-only).
//   4. OVERVIEW TTL    — artist_overview older than 7 d since fetched_at, pinned artists exempt.
//   5. ENTITY TTL      — unpinned entities untouched for 30 d.
//   6. BUDGET LRU      — while cache_bytes > budget, oldest-last_access-first down to 0.9 × budget.
//   7. RECLAIM         — incremental_vacuum slices + a TRUNCATE WAL checkpoint.
// Every sweep also honours a 15-minute `updated_at` grace on brand-new rows (critique #11).
//
// CANCELLATION: the pass is a sequence of individually atomic steps and individually atomic DELETE batches, so aborting
// BETWEEN any two of them leaves a fully consistent database (cache_bytes always == SUM(size) of what survived). That is
// what lets app shutdown simply cancel the token and walk away instead of waiting for a mid-flight GC.
public sealed class EntityCacheGc : IDisposable
{
    /// <summary>Unpinned entity TTL (§C.3).</summary>
    public const long EntityTtlSeconds = 30L * 24 * 60 * 60;
    /// <summary>`artist_overview` TTL (§C.3) — a page open re-derives it through the ArtistV4 SWR/etag pass.</summary>
    public const long OverviewTtlSeconds = 7L * 24 * 60 * 60;
    /// <summary>Membership GC horizon (locked decision 11).</summary>
    public const long MembershipTtlSeconds = 14L * 24 * 60 * 60;
    /// <summary>Freelist pages reclaimed per pass (§C.7 — slices on idle, never a routine full VACUUM).</summary>
    public const int VacuumPagesPerPass = 200;

    const int FirstPassDelayMs = 30_000;                  // warm + 30 s (§C.6)
    const int PeriodMs = 6 * 60 * 60 * 1000;              // then every 6 h
    const int PinSnapshotTimeoutMs = 15_000;              // never wedge on a UI thread that is gone

    readonly SqliteColdStore _cold;
    readonly CachedStore _store;
    readonly IWaveeLog? _log;
    readonly Func<ISet<string>>? _uiPins;                 // Services.BuildPinSet — UI-THREAD-AFFINE, never called here
    readonly CancellationTokenSource _cts = new();
    Action<Action>? _post;                                // the UI-thread marshaller (the MemoryGovernor's `post`)
    long _budgetBytes;
    int _started;

    /// <param name="uiPins">The UI-thread-affine pin snapshot factory (<c>Services.BuildPinSet</c>: now-playing, queue,
    /// context, the detail caches). It is INVOKED ONLY through the marshaller handed to <see cref="Start"/>.</param>
    public EntityCacheGc(SqliteColdStore cold, CachedStore store, IWaveeLog? log = null,
                         Func<ISet<string>>? uiPins = null, long budgetBytes = 0)
    {
        _cold = cold;
        _store = store;
        _log = log;
        _uiPins = uiPins;
        _budgetBytes = budgetBytes > 0 ? budgetBytes : SqliteColdStore.DefaultCacheBudgetBytes;
    }

    /// <summary>The live cache-tier byte budget. Settings writes it here AND to the `cache_budget_bytes` meta row, so a
    /// GC started before the user changed it still picks the new value up on its next pass.</summary>
    public long BudgetBytes
    {
        get => Interlocked.Read(ref _budgetBytes);
        set { if (value > 0) { Interlocked.Exchange(ref _budgetBytes, value); try { _cold.SetCacheBudgetBytes(value); } catch (Exception) { } } }
    }

    /// <summary>The last completed pass's report (diagnostics / tests).</summary>
    public EntityGcReport LastReport { get; private set; }

    /// <summary>Arm the ordered background sequence: warm → +30 s → GC pass → the one-time post-migration full VACUUM →
    /// then a 6 h period, each run re-snapshotting the in-memory pins on the UI thread. NEVER on the UI thread itself,
    /// never before first paint (the caller wires this from the app-mount effect, next to the MemoryGovernor timer).
    /// <paramref name="post"/> is that same UI-thread marshaller.</summary>
    public void Start(Action<Action> post)
    {
        if (post is null || Interlocked.Exchange(ref _started, 1) != 0) return;
        _post = post;
        // Environment.Exit / a normal process teardown must not wait on a mid-flight pass: cancel and walk away. Safe
        // because every batch is its own transaction (see the class comment).
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        _ = Task.Run(RunLoopAsync);
    }

    void OnProcessExit(object? sender, EventArgs e) { try { _cts.Cancel(); } catch (Exception) { } }

    async Task RunLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await _store.WarmComplete.ConfigureAwait(false);
            LogBootMarks();
            await Task.Delay(FirstPassDelayMs, ct).ConfigureAwait(false);
            await RunOnceAsync(ct).ConfigureAwait(false);

            // The one-time post-migration reclaim (v4→v5 dropped the whole legacy entity generation). Gated on the
            // `vacuum_pending` meta flag inside the store, so a crash before it runs just defers it to the next launch.
            // Deliberately AFTER the first GC: vacuuming before the big delete would compact pages we are about to free.
            if (!ct.IsCancellationRequested)
                try { if (_cold.RunFullVacuumIfPending()) _log?.Info("persist", "cache.vacuum", "one-time post-migration VACUUM done"); }
                catch (Exception ex) { _log?.Warn("persist", "cache.vacuum.failed", ex.Message); }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PeriodMs, ct).ConfigureAwait(false);
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _log?.Warn("persist", "cache.gc.loop", "metadata cache GC loop stopped: " + ex.Message); }
    }

    /// <summary>Snapshot the pins (UI thread) and run ONE pass off it. Public so a diagnostics action / a test can force
    /// a pass; safe to call concurrently with nothing else (the SQL side serializes on the cold store's writer lock).</summary>
    public async Task<EntityGcReport> RunOnceAsync(CancellationToken ct)
    {
        var exempt = await SnapshotPinsAsync(ct).ConfigureAwait(false);
        return RunPass(exempt, ct);
    }

    // ── the UI-thread pin snapshot (critique #10) ────────────────────────────────────────────────────────────────────
    // Services.BuildPinSet reads Peek()ed playback signals AND LibraryStore's detail caches, which are UI-thread-affine
    // (the MemoryGovernor deliberately marshals its own Trim through the same `post` for exactly this reason). So: the
    // GC NEVER calls BuildPinSet on its own thread — it posts a closure to the UI thread, waits for the frozen HashSet,
    // and then owns it exclusively. The CachedStore mirrors are lock-guarded and merged in off-thread.
    async Task<HashSet<string>> SnapshotPinsAsync(CancellationToken ct)
    {
        var pins = new HashSet<string>(StringComparer.Ordinal);
        var post = _post;
        if (post is not null && _uiPins is not null)
        {
            var tcs = new TaskCompletionSource<ISet<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                post(() => { try { tcs.TrySetResult(_uiPins()); } catch (Exception) { tcs.TrySetResult(null); } });
                // A UI thread that never runs the callback (torn down / mid-shutdown) must not wedge the GC forever.
                var done = await Task.WhenAny(tcs.Task, Task.Delay(PinSnapshotTimeoutMs, ct)).ConfigureAwait(false);
                if (done == tcs.Task && tcs.Task.Result is { } ui) foreach (var u in ui) if (u.Length > 0) pins.Add(u);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* an unavailable UI thread just means a smaller exempt list, never a wrong one */ }
        }
        _store.SnapshotPinMirrors(pins);   // thread-safe half: recent surfaces, rootlist, adopted, members
        return pins;
    }

    // ── one pass ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Run one full GC pass against an ALREADY-FROZEN exempt set. Synchronous (it is all writer-lane SQL) and
    /// cancellable between every step.</summary>
    public EntityGcReport RunPass(IReadOnlyCollection<string> exempt, CancellationToken ct)
    {
        long startTicks = Environment.TickCount64;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long graceBefore = now - SqliteColdStore.GcNewRowGraceSeconds;
        var r = new EntityGcReport();
        try
        {
            _store.FlushTouches();   // step 0 — recency before staleness
            // Mirror the live budget into `cache_budget_bytes` so the Settings readout and any offline inspection agree
            // with what this pass actually enforced (the app setting is the user's choice; the meta row is the record).
            try { _cold.SetCacheBudgetBytes(BudgetBytes); } catch (Exception) { }

            // 1. membership GC (independent of the pin table; it defines part of it)
            if (!ct.IsCancellationRequested)
            {
                var purged = _cold.GcSweepMemberships(now - MembershipTtlSeconds);
                r.MembershipPlaylists = purged.Count;
                if (purged.Count > 0) _store.OnMembershipsPurged(purged);
            }

            _cold.GcBeginPass(exempt);   // 2. the pin table
            r.PinnedUris = _cold.GcPinnedCount();

            // 3. expired extensions (recurring — critique #12's +7 d grace lives inside the sweep)
            if (!ct.IsCancellationRequested)
            {
                var (rows, bytes) = _cold.SweepExpiredExtensionsNow();
                r.ExpiredRows += rows;
                r.BytesFreed += bytes;
            }

            // 4. artist_overview TTL
            if (!ct.IsCancellationRequested)
            {
                var (rows, bytes) = _cold.GcSweepArtistOverviews(now - OverviewTtlSeconds);
                r.TtlRows += rows;
                r.BytesFreed += bytes;
            }

            // 5. unpinned entity TTL
            if (!ct.IsCancellationRequested)
            {
                var (rows, bytes) = _cold.GcSweepUnpinnedEntities(now - EntityTtlSeconds, graceBefore, ct);
                r.TtlRows += rows;
                r.BytesFreed += bytes;
            }

            // 6. byte-budget LRU
            if (!ct.IsCancellationRequested)
            {
                var (rows, bytes) = _cold.GcEnforceBudget(BudgetBytes, graceBefore, ct);
                r.BudgetRows += rows;
                r.BytesFreed += bytes;
            }
        }
        catch (Exception ex)
        {
            r.Error = ex.Message;
            _log?.Warn("persist", "cache.gc.failed", "metadata cache GC pass failed: " + ex.Message);
        }
        finally
        {
            try { _cold.GcEndPass(); } catch (Exception) { }
        }

        // 7. reclaim — slices, never a routine full VACUUM (§C.7: the Spotify SSD-wear incident).
        if (!ct.IsCancellationRequested && r.TotalRows > 0)
        {
            try { _cold.RunIncrementalVacuum(VacuumPagesPerPass); } catch (Exception) { }
            try { _cold.CheckpointWal(); } catch (Exception) { }
        }

        r.DurationMs = Environment.TickCount64 - startTicks;
        r.Cancelled = ct.IsCancellationRequested;
        LastReport = r;
        LogReport(r);
        return r;
    }

    // ── metrics (§G) ─────────────────────────────────────────────────────────────────────────────────────────────────
    void LogReport(in EntityGcReport r)
    {
        if (_log is null) return;
        _log.Event(WaveeLogLevel.Info, "persist", "cache.gc", "metadata cache GC", elapsedMs: r.DurationMs, fields:
        [
            WaveeLogField.Of("expired", r.ExpiredRows),
            WaveeLogField.Of("ttl", r.TtlRows),
            WaveeLogField.Of("budget", r.BudgetRows),
            WaveeLogField.Of("membership", r.MembershipPlaylists),
            WaveeLogField.Of("bytes_freed", r.BytesFreed),
            WaveeLogField.Of("pinned", r.PinnedUris),
            WaveeLogField.Of("cancelled", r.Cancelled),
        ]);
    }

    void LogBootMarks()
    {
        if (_log is null) return;
        _log.Event(WaveeLogLevel.Info, "persist", "boot.persistence", "cold tier ready", fields:
        [
            WaveeLogField.Of("boot.sqlite_open_ms", OpenMillis),
            WaveeLogField.Of("boot.identity_load_ms", IdentityLoadMillis),
            WaveeLogField.Of("boot.warm_ms", _store.WarmMillis),
            WaveeLogField.Of("boot.warm_rows", _store.WarmRows),
        ]);
    }

    /// <summary>Startup marks measured by the composition root around the two ctors (§G). Set before <see cref="Start"/>.</summary>
    public long OpenMillis { get; set; }
    public long IdentityLoadMillis { get; set; }

    public void Dispose()
    {
        try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch (Exception) { }
        try { _cts.Cancel(); } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>One GC pass's report (§G "GC report per run"): rows deleted by category, bytes freed, duration, pin count.</summary>
public struct EntityGcReport
{
    public int ExpiredRows;          // extension rows past expires_at + grace
    public int TtlRows;              // unpinned entities + artist_overview rows past their TTL
    public int BudgetRows;           // entities evicted by the byte-budget LRU
    public int MembershipPlaylists;  // playlists whose membership + header were purged
    public long BytesFreed;
    public long DurationMs;
    public long PinnedUris;
    public bool Cancelled;
    public string? Error;

    public readonly int TotalRows => ExpiredRows + TtlRows + BudgetRows;
}
