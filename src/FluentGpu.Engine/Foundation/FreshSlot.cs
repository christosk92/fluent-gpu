using System.Runtime.CompilerServices;

namespace FluentGpu.Foundation;

/// <summary>THE way a pooled append buffer hands out a slot that its caller then fills field by field: cleared, then
/// returned by reference.
///
/// <para><b>Why a non-inlined span clear and not <c>ref T row = ref a[i]; row = default;</c>.</b> On .NET 10.0.8 (arm64
/// AND x64, still present in 11.0 preview 4) the optimising JIT DROPS a whole-slot zero made through a reference —
/// <c>row = default</c>, <c>this = default</c>, <c>Unsafe.InitBlockUnaligned</c> — when the next store writes a zero
/// struct into the slot's first field (after inlining, e.g. an <c>Init(default, …)</c> or a constructed id whose temp the
/// JIT zero-initialises). Only the smaller store survives, so the slot keeps the previous tenant's other fields. It
/// shows only after tier-up (round ~161 of a reuse loop; <c>TieredPGO=0</c> hides the <c>row = default</c> form), so a
/// Debug run never sees it. Wavee's staged rows persisted another row's text through it (2026-09-25). A span clear
/// inside a <see cref="MethodImplOptions.NoInlining"/> call is an opaque store the caller's JIT cannot elide. Repro,
/// matrix and upstream draft: <c>docs/plans/dotnet-jit-zeroing-miscompile.md</c>; pinned by
/// <c>SlotZeroingTests</c> (engine) and <c>StagingLeaseTests</c> (Wavee), both of which only bite in <c>-c Release</c>.</para></summary>
public static class FreshSlot
{
    /// <summary>Zero <paramref name="array"/>[<paramref name="index"/>] and return it by reference.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ref T Of<T>(T[] array, int index) where T : struct
    {
        array.AsSpan(index, 1).Clear();
        return ref array[index];
    }
}
