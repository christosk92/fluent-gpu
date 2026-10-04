using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>Which kind of media memory owner a <see cref="MediaCensusRow"/> names.</summary>
public enum MediaCensusKind : byte
{
    /// <summary>A live <c>VideoMediaEngine</c> (Media Foundation Media Engine, its own D3D11 video device lease and swap chain).</summary>
    VideoEngine = 0,
    /// <summary>A live protected (PlayReady) session on the native runtime: its segment store, attached or prepared.</summary>
    ProtectedSession = 1,
}

/// <summary>What one live media owner says about itself when the census samples it. <paramref name="Id"/> is the owner's own
/// ordinal (an engine's creation number, a session's native handle) for the log lines; the natural size is 0x0 until the source's
/// metadata arrived; <paramref name="StoreBytes"/> is the protected session's CPU segment store (0 for an engine).</summary>
public readonly record struct MediaCensusRow(MediaCensusKind Kind, long Id, int NaturalWidth, int NaturalHeight, long StoreBytes, bool Attached);

/// <summary>
/// One census sample of the media stack (F197 / F235): what <see cref="MediaCensus"/> could name at the moment, as plain numbers.
/// <see cref="Format"/> is the one pure text rendering both <c>[memcensus]</c> and Wavee's <c>mem.sample</c> print.
/// <para><b>What is NOT counted:</b> the PlayReady Media Foundation Protected Media Path decodes inside <c>mfpmp.exe</c>, a
/// SEPARATE process, so no in-process census can see that memory whatever the adapter; the line says so
/// (<c>pmpDecode=not-counted</c>) rather than implying the protected rows are the whole story. The segment store is CPU memory
/// (already inside the working set), not VRAM.</para>
/// </summary>
public readonly record struct MediaCensusSnapshot(
    int VideoEngines, string VideoEngineSizes,
    int ProtectedSessions, int ProtectedAttached, long ProtectedStoreBytes,
    int PreparedSessions, bool ProtectedRuntimeUp, long ProtectedRuntimeStarts,
    int DualHandleSlots, int DualHandlePeak)
{
    /// <summary>One compact <c>key=value</c> run (no leading or trailing space), identical in every sink.</summary>
    public string Format()
    {
        var sb = new StringBuilder(192);
        sb.Append(CultureInfo.InvariantCulture, $"videoEngines={VideoEngines}");
        if (!string.IsNullOrEmpty(VideoEngineSizes)) sb.Append('[').Append(VideoEngineSizes).Append(']');
        sb.Append(CultureInfo.InvariantCulture,
            $" protectedSessions={ProtectedSessions} attached={ProtectedAttached} storeBytes={ProtectedStoreBytes} prepared={PreparedSessions}");
        sb.Append(" protectedRuntime=").Append(ProtectedRuntimeUp ? "up" : "down")
          .Append(CultureInfo.InvariantCulture, $"(starts={ProtectedRuntimeStarts})");
        sb.Append(CultureInfo.InvariantCulture, $" dualHandleSlots={DualHandleSlots} dualHandlePeak={DualHandlePeak}");
        sb.Append(" pmpDecode=not-counted");
        return sb.ToString();
    }
}

/// <summary>
/// The process-wide census of the media stack's memory owners (F197) and of the one-surface-per-player invariant across EVERY
/// window (F235). MemCensus (<c>--fg mem</c>) and Wavee's always-on <c>mem.sample</c> print <see cref="Capture"/>; before it
/// the per-rebuild engine leak, the runtime kept warm by the keeper and up to 64 MiB of segment store could be neither seen nor
/// attributed from a field log.
/// <para><b>Owners register themselves.</b> A <c>VideoMediaEngine</c> and a protected session each <see cref="Register"/> a
/// describe callback for their lifetime; the census calls it at sample time, so nothing is maintained per frame. Prepared
/// sessions and the native runtime's up/down are plain counters the backend / runtime note at their transitions.</para>
/// <para><b>Dual handles.</b> The host registers each window's <see cref="VideoSurfaceRegistry"/> (<see cref="RegisterRegistry"/>);
/// <see cref="CountDualHandleSlots()"/> scans the handles every LIVE slot of every registry carries (a fixed stack buffer, no
/// allocation, through the registry's any-thread mirror, so a window reaped mid-scan is harmless) and counts the slots that repeat
/// a handle another slot already carries: two elements writing one player's swap chain. The Debug-only
/// <see cref="OneSurfacePerPlayerGuard"/> scans one registry and is compiled out of the shipping binary.</para>
/// <para>Thread-safe: owners register and unregister from engine / UI / pool threads, the sampler reads from the UI thread.</para>
/// </summary>
public static class MediaCensus
{
    private sealed class Entry
    {
        public int Token;
        public MediaCensusKind Kind;
        public Func<MediaCensusRow> Describe = null!;
    }

    /// <summary>Most windows scanned for dual handles (the main window plus its pop-outs).</summary>
    public const int MaxRegistries = 8;

    private static readonly Lock s_gate = new();
    private static readonly List<Entry> s_entries = new();
    private static readonly VideoSurfaceRegistry?[] s_registries = new VideoSurfaceRegistry?[MaxRegistries];
    private static int s_nextToken;
    private static int s_prepared;
    private static int s_runtimeUp;
    private static long s_runtimeStarts;
    private static int s_dualPeak;

    /// <summary>Register a live owner; <paramref name="describe"/> is called (on the sampling thread, never under the census lock)
    /// whenever <see cref="Capture"/> runs, until <see cref="Unregister"/>. Returns the token to unregister with.</summary>
    public static int Register(MediaCensusKind kind, Func<MediaCensusRow> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        lock (s_gate)
        {
            int token = ++s_nextToken;
            s_entries.Add(new Entry { Token = token, Kind = kind, Describe = describe });
            return token;
        }
    }

    /// <summary>Drop an owner registered by <see cref="Register"/>. Idempotent; an unknown or zero token is ignored.</summary>
    public static void Unregister(int token)
    {
        if (token == 0) return;
        lock (s_gate)
        {
            for (int i = 0; i < s_entries.Count; i++)
                if (s_entries[i].Token == token) { s_entries.RemoveAt(i); return; }
        }
    }

    /// <summary>How many owners of <paramref name="kind"/> are registered right now (the number the always-on create / destroy
    /// lines print).</summary>
    public static int Count(MediaCensusKind kind)
    {
        int n = 0;
        lock (s_gate)
        {
            for (int i = 0; i < s_entries.Count; i++)
                if (s_entries[i].Kind == kind) n++;
        }
        return n;
    }

    /// <summary>A protected session was prepared (+1) or left the prepared state (-1: opened, expired or disposed).</summary>
    public static void NotePrepared(int delta) => Interlocked.Add(ref s_prepared, delta);

    /// <summary>The protected runtime's native instance came up (<c>true</c>) or was torn down (<c>false</c>).</summary>
    public static void NoteProtectedRuntime(bool up)
    {
        Volatile.Write(ref s_runtimeUp, up ? 1 : 0);
        if (up) Interlocked.Increment(ref s_runtimeStarts);
    }

    /// <summary>Add a window's registry to the dual-handle scan (the host does this once per window). False when the table is full
    /// or the registry is already present.</summary>
    public static bool RegisterRegistry(VideoSurfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        lock (s_gate)
        {
            int free = -1;
            for (int i = 0; i < s_registries.Length; i++)
            {
                if (ReferenceEquals(s_registries[i], registry)) return false;
                if (free < 0 && s_registries[i] is null) free = i;
            }
            if (free < 0) return false;
            s_registries[free] = registry;
            return true;
        }
    }

    /// <summary>Remove a window's registry (its host is being disposed or reaped). Idempotent.</summary>
    public static void UnregisterRegistry(VideoSurfaceRegistry registry)
    {
        lock (s_gate)
        {
            for (int i = 0; i < s_registries.Length; i++)
                if (ReferenceEquals(s_registries[i], registry)) s_registries[i] = null;
        }
    }

    /// <summary>The number of live slots, across every registered window, that carry a handle another live slot already carries
    /// (0 = one slot per swap chain, the invariant). Two slots on one handle count 1. Allocation-free.</summary>
    public static int CountDualHandleSlots()
    {
        int dual;
        lock (s_gate) dual = CountDualHandleSlots(s_registries);
        int peak = Volatile.Read(ref s_dualPeak);
        while (dual > peak)
        {
            int seen = Interlocked.CompareExchange(ref s_dualPeak, dual, peak);
            if (seen == peak) break;
            peak = seen;
        }
        return dual;
    }

    /// <summary>The worst <see cref="CountDualHandleSlots()"/> reading since the process started: a transient dual writer that was gone
    /// again by the next sample still shows here.</summary>
    public static int DualHandlePeak => Volatile.Read(ref s_dualPeak);

    /// <summary>The dual-handle count over exactly <paramref name="registries"/> (null entries skipped, at most
    /// <see cref="MaxRegistries"/> read): the pure core, usable without the process-wide table. Allocation-free.</summary>
    public static int CountDualHandleSlots(VideoSurfaceRegistry?[] registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        Span<nuint> handles = stackalloc nuint[MaxRegistries * VideoSurfaceRegistry.MaxSurfaces];
        int n = 0;
        int limit = Math.Min(registries.Length, MaxRegistries);
        for (int r = 0; r < limit; r++)
            if (registries[r] is { } registry) n += registry.CopyLiveHandles(handles[n..]);
        return CountRepeats(handles[..n]);
    }

    /// <summary>How many entries of <paramref name="handles"/> repeat an EARLIER non-zero entry. Pure; a fixed O(n^2) scan over at
    /// most <see cref="MaxRegistries"/> x 16 values.</summary>
    internal static int CountRepeats(ReadOnlySpan<nuint> handles)
    {
        int repeats = 0;
        for (int i = 1; i < handles.Length; i++)
        {
            nuint h = handles[i];
            if (h == 0) continue;
            for (int j = 0; j < i; j++)
                if (handles[j] == h) { repeats++; break; }
        }
        return repeats;
    }

    /// <summary>Sample every registered owner now. Calls each owner's describe callback (a throwing one is skipped), so it is for a
    /// diagnostic cadence (a census interval, a 5 s memory sample), never per frame.</summary>
    public static MediaCensusSnapshot Capture()
    {
        Entry[] entries;
        lock (s_gate) entries = s_entries.ToArray();
        int engines = 0, sessions = 0, attached = 0;
        long store = 0;
        StringBuilder? sizes = null;
        for (int i = 0; i < entries.Length; i++)
        {
            MediaCensusRow row;
            try { row = entries[i].Describe(); }
            catch (Exception) { continue; }
            if (row.Kind == MediaCensusKind.VideoEngine)
            {
                engines++;
                sizes ??= new StringBuilder(32);
                if (sizes.Length > 0) sizes.Append(',');
                sizes.Append(CultureInfo.InvariantCulture, $"{row.NaturalWidth}x{row.NaturalHeight}");
            }
            else
            {
                sessions++;
                if (row.Attached) attached++;
                store += Math.Max(0, row.StoreBytes);
            }
        }
        int dual = CountDualHandleSlots();
        return new MediaCensusSnapshot(engines, sizes?.ToString() ?? "", sessions, attached, store,
            Math.Max(0, Volatile.Read(ref s_prepared)), Volatile.Read(ref s_runtimeUp) != 0, Interlocked.Read(ref s_runtimeStarts),
            dual, DualHandlePeak);
    }

    /// <summary>Test seam: forget every owner, registry and counter.</summary>
    internal static void ResetForTest()
    {
        lock (s_gate)
        {
            s_entries.Clear();
            Array.Clear(s_registries);
        }
        Volatile.Write(ref s_prepared, 0);
        Volatile.Write(ref s_runtimeUp, 0);
        Interlocked.Exchange(ref s_runtimeStarts, 0);
        Volatile.Write(ref s_dualPeak, 0);
    }
}
