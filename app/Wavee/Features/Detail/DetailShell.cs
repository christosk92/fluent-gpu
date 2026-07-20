using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The delegates the rail / tracks / trailing builders need (kept off the frozen ctor path; rebuilt each
/// render so they close over live state). <see cref="Play"/>/<see cref="PlayAll"/>/<see cref="Shuffle"/> act on THIS
/// page's context; <see cref="PlayContext"/> plays an arbitrary uri (the More-by cards). <see cref="Accent"/> is the
/// art-derived (or default) accent colour.</summary>
readonly record struct DetailHandlers(
    Action<int> Play, Action PlayAll, Action Shuffle, Action<string> PlayContext, Action<string, string?> Go, ColorF Accent,
    IReadSignal<TrackSort> Sort, Action<TrackSort> SetSort,
    // List-view controls surfaced by the chrome / header toolbar (search query, quick-filter flags, row density).
    Signal<string> Query, IReadSignal<TrackFilterFlags> Flags, Action<TrackFilterFlags> SetFlags,
    IReadSignal<int> Density, Action<int> SetDensity,
    // Playlist/queue mutations for THIS context (insert next / append to the queue / add to a user playlist).
    Action PlayNext, Action AddToQueue, Action AddToPlaylist,
    // Open a related album / "Featured on" playlist card. Unlike Go (a bare route flip), these stash the card's partial
    // model first so the destination takes DetailPage's in-place fast path instead of a full skeleton remount. See DetailNav.
    Action<Album> OpenAlbum, Action<PlaylistSummary> OpenPlaylist,
    // A 1-element cell the TrackList fills with "play the VISIBLE (sorted/filtered) order from the top"; the rail's big
    // Play late-binds through it (null until the list mounts → falls back to Play(0)). Optional so other constructions
    // (LibraryPage) compile unchanged.
    Action?[]? PlayAllOverride = null,
    IReadSignal<bool>? MultiSelect = null, Action<bool>? SetMultiSelect = null);

// The two-column detail scaffold (mounted only once data is Ready, so its lifecycle = the loaded page's lifecycle).
// Owns: the art-derived backdrop wash + accent, the page-scoped Mica tint (set/cleared through the activation
// lifecycle), and the now-playing re-skin epoch. Delegates the rail / track list / trailing to the static builders.
sealed class DetailShell : Component
{
    static readonly LayoutTransition PaletteWashTransition = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(420f, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(320f, Easing.SmoothOut));

    // The model is a Loadable: the HEADER (cover/title/artist) renders immediately from its current value (the partial
    // preview the Home card had, or the loaded model on a deep link) and updates in place when the full model arrives;
    // the TRACK LIST streams in via the engine's Skel.Region inside TrackList. Connected cover animation is intentionally
    // disabled here so it cannot compete with the route-level page transition.
    readonly Signal<Route> _route;        // read reactively → ONE shell serves successive detail routes (kind/cfg/morphKey re-derived)
    readonly Loadable<DetailModel> _model;
    readonly Signal<DetailHandlers?> _liveHandlers = new(null);   // reactive parent→TrackList props; never freeze accent/actions
    readonly Image? _fallbackCover;       // preview cover retained if the loaded model resolves a null cover
    string? _ctxUri;                      // the loaded context uri — the per-context sort key; refreshed each render
    DetailConfig _cfg = DetailConfig.Album;   // derived from route kind + loaded ReleaseKind each render (reused slot re-derives)
    readonly object _tintOwner = new();   // identity for race-free last-writer-wins on ShellTint (see ShellTintState)
    readonly Signal<int> _mode = new(0);  // adaptive layout mode (0 widest), written by OnBoundsChanged
    float _measuredW;                     // last measured page width — replayed once when the rail layout-lock clears (Task C)
    bool _modeInitialized;                // first measurement uses the nominal breakpoints; later vertical crosses hysteresis
    readonly Signal<bool> _verticalHeaderPinned = new(false);   // vertical detail header scrolled past the top -> pinned chrome bar
    readonly Signal<TrackSort> _sort = new(TrackSort.Default);   // track-list sort, persisted per context (loaded per route)
    readonly Signal<string> _query = new("");                    // filter search query (transient — clears on navigation)
    readonly Signal<TrackFilterFlags> _filterFlags = new(TrackFilterFlags.None);   // quick-filter toggles (transient)
    readonly Signal<int> _density = new(1);                      // row density 0..3 (app-wide, persisted)
    readonly Signal<bool> _multiSelect = new(false);             // ephemeral multi-select mode (clears on navigation)
    readonly IAppSettings? _settings;
    readonly Signal<float> _albumRailW;
    readonly Signal<float> _playlistRailW;

    public DetailShell(Signal<Route> route, Loadable<DetailModel> model, Image? fallbackCover = null, IAppSettings? settings = null)
    {
        _route = route; _model = model; _fallbackCover = fallbackCover; _settings = settings;
        _albumRailW = new(settings?.Get(WaveeSettings.DetailAlbumRailWidth) ?? WaveeSettings.DetailAlbumRailWidth.Default);
        _playlistRailW = new(settings?.Get(WaveeSettings.DetailPlaylistRailWidth) ?? WaveeSettings.DetailPlaylistRailWidth.Default);
    }

    // Per-context persisted-sort keys (each album/playlist remembers its own sort). Keyed by the context uri so two
    // different lists never share a sort; falls back to the kind when a context has no uri. The column default is a −1
    // "never chosen" sentinel so the fallback can be PER-KIND: playlists/albums open in custom (context) order, Liked
    // Songs opens added-date-newest-first (the Spotify collection default). An explicit user choice persists ≥ 0.
    SettingKey<int> SortColKey() => new("detail.sort.col:" + (_ctxUri ?? _cfg.RailWidth.ToString()), -1);
    SettingKey<bool> SortDescKey() => new("detail.sort.desc:" + (_ctxUri ?? _cfg.RailWidth.ToString()), false);
    TrackSort _defaultSort = TrackSort.Default;   // per-kind fallback, derived each render (DateAdded desc for Liked)

    // Adaptive layout by the page's own width: 0 Wide (full rail) · 1 Mid (rail 224) · 2 Narrow (rail 188, still
    // two-column) · 3 Vertical (rail collapses to a top header, list below). Sized so the right track area keeps a
    // usable width before the vertical switch.
    const int Vertical = DetailLayoutBreakpoints.VerticalMode;
    static int ModeFor(float w, int currentMode, bool initialized)
        => DetailLayoutBreakpoints.ModeFor(w, currentMode, initialized);
    static float RailW(int mode, DetailConfig cfg) => mode switch { 0 => cfg.RailWidth, 1 => 224f, _ => 188f };
    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = _settings ?? svc?.Settings;
        _ = AppearancePrefs.Epoch.Value;
        bool colorWashesDisabled = settings?.Get(WaveeSettings.DisableColorWashes) ?? false;
        var bridge = UseContext(PlaybackBridge.Slot);
        var libBridge = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var shellTint = UseContext(ShellTint.Slot);
        var navPreview = UseContext(NavPreviewStore.Slot);   // in-app card nav stashes a preview → destination reconciles in place
        var shellUi = UseContext(ShellUi.Slot);              // rail layout-defer lock (Task C): gate mode churn during a rail reflow
        bool railLocked = shellUi?.RailLayoutLocked.Value ?? false;   // subscribe → flush the settled mode when the lock clears

        var route = _route.Value;                      // subscribe → re-derive kind/cfg/morphKey on a detail-route swap (reused slot)
        var (kind, id) = DetailPage.ParseDetail(route);
        var raw = _model.Value.Value;                  // subscribe → re-render preview→full (header updates in place)
        _cfg = DetailPage.ResolveConfig(kind, raw);    // release-kind-dependent (album→single); a reused slot re-derives it
        _ctxUri = raw.ContextUri;                      // the per-context sort key, refreshed as the model loads
        _defaultSort = kind == DetailKind.Liked ? new TrackSort(SortColumn.DateAdded, Descending: true) : TrackSort.Default;
        // Keep the flown-in cover if the loaded model resolved a null one — a fly must land on real art, never a bare
        // placeholder (defensive: the fake catalog is already consistent for a uri; a real backend may not be).
        var m = raw with { MorphKey = null, Cover = raw.Cover ?? _fallbackCover };

        // ContentHost exclusively owns route-level entrance motion. Keep this shell free of a second full-page reveal so
        // cold mounts and cached KeepAlive revisits use the same duration and easing.

        // "Is THIS context the one currently playing?" — a cheap O(1) test against a mount-time id set, so the wash /
        // tint use the live-track palette only when the playing track belongs to this page (else neutral / no tint).
        var trackIds = UseMemo(() =>
        {
            var s = new HashSet<string>(m.Tracks.Count);
            for (int i = 0; i < m.Tracks.Count; i++) s.Add(m.Tracks[i].Id);
            return s;
        }, DepKey.FromRef(raw));

        Track? cur = bridge?.CurrentTrack.Value;     // subscribe → re-derive wash/tint on track change (rare)
        Palette? livePal = bridge?.TrackPalette.Value;   // subscribe
        bool thisPlaying = cur is not null && trackIds.Contains(cur.Id);
        // m.Palette is the page's cover-extracted palette; when absent, fall back to the live-track palette while THIS
        // page is playing, else none — so the page tints from its own art, degrading to "no tint", never a wrong colour.
        Palette? art = m.Palette ?? (thisPlaying ? livePal : null);

        // The cover palette's Spotify colorDark is near-black, so LIFT it for legibility. The live-track palette was
        // already tuned for the player chrome (the bar tint reads it raw), so keep IT raw — unchanged from before this
        // feature, so a currently-playing track on a null-palette page (Liked / show) doesn't suddenly brighten.
        ColorF accent = m.Palette is { } cover ? WaveePalette.Lift(WaveePalette.Accent(cover))
                      : thisPlaying && livePal is { } lp ? WaveePalette.Accent(lp)
                      : Tok.AccentDefault;
        // The hero wash. Dark: the art's dark background tone (or the neutral #1C1C1C). Light: a soft ACCENT band instead
        // — a neutral-dark wash over the off-white card just reads as a muddy gray smudge (HeroWash applies it at 22%).
        ColorF washColor = Tok.Theme == ThemeKind.Light
            ? accent
            : WaveePalette.BackgroundDark(art ?? WaveePalette.Neutral);
        // The Mica scrim colour: the art's tinted-dark tone at a low alpha so Mica keeps reading as Mica (≈0.14). Null
        // when there is no real palette ⇒ plain Mica.
        ColorF? micaTint = (colorWashesDisabled || art is null || !_cfg.TwoColumn) ? null : Tok.Theme == ThemeKind.Light
            ? WaveePalette.Lift(WaveePalette.ToColor(art.Light)) with { A = 0.05f }
            : WaveePalette.TintedDark(art) with { A = 0.14f };

        // ── page-scoped Mica tint via the activation lifecycle (reconciler-hooks §0bis) ──
        // SET on mount + colour change (UseEffect) and on REACTIVATION (UseActivation.onActivated — a cached page does
        // not re-run the mount effect); CLEAR on park (UseActivation.onDeactivated), which KeepAlive always fires before
        // it evicts/unmounts a backgrounded page, so navigating away (incl. to a non-detail page) reverts to plain Mica.
        // Every clear is owner-gated so an A→B navigation lands on B's colour no matter which effect fires first.
        void SetTint(ColorF? c)
        {
            if (shellTint is not null) shellTint.Value = new ShellTintState(c, _tintOwner);
        }
        void ClearTint()
        {
            if (shellTint is not null && ReferenceEquals(shellTint.Peek().Owner, _tintOwner)) shellTint.Value = default;
        }

        // This shell is reused across detail routes. Key the publication on the exact nullable colour and route instead
        // of their 32-bit hash: the latter could suppress a real palette change and did not force a fresh ownership
        // claim when A -> B happened to resolve to the same tint. Reference comparison is correct for the shell signal;
        // nullable ColorF and routeName compare by value.
        UseEffect(() => SetTint(micaTint), DepKey.From(HashCode.Combine(route.Name, micaTint.HasValue, micaTint.GetValueOrDefault(), Tok.Theme)));
        UseActivation(onActivated: () => SetTint(micaTint), onDeactivated: ClearTint);

        // ── handlers (close over live svc/model; not frozen ctor args) ──
        void Play(int index) { if (m.ContextUri is { } uri && svc is not null) _ = svc.Player.PlayAsync(uri, Math.Max(0, index)); }
        void PlayContext(string uri) { if (svc is not null) _ = svc.Player.PlayAsync(uri, 0); }
        void Shuffle()
        {
            if (m.ContextUri is not { } uri || svc is null) return;
            _ = svc.Player.SetShuffleAsync(true);
            _ = svc.Player.PlayAsync(uri, 0);
        }
        // Add-to-queue / add-to-playlist act on THIS context's tracks (capped batch); each confirms with a toast.
        void AddToQueue()
        {
            // Queueing is remote-only (local playback is unsupported). No active device ⇒ prompt to choose one (the toast
            // opens the player-bar device picker) instead of the old silent no-op.
            if (string.IsNullOrEmpty(bridge.ActiveDeviceId.Peek())) { bridge.NotifyLocalPlaybackUnsupported(); return; }
            int n = DetailQueueActions.AddToEnd(svc?.Player, m.Tracks);
            if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        }
        void PlayNext()
        {
            if (string.IsNullOrEmpty(bridge.ActiveDeviceId.Peek())) { bridge.NotifyLocalPlaybackUnsupported(); return; }
            int n = DetailQueueActions.PlayNext(svc?.Player, m.Tracks);
            if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        }
        void AddToPlaylist()
        {
            if (libBridge is null || m.Tracks.Count == 0) return;
            var (plUri, plName) = libBridge.AddToDefaultPlaylist(m.Tracks);
            Toasts.Show(Strings.Detail.AddedToPlaylist(plName), ToastSeverity.Success,
                actionLabel: Loc.Get(Strings.Detail.GoToPlaylist), onAction: () => go("pl:" + plUri, plName));
        }
        // ── persisted per-context sort: load once at mount, save on every change (must be assigned BEFORE handlers
        // captures SetSort, which closes over `settings`) ──
        UseEffect(() =>
        {
            // Re-keyed per context: on a detail-route swap (reused slot) load THIS page's persisted sort + density and
            // clear the transient search/quick-filters so they never bleed across pages.
            _query.Value = "";
            _filterFlags.Value = TrackFilterFlags.None;
            _multiSelect.Value = false;
            if (settings is null) { _sort.Value = _defaultSort; return; }
            int col = settings.Get(SortColKey());   // −1 sentinel = never chosen → the per-kind default (Liked: DateAdded desc)
            _sort.Value = col < 0 ? _defaultSort : new TrackSort((SortColumn)col, settings.Get(SortDescKey()));
            _density.Value = settings.Get(WaveeSettings.RowDensity);
        }, _ctxUri ?? "");
        void SetSort(TrackSort s)
        {
            _sort.Value = s;
            settings?.Set(SortColKey(), (int)s.Column);
            settings?.Set(SortDescKey(), s.Descending);
        }
        void SetDensity(int d) { _density.Value = d; settings?.Set(WaveeSettings.RowDensity, d); }   // app-wide

        // SetSort / SetDensity are hoisted local functions; the rail + chrome toolbars read all list-view controls off here.
        var playAllOverride = new Action?[1];   // TrackList fills [0] with the visible-order play; the rail's Play late-binds through it
        var handlers = new DetailHandlers(Play, () => { var ov = playAllOverride[0]; if (ov is not null) ov(); else Play(0); },
            Shuffle, PlayContext, go, accent, _sort, SetSort,
            _query, _filterFlags, f => _filterFlags.Value = f, _density, SetDensity, PlayNext, AddToQueue, AddToPlaylist,
            a => DetailNav.OpenAlbum(navPreview, go, a),
            p => DetailNav.OpenPlaylist(navPreview, go, p),
            playAllOverride,
            MultiSelect: _multiSelect, SetMultiSelect: v => _multiSelect.Value = v);
        // TrackList is retained across preview→palette hydration and route reuse. Publish after render so its accent and
        // context-closing actions update through the supported signal path instead of frozen constructor arguments.
        UseEffect(() => _liveHandlers.Value = handlers, DepKey.FromRef(handlers));

        // Viewport-size context signal — resolved UNCONDITIONALLY here (rules of hooks): the positional-hook cursor must
        // see the SAME hook sequence on every render, but the branches below differ (single-column / vertical / two-column),
        // and only the two-column path needs the window height. Reading `.Value` (the subscription) is deferred to that
        // branch so single-column / vertical pages don't take a needless re-render on every resize.
        var viewportSig = UseContextSignal(Viewport.Size);
        UseEffect(() => _verticalHeaderPinned.Value = false, route.Name);

        // Task C flush: when the rail layout-lock clears, apply the SETTLED mode once from the last measured width (the
        // intermediate reflow widths were skipped in Measure while locked). Keyed on railLocked so it fires on the
        // false-edge only; the write converges (dep is the bool, unchanged by writing _mode). Unconditional + placed
        // BEFORE the single-column early return so the hook order stays stable across the layout branches.
        UseLayoutEffect(() =>
        {
            if (!railLocked && _measuredW > 0f)
            {
                int md = ModeFor(_measuredW, _mode.Peek(), _modeInitialized);
                _modeInitialized = true;
                if (md != _mode.Peek()) _mode.Value = md;
            }
        }, railLocked);

        // Single-column fallback: just the track table, full width, no rail / no wash.
        if (!_cfg.TwoColumn)
            return Embed.Comp(() => new TrackList(_route, _model, bridge, handlers, liveHandlers: _liveHandlers));

        // Adaptive two-column / vertical: measure the page width → mode. Value-gated → re-render only on a breakpoint cross.
        void Measure(RectF r)
        {
            if (r.W <= 0f) return;
            _measuredW = r.W;
            if (shellUi?.RailLockActive == true) return;
            int md = ModeFor(r.W, _mode.Peek(), _modeInitialized);
            _modeInitialized = true;
            if (md != _mode.Peek()) _mode.Value = md;
        }
        int mode = _mode.Value;   // subscribe → re-render on mode change
        // Self-heal (fail-safe #2, mirroring TrackList's tier clamp): never RENDER a mode wider than the last measured
        // width supports — a stale mode signal (stuck rail lock / lost flush) would keep the two-column layout at a
        // width where its rail + tracks cannot coexist. Narrower-than-needed is fine; the next Measure widens it.
        if (_measuredW > 0f && shellUi?.RailLockActive != true) { int fit = ModeFor(_measuredW, mode, _modeInitialized); if (fit > mode) mode = fit; }
        // Page-layout preference: "Stacked" forces the vertical hero SYSTEM at every width for track pages — the
        // metadata rail is never composed; Automatic keeps the responsive rail↔hero behavior. The override is applied
        // at render time only (the _mode signal keeps tracking the real width, so flipping the setting back reverts
        // instantly). Epoch-subscribed → the Settings toggle re-renders any mounted (incl. KeepAlive-parked) page live.
        _ = DetailHeroPrefs.Epoch.Value;
        if (_cfg.Content == DetailContent.Tracks
            && (settings?.Get(WaveeSettings.DetailPageLayout) ?? DetailVerticalLayout.PageAuto) == DetailVerticalLayout.PageHero)
            mode = Vertical;
        bool verticalTracks = mode == Vertical && _cfg.Content == DetailContent.Tracks;
        if (!verticalTracks && _verticalHeaderPinned.Peek()) _verticalHeaderPinned.Value = false;

        // The track list (drops columns by breakpoint, owns the now-playing re-skin + an external SelectionModel). Its
        // view controls (filter / sort / row size) ride a responsive Fluent command bar in the list's OWN chrome (always
        // on for tracks; the rail owns the context actions); the podcast episode toolbar stays in the episode column on
        // wide layouts.
        bool showToolbar = _cfg.Content == DetailContent.Tracks || mode != Vertical;
        // Right column = the track table OR the episode list (podcast shows). Distinct Keys so an album↔show swap in the
        // reused detail slot remounts the column cleanly instead of reconciling TrackList against EpisodeList.
        // MinWidth = 300 (the narrowest FUNCTIONAL track table, just under the tier-5 minimum): the pane may shrink but
        // never below the width the column system can actually lay out. Steady state never gets here (ModeFor flips to
        // Vertical below 560px), so the floor only bites on TRANSIENT frames — a mid-spring rail reflow or a stale
        // breakpoint — where an unfloored pane fed the grid a width far below the active column set's fixed sum and the
        // overflow guard crushed columns into overlapping glyphs. With the floor the worst transient is a clean edge
        // clip at the card boundary, never glyph soup.
        Element right = _cfg.Content == DetailContent.Episodes
            ? new BoxEl { Key = "right:eps", Grow = 1f, Shrink = 1f, MinWidth = 300f, Direction = 1, Children = [Embed.Comp(() => new EpisodeList(_route, _model, bridge, handlers, showToolbar))] }
            : new BoxEl
            {
                Key = "right:tracks", Grow = 1f, Shrink = 1f, MinWidth = 300f, MinHeight = 0f, Direction = 1,
                Children =
                [
                    Embed.Comp(() => new TrackList(_route, _model, bridge, handlers, showToolbar,
                        verticalHeader: verticalTracks,
                        verticalHeaderPinned: _verticalHeaderPinned,
                        liveHandlers: _liveHandlers)) with { Key = verticalTracks ? "tracks:vertical" : "tracks:standard" },
                ],
            };

        // VERTICAL (narrow): the rail HEADER (cover, title, meta, play + toolbar) fixed on top + the list (Grow=1)
        // scrolling below — a single column over the wash. (The list remounts on the two-column↔vertical cross, so its
        // scroll resets there; the rail-width modes 0/1/2 keep it mounted.)
        if (mode == Vertical)
        {
            Element verticalContent = new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children = verticalTracks ? [right] : [DetailRail.BuildHeader(m, _cfg, handlers, _model), right],
            };
            // The pinned chrome bar now lives INSIDE TrackList's ZStack overlay so it floats over the list AND the album
            // trailing scroller and never remounts when a query/filter remounts the list.
            Element verticalBody = verticalContent;
            var verticalPage = new BoxEl
            {
                Key = "detail:vertical",
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children = [verticalBody],
            };
            return new BoxEl
            {
                ZStack = true, Grow = 1f, OnBoundsChanged = Measure, ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Key = "detail-wash:" + (art?.GetHashCode() ?? 0) + ":" + Tok.Theme + ":" + colorWashesDisabled,
                        ZStack = true, Grow = 1f, HitTestVisible = false,
                        Gradient = colorWashesDisabled ? null : Surfaces.HeroWash(washColor), Animate = PaletteWashTransition,
                    },
                    verticalPage,
                ],
            };
        }

        // TWO-COLUMN: a centered, max-width row [rail | right] over a top-anchored art wash (behind both columns). The
        // rail width shrinks with the mode; the track list stays mounted across modes 0/1/2 (same row position).
        // Best-effort fit: the cover stays STRETCHED to the full width (a big hero); the TEXT gives — the title font
        // scales CONTINUOUSLY with the window height, and the description's line cap drops on a short window. Keyed on the
        // WINDOW height (known at mount + identical across navigation), NOT a post-layout measurement — so the title never
        // jumps/flickers on a nav and resizes smoothly. The rail's scrollbar stays the last resort.
        bool resizableRail = mode == 0 && (kind == DetailKind.Album || kind == DetailKind.Playlist);
        Signal<float> railWidthSignal = kind == DetailKind.Playlist ? _playlistRailW : _albumRailW;
        float railW = mode == 0 && resizableRail ? railWidthSignal.Value : RailW(mode, _cfg);
        float winH = viewportSig.Value.Height;   // subscribe (only here) → re-fit smoothly on resize (stable per page → no nav jump)
        float titleSize = Math.Clamp(24f + (winH - 620f) * 0.05f, 24f, 38f);   // 620px window → 24px … 900px → 38px
        int descLines = winH < 760f ? 3 : 6;
        var row = new BoxEl
        {
            // flex:1 1 0 — the same contract every other growing content container in the chain already declares
            // (WaveeShell's content-side, the ContentHost page wrappers, LibraryPage's columns): Basis=0 makes THIS row's
            // width the AVAILABLE content region rather than its children's intrinsic (rail + full track-table) width, and
            // Shrink=1 + MinWidth=0 let it yield when the rail opens and the region narrows. This is the ONE node on the
            // [rail | right] chain that previously declared neither, so it kept FlexShrink's Yoga-default 0 and its
            // intrinsic width — overflowing the narrowed content card, whose ClipToBounds then hard-cut the right columns
            // mid-glyph ("Plays"→"Pl") instead of the table reflowing to a tighter tier. `right` already shrinks (below);
            // the fix is to let its PARENT shrink so the reduced width actually reaches it.
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, MaxWidth = 1600f,
            Children = resizableRail
                ? [
                    DetailRail.Build(m, _cfg, handlers, railW, titleSize, descLines, _model),
                    DetailRailGrip(railWidthSignal, kind, settings),
                    right,
                  ]
                : [DetailRail.Build(m, _cfg, handlers, railW, titleSize, descLines, _model), right],
        };
        var twoColumnPage = new BoxEl
        {
            Key = "detail:two-column",
            Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
            ClipToBounds = true,
            Children = [row],
        };
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            OnBoundsChanged = Measure, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = "detail-wash:" + (art?.GetHashCode() ?? 0) + ":" + Tok.Theme + ":" + colorWashesDisabled,
                    ZStack = true, Grow = 1f, HitTestVisible = false,
                    Gradient = colorWashesDisabled ? null : Surfaces.HeroWash(washColor), Animate = PaletteWashTransition,
                },
                twoColumnPage,
            ],
        };
    }

    // Same persisted splitter implementation as LibraryPage's artist columns. It owns a 7-DIP hit strip around a
    // persistent 1px seam; width writes are direct during drag and committed to settings only on release.
    static Element DetailRailGrip(Signal<float> width, DetailKind kind, IAppSettings? settings) => new BoxEl
    {
        Width = 7f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children =
        [
            Embed.Comp(() => new ColumnGrip(width, 220f, 480f, () =>
                {
                    var key = kind == DetailKind.Playlist
                        ? WaveeSettings.DetailPlaylistRailWidth
                        : WaveeSettings.DetailAlbumRailWidth;
                    settings?.Set(key, width.Peek());
                }))
                // DetailShell is reused album↔playlist. Component ctor arguments are mount-stable, so key by width family
                // to remount the grip with the correct signal + persistence key on a cross-kind route.
                with { Key = "detail-rail-grip:" + kind },
        ],
    };
}
