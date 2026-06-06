namespace FluentGpu.Foundation;

/// <summary>Per-node dirty + state bits (a single 32-bit SceneStore column). Three orthogonal dirty axes.</summary>
[Flags]
public enum NodeFlags : uint
{
    None = 0,

    // dirty axes
    LayoutDirty = 1u << 0,
    PaintDirty = 1u << 1,
    TransformDirty = 1u << 2,

    // state
    Visible = 1u << 8,
    HitTestVisible = 1u << 9,
    ClipsToBounds = 1u << 10,
    WantsPointer = 1u << 12,
    Focusable = 1u << 13,
    Focused = 1u << 14,

    // lifecycle
    NewThisFrame = 1u << 29,
    Detached = 1u << 28,
}
