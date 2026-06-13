namespace FluentGpu.SourceGen;

/// <summary>
/// SCAFFOLD markers for the build-time generators that are designed but not yet implemented in this
/// (now-unified) analyzer assembly. The two SHIPPING generators live in <c>Localization/</c> and
/// <c>Validation/</c>; the items below are the still-unbuilt ones folded here when the four separate
/// SourceGen projects were consolidated into this one.
///
/// <para>Engine codegen (see design/subsystems/dsl-aot.md): ElementTypeId, modifier extensions, bitmask
/// DiffProps, the scene-writer (homed in the reconciler/leaf), HookDeps (≤4-arity capture + lanes/transition
/// lowering), Theme blobs, and the WGPU####/FG#### analyzers. Portable / Win32-free.</para>
///
/// <para>COM-binding generator (see design/subsystems/com-interop.md): emits hand-vtable <c>IComObject</c>
/// consume bindings + callee CCW vtables from a harvested <c>*.comabi.json</c> (no human-typed slot indices),
/// plus the <c>FGCOM####</c> rules. It stays Win32-free AT THE SOURCE LEVEL (no Win32/TerraFX PackageReference),
/// so hosting it here does not pull a Win32 dependency into the portable toolchain.</para>
/// </summary>
internal static class SourceGenMarker;
