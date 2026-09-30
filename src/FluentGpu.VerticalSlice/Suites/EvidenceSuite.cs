using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// The evidence ledgers' gates (docs/plans/evidence-diagnostics-implementation.md §A, runs with <c>--suite tiles</c>):
/// issue #1's regression pin (<c>gate.slices.inherited-opacity-rerasters</c> — failing-first as a known-failing evidence
/// gate, promoted to a plain check with content-derived tile validity, gpu-renderer.md §13.1c), the rings inside the
/// zero-alloc bracket, a capture aligned with the turn it hit, and the composite item record equal to what the headless
/// device drew. The permanent stale-tile sweep
/// over every suite is <see cref="StaleSweep"/>.
/// </summary>
static class EvidenceSuite
{
    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        InheritedOpacityChecks(strings, fonts);
        LedgerAllocZeroChecks(strings, fonts);
        CaptureSeqAlignedChecks(strings, fonts);
        ItemRecordMatchesModelChecks(strings, fonts);
    }

    static (HeadlessPlatformApp App, HeadlessGpuDevice Device, AppHost Host) Host(string name, StringTable strings,
        HeadlessFontSystem fonts, Component root, float w = 1200f, float h = 1000f)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(w, h), 1f));
        window.Show();
        var dev = new HeadlessGpuDevice();
        return (app, dev, new AppHost(app, window, dev, fonts, strings, root));
    }

    static void Frames(AppHost host, int n) { for (int i = 0; i < n; i++) host.RunFrame(); }

    // ── gate.slices.inherited-opacity-rerasters (failing-first, issue #1) ────────────────────────────────────────

    /// <summary>The artist page's compact band, reduced (Wavee <c>Artist.Page.cs</c> <c>BandBar</c>): a page scroller whose
    /// content holds a hero, then a band that <c>.Reveal</c>s over the page offset [<see cref="RevealStart"/>,
    /// +<see cref="RevealOver"/>] — a Fade (opacity) row plus a Parallax (translation) row, so the band is cut as a
    /// TRANSLATION Effect slice and its Fade alpha is RECORDED into its bytes (<c>NodePaint.Opacity</c>, not a group,
    /// not a composite parameter) — and inside the band a HORIZONTAL tab scroller, whose content is its own Scroll slice
    /// walked under the band's alpha. A page scroll through the ramp changes the tab slice's bytes (SigMiss: no tab node
    /// is dirty) while no damage reaches the tab slice's own slot.</summary>
    sealed class BandRevealProbe : Component
    {
        public const float HeroH = 600f, RevealStart = 300f, RevealOver = 44f, BandH = 56f;

        public override Element Render()
        {
            // The tabs FIT their lane (like the artist pivot at a normal width): an overflowing lane would edge-fade, and an
            // edge-fade group composites the band's alpha instead of baking it into the tab bytes.
            var tabs = new Element[5];
            for (int i = 0; i < tabs.Length; i++)
                tabs[i] = new BoxEl
                {
                    Width = 120f, Height = 40f, Fill = ColorF.FromRgba(60, 60, (byte)(90 + i * 12)),
                    Children = [new TextEl("tab " + i) { Size = 14f, Color = ColorF.FromRgba(0xE0, 0xE0, 0xE8) }],
                };
            var body = new Element[30];
            for (int i = 0; i < body.Length; i++)
                body[i] = new BoxEl { Height = 80f, Fill = ColorF.FromRgba(30, 30, (byte)(40 + i * 4)) };
            Element band = new BoxEl
            {
                Width = 1000f, Height = BandH, Direction = 0, AlignItems = FlexAlign.Center,
                Children = [new ScrollEl { Horizontal = true, Width = 700f, Height = 40f, Content = new BoxEl { Direction = 0, Children = tabs } }],
            }.Reveal(RevealStart, RevealOver, 4f);
            return new ScrollEl
            {
                Width = 1000f, Height = 800f,
                Content = new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [new BoxEl { Height = HeroH, Fill = ColorF.FromRgba(80, 40, 40) }, band, new BoxEl { Direction = 1, Children = body }],
                },
            };
        }
    }

    /// <summary>A plain parent over a scroller (the capture gate's scene).</summary>
    sealed class InheritedOpacityProbe : Component
    {
        public readonly Signal<float> Alpha = new(1f);

        public override Element Render()
        {
            var rows = new Element[12];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl
                {
                    Height = 60f, Fill = ColorF.FromRgba(40, 40, (byte)(60 + i * 10)),
                    Children = [new TextEl("tab " + i) { Size = 14f, Color = ColorF.FromRgba(0xE0, 0xE0, 0xE8) }],
                };
            return new BoxEl
            {
                Width = 800f, Height = 600f, Direction = 1,
                Opacity = Prop.Of(() => Alpha.Value),
                Children = [new ScrollEl { Width = 800f, Height = 400f, Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = rows } }],
            };
        }
    }

    static void InheritedOpacityChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, dev, host) = Host("evidence-band-reveal", strings, fonts, new BandRevealProbe());
        using var _a = app; using var _h = host;
        Frames(host, 30);

        // The page viewport (vertical) and the tab scroller's CONTENT node (its slice is the one that must re-raster).
        var scrollers = new List<NodeHandle>();
        CollectScrollers(host.Scene, host.Scene.Root, scrollers);
        NodeHandle page = NodeHandle.Null, tabContent = NodeHandle.Null;
        foreach (var n in scrollers)
        {
            ref readonly ScrollState sc = ref host.Scene.ScrollRef(n);
            if (sc.Orientation == 1) tabContent = sc.ContentNode; else if (page.IsNull) page = n;
        }
        var handle = page.IsNull ? null : host.TryGetScrollHandle(page);
        long staleTurns0 = TileInvariants.StaleTurns;
        long walks0 = host.WalkLedger?.Count ?? 0;

        // Walk the page through the band's reveal ramp, past it (pinned-equivalent: alpha 1) and back to rest: every
        // step changes the alpha baked into the tab slice's bytes. Each composited turn must re-raster the tab tiles whose
        // content changed — never keep old-alpha pixels valid.
        double s0 = BandRevealProbe.RevealStart, over = BandRevealProbe.RevealOver;
        double[] steps = [s0 + 4, s0 + 12, s0 + 22, s0 + 33, s0 + over, s0 + over + 200, s0 + 18, s0 + 6];
        int tabRasters = 0, stepsRastered = 0, stepsChanged = 0, idleStepRasters = 0, staleMax = 0, tabItems = 0, tabItemAlphaNot1 = 0;
        float bandAlphaMin = 2f, bandAlphaMax = -1f, prevAlpha = BandAlpha(host);
        foreach (double o in steps)
        {
            handle?.ScrollTo(o, ScrollMove.Immediate);
            bool rastered = false;
            for (int i = 0; i < 3; i++)
            {
                int seen = dev.CompositeFrameCount;
                host.RunFrame();
                staleMax = Math.Max(staleMax, host.LastTileCensus.StaleTiles);
                if (dev.CompositeFrameCount == seen) continue;
                var tabIds = new HashSet<int>();
                foreach (var row in dev.LastCompositeSlices)
                    if (!tabContent.IsNull && row.NodeIndex == (int)tabContent.Raw.Index) tabIds.Add(row.Id);
                foreach (var op in dev.LastCompositeRecords)
                {
                    if (op.Kind == CompositeRecordKind.RasterTile && tabIds.Contains(op.Tile.SliceId)) { tabRasters++; rastered = true; }
                    if (op.Kind == CompositeRecordKind.DrawItem && tabIds.Contains(op.Item.SliceId))
                    {
                        tabItems++;
                        if (Math.Abs(op.Item.Alpha - 1f) > 1e-4f) tabItemAlphaNot1++;   // composited, not baked: not this issue's route
                    }
                }
            }
            float a = BandAlpha(host);
            // A step that moved the band's alpha changed the tab bytes: its tab tiles must re-raster. (One that did not —
            // past the ramp, alpha 1 → 1 — changes nothing and must raster nothing.)
            if (a != prevAlpha) { stepsChanged++; if (rastered) stepsRastered++; }
            else if (rastered) idleStepRasters++;
            prevAlpha = a;
            bandAlphaMin = Math.Min(bandAlphaMin, a); bandAlphaMax = Math.Max(bandAlphaMax, a);
        }
        long staleTurns = TileInvariants.StaleTurns - staleTurns0;

        // What the walk ledger says about the tab slice's re-records: SigMiss (clean nodes, a changed inherited input).
        int sigMiss = 0, otherWalks = 0;
        if (host.WalkLedger is { } walks)
        {
            var buf = new WalkEntry[WalkLedger.WalkLedgerCapacity];
            int n = walks.Read(walks0, buf, out _);
            for (int i = 0; i < n; i++)
            {
                if (tabContent.IsNull || buf[i].NodeIndex != (int)tabContent.Raw.Index) continue;
                if (buf[i].Why == (byte)WalkWhy.SigMiss) sigMiss++; else otherWalks++;
            }
        }
        var s = TileInvariants.LastStale;
        // The scene reproduces the app's route iff: the alpha is BAKED (the tab item composites at 1, the band's Fade
        // varied across the steps) and the tab slice re-recorded on a signature miss.
        bool reproduces = !tabContent.IsNull && tabItems > 0 && tabItemAlphaNot1 == 0 && sigMiss > 0 && bandAlphaMax - bandAlphaMin > 0.5f;
        Check("gate.slices.inherited-opacity-rerasters a translation-cut band whose .Reveal alpha is RECORDED into its bytes re-records its nested tab scroller's slice with the new alpha (SigMiss) — that slice's visible tiles must re-raster on every step that moved the alpha (and on no other), 0 stale turns — never keep old-alpha pixels valid",
            reproduces && staleTurns == 0 && staleMax == 0 && stepsChanged > 0 && stepsRastered == stepsChanged && idleStepRasters == 0,
            $"reproduces={reproduces} tabRasters={tabRasters} stepsRastered={stepsRastered}/{stepsChanged} (alpha-changing steps of {steps.Length}) idleStepRasters={idleStepRasters} staleTurns={staleTurns} staleMax={staleMax} "
            + $"sigMissWalks={sigMiss} otherWalks={otherWalks} tabItems={tabItems} tabItemAlphaNot1={tabItemAlphaNot1} bandAlpha={bandAlphaMin:0.###}..{bandAlphaMax:0.###} "
            + $"lastStale=(slice {s.SliceId} node {s.NodeIndex}:{s.Gen} tile {s.Tx},{s.Ty} want {s.Want:x16} have {s.Have:x16} rasterFrame {s.RasterFrame})");
    }

    /// <summary>The band's posed Fade alpha: the one node carrying a Reveal row (NodePaint.Opacity written by the sink).</summary>
    static float BandAlpha(AppHost host)
    {
        float a = -1f;
        Walk(host.Scene, host.Scene.Root);
        return a;

        void Walk(SceneStore s, NodeHandle n)
        {
            if (n.IsNull || a >= 0f) return;
            ref readonly var b = ref s.Bounds(n);
            if (Math.Abs(b.H - BandRevealProbe.BandH) < 0.01f && Math.Abs(b.W - 1000f) < 0.01f) { a = s.Paint(n).Opacity; return; }
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Walk(s, c);
        }
    }

    static void CollectScrollers(SceneStore s, NodeHandle n, List<NodeHandle> scrollers)
    {
        if (n.IsNull) return;
        if (s.HasScroll(n)) scrollers.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) CollectScrollers(s, c, scrollers);
    }

    // ── gate.tiles.ledger-alloc-zero ──────────────────────────────────────────────────────────────────────────────

    sealed class ListProbe : Component
    {
        public override Element Render()
            => Virtual.ListBound(100_000, 36f, idx => new BoxEl
               {
                   Height = 36f,
                   Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
               })
               with { Width = 1100, Height = 900 };
    }

    static void LedgerAllocZeroChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, dev, host) = Host("evidence-ledger-alloc", strings, fonts, new ListProbe());
        using var _a = app; using var _h = host;
        Frames(host, 30);
        host.TryGetScrollHandle(host.Scene.Root)!.AutoScroll(3000.0);
        for (int f = 0; f < 200; f++) host.RunFrame();   // warm
        long rasters0 = host.RasterLedger?.Count ?? 0;
        int frame0 = host.CompositeLedger?.LatestFrame ?? 0;
        long worst = 0;
        for (int f = 0; f < 300; f++) worst = Math.Max(worst, host.RunFrame().HotPhaseAllocBytes);
        long logged = (host.RasterLedger?.Count ?? 0) - rasters0;
        int frames = (host.CompositeLedger?.LatestFrame ?? 0) - frame0;
        Check("gate.tiles.ledger-alloc-zero 300 warm frames of a 3000 DIP/s scroll with every evidence ring live (raster ledger, composite item record, walk ledger, the per-tile content wants and the stale sweep) allocate 0 managed bytes per frame",
            worst == 0 && logged > 0 && frames > 0, $"worstFrameBytes={worst} rastersLogged={logged} ledgerFrames={frames}");
    }

    // ── gate.tiles.capture-seq-aligned ────────────────────────────────────────────────────────────────────────────

    static void CaptureSeqAlignedChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new InheritedOpacityProbe();
        var (app, dev, host) = Host("evidence-capture", strings, fonts, probe);
        using var _a = app; using var _h = host;
        Frames(host, 20);
        int requests0 = dev.CaptureRequests;
        host.RequestFrameCapture();
        bool pendingBefore = host.FrameCapturePending;
        FrameStats stats = default;
        FrameCaptureResult? r = null;
        for (int i = 0; i < 4 && r is null; i++)
        {
            stats = host.RunFrame();
            host.TryTakeFrameCapture(out r);
        }
        bool ok = r is not null && pendingBefore && !host.FrameCapturePending
            && r.PublishSeq == stats.PublishSeq && r.PublishSeq != 0
            && r.Ledger.Header.PublishSeq == r.PublishSeq && r.TableFrame == r.Ledger.Header.Frame
            && r.TableFrame == host.CompositeLedger!.LatestFrame && r.Ledger.Header.Items > 0
            && r.Bgra is null && dev.CaptureRequests == requests0 + 1;
        Check("gate.tiles.capture-seq-aligned an armed capture completes on the next composited present with the publication, the tile-table frame and the composite record of THAT turn (the headless model counts the readback it cannot perform)",
            ok, r is null ? "no capture landed" :
                $"seq={r.PublishSeq} statsSeq={stats.PublishSeq} ledgerSeq={r.Ledger.Header.PublishSeq} tableFrame={r.TableFrame} ledgerFrame={r.Ledger.Header.Frame} "
                + $"latest={host.CompositeLedger!.LatestFrame} items={r.Ledger.Header.Items} bgra={(r.Bgra is null ? "null" : r.Bgra.Length.ToString())} requests={dev.CaptureRequests - requests0}");
    }

    // ── gate.tiles.item-record-matches-model ───────────────────────────────────────────────────────────────────────

    /// <summary>An opacity group over a scroller (a GROUP item enclosing the scroll slice's items), an edge-faded scroller
    /// (distributed feathers) and a plain header — the item kinds the ledger records.</summary>
    sealed class MixedProbe : Component
    {
        public override Element Render()
        {
            Element Rows(int n)
            {
                var rows = new Element[n];
                for (int i = 0; i < n; i++)
                    rows[i] = new BoxEl { Height = 48f, Fill = ColorF.FromRgba(50, 50, (byte)(70 + i * 5)),
                        Children = [new TextEl("row " + i) { Size = 13f, Color = ColorF.FromRgba(0xE0, 0xE0, 0xE8) }] };
                return new BoxEl { Direction = 1, MinWidth = 0f, Children = rows };
            }
            return new BoxEl
            {
                Direction = 1, Width = 1100f, Height = 900f, Fill = ColorF.FromRgba(18, 18, 22),
                Children =
                [
                    new BoxEl { Height = 60f, Children = [new TextEl("header") { Size = 16f, Color = ColorF.FromRgba(0xF0, 0xF0, 0xF0) }] },
                    new BoxEl { Height = 300f, Opacity = 0.7f, OpacityGroup = true,
                        Children = [new ScrollEl { Width = 1100f, Height = 300f, Content = Rows(20) }] },
                    new ScrollEl { Width = 1100f, Height = 400f, AutoEdgeFade = true, Content = Rows(30) },
                ],
            };
        }
    }

    static void ItemRecordMatchesModelChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, dev, host) = Host("evidence-item-record", strings, fonts, new MixedProbe());
        using var _a = app; using var _h = host;
        Frames(host, 20);
        host.RequestFrameCapture();   // force a composited turn (nothing else changes)
        FrameCaptureResult? r = null;
        for (int i = 0; i < 4 && r is null; i++) { host.RunFrame(); host.TryTakeFrameCapture(out r); }

        var drawn = new List<CompositeItem>();
        var groupHit = new Dictionary<int, bool>();
        foreach (var op in dev.LastCompositeRecords)
        {
            if (op.Kind == CompositeRecordKind.DrawItem) drawn.Add(op.Item);
            else if (op.Kind == CompositeRecordKind.PrepareGroup) groupHit[op.ItemIndex] = op.Hit;
        }
        int mismatches = 0, groups = 0, feathers = 0;
        string first = "";
        var items = r is null ? default : r.Ledger.View.Items;
        bool sameCount = r is not null && items.Length == drawn.Count;
        for (int i = 0; sameCount && i < items.Length; i++)
        {
            ref readonly ItemRecord rec = ref items[i];
            CompositeItem it = drawn[i];
            bool clipBounded = !(it.Clip.W <= 0f && it.Clip.H <= 0f && it.Clip.X == 0f && it.Clip.Y == 0f);
            bool same = rec.Kind == (byte)it.Kind && rec.SliceId == it.SliceId && rec.AlphaQ8 == CompositeLedger.Q8(it.Alpha)
                && rec.TransDx == (int)MathF.Round(it.Transform.Dx) && rec.TransDy == (int)MathF.Round(it.Transform.Dy)
                && rec.Inherited == it.Inherited && rec.Feather1 == it.Feather && rec.Feather2 == it.Feather2
                && ((rec.Flags & ItemRecordFlags.ClipBounded) != 0) == clipBounded
                && (!clipBounded || (rec.ClipX == CompositeLedger.S(it.Clip.X) && rec.ClipY == CompositeLedger.S(it.Clip.Y)
                                     && rec.ClipW == CompositeLedger.S(it.Clip.W) && rec.ClipH == CompositeLedger.S(it.Clip.H)))
                && rec.NodeIndex > 0;
            if (it.Kind == CompositeKind.Group)
            {
                groups++;
                bool hit = groupHit.TryGetValue(i, out bool h) && h;
                same &= ((rec.Flags & ItemRecordFlags.GroupHit) != 0) == hit
                        && ((rec.Flags & ItemRecordFlags.GroupRendered) != 0) == (groupHit.ContainsKey(i) && !hit);
            }
            if (!it.Feather.IsNone) feathers++;
            if (!same) { mismatches++; if (first.Length == 0) first = $"item {i} kind={it.Kind} slice={it.SliceId} rec(kind={rec.Kind} slice={rec.SliceId} a={rec.AlphaQ8} flags={rec.Flags} node={rec.NodeIndex})"; }
        }
        Check("gate.tiles.item-record-matches-model the composite item record of a turn is the headless device's own DrawItem sequence item for item — kind, slice, alpha, placement, clip, both feathers, the distributed fades, the group-cache outcome — and names a live node for every item",
            r is not null && sameCount && mismatches == 0 && groups > 0 && feathers > 0,
            r is null ? "no capture landed" : $"recorded={items.Length} drawn={drawn.Count} groups={groups} feathered={feathers} mismatches={mismatches} {first}");
    }
}
