using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>A reusable virtual-list playlist destination. It translates a window-space pointer into an insertion slot,
/// exposes one accent insertion line, and delegates the mutation. The engine still owns drag promotion, nearest-target
/// routing and edge autoscroll; this class owns only Wavee's playlist semantics.</summary>
sealed class PlaylistDropLane
{
    readonly ItemsViewController _controller;
    readonly Signal<int> _slot = new(-1);
    readonly Signal<float> _lineY = new(0f);
    readonly DropTargetSpec _target;
    SceneStore? _scene;
    int _count;
    float _itemExtent;
    float _leadingExtent;
    Action<object?, int>? _commit;

    public PlaylistDropLane(ItemsViewController controller)
    {
        _controller = controller;
        _target = new DropTargetSpec([WaveeDragKinds.Resource], Enter, Over, Leave, Drop)
        {
            CanAccept = static s => WaveeResourceDrop.CanDepositTracks(s.Payload),
        };
    }

    public void Configure(SceneStore scene, int itemCount, float itemExtent,
                          float leadingExtent, Action<object?, int> commit)
    {
        _scene = scene;
        _count = Math.Max(0, itemCount);
        _itemExtent = itemExtent;
        _leadingExtent = leadingExtent;
        _commit = commit;
    }

    public Element Wrap(Element body) => new BoxEl
    {
        ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        DropTarget = _target,
        Children =
        [
            body,
            new BoxEl
            {
                Key = "playlist-drop-line",
                Width = float.NaN, Height = 2f, Fill = Tok.AccentDefault, HitTestVisible = false,
                Opacity = Prop.Of(() => _slot.Value >= 0 ? 1f : 0f),
                OffsetY = Prop.Of(LineOffset),
                Transition = MotionTok.ControlFaster,
            },
        ],
    };

    void Enter(DragSession session) => Over(session);

    void Over(DragSession session)
    {
        if (!WaveeResourceDrop.CanDepositTracks(session.Payload)) { _slot.Value = -1; return; }
        float extent = _itemExtent;
        var viewport = _controller.Viewport;
        if (extent <= 0f || _scene is not { } scene || viewport.IsNull || !scene.IsLive(viewport)) return;
        var rect = scene.AbsoluteRect(viewport);
        float leading = _leadingExtent;
        float offset = _controller.ScrollOffset;
        float contentY = session.Position.Y - rect.Y + offset - leading;
        int slot = (int)MathF.Floor((contentY + extent * 0.5f) / extent);
        slot = Math.Clamp(slot, 0, _count);
        _lineY.Value = leading + slot * extent - offset - 1f;
        _slot.Value = slot;
    }

    void Leave(DragSession _) => _slot.Value = -1;

    void Drop(DragSession session)
    {
        int slot = _slot.Peek();
        _slot.Value = -1;
        if (slot >= 0 && WaveeResourceDrop.CanDepositTracks(session.Payload))
            _commit?.Invoke(session.Payload, slot);
    }

    float LineOffset() => _lineY.Value;
}
