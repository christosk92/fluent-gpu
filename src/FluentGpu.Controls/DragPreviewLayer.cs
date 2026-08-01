using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Renders the floating drag preview (the compact "chip") that follows the cursor during a drag. Mount it ONCE as the
/// TOP child of the app's root stack; it registers its container as <see cref="SceneStore.DragOverlay"/>, so the
/// recorder draws the whole layer in the TOPMOST unclipped band — above the main pass, above the drag-ghost band and
/// above connected-animation overlays. A chip can therefore never be clipped by an ancestor scissor nor overdrawn by
/// later content (the clip/z-order failures the dnd-kit <c>DragOverlay</c> exists to solve).
///
/// <para><b>Compositor-only follow.</b> The preview does NOT re-render per pointer move. Its wrapper binds
/// <c>Transform</c> to the engine's live drag-position signals (<c>UseDragPosition</c>) and clamps the chip's measured
/// box to the window, so a move costs one composited transform write — no component render, no reconcile, no layout,
/// zero allocation. The drag EPOCH that drives <c>UseDragState</c> is edge-triggered to match (session begin/end,
/// target/effect/caption change), so <see cref="Preview"/> runs only when the chip's CONTENT could differ.</para>
///
/// <para><b>Scope.</b> This is the in-app companion to <see cref="DragVisualStyle"/>. Pair it with
/// <c>DragLift.Stationary</c> sources (<see cref="Drag.Source"/>'s default): the source row stays in its slot, dimmed,
/// and this layer draws the only moving visual. For OS file drags the OS owns the drag image (Explorer's thumbnail),
/// so prefer a <see cref="DropZone"/> hover overlay there rather than a preview.</para>
/// </summary>
public sealed class DragPreviewLayer : Component
{
    /// <summary>Chip offset from the pointer hotspot (the Atlassian/Apple convention: down-right of the cursor so the
    /// pointer keeps pointing at the drop location rather than at the card).</summary>
    public const float CursorOffsetX = 16f;
    public const float CursorOffsetY = 8f;

    NodeHandle _root;
    NodeHandle _settleNode;

    /// <summary>Map the live drag to a preview element (keyed by <c>state.Kind</c>/<c>state.Payload</c>), or null to
    /// show nothing for that drag. Set once at mount (a stable delegate); reactivity comes from the drag state.
    /// Prefer <c>DragChip.Resolve(...)</c> — apps supply chip DATA, not elements or positions.</summary>
    public Func<DragState, Element?>? Preview;

    /// <summary>Sugar: <c>DragPreviewLayer.Of(state =&gt; …)</c> → the embeddable element to drop at the app root.</summary>
    public static Element Of(Func<DragState, Element?> preview)
        => Embed.Comp(() => new DragPreviewLayer { Preview = preview });

    public override Element Render()
    {
        DragState state = UseDragState();
        var (posX, posY) = UseDragPosition();
        // The chip's MEASURED box — written from the wrapper's arranged bounds, read by the clamp inside the transform
        // bind. Signals (not state) so a size change re-runs the bind, never a render.
        FloatSignal chipW = UseFloatSignal(0f);
        FloatSignal chipH = UseFloatSignal(0f);
        SceneStore? scene = Context.Scene;   // captured at render: the bind thunk runs outside any render context
        Element? body = state.Active ? Preview?.Invoke(state) : null;

        // Register as the engine's drag-overlay band. That registration is the WHOLE story now: the band is emitted
        // above the drop-spotlight scrim, so the chip is lit by construction and needs no exemption from anything (the
        // presentation-only exempt registry the multiply/divide dim required is deleted).
        UseLayoutEffect(() =>
        {
            if (Context.Scene is not { } sc) return null;
            sc.DragOverlay = _root;
            var captured = _root;
            return () => { if (sc.DragOverlay == captured) sc.DragOverlay = NodeHandle.Null; };
        }, DepKey.Empty);

        // Drop settle (~250ms): the gesture is over but DragState stays Active across the window so the chip can glide
        // into the drop point (ToTarget) or back to the source row (Home) instead of vanishing — rbd's "nothing ever
        // teleports". Seeded on the settle EDGE onto the inner box, which owns the transform the follow wrapper doesn't.
        DragSettlePhase settle = state.Settle;
        float settleDx = state.SettleTarget.X - (state.Position.X + CursorOffsetX);
        float settleDy = state.SettleTarget.Y - (state.Position.Y + CursorOffsetY);
        UseLayoutEffect(() =>
        {
            if (settle == DragSettlePhase.None || _settleNode.IsNull || Context.Anim is not { } anim) return;
            anim.SeedValue(_settleNode, AnimChannel.TranslateX, settleDx, MotionTokenId.ItemPlacement, from: 0f);
            anim.SeedValue(_settleNode, AnimChannel.TranslateY, settleDy, MotionTokenId.ItemPlacement, from: 0f);
            anim.SeedValue(_settleNode, AnimChannel.Opacity, 0f, MotionTokenId.ItemPlacement, from: 1f);
        }, DepKey.From(settleDx, settleDy, (int)settle, 0));

        // A non-clipping, input-transparent container that fills the root stack (so a child's composited transform is in
        // window-DIP space). When idle the container is empty (0 nodes) but STAYS registered, so the band membership is
        // stable frame-to-frame and no ancestor's stored render span can go stale from it.
        return new BoxEl
        {
            HitTestVisible = false,
            OnRealized = h => _root = h,
            Children = body is null
                ? []
                : [new BoxEl
                {
                    // Pointer follow (bound = compositor-only) + window clamp. ONE transform owner: the settle
                    // animation lives on the child below, never here.
                    Transform = Prop.Of(() => FollowTransform(posX, posY, chipW, chipH, scene)),
                    OnBoundsChanged = r => { chipW.SetIfChanged(r.W); chipH.SetIfChanged(r.H); },
                    HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            OnRealized = h => _settleNode = h,
                            HitTestVisible = false,
                            Children = [body],
                        },
                    ],
                }],
        };
    }

    /// <summary>The chip's window-space placement: pointer + cursor offset, clamped so the measured chip box stays
    /// inside the scene root (the dnd-kit <c>restrictToWindowEdges</c> modifier — screenshot S4's clipped ghost).
    /// Pure + allocation-free: it runs inside a bound effect on every drag move.</summary>
    private static Affine2D FollowTransform(FloatSignal posX, FloatSignal posY, FloatSignal chipW, FloatSignal chipH,
                                            SceneStore? scene)
    {
        float x = posX.Value + CursorOffsetX;
        float y = posY.Value + CursorOffsetY;
        float w = chipW.Value, h = chipH.Value;
        if (scene is not null && !scene.Root.IsNull)
        {
            RectF r = scene.AbsoluteRect(scene.Root);
            if (r.W > 0.5f && r.H > 0.5f)
            {
                float maxX = r.X + r.W - w; if (maxX < r.X) maxX = r.X;   // chip wider than the window: pin its left edge
                float maxY = r.Y + r.H - h; if (maxY < r.Y) maxY = r.Y;
                x = x < r.X ? r.X : (x > maxX ? maxX : x);
                y = y < r.Y ? r.Y : (y > maxY ? maxY : y);
            }
        }
        return Affine2D.Translation(x, y);
    }
}
