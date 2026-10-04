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
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// GpuMemoryBudgets.For's weak-tier image-cache cap (adreno-hang-fixes.md M3/E3): it is no longer the flat
// ImageCacheWeak constant on every weak adapter — it is 5/16 of that adapter's DXGI LOCAL segment budget, clamped
// to [32, 64] MB, with 0 (unknown LOCAL — headless, or the sample has not landed yet) falling back to the flat 40 MB
// default so nothing regresses for a caller that cannot supply a real sample. This suite drives the derivation
// directly, the same "take the tier flag/sample as an argument, not a global" shape ImageSuite's BudgetChecks and
// LayerPoolSuite's TrimVerdictChecks already use, and for the same reason: GpuProfile.IsWeak is ALWAYS false
// headlessly, so only an explicit-parameter API can be exercised for both tiers here.

static class BudgetsSuite
{
    public static void Run(StringTable strings)
    {
        WeakImageCacheDerivedFromLocalChecks();
        UmaLocalBudgetPremiseChecks();
    }

    // ── gate.budgets.uma-local-budget-is-the-shared-pool (F251) ─────────────────────────────────────────────────
    // On UMA (the Adreno X1 and every iGPU) DXGI reports the SHARED pool as the LOCAL segment: the on-box log reads
    // `vramMB=128 sharedMB=16162 uma=True tier=Weak` next to `budget:15394.5`. That LOCAL budget is the OS residency
    // budget the driver enforces and stays authoritative (budgets are NOT derived from the 128 MB DedicatedVideoMemory,
    // which would make the shed fire every frame), so the weak image cache lands at the 64 MB clamp and the VRAM shed stays
    // disarmed until real system memory pressure shrinks that budget. This pins that outcome so a "fix" that re-derives
    // from the carve-out fails here.
    static void UmaLocalBudgetPremiseChecks()
    {
        const long MB = 1024 * 1024;
        const long Local15Gb = 15394L * MB + MB / 2;   // the budget the on-box log reports on the 16 GB Adreno machine

        long umaCache = GpuMemoryBudgets.For(weak: true, localBudgetBytes: Local15Gb).ImageCache;
        Check("gate.budgets.uma-local-budget-is-the-shared-pool a UMA adapter's ~15 GB LOCAL budget (uma=true, tier=Weak) pins the weak image cache at the 64 MB clamp: the OS residency budget, not the 128 MB carve-out, is the premise",
            umaCache == 64 * MB,
            $"ImageCache={umaCache / MB}MB");

        var shed = new VramShedPolicy();
        bool calm = false;
        for (int i = 0; i < 8; i++) calm |= shed.Decide((260 + i) * MB, Local15Gb);   // the on-box used range: 0.8-484 MB of 15.4 GB
        Check("gate.budgets.uma-local-budget-is-the-shared-pool VramShedPolicy stays disarmed at the on-box UMA usage (a few hundred MB of a ~15 GB LOCAL budget)",
            !calm && !shed.Armed,
            $"fired={calm} armed={shed.Armed}");

        bool underPressure = shed.Decide(14 * 1024 * MB, Local15Gb);
        Check("gate.budgets.uma-local-budget-is-the-shared-pool the same policy arms and sheds once usage crosses 0.90 of that LOCAL budget (the OS shrinking the budget under system memory pressure)",
            underPressure && shed.Armed,
            $"fired={underPressure} armed={shed.Armed}");
    }

    // ── gate.budgets.weak-image-cache-derived-from-local ────────────────────────────────────────────────────────
    static void WeakImageCacheDerivedFromLocalChecks()
    {
        const long MB = 1024 * 1024;

        long at128 = GpuMemoryBudgets.For(weak: true, localBudgetBytes: 128 * MB).ImageCache;
        Check("gate.budgets.weak-image-cache-derived-from-local a 128 MB LOCAL segment (a part whose LOCAL budget really is ~128 MB) derives exactly the 40 MB flat default it replaces — a derivation, not a re-tune",
            at128 == 40 * MB,
            $"ImageCache={at128 / MB}MB");

        long at256 = GpuMemoryBudgets.For(weak: true, localBudgetBytes: 256 * MB).ImageCache;
        Check("gate.budgets.weak-image-cache-derived-from-local a larger LOCAL segment is allowed up past 40 MB, capped at the discrete cache's own 64 MB ceiling so a weak tier never out-budgets the discrete default",
            at256 == 64 * MB,
            $"ImageCache={at256 / MB}MB");

        long at64 = GpuMemoryBudgets.For(weak: true, localBudgetBytes: 64 * MB).ImageCache;
        Check("gate.budgets.weak-image-cache-derived-from-local a small LOCAL segment is floored at 32 MB so the eviction/prefetch ring never shrinks below what it needs to stay useful",
            at64 == 32 * MB,
            $"ImageCache={at64 / MB}MB");

        long atUnknown = GpuMemoryBudgets.For(weak: true, localBudgetBytes: 0).ImageCache;
        Check("gate.budgets.weak-image-cache-derived-from-local an unknown LOCAL segment (0 — headless, or the sample has not landed yet) keeps the flat 40 MB weak default instead of deriving from a meaningless sample",
            atUnknown == 40 * MB,
            $"ImageCache={atUnknown / MB}MB");

        long strongAt128 = GpuMemoryBudgets.For(weak: false, localBudgetBytes: 128 * MB).ImageCache;
        Check("gate.budgets.weak-image-cache-derived-from-local the discrete tier never reads localBudgetBytes at all — it stays the flat 64 MB default regardless of what LOCAL sample is passed",
            strongAt128 == 64 * MB,
            $"ImageCache={strongAt128 / MB}MB");
    }
}
