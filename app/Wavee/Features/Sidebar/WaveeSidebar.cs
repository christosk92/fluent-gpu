using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Row 2 left — the bespoke WaveeMusic SidebarView (SidebarView.xaml / SidebarStyles.xaml): 300 expanded / 56 compact,
// with Pinned (drop-zone), Your Library (async count badges + Local "Soon"), and Playlists (async, shimmer-skeleton).
// Differentiated chrome — art thumbnails, count pills, the dashed drop-zone, tracked section headers — NOT a flat list.
//
// MOTION (NavigationView-grade, adapted to the bespoke shape):
//   • width   — owned by the shell-wrapper pane (WaveeShell.cs): its width (56/300) animates via SizeMode.Reflow on the
//               row's direct child, so the content column re-solves and tiles against it frame-by-frame — gap-free, no fill.
//   • sections— Expander's reveal idiom: an always-mounted clip wrapper whose height eases 0↔auto (rows below reflow).
//   • rows    — ItemReflowTransition: enter-fade on mount (skeleton→real) + slide to new Y on reorder.
//   • body    — a keyed cross-fade between the compact rail and the expanded layout (two intentionally-different trees).
//   • pill    — a single overlay accent pill that GLIDES to the selected row (NavIndicator), positioned by MEASURING
//               the row's laid-out rect (robust to collapsible sections + async playlists — no hand-computed geometry).
sealed class WaveeSidebar : Component
{
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;
    readonly Signal<bool> _pinnedOpen = new(true);
    readonly Signal<bool> _libOpen = new(true);
    readonly Signal<bool> _plOpen = new(true);

    // Mount-time node handles for the selectable rows (key → node) + the scroll-content ZStack, so the overlay pill
    // can measure the selected row's laid-out Y. Plain instance fields: this component is mounted once and persists.
    readonly Dictionary<string, NodeHandle> _rowNodes = new();
    NodeHandle _contentNode;

    // ── overlay selection-pill geometry ──────────────────────────────────────────────────────────
    internal const float PillH = 16f;        // SelectionIndicator height (WinUI 3×16)
    const float PillX = 14f;                 // left inset = ExpandedBody pad-left(8) + row pad-left(6) → over the gutter

    // The pill target, recomputed each render and read by WaveeSelPill (mirrors NavigationView.IndicatorTarget): the
    // selectable row key to measure, the visibility decision, and a layout dep-string so the pill re-measures whenever
    // a section open-state / playlist load changes the row's Y.
    internal readonly record struct PillState(bool Visible, string RowKey, string Dep);
    internal static readonly Context<PillState> Pill = new(new PillState(false, "", ""));

    // ── motion specs ─────────────────────────────────────────────────────────────────────────────
    // (The pane WIDTH animation lives on the shell-wrapper pane in WaveeShell.cs — SizeMode.Reflow on the row's direct
    //  child, so the content column tracks it. Here we only own the in-pane motions below.)
    // Rows below an opened/closed section (or newly-resolved playlists) carry from their old Y to the new Y; a row
    // appearing for the first time fades + slides in from the left. Small engine-authored stagger.
    static LayoutTransition ItemReflowTransition(int visualIndex) => new(
        TransitionChannels.Position, TransitionDynamics.Spring(0.18f, 1f), SizeMode.Reveal,
        Enter: new EnterExit(Dx: -8f, Opacity: 0f, Active: true),
        DelayMs: MathF.Min(visualIndex * 8f, 48f));
    // Body cross-fade for the compact↔expanded swap (the two bodies are intentionally different layouts).
    static readonly LayoutTransition BodyFade = new(
        TransitionChannels.Opacity, TransitionDynamics.Spring(0.20f, 1f), SizeMode.Reveal,
        Enter: new EnterExit(Opacity: 0f, Active: true));
    // Section open/close reveal: the body's layout height eases 0↔auto through real layout so rows below reflow,
    // riding the content's bottom edge (slide-out-from-under-the-header). Mirrors Expander.cs:83-87.
    static readonly LayoutTransition SectionReveal = new(
        TransitionChannels.Size, TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing);

    public WaveeSidebar(Signal<Route> route, Action<string, string?> go, Signal<bool> compact)
    {
        _route = route; _go = go; _compact = compact;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var stats = UseAsyncResource(ct => svc!.Library.GetStatsAsync(ct), default(LibraryStats)!);
        var playlists = UseAsyncResource(async ct => (await svc!.Library.GetPlaylistsAsync(ct)).ToArray(), Array.Empty<PlaylistSummary>());

        bool compact = _compact.Value;        // subscribe (width)
        string sel = _route.Value.Name;       // subscribe (selection pill)
        bool pinnedOpen = _pinnedOpen.Value;  // subscribe (so the pill dep re-measures on section toggle)
        bool libOpen = _libOpen.Value;
        bool plOpen = _plOpen.Value;

        Element body = compact ? CompactBody(sel, playlists) : ExpandedBody(stats, playlists, sel);
        // Keyed cross-fade: the swap remounts a fresh wrapper (key flips), so the incoming body fades in via BodyFade.
        BoxEl bodyWrapped = new BoxEl { Key = compact ? "rail" : "full", Animate = BodyFade, Children = [ body ] };

        Element content;
        if (compact)
        {
            // Capture the root even when the first mount is compact. The compact root is reused when it updates into
            // the expanded ZStack, and OnRealized is mount-only, so the expanded-only callback would never fire.
            content = bodyWrapped with { OnRealized = h => _contentNode = h }; // compact rail = background-fill selection, no pill
        }
        else
        {
            var pillState = ComputePillState(sel, pinnedOpen, libOpen, plOpen, playlists);
            content = ZStack(
                bodyWrapped,
                Ctx.Provide(Pill, pillState, Embed.Comp(() => new WaveeSelPill(_rowNodes, () => _contentNode)))
            ) with { OnRealized = h => _contentNode = h };
        }

        return new BoxEl
        {
            // We FILL the shell-wrapper "pane" (WaveeShell.cs), which owns the animated width (56↔300, SidebarStyles.xaml:45-47)
            // and the SizeMode.Reflow that makes the content column track it. Cross-stretch sets our width = the pane's
            // (animated) width; Grow=1f fills its height. We read `compact` only to pick the body (rail vs full).
            Grow = 1f,
            // No corners: the sidebar is flush chrome anchored to the toolbar/title chrome — NOT a detached rounded card
            // (the WaveeMusic Sidebar.BackgroundBrush = LayerOnMicaBaseAlt pane, App.xaml:38, is part of the shell frame).
            // No Fill here: the SidebarPane (WaveeShell.cs) owns the chrome fill, so the resize content-fade dims the
            // CONTENT over a solid chrome backing instead of dissolving the chrome to the transparent root.
            Direction = 1, ClipToBounds = true,
            // AutoEdgeFade: the engine's opt-in premium edge-scroller — a TRUE alpha fade that feathers only the edge(s)
            // that currently overflow (top when scrolled down, bottom when more is below), ramped with the scroll offset,
            // so rows dissolve into the sidebar chrome as they leave the viewport. (The default surface-colour EdgeCues is
            // invisible on our translucent chrome, hence the explicit alpha fade.)
            // The scrollbar uses the standard auto-hide behavior; hover/wheel now reaches this ScrollView because the
            // shell resize overlay no longer covers the sidebar hit-test branch.
            Children = [ ScrollView(content) with { Grow = 1f, AutoEdgeFade = true } ],
        };
    }

    // Decide where the overlay pill should be (and whether it shows) from data only — the actual Y is MEASURED later.
    PillState ComputePillState(string sel, bool pinnedOpen, bool libOpen, bool plOpen, Loadable<PlaylistSummary[]> playlists)
    {
        bool isLib = sel is "albums" or "artists" or "liked" or "podcasts";
        bool isPl = sel.StartsWith("pl:", StringComparison.Ordinal);

        int plCount = 0; bool plLoaded = false;
        if ((LoadState)playlists.State.Value == LoadState.Ready && playlists.Value.Value is { } arr)
        {
            plCount = arr.Length;
            if (isPl) foreach (var p in arr) if ("pl:" + p.Uri == sel) { plLoaded = true; break; }
        }

        bool sectionOpen = isLib ? libOpen : isPl ? plOpen : false;
        bool visible = sectionOpen && (isLib || plLoaded);
        // Re-measure trigger: anything that moves the selected row's Y (selection, any section open-state, playlist load).
        string dep = string.Concat(sel, "|", pinnedOpen ? "1" : "0", libOpen ? "1" : "0", plOpen ? "1" : "0", "|", plCount.ToString());
        return new PillState(visible, sel, dep);
    }

    // ── expanded (300) ──────────────────────────────────────────────────────────────────────────
    Element ExpandedBody(Loadable<LibraryStats> stats, Loadable<PlaylistSummary[]> playlists, string sel) => new BoxEl
    {
        // Grow=1f fills the pane's WIDTH: bodyWrapped is a ROW (a bare BoxEl defaults to Direction=0), so this column is
        // on its horizontal MAIN axis and would otherwise hug to the rows' natural width and left-align — leaving dead
        // space on the right once the pane is dragged wider than that natural width (same reason CompactBody sets it).
        // Filling the width lets every row cross-stretch so its Grow=1f label pushes the trailing badge to the edge.
        Grow = 1f,
        Direction = 1, Gap = WaveeSpace.S, Padding = new Edges4(8f, 8f, 8f, 12f),
        Children =
        [
            Section(Loc.Get(Strings.Sidebar.Pinned), _pinnedOpen, PinnedDropZone()),
            Section(Loc.Get(Strings.Sidebar.YourLibrary), _libOpen, new BoxEl
            {
                Direction = 1, Gap = 2f,
                Children =
                [
                    LibRow("albums",   Mdl.Album,      Loc.Get(Strings.Sidebar.Albums),     sel, 0, CountBadge(stats, s => s.Albums)),
                    LibRow("artists",  Mdl.Contact,    Loc.Get(Strings.Sidebar.Artists),    sel, 1, CountBadge(stats, s => s.Artists)),
                    LibRow("liked",    Icons.Heart,    Loc.Get(Strings.Sidebar.LikedSongs), sel, 2, CountBadge(stats, s => s.LikedSongs)),
                    LibRow("podcasts", Mdl.RadioTower, Loc.Get(Strings.Sidebar.Podcasts),   sel, 3, CountBadge(stats, s => s.Podcasts)),
                    LocalRow(),
                ],
            }),
            Section(Loc.Get(Strings.Sidebar.Playlists), _plOpen, StatefulRegion.List(
                playlists, _ => PlaylistSkeletonRow(), skeletonCount: 5,
                content: arr => Flow.For(() => arr.Length, i => PlaylistRow(arr[i], sel, i), keyOf: i => arr[i].Uri),
                empty: EmptyState.Default()),
                action: Embed.Comp(() => new SidebarCreateButton(CreatePlaylist, CreateFolder))),
        ],
    };

    // `action` (optional) is a trailing affordance (e.g. the Playlists "+" create button) placed before the chevron.
    // Click dispatch targets the nearest clickable self-or-ancestor, so a clickable action consumes its own clicks
    // without toggling the section (the header toggle only fires on the non-clickable title/spacer/chevron).
    Element Section(string title, Signal<bool> open, Element body, Element? action = null)
    {
        bool isOpen = open.Value;             // subscribe
        return new BoxEl
        {
            Direction = 1, Gap = 2f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Height = 28f, AlignItems = FlexAlign.Center, Gap = 4f,
                    Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
                    HoverFill = Tok.FillSubtleSecondary, OnClick = () => open.Value = !open.Peek(),
                    Children =
                    [
                        new TextEl(title) { Size = 11f, Weight = 600, Color = Tok.TextTertiary },
                        new BoxEl { Grow = 1f },
                        .. (action is null ? Array.Empty<Element>() : new[] { action }),
                        Icon(isOpen ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextTertiary),
                    ],
                },
                // Always-mounted reveal wrapper (Expander PartClip idiom): the body's layout height eases 0↔auto, so
                // sections below reflow smoothly. The body stays mounted while collapsed (clip height 0) — that also
                // keeps the selected-row node measurable for the pill (it just goes hidden when its section is closed).
                new BoxEl
                {
                    Direction = 1, ClipToBounds = true,
                    Height = isOpen ? float.NaN : 0f,
                    Animate = SectionReveal,
                    Children = [ body ],
                },
            ],
        };
    }

    Element LibRow(string key, string glyph, string label, string sel, int index, Element trailing)
    {
        bool selected = sel == key;
        return new BoxEl
        {
            Key = key,
            Animate = ItemReflowTransition(index),
            OnRealized = h => _rowNodes[key] = h,
            Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 12f,
            Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => _go(key, null),
            Children =
            [
                SelGutter(),     // 3px reserve; the moving accent is the overlay WaveeSelPill
                Icon(glyph, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary),
                Body(label) with { Grow = 1f, Trim = TextTrim.CharacterEllipsis },
                trailing,
            ],
        };
    }

    Element LocalRow() => new BoxEl
    {
        Key = "local",
        Animate = ItemReflowTransition(4),
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 12f,
        Padding = new Edges4(6f, 0f, 8f, 0f), Opacity = 0.5f,
        Children =
        [
            new BoxEl { Width = 3f },
            Icon(Icons.Folder, 16f, Tok.TextSecondary),
            Body(Loc.Get(Strings.Sidebar.LocalFiles)) with { Grow = 1f, Trim = TextTrim.CharacterEllipsis },
            new BoxEl
            {
                Padding = new Edges4(8f, 2f, 8f, 2f), Corners = CornerRadius4.All(WaveeRadius.Pill), Fill = Tok.FillSubtleSecondary,
                Children = [ new TextEl(Loc.Get(Strings.Sidebar.Soon)) { Size = 11f, Color = Tok.TextTertiary } ],
            },
        ],
    };

    Element PinnedDropZone() => new BoxEl
    {
        Height = 56f, Margin = new Edges4(4f, 4f, 4f, 8f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(4f),
        BorderColor = Tok.TextTertiary with { A = 0.5f }, BorderWidth = 1f, BorderDashOn = 4f, BorderDashOff = 3f,
        Children = [ new TextEl(Loc.Get(Strings.Sidebar.DropToPin)) { Size = 12f, Color = Tok.TextTertiary with { A = 0.8f } } ],
    };

    Element CountBadge(Loadable<LibraryStats> stats, Func<LibraryStats, int> pick)
    {
        var st = (LoadState)stats.State.Value;          // subscribe
        if (st == LoadState.Ready && stats.Value.Value is { } s) return InfoBadge.Count(pick(s));
        return new BoxEl { Width = 22f, Height = 16f, Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary };
    }

    Element PlaylistRow(PlaylistSummary p, string sel, int index)
    {
        string key = "pl:" + p.Uri;
        bool selected = sel == key;
        return new BoxEl
        {
            Key = key,
            Animate = ItemReflowTransition(index),
            OnRealized = h => _rowNodes[key] = h,
            Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 10f,
            Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => _go(key, p.Name),
            Children =
            [
                SelGutter(),
                Surfaces.Artwork(p.Cover, SeedFrom(p.Uri), 32f, 32f, 6f),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Gap = 1f,
                    Children =
                    [
                        Body(p.Name) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                        Caption(Strings.Sidebar.SongCount(p.TrackCount)).Secondary(),
                    ],
                },
            ],
        };
    }

    // The 3px reserve where the selection accent sits — the moving pill is the overlay WaveeSelPill (so it can glide
    // between rows). Keeping the reserve means the row content does not shift when selection moves.
    static Element SelGutter() => new BoxEl { Width = 3f };

    static Element PlaylistSkeletonRow() => new BoxEl
    {
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 10f, Padding = new Edges4(9f, 0f, 8f, 0f),
        Children =
        [
            new BoxEl { Width = 32f, Height = 32f, Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = 4f,
                Children =
                [
                    new BoxEl { Width = 140f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = 80f, Height = 10f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                ],
            },
        ],
    };

    // ── compact rail (56) — centered library icons + create + playlist art (Spotify-style) ────────
    Element CompactBody(string sel, Loadable<PlaylistSummary[]> playlists)
    {
        var st = (LoadState)playlists.State.Value;     // subscribe → the rail fills in when playlists resolve
        PlaylistSummary[] arr = st == LoadState.Ready && playlists.Value.Value is { } a ? a : Array.Empty<PlaylistSummary>();

        var kids = new List<Element>
        {
            CompactIcon("albums",   Mdl.Album,      sel),
            CompactIcon("artists",  Mdl.Contact,    sel),
            CompactIcon("liked",    Icons.Heart,    sel),
            CompactIcon("podcasts", Mdl.RadioTower, sel),
            CompactIcon("local",    Icons.Folder,   sel),
            CompactDivider(),
            Embed.Comp(() => new SidebarCreateButton(CreatePlaylist, CreateFolder, 40f, 16f)),
        };
        if (st == LoadState.Ready) foreach (var p in arr) kids.Add(CompactArt(p, sel));
        else for (int i = 0; i < 4; i++) kids.Add(CompactSkeleton());

        return new BoxEl
        {
            // Grow=1f fills the rail's WIDTH (bodyWrapped is a row, so without it this column hugs to the 40-DIP tiles and
            // left-aligns). Filling the width lets AlignItems=Center actually center the tiles in the 56-DIP rail.
            Grow = 1f,
            Direction = 1, Gap = 6f, Padding = new Edges4(0f, 8f, 0f, 12f), AlignItems = FlexAlign.Center,
            Children = [.. kids],
        };
    }

    Element CompactIcon(string key, string glyph, string sel)
    {
        bool selected = sel == key;
        return new BoxEl
        {
            Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(6f),
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => _go(key, null),
            Children = [ Icon(glyph, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary) ],
        };
    }

    // A short centered rule between the library icons and the create/playlist section.
    static Element CompactDivider() => new BoxEl
    {
        Width = 24f, Height = 1f, Margin = new Edges4(0f, 4f, 0f, 4f), Fill = Tok.TextTertiary with { A = 0.3f },
    };

    // Create-affordance handlers. The library is read-only (fake-data-first, no mutation API yet), so these are the
    // entry points the create flow will hook into — intentional no-ops for now; the button + menu are the shipped UI.
    void CreatePlaylist() { }
    void CreateFolder() { }

    // A playlist as its cover art only (the rail's compact analogue of the expanded PlaylistRow); accent ring when selected.
    Element CompactArt(PlaylistSummary p, string sel)
    {
        string key = "pl:" + p.Uri;
        bool selected = sel == key;
        return new BoxEl
        {
            Key = key,
            Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(8f),
            BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent, BorderWidth = selected ? 2f : 0f,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => _go(key, p.Name),
            Children = [ Surfaces.Artwork(p.Cover, SeedFrom(p.Uri), 36f, 36f, 6f) ],
        };
    }

    static Element CompactSkeleton() => new BoxEl
    {
        Width = 40f, Height = 40f, Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary,
    };

    static int SeedFrom(string uri)
    {
        int h = 17;
        foreach (char c in uri) h = h * 31 + c;
        return h & 0x7fffffff;
    }
}

// The single overlay selection pill (WinUI NavigationView.NavIndicator). Glides to the selected row by MEASURING the
// row's laid-out AbsoluteRect (robust to collapsible sections + async playlists). On first mount it snaps; on
// subsequent moves it plays the WinUI NavigationView stretch animation (PlayIndicatorAnimations, 600ms):
//   • ScaleY: 1 → peakScale (FluentAccelerate, 0→200ms) → 1 (FluentDecelerate, 200→600ms)
//   • TranslateY: hold at fromY while scale reaches across the gap, then ease to toY while scale contracts.
//   • TransformOriginY pins the edge nearest fromY (top when moving down, bottom when moving up), so the stretch grows
//     toward the selected row instead of scaling in place at the destination.
// peakScale = abs(to - from) / PillH + 1 — the scale that makes the pill span the full old→new distance.
sealed class WaveeSelPill : Component
{
    readonly Dictionary<string, NodeHandle> _rows;
    readonly Func<NodeHandle> _content;
    NodeHandle _self;
    bool _seeded;
    float _prevY;   // last target Y (needed as fromY for the stretch animation on the NEXT move)
    float _originY = 0.5f;

    const float StretchDuration = 600f;
    const float StretchPeak = 0.333f;   // WinUI c_frame1 break-point (200ms of 600ms)

    public WaveeSelPill(Dictionary<string, NodeHandle> rows, Func<NodeHandle> content)
    {
        _rows = rows; _content = content;
    }

    public override Element Render()
    {
        var st = UseContext(WaveeSidebar.Pill);   // re-render when selection / visibility / layout-dep changes
        bool visible = st.Visible;

        // Measure AFTER layout (UseLayoutEffect runs post-layout, so AbsoluteRect is valid this frame). Re-runs whenever
        // st.Dep changes (selection, any section toggle, playlist load) — i.e. whenever the row's Y can move.
        UseLayoutEffect(() =>
        {
            if (!visible) return;
            var anim = Context.Anim; var scene = Context.Scene;
            if (anim is null || scene is null) return;
            if (_self.IsNull || !scene.IsLive(_self)) return;
            var content = _content();
            if (!_rows.TryGetValue(st.RowKey, out var row) || row.IsNull || content.IsNull) return;
            if (!scene.IsLive(row) || !scene.IsLive(content)) return;

            RectF rr = scene.AbsoluteRect(row);
            RectF cr = scene.AbsoluteRect(content);
            float targetY = (rr.Y - cr.Y) + (rr.H - WaveeSidebar.PillH) * 0.5f;

            if (!_seeded)
            {
                // First mount: snap immediately (no stretch on initial placement).
                _seeded = true;
                _prevY = targetY;
                anim.Spring(_self, AnimChannel.TranslateY, targetY, MotionSprings.NavPill, initial: targetY);
                return;
            }

            float fromY = anim.TryGetTrackValue(_self, AnimChannel.TranslateY, out var liveY)
                ? liveY
                : scene.Paint(_self).LocalTransform.Dy;
            if (MathF.Abs(targetY - fromY) < 0.5f)
            {
                // Negligible move (layout settle, same row) — don't restart animation.
                _prevY = targetY;
                return;
            }
            _prevY = targetY;

            // WinUI-parity stretch animation. peakScale makes the pill visually span old→new row.
            float peakScale = MathF.Abs(targetY - fromY) / WaveeSidebar.PillH + 1f;
            bool movingDown = targetY > fromY;
            _originY = movingDown ? 0f : 1f;
            ref var paint = ref scene.Paint(_self);
            paint.OriginY = _originY;
            scene.Mark(_self, NodeFlags.TransformDirty | NodeFlags.PaintDirty);

            // ScaleY: same shape for both directions — 1 → peakScale (FluentAccelerate) → 1 (FluentDecelerate).
            anim.Keyframes(_self, AnimChannel.ScaleY,
            [
                new Keyframe(0f,          1f,         Easing.Linear),
                new Keyframe(StretchPeak, peakScale,  Easing.FluentAccelerate),
                new Keyframe(1f,          1f,         Easing.FluentDecelerate),
            ], StretchDuration);

            // Hold while stretching across the gap; then use the same decel phase as ScaleY so the far edge stays
            // visually pinned while the pill contracts onto the destination row.
            anim.Keyframes(_self, AnimChannel.TranslateY,
            [
                new Keyframe(0f,          fromY,   Easing.Linear),
                new Keyframe(StretchPeak, fromY,   Easing.Linear),
                new Keyframe(1f,          targetY, Easing.FluentDecelerate),
            ], StretchDuration);
        }, st.Dep, visible);

        // Opacity fade with WinUI selection-indicator timing (150ms EaseOut). Capture _seeded before the effect is
        // queued: on the very first mount with visible=false, skip the animation to avoid a startup flash at Y=0.
        bool skipOpacityInit = !_seeded && !visible;
        UseLayoutEffect(() =>
        {
            if (skipOpacityInit) return;
            var a = Context.Anim; var hn = Context.HostNode;
            if (a is null || hn.IsNull) return;
            a.Animate(hn, AnimChannel.Opacity, visible ? 0f : 1f, visible ? 1f : 0f, 150f, Easing.EaseOut);
        }, visible);

        return new BoxEl
        {
            Width = 3f, Height = WaveeSidebar.PillH,
            Margin = new Edges4(14f, 0f, 0f, 0f),     // PillX: over the row gutter
            AlignSelf = FlexAlign.Start,
            TransformOriginY = _originY,
            Corners = CornerRadius4.All(2f),
            Fill = Tok.AccentDefault,
            Opacity = visible ? 1f : 0f,   // terminal value (WriteColumns writes this when no track is active)
            HitTestVisible = false,
            OnRealized = h => _self = h,
        };
    }
}

// A "+" create affordance: a subtle icon button that opens a light-dismiss MenuFlyout offering "Playlist" / "Folder"
// (the same overlay path WinUI's DropDownButton uses — Overlay.Service + MenuFlyout). Reused by the expanded Playlists
// header (24px) and the compact rail (40px). The flyout anchors below-left and re-click / Escape / click-outside dismiss.
sealed class SidebarCreateButton : Component
{
    readonly Action _onPlaylist, _onFolder;
    readonly float _box, _glyph;

    public SidebarCreateButton(Action onPlaylist, Action onFolder, float box = 24f, float glyph = 14f)
    {
        _onPlaylist = onPlaylist; _onFolder = onFolder; _box = box; _glyph = glyph;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        var items = new MenuFlyoutItem[]
        {
            new(Loc.Get(Strings.Sidebar.CreatePlaylist), Invoke: _onPlaylist),
            new(Loc.Get(Strings.Sidebar.CreateFolder),   Invoke: _onFolder),
        };

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Width = _box, Height = _box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(4f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [ Icon(Mdl.Add, _glyph, Tok.TextSecondary) ],
        };
    }
}
