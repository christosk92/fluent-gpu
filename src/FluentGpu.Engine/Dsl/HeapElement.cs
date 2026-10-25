namespace FluentGpu.Dsl;

/// <summary>A boundary element kept as an object (PERF r3 MODEL M4b): ContextProvider, VirtualList, For, SkelRegion, KeepAlive.
/// Few per page, each kept by its boundary node anyway; it converts to an <see cref="Element"/> handle implicitly.</summary>
public abstract partial record HeapElement : IElement
{
    public abstract ushort ElementTypeId { get; }

    public static implicit operator Element(HeapElement element) => new(element);

    /// <summary>This element with its element-valued members frozen (DESIGN 2.2, escapes).</summary>
    internal virtual HeapElement FreezeMembers() => SkeletonOverride is { } s ? this with { SkeletonOverride = s.Freeze() } : this;
}
