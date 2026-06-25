namespace FluentGpu.SourceGen;

/// <summary>
/// Historical scaffold marker. The build-time generators this once reserved are now IMPLEMENTED in this
/// (unified) analyzer assembly — see <c>docs/plans/source-generators-opportunity-investigation.md</c> §"Implementation
/// status" for the verdict + form of each. The two original SHIPPING generators live in <c>Localization/</c> and
/// <c>Validation/</c>; the engine + COM generators landed alongside:
///
/// <para>Engine codegen (design/subsystems/dsl-aot.md): <c>Engine/ElementGenerator.cs</c> (the marker attributes +
/// the WGPU0003 ElementTypeId guard, LIVE), <c>Engine/DiffPropsGenerator.cs</c> (the bitmask DiffProps + ref-equality),
/// <c>Engine/ThemeBlobGenerator.cs</c> (Theme blobs/TokenId), <c>Engine/RejectedSetGenerators.cs</c> (the verified
/// net-negative set, dormant), and <c>Engine/GatedMigrationGenerators.cs</c> (HookDeps/cold-slab/static-hoist behind
/// default-off markers). Each non-live generator is dormant until its trigger is present, so it cannot regress a build.</para>
///
/// <para>COM-binding generator (design/subsystems/com-interop.md): <c>Interop/ComInteropGenerator.cs</c> — hand-vtable
/// <c>IComObject</c> bindings from a harvested <c>*.comabi.json</c> (no human-typed slots), dormant until a manifest is
/// checked in. Stays Win32-free AT THE SOURCE LEVEL, so it does not pull a Win32 dependency into the portable toolchain.
/// The WGPU####/FGCOM#### analyzer family is partially landed (WGPU0003) and otherwise designed-but-deferred.</para>
/// </summary>
internal static class SourceGenMarker;
