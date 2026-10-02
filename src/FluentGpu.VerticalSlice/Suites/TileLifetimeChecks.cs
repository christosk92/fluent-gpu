using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// Tile TEXTURE lifetime at idle (gpu-renderer.md §13.1g) — the "content disappears while idling" defect seen in Wavee
/// (album track list blank below its header, the lyrics rail blank, one lyric line cut along a straight line; a hover
/// brought back only the tiles it re-rastered).
/// <para>Root cause: the D3D12 surface pool trimmed a tile slot's texture once the composite had not SAMPLED it for
/// <c>IdleEvictFrames + 120</c> turns, assuming the table had evicted every such tile first. But a tile consumed only
/// through a RETAINED group surface or leaf self-blur (re-drawn from its cached result on a key hit) is valid and placed
/// every turn while nothing samples it. A playhead or lyric animation keeps the turns coming, so after ~360 turns its
/// texture was retired under it; the trim reset the slot's serial, the next turn's group / blur key missed, and the
/// re-render sampled a slot with no texture — nothing composited where the table still believed current pixels were, and
/// nothing re-rastered it until damage arrived. Now the table owns texture lifetime (<see cref="SliceTable.TrimmedSurfaces"/>
/// → <c>CompositeFrame.TrimSurfaces</c>): only a slot NO tile holds is ever trimmed.</para>
/// <para>The gate drives the real headless host through the scenario: a playhead that composites every turn, a list inside
/// an opacity group (one retained GROUP surface — the album page) and a self-scrolling viewport of self-blurred rows (leaf
/// self-blurs — the lyrics rail) that rests longer than the pre-fix trim age between follow steps. Precondition: a placed
/// tile went unsampled past the pre-fix age (the pre-fix pool would have retired its texture under it). Invariant: no
/// composite ever samples a slot without a texture, no visible tile composites nothing, no tile is stale — and the trim
/// still runs for slots the table released.</para>
/// </summary>
static class TileLifetimeChecks
{
    const float W = 400f, H = 720f;
    /// <summary>The deleted <c>SurfacePool.TileTrimTurns</c>: the pre-fix pool retired a tile texture unsampled this long.</summary>
    const int PreFixTileTrimTurns = SliceTable.IdleEvictFrames + 120;
    /// <summary>Each idle window: past the pre-fix trim age with margin for the settle turns at its start.</summary>
    const int IdleTurns = PreFixTileTrimTurns + 120;

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        IdleKeepsPlacedTextures(strings, fonts);
    }

    /// <summary>The idle-playback page: a playhead (re-coloured every turn — the turns keep coming), a transient effect slice
    /// (removed after warm-up: its slot is released, so the trim has something legitimate to do), an album-like track list
    /// inside an opacity group (its tiles are consumed only through the group's retained surface) and a lyrics-like
    /// AutoEdgeFade virtual list of self-blurred rows (each a leaf self-blur) that follow-scrolls on its own.</summary>
    sealed class IdleProbe : Component
    {
        public static readonly Signal<int> Tick = new(0);
        public static readonly Signal<bool> Toast = new(true);
        public const int Tracks = 14, LyricRows = 24;
        public const float TrackH = 44f, LyricRowH = 56f;

        public override Element Render()
        {
            var tracks = new Element[Tracks];
            for (int i = 0; i < tracks.Length; i++)
                tracks[i] = new BoxEl
                {
                    Height = TrackH, Direction = 1, Padding = new Edges4(12f, 10f, 12f, 10f),
                    Fill = i % 2 == 0 ? ColorF.FromRgba(32, 34, 42) : ColorF.FromRgba(26, 28, 34),
                    Children = [new TextEl("Track " + i) { Size = 15f, Color = ColorF.FromRgba(0xE8, 0xE8, 0xEE) }],
                };
            return new BoxEl
            {
                Width = W, Height = H, Direction = 1, Fill = ColorF.FromRgba(12, 12, 16),
                Children =
                [
                    new BoxEl { Height = 8f, Fill = Prop.Of(() => (Tick.Value & 1) == 0 ? ColorF.FromRgba(90, 200, 120) : ColorF.FromRgba(80, 190, 110)) },
                    Toast.Value
                        ? new BoxEl { Height = 40f, Fill = ColorF.FromRgba(200, 120, 40), Opacity = 0.5f, OpacityGroup = true }
                        : new BoxEl { Height = 40f },
                    new BoxEl
                    {
                        // a partial-alpha opacity group with a child slice: ONE Group item (never distributed — only a
                        // pure edge fade distributes), its tiles consumed only through its retained surface on a key hit
                        Height = 260f, Direction = 1, Opacity = 0.9f, OpacityGroup = true,
                        Children =
                        [
                            new ScrollEl
                            {
                                Height = 260f, AutoEdgeFade = true, SuppressScrollBar = true, Fill = ColorF.FromRgba(22, 24, 30),
                                Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = tracks },
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Width = W, Height = 300f,
                        Children =
                        [
                            Virtual.ListBound(LyricRows, LyricRowH, idx => new BoxEl
                            {
                                Height = LyricRowH, Direction = 1, Padding = new Edges4(16f, 8f, 16f, 8f),
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Direction = 1, Blur = 3f,
                                        Children = [new TextEl("") { Size = 18f, Text = Prop.Of(() => "line " + idx.Value), Color = ColorF.FromRgba(0xF0, 0xF0, 0xF4) }],
                                    },
                                ],
                            }) with { Width = W, Height = 300f, AutoEdgeFade = true, SuppressScrollBar = true },
                        ],
                    },
                ],
            };
        }
    }

    static void Collect(SceneStore s, NodeHandle n, List<NodeHandle> scrollers)
    {
        if (n.IsNull) return;
        if (s.HasScroll(n)) scrollers.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Collect(s, c, scrollers);
    }

    // ── gate.tiles.idle-keeps-placed-textures / gate.tiles.self-scroll-idle-keeps-textures / gate.tiles.trim-only-free-slots ──
    static void IdleKeepsPlacedTextures(StringTable strings, HeadlessFontSystem fonts)
    {
        IdleProbe.Tick.Value = 0;
        IdleProbe.Toast.Value = true;
        var app = new HeadlessPlatformApp();
        using var _app = app;
        var window = new HeadlessWindow(new WindowDesc("tile-lifetime", new Size2(W, H), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        using var host = new AppHost(app, window, device, fonts, strings, new IdleProbe());

        for (int i = 0; i < 4; i++) { IdleProbe.Tick.Value++; host.RunFrame(); }
        IdleProbe.Toast.Value = false;   // the transient effect slice goes: its slot is released — the trim's legitimate work

        var scrollers = new List<NodeHandle>();
        Collect(host.Scene, host.Scene.Root, scrollers);
        NodeHandle lyrics = NodeHandle.Null;
        foreach (var sc in scrollers) if (host.Scene.ScrollRef(sc).ItemCount > 0) { lyrics = sc; break; }
        var follow = lyrics.IsNull ? null : host.TryGetScrollHandle(lyrics);

        int frames = 0, turns = 0, lostTurns = 0, censusLost = 0, exposed = 0, stale = 0;
        string firstBad = "";
        void Step()
        {
            IdleProbe.Tick.Value++;   // the playhead: a paint change outside every group / blur, so every frame composites
            frames++;
            int before = device.CompositeFrameCount;
            host.RunFrame();
            if (device.CompositeFrameCount == before) return;
            turns++;
            TileCensus c = host.LastTileCensus;
            if (device.LastLostPlacements != 0) lostTurns++;
            censusLost += c.LostPlacements;
            exposed += c.ExposedMissing;
            stale += c.StaleTiles;
            if (firstBad.Length == 0 && (device.LastLostPlacements != 0 || c.ExposedMissing != 0 || c.StaleTiles != 0))
                firstBad = $" firstBad=(turn={turns} lost={device.LastLostPlacements} exposedMissing={c.ExposedMissing} stale={c.StaleTiles} unsampledMax={device.MaxPlacedUnsampledTurns})";
        }

        // Idle 1: the group and the blurred rows are re-drawn from their retained results every turn (nothing samples
        // their tiles) for longer than the pre-fix trim age.
        for (int i = 0; i < IdleTurns; i++) Step();
        int unsampledIdle1 = device.MaxPlacedUnsampledTurns;
        long groupHitsIdle1 = device.GroupHitsTotal, leafHitsIdle1 = device.LeafBlurHits;
        long lostIdle1 = device.LostPlacementsTotal;

        // The lyrics auto-follow: a Follow glide two rows down, run until the viewport rests. Every moved blurred row's key
        // misses — it re-renders from its tiles, which must all still hold their textures.
        long leafRendersBefore = device.LeafBlurRenders;
        double offsetBefore = lyrics.IsNull ? 0.0 : host.Scene.ScrollRef(lyrics).Offset;
        follow?.ScrollTo(offsetBefore + 2 * IdleProbe.LyricRowH, ScrollMove.Follow);
        int glideFrames = 0;
        for (; glideFrames < 600; glideFrames++)
        {
            Step();
            if (glideFrames > 2 && host.Scene.TryGetScroll(lyrics, out var st) && !st.Motion.IsMoving && !st.UserScrollActive) break;
        }
        double offsetAfter = lyrics.IsNull ? 0.0 : host.Scene.ScrollRef(lyrics).Offset;
        long leafRendersOnFollow = device.LeafBlurRenders - leafRendersBefore;
        long lostOnFollow = device.LostPlacementsTotal - lostIdle1;

        // Idle 2: rest again past the pre-fix trim age, then the evidence.
        long leafHitsBeforeIdle2 = device.LeafBlurHits;
        for (int i = 0; i < IdleTurns; i++) Step();
        long leafHitsIdle2 = device.LeafBlurHits - leafHitsBeforeIdle2;

        string detail = $"frames={frames} turns={turns} unsampledMax={device.MaxPlacedUnsampledTurns} (idle1={unsampledIdle1}, pre-fix trim age {PreFixTileTrimTurns}) "
            + $"groupHits={device.GroupHitsTotal} (idle1={groupHitsIdle1}) leafBlurHits={device.LeafBlurHits} (idle1={leafHitsIdle1}, idle2={leafHitsIdle2}) leafBlurRenders={device.LeafBlurRenders} "
            + $"lostTotal={device.LostPlacementsTotal} lostTurns={lostTurns} censusLost={censusLost} exposedMissing={exposed} stale={stale} "
            + $"follow=(found={!lyrics.IsNull} offset {offsetBefore:0.#}->{offsetAfter:0.#} glideFrames={glideFrames} leafRenders={leafRendersOnFollow} lost={lostOnFollow}) "
            + $"trimmed=(device={device.TrimmedTextures} table={host.UiSliceTable.TrimmedTotal} whilePlaced={device.TrimmedWhilePlaced}){firstBad}";

        // The run reached the defect's trigger: a PLACED tile went unsampled past the pre-fix pool's trim age (both a group
        // and a leaf self-blur consumed their tiles only from retained results that long) — the pre-fix pool would have
        // retired that texture while the table still placed it valid.
        bool trigger = unsampledIdle1 > PreFixTileTrimTurns
            && groupHitsIdle1 > PreFixTileTrimTurns && leafHitsIdle1 > PreFixTileTrimTurns;
        Check("gate.tiles.idle-keeps-placed-textures (Wavee idle blanking) a list consumed only through its retained GROUP surface and blurred rows consumed only through their retained leaf self-blurs, idle past the pre-fix trim age while a playhead composites every turn, keep every placed tile's texture: no composite samples a slot without a texture (0 lost placements, device and census), no visible tile composites nothing, no tile is stale",
            trigger && lostIdle1 == 0 && device.LostPlacementsTotal == 0 && censusLost == 0 && exposed == 0 && stale == 0,
            detail);

        // The self-scrolling viewport: after resting past the trim age, its follow step re-renders the blurred rows from
        // their tiles — every one still textured — and it then rests past the trim age again without losing any.
        bool followed = !lyrics.IsNull && offsetAfter > offsetBefore + 0.5 && leafRendersOnFollow > 0;
        Check("gate.tiles.self-scroll-idle-keeps-textures (Wavee lyrics rail blank at idle) a self-scrolling viewport of self-blurred rows that rests past the pre-fix trim age, follow-scrolls on its own (no input) and rests again re-renders its moved rows from tiles that all still hold their textures (0 lost on the follow step and after it)",
            followed && lostOnFollow == 0 && leafHitsIdle2 > PreFixTileTrimTurns && device.LostPlacementsTotal == 0,
            detail);

        // The trim still reclaims memory, but only where the table released the slot: the transient effect slice's slot was
        // trimmed, and no trim ever named a slot a placement of the same frame samples.
        Check("gate.tiles.trim-only-free-slots tile textures are still trimmed — the released slot of a removed effect slice, SurfaceTrimTurns after it was freed — and never a slot a tile holds (no trim names a slot the same frame places)",
            device.TrimmedTextures > 0 && host.UiSliceTable.TrimmedTotal > 0 && device.TrimmedWhilePlaced == 0,
            detail);
    }
}
