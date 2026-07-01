using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
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
    // Playlist/queue mutations for THIS context (add the context's tracks to the queue / a user playlist).
    Action AddToQueue, Action AddToPlaylist,
    // Open a related album / "Featured on" playlist card. Unlike Go (a bare route flip), these stash the card's partial
    // model first so the destination takes DetailPage's in-place fast path instead of a full skeleton remount. See DetailNav.
    Action<Album> OpenAlbum, Action<PlaylistSummary> OpenPlaylist,
    // A 1-element cell the TrackList fills with "play the VISIBLE (sorted/filtered) order from the top"; the rail's big
    // Play late-binds through it (null until the list mounts → falls back to Play(0)). Optional so other constructions
    // (LibraryPage) compile unchanged.
    Action?[]? PlayAllOverride = null);

// The two-column detail scaffold (mounted only once data is Ready, so its lifecycle = the loaded page's lifecycle).
// Owns: the art-derived backdrop wash + accent, the page-scoped Mica tint (set/cleared through the activation
// lifecycle), and the now-playing re-skin epoch. Delegates the rail / track list / trailing to the static builders.
sealed class DetailShell : Component
{
    // The model is a Loadable: the HEADER (cover/title/artist) renders immediately from its current value (the partial
    // preview the Home card had, or the loaded model on a deep link) and updates in place when the full model arrives;
    // the TRACK LIST streams in via the engine's Skel.Region inside TrackList. MorphKey is applied to the header each
    // render so the cover stays a connected-animation participant.
    readonly Signal<Route> _route;        // read reactively → ONE shell serves successive detail routes (kind/cfg/morphKey re-derived)
    readonly Loadable<DetailModel> _model;
    readonly Image? _fallbackCover;       // the cover the Home card flew in: kept if the loaded model resolves a null
                                          // cover, so a connected-animation fly never lands on a bare placeholder
    string? _ctxUri;                      // the loaded context uri — the per-context sort key; refreshed each render
    DetailConfig _cfg = DetailConfig.Album;   // derived from route kind + loaded ReleaseKind each render (reused slot re-derives)
    readonly object _tintOwner = new();   // identity for race-free last-writer-wins on ShellTint (see ShellTintState)
    readonly Signal<int> _mode = new(0);  // adaptive layout mode (0 widest), written by OnBoundsChanged
    readonly Signal<TrackSort> _sort = new(TrackSort.Default);   // track-list sort, persisted per context (loaded per route)
    readonly Signal<string> _query = new("");                    // filter search query (transient — clears on navigation)
    readonly Signal<TrackFilterFlags> _filterFlags = new(TrackFilterFlags.None);   // quick-filter toggles (transient)
    readonly Signal<int> _density = new(1);                      // row density 0..3 (app-wide, persisted)

    public DetailShell(Signal<Route> route, Loadable<DetailModel> model, Image? fallbackCover = null)
    { _route = route; _model = model; _fallbackCover = fallbackCover; }

    // Per-context persisted-sort keys (each album/playlist remembers its own sort). Keyed by the context uri so two
    // different lists never share a sort; falls back to the kind when a context has no uri.
    SettingKey<int> SortColKey() => new("detail.sort.col:" + (_ctxUri ?? _cfg.RailWidth.ToString()), 0);
    SettingKey<bool> SortDescKey() => new("detail.sort.desc:" + (_ctxUri ?? _cfg.RailWidth.ToString()), false);

    // Adaptive layout by the page's own width: 0 Wide (full rail) · 1 Mid (rail 224) · 2 Narrow (rail 188, still
    // two-column) · 3 Vertical (rail collapses to a top header, list below). Sized so the right track area keeps a
    // usable width before the vertical switch.
    const int Vertical = 3;
    static int ModeFor(float w) => w <= 0f ? 0 : w >= 820f ? 0 : w >= 660f ? 1 : w >= 560f ? 2 : Vertical;
    static float RailW(int mode, DetailConfig cfg) => mode switch { 0 => cfg.RailWidth, 1 => 224f, _ => 188f };

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var libBridge = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var shellTint = UseContext(ShellTint.Slot);
        var navPreview = UseContext(NavPreviewStore.Slot);   // in-app card nav stashes a preview → destination reconciles in place
        var morph = UseContext(SharedTransition.Begin);      // connected-animation cover fly, same as a Home card

        var route = _route.Value;                      // subscribe → re-derive kind/cfg/morphKey on a detail-route swap (reused slot)
        var (kind, id) = DetailPage.ParseDetail(route);
        string? morphKey = MorphKeys.For(kind, id);

        var raw = _model.Value.Value;                  // subscribe → re-render preview→full (header updates in place)
        _cfg = DetailPage.ResolveConfig(kind, raw);    // release-kind-dependent (album→single); a reused slot re-derives it
        _ctxUri = raw.ContextUri;                      // the per-context sort key, refreshed as the model loads
        // Keep the flown-in cover if the loaded model resolved a null one — a fly must land on real art, never a bare
        // placeholder (defensive: the fake catalog is already consistent for a uri; a real backend may not be).
        var m = raw with { MorphKey = morphKey, Cover = raw.Cover ?? _fallbackCover };

        // Page entrance: a soft focus-in (opacity only, NO translate/scale — a geometric transform would shift the cover's
        // laid-out rect that the connected-animation fly targets). blur:0 is deliberate and load-bearing for smoothness: a
        // full-page BlurSigma reveal is a render-to-texture + separable-blur pass over the WHOLE page every frame, which on
        // a FRESH mount (covers still decoding/uploading, skeleton cross-dissolving) pushes the GPU render past one 120Hz
        // vblank → the page entered at ~60fps for ~250ms. A cached KeepAlive revisit re-activates without re-mounting so it
        // never paid this — which is exactly why fresh nav dropped to 60 while revisits stayed 120 (measured: 13ms vs 7ms
        // GPU submit). The opacity fade alone gives the same "resolves in" read at compositor cost, and the cover still
        // flies in on top — fresh nav now holds 120fps. Do not re-add a full-page blur here.
        this.UseSoftReveal(dy: 0f, blur: 0f);

        // "Is THIS context the one currently playing?" — a cheap O(1) test against a mount-time id set, so the wash /
        // tint use the live-track palette only when the playing track belongs to this page (else neutral / no tint).
        var trackIds = UseMemo(() =>
        {
            var s = new HashSet<string>(m.Tracks.Count);
            for (int i = 0; i < m.Tracks.Count; i++) s.Add(m.Tracks[i].Id);
            return s;
        }, raw);

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
        // when there is no real palette ⇒ plain Mica. Liked (single-column) never tints.
        ColorF? micaTint = (art is null || !_cfg.TwoColumn) ? null : (WaveePalette.TintedDark(art) with { A = 0.14f });

        // ── page-scoped Mica tint via the activation lifecycle (reconciler-hooks §0bis) ──
        // SET on mount + colour change (UseEffect) and on REACTIVATION (UseActivation.onActivated — a cached page does
        // not re-run the mount effect); CLEAR on park (UseActivation.onDeactivated), which KeepAlive always fires before
        // it evicts/unmounts a backgrounded page, so navigating away (incl. to a non-detail page) reverts to plain Mica.
        // Every clear is owner-gated so an A→B navigation lands on B's colour no matter which effect fires first.
        if (shellTint is not null)
        {
            void SetTint(ColorF? c) => shellTint.Value = new ShellTintState(c, _tintOwner);
            void ClearTint() { if (ReferenceEquals(shellTint.Peek().Owner, _tintOwner)) shellTint.Value = default; }

            UseEffect(() => SetTint(micaTint), micaTint?.GetHashCode() ?? 0);
            UseActivation(onActivated: () => SetTint(micaTint), onDeactivated: ClearTint);
        }

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
            if (svc is null) return;
            int n = Math.Min(m.Tracks.Count, 50);
            for (int i = 0; i < n; i++) _ = svc.Player.EnqueueAsync(m.Tracks[i].Uri);
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
        var settings = svc?.Settings;
        UseEffect(() =>
        {
            // Re-keyed per context: on a detail-route swap (reused slot) load THIS page's persisted sort + density and
            // clear the transient search/quick-filters so they never bleed across pages.
            _query.Value = "";
            _filterFlags.Value = TrackFilterFlags.None;
            if (settings is null) { _sort.Value = TrackSort.Default; return; }
            _sort.Value = new TrackSort((SortColumn)settings.Get(SortColKey()), settings.Get(SortDescKey()));
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
            _query, _filterFlags, f => _filterFlags.Value = f, _density, SetDensity, AddToQueue, AddToPlaylist,
            a => DetailNav.OpenAlbum(navPreview, morph, go, a),
            p => DetailNav.OpenPlaylist(navPreview, morph, go, p),
            playAllOverride);

        // Viewport-size context signal — resolved UNCONDITIONALLY here (rules of hooks): the positional-hook cursor must
        // see the SAME hook sequence on every render, but the branches below differ (single-column / vertical / two-column),
        // and only the two-column path needs the window height. Reading `.Value` (the subscription) is deferred to that
        // branch so single-column / vertical pages don't take a needless re-render on every resize.
        var viewportSig = UseContextSignal(Viewport.Size);

        // Single-column (liked): just the track table, full width, no rail / no wash (its toolbar stays in the chrome).
        if (!_cfg.TwoColumn)
            return Embed.Comp(() => new TrackList(_route, _model, bridge, handlers));

        // Adaptive two-column / vertical: measure the page width → mode. Value-gated → re-render only on a breakpoint cross.
        void Measure(RectF r) { if (r.W > 0f) { int md = ModeFor(r.W); if (md != _mode.Peek()) _mode.Value = md; } }
        int mode = _mode.Value;   // subscribe → re-render on mode change

        // The track list (drops columns by breakpoint, owns the now-playing re-skin + an external SelectionModel). In
        // VERTICAL mode its toolbar moves into the rail header, so drop it from the chrome there.
        bool showToolbar = mode != Vertical;
        // Right column = the track table OR the episode list (podcast shows). Distinct Keys so an album↔show swap in the
        // reused detail slot remounts the column cleanly instead of reconciling TrackList against EpisodeList.
        Element right = _cfg.Content == DetailContent.Episodes
            ? new BoxEl { Key = "right:eps", Grow = 1f, Direction = 1, Children = [Embed.Comp(() => new EpisodeList(_route, _model, bridge, handlers, showToolbar))] }
            : new BoxEl { Key = "right:tracks", Grow = 1f, Direction = 1, Children = [Embed.Comp(() => new TrackList(_route, _model, bridge, handlers, showToolbar))] };

        // VERTICAL (narrow): the rail HEADER (cover, title, meta, play + toolbar) fixed on top + the list (Grow=1)
        // scrolling below — a single column over the wash. (The list remounts on the two-column↔vertical cross, so its
        // scroll resets there; the rail-width modes 0/1/2 keep it mounted.)
        if (mode == Vertical)
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Gradient = Surfaces.HeroWash(washColor), OnBoundsChanged = Measure,
                Children = [DetailRail.BuildHeader(m, _cfg, handlers), right],
            };

        // TWO-COLUMN: a centered, max-width row [rail | right] over a top-anchored art wash (behind both columns). The
        // rail width shrinks with the mode; the track list stays mounted across modes 0/1/2 (same row position).
        // Best-effort fit: the cover stays STRETCHED to the full width (a big hero); the TEXT gives — the title font
        // scales CONTINUOUSLY with the window height, and the description's line cap drops on a short window. Keyed on the
        // WINDOW height (known at mount + identical across navigation), NOT a post-layout measurement — so the title never
        // jumps/flickers on a nav and resizes smoothly. The rail's scrollbar stays the last resort.
        float railW = RailW(mode, _cfg);
        float winH = viewportSig.Value.Height;   // subscribe (only here) → re-fit smoothly on resize (stable per page → no nav jump)
        float titleSize = Math.Clamp(24f + (winH - 620f) * 0.05f, 24f, 38f);   // 620px window → 24px … 900px → 38px
        int descLines = winH < 760f ? 3 : 6;
        var row = new BoxEl
        {
            Direction = 0, Grow = 1f, MaxWidth = 1600f,
            Children = [DetailRail.Build(m, _cfg, handlers, railW, titleSize, descLines), right],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Justify = FlexJustify.Center,
            Gradient = Surfaces.HeroWash(washColor), OnBoundsChanged = Measure,
            Children = [row],
        };
    }
}
