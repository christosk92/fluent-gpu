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
    }

    // ── gate.budgets.weak-image-cache-derived-from-local ────────────────────────────────────────────────────────
    static void WeakImageCacheDerivedFromLocalChecks()
    {
        const long MB = 1024 * 1024;

        long at128 = GpuMemoryBudgets.For(weak: true, localBudgetBytes: 128 * MB).ImageCache;
        Check("gate.budgets.weak-image-cache-derived-from-local a 128 MB LOCAL segment (the Adreno-class part this shipped against) derives exactly the 40 MB flat default it replaces — a derivation, not a re-tune",
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
