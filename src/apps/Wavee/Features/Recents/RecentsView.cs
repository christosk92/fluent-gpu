using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>The two recycle-pool kinds in the grouped Recents projection.</summary>
enum RecentsFlatItemKind : byte { DateHeader, Row }

/// <summary>One entry in the list passed to the grouped virtual layout. A header has no source row
/// (<see cref="OriginalRowIndex"/> is -1); a row always points back into the unfiltered wire vector.</summary>
readonly record struct RecentsFlatItem(
    RecentsFlatItemKind Kind,
    int OriginalRowIndex,
    int DayIndex,
    int MonthIndex);

/// <summary>A filtered row vector with one synthetic header before each calendar day. All maps are indexed by the
/// FLAT list, so the virtual list, sticky-header observer, calendar anchor and hydration pump share one projection.</summary>
sealed record RecentsSections(
    RecentsFlatItem[] Items,
    int[] HeaderIndices,
    string[] HeaderLabels,
    DateOnly[] HeaderDates,
    int[] FlatToRow,
    int[] FlatToDay,
    int[] FlatToMonth,
    int[] RowToFlat,
    int[] RowToDay);

/// <summary>Which localized metadata branch a Recents row renders.</summary>
enum RecentsMetaKind : byte { PlayedAt, PlayedCount, SavedCount }

/// <summary>Pure reason/count decision; formatting and icons stay in the rendered half.</summary>
readonly record struct RecentsMeta(RecentsMetaKind Kind, int Count);

/// <summary>The item contributing the most played entries to one day. The source row identity is retained so the
/// calendar can resolve live metadata without copying a title into the pure model.</summary>
readonly record struct RecentsDayTopItem(int OriginalRowIndex, string ItemId, int PlayCount);

/// <summary>One calendar day. <see cref="DensityLevel"/> is 0 for no plays and 1..5 for the logarithmic accent ramp.</summary>
sealed record RecentsCalendarDay(
    DateOnly Date,
    int PlayCount,
    int DensityLevel,
    RecentsDayTopItem? TopItem);

/// <summary>One newest-first calendar month, including days with no plays so the view can render a stable 7-column grid.</summary>
sealed record RecentsCalendarMonth(
    int Year,
    int Month,
    int FirstDayOffset,
    int TotalPlays,
    DateOnly? BusiestDay,
    int BusiestDayPlays,
    bool IsCurrentMonth,
    RecentsCalendarDay[] Days);

/// <summary>The calendar overview derived solely from the resident, filtered Recents rows.</summary>
sealed record RecentsCalendar(RecentsCalendarMonth[] Months, int MaximumDayPlays);

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

    /// <summary>Insert one synthetic header before each calendar day in an ALREADY-filtered display map. Header and row
    /// positions are returned together so every consumer anchors against the exact same grouped shape.</summary>
    public static RecentsSections BuildSections(IReadOnlyList<RecentsRow> rows, IReadOnlyList<int> display,
                                                DateTimeOffset now, CultureInfo culture,
                                                Func<string, string>? localize = null)
    {
        var items = new List<RecentsFlatItem>(display.Count + Math.Min(display.Count, 64));
        var headers = new List<int>();
        var labels = new List<string>();
        var dates = new List<DateOnly>();
        var flatRows = new List<int>(items.Capacity);
        var flatDays = new List<int>(items.Capacity);
        var flatMonths = new List<int>(items.Capacity);
        var rowToFlat = new int[rows.Count];
        var rowToDay = new int[rows.Count];
        Array.Fill(rowToFlat, -1);
        Array.Fill(rowToDay, -1);

        var months = new Dictionary<int, int>();
        DateOnly prior = default;
        bool havePrior = false;
        int dayIndex = -1;
        int monthIndex = -1;

        for (int i = 0; i < display.Count; i++)
        {
            int rowIndex = display[i];
            if ((uint)rowIndex >= (uint)rows.Count) continue;
            RecentsRow row = rows[rowIndex];
            DateOnly date = DateOf(row.PlayedAtMs, now.Offset);
            if (!havePrior || date != prior)
            {
                havePrior = true;
                prior = date;
                dayIndex++;
                monthIndex = MonthIndex(date, months);
                headers.Add(items.Count);
                labels.Add(date == DateOnly.MinValue
                    ? ""
                    : DayBucketLabel(date.ToDateTime(TimeOnly.MinValue), now, culture, localize));
                dates.Add(date);
                items.Add(new RecentsFlatItem(RecentsFlatItemKind.DateHeader, -1, dayIndex, monthIndex));
                flatRows.Add(-1);
                flatDays.Add(dayIndex);
                flatMonths.Add(monthIndex);
            }

            rowToFlat[rowIndex] = items.Count;
            rowToDay[rowIndex] = dayIndex;
            items.Add(new RecentsFlatItem(RecentsFlatItemKind.Row, rowIndex, dayIndex, monthIndex));
            flatRows.Add(rowIndex);
            flatDays.Add(dayIndex);
            flatMonths.Add(monthIndex);
        }

        return new RecentsSections(
            items.ToArray(), headers.ToArray(), labels.ToArray(), dates.ToArray(),
            flatRows.ToArray(), flatDays.ToArray(), flatMonths.ToArray(), rowToFlat, rowToDay);
    }

    /// <summary>Per-day bucket label: Today, Yesterday, the culture's weekday for days 2..6, then its abbreviated
    /// month/day pattern. Calendar comparison happens in <paramref name="now"/>'s offset, including across a year edge.</summary>
    public static string DayBucketLabel(DateTimeOffset at, DateTimeOffset now, CultureInfo culture,
                                        Func<string, string>? localize = null)
    {
        DateTimeOffset localNow = now.ToOffset(now.Offset);
        DateTimeOffset localAt = at.ToOffset(now.Offset);
        int days = DateOnly.FromDateTime(localNow.DateTime).DayNumber - DateOnly.FromDateTime(localAt.DateTime).DayNumber;
        Func<string, string> resolve = localize ?? Loc.Get;
        if (days == 0) return resolve(Strings.Detail.Today);
        if (days == 1) return resolve(Strings.Detail.Yesterday);
        if ((uint)(days - 2) <= 4u) return culture.DateTimeFormat.GetDayName(localAt.DayOfWeek);
        return localAt.ToString(ShortMonthDay(culture), culture);
    }

    /// <summary>Unix-ms convenience for <see cref="DayBucketLabel(DateTimeOffset,DateTimeOffset,CultureInfo,Func{string,string}?)"/>.</summary>
    public static string DayBucketLabel(long atMs, DateTimeOffset now, CultureInfo culture,
                                        Func<string, string>? localize = null)
        => atMs <= 0 ? "" : DayBucketLabel(DateTimeOffset.FromUnixTimeMilliseconds(atMs), now, culture, localize);

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

    /// <summary>Append the decoded, usable child URIs of one expandable row. Empty entries and duplicates collapse;
    /// <paramref name="pending"/> lets the page share the same misses-only rule as viewport hydration.</summary>
    public static int CollectChildUris(RecentsRow row, Func<string, bool> pending, List<string> into,
                                       int cap = BatchCap)
    {
        if (cap <= 0 || row.ChildUris is not { Count: > 0 } children) return 0;
        int added = 0;
        for (int i = 0; i < children.Count && added < cap; i++)
        {
            string uri = children[i];
            if (uri.Length == 0 || !pending(uri)) continue;
            bool duplicate = false;
            for (int j = 0; j < into.Count; j++)
                if (string.Equals(into[j], uri, StringComparison.Ordinal)) { duplicate = true; break; }
            if (duplicate) continue;
            into.Add(uri);
            added++;
        }
        return added;
    }

    /// <summary>Select the metadata sentence from the wire reason. Saved entries always describe at least the row
    /// itself; grouped plays preserve the server's authoritative child count; singles and unknown reasons show time.</summary>
    public static RecentsMeta MetaFor(RecentsRow row)
        => row.Reason switch
        {
            RecentsReason.Saved => new RecentsMeta(RecentsMetaKind.SavedCount, Math.Max(1, row.ChildCount)),
            RecentsReason.Played when row.Kind == RecentsRowKind.Group
                => new RecentsMeta(RecentsMetaKind.PlayedCount, Math.Max(0, row.ChildCount)),
            _ => new RecentsMeta(RecentsMetaKind.PlayedAt, 0),
        };

    /// <summary>Build the newest-first calendar for the already-filtered display map. Only played rows contribute
    /// heat: a grouped play contributes its authoritative child count (at least one), a played single contributes one,
    /// and Saved/Unknown rows contribute zero. They still extend the visible date range.</summary>
    public static RecentsCalendar DayDensity(IReadOnlyList<RecentsRow> rows, IReadOnlyList<int> display,
                                             DateTimeOffset now, CultureInfo culture)
    {
        var byDay = new Dictionary<DateOnly, DayAccumulator>();
        DateOnly today = DateOnly.FromDateTime(now.ToOffset(now.Offset).DateTime);
        DateOnly oldest = today;
        bool hasDatedRow = false;

        for (int i = 0; i < display.Count; i++)
        {
            int rowIndex = display[i];
            if ((uint)rowIndex >= (uint)rows.Count) continue;
            var row = rows[rowIndex];
            DateOnly date = DateOf(row.PlayedAtMs, now.Offset);
            if (date == DateOnly.MinValue) continue;
            if (!hasDatedRow || date < oldest) oldest = date;
            hasDatedRow = true;
            if (!byDay.TryGetValue(date, out var day))
            {
                day = new DayAccumulator();
                byDay.Add(date, day);
            }

            int contribution = PlayContribution(row);
            if (contribution <= 0) continue;
            day.Plays += contribution;
            if (day.Top is null || contribution > day.Top.Value.PlayCount)
                day.Top = new RecentsDayTopItem(rowIndex, row.ItemId, contribution);
        }

        DateOnly firstMonth = new(today.Year, today.Month, 1);
        DateOnly lastMonth = hasDatedRow && oldest < firstMonth
            ? new DateOnly(oldest.Year, oldest.Month, 1)
            : firstMonth;
        int maximum = 0;
        foreach (var day in byDay.Values) maximum = Math.Max(maximum, day.Plays);

        var months = new List<RecentsCalendarMonth>();
        for (DateOnly month = firstMonth; month >= lastMonth; month = month.AddMonths(-1))
        {
            int count = DateTime.DaysInMonth(month.Year, month.Month);
            var days = new RecentsCalendarDay[count];
            int total = 0, busiestCount = 0;
            DateOnly? busiest = null;
            for (int d = 1; d <= count; d++)
            {
                var date = new DateOnly(month.Year, month.Month, d);
                byDay.TryGetValue(date, out var value);
                int plays = value?.Plays ?? 0;
                total += plays;
                // Ascending day walk + >= makes an equal-count tie choose the newest date.
                if (plays > 0 && plays >= busiestCount) { busiestCount = plays; busiest = date; }
                days[d - 1] = new RecentsCalendarDay(date, plays, DensityLevel(plays, maximum), value?.Top);
            }

            int offset = ((int)new DateTime(month.Year, month.Month, 1).DayOfWeek
                          - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            months.Add(new RecentsCalendarMonth(month.Year, month.Month, offset, total, busiest, busiestCount,
                month.Year == today.Year && month.Month == today.Month, days));
        }
        return new RecentsCalendar(months.ToArray(), maximum);
    }

    static int PlayContribution(RecentsRow row)
        => row.Reason != RecentsReason.Played ? 0
            : row.Kind == RecentsRowKind.Group ? Math.Max(1, row.ChildCount) : 1;

    static int DensityLevel(int count, int maximum)
    {
        if (count <= 0 || maximum <= 0) return 0;
        double level = Math.Ceiling(Math.Log(1d + count) / Math.Log(1d + maximum) * 5d);
        return Math.Clamp((int)level, 1, 5);
    }

    sealed class DayAccumulator
    {
        public int Plays;
        public RecentsDayTopItem? Top;
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
    internal static string ShortMonthDay(CultureInfo culture)
    {
        string pattern = culture.DateTimeFormat.MonthDayPattern;
        return pattern.Contains("MMMM", StringComparison.Ordinal)
            ? pattern.Replace("MMMM", "MMM", StringComparison.Ordinal)
            : pattern;
    }

    /// <summary>Calendar day of a wire unix-ms timestamp in <paramref name="offset"/>. 0/negative is
    /// <see cref="DateOnly.MinValue"/> (unknown), never the Unix epoch. Internal so the page and tests share
    /// this conversion rather than re-deriving it.</summary>
    internal static DateOnly DateOf(long unixMs, TimeSpan offset)
        => unixMs <= 0 ? DateOnly.MinValue
            : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(offset).DateTime);

    static int MonthIndex(DateOnly date, Dictionary<int, int> months)
    {
        if (date == DateOnly.MinValue) return -1;
        int key = date.Year * 12 + date.Month - 1;
        if (months.TryGetValue(key, out int existing)) return existing;
        int index = months.Count;
        months.Add(key, index);
        return index;
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
