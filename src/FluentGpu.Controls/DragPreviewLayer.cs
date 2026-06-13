using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// Renders a fully CUSTOM floating drag preview (a card / "+N" badge / chip) that follows the cursor during a drag —
/// the styleable "cursor tip". Mount it ONCE as the TOP child of the app's root stack (so it paints above everything);
/// it reads the live drag via <c>UseDragState()</c> and, while a drag is active, places the app's
/// <see cref="Preview"/> element at the pointer (window DIP) with hit-testing OFF (the preview never eats input).
///
/// <para><b>Scope.</b> This is the in-app companion to <see cref="DragVisualStyle"/>: where the style knobs tune the
/// LIFTED node's ghost, the preview layer draws an INDEPENDENT visual (set the draggable's
/// <c>DragSource.Style.Opacity = 0</c> to hide the lifted node and show only this). For OS file drags the OS owns the
/// drag image (Explorer's thumbnail), so prefer a <see cref="DropZone"/> hover overlay there rather than a preview.</para>
/// </summary>
public sealed class DragPreviewLayer : Component
{
    /// <summary>Map the live drag to a preview element (keyed by <c>state.Kind</c>/<c>state.Payload</c>), or null to
    /// show nothing for that drag. Set once at mount (a stable delegate); reactivity comes from the drag state.</summary>
    public Func<DragState, Element?>? Preview;

    /// <summary>Sugar: <c>DragPreviewLayer.Of(state =&gt; …)</c> → the embeddable element to drop at the app root.</summary>
    public static Element Of(Func<DragState, Element?> preview)
        => Embed.Comp(() => new DragPreviewLayer { Preview = preview });

    public override Element Render()
    {
        DragState state = UseDragState();
        Element? body = state.Active ? Preview?.Invoke(state) : null;

        // A non-clipping, input-transparent container that fills the root stack (so a child's composited OffsetX/Y is in
        // window-DIP space). The preview wrapper is offset to the cursor. When idle the container is empty (0 nodes).
        return new BoxEl
        {
            HitTestVisible = false,
            Children = body is null
                ? []
                : [new BoxEl { OffsetX = state.Position.X, OffsetY = state.Position.Y, HitTestVisible = false, Children = [body] }],
        };
    }
}
