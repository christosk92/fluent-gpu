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

    // input
    DragYieldsToPan = 1u << 3,     // an OnDrag node that is a CROSS-AXIS content-pan (SwipeControl/FlipView): instead of
                                   // eagerly capturing the contact on press (Slider/EditableText do), it enrolls an
                                   // axis-locked Drag arena member that COMPETES with an enclosing scroller's Pan — it
                                   // wins when the gesture runs along its own axis and yields (the list scrolls) when
                                   // cross-axis (input-a11y.md §7A; the declarative form of DragController.YieldsToPan)

    // layout firewall (opt-in)
    LayoutBoundary = 1u << 7,         // the app declares this node's size PARENT-determined (it fills/clips, never content-
                                      // sized): a descendant relayout stops here (LayoutInvalidator) and re-solves only this
                                      // subtree via RunSubtree against the node's current bounds, instead of a full-tree
                                      // layout from the root. Set from Element.IsolateLayout. A resize still does a full layout.

    // layout notification
    BoundsChangedPending = 1u << 4,   // a NEW OnBoundsChanged handler was just installed on this node — deliver the
                                      // node's current arranged rect to it ONCE on the next arrange even if the rect is
                                      // unchanged (the handler is edge-triggered on subsequent deltas, but an
                                      // unconstrained node whose Arrange size == its silently-Measured size would
                                      // otherwise never get its initial value). Set in SetBoundsChangedHandler, cleared
                                      // by SetArrangedBounds after the one-shot invoke.

    // state
    Visible = 1u << 8,
    HitTestVisible = 1u << 9,
    ClipsToBounds = 1u << 10,
    Disabled = 1u << 11,          // input-disabled: the node does not hit-test/focus/key-activate/repeat/drag/click (visuals stay control-chosen)
    WantsPointer = 1u << 12,
    Focusable = 1u << 13,
    Focused = 1u << 14,
    Hovered = 1u << 15,
    HoverWithin = 1u << 5,        // a CONTAINER whose subtree holds the hovered leaf — set on the hovered node's
                                  // interactive STRICT-ancestor chain by the input dispatcher, so a row/group reads as
                                  // hovered while the pointer crosses its interactive children (links / buttons): the
                                  // recorder keeps its HoverFill + any descendant hover-reveal (inherited progress) lit,
                                  // instead of flickering off whenever the hovered leaf is an interactive child.
    Pressed = 1u << 16,
    FocusVisual = 1u << 22,   // focus arrived via keyboard (Tab/arrows) → draw the focus ring; pointer focus does NOT set it

    // scroll / virtualization
    // (The virtualization spec names VirtualRangeDirty=1<<13 / StickyPinned=1<<14, but those bits are already
    //  taken by Focusable/Focused in this map — see architecture-spec §2 vs the live NodeFlags column. We honor
    //  the *semantics* (distinct bits, NOT the Realized bit) at free positions in the live map.)
    // 1u << 6 is FREE — formerly ScrollStretchHeader; overscroll-stretch is now a generic ScrollBind closed-form op
    // (the bind targets the hero node by handle, so no per-node flag is needed).
    Scrollable = 1u << 17,        // node is a scroll viewport (carries a ScrollState row; Input may scroll it)
    VirtualRangeDirty = 1u << 18, // virtual list crossed an item boundary → re-realize the window next render
    StickyPinned = 1u << 19,      // a sticky header pinned by a phase-7 transform (excluded from clean-span reuse)
    ZStack = 1u << 20,            // z-stack container: children overlay at the origin, painted in order (last on top)
    BoundsAnimated = 1u << 21,    // carries a LayoutTransition: capture presented rect, diff vs target, project the residual

    // layout-transition presentation / lifecycle
    CounterScaled = 1u << 23,     // recorder post-applies the inverse of the nearest BoundsAnimated ancestor's animated scale
    Exiting = 1u << 24,           // removed node kept alive (orphan) until its exit animation settles, then reclaimed
    Relayouting = 1u << 25,       // SizeMode.Relayout size animation in flight → AppHost re-solves this subtree each tick
    DragGhost = 1u << 26,         // lifted drag visual (E5): excluded from the clipped main record pass and re-walked
                                  // in an UNCLIPPED top band emitted last (escapes ancestor scissors, paints above
                                  // overlays); set/cleared by Input.DragController, published as SceneStore.DragGhost
    ConnectedOverlay = 1u << 30,  // flying shared-element (Hero) visual: a connected-animation overlay node, excluded
                                  // from the clipped main + orphan passes and re-walked in an UNCLIPPED top band ABOVE
                                  // the drag ghost, so a card art flying into a clipped rail is never cut off. Set/
                                  // cleared by FluentGpu.Animation.ConnectedAnimation, tracked in SceneStore overlays.

    // lifecycle
    NewThisFrame = 1u << 29,
    Detached = 1u << 28,
    Parked = 1u << 27,            // node belongs to a KeepAlive-parked (backgrounded, detached) subtree: the animation /
                                  // scroll tickers skip it + exclude it from HasActive (idle wake-stop), and a component
                                  // mounted under it seeds INACTIVE. Set/cleared by Reconciler.SetSubtreeParked.
}
