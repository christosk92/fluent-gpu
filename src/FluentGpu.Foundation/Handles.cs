using System.Diagnostics;

namespace FluentGpu.Foundation;

/// <summary>Generational handle — 8 bytes {u32 index, u32 gen}. The canonical FluentGpu handle layout.</summary>
[DebuggerDisplay("#{Index}.{Gen}")]
public readonly struct Handle : IEquatable<Handle>
{
    public readonly uint Index;
    public readonly uint Gen;

    public Handle(uint index, uint gen) { Index = index; Gen = gen; }

    public bool IsNull => Index == 0u && Gen == 0u;
    public static Handle Null => default;

    public bool Equals(Handle other) => Index == other.Index && Gen == other.Gen;
    public override bool Equals(object? obj) => obj is Handle h && Equals(h);
    public override int GetHashCode() => unchecked((int)(Index * 397u ^ Gen));
    public static bool operator ==(Handle a, Handle b) => a.Equals(b);
    public static bool operator !=(Handle a, Handle b) => !a.Equals(b);
    public override string ToString() => IsNull ? "#null" : $"#{Index}.{Gen}";
}

/// <summary>Zero-cost typed wrapper over <see cref="Handle"/> for nodes in the SceneStore.</summary>
public readonly struct NodeHandle : IEquatable<NodeHandle>
{
    public readonly Handle Raw;
    public NodeHandle(Handle raw) => Raw = raw;
    public bool IsNull => Raw.IsNull;
    public static NodeHandle Null => default;
    public bool Equals(NodeHandle other) => Raw.Equals(other.Raw);
    public override bool Equals(object? obj) => obj is NodeHandle h && Equals(h);
    public override int GetHashCode() => Raw.GetHashCode();
    public static bool operator ==(NodeHandle a, NodeHandle b) => a.Equals(b);
    public static bool operator !=(NodeHandle a, NodeHandle b) => !a.Equals(b);
    public override string ToString() => $"node{Raw}";
}

/// <summary>Typed wrapper for brushes interned in the BrushTable.</summary>
public readonly struct BrushHandle : IEquatable<BrushHandle>
{
    public readonly Handle Raw;
    public BrushHandle(Handle raw) => Raw = raw;
    public bool IsNull => Raw.IsNull;
    public static BrushHandle Null => default;
    public bool Equals(BrushHandle other) => Raw.Equals(other.Raw);
    public override bool Equals(object? obj) => obj is BrushHandle h && Equals(h);
    public override int GetHashCode() => Raw.GetHashCode();
    public override string ToString() => $"brush{Raw}";
}
