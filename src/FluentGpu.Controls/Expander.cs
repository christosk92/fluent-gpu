using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Computed template settings for the Expander (the typed-record convention — see <see cref="Tween"/>): the
/// chevron rotation and whether the content panel participates, derived once from the open state. Mirrors the geometry
/// WinUI's generated <c>ExpanderTemplateSettings</c> binds into its chevron storyboard.</summary>
public readonly record struct ExpanderTemplateSettings(float ChevronRotationDeg, bool ContentVisible)
{
    public static ExpanderTemplateSettings For(bool open) => new(open ? 180f : 0f, open);
}

/// <summary>
/// A WinUI-flavoured Expander: a clickable header row with a trailing chevron over a collapsible content panel. The
/// header toggles local <see cref="Component"/> state (or a controlled <see cref="IsExpanded"/> signal); the single
/// chevron glyph is ROTATED by the computed <see cref="ExpanderTemplateSettings"/> (down collapsed → up expanded).
///
/// MOTION — a DELIBERATE divergence from WinUI: the card height EASES (WinUI's ExpandDown/CollapseUp storyboards SNAP
/// the layout space and only translate the content, Expander.xaml:39-90), because FluentGpu's design goal is smooth
/// transitions of content, not discrete layout repositions. The storyboard TIMINGS and EASINGS stay WinUI-exact:
/// expand 333ms KeySpline(0,0,0,1), collapse 167ms KeySpline(1,1,0,1). The whole open/close is one declarative
/// engine transition — <see cref="SizeMode.Reflow"/> on the content clip wrapper (the interpolated height runs
/// through real layout each tick, so neighbouring content reflows smoothly and RIGIDLY), with
/// <see cref="SizeAnchor.Trailing"/> riding the content's bottom edge on the reveal edge (the WinUI
/// slide-out-from-under-the-header look, rounded bottom corners visible mid-motion). No control-local ticker, no
/// per-frame re-render: the engine owns the motion.
///
/// CUSTOMIZATION goes through <see cref="Parts"/> (the one generic door — no per-feature knobs): every named template
/// part accepts arbitrary element props, e.g. a sticky pinned header
/// (<c>[PartHeader] = b => b with { ScrollBinds = [new(){ PinTop = 8f, OnFlag = … }], Fill = stuck.Value ? … : b.Fill }</c>) or an
/// edge-to-edge content panel (<c>[PartContent] = c => c with { Padding = Edges4.All(0) }</c>). Mechanics-critical
/// props are re-asserted after the modifier, so customization can restyle everything but break nothing.
/// </summary>
public sealed class Expander : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The returned card root (pure-layout column). Owned: Children.</summary>
    public const string PartRoot = "Root";
    /// <summary>The clickable header row (WinUI ExpanderHeader). Owned: OnClick (toggle), Role.</summary>
    public const string PartHeader = "Header";
    /// <summary>The trailing 32×32 chevron button (WinUI ExpanderChevron). Owned: OnRealized (rotation-tween ref,
    /// chained with any modifier-supplied handler).</summary>
    public const string PartChevron = "Chevron";
    /// <summary>The always-mounted reveal wrapper (WinUI ExpanderContentClip) — the SizeMode.Reflow host. Owned:
    /// ClipToBounds, Height (the open/closed toggle), Animate (the reflow spec), Children, OnRealized (chained).
    /// NOTE: also transform-owned mid-motion (Trailing anchor) — do not add a transform-owning ScrollBinds entry (a PinTop / StretchFromTop bind) or a bound Transform here.</summary>
    public const string PartClip = "Clip";
    /// <summary>The padded content panel (WinUI ExpanderContent). Owned: Children (the <see cref="Content"/> slot —
    /// restructure via the slot, restyle via this part: padding, fill, border, corners…).</summary>
    public const string PartContent = "Content";

    public string Header = "";
    /// <summary>Arbitrary header content (WinUI <c>Expander.Header</c> is object content, not just a string). When
    /// set it replaces the default header label; the chevron button stays. The element is given <c>Grow = 1</c>'s
    /// slot in the header row, so a column of title + caption lays out naturally.</summary>
    public Element? HeaderContent;
    public Element Content = new BoxEl { };
    public bool InitiallyExpanded = false;
    /// <summary>Optional CONTROLLED open state (the WinUI <c>IsExpanded</c> dependency property, two-way): when set,
    /// the expander reads this signal instead of its local state — writes from anywhere (an "expand all" button, a
    /// view-model) open/close it with the full motion — and the header click writes back into it.</summary>
    public Signal<bool>? IsExpanded;
    /// <summary>Optional <c>onChange</c> sugar: fired with the new open state AFTER a header toggle writes the state;
    /// a programmatic <see cref="IsExpanded"/> write does not echo it.</summary>
    public Action<bool>? OnChange;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see the
    /// class remarks and <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary><paramref name="isExpanded"/> = optional CONTROLLED open-state <see cref="Signal{T}"/> (null ⇒ the
    /// expander owns its state via <paramref name="initiallyExpanded"/> — today's behavior); <paramref name="onChange"/>
    /// fires on a header toggle. <paramref name="content"/> is a <see cref="MountOnceContentAttribute">deliberate
    /// mount-time slot</see> (STATIC content); a parent with per-render content uses the re-push slots overload below
    /// (<c>Embed.Comp(new ExpanderSlots(...), …)</c>).</summary>
    public static Element Create(string header, [MountOnceContent] Element content, bool initiallyExpanded = false,
                                 Signal<bool>? isExpanded = null, Action<bool>? onChange = null)
        => Embed.Comp(() => new Expander { Header = header, Content = content, InitiallyExpanded = initiallyExpanded,
                                           IsExpanded = isExpanded, OnChange = onChange });

    /// <summary>LIVE content slots RE-PUSHED to the core (<c>Embed.Comp(slots, …)</c>; the SelectorBar/RadioButtons
    /// pattern). An <see cref="Expander"/> is an autonomous component: its <see cref="Content"/>/<see cref="HeaderContent"/>/
    /// <see cref="Parts"/> FIELDS are frozen at first mount (a reused <c>ComponentEl</c> never re-runs its factory),
    /// so dynamic content passed by value would go stale. A parent that rebuilds its content each render must instead
    /// mount the Expander as <c>Embed.Comp(new ExpanderSlots(...), () =&gt; new Expander { InitiallyExpanded = …,
    /// IsExpanded = … })</c>; when present these slots WIN over the fields, and the Expander re-renders reactively
    /// whenever the re-pushed value changes (props are signal-backed). Read with <c>UsePropsOrDefault</c>.</summary>
    public sealed record ExpanderSlots(Element? HeaderContent, Element Content, TemplateParts? Parts);

    // WinUI Expander durations/easings (Expander.xaml, ExpandDown ~62-77 / CollapseUp ~78-90), applied to the clip
    // wrapper's LAYOUT height (SizeMode.Reflow) instead of WinUI's content TranslateY-into-snapped-space:
    //   expand   = clip height 0 → ContentHeight over 333ms, KeySpline 0,0,0,1 (Easing.FluentPopOpen).
    //   collapse = ContentHeight → 0 over 167ms, KeySpline 1,1,0,1 (the ExitDynamics leg); the content stays MOUNTED
    //              until the reflow settles (WinUI keyframes Visibility=Collapsed at t=167ms, Expander.xaml:81-83).
    //   chevron  = the AnimatedChevronUpDownSmall rotate keyframes span 10/260 of the 4333.33ms composition ≈ 167ms
    //              with cubic-bezier(0.167, 0.167, 0, 1) (AnimatedChevronUpDownSmallVisualSource.cpp:104,352,438-440).
    const float ChevronMs = 167f;
    static readonly LayoutTransition Reflow = new(TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.FluentPopOpen),
        Size: SizeMode.Reflow,
        ExitDynamics: TransitionDynamics.Tween(167f, EasingSpec.CubicBezier(1f, 1f, 0f, 1f)),
        Anchor: SizeAnchor.Trailing);

    public override Element Render()
    {
        // Live content slots (SettingsExpander etc.) win over the frozen fields; reading the re-pushed props subscribes
        // this component so a parent that rebuilds its content re-renders us with it. Static callers provide no slots → fields.
        var slots = UsePropsOrDefault<ExpanderSlots>();
        Element? headerContent = slots?.HeaderContent ?? HeaderContent;
        Element contentSlot = slots?.Content ?? Content;
        TemplateParts? parts = slots?.Parts ?? Parts;

        var (localOpen, setLocalOpen) = UseState(InitiallyExpanded);
        // Controlled (IsExpanded signal) or local state — reading the signal subscribes this component, so external
        // writes (an "expand all" button) re-render and run the full open/close motion.
        bool open = IsExpanded is { } ext ? ext.Value : localOpen;
        // The content's MOUNT state lags `open` on collapse: WinUI keyframes Visibility=Collapsed at t=167ms
        // (Expander.xaml:81-83), so the panel stays mounted while the clip shrinks over it and unmounts at settle.
        var shown = UseSignal(IsExpanded is { } init ? init.Peek() : InitiallyExpanded);
        var settings = ExpanderTemplateSettings.For(open);   // typed computed settings drive the chevron
        var chevronRef = UseRef<NodeHandle>(default);
        var clipRef = UseRef<NodeHandle>(default);
        var chevronSeeded = UseRef(false);

        bool showContent = shown.Value;          // subscribe: the collapse watcher's write re-renders this component
        bool closing = showContent && !open;     // mid collapse-reflow: content mounted, clip shrinking, watcher armed

        // An EXTERNAL open (controlled-signal write, not a header click) must mount the content too. Effects run
        // after the commit, so the panel mounts one frame later and the reflow seeds from 0 — same motion.
        UseEffect(() => { if (open && !shown.Peek()) shown.Value = true; }, open);

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
            // A mid-flight toggle starts from the LIVE rotation, not the recomputed endpoint — WinUI's
            // AnimatedIcon machine never snaps on interrupt (it queues/blends, AnimatedIcon.cpp:235-267).
            float from = anim.TryGetTrackValue(chevronRef.Value, AnimChannel.Rotation, out float live)
                ? live
                : (open ? 0f : 180f);
            anim.Animate(chevronRef.Value, AnimChannel.Rotation, from, to, ChevronMs,
                EasingSpec.CubicBezier(0.167f, 0.167f, 0f, 1f));
        }, open);

        Action<NodeHandle> chevronCapture = h => chevronRef.Value = h;
        Action<NodeHandle> clipCapture = h => clipRef.Value = h;
        Action toggle = () =>
        {
            bool next = !open;
            if (IsExpanded is { } sig) sig.Value = next; else setLocalOpen(next);   // write the state first
            if (next) shown.Value = true;
            OnChange?.Invoke(next);                                                  // then onChange (user toggle only)
        };

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
            OnRealized = chevronCapture,                          // capture for the rotation tween (AnimEngine-owned LocalTransform)
            Children =
            [
                // ExpanderChevronGlyphSize = 12. ExpanderChevronForeground = TextFillColorPrimaryBrush. One glyph, rotated.
                new TextEl(Icons.ChevronDown) { Size = 12f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont },
            ],
        };
        if (parts is { } cp)
        {
            var m = cp.Apply(PartChevron, chevron);
            chevron = m with { OnRealized = TemplateParts.Chain(chevronCapture, m.OnRealized) };
        }

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
            // Keep only the top corners while the body is mounted, INCLUDING the closing reflow (the panel stays
            // visibly attached under the header for the whole 167ms shrink — rounding the header bottom mid-reveal
            // would punch a notch against it). Once the body unmounts the header regains the full ControlCornerRadius.
            Corners = showContent ? new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f) : Radii.ControlAll,
            OnClick = toggle,
            Role = AutomationRole.Expander,
            Children =
            [
                headerContent is { } hc
                    ? new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [hc] }
                    : new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                chevron,
            ],
        };
        // Parts: restyle anything (sticky pin + :stuck fill swap, shadows, padding…); the toggle mechanics always win.
        header = parts.Apply(PartHeader, header) with { OnClick = toggle, Role = AutomationRole.Expander };

        // ExpanderContent (Expander.xaml:114): the panel inside the reveal. It keeps its natural size; the clip
        // wrapper's animated layout height crops it, and the Trailing anchor slides it with the reveal edge.
        var content = new BoxEl
        {
            Direction = 1,                       // vertical content area: stretch the child to full width so wrapping text reserves its true height
            Padding = Edges4.All(16),            // ExpanderContentPadding = 16 (restyle via [PartContent] = c => c with { Padding = … })
            MinHeight = 48f,                     // ExpanderContent MinHeight = TemplateBinding MinHeight (ExpanderMinHeight = 48)
            Fill = Tok.FillCardSecondary,        // ExpanderContentBackground = CardBackgroundFillColorSecondaryBrush
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault, // ExpanderContentBorderBrush = CardStrokeColorDefaultBrush
            // ExpanderContentDownBorderThickness = 1,0,1,1 (NO top edge — the header's own bottom border is the
            // divider). The engine border is uniform, so the panel rises 1px under the clip wrapper and the wrapper
            // crops exactly the top border row (the AutoSuggestBox −1 border-overlap idiom).
            Margin = new Edges4(0, -1f, 0, 0),
            Corners = new CornerRadius4(0f, 0f, Radii.Control, Radii.Control),   // BottomCornerRadiusFilterConverter (Expander.xaml:114)
            Children = [contentSlot],
        };
        content = parts.Apply(PartContent, content) with { Children = content.Children };   // structure = the Content slot

        // ExpanderContentClip (Expander.xaml:112-113, "The clip is a composition clip applied in code") — ALWAYS
        // MOUNTED, the engine transition's host node. The declared Height toggle 0 ↔ NaN(auto) IS the whole motion
        // trigger: the commit snap-solves the new target, the host's FLIP projection diffs old vs new size, and the
        // SizeMode.Reflow track eases the LAYOUT height through the WinUI curves while siblings reflow each tick.
        // The Trailing anchor keeps the panel's bottom edge on the reveal edge (slide-from-under-the-header).
        Element[] clipKids = showContent ? [content] : [];
        var contentClip = new BoxEl
        {
            Direction = 1,
            ClipToBounds = true,
            Height = open ? float.NaN : 0f,
            Animate = Reflow,
            OnRealized = clipCapture,              // the collapse watcher polls this node's reflow track
            Children = clipKids,
        };
        if (parts is { } pp)
        {
            var m = pp.Apply(PartClip, contentClip);
            contentClip = m with
            {
                ClipToBounds = true,
                Height = open ? float.NaN : 0f,
                Animate = Reflow,
                Children = clipKids,
                OnRealized = TemplateParts.Chain(clipCapture, m.OnRealized),
            };
        }

        // The card root mirrors the template's root Grid: pure layout, NO fill/border/clip. The collapse watcher is
        // mounted only while the closing reflow runs (the WinUI Visibility=Collapsed-at-167ms keyframe).
        Element[] children = closing
            ? [header, contentClip, Embed.Comp(() => new ExpanderCollapseWatcher { Clip = () => clipRef.Value, Shown = shown })]
            : [header, contentClip];

        var root = new BoxEl
        {
            Direction = 1,
            Children = children,
        };
        return parts.Apply(PartRoot, root) with { Children = children };
    }
}

/// <summary>Per-frame poller (the PasswordBox PeekReleaseWatcher idiom), mounted only WHILE the collapse reflow runs:
/// the moment the clip's SizeMode.Reflow track settles (the AnimEngine reclaims it), flip the mount signal off — the
/// WinUI CollapseUp storyboard's Visibility=Collapsed keyframe at t=167ms (Expander.xaml:81-83). The clip is already
/// at its declared 0 height, so the unmount itself moves nothing.</summary>
internal sealed class ExpanderCollapseWatcher : Component
{
    public required Func<NodeHandle> Clip;
    public required Signal<bool> Shown;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (only during the 167ms reflow)
        UseEffect(() =>
        {
            if (!Shown.Peek()) return;
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = Clip();
            // Settled (the reflow track completed and was reclaimed) — or the node vanished: unmount now.
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node) || !anim.HasTracks(node))
                Shown.Value = false;
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}
