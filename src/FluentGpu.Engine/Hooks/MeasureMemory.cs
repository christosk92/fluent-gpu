using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

/// <summary>
/// E17c — the remembered measurements behind keyed measurement (<c>UseMeasuredWidth(key)</c>, <c>UseMeasuredBounds(key)</c>,
/// the keyed <c>Responsive.Of</c> overloads). A keyed measurement writes its last arranged rect here; a component that mounts
/// with the same key starts from that rect instead of 0 or a fixed fallback, so its FIRST render already builds at the size it
/// will be laid out at. That is what lets a remounted page (a theme transaction's new root, a revisit) settle in one pass.
/// <list type="bullet">
/// <item><b>Keys are semantic</b>: a region role composed onto the ambient <see cref="Scope"/> (the application provides the route
/// instance there, e.g. <c>"tab0/artist:42/"</c>), so the same region of the same page finds its size again after a remount.
/// The full key is composed once, when the hook cell (or keyed box) is created; it never allocates per frame.</item>
/// <item><b>Bounded</b>: at most <see cref="Capacity"/> keys; the least recently captured one is dropped when a new key would
/// exceed it.</item>
/// <item><b>Change journal</b>: every measured value that changes (keyed or not) bumps a process-wide serial, and while a settle is
/// running (<c>AppHost.Settle</c>) the key (or, for an unkeyed hook, its call site) is recorded, so a settle that hits its cap can
/// name the regions that did not stabilise.</item>
/// </list>
/// UI thread only. A capture of an unchanged value and a lookup allocate nothing.
/// </summary>
public static class MeasureMemory
{
    /// <summary>The ambient key prefix of keyed measurement: the application provides the route instance here (a page slot's
    /// identity), and every keyed measurement below composes its role key onto it. Default empty (the role key alone).</summary>
    public static readonly Context<string> Scope = new("");

    /// <summary>The most keys remembered at once (least recently captured dropped first).</summary>
    public const int Capacity = 1024;

    private struct Slot
    {
        public RectF Bounds;
        public long Stamp;
    }

    private static readonly Dictionary<string, Slot> s_slots = new(System.StringComparer.Ordinal);
    private static long s_stamp;
    private static long s_changeSerial;

    // The settle journal (AppHost.Settle): on only while a settle runs; keys of measurements that changed since the last reset.
    private static int s_journalDepth;
    private static readonly List<string> s_journal = new();
    private static readonly HashSet<string> s_journalSet = new(System.StringComparer.Ordinal);

    /// <summary>The number of remembered keys.</summary>
    public static int Count => s_slots.Count;

    /// <summary>The full key of <paramref name="role"/> under <paramref name="scope"/> (<c>scope + role</c>; the role alone when the
    /// scope is empty). Allocates the composed string: call it once per mount, never per frame.</summary>
    public static string Compose(string? scope, string role)
        => string.IsNullOrEmpty(scope) ? role : string.Concat(scope, role);

    /// <summary>The remembered arranged rect of <paramref name="key"/> (a full key, see <see cref="Compose"/>).</summary>
    public static bool TryGet(string key, out RectF bounds)
    {
        if (s_slots.TryGetValue(key, out var slot)) { bounds = slot.Bounds; return true; }
        bounds = default;
        return false;
    }

    /// <summary>The remembered size of <paramref name="key"/>.</summary>
    public static bool TryGetSize(string key, out Size2 size)
    {
        if (s_slots.TryGetValue(key, out var slot)) { size = new Size2(slot.Bounds.W, slot.Bounds.H); return true; }
        size = default;
        return false;
    }

    /// <summary>Remember <paramref name="size"/> for <paramref name="key"/> (the remembered origin is kept). A changed value is a
    /// measured-decision change: it bumps the change serial and, during a settle, is journaled.</summary>
    public static void Capture(string key, Size2 size)
    {
        RectF origin = s_slots.TryGetValue(key, out var slot) ? slot.Bounds : default;
        Capture(key, new RectF(origin.X, origin.Y, size.Width, size.Height));
    }

    /// <summary>Remember the arranged <paramref name="bounds"/> of <paramref name="key"/>. A changed value bumps the change serial
    /// and, during a settle, is journaled. An unchanged one only refreshes the key's recency (no allocation).</summary>
    public static void Capture(string key, RectF bounds)
    {
        if (Remember(key, bounds)) NoteChanged(key);
    }

    /// <summary>Forget <paramref name="key"/>. Returns true when it was remembered.</summary>
    public static bool Forget(string key) => s_slots.Remove(key);

    /// <summary>Forget every key.</summary>
    public static void Clear() => s_slots.Clear();

    /// <summary>Store without journaling (a measured hook journals its own signal change). True when the stored value changed
    /// (or the key is new).</summary>
    internal static bool Remember(string key, RectF bounds)
    {
        ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(s_slots, key);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot))
        {
            slot.Stamp = ++s_stamp;
            if (slot.Bounds == bounds) return false;
            slot.Bounds = bounds;
            return true;
        }
        if (s_slots.Count >= Capacity) EvictOldest();
        s_slots[key] = new Slot { Bounds = bounds, Stamp = ++s_stamp };
        return true;
    }

    private static void EvictOldest()
    {
        string? oldest = null;
        long min = long.MaxValue;
        foreach (var kv in s_slots)
            if (kv.Value.Stamp < min) { min = kv.Value.Stamp; oldest = kv.Key; }
        if (oldest is not null) s_slots.Remove(oldest);
    }

    // ── the change serial and the settle journal ──────────────────────────────────────────────────────────────────────

    /// <summary>Bumped on every measured-value change (keyed or not). A settle compares it across a pass.</summary>
    internal static long ChangeSerial => s_changeSerial;

    /// <summary>A keyed measurement changed.</summary>
    internal static void NoteChanged(string key)
    {
        s_changeSerial++;
        if (s_journalDepth > 0 && s_journalSet.Add(key)) s_journal.Add(key);
    }

    /// <summary>An UNKEYED measured hook changed; during a settle its call site names it (<c>File.cs:line</c>, allocated only
    /// then).</summary>
    internal static void NoteChangedUnkeyed(string? file, int line)
    {
        s_changeSerial++;
        if (s_journalDepth == 0) return;
        string site = "unkeyed " + (file is null ? "?" : Path.GetFileName(file)) + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s_journalSet.Add(site)) s_journal.Add(site);
    }

    internal static void BeginJournal() { if (s_journalDepth++ == 0) ResetJournal(); }

    internal static void EndJournal()
    {
        if (s_journalDepth == 0) return;
        if (--s_journalDepth == 0) ResetJournal();
    }

    /// <summary>Clear the journal (a settle resets it at the start of each pass, so it names the keys of the LAST pass).</summary>
    internal static void ResetJournal()
    {
        s_journal.Clear();
        s_journalSet.Clear();
    }

    internal static int JournalCount => s_journal.Count;

    internal static void CopyJournal(List<string> into) => into.AddRange(s_journal);
}
