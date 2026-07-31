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
    float Indent,
    float Top);

readonly record struct SidebarPillRegistration(NodeHandle Node);

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

    readonly SidebarPane _owner;
    readonly Func<SidebarPillState> _state;
    NodeHandle _self;
    string? _route;

    public SidebarSelectionPill(SidebarPane owner, Func<SidebarPillState> state)
    {
        _owner = owner;
        _state = state;
    }

    public override Element Render()
    {
        var state = _state();
        bool selected = state.Selected;
        bool recycled = _route is not null && !string.Equals(_route, state.Route, StringComparison.Ordinal);
        int routeHash = state.Route is null ? 0 : StringComparer.Ordinal.GetHashCode(state.Route);
        UseLayoutEffect(() =>
        {
            _route = state.Route;
            var anim = Context.Anim;
            if (anim is null || _self.IsNull || state.Route is not { Length: > 0 } route) return;
            _owner.RegisterSelectionPill(route, _self);
            // A recycled node may inherit an interrupted transform from the route previously bound to the slot. The pane
            // owns real navigation transactions; recycle is never navigation and therefore always snaps.
            if (recycled) NavigationSelectionMotion.SnapVertical(anim, _self, selected);
        }, DepKey.From(routeHash));

        return new BoxEl
        {
            Width = PillW,
            Height = PillH,
            Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
            Corners = CornerRadius4.All(PillW * 0.5f),
            Fill = Tok.AccentDefault,
            Opacity = selected ? 1f : 0f,
            // NavigationSelectionMotion expresses WinUI's animated CenterPoint swap as painted-top coordinates.
            // A top-edge origin is therefore part of the primitive's geometry contract, not a visual preference.
            TransformOriginY = 0f,
            HitTestVisible = false,
            OnRealized = h => _self = h,
        };
    }
}
