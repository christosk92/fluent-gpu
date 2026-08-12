using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The Recents page ──────────────────────────────────────────────────────────────────────────────────────────────────
// One virtualized list over the WHOLE grouped recents snapshot (~1,708 rows on a real account) plus a viewport-driven
// hydration pump, under a Zune-ish typographic masthead and a Mica wash.
//
// THE ONE FACT THE WHOLE PAGE IS SHAPED BY: recents is a POINTER LIST. `GET /playlist/v2/list/recents/page` returns item
// ids, uris, timestamps and group child-counts and NOT ONE readable string — Title/Subtitle/Image are null on every
// freshly fetched row BY DESIGN. So the page owns three things the other list pages do not:
//   1. it never pages the wire (the whole list arrives at once → VirtualCollection.FromSnapshot, no remote paging);
//   2. it HYDRATES the entities the user actually realized, and only those (OnVisibleRange);
//   3. it re-renders exactly the realized slots when hydration lands, never rebuilding a 1,708-row list.
//
// HYDRATION GOES THROUGH THE CHOKEPOINT. `Services.Metadata.SyncAllAsync` is the app's ONE extended-metadata entry
// point: SWR cache, in-flight dedup, partial-cache skip (a fresh uri never hits the network), ETag/304 conditional
// reads, and — the part that matters most here — PROJECTION INTO THE STORE, which is how every other surface shares the
// same facts and how they survive a restart via CachedStore. The rows therefore hold NO copied strings: a row renders by
// resolving its uri against the store, and a store change re-skins the realized window. A page-local metadata cache
// would have been thrown away on navigate-away and shared with nobody.
//
// Filtering is CLIENT-SIDE, always: no request in the captured session carries a filter parameter, so a chip change
// re-cuts the loaded snapshot and never touches the network.
//
// The page takes NO constructor dependency (app-page rule) — everything resolves through UseContext in Render.
sealed class RecentsPage : Component
{
    /// <summary>MediaCard.Row's plain arm is a fixed 64-DIP row; the measured layout seeds from that and corrects on
    /// realize, so the (defensive) track arm may differ without the viewport mis-sizing.</summary>
    const float RowHeight = 64f;
    /// <summary>Date-header band. ModuleHeader is 20/28; 48 DIP is the house thumb rung that fits that type plus
    /// the prototype's label + rule + count row without clipping. Distinct from <see cref="TrackRow.HeaderHeight"/>
    /// (36), which is a playlist section label.</summary>
    const float DateHeaderHeight = WaveeSize.Thumb48;
    const float ChildRowHeight = WaveeSize.Thumb40;
    const int OverscanRows = 6;
    const float PageInset = Spacing.PageWide;
    /// <summary>The summary line reserves its width instead of reflowing as the count/date resolve. This engine has NO
    /// tabular-figures seam (ConcertUi and FlipCountdown both say so in as many words), so a reserved measure is the
    /// app's established substitute for tabular numerals.</summary>
    const float SummaryMinWidth = 220f;
    /// <summary>Hero entrance stagger. Applied to the MASTHEAD's two lines only — never to the list, whose entrance is
    /// the engine's realized-window-bounded StaggerColdRealize. 1,708 authored delays is a bug, not a choreography.
    /// <para>The value now lives in <see cref="WaveeMotion.MastheadStaggerMs"/>, shared with the app's other drill-in
    /// masthead (HomeSectionPage) so the two surfaces cannot drift apart by a number.</para></summary>
    const float HeroStaggerMs = WaveeMotion.MastheadStaggerMs;
    /// <summary>The desktop client's attribution tag for recents hydration traffic (`client-feature-id`). Threaded
    /// through SyncAllAsync → IMetadataSource.FetchAsync → the transport, so it survives the chokepoint.</summary>
    const string FeatureId = "mdata_esperanto";

    /// <summary>The recycled-slot fallback: a bound slot transiently outside the range renders nothing.</summary>
    static readonly RecentsFlatItem EmptyFlat = new(RecentsFlatItemKind.Row, -1, -1, -1);
    static readonly RecentsSnapshot PendingSeed = CreatePendingSeed();

    // ── reactive surface (three signals; everything else is a plain field the slots read at render time) ──────────────
    /// <summary>Bumped when hydration lands in the STORE (or a snapshot is adopted). The bound projection carries it, so
    /// exactly the realized slots re-render — the DetailTracks mechanism.</summary>
    readonly Signal<int> _epoch = new(0);
    /// <summary>Snapshot/filter shape only. Metadata hydration must never replace the stateful grouped layout.</summary>
    readonly Signal<int> _shapeEpoch = new(0);
    /// <summary>The selected content-type TOKEN (wire spelling), null = "All". Never a label — the label is derived.</summary>
    readonly Signal<string?> _chip = new(null);
    /// <summary>Exactly one disclosure is open. The wire item id is the identity; entity URIs repeat.</summary>
    readonly Signal<string> _expandedRow = new("");
    readonly Signal<int> _stickyHeader = new(-1);
    readonly Signal<float> _stickyPush = new(0f);
    readonly Signal<bool> _isZoomedOut = new(false);
    readonly Signal<DateOnly> _calendarDay = new(DateOnly.FromDateTime(DateTime.Now));
    readonly object _washOwner = new();

    // ── the snapshot, owned as plain arrays (never a signal: a 1,708-element list is not a value to diff) ─────────────
    // The rows stay POINTERS for their whole life. Nothing here is ever rewritten with hydrated text — that lives in the
    // store, which is shared, persisted and updated by every other surface too.
    RecentsRow[] _rows;                                      // wire order
    RecentsRow[] _display;                                   // the chip's cut of _rows (== _rows when nothing is selected)
    bool[] _morphable;                                       // first occurrence of each uri → may claim the shared-element tag
    RecentsSections _sections;
    RecentsCalendar _calendar;
    string[] _chipTokens = Array.Empty<string>();
    string[] _chipLabels = Array.Empty<string>();
    string? _revision;
    bool _hasSnapshot;

    /// <summary>The resident collection the viewport reads through. Snapshot-backed on purpose: recents arrives whole,
    /// so virtualization here is about MOUNTED UI, not remote paging.</summary>
    readonly VirtualCollection<RecentsFlatItem> _vc;
    readonly ItemsViewController _listController = new();
    readonly ItemsViewController _calendarController = new();
    readonly AnnotatedScrollBarController _scrollController = new();
    readonly SemanticZoomController _zoomController = new();
    GroupedListVirtualLayout? _groupedLayout;
    /// <summary>Integration seam for SemanticZoom: inline and overlay day headers share this callback.</summary>
    internal Action<DateOnly>? DateHeaderInvoked { get; set; }

    // ── hydration bookkeeping. UI-THREAD ONLY: every mutation happens in Render, in Pump, or in a posted continuation. ─
    // NOTE this is NOT a metadata cache — that is the chokepoint's job. It only stops the SAME uri being handed to
    // SyncAllAsync twice while one call is still in flight; freshness, dedup and skipping belong to MetadataService.
    readonly HashSet<string> _inflight = new(StringComparer.Ordinal);
    readonly List<string> _batch = new(RecentsView.BatchCap);
    int _rangeFirst, _rangeEnd;
    bool _pumpArmed;
    bool _storeDirty;

    // Services + callbacks, refreshed at the top of every render so a bound slot never holds a mount-time instance.
    Services? _svc;
    IStore? _store;
    Wavee.Backend.Metadata.MetadataService? _metadata;
    Action<Action> _post = static a => a();
    Action<string, string?> _go = static (_, _) => { };
    NavPreviewStore? _preview;
    CancellationTokenSource? _cts;
    CultureInfo _culture = CultureInfo.CurrentCulture;
    DateTimeOffset _now = DateTimeOffset.Now;

    /// <summary>The atomic value the bound rows observe: the hydration/adoption epoch, the collection's own version
    /// (bumped by a snapshot replacement), and the collection itself — so both selectors derive solely from ONE snapshot
    /// rather than reading mutable page fields.</summary>
    readonly record struct RowsView(int Epoch, int Version, VirtualCollection<RecentsFlatItem> Rows);

    /// <summary>What a row displays, resolved from the STORE at render time. Never stored on the row.</summary>
    readonly record struct RowFacts(string? Title, string? Subtitle, Image? Cover);

    static RecentsSnapshot CreatePendingSeed()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new RecentsRow[8];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new RecentsRow(
                RecentsRowKind.Group, "pending:" + i, "", null, null, null, null, 1,
                now.AddMinutes(-i).ToUnixTimeMilliseconds(), RecentsEntityKind.Unknown,
                RecentsReason.Played);
        return new RecentsSnapshot(null, rows);
    }

    static RecentsRow[] CopyRows(IReadOnlyList<RecentsRow> incoming)
    {
        var rows = new RecentsRow[incoming.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = incoming[i];
        return rows;
    }

    public RecentsPage()
    {
        _rows = CopyRows(PendingSeed.Rows);
        _display = _rows;
        var displayToRow = RecentsView.Filter(_rows, null);
        _morphable = RecentsView.FirstOccurrence(_rows);
        var now = DateTimeOffset.Now;
        _sections = RecentsView.BuildSections(_rows, displayToRow, now, CultureInfo.CurrentCulture);
        _calendar = RecentsView.DayDensity(_rows, displayToRow, now, CultureInfo.CurrentCulture);
        _vc = VirtualCollection<RecentsFlatItem>.FromSnapshot(_sections.Items);
        DateHeaderInvoked = OpenOverview;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var preview = UseContext(NavPreviewStore.Slot);
        var shellMaterial = UseContext(ShellMaterial.Slot);
        var post = UsePost();
        _post = post;
        _go = go;
        _preview = preview;
        _svc = svc;
        _store = svc?.RealStore;
        _metadata = svc?.Metadata;
        _culture = CultureInfo.CurrentCulture;
        _now = DateTimeOffset.Now;
        if (svc is null) return new BoxEl { Grow = 1f };

        // ── the cold read. One page-scoped CTS also cancels every hydration batch on unmount. ─────────────────────────
        var recents = UseResource(ct => FetchResourceAsync(svc.Recents, ct), PendingSeed);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            return (Action?)(() =>
            {
                _cts = null;
                try { cts.Cancel(); cts.Dispose(); } catch { }
            });
        }, DepKey.Empty);

        // ── the store is the row model, so a store WRITE is what makes rows readable. Subscribe once and coalesce: the
        //    playback path writes tracks constantly, and one epoch bump per write would re-render the realized window on
        //    every heartbeat. One posted bump per turn is enough — the rows re-read the store when they re-render.
        var store = svc.RealStore;
        UseEffect(() =>
        {
            if (store is null) return (Action?)null;
            var sub = store.Changes.Subscribe(Observers.From<StoreChange>(_ => MarkStoreDirty()));
            return (Action?)(() => sub.Dispose());
        }, DepKey.FromRef(store));

        int epoch = _epoch.Value;          // subscribe: hydration re-renders the chrome (summary + wash) too
        int shapeEpoch = _shapeEpoch.Value;
        string? token = _chip.Value;

        // ── the shell MATERIAL (Mica wash). Recents publishes ONE leg — the most recent hydrated cover — through the
        //    same HomeWashSource resolution Home uses, so the colour is the page's own content and never invented.
        _ = AppearancePrefs.Epoch.Value;   // the Settings toggle applies LIVE (the DisableColorWashes idiom)
        bool washesDisabled = svc.Settings.Get(WaveeSettings.DisableColorWashes);
        var washCard = WashCard();
        // Watch exactly the ONE artwork whose grading the wash is still waiting on — never the plane's global epoch,
        // which every scrolling batch of this very list would bump.
        if (HomeWashSource.PlaneUrl(washCard) is { Length: > 0 } planeUrl)
            _ = SpotifyLive.CoverColorPlane.Current.Watch(planeUrl).Value;
        var pick = washesDisabled ? null : HomeWashSource.Pick(washCard, Surfaces.ChromeSchemeFor);
        HomeWash? wash = washesDisabled || pick is null
            ? null
            : new HomeWash(new WashLayer(pick.Value.Color, pick.Value.Key), null, null);

        // Owner-gated exactly like HomePage/DetailShell: a page clears the material only while it is still the owner,
        // so a "park this page + activate the destination" nav lands on the destination's material whichever effect
        // fires first.
        void SetWash(HomeWash? w)
        {
            if (shellMaterial is not null) shellMaterial.Value = new ShellMaterialState(_washOwner, null, w);
        }
        void ClearWash()
        {
            if (shellMaterial is not null && ReferenceEquals(shellMaterial.Peek().Owner, _washOwner))
                shellMaterial.Value = default;
        }
        UseEffect(() => SetWash(wash),
            DepKey.From(HashCode.Combine(washesDisabled, pick?.Key, pick?.Color.R, pick?.Color.G, pick?.Color.B)));
        UseActivation(
            onActivated: () =>
            {
                SetWash(wash);
                // Revision sync on REACTIVATION, never on a cadence: a null diff answer means "unchanged", and the
                // correct response to that is to do nothing at all.
                if (_hasSnapshot) recents.Refresh();
            },
            onDeactivated: ClearWash);
        // …and on UNMOUNT too: onDeactivated fires only on PARK, so a nav that evicts this page without parking it
        // would otherwise leave a wash owned by a gone page. Owner-gated, so it can never clobber the next page's.
        UseEffect(() => (Action?)ClearWash, DepKey.Empty);

        // ── chrome ────────────────────────────────────────────────────────────────────────────────────────────────────
        Element hero = Hero();
        Element? chips = _chipLabels.Length == 0
            ? null
            : ContentFilterChips.Build(
                new ContentFilterChipSet(_chipLabels, _chipLabels.Length),
                LabelOf(token),
                SelectChip,
                Loc.Get(Strings.Detail.Filter.All),
                "recents.chips");

        Element body = Skel.Region(
            recents.Loadable,
            content: _ => Embed.Comp(() => new RecentsSemanticSurface(this, token, shapeEpoch)) with
            { Key = "recents-semantic:" + (token ?? "all") + ":" + shapeEpoch },
            reveal: SkelReveal.None,
            isEmpty: snapshot => snapshot.Rows.Count == 0,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Sidebar.Section.EmptyRecents)),
            onFailed: () => ErrorState.Build(recents.Loadable.Error, onRetry: () => recents.Refresh()));

        var kids = new List<Element>(3) { hero };
        if (chips is not null)
            kids.Add(new BoxEl { Padding = new Edges4(PageInset, 0f, PageInset, 0f), Children = [chips] });
        kids.Add(new BoxEl
        {
            Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(PageInset, 0f, PageInset, PlayerDock.Reserve + Spacing.L),
            // The FLIP: a chip switch changes the list's identity (below), and this wrapper glides the swap instead of
            // cutting to a differently-sized list. Motion.ReducedMotion is a VALUE, so this is a null vs a transition,
            // never a divergent hook path.
            Layout = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                MotionTok.ContentResize.ToDynamics(),
                Enter: new EnterExit(Dy: Spacing.S, Opacity: 0f, Active: true),
                Exit: new EnterExit(Opacity: 0f, Active: true)),
            Children = [body],
        });

        _ = epoch;   // read above; the explicit subscription this chrome depends on
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Focusable = true,
            OnKeyDown = e =>
            {
                if (e.Handled || e.KeyCode != Keys.Escape || !_isZoomedOut.Peek()) return;
                _zoomController.ZoomInTo(-1);
                e.Handled = true;
            },
            Children = kids.ToArray(),
        };
    }

    // ── masthead ──────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>An oversized, light display cut of the surface's own name over one thin metadata line. The stagger lives
    /// on the CONTAINER (two children), the enter on each line — the engine's own idiom.</summary>
    Element Hero()
    {
        // The count is the page's ONE authored word on this line; the window either side of it stays culture-table
        // formatting (RecentsView owns no copy and is engine-free — see its Summary doc for the seam).
        string summary = _hasSnapshot
            ? RecentsView.Summary(_rows, _now, _culture, static n => Strings.Recents.ItemCount(n))
            : "";
        var title = WaveeType.SurfaceDisplay(Loc.Get(Strings.Home.Recents)) with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = 1f, Basis = 0f,
            Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };
        var overview = Button.Create(Loc.Get(Strings.Recents.Overview), OpenOverviewFromMasthead,
            ButtonAppearance.Subtle, ControlSize.Small, glyph: Icons.Calendar) with { Shrink = 0f };
        var lines = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                Children = [title, overview],
            },
        };
        if (summary.Length > 0)
            lines.Add(Caption(summary) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                // Reserved measure in lieu of tabular figures (no such seam exists here): the line does not reflow as
                // the count and the played window resolve.
                MinWidth = SummaryMinWidth,
                Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
            });
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS,
            Padding = new Edges4(PageInset, Spacing.XXL, PageInset, Spacing.L),
            Stagger = HeroStaggerMs,
            Children = lines.ToArray(),
        };
    }

    // ── the list ──────────────────────────────────────────────────────────────────────────────────────────────────────
    void OpenOverviewFromMasthead()
    {
        _calendarDay.Value = DateOnly.FromDateTime(_now.ToOffset(_now.Offset).DateTime);
        _zoomController.ZoomOutTo(-1);
    }

    void OpenOverview(DateOnly date)
    {
        _calendarDay.Value = date;
        _zoomController.ZoomOutTo(HeaderFlatFor(date));
    }

    int HeaderFlatFor(DateOnly date)
    {
        for (int i = 0; i < _sections.HeaderDates.Length; i++)
            if (_sections.HeaderDates[i] == date) return _sections.HeaderIndices[i];
        return -1;
    }

    int MonthFor(DateOnly date)
    {
        for (int i = 0; i < _calendar.Months.Length; i++)
        {
            var month = _calendar.Months[i];
            if (month.Year == date.Year && month.Month == date.Month) return i;
        }
        return -1;
    }

    int MapInToOut(int flatIndex)
    {
        if ((uint)flatIndex >= (uint)_sections.Items.Length) return -1;
        int day = _sections.Items[flatIndex].DayIndex;
        return (uint)day < (uint)_sections.HeaderDates.Length ? MonthFor(_sections.HeaderDates[day]) : -1;
    }

    int MapOutToIn(int monthIndex)
    {
        DateOnly selected = _calendarDay.Peek();
        int exact = HeaderFlatFor(selected);
        if (exact >= 0 && MonthFor(selected) == monthIndex) return exact;
        if ((uint)monthIndex >= (uint)_calendar.Months.Length) return -1;
        var month = _calendar.Months[monthIndex];
        for (int i = 0; i < _sections.HeaderDates.Length; i++)
        {
            var date = _sections.HeaderDates[i];
            if (date.Year == month.Year && date.Month == month.Month) return _sections.HeaderIndices[i];
        }
        return -1;
    }

    sealed class RecentsSemanticSurface : Component
    {
        readonly RecentsPage _page;
        readonly string? _token;
        readonly int _shapeEpoch;

        public RecentsSemanticSurface(RecentsPage page, string? token, int shapeEpoch)
        {
            _page = page; _token = token; _shapeEpoch = shapeEpoch;
        }

        public override Element Render()
        {
            Element detail = Embed.Comp(() => new RecentsListSurface(_page, _token, _shapeEpoch)) with
            { Key = "recents-detail:" + (_token ?? "all") + ":" + _shapeEpoch };
            Element rail = Embed.Comp(() => new RecentsRail(_page, _shapeEpoch)) with
            { Key = "recents-rail:" + (_token ?? "all") + ":" + _shapeEpoch };
            Element zoomedIn = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.M,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                        Children = [detail],
                    },
                    rail,
                ],
            };
            Element zoomedOut = Embed.Comp(() => new CalendarOverviewSurface(_page, _shapeEpoch)) with
            { Key = "recents-calendar:" + _shapeEpoch };
            return SemanticZoom.Create(
                new SemanticZoomSlots(
                    new SemanticZoomView(zoomedIn, _page._listController),
                    new SemanticZoomView(zoomedOut, _page._calendarController)),
                new SemanticZoomOptions
                {
                    IsZoomedOut = _page._isZoomedOut,
                    Controller = _page._zoomController,
                    MapInToOut = _page.MapInToOut,
                    MapOutToIn = _page.MapOutToIn,
                });
        }
    }

    /// <summary>Owns the annotated rail so extent-signal churn cannot rebuild label arrays. GroupedListVirtualLayout
    /// ignores cross size (<c>OffsetOf</c>/<c>IndexAt</c> take 0); labels are memoized on <see cref="_shapeEpoch"/>.</summary>
    sealed class RecentsRail : Component
    {
        readonly RecentsPage _page;
        readonly int _shapeEpoch;
        readonly Func<AnnotatedScrollBarLabel[]> _labels;
        readonly Func<float[]> _ticks;
        readonly Func<float, AnnotatedScrollBarLabel?> _detail;

        public RecentsRail(RecentsPage page, int shapeEpoch)
        {
            _page = page;
            _shapeEpoch = shapeEpoch;
            _labels = page.RailLabels;
            _ticks = page.RailTicks;
            _detail = page.RailDetail;
        }

        public override Element Render()
        {
            var bounds = UseMeasuredBounds().Value;
            float viewport = _page._scrollController.ViewportLength.Value;
            float railHeight = bounds.H > 0f ? bounds.H : viewport > 0f ? viewport : WaveeSize.RailAlbum;
            // Shape is the identity; the 0/1 bit lets the first post-layout render populate labels if this
            // rail mounted before RecentsListSurface assigned _groupedLayout. Not a per-scroll key.
            int layoutReady = _page._groupedLayout is null ? 0 : 1;
            var labels = UseMemo(_labels, DepKey.From(_shapeEpoch, layoutReady));
            var ticks = UseMemo(_ticks, DepKey.From(_shapeEpoch, layoutReady));
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, MinHeight = 0f,
                Children =
                [
                    AnnotatedScrollBar.Create(_page._scrollController, new AnnotatedScrollBarOptions
                    {
                        Labels = labels,
                        TickOffsets = ticks,
                        Height = railHeight,
                        DetailLabelAtOffset = _detail,
                    }),
                ],
            };
        }
    }

    AnnotatedScrollBarLabel[] RailLabels()
    {
        var layout = _groupedLayout;
        if (layout is null) return [];
        var labels = new List<AnnotatedScrollBarLabel>();
        int priorMonth = -1, priorYear = -1;
        for (int i = 0; i < _sections.HeaderIndices.Length; i++)
        {
            DateOnly date = _sections.HeaderDates[i];
            if (date == DateOnly.MinValue || date.Month == priorMonth && date.Year == priorYear) continue;
            // The rail is LabelsMinWidth (44). Full month names ellipsis to "Augu.." and make the
            // labels unreadable; abbreviated months fit, and the pointer flag carries the day.
            string text = priorYear != date.Year
                ? date.ToString("MMM yy", _culture)
                : date.ToString("MMM", _culture);
            labels.Add(new AnnotatedScrollBarLabel(layout.OffsetOf(_sections.HeaderIndices[i], 0f), text));
            priorMonth = date.Month; priorYear = date.Year;
        }
        return labels.ToArray();
    }

    float[] RailTicks()
    {
        var layout = _groupedLayout;
        if (layout is null) return [];
        var ticks = new float[_sections.HeaderIndices.Length];
        for (int i = 0; i < ticks.Length; i++) ticks[i] = layout.OffsetOf(_sections.HeaderIndices[i], 0f);
        return ticks;
    }

    AnnotatedScrollBarLabel? RailDetail(float offset)
    {
        var layout = _groupedLayout;
        if (layout is null || _sections.Items.Length == 0) return null;
        int flat = Math.Clamp(layout.IndexAt(offset, 0f), 0, _sections.Items.Length - 1);
        int day = _sections.Items[flat].DayIndex;
        if ((uint)day >= (uint)_sections.HeaderLabels.Length) return null;
        return new AnnotatedScrollBarLabel(layout.OffsetOf(_sections.HeaderIndices[day], 0f),
            _sections.HeaderLabels[day]);
    }

    sealed class CalendarOverviewSurface : Component
    {
        readonly RecentsPage _page;
        readonly int _shapeEpoch;
        public CalendarOverviewSurface(RecentsPage page, int shapeEpoch) { _page = page; _shapeEpoch = shapeEpoch; }

        public override Element Render()
        {
            _ = _page._epoch.Value;
            DateOnly selected = _page._calendarDay.Value;
            var calendar = _page._calendar;
            Element months = ItemsView.Create(
                calendar.Months.Length,
                i => Embed.Comp(() => new CalendarMonthCard(_page, i)) with
                { Key = "recents-month:" + calendar.Months[i].Year + ":" + calendar.Months[i].Month },
                RepeatLayout.GridFit(WaveeSize.RailAlbum, Spacing.PageWide,
                    WaveeSize.RailAlbum + WaveeSize.Thumb64 + Spacing.PageWide),
                new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.None,
                    Selector = SelectorVisual.None,
                    Controller = _page._calendarController,
                    Grow = 1f,
                    Overscan = 1,
                    KeyOf = i => "recents-month:" + calendar.Months[i].Year + ":" + calendar.Months[i].Month,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = "recents-calendar:" + _shapeEpoch,
                        AutoEdgeFade = true,
                    },
                });
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
                OnPointerExit = _page.ResetCalendarDay,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Shrink = 0f, Gap = Spacing.XXS,
                        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
                        Children =
                        [
                            Caption(selected.ToString("dddd d MMMM", _page._culture)) with
                            { Weight = 600, Color = Tok.TextPrimary },
                            Caption(_page.CalendarReadout(selected)) with
                            { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                    months,
                ],
            };
        }
    }

    sealed class CalendarMonthCard : Component
    {
        readonly RecentsPage _page;
        readonly int _monthIndex;
        public CalendarMonthCard(RecentsPage page, int monthIndex) { _page = page; _monthIndex = monthIndex; }

        public override Element Render()
        {
            var month = _page._calendar.Months[_monthIndex];
            DateOnly today = DateOnly.FromDateTime(_page._now.ToOffset(_page._now.Offset).DateTime);
            var weekNames = new Element[7];
            var firstDay = _page._culture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < weekNames.Length; i++)
            {
                int day = ((int)firstDay + i) % 7;
                weekNames[i] = new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Spacing.XXL,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Caption(_page._culture.DateTimeFormat.AbbreviatedDayNames[day]) with
                    { Color = Tok.TextTertiary, MaxLines = 1 }],
                };
            }

            var rows = new List<Element>(7)
            {
                new BoxEl { Direction = 0, Children = weekNames },
            };
            DateOnly first = new(month.Year, month.Month, 1);
            DateOnly gridFirst = first.AddDays(-month.FirstDayOffset);
            for (int week = 0; week < 6; week++)
            {
                var cells = new Element[7];
                for (int column = 0; column < 7; column++)
                {
                    DateOnly date = gridFirst.AddDays(week * 7 + column);
                    cells[column] = _page.CalendarCell(date, month, today, _monthIndex);
                }
                rows.Add(new BoxEl { Direction = 0, Children = cells });
            }

            string monthTitle = first.ToString("MMMM", _page._culture);
            string total = Strings.Recents.PlayCount(month.TotalPlays);
            string busiest = month.BusiestDay is { } busy
                ? Strings.Recents.Busiest(busy.ToString("d MMMM", _page._culture), month.BusiestDayPlays)
                : Loc.Get(Strings.Recents.NothingPlayed);
            if (month.IsCurrentMonth) total += " · " + Loc.Get(Strings.Recents.SoFar);
            return new BoxEl
            {
                Direction = 1, MinWidth = WaveeSize.RailAlbum, Gap = Spacing.S,
                OnPointerExit = _page.ResetCalendarDay,
                Children =
                [
                    WaveeType.ModuleHeader(monthTitle) with
                    {
                        Color = month.IsCurrentMonth ? WaveeAccent.Decor : Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    Caption(total) with { Color = Tok.TextTertiary, MaxLines = 1 },
                    Caption(busiest) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows.ToArray() },
                ],
            };
        }
    }

    RecentsCalendarDay? CalendarDay(DateOnly date)
    {
        int monthIndex = MonthFor(date);
        if ((uint)monthIndex >= (uint)_calendar.Months.Length) return null;
        var month = _calendar.Months[monthIndex];
        int day = date.Day - 1;
        return (uint)day < (uint)month.Days.Length ? month.Days[day] : null;
    }

    string CalendarReadout(DateOnly date)
    {
        var day = CalendarDay(date);
        int plays = day?.PlayCount ?? 0;
        string readout = Strings.Recents.PlayCount(plays);
        if (day?.TopItem is { } top && (uint)top.OriginalRowIndex < (uint)_rows.Length
            && FactsFor(_rows[top.OriginalRowIndex]).Title is { Length: > 0 } title)
            readout += " · " + Strings.Recents.Mostly(title);
        return readout;
    }

    string CalendarTooltip(DateOnly date)
        => date.ToString("dddd d MMMM", _culture) + " · " + CalendarReadout(date);

    void ResetCalendarDay()
        => _calendarDay.Value = DateOnly.FromDateTime(_now.ToOffset(_now.Offset).DateTime);

    static ColorF DensityFill(int level)
    {
        if (level <= 0) return ColorF.Transparent;
        ColorF accent = WaveeAccent.Decor;
        float t = Math.Clamp(level, 1, 5) / 5f;
        float alpha = Tok.AccentSubtle.A + (accent.A - Tok.AccentSubtle.A) * t;
        return accent with { A = alpha };
    }

    Element CalendarCell(DateOnly date, RecentsCalendarMonth month, DateOnly today, int monthIndex)
    {
        bool inMonth = date.Year == month.Year && date.Month == month.Month;
        var day = inMonth ? CalendarDay(date) : null;
        bool isToday = inMonth && date == today;
        bool hasRows = inMonth && HeaderFlatFor(date) >= 0;
        var numeral = Body(date.Day.ToString(_culture)) with
        {
            Weight = (ushort)(isToday ? 700 : 400),
            Color = !inMonth ? Tok.TextDisabled : isToday ? WaveeAccent.Decor : Tok.TextPrimary,
            MaxLines = 1,
        };
        var children = new List<Element>(2) { numeral };
        if (isToday)
            children.Add(new BoxEl
            {
                Width = Spacing.XS, Height = Spacing.XS, Corners = Radii.FullAll,
                Fill = WaveeAccent.Decor, AlignSelf = FlexAlign.End,
            });
        var cell = new BoxEl
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Height = WaveeSize.Thumb40,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = inMonth ? DensityFill(day?.DensityLevel ?? 0) : Tok.FillSubtleTransparent,
            BorderWidth = isToday ? Spacing.XXS : 0f,
            BorderColor = isToday ? WaveeAccent.Decor : ColorF.Transparent,
            Shadow = isToday ? new ShadowSpec(Spacing.M, 0f, 0f, Tok.AccentSubtle) : null,
            Role = hasRows ? AutomationRole.Button : AutomationRole.None,
            Focusable = hasRows,
            TabStop = hasRows,
            Cursor = hasRows ? CursorId.Hand : CursorId.Arrow,
            OnHoverMove = inMonth ? _ => _calendarDay.SetIfChanged(date) : null,
            OnClick = hasRows ? () =>
            {
                _calendarDay.Value = date;
                _zoomController.ZoomInTo(monthIndex);
            } : null,
            OnFocusChanged = hasRows ? focused =>
            {
                if (focused) _calendarDay.SetIfChanged(date); else ResetCalendarDay();
            } : null,
            Children = children.ToArray(),
        };
        if (!inMonth) return cell;
        // ToolTip.Wrap is layout-transparent on AlignSelf but does not carry Grow; the flex track owns the
        // 7-column contract so the wrap cannot collapse the cell into a content-sized accent bar.
        return new BoxEl
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Height = WaveeSize.Thumb40, Direction = 1,
            Children = [ToolTip.Wrap(cell, CalendarTooltip(date))],
        };
    }

    sealed class RecentsListSurface : Component
    {
        readonly RecentsPage _page;
        readonly string? _token;
        readonly int _shapeEpoch;

        public RecentsListSurface(RecentsPage page, string? token, int shapeEpoch)
        {
            _page = page; _token = token; _shapeEpoch = shapeEpoch;
        }

        public override Element Render()
        {
            var layout = UseMemo(
                () => new GroupedListVirtualLayout(_page._sections.HeaderIndices, DateHeaderHeight, RowHeight),
                DepKey.From(_shapeEpoch));
            _page._groupedLayout = layout;
            var view = UseComputed(() => new RowsView(_page._epoch.Value, _page._vc.Version.Value, _page._vc));
            var items = UseMemo(() => BoundItems.Project(
                view,
                static s => s.Rows.CountOr0,
                static (s, i) => s.Rows[i],
                EmptyFlat), DepKey.Empty);

            Element list = ItemsView.CreateBound<RecentsFlatItem>(
                items,
                (BoundItemScope<RecentsFlatItem> scope) => Embed.Comp(() => new RecentsRowSlot(_page, scope)),
                RepeatLayout.Measured(layout),
                new ListOptions<RecentsFlatItem>
            {
                // The rows are cards with their own chrome; a list selector here would be a second, competing cue.
                SelectionMode = ItemsSelectionMode.None,
                Selector = SelectorVisual.None,
                IsItemInvokedEnabled = true,
                OnInvokedTyped = (_, item) => _page.InvokeFlat(item),
                ItemTextTyped = (_, item) => _page.TextFor(item),
                Controller = _page._listController,
                Overscan = OverscanRows,
                Grow = 1f,
                // One recycle pool per row KIND: a group card's slot must never rebind into the (defensive) track-grid
                // shape — a cross-shape reuse forces a full rebuild instead of a cheap rebind.
                ContentType = _page.ContentTypeOf,
                Scroll = new ScrollOptions
                {
                    ScrollKey = "recents:" + (_token ?? "all"),
                    AutoEdgeFade = true,
                    VerticalScrollController = _page._scrollController,
                    SuppressScrollBar = true,
                    OnScrollGeometryChanged = (_page.ProjectSticky, _page.UpdateSticky),
                },
                // The engine's own cold-realize stagger: bounded to the REALIZED window by construction, which is the
                // only kind of entrance a 1,708-row list may have.
                Entrance = new EntranceOptions { StaggerColdRealize = true },
                // The point of the page: the realized window moved → hydrate what it still misses.
                OnVisibleRange = _page.OnVisibleRange,
                KeyOf = _page.FlatKey,
            }) with { Key = "recents-list:" + (_token ?? "all") + ":" + _shapeEpoch };

            Element overlay = Embed.Comp(() => new StickyDayHeader(_page)) with
            { Key = "recents-sticky:" + (_token ?? "all") + ":" + _shapeEpoch };
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                ZStack = true, ClipToBounds = true,
                Children = [list, overlay],
            };
        }
    }

    int ContentTypeOf(int index)
    {
        var sections = _sections;
        if ((uint)index >= (uint)sections.Items.Length) return 0;
        var flat = sections.Items[index];
        if (flat.Kind == RecentsFlatItemKind.DateHeader) return 0;
        int rowIndex = flat.OriginalRowIndex;
        return (uint)rowIndex < (uint)_rows.Length ? 1 + (int)_rows[rowIndex].Kind : 1;
    }

    // ── rows ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A recents row is a card ONE realized slot renders. Its own component so the slot re-renders on its own
    /// item subscription (an index rebind, or a hydration epoch) without the page re-rendering.</summary>
    string FlatKey(int index)
    {
        var sections = _sections;
        if ((uint)index >= (uint)sections.Items.Length) return "recents:missing:" + index;
        var item = sections.Items[index];
        if (item.Kind == RecentsFlatItemKind.DateHeader) return "recents:day:" + (uint)item.DayIndex;
        int rowIndex = item.OriginalRowIndex;
        return (uint)rowIndex < (uint)_rows.Length ? "recents:item:" + _rows[rowIndex].ItemId : "recents:missing:" + index;
    }

    void InvokeFlat(RecentsFlatItem item)
    {
        if (item.Kind != RecentsFlatItemKind.Row) return;
        int rowIndex = item.OriginalRowIndex;
        if ((uint)rowIndex >= (uint)_rows.Length) return;
        var row = _rows[rowIndex];
        if (CanExpand(row)) ToggleExpanded(row);
        else Open(row);
    }

    string TextFor(RecentsFlatItem item)
    {
        if (item.Kind == RecentsFlatItemKind.DateHeader)
            return (uint)item.DayIndex < (uint)_sections.HeaderLabels.Length ? _sections.HeaderLabels[item.DayIndex] : "";
        int rowIndex = item.OriginalRowIndex;
        return (uint)rowIndex < (uint)_rows.Length ? FactsFor(_rows[rowIndex]).Title ?? "" : "";
    }

    long ProjectSticky(ScrollGeometry geometry)
    {
        StickyMetrics(geometry, out int header, out float push);
        int quantized = (int)MathF.Round(push / Spacing.XXS);
        return ((long)(header + 1) << 32) | (uint)quantized;
    }

    void UpdateSticky(ScrollGeometry geometry)
    {
        StickyMetrics(geometry, out int header, out float push);
        float quantized = MathF.Round(push / Spacing.XXS) * Spacing.XXS;
        if (_stickyHeader.Peek() != header) _stickyHeader.Value = header;
        if (!_stickyPush.Peek().Equals(quantized)) _stickyPush.Value = quantized;
        ProbeStickyAlignment(geometry, header);
    }

    /// <summary>Issue 2 DEBUG probe: Today-over-July is not a grouping bug if the same PlayedAtMs feeds both
    /// the sticky header and the realized rows. Fail when a row's calendar day disagrees with its section header,
    /// or when the stuck header's day disagrees with the first visible row.</summary>
    [Conditional("DEBUG")]
    void ProbeStickyAlignment(ScrollGeometry geometry, int stickyFlat)
    {
        var layout = _groupedLayout;
        var sections = _sections;
        var rows = _rows;
        if (layout is null || sections.Items.Length == 0) return;
        TimeSpan offset = _now.Offset;
        ProbeFlatDate(sections, rows, stickyFlat, offset, "sticky");
        int at = layout.IndexAt(geometry.OffsetY, 0f);
        for (int d = -2; d <= 2; d++)
            ProbeFlatDate(sections, rows, at + d, offset, "visible");

        if ((uint)stickyFlat >= (uint)sections.Items.Length) return;
        int stickyDay = sections.Items[stickyFlat].DayIndex;
        int visibleDay = -1;
        int last = Math.Min(sections.Items.Length, at + 6);
        for (int i = Math.Max(0, at); i < last; i++)
        {
            if (sections.Items[i].Kind != RecentsFlatItemKind.Row) continue;
            visibleDay = sections.Items[i].DayIndex;
            break;
        }
        if (visibleDay < 0 || stickyDay == visibleDay) return;
        Debug.Fail($"recents sticky day {stickyDay} != first visible row day {visibleDay} (flat sticky={stickyFlat} at={at})");
    }

    [Conditional("DEBUG")]
    static void ProbeFlatDate(RecentsSections sections, RecentsRow[] rows, int flat, TimeSpan offset, string where)
    {
        if ((uint)flat >= (uint)sections.Items.Length) return;
        var item = sections.Items[flat];
        if (item.Kind != RecentsFlatItemKind.Row) return;
        if ((uint)item.OriginalRowIndex >= (uint)rows.Length) return;
        if ((uint)item.DayIndex >= (uint)sections.HeaderDates.Length) return;
        long unixMs = rows[item.OriginalRowIndex].PlayedAtMs;
        DateOnly played = unixMs <= 0
            ? DateOnly.MinValue
            : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(offset).DateTime);
        DateOnly header = sections.HeaderDates[item.DayIndex];
        if (played == header) return;
        Debug.Fail($"recents {where} flat {flat}: row day {played} != header {header} (DayIndex={item.DayIndex})");
    }

    void StickyMetrics(ScrollGeometry geometry, out int header, out float push)
    {
        var layout = _groupedLayout;
        var sections = _sections;
        if (layout is null || sections.HeaderIndices.Length == 0)
        {
            header = -1; push = 0f; return;
        }
        header = layout.StickyHeaderIndexAt(geometry.OffsetY);
        if (header < 0 || (uint)header >= (uint)sections.Items.Length)
        {
            header = -1; push = 0f; return;
        }
        int day = sections.Items[header].DayIndex;
        if ((uint)(day + 1) >= (uint)sections.HeaderIndices.Length)
        {
            push = 0f; return;
        }
        int next = sections.HeaderIndices[day + 1];
        push = MathF.Min(0f,
            layout.OffsetOf(next, geometry.ViewportW) - geometry.OffsetY - DateHeaderHeight);
    }

    void InvokeDay(int dayIndex)
    {
        if ((uint)dayIndex < (uint)_sections.HeaderDates.Length)
            DateHeaderInvoked?.Invoke(_sections.HeaderDates[dayIndex]);
    }

    int CountForDay(int dayIndex)
    {
        var headers = _sections.HeaderIndices;
        if ((uint)dayIndex >= (uint)headers.Length) return 0;
        int start = headers[dayIndex];
        int end = dayIndex + 1 < headers.Length ? headers[dayIndex + 1] : _sections.Items.Length;
        return Math.Max(0, end - start - 1);
    }

    Element DayHeader(int dayIndex, bool overlay)
    {
        string label = (uint)dayIndex < (uint)_sections.HeaderLabels.Length ? _sections.HeaderLabels[dayIndex] : "";
        int n = CountForDay(dayIndex);
        return new BoxEl
        {
            Direction = 0, Height = DateHeaderHeight, Grow = overlay ? 1f : 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Fill = overlay ? Tok.FillLayerDefault : ColorF.Transparent,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = () => InvokeDay(dayIndex),
            Children =
            [
                WaveeType.ModuleHeader(label) with
                {
                    Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, AlignSelf = FlexAlign.Center,
                    Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
                Caption(n > 0 ? Strings.Recents.ItemCount(n) : "") with
                {
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                },
            ],
        };
    }

    sealed class StickyDayHeader : Component
    {
        readonly RecentsPage _page;
        public StickyDayHeader(RecentsPage page) => _page = page;

        public override Element Render()
        {
            int flatIndex = _page._stickyHeader.Value;
            int day = (uint)flatIndex < (uint)_page._sections.Items.Length
                ? _page._sections.Items[flatIndex].DayIndex : -1;
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f,
                Height = DateHeaderHeight,
                HitTestVisible = day >= 0,
                HitTestPassThrough = true,
                Opacity = day >= 0 ? 1f : 0f,
                Transform = Prop.Of(() => Affine2D.Translation(0f, _page._stickyPush.Value)),
                OnPointerWheel = ForwardWheel,
                Children = [_page.DayHeader(day, overlay: true)],
            };
        }

        void ForwardWheel(WheelEventArgs e)
        {
            // WheelEventArgs.Delta is the same signed DIP the viewport path consumes (InputDispatcher.ScrollBy
            // does offset + delta; ItemsView.ControllerScrollBy does OffsetY + request.Delta). Do not negate.
            _page._scrollController.ScrollBy(e.Delta);
            e.Handled = true;
        }
    }

    sealed class RecentsRowSlot : Component
    {
        readonly RecentsPage _page;
        readonly BoundItemScope<RecentsFlatItem> _scope;

        public RecentsRowSlot(RecentsPage page, BoundItemScope<RecentsFlatItem> scope) { _page = page; _scope = scope; }

        public override Element Render()
        {
            var flat = _scope.Item.Value;
            _ = _scope.Index.Value;
            if (flat.Kind == RecentsFlatItemKind.DateHeader) return _page.DayHeader(flat.DayIndex, overlay: false);
            int rowIndex = flat.OriginalRowIndex;
            if ((uint)rowIndex >= (uint)_page._rows.Length) return new BoxEl { Height = RowHeight };
            var row = _page._rows[rowIndex];
            return Embed.Comp(() => new HydratedRecentsRow(_page, row, rowIndex)) with
            { Key = "recents-row:" + row.ItemId };
        }
    }

    sealed class HydratedRecentsRow : Component
    {
        readonly RecentsPage _page;
        readonly RecentsRow _initialRow;
        readonly int _initialRowIndex;

        public HydratedRecentsRow(RecentsPage page, RecentsRow initialRow, int initialRowIndex)
        {
            _page = page; _initialRow = initialRow; _initialRowIndex = initialRowIndex;
        }

        public override Element Render()
        {
            var facts = UseLoadable<RowFacts>();
            int epoch = _page._epoch.Value;
            RecentsRow row = LiveRow();
            bool expanded = string.Equals(_page._expandedRow.Value, row.ItemId, StringComparison.Ordinal);
            UseEffect(() =>
            {
                var resolved = _page.FactsFor(LiveRow());
                if (resolved.Title is { Length: > 0 }) facts.SetReady(resolved);
                else facts.SetPending(default);
            }, DepKey.From(epoch));
            return Skel.Region(facts,
                content: resolved => _page.RowContent(LiveRow(), resolved, _initialRowIndex, expanded),
                reveal: SkelReveal.FadeOnly,
                smoothResize: false);
        }

        RecentsRow LiveRow()
        {
            if ((uint)_initialRowIndex < (uint)_page._rows.Length)
            {
                RecentsRow live = _page._rows[_initialRowIndex];
                if (string.Equals(live.ItemId, _initialRow.ItemId, StringComparison.Ordinal))
                    return live;
            }
            return _initialRow;
        }
    }

    /// <summary>Resolve a row's display facts FROM THE STORE. Liked Songs is answered locally — the app ships that cover
    /// and that name, so the one entity kind the catalogue kinds cannot address costs no request at all.</summary>
    RowFacts FactsFor(RecentsRow row)
    {
        if (RecentsView.HydrationUri(row) is not { Length: > 0 } uri) return default;
        return FactsFor(uri);
    }

    RowFacts FactsFor(string uri)
    {
        var kind = RecentsList.EntityKindOf(uri);
        if (kind == RecentsEntityKind.Collection)
            return new RowFacts(Loc.Get(Strings.Detail.LikedSongs), null, null);
        if (_store is not { } store) return default;
        return kind switch
        {
            RecentsEntityKind.Playlist => store.GetPlaylist(uri) is { } p
                ? new RowFacts(NullIfEmpty(p.Name), NullIfEmpty(p.OwnerName), p.Cover) : default,
            RecentsEntityKind.Album => store.GetAlbum(uri) is { } a
                ? new RowFacts(NullIfEmpty(a.Name), ArtistNames(a.Artists), a.Cover) : default,
            RecentsEntityKind.Artist => store.GetArtist(uri) is { } ar
                ? new RowFacts(NullIfEmpty(ar.Name), null, ar.Image) : default,
            RecentsEntityKind.Show => store.GetShow(uri) is { } sh
                ? new RowFacts(NullIfEmpty(sh.Name), NullIfEmpty(sh.Publisher), sh.Cover) : default,
            RecentsEntityKind.Episode => store.GetEpisode(uri) is { } ep
                ? new RowFacts(NullIfEmpty(ep.Title), NullIfEmpty(ep.ShowName), ep.Image) : default,
            RecentsEntityKind.Track => store.GetTrack(uri) is { } t
                ? new RowFacts(NullIfEmpty(t.Title), ArtistNames(t.Artists), t.Image) : default,
            _ => default,
        };
    }

    static readonly LayoutTransition DrawerReveal = new(
        TransitionChannels.Size | TransitionChannels.Opacity | TransitionChannels.Position,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Dy: -Spacing.S, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
        ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics(),
        Size: SizeMode.Reflow,
        Anchor: SizeAnchor.Leading);

    Element RowContent(RecentsRow row, RowFacts facts, int displayIndex, bool expanded)
    {
        if (row.ItemId.Length == 0) return new BoxEl { Height = RowHeight };
        // Unhydrated: the REAL row geometry with neutral placeholder tiles. Never empty space, and never an invented
        // string — the wire genuinely does not know this row's name yet.
        string uri = RecentsView.HydrationUri(row) ?? row.Uri;
        var kind = RecentsList.EntityKindOf(uri);
        // Liked Songs: the app's own cover art keys off the canonical collection uri, and `spotify:user:{id}:collection`
        // is the SAME entity under the recents surface's spelling. Handing the canonical one to the card is what makes
        // the bundled cover (and now-playing matching) resolve; navigation still goes through the shared dispatcher.
        string artUri = kind == RecentsEntityKind.Collection ? LikedSongsArtwork.Uri : uri;
        string when = RecentsView.PlayedAt(row.PlayedAtMs, _now, _culture);
        // "Played N tracks": the group's authoritative child_count (group_metadata field 1). NEVER ChildUris.Count —
        // the server truncates that list (a child_count of 11 arrived with 3 uris). A real PLURAL key, not a "×N"
        // glyph: the generator emits the typed Strings.Recents.PlayedCount(count) from the ICU template, so a language
        // whose one/other split differs from English gets its own branch instead of an English-shaped multiplier.
        var metaDecision = RecentsView.MetaFor(row);
        string phrase = metaDecision.Kind switch
        {
            RecentsMetaKind.PlayedCount => Strings.Recents.PlayedCount(metaDecision.Count),
            RecentsMetaKind.SavedCount => Strings.Recents.SavedCount(metaDecision.Count),
            _ => "",
        };
        // Prototype row: title + subtitle (two lines), time as its own trailing column — never a third meta line
        // inside the 64-DIP band.
        string owner = facts.Subtitle ?? "";
        string sub = phrase.Length == 0 ? owner
            : owner.Length == 0 ? phrase
            : phrase + " · " + owner;
        Element? savedLine = row.Reason == RecentsReason.Saved ? SavedMeta(sub.Length > 0 ? sub : phrase) : null;

        if (row.Kind == RecentsRowKind.Single)
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = [TrackRowContent(row, facts, displayIndex, uri) with { Key = "row" }] };

        bool canExpand = CanExpand(row);
        Element chevron = canExpand
            ? TrackRow.ExpandChevron(expanded, () => ToggleExpanded(row)) with { Transition = MotionTok.DisclosureChevron }
            : new BoxEl { Width = Spacing.XXL, Height = Spacing.XXL, Shrink = 0f };
        var trailingKids = new List<Element>(2);
        if (when.Length > 0)
            trailingKids.Add(Caption(when) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 });
        trailingKids.Add(chevron);
        Element trailing = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = trailingKids.ToArray(),
        };

        Element card = MediaCard.Row(
            facts.Cover, facts.Title!, savedLine is null ? sub : "", artUri,
            circular: kind == RecentsEntityKind.Artist,
            onClick: canExpand ? () => ToggleExpanded(row) : () => Open(row),
            onPlay: () => Play(uri),
            typeChip: KindLabel(kind),
            metaContent: savedLine,
            trailing: trailing,
            plated: false,
            leadingArtwork: row.Reason == RecentsReason.Saved ? context => SavedArtwork(row, context) : null,
            // Shared-element source. Tagged for the FIRST occurrence of this uri only — uris repeat down a recents list
            // (~1,388 repeats on a real account) and two live nodes under one MorphId is a duplicate-key bug: the
            // engine's registry is last-writer-wins, and SetTaggedVisible/SetTaggedOpacity hide EVERY node carrying the
            // flying key, so a second tagged row would blank itself mid-fly.
            //
            // Nothing flies today, and the missing half is NOT DetailShell's `MorphKey = null` — it is the forward
            // CAPTURE. SharedTransition.Begin has no callers left anywhere in the app (3b80bbcf8 removed them), and
            // ConnectedAnimation captures nowhere else, so no snapshot is ever taken. See the long note at
            // DetailShell.cs's `MorphKey = null` for the full finding. This stays the source half of the pair, minted
            // through the ONE shared convention (MorphKeys.For) so the two sides cannot drift while it is dormant.
            morphKey: Morphable(displayIndex) ? MorphKeys.For(DetailKindOf(kind), uri) : null);
        // Prototype rows are flat (transparent rest, hover wash). Card lift physics belong to plated home/search cards.
        Element primary = new BoxEl { Key = "row", Direction = 1, Children = [card] };
        Element? drawer = expanded && canExpand ? Drawer(row) : null;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = drawer is null ? [primary] : [primary, drawer],
        };
    }

    /// <summary>The defensive single-play arm. Zero occurrences in real captured data (9,446 items → 1,708 headers,
    /// 7,738 collapsed members, 0 ungrouped singles), but the grouping transform can still emit one, so the path exists
    /// and reuses the shared track cell rather than inventing a second row vocabulary.</summary>
    static Element SavedMeta(string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Icon(Icons.Check, 12f, Tok.SystemFillSuccess) with { Shrink = 0f },
            Caption(text) with
            {
                Color = Tok.SystemFillSuccess, Weight = 600, Grow = 1f, Basis = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    static bool CanExpand(RecentsRow row)
    {
        if (row.ChildCount <= 0 || row.ChildUris is not { Count: > 0 } children) return false;
        for (int i = 0; i < children.Count; i++) if (children[i].Length > 0) return true;
        return false;
    }

    void ToggleExpanded(RecentsRow row)
    {
        bool closing = string.Equals(_expandedRow.Peek(), row.ItemId, StringComparison.Ordinal);
        _expandedRow.Value = closing ? "" : row.ItemId;
        if (!closing) HydrateChildren(row, RecentsView.BatchCap);
    }

    Element Drawer(RecentsRow row)
    {
        var children = row.ChildUris!;
        var rendered = new List<Element>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            string uri = children[i];
            if (uri.Length == 0) continue;
            rendered.Add(new BoxEl
            {
                Key = "child-wrap:" + row.ItemId + ":" + i,
                Direction = 1,
                Enter = new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
                Transition = MotionTok.DisclosureExpand,
                Children =
                [
                    Embed.Comp(() => new HydratedChildRow(this, uri, i)) with
                    { Key = "child:" + row.ItemId + ":" + i },
                ],
            });
        }
        // Spine: a 1px divider descending from the parent art, children hanging off it (TrackVersionsPanel
        // connector idiom; proto `.kid-wrap{margin-left:30px;padding-left:26px;border-left}`).
        return new BoxEl
        {
            Key = "drawer:" + row.ItemId,
            Direction = 0, MinWidth = 0f, ClipToBounds = true,
            Margin = new Edges4(Spacing.XXL + Spacing.S, Spacing.XXS, 0f, Spacing.S),
            Animate = DrawerReveal,
            Children =
            [
                new BoxEl
                {
                    Width = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Padding = new Edges4(Spacing.L + Spacing.S, 0f, Spacing.S, 0f),
                    Stagger = WaveeMotion.StaggerMs,
                    Children = rendered.ToArray(),
                },
            ],
        };
    }

    sealed class HydratedChildRow : Component
    {
        readonly RecentsPage _page;
        readonly string _initialUri;
        readonly int _index;
        public HydratedChildRow(RecentsPage page, string initialUri, int index)
        {
            _page = page; _initialUri = initialUri; _index = index;
        }

        public override Element Render()
        {
            var facts = UseLoadable<RowFacts>();
            int epoch = _page._epoch.Value;
            UseEffect(() =>
            {
                var resolved = _page.FactsFor(_initialUri);
                if (resolved.Title is { Length: > 0 }) facts.SetReady(resolved);
                else facts.SetPending(default);
            }, DepKey.From(epoch));
            return Skel.Region(facts, resolved => _page.ChildRowContent(_initialUri, resolved, _index),
                reveal: SkelReveal.FadeOnly, smoothResize: false);
        }
    }

    Element ChildRowContent(string uri, RowFacts facts, int index)
    {
        long duration = _store?.GetTrack(uri)?.DurationMs
            ?? _store?.GetEpisode(uri)?.DurationMs ?? 0;
        string time = duration > 0 ? DetailFormat.TrackTime(duration) : "";
        string subtitle = facts.Subtitle ?? "";
        var titleKids = new List<Element>(2)
        {
            WaveeType.TrackTitle(facts.Title ?? "") with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (subtitle.Length > 0)
            titleKids.Add(Caption("· " + subtitle) with
            {
                Color = Tok.TextTertiary, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        return new BoxEl
        {
            Direction = 0, Height = ChildRowHeight, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll,
            HoverFill = Tok.FillSubtleSecondary,
            Children =
            [
                new BoxEl
                {
                    Width = Spacing.XL, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    Children =
                    [
                        Caption((index + 1).ToString(_culture)) with { Color = Tok.TextTertiary, MaxLines = 1 },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                    Children = titleKids.ToArray(),
                },
                Caption(time) with { Color = Tok.TextTertiary, Shrink = 0f },
            ],
        };
    }

    Element SavedArtwork(RecentsRow row, Element context)
    {
        var children = row.ChildUris;
        var layers = new List<Element>(3);
        if (children is not null)
        {
            int shown = 0;
            for (int i = 0; i < children.Count && shown < 2; i++)
            {
                string uri = children[i];
                if (uri.Length == 0) continue;
                var facts = FactsFor(uri);
                float x = shown == 0 ? Spacing.M : Spacing.L;
                layers.Add(new BoxEl
                {
                    Width = WaveeSize.Thumb40, Height = WaveeSize.Thumb40,
                    Transform = Affine2D.Translation(x, 0f),
                    Children = [Surfaces.Artwork(facts.Cover, uri.GetHashCode() & 0x7fffffff,
                        WaveeSize.Thumb40, WaveeSize.Thumb40, Radii.Control)],
                });
                shown++;
            }
        }
        layers.Add(new BoxEl
        {
            Width = WaveeSize.Thumb48, Height = WaveeSize.Thumb48,
            Transform = Affine2D.Translation(0f, Spacing.S),
            Children = [context],
        });
        return new BoxEl
        {
            Width = WaveeSize.Thumb48 + Spacing.M, Height = WaveeSize.Thumb48 + Spacing.S,
            Shrink = 0f, ZStack = true, Children = layers.ToArray(),
        };
    }

    Element TrackRowContent(RecentsRow row, RowFacts facts, int displayIndex, string uri)
    {
        _ = row;    // the single arm has no group facts to state — its identity is entirely the track's
        var track = _store?.GetTrack(uri)
                    ?? new Track(HomeCardNav.Id(uri), uri, facts.Title ?? "", Array.Empty<ArtistRef>(),
                                 new AlbumRef("", "", ""), 0L, false, facts.Cover);
        var columns = new ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: false,
            Heart: false, Thumb: true, Actions: false);
        Element title = WaveeType.TrackTitle(facts.Title ?? "") with
        { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        return TrackRow.Grid(track, displayIndex, default, columns, SingleRowTracks, RowHeight, title,
            showTrackArtist: false, _go, onPlay: () => Play(uri));
    }

    /// <summary>The single arm's column widths: # · thumb · title* · the duration lane. Static because the shape never
    /// varies — this surface has no width tiers.</summary>
    static readonly TrackSize[] SingleRowTracks =
        [TrackSize.Px(36f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(1f), TrackSize.Px(52f)];

    bool Morphable(int rowIndex)
    {
        var flags = _morphable;
        return (uint)rowIndex < (uint)flags.Length && flags[rowIndex];
    }

    static DetailKind DetailKindOf(RecentsEntityKind kind) => kind switch
    {
        RecentsEntityKind.Album => DetailKind.Album,
        _ => DetailKind.Playlist,
    };

    /// <summary>The trailing capsule names what the row IS — a recents list mixes every entity kind, and without it a
    /// playlist and an album read as the same card. Existing keys only; this page adds none of its own.</summary>
    static string? KindLabel(RecentsEntityKind kind) => kind switch
    {
        RecentsEntityKind.Album => Loc.Get(Strings.Home.Album),
        RecentsEntityKind.Artist => Loc.Get(Strings.Home.Artist),
        RecentsEntityKind.Show => Loc.Get(Strings.Podcast.Show),
        RecentsEntityKind.Episode => Loc.Get(Strings.Podcast.Episodes),
        RecentsEntityKind.Track => Loc.Get(Strings.Detail.Column.Song),
        RecentsEntityKind.Playlist => Loc.Get(Strings.Nav.Playlist),
        _ => null,   // Collection's title already says "Liked Songs"; Unknown names nothing it can vouch for
    };

    static string? ArtistNames(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return null;
        if (artists.Count == 1) return NullIfEmpty(artists[0].Name);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count; i++)
        {
            if (artists[i].Name.Length == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(artists[i].Name);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ── navigation ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Opening a row goes to its context. Routed through the SHARED card dispatcher, not a local switch — the
    /// two surfaces that already own one drifted apart over exactly this (the Liked branch).</summary>
    void Open(RecentsRow row)
    {
        if (RecentsView.HydrationUri(row) is not { Length: > 0 } uri) return;
        var facts = FactsFor(row);
        var card = new HomeCard(uri, facts.Title ?? "", facts.Subtitle, facts.Cover,
            CardKindOf(RecentsList.EntityKindOf(uri)),
            // OwnerName, not Subtitle: PlaylistSummary's third slot IS the owner, and a playlist's store row already
            // resolves LIST_METADATA_V2's `source` into OwnerName for exactly that role.
            Meta: facts.Subtitle is { Length: > 0 } owner ? new HomeCardMeta(OwnerName: owner) : null);
        HomeCardNav.Open(card, _preview, _go, playTrack: null);
    }

    void Play(string uri)
    {
        if (_svc is not { } svc) return;
        // A track/episode plays itself; everything else is a CONTEXT the player starts from the top of.
        if (RecentsList.EntityKindOf(uri) is RecentsEntityKind.Track or RecentsEntityKind.Episode)
            _ = svc.Player.PlayTrackAsync(uri);
        else _ = svc.Player.PlayAsync(uri, 0);
    }

    static HomeCardKind CardKindOf(RecentsEntityKind kind) => kind switch
    {
        RecentsEntityKind.Track => HomeCardKind.Track,
        RecentsEntityKind.Album => HomeCardKind.Album,
        RecentsEntityKind.Artist => HomeCardKind.Artist,
        RecentsEntityKind.Show => HomeCardKind.Podcast,
        RecentsEntityKind.Episode => HomeCardKind.Episode,
        RecentsEntityKind.Collection => HomeCardKind.Liked,
        _ => HomeCardKind.Playlist,
    };

    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    string? LabelOf(string? token)
    {
        if (token is null) return null;
        for (int i = 0; i < _chipTokens.Length; i++)
            if (string.Equals(_chipTokens[i], token, StringComparison.OrdinalIgnoreCase)) return _chipLabels[i];
        return null;
    }

    void SelectChip(string? label)
    {
        string? token = null;
        if (label is not null)
            for (int i = 0; i < _chipLabels.Length; i++)
                if (string.Equals(_chipLabels[i], label, StringComparison.Ordinal)) { token = _chipTokens[i]; break; }
        if (string.Equals(token, _chip.Peek(), StringComparison.Ordinal)) return;
        _chip.Value = token;
        Recut(token);      // CLIENT-SIDE: re-cut the loaded snapshot. No request carries a filter parameter.
    }

    /// <summary>The chip's visible label. A real key for each content type the CAPTURE proves this list carries
    /// (`content_type_music`, `content_type_podcasts` — 1,703 and 5 headers respectively); the wire token itself for
    /// anything else, because a data-derived name is honest where an invented one is not and a content type the server
    /// adds tomorrow stays renderable today. No key is minted for a token that has never been observed.</summary>
    string LabelFor(string token)
    {
        if (string.Equals(token, "music", StringComparison.OrdinalIgnoreCase))
            return Loc.Get(Strings.Recents.Chip.Music);
        if (string.Equals(token, "podcasts", StringComparison.OrdinalIgnoreCase))
            return Loc.Get(Strings.Recents.Chip.Podcasts);
        return RecentsView.ChipLabel(token, _culture);
    }

    // ── snapshot lifecycle ────────────────────────────────────────────────────────────────────────────────────────────
    async Task<RecentsSnapshot> FetchResourceAsync(IRecentsSource source, CancellationToken ct)
    {
        if (!_hasSnapshot)
        {
            var initial = await source.FetchAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _post(() => Adopt(initial));
            return initial;
        }
        byte[]? revision = null;
        if (_revision is { Length: > 0 } hex)
        {
            try { revision = Convert.FromHexString(hex); } catch (FormatException) { revision = null; }
        }
        var rows = _rows;
        var fresh = await source.FetchDiffAsync(revision, rows, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (fresh is null) return new RecentsSnapshot(_revision, rows);
        _post(() => Adopt(fresh));
        return fresh;
    }

    /// <summary>Install a snapshot. Hydration SURVIVES for free: the display facts live in the store, keyed by entity
    /// uri, so a diff that reorders or extends the list re-renders instantly against what is already resident and asks
    /// the network only for the genuinely new pointers.</summary>
    void Adopt(RecentsSnapshot snapshot)
    {
        var rows = CopyRows(snapshot.Rows);
        _rows = rows;
        _morphable = RecentsView.FirstOccurrence(rows);
        _revision = snapshot.Revision;
        _hasSnapshot = true;

        var tokens = RecentsView.ContentTypes(rows);
        _chipTokens = new string[tokens.Count];
        _chipLabels = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) { _chipTokens[i] = tokens[i]; _chipLabels[i] = LabelFor(tokens[i]); }
        // A chip that no longer exists in the new snapshot cannot stay selected.
        string? token = _chip.Peek();
        if (token is not null && LabelOf(token) is null) { token = null; _chip.Value = null; }

        _inflight.Clear();
        Recut(token);
        _epoch.Value++;
    }

    /// <summary>Re-cut the display array for a chip token. The row array is untouched — filtering is a VIEW, so a chip
    /// switch can never lose hydration or reach the network.</summary>
    void Recut(string? token)
    {
        var rows = _rows;
        var map = RecentsView.Filter(rows, token);
        RecentsRow[] display;
        if (token is null)
        {
            display = rows;   // the identity cut shares the array outright — no copy
        }
        else
        {
            display = new RecentsRow[map.Length];
            for (int i = 0; i < map.Length; i++) display[i] = rows[map[i]];
        }
        _display = display;
        _sections = RecentsView.BuildSections(rows, map, _now, _culture);
        _calendar = RecentsView.DayDensity(rows, map, _now, _culture);
        _calendarDay.Value = DateOnly.FromDateTime(_now.LocalDateTime);
        _vc.ReplaceSnapshot(_sections.Items);
        _expandedRow.Value = "";
        _stickyHeader.Value = -1;
        _stickyPush.Value = 0f;
        _shapeEpoch.Value++;
    }

    // ── viewport hydration ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The realized window moved. Called from the reconciler's realize path, so it does the cheapest possible
    /// thing: record the range and arm ONE pump. The pump then reads the LATEST range — which is how work for a range
    /// that has already scrolled away is dropped before it is ever started.</summary>
    void OnVisibleRange(int first, int end)
    {
        _rangeFirst = first;
        _rangeEnd = end;
        if (_pumpArmed) return;
        _pumpArmed = true;
        _post(Pump);
    }

    void Pump()
    {
        _pumpArmed = false;
        if (_storeDirty) { _storeDirty = false; _epoch.Value++; }
        if (_metadata is not { } metadata || _cts is not { } cts) return;
        _batch.Clear();
        RecentsView.CollectRange(_rows, _sections.FlatToRow, _rangeFirst, _rangeEnd, Pending, _batch);
        int hi = Math.Min(_rangeEnd, _sections.FlatToRow.Length);
        for (int i = Math.Max(0, _rangeFirst); i < hi && _batch.Count < RecentsView.BatchCap; i++)
        {
            int rowIndex = _sections.FlatToRow[i];
            if ((uint)rowIndex >= (uint)_rows.Length || _rows[rowIndex].Reason != RecentsReason.Saved) continue;
            RecentsView.CollectChildUris(_rows[rowIndex], Pending, _batch,
                Math.Min(2, RecentsView.BatchCap - _batch.Count));
        }
        if (_batch.Count == 0) return;
        var uris = _batch.ToArray();
        for (int i = 0; i < uris.Length; i++) _inflight.Add(uris[i]);
        _ = HydrateAsync(metadata, uris, cts.Token);
    }

    void HydrateChildren(RecentsRow row, int cap)
    {
        if (_metadata is not { } metadata || _cts is not { } cts || cap <= 0) return;
        var pending = new List<string>(Math.Min(cap, RecentsView.BatchCap));
        RecentsView.CollectChildUris(row, Pending, pending, Math.Min(cap, RecentsView.BatchCap));
        if (pending.Count == 0) return;
        var uris = pending.ToArray();
        for (int i = 0; i < uris.Length; i++) _inflight.Add(uris[i]);
        _ = HydrateAsync(metadata, uris, cts.Token);
    }

    /// <summary>Which URIs this window still owes the chokepoint. Freshness/dedup/skip belong to MetadataService — this
    /// only avoids handing the same uri to two overlapping SyncAllAsync calls, and skips the kinds that resolve
    /// LOCALLY: Liked Songs ships with the app, and an uri whose kind the catalogue cannot address would be dropped by
    /// KindFor anyway.</summary>
    bool Pending(string uri)
    {
        if (_inflight.Contains(uri)) return false;
        return RecentsList.EntityKindOf(uri) is RecentsEntityKind.Track or RecentsEntityKind.Album
            or RecentsEntityKind.Artist or RecentsEntityKind.Show or RecentsEntityKind.Episode
            or RecentsEntityKind.Playlist;
    }

    async Task HydrateAsync(Wavee.Backend.Metadata.MetadataService metadata, string[] uris, CancellationToken ct)
    {
        try
        {
            // closeRefs:false — the track-ref closure walks TRACK rows looking for blank album refs, and a recents
            // window is entity pointers, not a tracklist. FeatureId keeps the desktop client's per-surface attribution
            // on whatever this actually has to fetch (a cache/304 hit sends nothing at all).
            //
            // headerTraits:true — and ONLY here. The census ties the 178/179/220 bundle to `mdata_esperanto`
            // specifically; the other bulk callers on this same chokepoint (the 500-uri discography prefetch, the
            // 300-uri tracklist loaders) carry different client-feature-ids and must keep asking for one kind each.
            // A viewport batch is at most RecentsView.BatchCap uris, so the extra kinds cost a bounded handful of
            // bytes on a request the surface was making anyway.
            await metadata.SyncAllAsync(uris, ct, closeRefs: false, clientFeatureId: FeatureId, headerTraits: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort: rows keep their skeleton */ }
        if (ct.IsCancellationRequested) return;
        _post(() =>
        {
            for (int i = 0; i < uris.Length; i++) _inflight.Remove(uris[i]);
            // The store's own change signal usually beats us here; the bump is what guarantees the realized window
            // re-reads even when the projection wrote nothing new.
            _epoch.Value++;
        });
    }

    /// <summary>A store write landed. Coalesced onto the existing pump so a burst (a bulk projection, a playback
    /// heartbeat) costs ONE epoch bump and therefore one re-render of the realized window.</summary>
    void MarkStoreDirty()
    {
        _post(() =>
        {
            if (_rows.Length == 0) return;   // nothing realized to re-skin — a parked/empty page ignores the churn
            _storeDirty = true;
            if (_pumpArmed) return;
            _pumpArmed = true;
            _post(Pump);
        });
    }

    /// <summary>The wash's source card: the most recent row that has actually resolved a cover. Null until one has —
    /// a wash invented before any artwork landed would be a colour the page does not own.</summary>
    HomeCard? WashCard()
    {
        var rows = _display;
        int scan = Math.Min(rows.Length, 32);   // the wash is the TOP of the list, not a full-array search per render
        for (int i = 0; i < scan; i++)
        {
            var facts = FactsFor(rows[i]);
            if (facts.Cover?.Url is not { Length: > 0 }) continue;
            string uri = RecentsView.HydrationUri(rows[i]) ?? rows[i].Uri;
            return new HomeCard(uri, facts.Title ?? "", facts.Subtitle, facts.Cover,
                CardKindOf(RecentsList.EntityKindOf(uri)));
        }
        return null;
    }
}
