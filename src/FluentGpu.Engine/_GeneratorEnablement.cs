// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  GENERATOR ENABLEMENT — the "oneshot enable all generators" directive.
//
//  DiffPropsGenerator (GEN-01/10) is now opt-OUT (fires on every Element record automatically), and TokenSet carries
//  [ThemeTokens] (GEN-08/13). The generators below trigger on marker attributes that have no natural home in the
//  engine, so this file applies each marker once to a dummy declaration to switch the generator ON. Their emitted
//  output is additive (new types in their own namespaces) and not wired into any live path — the source-gen
//  investigation verified these as net-negative / premature / risky-migration; turning them on proves they emit and
//  compile without regressing the engine. Delete this file to turn them back off.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#pragma warning disable CS0169, CS0649, IDE0051, IDE0044 // dummy fields exist only to shape the dummy declarations

namespace FluentGpu.GeneratorEnablement;

[FluentGpu.CodeGen.Element]                  internal sealed class EnableDispatch { }              // GEN-03 dispatch table
[FluentGpu.CodeGen.MotionTokens]             internal sealed class EnableMotionTable { }           // GEN-07 motion table
[FluentGpu.CodeGen.StaticGrid]               internal sealed class EnableGridTracks { }            // GEN-12 grid tracks
[FluentGpu.CodeGen.FastMeasure]              internal sealed class EnableMeasure { }               // GEN-11 specialized measure
[FluentGpu.CodeGen.ReactiveGraph]            internal sealed class EnableSignalGraph { }           // GEN-15 signal graph
[FluentGpu.CodeGen.EnableHookDepsLowering]   internal sealed class EnableHookDeps { }              // GEN-02 GcDepTable/DepDeps
[FluentGpu.CodeGen.EnableColdSlab]           internal sealed class EnableColdSlabHere { }          // GEN-17 ColdSlab<T>
[FluentGpu.CodeGen.EnableStaticHoist]        internal sealed class EnableHoist { }                 // GEN-04 StaticSubtreeCache

[FluentGpu.CodeGen.DrawOp] internal struct EnableOpCodec { public int A; public float B; }        // GEN-09 blittable codec

internal static class EnableMethodGenerators
{
    [FluentGpu.CodeGen.Modifier]    internal static void EnableFusion() { }                        // GEN-14 modifier fusion
    [FluentGpu.CodeGen.SpanFormat]  internal static void EnableSpanFormat() { }                    // GEN-18 span formatting
    [FluentGpu.CodeGen.HandlerThunk] internal static void EnableThunk() { }                        // GEN-06 handler thunk
}
