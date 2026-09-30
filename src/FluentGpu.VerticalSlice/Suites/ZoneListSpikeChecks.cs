using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

// ── Wave-0 spikes for Wavee's Home remediation (WaveeMusic docs/plans/wavee/home-redesign-remediation.md §2 E17/E18,
// §3.1, §3.10, §6 "Wave 0"). Four engine facts the app architecture is decided on, each pinned as a headless gate:
//
//   gate.scroll-effects.sticky-in-realized-row      a `.Sticky(44, scope: rowKey)` chapter header inside a REALIZED
//                                                   ItemsView.Create row (a recyclable row, not the persistent prefix)
//                                                   pins at the line while its row is in view and releases at the row's
//                                                   end — for a BoxEl header, for a ComponentEl header (the effect on the
//                                                   Embed.Comp itself — E14) and for the effect on the component's
//                                                   RENDERED ROOT with the row named as its scope.
//   gate.shelf.controller.rebind-restores-page      a PagedShelf bound to a caller-owned ShelfController, paged to 2,
//                                                   recycled out of the ItemsView window and back (a real remount),
//                                                   returns to page 2 (E18).
//   gate.keepalive.same-key-view                    Flow.KeepAlive re-runs its `view` on a SAME-KEY token change, so a
//                                                   retained page receives re-pushed props (Embed.Comp(props, factory))
//                                                   from the new token — the Shell's PageBody shape (§3.10).
//   gate.items.nested-shelf-realize-alloc           realizing a row that contains a PagedShelf allocates only on the
//                                                   reconcile edge; steady frames stay at 0 hot-phase bytes.
//
// A failing spike is a valid result: the gate keeps the full contract and is reported through EvidenceGate.KnownFailing
// (an [OPEN] line, listed in the run summary) so the fix is Wave-1 engine work and the gate flips to a plain Check the
// moment it lands — never an app-side workaround.
static class ZoneListSpikeChecks
{
    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        StickyInRealizedRowChecks(strings, fonts);
        ShelfRebindRestoresPageChecks(strings, fonts);
        KeepAliveSameKeyViewChecks(strings, fonts);
        NestedShelfRealizeAllocChecks(strings, fonts);
    }

    // ══ 1. E17 — a sticky chapter header inside a realized ItemsView row ══════════════════════════════════════════

    enum HeaderShape
    {
        /// <summary>The header is a raw BoxEl carrying <c>.Sticky(Inset, scope: rowKey)</c>.</summary>
        Box,
        /// <summary>The header is an <c>Embed.Comp</c>; the sticky is on the ComponentEl ITSELF (E14's dropped case).</summary>
        ComponentEl,
        /// <summary>The header is an <c>Embed.Comp</c>; the sticky is on the component's RENDERED ROOT, naming the row
        /// as its scope (so the anchor-mirroring "limit == 0" trap of e11virt.comp-pin does not apply).</summary>
        ComponentRoot,
    }

    /// <summary>A 30-row zone list (each row = 40-DIP chapter header + 260-DIP body, the row named as the header's
    /// sticky scope) in a 640×480 viewport; row 3 (content top 900) carries the distinctive header fill the checks
    /// track through the composited rects.</summary>
    sealed class ZoneListProbe : Component
    {
        public const int N = 30, Target = 3;
        public const float RowH = 300f, HeaderH = 40f, Inset = 44f, ViewW = 640f, ViewH = 480f;
        public static readonly ColorF TargetHeader = ColorF.FromRgba(220, 60, 60);
        public static readonly ColorF OtherHeader = ColorF.FromRgba(60, 60, 220);
        public static readonly ColorF Body = ColorF.FromRgba(30, 30, 30);

        readonly HeaderShape _shape;
        readonly RepeatLayout _layout = RepeatLayout.Stack(RowH);
        public ZoneListProbe(HeaderShape shape) => _shape = shape;

        sealed class HeaderComp : Component
        {
            readonly ColorF _fill;
            readonly string? _scope;
            readonly bool _stickyOnRoot;
            public HeaderComp(ColorF fill, string? scope, bool stickyOnRoot) { _fill = fill; _scope = scope; _stickyOnRoot = stickyOnRoot; }
            public override Element Render()
            {
                var root = new BoxEl { Height = HeaderH, Fill = _fill };
                return _stickyOnRoot ? root.Sticky(Inset, scope: _scope) : root;
            }
        }

        Element Row(int i)
        {
            string key = "zone:" + i;
            var fill = i == Target ? TargetHeader : OtherHeader;
            Element header = _shape switch
            {
                HeaderShape.Box => new BoxEl { Height = HeaderH, Fill = fill }.Sticky(Inset, scope: key),
                HeaderShape.ComponentEl => Embed.Comp(() => new HeaderComp(fill, null, false)).Sticky(Inset, scope: key),
                _ => Embed.Comp(() => new HeaderComp(fill, key, true)),
            };
            // Grow/Shrink/MinWidth: the ItemContainer's content lane is a ROW-direction single-child wrapper, which
            // shrink-wraps its child on the main axis (fluentgpu skill rule 11) — a zone row must claim the lane's width.
            return new BoxEl
            {
                Key = key, ScrollScope = key, Direction = 1, Height = RowH, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children = [header, new BoxEl { Height = RowH - HeaderH, Fill = Body }],
            };
        }

        public override Element Render() => new BoxEl
        {
            Width = ViewW, Height = ViewH,
            Children = [ItemsView.Create(N, Row, _layout, new ListOptions { Grow = 1f, SelectionMode = ItemsSelectionMode.None })],
        };
    }

    /// <summary>The composited (world-space) top of the first rect painted with <paramref name="fill"/>, NaN when none.</summary>
    static float WorldTopOf(HeadlessGpuDevice dev, ColorF fill)
    {
        for (int i = 0; i < dev.LastRects.Count; i++)
        {
            var r = dev.LastRects[i];
            if (!ColorClose(r.Fill, fill, 0.006f)) continue;
            return r.Transform.TransformBounds(r.Rect).Y;
        }
        return float.NaN;
    }

    readonly record struct StickySample(float WorldY, float SceneY, float Dy, bool Pinned, bool Realized, bool Prefix, int First, int Last,
                                        bool Baked, float AbsY, int RectCount);

    static string Rect(in RectF r) => $"({r.X:0.#},{r.Y:0.#} {r.W:0.#}x{r.H:0.#})";

    static int RectsWith(HeadlessGpuDevice dev, ColorF fill)
    {
        int n = 0;
        for (int i = 0; i < dev.LastRects.Count; i++) if (ColorClose(dev.LastRects[i].Fill, fill, 0.006f)) n++;
        return n;
    }

    static void StickyInRealizedRowChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const float RowTop = ZoneListProbe.RowH * ZoneListProbe.Target;          // 900
        const float Limit = ZoneListProbe.RowH - ZoneListProbe.HeaderH;          // 260: the scope clamp (row end − header)

        (bool ok, string detail) Arm(HeaderShape shape)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky-in-row-" + shape, new Size2(ZoneListProbe.ViewW, ZoneListProbe.ViewH), 1f));
            window.Show();
            var dev = new HeadlessGpuDevice();
            using var host = new AppHost(app, window, dev, fonts, strings, new ZoneListProbe(shape));
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            var vp = FindScrollNode(scene, scene.Root);
            var handle = host.TryGetScrollHandle(vp)!;

            StickySample At(double offset)
            {
                handle.ScrollTo(offset, ScrollMove.Immediate);
                for (int i = 0; i < 4; i++) host.RunFrame();
                scene.TryGetScroll(vp, out var sc);
                var content = sc.ContentNode;
                var header = FindFillNode(scene, scene.Root, ZoneListProbe.TargetHeader);
                bool realized = !header.IsNull && sc.FirstRealized <= ZoneListProbe.Target && ZoneListProbe.Target < sc.LastRealized;
                // The node that carries the effect: the fill node itself (Box / ComponentRoot) or its component anchor (ComponentEl).
                var effectNode = header.IsNull ? header : shape == HeaderShape.ComponentEl ? scene.Parent(header) : header;
                bool pinned = !effectNode.IsNull && (scene.Flags(effectNode) & NodeFlags.StickyPinned) != 0;
                float dy = effectNode.IsNull ? float.NaN : scene.Paint(effectNode).LocalTransform.Dy;
                // Scene-side screen Y: content pose + every ancestor's laid-out Y up to the content + the effect's own translation.
                float sceneY = float.NaN;
                if (!header.IsNull)
                {
                    float acc = 0f;
                    for (var n = header; !n.IsNull && n != content; n = scene.Parent(n)) acc += scene.Bounds(n).Y;
                    sceneY = scene.Paint(content).LocalTransform.Dy + acc + (float.IsNaN(dy) ? 0f : dy);
                }
                bool baked = !effectNode.IsNull && scene.TryGetScrollEffects((int)effectNode.Raw.Index, out _);
                float absY = header.IsNull ? float.NaN : scene.AbsoluteRect(header).Y;
                return new StickySample(WorldTopOf(dev, ZoneListProbe.TargetHeader), sceneY, dy, pinned, realized,
                    sc.PersistentPrefixCount > 0, sc.FirstRealized, sc.LastRealized, baked, absY, RectsWith(dev, ZoneListProbe.TargetHeader));
            }

            var before = At(RowTop - 100.0);              // row top on screen at 100: released (shift < 0 → 0)
            // Paint diagnostics at the released offset: the header node's flags/bounds, its anchor's, and every composited
            // rect whose world top lies in the header's band — names WHY a header is not in the composited stream.
            string paintDbg;
            {
                var header = FindFillNode(scene, scene.Root, ZoneListProbe.TargetHeader);
                var sb = new System.Text.StringBuilder();
                if (!header.IsNull)
                {
                    var anchor = scene.Parent(header);
                    var row = scene.Parent(anchor);
                    scene.TryGetScroll(vp, out var scd);
                    sb.Append($"header(flags={scene.Flags(header)} b={Rect(scene.Bounds(header))} op={scene.Paint(header).Opacity:0.##}) ");
                    sb.Append($"parent(flags={scene.Flags(anchor)} b={Rect(scene.Bounds(anchor))} kids={DirectChildCount(scene, anchor)}) ");
                    sb.Append($"row(b={Rect(scene.Bounds(row))} abs={Rect(scene.AbsoluteRect(row))}) vp(b={Rect(scene.Bounds(vp))} realized=[{scd.FirstRealized},{scd.LastRealized}) contentKids={DirectChildCount(scene, scd.ContentNode)})");
                }
                else sb.Append("header=null");
                sb.Append(" band-rects=");
                for (int i = 0; i < dev.LastRects.Count; i++)
                {
                    var r = dev.LastRects[i];
                    var w = r.Transform.TransformBounds(r.Rect);
                    if (w.Y >= 80f && w.Y <= 160f) sb.Append($"[{w.Y:0}:{w.H:0} {r.Fill.R:0.00},{r.Fill.G:0.00},{r.Fill.B:0.00}]");
                }
                sb.Append($" total={dev.LastRects.Count}");
                paintDbg = sb.ToString();
            }
            var pinnedA = At(RowTop + 100.0);             // shift 144 ≤ 260: held at the 44 line
            var pinnedB = At(RowTop + 200.0);             // shift 244 ≤ 260: still held
            var released = At(RowTop + Limit - 10.0);     // shift 294 > 260: clamped — the header rides the row's end (screen 10)
            var back = At(RowTop + 100.0);                // scrolling back re-pins
            var steady = host.RunFrame();                 // a steady pinned frame allocates nothing in the hot phases

            bool realizedRow = before.Realized && pinnedA.Realized && released.Realized && !pinnedA.Prefix;
            bool releasedBefore = Near(before.WorldY, 100f, 0.01f) && !before.Pinned;
            bool pins = Near(pinnedA.WorldY, ZoneListProbe.Inset, 0.01f) && pinnedA.Pinned && Near(pinnedA.Dy, 144f, 0.01f)
                        && Near(pinnedB.WorldY, ZoneListProbe.Inset, 0.01f) && pinnedB.Pinned;
            bool releasesAtEnd = Near(released.WorldY, 10f, 0.01f) && Near(released.Dy, Limit, 0.01f);
            bool rePins = Near(back.WorldY, ZoneListProbe.Inset, 0.01f) && back.Pinned;
            bool sceneAgrees = Near(pinnedA.SceneY, pinnedA.WorldY, 0.01f) && Near(released.SceneY, released.WorldY, 0.01f);
            bool ok = realizedRow && releasedBefore && pins && releasesAtEnd && rePins && sceneAgrees && steady.HotPhaseAllocBytes == 0;
            string detail =
                $"{shape}: realized={realizedRow} window=[{pinnedA.First},{pinnedA.Last}) prefix={pinnedA.Prefix} effectBaked={pinnedA.Baked} " +
                $"@800 absY={before.AbsY:0.##} rects={before.RectCount} " +
                $"@800 y={before.WorldY:0.##}(exp 100,pinned={before.Pinned}) @1000 y={pinnedA.WorldY:0.##}(exp 44) dy={pinnedA.Dy:0.##}(exp 144) pinned={pinnedA.Pinned} " +
                $"@1100 y={pinnedB.WorldY:0.##}(exp 44) @1150 y={released.WorldY:0.##}(exp 10) dy={released.Dy:0.##}(exp 260) " +
                $"back@1000 y={back.WorldY:0.##} sceneY/worldY agree={sceneAgrees} steadyBytes={steady.HotPhaseAllocBytes}" +
                (ok ? "" : " | paint@800: " + paintDbg);
            return (ok, detail);
        }

        var box = Arm(HeaderShape.Box);
        Check("gate.scroll-effects.sticky-in-realized-row a `.Sticky(44, scope: rowKey)` BoxEl header inside a REALIZED ItemsView.Create row (not the persistent prefix) pins at the 44 line while its row is in view (composited rect + scene pose agree), releases at the row's end, re-pins on the way back, and a steady pinned frame allocates nothing",
            box.ok, box.detail);

        var comp = Arm(HeaderShape.ComponentEl);
        // E14 landed (home-redesign-remediation.md §2): WriteAnchorColumns now runs BakeScrollEffects for the
        // ComponentEl anchor itself from both MountComponent and the Update reuse branch, so this is a plain Check.
        Check("gate.scroll-effects.sticky-in-realized-row.componentel the SAME contract with the header as an Embed.Comp and the `.Sticky` on the ComponentEl itself",
            comp.ok, comp.detail);

        var root = Arm(HeaderShape.ComponentRoot);
        Check("gate.scroll-effects.sticky-in-realized-row.component-root the SAME contract with the header as an Embed.Comp whose RENDERED ROOT carries `.Sticky(44, scope: rowKey)` (the row named as the scope, so the anchor-mirroring clamp of e11virt.comp-pin does not bite)",
            root.ok, root.detail);
    }

    // ══ 2. E18 — a ShelfController-bound PagedShelf recycled out of the window and back returns to its page ═══════

    sealed record ZoneItem(int Id, string Title);

    enum RowKind { Plain, Component, NestedList, ShelfNoController, Shelf }

    /// <summary>Ten 200-DIP rows in a 640×480 viewport, each an autonomous <c>ShelfRow</c> component hosting a
    /// 320-wide PagedShelf (100-DIP fixed cards, 10 gap ⇒ 3 columns/page, 12 items ⇒ 4 pages, page stride 330) bound to
    /// a row-owned ShelfController held OUTSIDE the row (the plan's §3.1 shape: the controller outlives the row's
    /// realize/unrealize).</summary>
    sealed class ShelfZoneList : Component
    {
        public const int Rows = 10, ItemCount = 12;
        public const float RowH = 200f, ViewW = 640f, ViewH = 480f, ShelfW = 320f, CardW = 100f, Gap = 10f;
        public readonly ShelfController[] Controllers = new ShelfController[Rows];
        public readonly int[] Constructions = new int[Rows];
        public readonly IReadOnlyList<ZoneItem> Items;
        readonly RepeatLayout _layout = RepeatLayout.Stack(RowH);
        readonly RowKind _kind;

        /// <param name="kind">The row shape — the four arms bisect a nested shelf's scrolling cost: plain boxes (the
        /// list's own baseline), a component row with plain boxes, a component row hosting a plain horizontal
        /// ItemsView (a nested scroller without the shelf's logic), and the real PagedShelf row.</param>
        public ShelfZoneList(RowKind kind = RowKind.Shelf)
        {
            _kind = kind;
            for (int i = 0; i < Rows; i++) Controllers[i] = new ShelfController();
            var items = new ZoneItem[ItemCount];
            for (int i = 0; i < ItemCount; i++) items[i] = new ZoneItem(i, "card-" + i);
            Items = items;
        }

        sealed class ShelfRow : Component
        {
            readonly ShelfZoneList _o;
            readonly int _i;
            readonly bool _withController;
            public ShelfRow(ShelfZoneList o, int i, bool withController = true) { _o = o; _i = i; _withController = withController; o.Constructions[i]++; }
            public override Element Render() => new BoxEl
            {
                Direction = 1, Width = ShelfW,
                Children =
                [
                    new BoxEl { Height = 24f, Children = [Ui.Text("zone " + _i)] },
                    PagedShelf.Create(_o.Items,
                        (item, _, width) => new BoxEl { Width = width, Height = 44f, Children = [Ui.Text(item.Title)] },
                        cardHeight: static _ => 44f,
                        pager: ShelfPager.None,
                        minCardW: CardW, maxCardW: CardW, fixedCardW: CardW, gap: Gap,
                        headerGap: 0f, edgeFade: 0f,
                        keyOf: static (item, _) => item.Id.ToString(),
                        controller: _withController ? _o.Controllers[_i] : null) with { Key = "shelf" },
                ],
            };
        }

        /// <summary>A component row with plain boxes — the "component" arm.</summary>
        sealed class PlainRow : Component
        {
            public override Element Render() => new BoxEl
            {
                Direction = 1, Width = ShelfW,
                Children = [new BoxEl { Height = 24f, Fill = ColorF.FromRgba(60, 60, 60) }, new BoxEl { Height = 44f, Fill = ColorF.FromRgba(40, 80, 40) }],
            };
        }

        /// <summary>A component row hosting a plain horizontal ItemsView of the same 12 cards — a nested scroller with
        /// none of PagedShelf's paging/latch/controller logic (the "nested-list" arm).</summary>
        sealed class NestedListRow : Component
        {
            readonly ShelfZoneList _o;
            readonly RepeatLayout _cards = RepeatLayout.HorizontalGrid(1, CardW, Gap);
            public NestedListRow(ShelfZoneList o) => _o = o;
            public override Element Render() => new BoxEl
            {
                Direction = 1, Width = ShelfW,
                Children =
                [
                    new BoxEl { Height = 24f, Fill = ColorF.FromRgba(60, 60, 60) },
                    new BoxEl
                    {
                        Height = 44f, Width = ShelfW,
                        Children = [ItemsView.Create(ItemCount, i => new BoxEl { Width = CardW, Height = 44f, Children = [Ui.Text(_o.Items[i].Title)] }, _cards,
                            new ListOptions { Grow = 1f, SelectionMode = ItemsSelectionMode.None })],
                    },
                ],
            };
        }

        Element Row(int i)
        {
            int idx = i;
            Element body = _kind switch
            {
                RowKind.Plain => new BoxEl
                {
                    Direction = 1, Width = ShelfW,
                    Children = [new BoxEl { Height = 24f, Fill = ColorF.FromRgba(60, 60, 60) }, new BoxEl { Height = 44f, Fill = ColorF.FromRgba(40, 80, 40) }],
                },
                RowKind.Component => Embed.Comp(() => new PlainRow()),
                RowKind.NestedList => Embed.Comp(() => new NestedListRow(this)),
                RowKind.ShelfNoController => Embed.Comp(() => new ShelfRow(this, idx, withController: false)),
                _ => Embed.Comp(() => new ShelfRow(this, idx)),
            };
            return new BoxEl { Key = "zone:" + i, Direction = 1, Height = RowH, Children = [body] };
        }

        public override Element Render() => new BoxEl
        {
            Width = ViewW, Height = ViewH,
            Children = [ItemsView.Create(Rows, Row, _layout, new ListOptions { Grow = 1f, SelectionMode = ItemsSelectionMode.None })],
        };
    }

    static void Settle(AppHost host, int frames = 12) { for (int i = 0; i < frames; i++) host.RunFrame(); }

    /// <summary>Pump frames until <paramref name="vp"/>'s main-axis offset has been still for four consecutive frames
    /// (a chevron glide is an animated bring-into-view; twelve frames is not enough for it to land). Returns the number
    /// of frames pumped, capped at 240.</summary>
    static int SettleUntilStill(AppHost host, SceneStore s, NodeHandle vp)
    {
        double last = double.NaN;
        int still = 0, frames = 0;
        while (frames < 240 && still < 4)
        {
            host.RunFrame(); frames++;
            if (!s.TryGetScroll(vp, out var sc)) break;
            double now = sc.Offset;
            still = now == last ? still + 1 : 0;
            last = now;
        }
        return frames;
    }

    /// <summary>The outer zone-list viewport (ItemCount == Rows) and the shelf viewports (ItemCount == ItemCount) in
    /// document order — the first shelf viewport belongs to the first realized row.</summary>
    static (NodeHandle outer, List<NodeHandle> shelves) Viewports(SceneStore s)
    {
        var all = new List<NodeHandle>();
        CollectScrollNodes(s, s.Root, all);
        var outer = NodeHandle.Null;
        var shelves = new List<NodeHandle>();
        foreach (var n in all)
        {
            if (!s.TryGetScroll(n, out var sc)) continue;
            if (sc.ItemCount == ShelfZoneList.Rows && outer.IsNull) outer = n;
            else if (sc.ItemCount == ShelfZoneList.ItemCount) shelves.Add(n);
        }
        return (outer, shelves);
    }

    static void ShelfRebindRestoresPageChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-rebind", new Size2(ShelfZoneList.ViewW, ShelfZoneList.ViewH), 1f));
        window.Show();
        var probe = new ShelfZoneList();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        Settle(host);
        var scene = host.Scene;
        var (outer, shelves0) = Viewports(scene);
        var outerHandle = host.TryGetScrollHandle(outer)!;
        var ctl = probe.Controllers[0];

        bool startsRight = shelves0.Count > 0 && ctl.PageCount.Peek() == 4 && ctl.Page.Peek() == 0 && probe.Constructions[0] == 1;

        // Page row 0's shelf to page 2 through its controller: the shelf's REAL offset lands on the page-2 boundary (660).
        ctl.GoTo(2);
        int glideFrames = shelves0.Count > 0 ? SettleUntilStill(host, scene, shelves0[0]) : 0;
        scene.TryGetScroll(shelves0[0], out var shelfAt2);
        bool pagedTo2 = ctl.Page.Peek() == 2 && Near((float)shelfAt2.OffsetX, 660f, 1f);

        // Scroll the zone list far enough that row 0 leaves the realized window (a keyed component row is not
        // recyclable: the reconciler REMOVES it — the ShelfRow and its PagedShelfCore are disposed).
        outerHandle.ScrollTo(ShelfZoneList.RowH * 7, ScrollMove.Immediate);
        Settle(host);
        scene.TryGetScroll(outer, out var scAway);
        bool row0Gone = scAway.FirstRealized > 0 && probe.Constructions[0] == 1;

        // And back: row 0 re-realizes as a FRESH ShelfRow (constructions == 2) whose new PagedShelfCore re-binds the
        // SAME controller. The contract: the shelf returns to page 2 — controller.Page still reads 2 and the shelf's own
        // offset is the page-2 boundary again.
        outerHandle.ScrollTo(0.0, ScrollMove.Immediate);
        Settle(host);
        var (_, shelvesBack) = Viewports(scene);
        if (shelvesBack.Count > 0) SettleUntilStill(host, scene, shelvesBack[0]);   // let any restore glide land before reading
        scene.TryGetScroll(outer, out var scBack);
        bool remounted = scBack.FirstRealized == 0 && probe.Constructions[0] == 2 && shelvesBack.Count > 0 && shelvesBack[0] != shelves0[0];
        int pageAfter = ctl.Page.Peek();
        double offsetAfter = shelvesBack.Count > 0 && scene.TryGetScroll(shelvesBack[0], out var shelfBack) ? shelfBack.OffsetX : double.NaN;
        bool pageRestored = pageAfter == 2 && Near((float)offsetAfter, 660f, 1f);

        // E18 landed (home-redesign-remediation.md §2): the ctor seeds _page from controller.Page.Peek() right after
        // Bind, and the bring-into-view effect's first realize is forced non-animated (ScrollMove.Immediate) so a
        // restored page lands rather than glides — so this is now a plain Check, not EvidenceGate.KnownFailing.
        Check("gate.shelf.controller.rebind-restores-page a PagedShelf bound to a caller-owned ShelfController, paged to 2, recycled OUT of the ItemsView window and back (a real remount: a fresh ShelfRow + PagedShelfCore re-binding the same controller) returns to page 2 — controller.Page reads 2 and the shelf's own offset is the page-2 boundary",
            startsRight && pagedTo2 && row0Gone && remounted && pageRestored,
            $"start={startsRight} pagedTo2={pagedTo2}(offset={shelfAt2.OffsetX:0} after {glideFrames} glide frames) row0Gone={row0Gone}(first={scAway.FirstRealized}) remounted={remounted}(ctor={probe.Constructions[0]}) pageAfter={pageAfter}(exp 2) offsetAfter={offsetAfter:0}(exp 660)");
    }

    // ══ 3. Flow.KeepAlive re-runs `view` on a same-key token change ═══════════════════════════════════════════════

    sealed record RouteTok(string Kind, string Arg);
    sealed record PageProps(string Arg);

    /// <summary>The Shell's ContentHost shape, reduced: <c>Flow.KeepAlive(() => route, r => r.Kind, r => PageBox(r))</c>
    /// where the page box is a keyed BoxEl wrapping <c>Embed.Comp(new PageProps(r.Arg), factory)</c>; the slot key
    /// ignores the arg (Home's KeyedByArg = false) so a Home→Home route with a different arg is a same-key token change.</summary>
    sealed class KeepAliveProbe : Component
    {
        public readonly Signal<RouteTok> Route = new(new RouteTok("home", ""));
        public int ViewCalls, Constructions, Renders, TransitionCalls;
        public string? LastArg;
        public object? LastOld, LastNew;

        /// <summary>Only the "home" page is counted — the "search" page mounted to park it is a different instance by design.</summary>
        sealed class ArgPage : Component
        {
            readonly KeepAliveProbe _o;
            readonly bool _home;
            public ArgPage(KeepAliveProbe o, bool home) { _o = o; _home = home; if (home) o.Constructions++; }
            public override Element Render()
            {
                var p = UseProps<PageProps>();
                if (_home) { _o.Renders++; _o.LastArg = p.Arg; }
                return new BoxEl { Width = 40f, Height = 40f, Children = [Ui.Text("arg:" + p.Arg)] };
            }
        }

        public override Element Render() => Flow.KeepAlive(
            () => Route.Value,
            static r => r.Kind,
            r =>
            {
                ViewCalls++;
                return new BoxEl
                {
                    Key = "page:" + r.Kind, Direction = 1,
                    Children = [Embed.Comp(new PageProps(r.Arg), () => new ArgPage(this, r.Kind == "home"))],
                };
            },
            new KeepAliveOptions(MaxEntries: 3, TransitionFor: (o, n) => { TransitionCalls++; LastOld = o; LastNew = n; return null; }));
    }

    static void KeepAliveSameKeyViewChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("keepalive-same-key", new Size2(200, 200), 1f));
        window.Show();
        var probe = new KeepAliveProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame(); host.RunFrame();
        bool mounted = probe.ViewCalls == 1 && probe.Constructions == 1 && probe.Renders == 1 && probe.LastArg == "";

        // Same key ("home"), different token (the facet arg): view must re-run, the SAME page instance must receive the
        // new arg through its re-pushed props, and TransitionFor must see the (old, new) edge.
        var first = probe.Route.Peek();
        var second = new RouteTok("home", "podcasts");
        probe.Route.Value = second;
        host.RunFrame(); host.RunFrame();
        bool viewReran = probe.ViewCalls == 2;
        bool sameInstanceNewArg = probe.Constructions == 1 && probe.Renders == 2 && probe.LastArg == "podcasts";
        bool edgeDelivered = probe.TransitionCalls == 1 && ReferenceEquals(probe.LastOld, first) && ReferenceEquals(probe.LastNew, second);

        // Park it under another key, then come back with a THIRD arg: reactivation + the re-push land in one pass.
        probe.Route.Value = new RouteTok("search", "");
        host.RunFrame(); host.RunFrame();
        int viewsAtSearch = probe.ViewCalls;
        probe.Route.Value = new RouteTok("home", "music");
        host.RunFrame(); host.RunFrame();
        bool reactivatedWithArg = probe.ViewCalls == viewsAtSearch + 1 && probe.Constructions == 1 && probe.LastArg == "music";

        Check("gate.keepalive.same-key-view Flow.KeepAlive re-runs `view` on a SAME-KEY token change: the retained page instance (one construction) receives the new token's arg through re-pushed props, TransitionFor sees the (old, new) edge, and a park-and-return with a third arg re-pushes again",
            mounted && viewReran && sameInstanceNewArg && edgeDelivered && reactivatedWithArg,
            $"mounted={mounted} views={probe.ViewCalls} ctor={probe.Constructions} renders={probe.Renders} lastArg={probe.LastArg} transitions={probe.TransitionCalls} edge={edgeDelivered} reactivated={reactivatedWithArg}");
    }

    // ══ 4. realizing a row with a nested PagedShelf allocates only on the reconcile edge ═══════════════════════════

    readonly record struct AllocArm(long SteadyAtRest, string Ticks, long TickFirst, long TickWorstAfterFirst, long TickLast, bool TickRendered, bool TickNoRealize,
                                    string Ticks2, long Tick2Worst,
                                    long Edge, bool RealizedNewRows, long SteadyAfterRealize, string Window);

    static AllocArm RunAllocArm(StringTable strings, HeadlessFontSystem fonts, RowKind kind, double tickDip = 20.0)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nested-shelf-alloc-" + kind, new Size2(ShelfZoneList.ViewW, ShelfZoneList.ViewH), 1f));
        window.Show();
        var probe = new ShelfZoneList(kind);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        Settle(host, 16);
        var scene = host.Scene;
        var (outer, _) = Viewports(scene);
        var outerHandle = host.TryGetScrollHandle(outer)!;

        long MaxSteady(int frames)
        {
            long worst = 0;
            for (int i = 0; i < frames; i++) worst = Math.Max(worst, host.RunFrame().HotPhaseAllocBytes);
            return worst;
        }

        long steadyAtRest = MaxSteady(5);

        // Warm-up: one round trip over the same distance the ticks below will cover (and the realize edge further down),
        // so every capacity that grows once (tile ledgers, coverage rows, the headless device's lists) has grown. A byte
        // that survives this warm-up on a later tick is a STEADY cost, not a first-time growth.
        outerHandle.ScrollTo(ShelfZoneList.RowH * 4, ScrollMove.Immediate);
        Settle(host, 16);
        outerHandle.ScrollTo(0.0, ScrollMove.Immediate);
        Settle(host, 16);
        MaxSteady(3);

        // Sixteen consecutive transform-only scroll ticks inside the realized window (no realize edge): the first may
        // still carry a plan-slot warm-up; every later tick is the steady scrolling cost. Each tick's hot-phase bytes are
        // split by the always-on attribution: bytes inside the reactive flush (RebindFlushAllocBytes) and the number of
        // reactive units that ran.
        scene.TryGetScroll(outer, out var sc0);
        var ticks = new System.Text.StringBuilder();
        long tickFirst = 0, tickWorstAfterFirst = 0, tickLast = 0;
        bool tickRendered = false;
        for (int t = 0; t < 16; t++)
        {
            outerHandle.ScrollBy(tickDip, ScrollMove.Immediate);
            var f = host.RunFrame();
            if (t > 0) ticks.Append('/');
            ticks.Append(f.HotPhaseAllocBytes);
            if (f.Rendered) ticks.Append('R');   // the frame RECORDED (a paint change), not a pure composite turn
            if (f.RebindFlushAllocBytes != 0 || f.ReactiveUnits != 0) ticks.Append($"(flush={f.RebindFlushAllocBytes} rx={f.ReactiveUnits})");
            if (t == 0) tickFirst = f.HotPhaseAllocBytes; else tickWorstAfterFirst = Math.Max(tickWorstAfterFirst, f.HotPhaseAllocBytes);
            tickLast = f.HotPhaseAllocBytes;
            tickRendered |= f.Rendered;
        }
        scene.TryGetScroll(outer, out var sc1);
        bool tickNoRealize = sc1.FirstRealized == sc0.FirstRealized && sc1.LastRealized == sc0.LastRealized;

        // A SECOND pass over the same ticks from the same start: a byte that appears in pass 1 and not in pass 2 is a
        // first-time growth of a structure the immediate warm-up jumps never exercised (a state the app also reaches
        // once); a byte that recurs is the steady scrolling cost. (Measured 2026-09-26 with --fg alloc's per-segment
        // probes: the shelf arms' lone pass-1 byte count — 8,728 B, positional, the first tick past offset 40 — sits in
        // the `submit` segment, i.e. the composite turn over the slice arenas, the first time a shelf row is partially
        // clipped by the viewport top; pass 2 is 0 in every arm.)
        outerHandle.ScrollTo(0.0, ScrollMove.Immediate);
        Settle(host, 16);
        MaxSteady(3);
        var ticks2 = new System.Text.StringBuilder();
        long tick2Worst = 0;
        for (int t = 0; t < 16; t++)
        {
            outerHandle.ScrollBy(tickDip, ScrollMove.Immediate);
            var f = host.RunFrame();
            if (t > 0) ticks2.Append('/');
            ticks2.Append(f.HotPhaseAllocBytes);
            if (f.Rendered) ticks2.Append('R');
            tick2Worst = Math.Max(tick2Worst, f.HotPhaseAllocBytes);
        }

        // The realize edge: a jump that brings NEW rows into the window. Allocation is expected on this frame (mount =
        // reconcile edge) and must be gone once the rows (and their shelves) have settled.
        outerHandle.ScrollTo(ShelfZoneList.RowH * 4, ScrollMove.Immediate);
        var edge = host.RunFrame();
        scene.TryGetScroll(outer, out var sc2);
        bool realizedNewRows = sc2.FirstRealized != sc1.FirstRealized || sc2.LastRealized != sc1.LastRealized;
        Settle(host, 16);
        long steadyAfterRealize = MaxSteady(5);

        return new AllocArm(steadyAtRest, ticks.ToString(), tickFirst, tickWorstAfterFirst, tickLast,
            tickRendered, tickNoRealize, ticks2.ToString(), tick2Worst, edge.HotPhaseAllocBytes, realizedNewRows, steadyAfterRealize,
            $"{sc1.FirstRealized}-{sc1.LastRealized}→{sc2.FirstRealized}-{sc2.LastRealized}");
    }

    static string Describe(in AllocArm a)
        => $"steadyAtRest={a.SteadyAtRest} ticks={a.Ticks}(anyRendered={a.TickRendered} noRealize={a.TickNoRealize}) pass2={a.Ticks2} edge={a.Edge}(window {a.Window} newRows={a.RealizedNewRows}) steadyAfterRealize={a.SteadyAfterRealize}";

    static void NestedShelfRealizeAllocChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var shelf = RunAllocArm(strings, fonts, RowKind.Shelf);
        var plain = RunAllocArm(strings, fonts, RowKind.Plain);
        var comp = RunAllocArm(strings, fonts, RowKind.Component);
        var nested = RunAllocArm(strings, fonts, RowKind.NestedList);
        var noCtl = RunAllocArm(strings, fonts, RowKind.ShelfNoController);
        var shelf10 = RunAllocArm(strings, fonts, RowKind.Shelf, tickDip: 10.0);   // positional (offset) or temporal (frame count)?

        Check("gate.items.nested-shelf-realize-alloc realizing zone rows that each host a PagedShelf allocates only on the reconcile edge: steady frames at rest and steady frames after the realize both stay at 0 hot-phase bytes",
            shelf.SteadyAtRest == 0 && shelf.RealizedNewRows && shelf.SteadyAfterRealize == 0,
            "shelf rows: " + Describe(in shelf));

        // The scrolling cost, bisected: the same list with plain rows (the list's own baseline), with a component row of
        // plain boxes, with a component row hosting a plain nested horizontal ItemsView, and with the PagedShelf row. A
        // tick that allocates in one arm and not in the arm below it belongs to that layer.
        Check("gate.items.nested-shelf-scroll-tick-alloc steady transform-only scrolling over realized zone rows that host PagedShelves (a second pass over the same 16 ticks, every first-time growth already taken) allocates no more per tick than the same pass over plain rows",
            shelf.Tick2Worst <= plain.Tick2Worst && shelf10.Tick2Worst <= plain.Tick2Worst,
            "shelf: " + Describe(in shelf) + " | shelf@10px: " + Describe(in shelf10) + " | shelf-no-controller: " + Describe(in noCtl) + " | nested-list: " + Describe(in nested) + " | component: " + Describe(in comp) + " | plain: " + Describe(in plain));
    }
}
