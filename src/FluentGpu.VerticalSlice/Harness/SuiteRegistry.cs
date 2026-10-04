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
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;


namespace FluentGpu.VerticalSlice.Harness;

public readonly record struct SuiteEntry(string Id, string Tag, Action<StringTable> Run);

/// <summary>Explicit ordered suite registry — no reflection (AOT-safe).</summary>
public static class SuiteRegistry
{
    public static readonly SuiteEntry[] All =
    [
        new("geolocation", "geolocation", GeolocationSuite.Run),
        new("layout", "layout", LayoutShellSuite.Run),
        new("layout-inc", "layout-inc", FluentGpu.VerticalSlice.Suites.LayoutIncrementalSuite.Run),
        new("hooks", "hooks", HooksSuite.Run),
        new("anim", "anim", AnimSuite.Run),
        new("scroll", "scroll", ScrollSuite.Run),
        new("scroll-motion", "scroll", FluentGpu.VerticalSlice.Suites.ScrollMotionSuite.Run),   // scroll rework: closed-form plans, coverage, zero-alloc flat lists
        new("scroll-effects", "scroll", FluentGpu.VerticalSlice.Suites.ScrollEffectsSuite.Run),   // scroll-GPU plan §F: engaged edge, collapse, stretch, UseScroll, MeasureAll
        new("engaged-feather", "scroll", FluentGpu.VerticalSlice.Suites.EngagedFeatherSuite.Run),   // F(ii) 2026-09-25: a WhileStuck edge fade engages on the sticky clip's own pose
        new("item-band-deep", "scroll", FluentGpu.VerticalSlice.Suites.ItemBandDeepSuite.Run),   // G 2026-09-25: a deep jump in an item-band list keeps every row above the arrange origin
        new("wake-present", "wake-present", IdleWakePresentChecks.Run),   // idle→wheel-notch first-frame present + missed-vsync artifact fix
        new("touch", "touch", TouchSuite.Run),
        new("image", "image", ImageSuite.Run),
        new("budgets", "budgets", BudgetsSuite.Run),
        new("tiles", "tiles", FluentGpu.VerticalSlice.Suites.TileSuite.Run),   // scroll-GPU plan P0: retained-tile needed set, slice table, budget, feather, composite seam
        new("slices", "tiles", FluentGpu.VerticalSlice.Suites.SliceSuite.Run),   // scroll-GPU plan P1: the recorder partition — paint order, zero-byte scroll tick, tile invalidation reasons
        new("evidence", "tiles", FluentGpu.VerticalSlice.Suites.EvidenceSuite.Run),   // evidence ledgers: #1 failing-first (open), ledger alloc-zero, capture alignment, item record == model
        new("tile-lifetime", "tiles", FluentGpu.VerticalSlice.Suites.TileLifetimeChecks.Run),   // tile texture lifetime at idle: retained group / leaf-blur tiles keep their textures (Wavee idle blanking)
        new("controls", "controls", ControlsSuite.Run),
        new("zone-list-spikes", "controls", FluentGpu.VerticalSlice.Suites.ZoneListSpikeChecks.Run),   // Wave-0 spikes for Wavee's Home zone list: E17 sticky-in-realized-row, E18 controller rebind, KeepAlive same-key view, nested-shelf alloc
        new("component-anchor", "controls", FluentGpu.VerticalSlice.Suites.ComponentAnchorChecks.Run),   // E14: WriteAnchorColumns applies a ComponentEl's base-Element props to its own anchor (sticky/visible/exit/alloc)
        new("titlebar", "titlebar", TitleBarSuite.Run),
        new("nav", "nav", NavSuite.Run),
        new("overlay", "overlay", OverlaySuite.Run),
        new("layerpool", "layerpool", LayerPoolSuite.Run),
        new("damage", "damage", DamageSuite.Run),
        new("path", "path", PathSuite.Run),
        new("series", "series", SeriesSuite.Run),   // SeriesEl: chunked DrawSeriesCmd arithmetic + headless decode + the bound sample source's zero-alloc steady state; also the WindowOccluded hook gate
        new("lottie", "lottie", LottieSuite.Run),
        new("text", "text", TextSuite.Run),
        new("span-links", "text", SpanLinkDispatchChecks.Run),   // inline-hyperlink dispatch ownership; runs with --suite text
        new("bound", "bound", BoundTemplateSuite.Run),
        new("listrow", "listrow", ListRowSuite.Run),   // scroll-rework Wave 0.E: ListRowEl one-node-per-row / zero-alloc-text-change / placeholder-geometry gates
        new("diagnostics", "diagnostics", DiagnosticsSuite.Run),
        new("media-seam", "media-seam", MediaSeamSuite.Run),
        new("continuity", "continuity", VisualContinuityChecks.Run),
        new("detached-render", "detached-render", FluentGpu.VerticalSlice.Suites.DetachedRenderResilienceSuite.Run),
        new("detached-pacing", "detached-pacing", FluentGpu.VerticalSlice.Suites.DetachedPacingSuite.Run),   // F094/F097: a pop-out's motion is not the parent's present; per-target pacing evidence and queue depth
    ];

    public static IEnumerable<SuiteEntry> Filter(string? suiteSpec)
    {
        if (string.IsNullOrWhiteSpace(suiteSpec) || suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        var tags = suiteSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("all")) return All;

        var known = new HashSet<string>(All.Select(e => e.Tag), StringComparer.OrdinalIgnoreCase);
        known.Add("core");
        known.Add("all");
        foreach (var tag in set)
        {
            if (!known.Contains(tag))
                throw new ArgumentException("Unknown suite '" + tag + "'. Known: " + KnownSuitesHelp());
        }

        // core is handled by Program (checks 1-9); filter returns matching registry entries only.
        return All.Where(e => set.Contains(e.Tag));
    }

    public static string KnownSuitesHelp() =>
        "core, all, " + string.Join(", ", All.Select(e => e.Tag).Distinct());
}
