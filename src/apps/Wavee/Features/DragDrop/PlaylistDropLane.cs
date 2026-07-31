using System;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>A virtual-list playlist destination with a recycling-safe live gap. The row model remains at rest during the
/// gesture; ItemsView placement displaces the suffix while this lane paints a capped real-track preview in the gap.</summary>
sealed class PlaylistDropLane
{
    const int PreviewRowCap = 3;

    readonly ItemsViewController _controller;
    readonly VirtualInsertionPreviewController _projection;
    readonly Signal<int> _slot = new(-1);
    readonly Signal<float> _lineY = new(0f);
    readonly Signal<float> _previewY = new(0f);
    readonly Signal<int> _previewVersion = new(0);
    readonly DropTargetSpec _target;
    SceneStore? _scene;
    int _count;
    int _firstItemIndex;
    float _itemExtent;
    float _leadingExtent;
    string _targetUri = "";
    bool _allowSameListMove;
    WaveeResourceDragPayload? _previewPayload;
    Func<object?, int, Task>? _commit;
    Action<Action>? _post;
    object? _membershipToken;
    object? _commitBaseline;
    int _commitEpoch;
    bool _awaitingMembership;

    public PlaylistDropLane(ItemsViewController controller, Signal<int> placementVersion)
    {
        _controller = controller;
        _projection = new VirtualInsertionPreviewController(placementVersion);
        _target = new DropTargetSpec([WaveeDragKinds.Resource], Enter, Over, Leave, Drop)
        {
            CanAccept = s => CanAccept(s.Payload),
            VisualPolicy = DropTargetVisualPolicy.Spotlight,
        };
    }

    public void Configure(SceneStore scene, int itemCount, float itemExtent, float leadingExtent,
                          int firstItemIndex, string targetUri, bool allowSameListMove,
                          Func<object?, int, Task> commit, Action<Action> post)
    {
        _scene = scene;
        _count = Math.Max(0, itemCount);
        _itemExtent = itemExtent;
        _leadingExtent = leadingExtent;
        _firstItemIndex = Math.Max(0, firstItemIndex);
        _targetUri = targetUri;
        _allowSameListMove = allowSameListMove;
        _commit = commit;
        _post = post;
    }

    /// <summary>Called from the owner's layout effect. A new optimistic membership snapshot is the handoff edge: the real
    /// list has accepted the mutation, so the temporary gap can close into its FLIP without a blank intermediate frame.</summary>
    public void ObserveMembership(object membershipToken)
    {
        _membershipToken = membershipToken;
        if (!_awaitingMembership || ReferenceEquals(_commitBaseline, membershipToken)) return;
        _awaitingMembership = false;
        ClearPreview();
    }

    public (float dx, float dy) Displacement(int itemIndex) => _projection.DisplacementFor(itemIndex);

    public Element Wrap(Element body) => new BoxEl
    {
        ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        DropTarget = _target,
        Children =
        [
            body,
            Embed.Comp(() => new PlaylistInsertionPreview(this)),
            new BoxEl
            {
                Key = "playlist-drop-line",
                Width = float.NaN, Height = Spacing.XXS, Fill = Tok.AccentDefault, HitTestVisible = false,
                Opacity = Prop.Of(() => _slot.Value >= 0 ? 1f : 0f),
                Transform = Prop.Of(LineTransform),
                Transition = MotionTok.ControlFaster,
            },
        ],
    };

    bool CanAccept(object? payload)
    {
        var resource = WaveeResourceDrag.Unwrap(payload);
        if (resource is not { CanCopyTracks: true }) return false;
        bool sameListMove = resource.SourceRows is { Count: > 0 }
            && string.Equals(resource.SourcePlaylistUri, _targetUri, StringComparison.Ordinal);
        return !sameListMove || _allowSameListMove;
    }

    void Enter(DragSession session) => Over(session);

    void Over(DragSession session)
    {
        if (!CanAccept(session.Payload) || WaveeResourceDrag.Unwrap(session.Payload) is not { } payload)
        {
            ClearPreview();
            return;
        }
        float extent = _itemExtent;
        var viewport = _controller.Viewport;
        if (extent <= 0f || _scene is not { } scene || viewport.IsNull || !scene.IsLive(viewport)) return;
        var rect = scene.AbsoluteRect(viewport);
        float offset = _controller.ScrollOffset;
        float contentY = session.Position.Y - rect.Y + offset - _leadingExtent;
        int slot = (int)MathF.Floor((contentY + extent * 0.5f) / extent);
        slot = Math.Clamp(slot, 0, _count);

        int total = payload.Tracks is { Count: > 0 } tracks ? tracks.Count : 1;
        int previewRows = Math.Min(PreviewRowCap, total);
        float previewExtent = previewRows * extent;
        bool projectionChanged = _projection.Update(slot, _firstItemIndex, previewExtent);
        bool payloadChanged = !ReferenceEquals(_previewPayload, payload);
        _previewPayload = payload;
        float previewY = _leadingExtent + slot * extent - offset;
        _lineY.Value = previewY - Spacing.XXS * 0.5f;
        _previewY.Value = previewY;
        _slot.Value = slot;
        session.Effect = payload.SourceRows is { Count: > 0 }
            && string.Equals(payload.SourcePlaylistUri, _targetUri, StringComparison.Ordinal)
                ? DropEffect.Move
                : DropEffect.Copy;
        if (projectionChanged || payloadChanged)
            _previewVersion.Value = _previewVersion.Peek() + 1;
    }

    void Leave(DragSession _)
    {
        if (!_awaitingMembership) ClearPreview();
    }

    void Drop(DragSession session)
    {
        int slot = _slot.Peek();
        if (slot < 0 || !CanAccept(session.Payload) || _commit is not { } commit)
        {
            ClearPreview();
            return;
        }

        _awaitingMembership = true;
        _commitBaseline = _membershipToken;
        int epoch = ++_commitEpoch;
        _ = CompleteAsync(commit, session.Payload, slot, epoch);
    }

    async Task CompleteAsync(Func<object?, int, Task> commit, object? payload, int slot, int epoch)
    {
        try { await commit(payload, slot).ConfigureAwait(false); }
        finally
        {
            _post?.Invoke(() =>
            {
                if (epoch != _commitEpoch || !_awaitingMembership) return;
                _awaitingMembership = false;
                ClearPreview();
            });
        }
    }

    void ClearPreview()
    {
        bool changed = _projection.Clear();
        if (_slot.Peek() >= 0) { _slot.Value = -1; changed = true; }
        if (_previewPayload is not null) { _previewPayload = null; changed = true; }
        if (changed) _previewVersion.Value = _previewVersion.Peek() + 1;
    }

    Affine2D LineTransform() => Affine2D.Translation(0f, _lineY.Value);

    sealed class PlaylistInsertionPreview : Component
    {
        readonly PlaylistDropLane _owner;
        public PlaylistInsertionPreview(PlaylistDropLane owner) => _owner = owner;

        public override Element Render()
        {
            _ = _owner._previewVersion.Value;
            int slot = _owner._slot.Peek();
            var payload = _owner._previewPayload;
            if (slot < 0 || payload is null) return new BoxEl { Height = 0f, HitTestVisible = false };

            float rowH = _owner._itemExtent;
            var tracks = payload.Tracks;
            int total = tracks is { Count: > 0 } ? tracks.Count : 1;
            int shown = Math.Min(PreviewRowCap, total);
            var rows = new Element[shown];
            for (int i = 0; i < shown; i++)
            {
                Track? track = tracks is { Count: > 0 } ? tracks[i] : null;
                int hidden = i == shown - 1 ? total - shown : 0;
                rows[i] = PreviewRow(track, payload.Name, rowH, hidden);
            }

            return new BoxEl
            {
                Key = "playlist-drop-preview",
                Direction = 1, Height = shown * rowH, Shrink = 0f,
                Transform = Prop.Of(() => Affine2D.Translation(0f, _owner._previewY.Value)),
                HitTestVisible = false, ClipToBounds = true,
                Children = rows,
            };
        }

        static Element PreviewRow(Track? track, string fallback, float height, int hidden)
        {
            string title = track?.Title is { Length: > 0 } name ? name : fallback;
            string subtitle = track?.Artists is { Count: > 0 } artists ? artists[0].Name : "";
            Element art = track is { } t
                ? Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff,
                    TrackRow.ThumbSize, TrackRow.ThumbSize, Radii.Control)
                : new BoxEl
                {
                    Width = TrackRow.ThumbSize, Height = TrackRow.ThumbSize,
                    Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                    Children = [Ui.Icon(Icons.MusicNote, 16f, Tok.TextSecondary)],
                };
            var children = new Element[hidden > 0 ? 3 : 2];
            children[0] = art;
            children[1] = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
                Children =
                [
                    new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
            if (hidden > 0)
                children[2] = new BoxEl
                {
                    Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
                    Corners = Radii.PillAll, Fill = Tok.AccentSubtle,
                    Children = [new TextEl("+" + hidden) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary }],
                };
            return new BoxEl
            {
                Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Margin = new Edges4(TrackRow.RowInset, 0f, TrackRow.RowInset, 0f),
                Padding = new Edges4(TrackRow.PadX - TrackRow.RowInset, 0f,
                    TrackRow.PadX - TrackRow.RowInset, 0f),
                Corners = Radii.ControlAll,
                Fill = Tok.FillSolidSecondary,
                BorderWidth = 1f, BorderColor = Tok.AccentDefault,
                Shadow = Elevation.Card, HitTestVisible = false,
                Children = children,
            };
        }
    }
}
