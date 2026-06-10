using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

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
/// the open state (local <see cref="Component"/> state); the single chevron glyph is ROTATED by the computed
/// <see cref="ExpanderTemplateSettings"/> (down when collapsed → up when expanded). The open/close motion is the WinUI
/// ExpandDown/CollapseUp storyboard pair, exactly: the card height SNAPS (no height tween — WinUI never animates it),
/// and the content panel SLIDES vertically under the header behind a clip wrapper (the template's ExpanderContentClip,
/// Expander.xaml:112-113). There is NO opacity animation anywhere in the WinUI storyboards.
/// </summary>
public sealed class Expander : Component
{
    public string Header = "";
    public Element Content = new BoxEl { };
    public bool InitiallyExpanded = false;

    public static Element Create(string header, Element content, bool initiallyExpanded = false)
        => Embed.Comp(() => new Expander { Header = header, Content = content, InitiallyExpanded = initiallyExpanded });

    // WinUI Expander motion (Expander.xaml, ExpandDown ~62-77 / CollapseUp ~78-90):
    //   expand   = content Visibility=Visible at t=0 (the card is already full height — it snaps), then TranslateY
    //              runs a discrete keyframe at t=0 to NegativeContentHeight and a spline to 0 at 0:0:0.333 with
    //              KeySpline 0,0,0,1 (Expander.xaml:68-74). KeySpline 0,0,0,1 == Easing.FluentPopOpen (Easing.cs:75).
    //   collapse = TranslateY 0 → NegativeContentHeight over 0:0:0.167 with KeySpline 1,1,0,1 (Expander.xaml:84-87);
    //              the content stays Visible until t=0:0:0.167 (Expander.xaml:81-83) — so it stays MOUNTED for the
    //              slide and is unmounted at settle by the watcher below.
    //   chevron  = the AnimatedChevronUpDownSmall rotate keyframes span 10/260 of the 4333.33ms composition ≈ 167ms
    //              with cubic-bezier(0.167, 0.167, 0, 1) (AnimatedChevronUpDownSmallVisualSource.cpp:104,352,438-440).
    const float ChevronMs = 167f;
    const float ExpandMs = 333f;
    const float CollapseMs = 167f;

    public override Element Render()
    {
        var (open, setOpen) = UseState(InitiallyExpanded);
        // The content's MOUNT state lags `open` on collapse: WinUI keyframes Visibility=Collapsed at t=167ms
        // (Expander.xaml:81-83), so the panel stays mounted while it slides up and unmounts when the slide settles.
        var shown = UseSignal(InitiallyExpanded);
        var settings = ExpanderTemplateSettings.For(open);   // typed computed settings drive the chevron + content slide
        var chevronRef = UseRef<NodeHandle>(default);
        var contentRef = UseRef<NodeHandle>(default);
        var chevronSeeded = UseRef(false);
        var slideArmed = UseRef(false);

        bool showContent = shown.Value;          // subscribe: the settle watcher's write re-renders this component
        bool closing = showContent && !open;     // mid collapse-slide: content mounted, slide running, watcher armed

        // Animate the chevron rotation toward the computed setting whenever the open state flips (down 0° ↔ up 180°).
        // The AnimEngine owns the chevron LocalTransform (no static Rotation); the recorder pivots it about the centre.
        // 167ms with the Lottie rotate spline cubic-bezier(0.167, 0.167, 0, 1) (AnimatedChevronUpDownSmallVisualSource.cpp:352).
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || chevronRef.Value.IsNull || !scene.IsLive(chevronRef.Value)) return;
            float to = settings.ChevronRotationDeg;
            if (!chevronSeeded.Value)
            {
                chevronSeeded.Value = true;
                anim.Animate(chevronRef.Value, AnimChannel.Rotation, to, to, 1f, Easing.Linear);   // seed the resting angle, no visible motion
                return;
            }
            anim.Animate(chevronRef.Value, AnimChannel.Rotation, open ? 0f : 180f, to, ChevronMs,
                EasingSpec.CubicBezier(0.167f, 0.167f, 0f, 1f));
        }, open);

        // The content slide (the ExpandDown/CollapseUp TranslateY storyboards). A LAYOUT effect: runs in phase 6.5,
        // AFTER layout solved the just-mounted panel (Bounds valid = WinUI TemplateSettings.ContentHeight) and BEFORE
        // this frame's anim tick — so the first painted frame of an expand already sits at −ContentHeight (WinUI's
        // discrete keyframe at t=0, Expander.xaml:72). The initial mount seeds nothing: the panel rests at
        // TranslateY=0 (identity), so initiallyExpanded mounts with no motion.
        UseLayoutEffect(() =>
        {
            if (!slideArmed.Value) { slideArmed.Value = true; return; }   // first mount = rest, never motion
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = contentRef.Value;
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node)) return;
            float h = scene.Bounds(node).H;                               // ContentHeight, read after layout
            if (h <= 0f) return;
            if (open)
                anim.Animate(node, AnimChannel.TranslateY, -h, 0f, ExpandMs, Easing.FluentPopOpen);   // KeySpline 0,0,0,1 (Expander.xaml:73)
            else
                anim.Animate(node, AnimChannel.TranslateY, 0f, -h, CollapseMs,
                    EasingSpec.CubicBezier(1f, 1f, 0f, 1f));              // KeySpline 1,1,0,1 (Expander.xaml:86), exact
        }, open);

        // Trailing 32x32 rounded chevron button: only this gets the subtle hover/press, not the whole header.
        var chevron = new BoxEl
        {
            Width = 32f,                                          // ExpanderChevronButtonSize = 32
            Height = 32f,
            Margin = new Edges4(20, 0, 8, 0),                     // ExpanderChevronMargin = 20,0,8,0
            Corners = Radii.ControlAll,                           // ControlCornerRadius = 4
            HoverFill = Tok.FillSubtleSecondary,                  // ExpanderChevronPointerOverBackground
            PressedFill = Tok.FillSubtleTertiary,                 // ExpanderChevronPressedBackground
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
            MinHeight = 48f,                                      // ExpanderMinHeight = 48
            AlignItems = FlexAlign.Center,
            // Chevron handles the right inset via its own margin.
            Padding = new Edges4(16, 0, 0, 0),                    // ExpanderHeaderPadding = 16,0,0,0
            // Header background does not change on hover — stays CardBackgroundFillColorDefault at rest and hover.
            Fill = Tok.FillCardDefault,
            // WinUI Expander header (ToggleButton) carries a 1px CardStrokeColorDefault border (ExpanderHeaderBorderThickness = 1).
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            // Open-state corner filtering (Expander.xaml:64, ExpandDown VisualState setter): expanded ⇒
            // TopCornerRadiusFilterConverter keeps only the TOP corners; collapsed ⇒ the full ControlCornerRadius
            // (Expander.xaml:26 + CornerRadius_themeresources.xaml:5 = 4).
            Corners = open ? new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f) : Radii.ControlAll,
            OnClick = () => { bool next = !open; setOpen(next); if (next) shown.Value = true; },
            Role = AutomationRole.Expander,
            Children =
            [
                new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Grow = 1 },
                chevron,
            ],
        };

        // ExpanderContent (Expander.xaml:114): the panel that SLIDES. Its TranslateY is AnimEngine-owned.
        var content = new BoxEl
        {
            Direction = 1,                       // vertical content area: stretch the child to full width so wrapping text reserves its true height
            Padding = Edges4.All(16),            // ExpanderContentPadding = 16
            MinHeight = 48f,                     // ExpanderContent MinHeight = TemplateBinding MinHeight (ExpanderMinHeight = 48)
            Fill = Tok.FillCardSecondary,        // ExpanderContentBackground = CardBackgroundFillColorSecondaryBrush
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault, // ExpanderContentBorderBrush = CardStrokeColorDefaultBrush
            // ExpanderContentDownBorderThickness = 1,0,1,1 (NO top edge — the header's own bottom border is the
            // divider). The engine border is uniform, so the panel rises 1px under the clip wrapper and the wrapper
            // crops exactly the top border row (the AutoSuggestBox −1 border-overlap idiom).
            Margin = new Edges4(0, -1f, 0, 0),
            Corners = new CornerRadius4(0f, 0f, Radii.Control, Radii.Control),   // BottomCornerRadiusFilterConverter (Expander.xaml:114)
            OnRealized = h => contentRef.Value = h,   // capture for the TranslateY slide (read ContentHeight after layout)
            Children = [Content],
        };

        // ExpanderContentClip (Expander.xaml:112-113, "The clip is a composition clip applied in code"): a plain
        // rectangular clip wrapper — the sliding panel is clipped at the header seam, never painting over the header.
        // Column so the panel stretches to the card width (cross-axis stretch), like the template's Grid row.
        var contentClip = new BoxEl { Direction = 1, ClipToBounds = true, Children = [content] };

        // The card root mirrors the template's root Grid: pure layout, NO fill/border/clip and NO layout transition —
        // its height comes straight from header (+ content), so it SNAPS open/closed exactly like WinUI.
        Element[] children = closing
            ? new Element[] { header, contentClip, Embed.Comp(() => new ExpanderCollapseWatcher { Content = () => contentRef.Value, Shown = shown }) }
            : showContent ? new Element[] { header, contentClip } : new Element[] { header };

        return new BoxEl
        {
            Direction = 1,
            Children = children,
        };
    }
}

/// <summary>Per-frame poller (the PasswordBox PeekReleaseWatcher idiom), mounted only WHILE the collapse slide runs:
/// the moment the content's TranslateY track settles (the AnimEngine reclaims it), flip the mount signal off — the
/// WinUI CollapseUp storyboard's Visibility=Collapsed keyframe at t=167ms (Expander.xaml:81-83).</summary>
internal sealed class ExpanderCollapseWatcher : Component
{
    public required Func<NodeHandle> Content;
    public required Signal<bool> Shown;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (only during the 167ms slide)
        UseEffect(() =>
        {
            if (!Shown.Peek()) return;
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = Content();
            // Settled (the eased TranslateY track completed and was reclaimed) — or the node vanished: unmount now.
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node) || !anim.HasTracks(node))
                Shown.Value = false;
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}
