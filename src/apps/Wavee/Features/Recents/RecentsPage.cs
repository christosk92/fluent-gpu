using System;
using System.Collections.Generic;
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
    const int OverscanRows = 6;
    const float PageInset = Spacing.XXL;
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
    static readonly RecentsRow EmptyRow = new(RecentsRowKind.Group, "", "", null, null, null, null, 0, 0,
        RecentsEntityKind.Unknown);

    // ── reactive surface (three signals; everything else is a plain field the slots read at render time) ──────────────
    /// <summary>Bumped when hydration lands in the STORE (or a snapshot is adopted). The bound projection carries it, so
    /// exactly the realized slots re-render — the DetailTracks mechanism.</summary>
    readonly Signal<int> _epoch = new(0);
    /// <summary>The selected content-type TOKEN (wire spelling), null = "All". Never a label — the label is derived.</summary>
    readonly Signal<string?> _chip = new(null);
    /// <summary>0 = loading · 1 = ready · 2 = settled empty (offline, or an account with no plays).</summary>
    readonly Signal<int> _state = new(0);
    readonly object _washOwner = new();

    // ── the snapshot, owned as plain arrays (never a signal: a 1,708-element list is not a value to diff) ─────────────
    // The rows stay POINTERS for their whole life. Nothing here is ever rewritten with hydrated text — that lives in the
    // store, which is shared, persisted and updated by every other surface too.
    RecentsRow[] _rows = Array.Empty<RecentsRow>();          // wire order
    RecentsRow[] _display = Array.Empty<RecentsRow>();       // the chip's cut of _rows (== _rows when nothing is selected)
    int[] _displayToRow = Array.Empty<int>();
    bool[] _morphable = Array.Empty<bool>();                 // first occurrence of each uri → may claim the shared-element tag
    string[] _chipTokens = Array.Empty<string>();
    string[] _chipLabels = Array.Empty<string>();
    string? _revision;

    /// <summary>The resident collection the viewport reads through. Snapshot-backed on purpose: recents arrives whole,
    /// so virtualization here is about MOUNTED UI, not remote paging.</summary>
    readonly VirtualCollection<RecentsRow> _vc = VirtualCollection<RecentsRow>.FromSnapshot(ReadOnlyMemory<RecentsRow>.Empty);

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
    readonly record struct RowsView(int Epoch, int Version, VirtualCollection<RecentsRow> Rows);

    /// <summary>What a row displays, resolved from the STORE at render time. Never stored on the row.</summary>
    readonly record struct RowFacts(string? Title, string? Subtitle, Image? Cover);

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
        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = LoadAsync(svc.Recents, cts.Token);
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
        int state = _state.Value;
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
                if (_cts is { } live) _ = RevalidateAsync(svc.Recents, live.Token);
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

        Element body = state switch
        {
            0 => LoadingRows(),
            2 => EmptyState(),
            _ => List(token),
        };

        var kids = new List<Element>(3) { hero };
        if (chips is not null)
            kids.Add(new BoxEl { Padding = new Edges4(PageInset, 0f, PageInset, 0f), Children = [chips] });
        kids.Add(new BoxEl
        {
            Grow = 1f, Direction = 1, MinHeight = 0f,
            Padding = new Edges4(PageInset, 0f, PageInset, 0f),
            // The FLIP: a chip switch changes the list's identity (below), and this wrapper glides the swap instead of
            // cutting to a differently-sized list. Motion.ReducedMotion is a VALUE, so this is a null vs a transition,
            // never a divergent hook path.
            Layout = Motion.ReducedMotion
                ? null
                : new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                    TransitionDynamics.Tween(220f, Easing.SmoothOut),
                    Enter: new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                    Exit: new EnterExit(Opacity: 0f, Active: true)),
            Children = [body],
        });

        _ = epoch;   // read above; the explicit subscription this chrome depends on
        return new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = kids.ToArray() };
    }

    // ── masthead ──────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>An oversized, light display cut of the surface's own name over one thin metadata line. The stagger lives
    /// on the CONTAINER (two children), the enter on each line — the engine's own idiom.</summary>
    Element Hero()
    {
        // The count is the page's ONE authored word on this line; the window either side of it stays culture-table
        // formatting (RecentsView owns no copy and is engine-free — see its Summary doc for the seam).
        string summary = RecentsView.Summary(_rows, _now, _culture, static n => Strings.Recents.ItemCount(n));
        var lines = new List<Element>(2)
        {
            WaveeType.SurfaceDisplay(Loc.Get(Strings.Home.Recents)) with
            {
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
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
            Stagger = Motion.ReducedMotion ? 0f : HeroStaggerMs,
            Children = lines.ToArray(),
        };
    }

    static Element EmptyState() => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(Loc.Get(Strings.Sidebar.Section.EmptyRecents))
                { Size = 14f, LineHeight = 20f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap },
        ],
    };

    /// <summary>The cold shape: the REAL row skeleton, so the swap to content is a fill rather than a re-layout.</summary>
    static Element LoadingRows()
    {
        var rows = new Element[8];
        for (int i = 0; i < rows.Length; i++) rows[i] = SkeletonRow() with { Key = "recents-skel:" + i };
        return new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = rows };
    }

    // ── the list ──────────────────────────────────────────────────────────────────────────────────────────────────────
    Element List(string? token)
    {
        // Hoisted stateful layout: a measured layout carries the Fenwick extent table + the scroll anchor, so rebuilding
        // one per render would throw that state away every frame.
        var layout = UseMemo(static () => new MeasuredStackVirtualLayout(RowHeight), DepKey.Empty);
        var view = UseComputed(() => new RowsView(_epoch.Value, _vc.Version.Value, _vc));
        var items = UseMemo(() => BoundItems.Project(
            view,
            static s => s.Rows.CountOr0,
            static (s, i) => s.Rows[i] ?? EmptyRow,
            EmptyRow), DepKey.Empty);

        return ItemsView.CreateBound(
            items,
            scope => Embed.Comp(() => new RecentsRowSlot(this, scope)),
            RepeatLayout.Measured(layout),
            new ListOptions<RecentsRow>
            {
                // The rows are cards with their own chrome; a list selector here would be a second, competing cue.
                SelectionMode = ItemsSelectionMode.None,
                Selector = SelectorVisual.None,
                IsItemInvokedEnabled = true,
                OnInvokedTyped = (_, row) => Open(row),
                ItemTextTyped = (_, row) => FactsFor(row).Title ?? "",
                Overscan = OverscanRows,
                Grow = 1f,
                // One recycle pool per row KIND: a group card's slot must never rebind into the (defensive) track-grid
                // shape — a cross-shape reuse forces a full rebuild instead of a cheap rebind.
                ContentType = ContentTypeOf,
                Scroll = new ScrollOptions { ScrollKey = "recents:" + (token ?? "all"), AutoEdgeFade = true },
                // The engine's own cold-realize stagger: bounded to the REALIZED window by construction, which is the
                // only kind of entrance a 1,708-row list may have.
                Entrance = new EntranceOptions { StaggerColdRealize = !Motion.ReducedMotion },
                // The point of the page: the realized window moved → hydrate what it still misses.
                OnVisibleRange = OnVisibleRange,
            }) with { Key = "recents-list:" + (token ?? "all") };
    }

    int ContentTypeOf(int index)
    {
        var display = _display;
        return (uint)index < (uint)display.Length ? (int)display[index].Kind : 0;
    }

    // ── rows ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A recents row is a card ONE realized slot renders. Its own component so the slot re-renders on its own
    /// item subscription (an index rebind, or a hydration epoch) without the page re-rendering.</summary>
    sealed class RecentsRowSlot : Component
    {
        readonly RecentsPage _page;
        readonly BoundItemScope<RecentsRow> _scope;

        public RecentsRowSlot(RecentsPage page, BoundItemScope<RecentsRow> scope) { _page = page; _scope = scope; }

        public override Element Render()
        {
            var row = _scope.Item.Value;      // subscribe: the recycled index AND the hydration epoch resolve here
            int index = _scope.Index.Value;
            return _page.RowContent(row, index);
        }
    }

    /// <summary>Resolve a row's display facts FROM THE STORE. Liked Songs is answered locally — the app ships that cover
    /// and that name, so the one entity kind the catalogue kinds cannot address costs no request at all.</summary>
    RowFacts FactsFor(RecentsRow row)
    {
        if (RecentsView.HydrationUri(row) is not { Length: > 0 } uri) return default;
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

    Element RowContent(RecentsRow row, int displayIndex)
    {
        if (row.ItemId.Length == 0) return new BoxEl { Height = RowHeight };
        var facts = FactsFor(row);
        // Unhydrated: the REAL row geometry with neutral placeholder tiles. Never empty space, and never an invented
        // string — the wire genuinely does not know this row's name yet.
        if (facts.Title is not { Length: > 0 }) return SkeletonRow();

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
        string meta = row.ChildCount > 1
            ? (when.Length > 0 ? Strings.Recents.PlayedCount(row.ChildCount) + " · " + when
                               : Strings.Recents.PlayedCount(row.ChildCount))
            : when;

        if (row.Kind == RecentsRowKind.Single) return TrackRowContent(row, facts, displayIndex, uri);

        Element card = MediaCard.Row(
            facts.Cover, facts.Title!, facts.Subtitle ?? "", artUri,
            circular: kind == RecentsEntityKind.Artist,
            onClick: () => Open(row),
            onPlay: () => Play(uri),
            typeChip: KindLabel(kind),
            meta: meta,
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
        // The app's ONE card hover/press physics (lift + press scale on the shared motion token), applied to a plain
        // wrapper because MediaCard.Row hands back an Element and the physics are a BoxEl transform. Under reduced
        // motion the tokens collapse to no movement on their own — this is not a branch.
        return MediaCard.ApplyCardPhysics(new BoxEl { Direction = 1, Children = [card] });
    }

    /// <summary>The defensive single-play arm. Zero occurrences in real captured data (9,446 items → 1,708 headers,
    /// 7,738 collapsed members, 0 ungrouped singles), but the grouping transform can still emit one, so the path exists
    /// and reuses the shared track cell rather than inventing a second row vocabulary.</summary>
    Element TrackRowContent(RecentsRow row, RowFacts facts, int displayIndex, string uri)
    {
        _ = row;    // the single arm has no group facts to state — its identity is entirely the track's
        var track = _store?.GetTrack(uri)
                    ?? new Track(HomeCardNav.Id(uri), uri, facts.Title!, Array.Empty<ArtistRef>(),
                                 new AlbumRef("", "", ""), 0L, false, facts.Cover);
        var columns = new ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: false,
            Heart: false, Thumb: true, Actions: false);
        Element title = WaveeType.TrackTitle(facts.Title!) with
        { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        return TrackRow.Grid(track, displayIndex, default, columns, SingleRowTracks, RowHeight, title,
            showTrackArtist: false, _go, onPlay: () => Play(uri));
    }

    /// <summary>The single arm's column widths: # · thumb · title* · the duration lane. Static because the shape never
    /// varies — this surface has no width tiers.</summary>
    static readonly TrackSize[] SingleRowTracks =
        [TrackSize.Px(36f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(1f), TrackSize.Px(52f)];

    static Element SkeletonRow() => new BoxEl
    {
        Direction = 0, Height = RowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Corners = Radii.CardAll,
        Children =
        [
            // A null url resolves to the neutral opaque placeholder tile — the same one a real cover loads over.
            Surfaces.Shimmer(null, 48, 48, WaveeSize.Thumb48, WaveeSize.Thumb48, Radii.Control),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XS,
                Children =
                [
                    Surfaces.Shimmer(null, 0, 0, 168f, 12f, 4f),
                    Surfaces.Shimmer(null, 0, 0, 108f, 10f, 4f),
                ],
            },
        ],
    };

    bool Morphable(int displayIndex)
    {
        var map = _displayToRow;
        var flags = _morphable;
        if ((uint)displayIndex >= (uint)map.Length) return false;
        int r = map[displayIndex];
        return (uint)r < (uint)flags.Length && flags[r];
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
        HomeCardNav.Open(card, _preview, _go, u => _ = _svc?.Player.PlayTrackAsync(u));
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
    async Task LoadAsync(IRecentsSource source, CancellationToken ct)
    {
        RecentsSnapshot snapshot;
        try { snapshot = await source.FetchAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _post(() => { if (_rows.Length == 0) _state.Value = 2; });
            return;
        }
        if (ct.IsCancellationRequested) return;
        _post(() => Adopt(snapshot));
    }

    /// <summary>Revision-gated revalidation. A NULL answer means "unchanged" and the correct response is to do NOTHING —
    /// no rebuild, no re-hydration, no scroll disturbance.</summary>
    async Task RevalidateAsync(IRecentsSource source, CancellationToken ct)
    {
        if (_rows.Length == 0) return;
        byte[]? revision = null;
        if (_revision is { Length: > 0 } hex)
        {
            try { revision = Convert.FromHexString(hex); } catch (FormatException) { revision = null; }
        }
        var rows = _rows;
        RecentsSnapshot? fresh;
        try { fresh = await source.FetchDiffAsync(revision, rows, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return; }
        if (fresh is null || ct.IsCancellationRequested) return;   // unchanged
        _post(() => Adopt(fresh));
    }

    /// <summary>Install a snapshot. Hydration SURVIVES for free: the display facts live in the store, keyed by entity
    /// uri, so a diff that reorders or extends the list re-renders instantly against what is already resident and asks
    /// the network only for the genuinely new pointers.</summary>
    void Adopt(RecentsSnapshot snapshot)
    {
        var incoming = snapshot.Rows;
        var rows = new RecentsRow[incoming.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = incoming[i];
        _rows = rows;
        _morphable = RecentsView.FirstOccurrence(rows);
        _revision = snapshot.Revision;

        var tokens = RecentsView.ContentTypes(rows);
        _chipTokens = new string[tokens.Count];
        _chipLabels = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) { _chipTokens[i] = tokens[i]; _chipLabels[i] = LabelFor(tokens[i]); }
        // A chip that no longer exists in the new snapshot cannot stay selected.
        string? token = _chip.Peek();
        if (token is not null && LabelOf(token) is null) { token = null; _chip.Value = null; }

        _inflight.Clear();
        Recut(token);
        _state.Value = rows.Length == 0 ? 2 : 1;
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
        _displayToRow = map;
        _display = display;
        _vc.ReplaceSnapshot(display);
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
        RecentsView.CollectRange(_rows, _displayToRow, _rangeFirst, _rangeEnd, Pending, _batch);
        if (_batch.Count == 0) return;
        var uris = _batch.ToArray();
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
