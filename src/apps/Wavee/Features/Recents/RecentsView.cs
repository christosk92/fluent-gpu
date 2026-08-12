using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee;

/// <summary>The Recents page's PURE half — every decision the page makes that is not a rendered element.
///
/// Split out of <c>RecentsPage</c> for the same reason as <c>HomeSectionPaging</c>/<c>HomeWashSource</c>: these are the
/// rules a reviewer wants pinned (which chips exist, what a chip filters, which URIs a viewport owes the network, how
/// old a play reads), and none of them need a window, a GPU or a service. Engine-free by construction — System +
/// <c>Wavee.Core</c> only — so <c>RecentsViewTests</c> drives the REAL rules rather than a copy of them.
///
/// One invariant runs through the whole file: a recents ROW is a POINTER. Title/Subtitle/Image are null on a freshly
/// fetched row by design, so nothing here may invent a string — every display fact either comes from the wire
/// (<c>ChildCount</c>, <c>PlayedAtMs</c>, <c>ContentType</c>) or from the culture's own date/number tables.</summary>
static class RecentsView
{
    /// <summary>How many URIs one viewport-driven hydration request may carry. The realized window (rows + overscan) is
    /// a few dozen rows; the cap only bounds a pathological jump-scroll that realizes a very tall window at once.</summary>
    public const int BatchCap = 64;

    // ── content-type chips ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The distinct <c>content_type_*</c> tokens present in the list, in FIRST-SEEN (wire) order.
    ///
    /// Derived from the rows, never from a fixed table: the wire carries the concept as a key suffix
    /// (<c>content_type_music</c> / <c>content_type_podcasts</c>), and a list that contains only music must not offer a
    /// podcast chip that can filter to nothing. Rows with no content type contribute nothing — they stay visible under
    /// every chip because "All" is the only lens that claims them.</summary>
    public static IReadOnlyList<string> ContentTypes(IReadOnlyList<RecentsRow> rows)
    {
        if (rows.Count == 0) return Array.Empty<string>();
        var seen = new List<string>(4);
        for (int i = 0; i < rows.Count; i++)
        {
            string? t = rows[i].ContentType;
            if (string.IsNullOrEmpty(t)) continue;
            bool known = false;
            for (int j = 0; j < seen.Count; j++)
                if (string.Equals(seen[j], t, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (!known) seen.Add(t);
        }
        return seen;
    }

    /// <summary>The chip predicate. A null selection is "All" and matches every row (including one the server gave no
    /// content type at all); a selected token matches only rows carrying it.</summary>
    public static bool Matches(RecentsRow row, string? token)
        => token is null || string.Equals(row.ContentType, token, StringComparison.OrdinalIgnoreCase);

    /// <summary>The display map: display index → index into <paramref name="rows"/>, in wire order.
    ///
    /// Filtering is CLIENT-SIDE and nothing else: no request in the whole captured session carries a filter parameter,
    /// so a chip change must never reach the network — it re-cuts the loaded snapshot and that is all.</summary>
    public static int[] Filter(IReadOnlyList<RecentsRow> rows, string? token)
    {
        if (token is null)
        {
            var all = new int[rows.Count];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return all;
        }
        var kept = new List<int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            if (Matches(rows[i], token)) kept.Add(i);
        return kept.ToArray();
    }

    // ── hydration targets ─────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The ONE uri a row is hydrated from, or null when it names nothing fetchable.
    ///
    /// <c>ContextUri</c> first — that is the entity the card stands for and the thing opening the row navigates to. A
    /// single-context group header carries no uri of its own (the wire leaves it empty), and those rows are rendered
    /// from their <c>group_metadata</c> children, so the first child is what the network is asked for. <c>Uri</c> is
    /// the last resort for an ungrouped single.</summary>
    public static string? HydrationUri(RecentsRow row)
    {
        if (row.ContextUri is { Length: > 0 } ctx) return ctx;
        var kids = row.ChildUris;
        if (kids is not null)
            for (int i = 0; i < kids.Count; i++)
                if (kids[i] is { Length: > 0 } child) return child;
        return row.Uri.Length > 0 ? row.Uri : null;
    }

    /// <summary>The entity kind the hydration uri points at — what decides whether a row is asked for the entity-header
    /// trait bundle or for the track bundle.</summary>
    public static RecentsEntityKind TargetKind(RecentsRow row)
    {
        string? uri = HydrationUri(row);
        return uri is null ? RecentsEntityKind.Unknown : RecentsList.EntityKindOf(uri);
    }

    /// <summary>Collect the URIs a realized window still owes the network.
    ///
    /// <paramref name="lastExclusive"/> matches the engine's contract (<c>VirtualListEl.OnVisibleRange</c> reports the
    /// realized window with an EXCLUSIVE end, overscan halo included). <paramref name="pending"/> answers "does this uri
    /// still need a request?" — it is what makes the rule request MISSES ONLY: a uri already hydrated, already in flight,
    /// or already answered-with-nothing is never asked for twice. Duplicates inside one window collapse too, which
    /// matters because ~1,388 uris repeat across a real recents list.
    ///
    /// Returns the number appended to <paramref name="into"/>; the range is clamped, so a stale range from a list that
    /// has since shrunk can never index out of bounds.</summary>
    public static int CollectRange(IReadOnlyList<RecentsRow> rows, IReadOnlyList<int> map, int first, int lastExclusive,
                                   Func<string, bool> pending, List<string> into, int cap = BatchCap)
    {
        int added = 0;
        if (cap <= 0) return 0;
        int lo = Math.Max(0, first);
        int hi = Math.Min(map.Count, lastExclusive);
        for (int i = lo; i < hi && added < cap; i++)
        {
            int r = map[i];
            if ((uint)r >= (uint)rows.Count) continue;
            string? uri = HydrationUri(rows[r]);
            if (uri is null || !pending(uri)) continue;
            bool dup = false;
            for (int j = 0; j < into.Count; j++)
                if (string.Equals(into[j], uri, StringComparison.Ordinal)) { dup = true; break; }
            if (dup) continue;
            into.Add(uri);
            added++;
        }
        return added;
    }

    /// <summary>Which rows may claim the shared-element (connected-animation) tag for their cover.
    ///
    /// The morph key is derived from the entity uri, and uris REPEAT down a recents list (play one playlist on three
    /// different days and it heads three groups). Two live nodes carrying the same MorphId is a duplicate-key bug, not a
    /// nicer animation — so only the FIRST occurrence of each uri, i.e. the most recent play, is tagged. Every later
    /// occurrence renders an ordinary untagged cover.</summary>
    public static bool[] FirstOccurrence(IReadOnlyList<RecentsRow> rows)
    {
        var flags = new bool[rows.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            string? uri = HydrationUri(rows[i]);
            if (uri is null) continue;
            flags[i] = seen.Add(uri);
        }
        return flags;
    }

    // ── formatting (culture tables only — no authored copy) ───────────────────────────────────────────────────────────
    /// <summary>When a row was played, as compactly as the culture allows: today → the time, the last week → the
    /// abbreviated weekday, this year → abbreviated month + day, older → the culture's short date.
    ///
    /// Deliberately built out of <see cref="CultureInfo"/>'s own tables rather than authored strings: the Recents page
    /// owns no localized copy of its own (the loc keys are a separate concern), and "2 hours ago" would need one.</summary>
    public static string PlayedAt(DateTimeOffset at, DateTimeOffset now, CultureInfo culture)
    {
        if (at.Year <= 1) return "";
        if (at.Date == now.Date) return at.ToString("t", culture);
        var age = now - at;
        if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(7))
            return culture.DateTimeFormat.GetAbbreviatedDayName(at.DayOfWeek);
        if (at.Year == now.Year) return at.ToString(ShortMonthDay(culture), culture);
        return at.ToString("d", culture);
    }

    /// <summary>Convenience over the wire's unix-ms timestamp. 0/negative (the wire's "unknown") yields "".</summary>
    public static string PlayedAt(long playedAtMs, DateTimeOffset now, CultureInfo culture)
        => playedAtMs <= 0 ? "" : PlayedAt(DateTimeOffset.FromUnixTimeMilliseconds(playedAtMs).ToOffset(now.Offset), now, culture);

    /// <summary>The culture's month-day pattern with the FULL month name narrowed to the abbreviated one. There is no
    /// standard "abbreviated month + day" format specifier, and a row's meta lane cannot afford "12 September".</summary>
    static string ShortMonthDay(CultureInfo culture)
    {
        string pattern = culture.DateTimeFormat.MonthDayPattern;
        return pattern.Contains("MMMM", StringComparison.Ordinal)
            ? pattern.Replace("MMMM", "MMM", StringComparison.Ordinal)
            : pattern;
    }

    /// <summary>The hero's thin metadata line: how many rows, and the window they span. Empty when there is nothing to
    /// state yet, so the line simply does not render rather than claiming "0".
    ///
    /// <para><paramref name="countPhrase"/> is the seam for the one AUTHORED word on this line ("1,708 items"). It is a
    /// delegate rather than a string constant for the same reason <c>CollectRange</c> takes <c>pending</c>: this file
    /// owns no localized copy and must stay engine-free, so the page supplies <c>Strings.Recents.ItemCount</c> and the
    /// tests supply nothing at all. Omitted ⇒ the bare culture-formatted number, which is what the wire alone can
    /// vouch for.</para></summary>
    public static string Summary(IReadOnlyList<RecentsRow> rows, DateTimeOffset now, CultureInfo culture,
                                 Func<int, string>? countPhrase = null)
    {
        if (rows.Count == 0) return "";
        long oldest = long.MaxValue, newest = long.MinValue;
        for (int i = 0; i < rows.Count; i++)
        {
            long t = rows[i].PlayedAtMs;
            if (t <= 0) continue;
            if (t < oldest) oldest = t;
            if (t > newest) newest = t;
        }
        string count = countPhrase is null ? rows.Count.ToString("N0", culture) : countPhrase(rows.Count);
        if (newest == long.MinValue) return count;
        string from = PlayedAt(oldest, now, culture), to = PlayedAt(newest, now, culture);
        if (from.Length == 0 || to.Length == 0) return count;
        return string.Equals(from, to, StringComparison.Ordinal)
            ? count + " · " + to
            : count + " · " + from + " – " + to;
    }

    /// <summary>A chip's label. The wire token IS the label when the app has no key for it — a data-derived name is
    /// honest where an invented one is not, and it keeps a content type the server adds tomorrow renderable today.</summary>
    public static string ChipLabel(string token, CultureInfo culture)
        => token.Length == 0 ? token : char.ToUpper(token[0], culture) + token[1..];
}
