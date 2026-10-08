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

/// <summary>Per-instance opt-outs for <see cref="Expander"/>'s built-in mechanics (the <see cref="TemplateParts"/>
/// door is for restyling; this is for behaviour). See <see cref="AnimateContentResize"/>.</summary>
public sealed record ExpanderOptions
{
    /// <summary>Default <c>true</c> — today's behaviour, unchanged: EVERY height change of the content clip host
    /// reveals the new height (the FlowReveal spring), including one caused by the SETTLED content resizing for a reason
    /// that has nothing to do with opening or closing (e.g. an inline drawer expanding inside an already-open Expander) —
    /// the clip's <c>Animate</c> spec can't otherwise tell "I am opening/closing" from "my content just got taller." Set
    /// <c>false</c> to scope the reveal to the open/close TOGGLE itself: a steady-open Expander whose content resizes
    /// re-lays out in one instant frame instead of replaying the disclosure motion.</summary>
    public bool AnimateContentResize { get; init; } = true;
}

/// <summary>
/// A WinUI-flavoured Expander: a clickable header row with a trailing chevron over a collapsible content panel. The
/// header toggles local <see cref="Component"/> state (or a controlled <see cref="IsExpanded"/> signal); the single
/// chevron glyph is ROTATED by the computed <see cref="ExpanderTemplateSettings"/> (down collapsed → up expanded).
///
/// MOTION — NOT WinUI's (Expander.xaml snaps the layout space and slides only the content). The content clip wrapper's
/// declared Height toggles 0 ↔ NaN(auto): layout lands ONCE at the new size and <see cref="SizeMode.FlowReveal"/> springs
/// the PRESENTED height under <c>MotionTok.Reveal</c> (critically damped, ~0.31 s, the same spring both ways) while every
/// sibling below rides the difference at paint time — no per-frame layout, no component render, interruptible from the
/// live value with its velocity. <see cref="SizeAnchor.Parallax"/>: the panel trails the moving edge by a damped share of
/// what is still hidden (≤ 24 DIP), so it reads as unfolding rather than wiped. The chevron rotates on the same spring.
/// The panel stays mounted through a close and unmounts when the engine reports the reveal at rest
/// (<c>AnimEngine.WhenSettled</c>) — no control-local ticker, no per-frame watcher.
///
/// CUSTOMIZATION goes through <see cref="Parts"/> (the one generic door — no per-feature knobs): every named template
/// part accepts arbitrary element props, e.g. a sticky pinned header
/// (<c>[PartHeader] = b => b.Sticky(8f) with { Fill = … }</c>) or an
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
    /// <summary>The always-mounted reveal wrapper (WinUI ExpanderContentClip) — the SizeMode.FlowReveal host. Owned:
    /// ClipToBounds, Height (the open/closed toggle), Animate (the reveal spec), Children, OnRealized (chained).
    /// NOTE: its children are shifted mid-motion (Parallax ChildShiftY) — do not add a ChildShift-owning scroll effect here.</summary>
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
    /// <summary>Behavioural opt-outs — see <see cref="ExpanderOptions.AnimateContentResize"/>.</summary>
    public ExpanderOptions Options = new();

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

    // The one disclosure motion (docs/plans/smooth-reveal-implementation.md §1): layout snaps once, the presented height
    // springs under MotionTok.Reveal, siblings ride it at paint time; Parallax lets the panel trail the edge.
    static readonly LayoutTransition Reveal = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
        Size: SizeMode.FlowReveal, Anchor: SizeAnchor.Parallax);
    // ExpanderOptions.AnimateContentResize=false's STEADY-state spec: no Size channel, so a settled-open clip host's content
    // change lands in the next layout as is. Only used while no toggle is in flight (see `animateResize` in Render).
    static readonly LayoutTransition RevealNoResize = Reveal with { Channels = TransitionChannels.None };

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
        // The panel's MOUNT lags `open` on close: it stays mounted while the presented height closes over it and unmounts
        // when the reveal settles (the settle callback below, or the always-fire check in the layout effect).
        var shown = UseSignal(IsExpanded is { } init ? init.Peek() : InitiallyExpanded);
        var settings = ExpanderTemplateSettings.For(open);   // typed computed settings drive the chevron
        var chevronRef = UseRef<NodeHandle>(default);
        var clipRef = UseRef<NodeHandle>(default);
        var chevronSeeded = UseRef(false);
        var openNow = UseRef(open);     // the settle callback runs outside render: it reads the committed open state here
        var lastOpen = UseRef(open);    // the open state the previous commit rendered — a toggle render sees open != lastOpen
        // Up from a toggle until its reveal settles: keeps the Size channel on the clip while a toggle is in flight even
        // when ExpanderOptions.AnimateContentResize=false (only the steady state drops it).
        var transitioning = UseSignal(false);

        bool showContent = shown.Value;          // subscribe: the settle write re-renders this component

        // Settle: the panel unmounts when closed, and either way the toggle window ends. Idempotent: the engine callback
        // and the always-fire check below may both run for one toggle.
        Action onSettled = UseMemo<Action>(() => () =>
        {
            if (!openNow.Value) shown.Value = false;
            transitioning.Value = false;
        }, DepKey.Empty);

        // The open/close bookkeeping, at 6.5 (after the host seeded this commit's reveal at 6.3):
        //  • an EXTERNAL open (controlled-signal write, not a header click) mounts the panel; it reveals from 0 next frame;
        //  • ALWAYS-FIRE: with a toggle in flight and NO live reveal row after this commit's layout, nothing will ever call
        //    back — settle now.
        UseLayoutEffect(() =>
        {
            openNow.Value = open;
            if (lastOpen.Value != open) transitioning.Value = true;
            lastOpen.Value = open;
            if (open && !shown.Peek()) { shown.Value = true; return; }
            if (!transitioning.Peek()) return;
            var anim = Context.Anim;
            var node = clipRef.Value;
            if (anim is null || node.IsNull || !anim.IsRevealing(node)) onSettled();
        }, DepKey.From(open ? 1 : 0, showContent ? 1 : 0));

        // The chevron rides the SAME spring as the reveal, so the glyph and the edge land together; a mid-flight toggle
        // retargets from the live angle with its velocity. The first mount seeds the resting angle with no motion.
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || chevronRef.Value.IsNull || !scene.IsLive(chevronRef.Value)) return;
            float to = settings.ChevronRotationDeg;
            if (!chevronSeeded.Value)
            {
                chevronSeeded.Value = true;
                anim.SeedValue(chevronRef.Value, AnimChannel.Rotation, to, MotionTokenId.Reveal, from: to);
                return;
            }
            anim.SeedValue(chevronRef.Value, AnimChannel.Rotation, to, MotionTokenId.Reveal);
        }, open);

        // The engine calls back (next frame start) when the clip's reveal rests, snapped ones included, and once more if
        // the clip node dies. One callback per clip node, unregistered on unmount.
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var node = clipRef.Value;
            if (anim is null || node.IsNull) return null;
            anim.WhenSettled(node, AnimChannel.RevealExtent, onSettled);
            return () => anim.WhenSettled(node, AnimChannel.RevealExtent, null);
        }, DepKey.Empty);

        bool toggled = open != lastOpen.Value;
        bool animateResize = Options.AnimateContentResize || toggled || transitioning.Value;

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
            // Keep only the top corners while the body is mounted, INCLUDING the closing reveal (the panel stays
            // visibly attached under the header for the whole close, until the reveal settles — rounding the header bottom mid-reveal
            // would punch a notch against it). Once the body unmounts the header regains the full ControlCornerRadius.
            Corners = showContent ? new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f) : Radii.ControlAll,
            OnClick = toggle,
            Role = AutomationRole.Expander,
            Children =
            [
                // A COLUMN: the header content is stretched to this slot's WIDTH (cross axis). In a row it was only as
                // wide as its own content, and a self-measuring header (SettingsCard builds its layout from its last
                // measured width) then latched whatever width one narrow frame gave it: a zoom step left Settings'
                // expander headers wrapped into a sliver, icon dropped, the switch spilling below the card.
                headerContent is { } hc
                    ? new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [hc] }
                    : new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                chevron,
            ],
        };
        // Parts: restyle anything (sticky pin + :stuck fill swap, shadows, padding…); the toggle mechanics always win.
        header = parts.Apply(PartHeader, header) with { OnClick = toggle, Role = AutomationRole.Expander };

        // ExpanderContent (Expander.xaml:114): the panel inside the reveal. It keeps its natural size; the clip
        // wrapper's presented height crops it, and the Parallax anchor trails it behind the reveal edge.
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
        // trigger: the host's projection diffs old vs new size and the SizeMode.FlowReveal row springs the presented
        // height; siblings ride it at paint time.
        Element[] clipKids = showContent ? [content] : [];
        var contentClip = new BoxEl
        {
            Direction = 1,
            ClipToBounds = true,
            Height = open ? float.NaN : 0f,
            Animate = animateResize ? Reveal : RevealNoResize,
            OnRealized = clipCapture,              // the settle callback and the always-fire check read this node
            Children = clipKids,
        };
        if (parts is { } pp)
        {
            var m = pp.Apply(PartClip, contentClip);
            contentClip = m with
            {
                ClipToBounds = true,
                Height = open ? float.NaN : 0f,
                Animate = animateResize ? Reveal : RevealNoResize,
                Children = clipKids,
                OnRealized = TemplateParts.Chain(clipCapture, m.OnRealized),
            };
        }

        // The card root mirrors the template's root Grid: pure layout, NO fill/border/clip.
        Element[] children = [header, contentClip];

        var root = new BoxEl
        {
            Direction = 1,
            Children = children,
        };
        return parts.Apply(PartRoot, root) with { Children = children };
    }
}
