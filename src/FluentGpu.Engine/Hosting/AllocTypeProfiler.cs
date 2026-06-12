using System.Diagnostics.Tracing;
using System.Globalization;

namespace FluentGpu.Hosting;

/// <summary>
/// FG_ALLOC_TYPES=1: a per-TYPE allocation profiler. An <see cref="EventListener"/> on the CLR's
/// "Microsoft-Windows-DotNETRuntime" provider captures <c>GCAllocationTick</c> (event id 10) — the runtime emits one
/// per ~100KB allocated, carrying the allocating type's name and the chunk size — and aggregates bytes per type.
/// Once per second it prints "[alloctypes] top: TypeA NN.NKB/s | …" (top 12) to stderr and resets the window, so
/// scroll-time churn can be pinned to a concrete type without an external profiler.
///
/// COST: constructed only when the flag is set (idempotent <see cref="Start"/>). The listener callback runs on a CLR
/// thread and allocates (the payload type-name strings) — this profiler is intentionally self-allocating; its own
/// GCAllocationTick noise is folded into the totals and called out in the header line once. Off by default and never
/// touched when the flag is clear ⇒ zero overhead. Public only so the app entry point (FluentApp.Run) can
/// <see cref="Start"/>/<see cref="Stop"/> it; the report is driven by the host on the frame cadence.
/// </summary>
public sealed class AllocTypeProfiler : EventListener
{
    // GC keyword (0x1) gates GCAllocationTick; Verbose level is required for the per-tick (vs aggregated) variant.
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const int GCAllocationTickEventId = 10;
    private const string RuntimeProviderName = "Microsoft-Windows-DotNETRuntime";

    private static readonly object s_startGate = new();
    private static AllocTypeProfiler? s_instance;

    private const int TopN = 12;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _bytesByType = new(StringComparer.Ordinal);
    // Reused top-N scratch (parallel arrays — a managed-tuple span can't be stackalloc'd; this avoids per-report alloc).
    private readonly string[] _topType = new string[TopN];
    private readonly long[] _topBytes = new long[TopN];
    private long _windowStartTicks;
    private bool _headerEmitted;
    private EventSource? _runtimeSource;   // captured in OnEventSourceCreated so Dispose can disable it

    /// <summary>Construct the singleton listener once. Safe to call repeatedly; only the first call wires the
    /// listener. Call only when FG_ALLOC_TYPES is set.</summary>
    public static void Start()
    {
        if (s_instance is not null) return;
        lock (s_startGate)
        {
            s_instance ??= new AllocTypeProfiler();
        }
    }

    /// <summary>Tear down the listener (idempotent) — keeps headless runs leak-free.</summary>
    public static void Stop()
    {
        AllocTypeProfiler? inst;
        lock (s_startGate)
        {
            inst = s_instance;
            s_instance = null;
        }
        inst?.Dispose();
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // EventListener bases enumerate already-created sources during the ctor; capture + enable the runtime source.
        if (!eventSource.Name.Equals(RuntimeProviderName, StringComparison.Ordinal)) return;
        _runtimeSource = eventSource;
        EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId != GCAllocationTickEventId) return;
        // EventListener can deliver events DURING the base ctor (after EnableEvents in OnEventSourceCreated) before
        // derived field initializers have run — guard the not-yet-constructed dictionary.
        if (_bytesByType is null) return;
        var payload = eventData.Payload;
        var names = eventData.PayloadNames;
        if (payload is null || names is null) return;

        // GCAllocationTick_V4 carries "AllocationAmount64" (ulong, the precise chunk) and "TypeName" (string).
        long amount = 0;
        string? type = null;
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            if (type is null && name.Equals("TypeName", StringComparison.Ordinal))
                type = payload[i] as string;
            else if (name.Equals("AllocationAmount64", StringComparison.Ordinal))
                amount = ToLong(payload[i]);
        }
        if (amount == 0)   // older payloads only expose the 32-bit AllocationAmount
            for (int i = 0; i < names.Count; i++)
                if (names[i].Equals("AllocationAmount", StringComparison.Ordinal)) { amount = ToLong(payload[i]); break; }
        if (type is null || amount <= 0) return;

        lock (_gate)
        {
            _bytesByType.TryGetValue(type, out long v);
            _bytesByType[type] = v + amount;
        }
    }

    private static long ToLong(object? o) => o switch
    {
        ulong u => (long)u,
        long l => l,
        uint ui => ui,
        int i => i,
        _ => 0,
    };

    /// <summary>Emit the once-per-second top-N line if a second has elapsed, then reset. Cheap timestamp check
    /// otherwise. Driven from the host frame loop (only when the flag is on) so it shares the loop's cadence and
    /// needs no extra timer thread.</summary>
    public static void MaybeReport() => s_instance?.MaybeReportInstance();

    private void MaybeReportInstance()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_windowStartTicks == 0) { _windowStartTicks = now; return; }
        double sec = (now - _windowStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (sec < 1.0) return;

        // Snapshot the top N under the lock into the reused scratch, then format outside it.
        int topCount = 0;
        lock (_gate)
        {
            foreach (var kv in _bytesByType)
            {
                // Insertion sort into the fixed top-N (descending) — no per-report allocation.
                if (topCount < TopN)
                {
                    int j = topCount++;
                    while (j > 0 && _topBytes[j - 1] < kv.Value) { _topType[j] = _topType[j - 1]; _topBytes[j] = _topBytes[j - 1]; j--; }
                    _topType[j] = kv.Key; _topBytes[j] = kv.Value;
                }
                else if (kv.Value > _topBytes[topCount - 1])
                {
                    int j = topCount - 1;
                    while (j > 0 && _topBytes[j - 1] < kv.Value) { _topType[j] = _topType[j - 1]; _topBytes[j] = _topBytes[j - 1]; j--; }
                    _topType[j] = kv.Key; _topBytes[j] = kv.Value;
                }
            }
            _bytesByType.Clear();
        }

        var sb = new System.Text.StringBuilder(256);
        sb.Append("[alloctypes]");
        if (!_headerEmitted) { sb.Append(" (sampled via GCAllocationTick ~per-100KB; includes this profiler's own string allocs)"); _headerEmitted = true; }
        sb.Append(" top:");
        for (int i = 0; i < topCount; i++)
        {
            double kbPerSec = _topBytes[i] / sec / 1024.0;
            sb.Append(CultureInfo.InvariantCulture, $" {_topType[i]} {kbPerSec:0.0}KB/s");
            if (i < topCount - 1) sb.Append(" |");
        }
        if (topCount == 0) sb.Append(" (none)");
        Console.Error.WriteLine(sb.ToString());

        _windowStartTicks = now;
    }

    public override void Dispose()
    {
        if (_runtimeSource is not null)
        {
            DisableEvents(_runtimeSource);
            _runtimeSource = null;
        }
        base.Dispose();
    }
}
