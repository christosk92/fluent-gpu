using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>The live state for one item-owned NavigationView selection indicator.</summary>
readonly record struct SidebarPillState(
    string? Route,
    bool Selected,
    bool Departing,
    int Direction,
    int Epoch,
    float Indent,
    float Top,
    float Travel,
    bool SameDepth,
    bool CanAnimate);

/// <summary>
/// One NavigationViewItem-style SelectionIndicator. Every selectable realized row permanently owns one 3×16 node. On a
/// route edge the previous and next rows run the paired Microsoft NavigationView timeline; an unrealized peer, first
/// paint, geometry refresh, or recycle snaps. That ownership removes the global-overlay feedback loop that made the cue
/// flicker in the section-header band.
/// </summary>
sealed class SidebarSelectionPill : Component
{
    public const float PillH = 16f;
    public const float PillW = 3f;

    readonly Func<SidebarPillState> _state;
    NodeHandle _self;
    string? _route;
    int _epoch = -1;

    public SidebarSelectionPill(Func<SidebarPillState> state) => _state = state;

    public override Element Render()
    {
        var state = _state();
        bool selected = state.Selected;
        bool departing = !selected && state.Departing;
        bool involved = selected || departing;
        int previousEpoch = _epoch;
        bool first = previousEpoch < 0;
        bool recycled = !first && !string.Equals(_route, state.Route, StringComparison.Ordinal);

        float from = selected ? -state.Travel : 0f;
        float to = selected ? 0f : state.Travel;
        bool outgoing = departing;

        // Same-depth motion is expressed as painted top + scale about the top edge. On a depth/lane change WinUI does no
        // diagonal flight: each stationary cue scales from the edge facing its peer.
        float originY = 0f;
        if (!state.SameDepth && state.Direction != 0)
            originY = selected
                ? (state.Direction > 0 ? 0f : 1f)
                : (state.Direction > 0 ? 1f : 0f);

        int routeHash = state.Route is null ? 0 : StringComparer.Ordinal.GetHashCode(state.Route);
        int flags = (selected ? 1 : 0) | (departing ? 2 : 0)
                    | (state.SameDepth ? 4 : 0) | (state.CanAnimate ? 8 : 0);
        UseLayoutEffect(() =>
        {
            _epoch = state.Epoch;
            _route = state.Route;
            var anim = Context.Anim;
            if (anim is null || _self.IsNull) return;

            // WinUI keeps an in-flight animation when asked for the same target. A slot identity change is recycling,
            // not retargeting, and must instead clear every inherited channel before the new row can paint.
            if (!first && !recycled && previousEpoch == state.Epoch) return;
            if (first || recycled || !involved || !state.CanAnimate || state.Direction == 0)
            {
                NavigationSelectionMotion.SnapVertical(anim, _self, selected);
                return;
            }

            NavigationSelectionMotion.StartVertical(
                anim, _self, from, to, PillH, outgoing, state.SameDepth);
        }, DepKey.From(HashCode.Combine(state.Epoch, routeHash, flags,
                                        BitConverter.SingleToInt32Bits(state.Travel))));

        return new BoxEl
        {
            Width = PillW,
            Height = PillH,
            Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
            Corners = CornerRadius4.All(PillW * 0.5f),
            Fill = Tok.AccentDefault,
            Opacity = selected ? 1f : 0f,
            TransformOriginY = originY,
            HitTestVisible = false,
            OnRealized = h => _self = h,
        };
    }
}
