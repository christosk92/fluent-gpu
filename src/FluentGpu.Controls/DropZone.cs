using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A styled drop target that wraps <see cref="Content"/> and ANNOUNCES droppability by restyling the zone itself — no
/// second labelled panel (so the content's own text never doubles up). Three states, cross-faded:
/// <list type="bullet">
/// <item>rest — just the content;</item>
/// <item>a COMPATIBLE drag is live anywhere (<c>UseDragState</c>) — a soft dashed accent ring + faint accent glow fade
/// in ("you can drop here");</item>
/// <item>hovering THIS zone (<c>OnEnter</c>/<c>OnLeave</c>) — the ring brightens and the glow intensifies.</item>
/// </list>
/// Works for BOTH in-app drags (a <c>BoxEl.Draggable</c> of a matching kind) AND OS file drags (the backend's
/// IDropTarget feeds the same <c>DragDropContext</c>). The ring/glow cross-fade via the engine's BrushTransition; the
/// border width is constant across states so the layout never jumps. Props are mount-time (the component is autonomous).
/// </summary>
public sealed class DropZone : Component
{
    /// <summary>The drag kinds this zone accepts (e.g. <c>DropKinds.Files</c>, or an in-app kind discriminator).</summary>
    public string[] Accept = [];
    /// <summary>Fired on a drop over the zone — read the payload from <c>session.Payload</c> (a <c>FileDropData</c> for
    /// OS files; the source's typed payload for an in-app drag).</summary>
    public Action<DragSession>? OnDrop;
    /// <summary>The content rendered inside the zone (it carries its own label/icon — the DropZone adds only the
    /// reactive ring/glow around it).</summary>
    public Element Content = new BoxEl();

    // ── affordance styling ──
    private ColorF? _accent;
    /// <summary>Explicit affordance color, or the live accent token when left unset.</summary>
    public ColorF Accent { get => _accent ?? Tok.AccentDefault; set => _accent = value; }
    public float DashOn = 6f, DashOff = 6f, Corner = 8f, BorderWidth = 2f;
    /// <summary>Span the parent (Grow = 1) — set by <see cref="Window"/> for a whole-window zone.</summary>
    public bool Fill;
    /// <summary>Seed the hover state (for an always-on cue or a static screenshot of the hover look).</summary>
    public bool InitiallyOver;

    // FGRP001: Content is a deliberate mount-time slot for these convenience factories — a drop zone wraps STATIC
    // content built once by the caller. A caller with per-render-changing content should key/remount the zone.
#pragma warning disable FGRP001
    /// <summary>A sized drop zone wrapping <paramref name="content"/>.</summary>
    public static Element Create(string[] accept, Action<DragSession> onDrop, Element content)
        => Embed.Comp(() => new DropZone { Accept = accept, OnDrop = onDrop, Content = content });

    /// <summary>A whole-window "drop anywhere" zone (spans the parent).</summary>
    public static Element Window(string[] accept, Action<DragSession> onDrop, Element content)
        => Embed.Comp(() => new DropZone { Accept = accept, OnDrop = onDrop, Content = content, Fill = true });
#pragma warning restore FGRP001

    public override Element Render()
    {
        var over = UseSignal(InitiallyOver);
        var state = UseDragState();   // re-renders this zone while a drag is live (begin/move/end)

        // Build the drop-target spec once (stable closures) — avoids per-frame alloc as UseDragState re-renders.
        var specRef = UseRef<DropTargetSpec?>(null);
        specRef.Value ??= new DropTargetSpec(
            Accept,
            OnEnter: _ => over.Value = true,
            OnLeave: _ => over.Value = false,
            OnDrop: s => { over.Value = false; OnDrop?.Invoke(s); });

        bool compatible = state.Active && Match(state.Kind);
        bool hovering = over.Value;
        bool cue = compatible || hovering;

        // Ring: a subtle resting frame, a soft accent when a compatible drag is live, full accent on hover (cross-faded).
        ColorF ring = hovering ? Accent : compatible ? Accent with { A = 0.55f } : Tok.StrokeCardDefault;
        // Glow: an accent-colored, zero-offset soft shadow — a halo that grows on hover.
        ShadowSpec? glow = cue
            ? new ShadowSpec(Blur: hovering ? 28f : 16f, OffsetY: 0f, OffsetX: 0f, Color: Accent with { A = hovering ? 0.40f : 0.18f })
            : null;

        return new BoxEl
        {
            ZStack = true,
            Grow = Fill ? 1f : 0f,
            DropTarget = specRef.Value,
            Corners = CornerRadius4.All(Corner),
            BorderColor = ring,
            BorderWidth = BorderWidth,                 // constant → no layout jump between states
            BorderDashOn = cue ? DashOn : 0f,
            BorderDashOff = DashOff,
            BrushTransitionMs = 160f,                  // cross-fade the ring color as states change (soft + beautiful)
            Shadow = glow,
            Children = [Content],
        };
    }

    private bool Match(string kind) => Array.IndexOf(Accept, kind) >= 0;
}
