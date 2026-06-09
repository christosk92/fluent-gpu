using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>Computed template settings for the Expander (the typed-record convention — see <see cref="Tween"/>): the
/// chevron rotation and whether the content panel participates, derived once from the open state. Mirrors the geometry
/// WinUI's generated <c>ExpanderTemplateSettings</c> binds into its chevron storyboard. Richer fields (content reveal
/// height for a clip-channel reveal) follow the same shape.</summary>
public readonly record struct ExpanderTemplateSettings(float ChevronRotationDeg, bool ContentVisible)
{
    public static ExpanderTemplateSettings For(bool open) => new(open ? 180f : 0f, open);
}

/// <summary>
/// A WinUI Expander: a clickable header row with a trailing chevron over a collapsible content panel. The header toggles
/// the open state (local <see cref="Component"/> state); the content panel is only rendered while expanded. The single
/// chevron glyph is ROTATED by the computed <see cref="ExpanderTemplateSettings"/> (down when collapsed → up when
/// expanded). The whole control is a bordered, clipped card.
/// </summary>
public sealed class Expander : Component
{
    public string Header = "";
    public Element Content = new BoxEl { };
    public bool InitiallyExpanded = false;

    public static Element Create(string header, Element content, bool initiallyExpanded = false)
        => Embed.Comp(() => new Expander { Header = header, Content = content, InitiallyExpanded = initiallyExpanded });

    // WinUI Expander motion (Expander.xaml / _perf2026 storyboards): the content reveal is ASYMMETRIC —
    // expand = 333ms with KeySpline 0,0,0,1 (FluentPopOpen); collapse = 167ms with KeySpline 1,1,0,1 (FluentAccelerate).
    // The chevron rotate is the ControlFast 167ms.
    const float ChevronMs = 167f;
    const float ExpandMs = 333f;
    const float CollapseMs = 167f;

    public override Element Render()
    {
        var (open, setOpen) = UseState(InitiallyExpanded);
        var settings = ExpanderTemplateSettings.For(open);   // typed computed settings drive the chevron + content reveal
        var chevronRef = UseRef<NodeHandle>(default);
        var mounted = UseRef(false);

        // Animate the chevron rotation toward the computed setting whenever the open state flips (down 0° ↔ up 180°).
        // The AnimEngine owns the chevron LocalTransform (no static Rotation); the recorder pivots it about the centre.
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || chevronRef.Value.IsNull || !scene.IsLive(chevronRef.Value)) return;
            float to = settings.ChevronRotationDeg;
            if (!mounted.Value)
            {
                mounted.Value = true;
                anim.Animate(chevronRef.Value, AnimChannel.Rotation, to, to, 1f, Easing.Linear);   // seed the resting angle, no visible motion
                return;
            }
            anim.Animate(chevronRef.Value, AnimChannel.Rotation, open ? 0f : 180f, to, ChevronMs, Easing.FluentStandard);
        }, open);

        // Trailing 32x32 rounded chevron button: only this gets the subtle hover/press, not the whole header.
        var chevron = new BoxEl
        {
            Width = 32f,                                          // ExpanderChevronButtonSize = 32
            Height = 32f,
            Margin = new Edges4(20, 0, 8, 0),                     // ExpanderChevronMargin = 20,0,8,0
            Corners = Radii.ControlAll,                           // ControlCornerRadius = 4
            HoverFill = Tok.FillSubtleSecondary,                  // ExpanderChevronPointerOverBackground
            PressedFill = Tok.FillSubtleTertiary,                 // ExpanderChevronPressedBackground (was missing)
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            OnRealized = h => chevronRef.Value = h,               // capture for the rotation tween (AnimEngine-owned LocalTransform)
            Children =
            [
                // ExpanderChevronGlyphSize = 12. ExpanderChevronForeground = TextFillColorPrimaryBrush. One glyph, rotated.
                new TextEl(Icons.ChevronDown) { Size = 12f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont },
            ],
        };

        var header = new BoxEl
        {
            Direction = 0,
            MinHeight = 48f,
            AlignItems = FlexAlign.Center,
            // Chevron handles the right inset via its own margin.
            Padding = new Edges4(16, 0, 0, 0),
            // Header background does not change on hover — stays CardBackgroundFillColorDefault at rest and hover.
            Fill = Tok.FillCardDefault,
            // WinUI Expander header (ToggleButton) carries a 1px CardStrokeColorDefault border (ExpanderHeaderBorderBrush).
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            OnClick = () => setOpen(!open),
            Role = AutomationRole.Expander,
            Children =
            [
                new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Grow = 1 },
                chevron,
            ],
        };

        var content = new BoxEl
        {
            Direction = 1,                       // vertical content area: stretch the child to full width so wrapping text reserves its true height
            Padding = Edges4.All(16),
            Fill = Tok.FillCardSecondary,
            // Reveal in on expand / out (orphaned) on collapse: a height clip-reveal + fade, clipped by the card. Asymmetric
            // WinUI timing: expand 333ms (0,0,0,1), collapse 167ms (1,1,0,1) via the new LayoutTransition.ExitDynamics.
            Animate = new LayoutTransition(
                TransitionChannels.Size | TransitionChannels.Opacity,
                TransitionDynamics.Tween(ExpandMs, Easing.FluentPopOpen),
                SizeMode.Reveal,
                Enter: new EnterExit(Opacity: 0f, Active: true),
                Exit: new EnterExit(Opacity: 0f, Active: true),
                ExitDynamics: TransitionDynamics.Tween(CollapseMs, Easing.FluentAccelerate)),
            Children = [Content],
        };

        return new BoxEl
        {
            Direction = 1,
            Corners = Radii.OverlayAll,
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            ClipToBounds = true,
            // The card's own height animates as content is added/removed, so siblings below reflow smoothly (live relayout).
            // The size-change path uses Dynamics (expand-matched 333ms / 0,0,0,1); the content reveal above carries the asymmetry.
            Animate = LayoutTransition.BoundsT(SizeMode.Relayout) with { Dynamics = TransitionDynamics.Tween(ExpandMs, Easing.FluentPopOpen) },
            Children = settings.ContentVisible ? new Element[] { header, content } : new Element[] { header },
        };
    }
}
