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
    Disabled = 1u << 11,          // input-disabled: the node does not hit-test/focus/key-activate/repeat/drag/click (visuals stay control-chosen)
    WantsPointer = 1u << 12,
    Focusable = 1u << 13,
    Focused = 1u << 14,
    Hovered = 1u << 15,
    Pressed = 1u << 16,
    FocusVisual = 1u << 22,   // focus arrived via keyboard (Tab/arrows) → draw the focus ring; pointer focus does NOT set it

    // scroll / virtualization
    // (The virtualization spec names VirtualRangeDirty=1<<13 / StickyPinned=1<<14, but those bits are already
    //  taken by Focusable/Focused in this map — see architecture-spec §2 vs the live NodeFlags column. We honor
    //  the *semantics* (distinct bits, NOT the Realized bit) at free positions in the live map.)
    Scrollable = 1u << 17,        // node is a scroll viewport (carries a ScrollState row; Input may scroll it)
    VirtualRangeDirty = 1u << 18, // virtual list crossed an item boundary → re-realize the window next render
    StickyPinned = 1u << 19,      // a sticky header pinned by a phase-7 transform (excluded from clean-span reuse)
    ZStack = 1u << 20,            // z-stack container: children overlay at the origin, painted in order (last on top)
    BoundsAnimated = 1u << 21,    // carries a LayoutTransition: capture presented rect, diff vs target, project the residual

    // layout-transition presentation / lifecycle
    CounterScaled = 1u << 23,     // recorder post-applies the inverse of the nearest BoundsAnimated ancestor's animated scale
    Exiting = 1u << 24,           // removed node kept alive (orphan) until its exit animation settles, then reclaimed
    Relayouting = 1u << 25,       // SizeMode.Relayout size animation in flight → AppHost re-solves this subtree each tick

    // lifecycle
    NewThisFrame = 1u << 29,
    Detached = 1u << 28,
}
