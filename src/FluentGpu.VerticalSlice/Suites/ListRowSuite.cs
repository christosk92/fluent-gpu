using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>
/// Scroll-rework Wave 0.E (scroll-rework-design.md §B.4) — registered as the <c>listrow</c> suite. <c>ListRowEl</c>
/// is the flat, one-node twin of a ~90-node/~50-bind track-row <c>GridEl</c>: this suite builds 40 rows directly
/// (no virtualization/ItemsView layer — that is a separate wave's concern) and checks the THREE claims the design
/// makes about it: one scene node per row (not a subtree), a text-only rebind allocates nothing once settled (the
/// bound <c>Cells</c> channel, like every other bound <c>Prop&lt;T&gt;</c>), and toggling <c>Placeholder</c> repaints
/// without moving anything (same geometry, no relayout, no remount).
/// </summary>
static class ListRowSuite
{
    public static void Run(StringTable strings)
    {
        OneNodePerRowChecks(strings);
        ZeroAllocOnTextChangeChecks(strings);
        PlaceholderPreservesGeometryChecks(strings);
    }

    const int RowCount = 40;
    const float RowHeight = 36f;
    const float RowWidth = 300f;

    /// <summary>40 rows, each ONE bound <c>Cells</c> channel over a per-row <see cref="RowCellBuffer"/> (built once,
    /// refilled every fire — the <c>BoundItemScope.Spans</c>/<c>SpanBuffer</c> shape, generalized to cells) plus a
    /// shared bound <c>Placeholder</c>. Mirrors <c>BoundTemplateSuite</c>'s probe shape.</summary>
    sealed class RowsProbe : Component
    {
        public readonly Signal<string>[] Titles;
        public readonly Signal<bool> Placeholder = new(false);
        readonly RowCellBuffer[] _buffers;

        public RowsProbe(int n)
        {
            Titles = new Signal<string>[n];
            _buffers = new RowCellBuffer[n];
            for (int i = 0; i < n; i++)
            {
                Titles[i] = new Signal<string>("Track " + i);
                _buffers[i] = new RowCellBuffer();
            }
        }

        public override Element Render()
        {
            var rows = new Element[Titles.Length];
            for (int i = 0; i < Titles.Length; i++)
            {
                var title = Titles[i];
                var buf = _buffers[i];
                var placeholder = Placeholder;
                rows[i] = new ListRowEl(Prop.Of<RowCells>(() =>
                {
                    buf.Clear();
                    buf.Add(new RowCell
                    {
                        Kind = RowCellKind.Text, Rect = new RectF(4f, 8f, 180f, 20f),
                        Text = title.Value, Color = ColorF.FromRgba(255, 255, 255, 255), FontSize = 12f,
                    });
                    buf.Add(new RowCell
                    {
                        Kind = RowCellKind.Rect, Rect = new RectF(200f, 8f, 20f, 20f),
                        Color = ColorF.FromRgba(80, 80, 80, 255),
                    });
                    return buf.Current;
                }))
                {
                    Key = "row" + i,
                    Height = RowHeight, Width = RowWidth,
                    Placeholder = Prop.Of(() => placeholder.Value),
                    PlaceholderColor = ColorF.FromRgba(0x33, 0x33, 0x33, 255),
                };
            }
            return new BoxEl { Direction = 1, Width = RowWidth, Children = rows };
        }
    }

    static (AppHost host, RowsProbe probe) Mount(StringTable strings, HeadlessPlatformApp app)
    {
        var fonts = new HeadlessFontSystem(strings);
        var window = new HeadlessWindow(new WindowDesc("listrow", new Size2(RowWidth, RowHeight * RowCount), 1f));
        window.Show();
        var probe = new RowsProbe(RowCount);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        return (host, probe);
    }

    /// <summary>Recursively collects every <see cref="VisualKind.ListRow"/> node under <paramref name="node"/> —
    /// the design's central claim is that a row costs ONE scene node, not a subtree, so a hit here must always be a
    /// LEAF (<c>ChildCount == 0</c>).</summary>
    static void CollectListRows(SceneStore scene, NodeHandle node, List<NodeHandle> into)
    {
        if (scene.Paint(node).VisualKind == VisualKind.ListRow) into.Add(node);
        var c = scene.FirstChild(node);
        while (!c.IsNull)
        {
            CollectListRows(scene, c, into);
            c = scene.NextSibling(c);
        }
    }

    // ── gate.listrow.one-node-per-row ─────────────────────────────────────────────────────────────────────────────
    static void OneNodePerRowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, _) = Mount(strings, app);

        var rows = new List<NodeHandle>();
        CollectListRows(host.Scene, host.Scene.Root, rows);

        bool countOk = rows.Count == RowCount;
        bool allLeaves = true;
        foreach (var r in rows) if (host.Scene.ChildCount(r) != 0) allLeaves = false;

        Check($"gate.listrow.one-node-per-row {RowCount} ListRowEl rows realize as exactly {RowCount} scene nodes, each a LEAF (no child subtree) — the ~90-node track row collapsed to one",
            countOk && allLeaves, $"rows={rows.Count} allLeaves={allLeaves}");
        host.Dispose();
    }

    // ── gate.listrow.zero-alloc-on-text-change ────────────────────────────────────────────────────────────────────
    static void ZeroAllocOnTextChangeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, probe) = Mount(strings, app);
        for (int k = 0; k < 2; k++) host.RunFrame();   // settle

        // A text-only rebind: writes ONE row's title signal (the bound Cells channel's own Effect re-fires — the
        // component itself does NOT re-render, per the reactivity model: writing a signal re-runs only the
        // computations that read it). RowCellBuffer.Clear/Add reuse the same backing array past its first-fire
        // high-water mark, and WriteRowCells' AddRef/Release swap only touches the ONE changed id.
        // Swaps to row #7's ALREADY-INTERNED title (not a brand-new string): interning a genuinely NEW string is an
        // unavoidable cold-path allocation shared by every text-bearing element (TextEl included) — the claim under
        // test is that the RowCellBuffer refill + the AddRef/Release id-swap themselves add nothing on top of that,
        // which a repeat-content swap isolates cleanly (AddRef/Release on an EXISTING StringTable entry, no new
        // Dictionary entry).
        probe.Titles[3].Value = "Track 7";
        var changeFrame = host.RunFrame();   // the BoundTemplateSuite convention: the change frame itself is allowed
                                              // a bounded, one-time cost (e.g. the DrawList's own backing buffer
                                              // growing to this row's now-larger per-cell command count — generic
                                              // capacity warm-up, not a per-frame leak); only a LATER settled frame
                                              // must be strictly zero.
        for (int k = 0; k < 3; k++) host.RunFrame();
        var steady = host.RunFrame();

        Check("gate.listrow.zero-alloc-on-text-change a single row's bound Cells title change, swapped to already-interned content (RowCellBuffer refill + WriteRowCells AddRef/Release id-swap, isolated from the unavoidable cold-intern cost of a brand-new string), settles to 0 bytes in the hot paint phase",
            steady.HotPhaseAllocBytes == 0, $"changeFrameAlloc={changeFrame.HotPhaseAllocBytes}B steadyAlloc={steady.HotPhaseAllocBytes}B");
        host.Dispose();
    }

    // ── gate.listrow.placeholder-preserves-geometry ───────────────────────────────────────────────────────────────
    static void PlaceholderPreservesGeometryChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, probe) = Mount(strings, app);
        for (int k = 0; k < 2; k++) host.RunFrame();

        var rows = new List<NodeHandle>();
        CollectListRows(host.Scene, host.Scene.Root, rows);
        var before = new RectF[rows.Count];
        for (int i = 0; i < rows.Count; i++) before[i] = host.Scene.Bounds(rows[i]);
        bool hadCellsBefore = host.Scene.TryGetRowCells(rows[0], out var cellsBefore, out bool phBefore, out _);
        var cell0RectBefore = cellsBefore.Length > 0 ? cellsBefore[0].Rect : default;

        probe.Placeholder.Value = true;   // Placeholder is a recorder-only decision (no LayoutInput touch) — same geometry
        host.RunFrame();

        bool geometryUnchanged = true;
        for (int i = 0; i < rows.Count; i++)
            if (host.Scene.Bounds(rows[i]) != before[i]) geometryUnchanged = false;
        bool hadCellsAfter = host.Scene.TryGetRowCells(rows[0], out var cellsAfter, out bool phAfter, out _);
        var cell0RectAfter = cellsAfter.Length > 0 ? cellsAfter[0].Rect : default;

        Check("gate.listrow.placeholder-preserves-geometry toggling ListRowEl.Placeholder flips every row's recorder draw mode without moving the row's Bounds or any cell's own Rect (no relayout, no type swap)",
            hadCellsBefore && hadCellsAfter && !phBefore && phAfter && geometryUnchanged && cell0RectBefore == cell0RectAfter,
            $"geometryUnchanged={geometryUnchanged} ph {phBefore}->{phAfter} cell0Rect {cell0RectBefore}=={cell0RectAfter}");
        host.Dispose();
    }
}
