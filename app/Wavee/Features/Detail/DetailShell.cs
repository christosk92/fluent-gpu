using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
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
    IReadSignal<int> Density, Action<int> SetDensity);

// The two-column detail scaffold (mounted only once data is Ready, so its lifecycle = the loaded page's lifecycle).
// Owns: the art-derived backdrop wash + accent, the page-scoped Mica tint (set/cleared through the activation
// lifecycle), and the now-playing re-skin epoch. Delegates the rail / track list / trailing to the static builders.
sealed class DetailShell : Component
{
    readonly DetailModel _m;
    readonly DetailConfig _cfg;
    readonly object _tintOwner = new();   // identity for race-free last-writer-wins on ShellTint (see ShellTintState)
    readonly Signal<int> _mode = new(0);  // adaptive layout mode (0 widest), written by OnBoundsChanged
    readonly Signal<TrackSort> _sort = new(TrackSort.Default);   // track-list sort, persisted per context (loaded at mount)
    readonly Signal<string> _query = new("");                    // filter search query (transient — clears on navigation)
    readonly Signal<TrackFilterFlags> _filterFlags = new(TrackFilterFlags.None);   // quick-filter toggles (transient)
    readonly Signal<int> _density = new(1);                      // row density 0..3 (app-wide, persisted)

    public DetailShell(DetailModel m, DetailConfig cfg) { _m = m; _cfg = cfg; }

    // Per-context persisted-sort keys (each album/playlist remembers its own sort). Keyed by the context uri so two
    // different lists never share a sort; falls back to the kind when a context has no uri.
    SettingKey<int> SortColKey() => new("detail.sort.col:" + (_m.ContextUri ?? _cfg.RailWidth.ToString()), 0);
    SettingKey<bool> SortDescKey() => new("detail.sort.desc:" + (_m.ContextUri ?? _cfg.RailWidth.ToString()), false);

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
        var go = UseContext(HistoryStore.NavCtx);
        var shellTint = UseContext(ShellTint.Slot);

        // "Is THIS context the one currently playing?" — a cheap O(1) test against a mount-time id set, so the wash /
        // tint use the live-track palette only when the playing track belongs to this page (else neutral / no tint).
        var trackIds = UseMemo(() =>
        {
            var s = new HashSet<string>(_m.Tracks.Count);
            for (int i = 0; i < _m.Tracks.Count; i++) s.Add(_m.Tracks[i].Id);
            return s;
        }, _m);

        Track? cur = bridge?.CurrentTrack.Value;     // subscribe → re-derive wash/tint on track change (rare)
        Palette? livePal = bridge?.TrackPalette.Value;   // subscribe
        bool thisPlaying = cur is not null && trackIds.Contains(cur.Id);
        // _m.Palette is the future per-page art palette (GetPaletteAsync, §9 gap 2); until then, the live-track palette
        // when this page is playing, else none — the feature degrades to "no tint", never a wrong colour.
        Palette? art = _m.Palette ?? (thisPlaying ? livePal : null);

        ColorF washColor = WaveePalette.BackgroundDark(art ?? WaveePalette.Neutral);
        ColorF accent = art is null ? Tok.AccentDefault : WaveePalette.Accent(art);
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
        void Play(int index) { if (_m.ContextUri is { } uri && svc is not null) _ = svc.Player.PlayAsync(uri, Math.Max(0, index)); }
        void PlayContext(string uri) { if (svc is not null) _ = svc.Player.PlayAsync(uri, 0); }
        void Shuffle()
        {
            if (_m.ContextUri is not { } uri || svc is null) return;
            _ = svc.Player.SetShuffleAsync(true);
            _ = svc.Player.PlayAsync(uri, 0);
        }
        // ── persisted per-context sort: load once at mount, save on every change (must be assigned BEFORE handlers
        // captures SetSort, which closes over `settings`) ──
        var settings = svc?.Settings;
        UseEffect(() =>
        {
            if (settings is null) return;
            _sort.Value = new TrackSort((SortColumn)settings.Get(SortColKey()), settings.Get(SortDescKey()));
            _density.Value = settings.Get(WaveeSettings.RowDensity);
        });
        void SetSort(TrackSort s)
        {
            _sort.Value = s;
            settings?.Set(SortColKey(), (int)s.Column);
            settings?.Set(SortDescKey(), s.Descending);
        }
        void SetDensity(int d) { _density.Value = d; settings?.Set(WaveeSettings.RowDensity, d); }   // app-wide

        // SetSort / SetDensity are hoisted local functions; the rail + chrome toolbars read all list-view controls off here.
        var handlers = new DetailHandlers(Play, () => Play(0), Shuffle, PlayContext, go, accent, _sort, SetSort,
            _query, _filterFlags, f => _filterFlags.Value = f, _density, SetDensity);

        // Single-column (liked): just the track table, full width, no rail / no wash (its toolbar stays in the chrome).
        if (!_cfg.TwoColumn)
            return Embed.Comp(() => new TrackList(_m, _cfg, bridge, handlers));

        // Adaptive two-column / vertical: measure the page width → mode. Value-gated → re-render only on a breakpoint cross.
        void Measure(RectF r) { if (r.W > 0f) { int md = ModeFor(r.W); if (md != _mode.Peek()) _mode.Value = md; } }
        int mode = _mode.Value;   // subscribe → re-render on mode change

        // The track list (drops columns by breakpoint, owns the now-playing re-skin + an external SelectionModel). In
        // VERTICAL mode its toolbar moves into the rail header, so drop it from the chrome there.
        bool showToolbar = mode != Vertical;
        Element right = Embed.Comp(() => new TrackList(_m, _cfg, bridge, handlers, showToolbar));

        // VERTICAL (narrow): the rail HEADER (cover, title, meta, play + toolbar) fixed on top + the list (Grow=1)
        // scrolling below — a single column over the wash. (The list remounts on the two-column↔vertical cross, so its
        // scroll resets there; the rail-width modes 0/1/2 keep it mounted.)
        if (mode == Vertical)
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Gradient = Surfaces.HeroWash(washColor), OnBoundsChanged = Measure,
                Children = [DetailRail.BuildHeader(_m, _cfg, handlers), right],
            };

        // TWO-COLUMN: a centered, max-width row [rail | right] over a top-anchored art wash (behind both columns). The
        // rail width shrinks with the mode; the track list stays mounted across modes 0/1/2 (same row position).
        var row = new BoxEl
        {
            Direction = 0, Grow = 1f, MaxWidth = 1600f,
            Children = [DetailRail.Build(_m, _cfg, handlers, RailW(mode, _cfg)), right],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Justify = FlexJustify.Center,
            Gradient = Surfaces.HeroWash(washColor), OnBoundsChanged = Measure,
            Children = [row],
        };
    }
}
