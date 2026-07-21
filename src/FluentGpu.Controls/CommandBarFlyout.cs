using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The kind of a <see cref="AppBarCommand"/> in a <see cref="CommandBarFlyout"/>/<see cref="CommandBar"/>:
/// a plain push button, a checkable toggle (PRIMARY toggles paint the accent pill when checked; OVERFLOW toggles show
/// only the E73E check glyph — V2 OverflowChecked, CommandBarFlyout_themeresources.xaml:434-438), or a divider row.</summary>
public enum AppBarCommandKind : byte { Button, ToggleButton, Separator }

/// <summary>WinUI FlyoutShowMode subset for <see cref="CommandBarFlyout"/> (CommandBarFlyout.cpp:162-201):
/// <see cref="Standard"/> opens with the overflow ALREADY expanded — transitions suppressed while opening
/// (m_commandBarFlyoutIsOpening → UpdateUI(false), CommandBarFlyoutCommandBar.cpp:25/:121/:189-197) — and focuses the
/// first command (cpp:29-71). <see cref="Transient"/> (the default) opens collapsed and never steals focus.
/// AlwaysExpanded forces Standard (CommandBarFlyout.cpp:173-177).</summary>
public enum CommandBarFlyoutShowMode : byte { Transient, Standard }

/// <summary>A single command in a <see cref="CommandBarFlyout"/>/<see cref="CommandBar"/> — the engine analog of
/// WinUI's <c>ICommandBarElement</c> (AppBarButton / AppBarToggleButton / AppBarSeparator). An <see cref="Icon"/> +
/// <see cref="Label"/> + optional <see cref="Invoke"/> callback. The <see cref="Icon"/> is a polymorphic
/// <see cref="IconRef"/>: a plain glyph string converts implicitly (existing call sites unchanged), or
/// <c>IconRef.Themed("Name")</c> renders a layered vector icon. Use <see cref="Separator"/> for a divider in the
/// overflow menu. Optional extras: <see cref="AcceleratorText"/> (the right-aligned KeyboardAcceleratorTextOverride
/// hint), <see cref="Accelerator"/> (a REAL engine chord that invokes the command from anywhere), and
/// <see cref="Flyout"/> (a cascading sub-menu — the secondary row shows the E76C chevron and clicking it opens the
/// sub-menu WITHOUT closing the parent flyout, CommandBarFlyout.cpp:95-108).</summary>
public sealed record AppBarCommand(
    IconRef Icon,
    string Label,
    Action? Invoke = null,
    AppBarCommandKind Kind = AppBarCommandKind.Button,
    bool IsChecked = false,
    bool Enabled = true)
{
    /// <summary>Right-aligned keyboard-accelerator hint (KeyboardAcceleratorTextLabel), e.g. "Ctrl+S".</summary>
    public string? AcceleratorText { get; init; }
    /// <summary>A real keyboard chord that invokes <see cref="Invoke"/> from anywhere (WinUI KeyboardAccelerator).</summary>
    public KeyAccelerator? Accelerator { get; init; }
    /// <summary>Cascading sub-menu items (secondary commands only): the row gains the E76C SubItemChevron
    /// (CommandBarFlyout_themeresources.xaml:303) and clicking it opens the sub-menu, keeping the parent open.</summary>
    public IReadOnlyList<MenuFlyoutItem>? Flyout { get; init; }
    /// <summary>Icon shown when this toggle is CHECKED in the LABELED primary strip (the Explorer context-menu shape):
    /// rendered accent-tinted on a transparent plate — NO accent pill. Falls back to <see cref="Icon"/>. Ignored by the
    /// standalone <see cref="CommandBarFlyout"/> parity control (which keeps the WinUI accent-pill checked look).</summary>
    public IconRef? CheckedIcon { get; init; }

    /// <summary>A divider row for the secondary (overflow) menu (mirrors <c>MenuFlyoutItem.Separator</c>).</summary>
    public static AppBarCommand Separator => new(default, "", Kind: AppBarCommandKind.Separator);
}

/// <summary>A WinUI CommandBarFlyout: a trigger button that opens a contextual command toolbar anchored below it.
/// The popup has a horizontal PRIMARY row of icon AppBarButtons plus a trailing "…" More ellipsis toggle, and a
/// vertical SECONDARY overflow menu of labeled rows that the … toggle expands. 1:1 with the V2 sources
/// <c>controls/dev/CommandBarFlyout/CommandBarFlyout_themeresources.xaml</c> + <c>CommandBarFlyoutCommandBar.cpp</c>:
/// <list type="bullet">
/// <item>Primary buttons: MinWidth 40 ContentRoot (:285), InnerBorderMargin 2 (:107), 16px icon, subtle fill ramp
/// with the 83ms BrushTransition (:282) on every state swap. CHECKED primary toggles paint the accent pill
/// (Checked/CheckedPointerOver/CheckedPressed, :386-406; brushes :24-:30) with on-accent foreground; CheckedDisabled
/// drops the pill (:407-416). Primary clicks do NOT auto-close (closeFlyoutFunc is hooked only on SecondaryCommands,
/// CommandBarFlyout.cpp:67-74).</item>
/// <item>Overflow rows: plate inset 2 (InnerBorderMargin :107) inside the presenter's 3px ItemsPresenter margin
/// (:556); check glyph E73E @12 (:505); 16×16 menu icon; OverflowTextLabel Body 14 Padding 0,6,0,7 at the 12/39/67
/// lead ladder (:301/:152/:180); KeyboardAcceleratorTextLabel Caption 12 Margin 24,0,12,0 (:302); SubItemChevron
/// E76C @12 (:303). CHECKED overflow toggles show ONLY the check glyph — no accent pill (OverflowChecked sets just
/// OverflowCheckGlyph.Opacity=1, :434-438; hover/press stay the Subtle ramp, :439-458). Every secondary command —
/// toggles included — closes the flyout on invoke (Click/Checked/Unchecked → Hide(), CommandBarFlyout.cpp:97-105).</item>
/// <item>Expand/collapse: the V2 ExpansionStates transitions (themeresources:697-857) — the overflow reveals through
/// a HALF-HEIGHT CLIP slide with static content (ExpandDownAnimationStartPosition = −h/2,
/// CommandBarFlyoutCommandBar.cpp:891; ExpandedUp from +h/2, :888), synced with the WIDTH expansion (content clip
/// right edge from the midpoint, WidthExpansionAnimationStartPosition = −delta/2, :922; MoreButton glide delta/2→0,
/// :973) — 250ms open / 167ms close on ControlFastOutSlowInKeySpline 0,0,0,1. ExpandedUp places the overflow ABOVE
/// the primary row with flipped seam/corners (:966-973) when the popup is bottom-anchored (shouldExpandUp,
/// :609-657).</item>
/// <item>Flyout open/close: an 83ms opacity fade (Opening/ClosingOpacityStoryboard, themeresources:655-662) — the
/// menu clip-unfold is explicitly disabled (AreOpenCloseAnimationsEnabled(false), CommandBarFlyout.cpp:44).</item>
/// </list>
/// The flyout body returns INNER content only — <see cref="OverlayHost"/>'s FlyoutSurface supplies the acrylic
/// backdrop, 1px stroke, shadow and rounded corners. The popup is WINDOWED (ShouldConstrainToRootBounds=False,
/// CommandBarFlyout.cpp:43).</summary>
public sealed class CommandBarFlyout : Component
{
    // Template parts (the WinUI x:Name vocabulary where one exists; see TemplateParts). Each part's doc lists the
    // props the control OWNS (re-asserted after any modifier — a Parts customization cannot win those). The popup
    // parts are popup-built each open, so their modifiers run inside the overlay body's render.
    /// <summary>The trigger button that opens the flyout (engine-local — WinUI flyouts have no built-in trigger).
    /// Owned: OnClick (the open toggle), Role, OnRealized (the popup anchor capture, chained).</summary>
    public const string PartTrigger = "Trigger";
    /// <summary>The popup body root. Owned: ClipToBounds (the expand clips must never paint past the popup's rounded
    /// corners), Children, OnRealized (the width-expansion clip + open/close fade node capture, chained).</summary>
    public const string PartRoot = "Root";
    /// <summary>The horizontal primary icon row (WinUI PrimaryItemsRoot). Owned: Children.</summary>
    public const string PartPrimaryRow = "PrimaryRow";
    /// <summary>The trailing … ellipsis expand toggle (WinUI MoreButton). Owned: OnClick (expand/collapse), Role,
    /// OnKeyDown (the arrow-key roving), OnRealized (the width-expansion glide node capture, chained).</summary>
    public const string PartMoreButton = "MoreButton";
    /// <summary>The overflow region of labeled rows (WinUI OverflowContentRoot). Owned: OnRealized (the expand
    /// clip storyboard's node capture, chained), ClipToBounds, Children.</summary>
    public const string PartOverflow = "Overflow";

    public string TriggerLabel = "Commands";
    public IReadOnlyList<AppBarCommand> PrimaryCommands = [];
    public IReadOnlyList<AppBarCommand> SecondaryCommands = [];
    /// <summary>WinUI V2 AlwaysExpanded: keep the overflow menu shown and hide the … More button. Forces
    /// <see cref="CommandBarFlyoutShowMode.Standard"/> (CommandBarFlyout.cpp:173-177).</summary>
    public bool AlwaysExpanded = false;

    // Frozen-props tripwire (ReuseGuard): TriggerLabel freezes at mount (the command lists are slots — deliver those
    // via a provider). A reused instance whose trigger label changed is the frozen-props bug.
    public override bool ChecksReuse => ReuseGuard.CompiledIn;
    public override void DebugCheckReuse(Component next)
    {
        if (next is CommandBarFlyout n && n.TriggerLabel != TriggerLabel)
            ReuseGuard.ScalarChanged(this, nameof(TriggerLabel));
    }
    /// <summary>Standard = open expanded (no transition) + focus the first command; Transient = open collapsed,
    /// no focus steal (CommandBarFlyout.cpp:162-201; CommandBarFlyoutCommandBar.cpp:29-71).</summary>
    public CommandBarFlyoutShowMode ShowMode = CommandBarFlyoutShowMode.Transient;
    public FlyoutPlacement Placement = FlyoutPlacement.BottomLeft;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract. Threaded into the popup body each open.</summary>
    public TemplateParts? Parts;

    public static Element Create(
        string triggerLabel,
        IReadOnlyList<AppBarCommand> primaryCommands,
        IReadOnlyList<AppBarCommand> secondaryCommands,
        bool alwaysExpanded = false,
        FlyoutPlacement placement = FlyoutPlacement.BottomLeft,
        CommandBarFlyoutShowMode showMode = CommandBarFlyoutShowMode.Transient)
        => Embed.Comp(() => new CommandBarFlyout
        {
            TriggerLabel = triggerLabel,
            PrimaryCommands = primaryCommands,
            SecondaryCommands = secondaryCommands,
            AlwaysExpanded = alwaysExpanded,
            ShowMode = showMode,
            Placement = placement,
        });

    /// <summary>Back-compat shim: the zero/one-arg form still works, supplying a representative sample command set so
    /// existing call sites (and the gallery) keep compiling.</summary>
    public static Element Create(string triggerLabel = "Commands") => Create(triggerLabel, DefaultPrimary, DefaultSecondary);

    /// <summary>Build the CommandBarFlyout BODY (no trigger button) as standalone overlay content — the Win11 Explorer
    /// context menu shape: a primary icon strip over the secondary rows. Used by <c>ContextMenu</c> to open the
    /// command-bar body at a point. <paramref name="alwaysExpanded"/> (default true) shows the overflow immediately and
    /// hides the … button; the body opens in Standard mode (WinUI initial focus on the first command). Pass
    /// <paramref name="fadeCloseSlot"/> so a tracker-forced close rides the 83ms ClosingOpacityStoryboard;
    /// <paramref name="touchInputMode"/> for the roomier touch metrics (a Hold-triggered open);
    /// <paramref name="overflowMinWidth"/> to widen the overflow to the Explorer ~250 feel. Internal: the standalone
    /// command-bar body is composed by <c>ContextMenu</c>; the public door is <see cref="Create"/>.</summary>
    internal static Element BuildBody(
        IReadOnlyList<AppBarCommand> primary,
        IReadOnlyList<AppBarCommand> secondary,
        Action close,
        Ref<Action?>? fadeCloseSlot = null,
        TemplateParts? parts = null,
        bool alwaysExpanded = true,
        float overflowMinWidth = 136f,
        bool touchInputMode = false,
        bool labeledPrimary = false)
        => Embed.Comp(() => new CommandBarFlyoutBody
        {
            Primary = primary,
            Secondary = secondary,
            AlwaysExpanded = alwaysExpanded,
            StandardMode = true,   // Explorer shape: overflow expanded + initial focus (CommandBarFlyout.cpp:29-71)
            Close = close,
            FadeCloseSlot = fadeCloseSlot,
            Parts = parts,
            TouchInputMode = touchInputMode,
            OverflowMinWidth = overflowMinWidth,
            LabeledPrimary = labeledPrimary,
        });

    static readonly AppBarCommand[] DefaultPrimary =
    [
        new(Icons.Accept, "Accept"),
        new(Icons.Share, "Share"),
        new(Icons.Tag, "Tag"),
    ];

    static readonly AppBarCommand[] DefaultSecondary =
    [
        new(Icons.Settings, "Settings"),
        AppBarCommand.Separator,
        new(Icons.Accept, "Show grid", Kind: AppBarCommandKind.ToggleButton, IsChecked: true),
    ];

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        // The body registers its 83ms fade-out close here (ClosingOpacityStoryboard, themeresources:659-662) so the
        // trigger's own toggle also closes through the fade instead of snap-hiding.
        var fadeClose = UseRef<Action?>(null);
        // `expanded` is a SIGNAL (not UseState) so the … toggle re-renders the overflow region granularly inside the
        // overlay body Component without re-opening the popup or bumping the OverlayHost version.
        var expanded = UseSignal(AlwaysExpanded);

        bool standardMode = AlwaysExpanded || ShowMode == CommandBarFlyoutShowMode.Standard;

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } o)
            {
                if (fadeClose.Value is { } fade) fade(); else o.Close();
                return;
            }
            // Standard ShowMode (incl. AlwaysExpanded, which forces Standard — CommandBarFlyout.cpp:173-177) opens
            // with the overflow already expanded (commandBar.IsOpen(true), cpp:194-197); Transient opens collapsed.
            expanded.Value = standardMode;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new CommandBarFlyoutBody
                {
                    Primary = PrimaryCommands,
                    Secondary = SecondaryCommands,
                    AlwaysExpanded = AlwaysExpanded,
                    StandardMode = standardMode,
                    Expanded = expanded,
                    Close = () => handle.Value?.Close(),
                    FadeCloseSlot = fadeClose,
                    Parts = Parts,
                }),
                Placement,
                // ShouldConstrainToRootBounds=False + AreOpenCloseAnimationsEnabled=False (CommandBarFlyout.cpp:43-44):
                // the popup must NOT play the menu clip-unfold — WinUI's open/close are the 83ms opacity storyboards
                // (themeresources:655-662). PopupChrome.CommandBar = windowed transient-acrylic chrome with NO host
                // open motion (the body seeds its own 83ms fade) and the 83ms host close fade — so light-dismiss
                // closes fade exactly like command/toggle closes (the old Static chrome hid those instantly).
                new PopupOptions(Chrome: PopupChrome.CommandBar) { ConstrainToRootBounds = false });
        }

        Action<NodeHandle> anchorCapture = x => anchor.Value = x;
        var trigger = new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = anchorCapture,
            OnClick = Toggle,
            Role = AutomationRole.Button,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 8f,
            MinHeight = 32f,
            Padding = new Edges4(11, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            Children =
            [
                new TextEl(TriggerLabel) { Size = 14f, Color = Tok.TextPrimary },
                new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
            ],
        };
        // Parts: restyle the trigger freely; the open toggle and the popup anchor capture always win.
        if (Parts is { } tp)
        {
            var m = tp.Apply(PartTrigger, trigger);
            trigger = m with { OnClick = Toggle, Role = AutomationRole.Button, OnRealized = TemplateParts.Chain(anchorCapture, m.OnRealized) };
        }
        return trigger;
    }
}

/// <summary>The inner content of the CommandBarFlyout popup (NO surface chrome — the host's FlyoutSurface supplies
/// acrylic + 1px stroke + shadow + corner). Its own Component so reading <see cref="Expanded"/> grants the … toggle a
/// granular re-render of the overflow region. Layout mirrors WinUI's CommandBarFlyoutCommandBar: a horizontal
/// PrimaryItemsRoot (Height 40, Margin 3,3,0,3) of icon buttons + a trailing 44px ellipsis MoreButton, then an
/// OverflowRegion of full-width labeled rows (ABOVE the row when expanded up) whose EXPAND/COLLAPSE runs the V2
/// half-height clip + width-expansion storyboards. The body also drives the flyout-level 83ms opacity fades
/// (Opening/ClosingOpacityStoryboard) and the WinUI arrow-key roving (CommandBarFlyoutCommandBar.cpp:1159-1296).</summary>
internal sealed class CommandBarFlyoutBody : Component
{
    public IReadOnlyList<AppBarCommand> Primary = [];
    public IReadOnlyList<AppBarCommand> Secondary = [];
    public bool AlwaysExpanded;
    /// <summary>FlyoutShowMode.Standard semantics: opened expanded without transitions + initial focus on the first
    /// command (CommandBarFlyout.cpp:162-201; CommandBarFlyoutCommandBar.cpp:25-71).</summary>
    public bool StandardMode;
    public Signal<bool> Expanded = new(false);
    public Action Close = () => { };
    /// <summary>Outer slot the body fills with its fade-close so the trigger toggle closes through the 83ms fade.</summary>
    public Ref<Action?>? FadeCloseSlot;
    /// <summary>The owning <see cref="CommandBarFlyout"/>'s part modifiers (keyed by its <c>PartXxx</c> consts).</summary>
    public TemplateParts? Parts;
    /// <summary>WinUI TouchInputMode/GameControllerInputMode: OverflowTextLabel.Padding 0,9,0,11 + check glyph margin
    /// 12,10,12,10 (CommandBarFlyout_themeresources.xaml:464-465) instead of the pointer metrics.</summary>
    public bool TouchInputMode;
    /// <summary>Explorer shell-menu shape (set by <c>ContextMenu</c>): the primary strip renders an icon-OVER-label
    /// stack per button (20px icon + a Caption-size label, min-width 64, taller row), and a checked toggle drops the
    /// WinUI accent pill for an accent-tinted glyph on a transparent plate. Off = the WinUI icon-only parity control.</summary>
    public bool LabeledPrimary;

    // Dim constants from CommandBarFlyout_themeresources.xaml.
    const float PrimaryRowHeight = 40f;        // PrimaryItemsControl Height (:1043)
    const float LabeledPrimaryRowHeight = 70f; // Explorer icon-over-label strip: ~20px icon + Caption label + padding
                                               // (row 70 + primaryRow Margin T3+B3 = 76px total strip — Explorer parity)
    const float LabeledPrimaryBtnMinWidth = 64f;
    const float AppBarBtnMinWidth = 40f;       // CommandBarFlyoutAppBarButton ContentRoot MinWidth (:285 MinWidth=40)
    const float MoreButtonWidth = 44f;         // EllipsisButton Width (:568)
    /// <summary>CommandBarOverflowPresenter MinWidth (:530). Overridable via <see cref="CommandBarFlyout.BuildBody"/>
    /// so a context menu can widen the overflow to the Win11 Explorer ~250 feel (<c>ContextMenuOptions.MinWidth</c>).</summary>
    public float OverflowMinWidth = 136f;
    const float OverflowMaxWidth = 440f;       // CommandBarOverflowPresenter MaxWidth (:531)
    const float OverflowMaxHeight = 480f;      // CommandBarOverflowPresenter MaxHeight (:532) — scrolls past it (:533-536)
    const float FlyoutMaxWidth = 440f;         // CommandBarFlyoutCommandBar MaxWidth (:639)

    public override Element Render()
    {
        lastService = UseContext(Overlay.Service);          // cascading sub-flyouts open through the same service
        var hk = UseContext(InputHooks.Current);            // focus moves: arrow roving + the Standard initial focus
        var placement = UseContext(Overlay.Placement);

        bool hasSecondary = Secondary.Count > 0;
        // SecondaryCommandsOnly (themeresources:866-874): WinUI zeroes the primary row (Opacity 0 / Height 0 / not
        // hit-testable) and skips the overflow shadow (shadow only added when primary commands exist,
        // CommandBarFlyout.cpp:203-207) — the flyout IS the overflow menu, opened expanded with no … button.
        bool secondaryOnly = hasSecondary && Primary.Count == 0;
        bool expanded = Expanded.Value || AlwaysExpanded || secondaryOnly;   // SUBSCRIBE: … toggle re-renders this body

        var collapsing = UseSignal(false);                  // reverse storyboard in flight; region stays mounted
        var closing = UseSignal(false);                     // 83ms closing fade in flight; Close() fires at its end
        var pendingFocus = UseSignal(-1);                   // secondary index awaiting focus once the overflow mounts
        // Standard/AlwaysExpanded/secondary-only open EXPANDED with the transitions suppressed
        // (UpdateUI(!m_commandBarFlyoutIsOpening), CommandBarFlyoutCommandBar.cpp:25/:121): seed as already-expanded
        // so the first mount never replays the 250ms expand storyboard.
        var expandSeeded = UseRef(AlwaysExpanded || StandardMode || secondaryOnly);
        var seededUp = UseRef(false);                       // direction the live reveal was seeded with (flip detect)
        var openFadeSeeded = UseRef(false);
        var collapsedW = UseRef(0f);                        // collapsed (primary-row) width — WinUI collapsedWidth (cpp:879)
        var widthDelta = UseRef(0f);                        // live expandedW − collapsedW for the collapse reverse
        var overflowNode = UseRef<NodeHandle>(default);
        _overflowNode = overflowNode;
        bool collapsingNow = collapsing.Value;
        bool closingNow = closing.Value;
        int pendingFocusNow = pendingFocus.Value;
        bool showMore = hasSecondary && !AlwaysExpanded && !secondaryOnly;
        bool showOverflow = hasSecondary && (expanded || collapsingNow);

        if (_primaryNodes.Length != Primary.Count) _primaryNodes = new NodeHandle[Primary.Count];
        if (_secondaryNodes.Length != Secondary.Count) _secondaryNodes = new NodeHandle[Secondary.Count];

        // ── Labeled-strip minimum width (Explorer parity) ──────────────────────────────────────────────────────────
        // The LabeledPrimary columns carry Basis 0 (contribute 0 natural width) but clamp to LabeledPrimaryBtnMinWidth,
        // so N columns need N × (MinWidth + 4px inner margin) + (N−1) dividers + the row's left margin. When that
        // exceeds the overflow MinWidth (e.g. a 4-button track strip: 4×68 + 3 + 3 = 278 > 250), the clamped columns
        // overflow the ClipToBounds root and the last label clips ("Sav…"). Force the root wide enough to fit the strip
        // (and keep the overflow region flush via the column-stretch). Icon-only mode keeps its natural width (NaN).
        float rootMinWidth = float.NaN;
        if (LabeledPrimary && Primary.Count > 0)
        {
            float stripMin = Primary.Count * (LabeledPrimaryBtnMinWidth + 4f)  // MinWidth + InnerBorderMargin L2+R2
                           + (Primary.Count - 1) * 1f                          // LabeledStripDivider width
                           + 3f;                                               // primaryRow Margin L3 (R0)
            rootMinWidth = stripMin > OverflowMinWidth ? stripMin : OverflowMinWidth;
        }

        // ── ExpandedUp (shouldExpandUp, CommandBarFlyoutCommandBar.cpp:609-657): WinUI expands the overflow UPWARD
        // when there's no room below the bar but room above. Engine analog: the positioner bottom-anchors the popup
        // when it doesn't fit below the anchor (OverlayPlacementInfo.OpensUp) — growth then happens upward with the
        // primary row glued to the anchor edge, the overflow stacks ABOVE the row, seam/corners flip
        // (ExpandedUpWithPrimaryCommands, themeresources:966-973) and the reveal runs bottom-half-up
        // (ExpandUpAnimationStartPosition = +h/2, cpp:888).
        bool expandUp = !secondaryOnly && (placement?.Value.OpensUp ?? false);

        // ── Flyout-level open fade: OpeningOpacityStoryboard = Opacity 0→1 over ControlFasterAnimationDuration 83ms,
        // linear (themeresources:655-658; AreOpenCloseAnimationsEnabled(false) kills the default flyout transition,
        // CommandBarFlyout.cpp:44). Driven on the body root — the host surface card is host-owned. Mount-once.
        UseLayoutEffect(() =>
        {
            if (openFadeSeeded.Value) return;
            var scene = Context.Scene;
            var anim = Context.Anim;
            if (scene is null || anim is null || _rootNode.IsNull || !scene.IsLive(_rootNode)) return;
            openFadeSeeded.Value = true;
            anim.Animate(_rootNode, AnimChannel.Opacity, 0f, 1f, Motion.ControlFaster, Easing.Linear);
        }, DepKey.Empty);   // mount-once

        void CloseSubFlyout() { if (subHandleLive is { IsOpen: true } s) s.Close(); subHandleLive = null; }

        // ClosingOpacityStoryboard: Opacity 1→0 over 83ms linear (themeresources:659-662), then hide. Every
        // command/toggle close (and the trigger toggle, via FadeCloseSlot) routes through here.
        void RequestClose()
        {
            if (closing.Peek()) return;
            var scene = Context.Scene;
            var anim = Context.Anim;
            CloseSubFlyout();
            if (scene is null || anim is null || _rootNode.IsNull || !scene.IsLive(_rootNode)) { Close(); return; }
            closing.Value = true;   // mounts the 83ms clock below
            anim.Animate(_rootNode, AnimChannel.Opacity, 1f, 0f, Motion.ControlFaster, Easing.Linear);
        }
        _requestClose = RequestClose;
        if (FadeCloseSlot is { } slot) slot.Value = RequestClose;

        // ── Expand/collapse storyboards — the V2 ExpansionStates transitions (themeresources:697-857), driven on the
        // region + root + MoreButton nodes through the AnimEngine clip/translate channels:
        //  • Reveal: a HALF-HEIGHT CLIP slide, content static — ExpandDownAnimationStartPosition = −h/2
        //    (CommandBarFlyoutCommandBar.cpp:891) ⇒ the clip's far edge eases h/2 → h; ExpandedUp mirrors from +h/2
        //    (cpp:888) ⇒ the near edge eases h/2 → 0. Only OverflowContentRootClipTransform animates — no content
        //    translate (themeresources:784-787/:716-719).
        //  • Width: the content clip's right edge jumps to the (collapsed+expanded)/2 midpoint and eases to the
        //    expanded width (WidthExpansionAnimationStartPosition = −delta/2 → −delta, cpp:921-923;
        //    themeresources:776-779) while the MoreButton glides delta/2 → 0 (cpp:973; themeresources:772-775).
        // Open = ControlNormal 250ms, close = ControlFast 167ms, both ControlFastOutSlowInKeySpline 0,0,0,1.
        UseLayoutEffect(() =>
        {
            var scene = Context.Scene;
            var anim = Context.Anim;
            if (scene is null || anim is null) return;
            bool rootLive = !_rootNode.IsNull && scene.IsLive(_rootNode);

            if (!showOverflow)
            {
                // Collapsed: remember the primary-row width — WinUI's collapsedWidth measure (cpp:879).
                if (rootLive) collapsedW.Value = scene.AbsoluteRect(_rootNode).W;
                return;
            }
            var node = overflowNode.Value;
            if (node.IsNull || !scene.IsLive(node)) return;
            float h = scene.AbsoluteRect(node).H;
            if (h <= 0f) return;

            if (expanded && !collapsing.Peek())
            {
                if (!expandSeeded.Value)
                {
                    expandSeeded.Value = true;
                    seededUp.Value = expandUp;
                    if (expandUp)
                        anim.Animate(node, AnimChannel.ClipT, h * 0.5f, 0f, Motion.ControlNormal, Easing.FluentPopOpen);
                    else
                        anim.Animate(node, AnimChannel.ClipB, h * 0.5f, h, Motion.ControlNormal, Easing.FluentPopOpen);

                    if (rootLive)
                    {
                        float c = collapsedW.Value;
                        float e = scene.AbsoluteRect(_rootNode).W;
                        if (c > 0f && e - c > 0.5f)
                        {
                            widthDelta.Value = e - c;
                            anim.Animate(_rootNode, AnimChannel.ClipR, (c + e) * 0.5f, e, Motion.ControlNormal, Easing.FluentPopOpen);
                            if (!_moreNode.IsNull && scene.IsLive(_moreNode))
                                anim.Animate(_moreNode, AnimChannel.TranslateX, (c - e) * 0.5f, 0f, Motion.ControlNormal, Easing.FluentPopOpen);
                        }
                        else widthDelta.Value = 0f;
                    }
                }
                else if (seededUp.Value != expandUp)
                {
                    // The positioner flipped the popup while expanded (the grown popup no longer fit below): settle
                    // the reveal instantly in the new direction instead of clipping against the stale edge.
                    anim.Cancel(node, AnimChannel.ClipT);
                    anim.Cancel(node, AnimChannel.ClipB);
                    seededUp.Value = expandUp;
                }
            }
            else if (collapsing.Peek() && expandSeeded.Value)
            {
                expandSeeded.Value = false;
                if (expandUp)
                    anim.Animate(node, AnimChannel.ClipT, 0f, h * 0.5f, Motion.ControlFast, Easing.FluentPopOpen);
                else
                    anim.Animate(node, AnimChannel.ClipB, h, h * 0.5f, Motion.ControlFast, Easing.FluentPopOpen);
                if (rootLive && widthDelta.Value > 0.5f)
                {
                    float e = scene.AbsoluteRect(_rootNode).W;
                    float c = e - widthDelta.Value;
                    anim.Animate(_rootNode, AnimChannel.ClipR, e, (c + e) * 0.5f, Motion.ControlFast, Easing.FluentPopOpen);
                    if (!_moreNode.IsNull && scene.IsLive(_moreNode))
                        anim.Animate(_moreNode, AnimChannel.TranslateX, 0f, (c - e) * 0.5f, Motion.ControlFast, Easing.FluentPopOpen);
                    widthDelta.Value = 0f;
                }
            }
        }, DepKey.From(HashCode.Combine(expanded, collapsingNow, expandUp)));

        // Keyboard entry into the overflow (Down past the … / Up-wrap): WinUI opens the overflow BEFORE focusing the
        // secondary command (IsOpen(true) → FocusControl, CommandBarFlyoutCommandBar.cpp:1266-1279) — ours focuses
        // the requested row once it has realized.
        UseLayoutEffect(() =>
        {
            int j = pendingFocus.Peek();
            if (j < 0 || !(Expanded.Peek() || AlwaysExpanded || secondaryOnly)) return;
            var scene = Context.Scene;
            if (scene is null) return;
            if ((uint)j >= (uint)_secondaryNodes.Length) { pendingFocus.Value = -1; return; }
            var node = _secondaryNodes[j];
            if (node.IsNull || !scene.IsLive(node)) return;
            pendingFocus.Value = -1;
            hk.MoveFocusVisual?.Invoke(node);
        }, (pendingFocusNow, expanded));

        // Standard-mode initial focus: the first primary command if any, else the first secondary
        // (CommandBarFlyoutCommandBar.cpp:29-71 — FocusState::Programmatic, so no focus visual). Mount-once.
        UseLayoutEffect(() =>
        {
            if (!StandardMode) return;
            var scene = Context.Scene;
            if (scene is null) return;
            NodeHandle target = default;
            for (int i = 0; i < Primary.Count; i++)
                if (Primary[i].Enabled && !_primaryNodes[i].IsNull && scene.IsLive(_primaryNodes[i])) { target = _primaryNodes[i]; break; }
            if (target.IsNull)
                for (int j = 0; j < Secondary.Count; j++)
                    if (Secondary[j].Kind != AppBarCommandKind.Separator && Secondary[j].Enabled
                        && !_secondaryNodes[j].IsNull && scene.IsLive(_secondaryNodes[j])) { target = _secondaryNodes[j]; break; }
            if (!target.IsNull) hk.FocusNode?.Invoke(target, false);
        }, DepKey.Empty);   // mount-once

        void ExpandToggle()
        {
            if (AlwaysExpanded || secondaryOnly) return;
            if (!Expanded.Peek()) { collapsing.Value = false; Expanded.Value = true; return; }
            if (collapsing.Peek()) return;       // collapse already in flight
            collapsing.Value = true;             // keep the region mounted while the reverse storyboard runs
        }

        // ── Arrow-key roving (CommandBarFlyoutCommandBar::OnKeyDown, cpp:1159-1296): Left/Right cycle the HORIZONTAL
        // list (primary commands + MoreButton) with NO wrap — the key is consumed even when focus stays put
        // (:1286-1290); Up/Down cycle the VERTICAL list (primary → MoreButton → secondary) WITH wrap (:1215-1219
        // shouldLoop), auto-expanding the overflow when the target is a secondary command (:1266-1273). Escape is
        // engine-handled (the overlay's light-dismiss preview, :1190-1198 equivalent). WinUI's Tab group-hop
        // (:1172-1188) is engine-blocked: the dispatcher consumes Tab for focus movement before node key handlers.
        void FocusNodeVisual(NodeHandle n)
        {
            if (Context.Scene is { } sc && !n.IsNull && sc.IsLive(n)) hk.MoveFocusVisual?.Invoke(n);
        }

        void HorizontalMove(int from, int dir)
        {
            int count = Primary.Count + (showMore ? 1 : 0);
            for (int i = from + dir; i >= 0 && i < count; i += dir)
            {
                if (i < Primary.Count && !Primary[i].Enabled) continue;
                FocusNodeVisual(i == Primary.Count ? _moreNode : _primaryNodes[i]);
                return;
            }
        }

        void VerticalMove(int from, int dir)
        {
            int moreCount = showMore ? 1 : 0;
            int total = Primary.Count + moreCount + Secondary.Count;
            if (total == 0) return;
            for (int step = 1; step <= total; step++)
            {
                int i = (((from + dir * step) % total) + total) % total;
                if (i < Primary.Count)
                {
                    if (!Primary[i].Enabled) continue;
                    FocusNodeVisual(_primaryNodes[i]);
                    return;
                }
                if (i < Primary.Count + moreCount) { FocusNodeVisual(_moreNode); return; }
                int j = i - Primary.Count - moreCount;
                var cmd = Secondary[j];
                if (cmd.Kind == AppBarCommandKind.Separator || !cmd.Enabled) continue;
                bool overflowShown = Expanded.Peek() || AlwaysExpanded || secondaryOnly;
                if (overflowShown && !collapsing.Peek()
                    && (uint)j < (uint)_secondaryNodes.Length
                    && !_secondaryNodes[j].IsNull && Context.Scene is { } s2 && s2.IsLive(_secondaryNodes[j]))
                {
                    FocusNodeVisual(_secondaryNodes[j]);
                    return;
                }
                // Auto-expand on keyboard entry (cpp:1266-1273); the pendingFocus effect lands focus once mounted.
                pendingFocus.Value = j;
                collapsing.Value = false;
                Expanded.Value = true;
                return;
            }
        }

        void OnBarKey(int hIdx, KeyEventArgs a)
        {
            if (a.Handled) return;
            switch (a.KeyCode)
            {
                case Keys.Left: HorizontalMove(hIdx, -1); a.Handled = true; break;
                case Keys.Right: HorizontalMove(hIdx, +1); a.Handled = true; break;
                case Keys.Down: VerticalMove(hIdx, +1); a.Handled = true; break;
                case Keys.Up: VerticalMove(hIdx, -1); a.Handled = true; break;
            }
        }

        void OnRowKey(int j, KeyEventArgs a)
        {
            if (a.Handled) return;
            int v = Primary.Count + (showMore ? 1 : 0) + j;
            switch (a.KeyCode)
            {
                case Keys.Down: VerticalMove(v, +1); a.Handled = true; break;
                case Keys.Up: VerticalMove(v, -1); a.Handled = true; break;
                // Consumed but move nothing — the horizontal list holds only primary commands + the MoreButton
                // (cpp:1215, :1286-1290).
                case Keys.Left or Keys.Right: a.Handled = true; break;
            }
        }

        Action<NodeHandle> rootCapture = h => _rootNode = h;

        // The 83ms closing-fade clock: fires the real Close() once the ClosingOpacityStoryboard settles.
        Element? closeClock = closingNow
            ? Embed.Comp(() => new ToolTipClock { DurationMs = Motion.ControlFaster, OnElapsed = Close }) with { Key = "cbf-close" }
            : null;

        // ── SecondaryCommandsOnly: no primary row, no seam, no … — a plain expanded menu (themeresources:866-874). ──
        if (secondaryOnly)
        {
            var menuChildren = new List<Element>(2) { BuildOverflow(expandUp: false, secondaryOnly: true, OnRowKey) };
            if (closeClock is not null) menuChildren.Add(closeClock);
            var menuRoot = new BoxEl
            {
                Direction = 1,
                AlignSelf = FlexAlign.Start,
                MaxWidth = FlyoutMaxWidth,
                OnRealized = rootCapture,
                Children = menuChildren.ToArray(),
            };
            var mm = Parts.Apply(CommandBarFlyout.PartRoot, menuRoot);
            return mm with { Children = menuRoot.Children, OnRealized = TemplateParts.Chain(rootCapture, mm.OnRealized) };
        }

        // ── PrimaryItemsRoot: horizontal row of icon buttons + trailing ellipsis ──────────────────────────────
        var primaryChildren = new List<Element>(Primary.Count * 2 + 2);
        if (LabeledPrimary)
        {
            // Explorer strip: equal-width columns filling the row (PrimaryButton carries Basis 0 + Grow 1), with a
            // 1px vertical divider BETWEEN adjacent buttons — never at the ends, never around the … More button
            // (which isn't shown in labeled mode). The row width is driven by the overflow's MinWidth, not by any
            // one label, so a long "Save to Liked Songs" can't balloon its column and stretch the whole menu.
            for (int i = 0; i < Primary.Count; i++)
            {
                if (i > 0) primaryChildren.Add(LabeledStripDivider());
                int idx = i;
                primaryChildren.Add(PrimaryButton(Primary[i], idx, OnBarKey));
            }
        }
        else
        {
            for (int i = 0; i < Primary.Count; i++)
            {
                int idx = i;
                primaryChildren.Add(PrimaryButton(Primary[i], idx, OnBarKey));
            }
            primaryChildren.Add(new BoxEl { Grow = 1f });   // spacer pins the … button to the right edge
            if (showMore)
                primaryChildren.Add(MoreButton(ExpandToggle, OnBarKey));
        }

        var primaryRow = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Height = LabeledPrimary ? LabeledPrimaryRowHeight : PrimaryRowHeight,
            Margin = new Edges4(3, 3, 0, 3),
            // The corner joint flattens against the overflow on the joined side: ExpandedDown keeps the row's TOP
            // corners (TopCornerRadiusFilterConverter, themeresources:978-979), ExpandedUp keeps the BOTTOM corners
            // (:969-970).
            Corners = !showOverflow ? Radii.OverlayAll : expandUp ? Radii.OverlayBottom : Radii.OverlayTop,
            Children = primaryChildren.ToArray(),
        };
        // KEYED: the root's children REORDER when the expand direction flips (overflow above vs below) — keys keep
        // the retained nodes (and the captured handles the storyboards drive) stable across the reorder.
        primaryRow = Parts.Apply(CommandBarFlyout.PartPrimaryRow, primaryRow) with { Children = primaryRow.Children, Key = "cbf-primary" };

        if (!showOverflow)
        {
            var collapsedChildren = new List<Element>(2) { primaryRow };
            if (closeClock is not null) collapsedChildren.Add(closeClock);
            var collapsedRoot = new BoxEl
            {
                Direction = 1,
                AlignSelf = FlexAlign.Start,
                MaxWidth = FlyoutMaxWidth,
                MinWidth = rootMinWidth,   // widen to fit the labeled strip's clamped columns (NaN = natural, icon-only)
                OnRealized = rootCapture,
                Children = collapsedChildren.ToArray(),
            };
            var cm = Parts.Apply(CommandBarFlyout.PartRoot, collapsedRoot);
            return cm with { Children = collapsedRoot.Children, OnRealized = TemplateParts.Chain(rootCapture, cm.OnRealized) };
        }

        // The collapse clock unmounts the region once the 167ms reverse storyboard settles.
        Element? collapseClock = collapsingNow
            ? Embed.Comp(() => new ToolTipClock
            {
                DurationMs = Motion.ControlFast,
                OnElapsed = () => { CloseSubFlyout(); collapsing.Value = false; Expanded.Value = false; },
            }) with { Key = "cbf-collapse" }
            : null;

        // 1px seam border between the primary row and the overflow region — CommandBarFlyoutBorderBrush =
        // ControlStrokeColorDefaultBrush (themeresources:7, bound :629-630); the SecondaryItemsControl joint border
        // is 1,0,1,1 expanded down / 1,1,1,0 expanded up (:977/:968) so the seam sits on the joined edge either way.
        var seam = new BoxEl { Height = 1f, Fill = Tok.StrokeControlDefault, Key = "cbf-seam" };
        var overflow = BuildOverflow(expandUp, secondaryOnly: false, OnRowKey) with { Key = "cbf-overflow" };

        var children = new List<Element>(5);
        if (expandUp) { children.Add(overflow); children.Add(seam); children.Add(primaryRow); }
        else { children.Add(primaryRow); children.Add(seam); children.Add(overflow); }
        if (collapseClock is not null) children.Add(collapseClock);
        if (closeClock is not null) children.Add(closeClock);

        var root = new BoxEl
        {
            Direction = 1,
            AlignSelf = FlexAlign.Start,
            MaxWidth = FlyoutMaxWidth,
            MinWidth = rootMinWidth,   // widen to fit the labeled strip's clamped columns (NaN = natural, icon-only)
            ClipToBounds = true,   // the expand clips must never paint past the popup's rounded corners
            OnRealized = rootCapture,
            Children = children.ToArray(),
        };
        var rm = Parts.Apply(CommandBarFlyout.PartRoot, root);
        return rm with { ClipToBounds = true, Children = root.Children, OnRealized = TemplateParts.Chain(rootCapture, rm.OnRealized) };
    }

    // ── A single primary (icon-only) AppBarButton — CommandBarFlyoutAppBarButtonStyleBase metrics + the 83ms
    //    BrushTransition on the InnerBorder background (CommandBarFlyout_themeresources.xaml:280-283). A CHECKED
    //    toggle paints the accent pill on the 2px-inset plate (Checked → AppBarButtonInnerBorder.Background =
    //    CommandBarFlyoutAppBarButtonBackgroundChecked = AccentFillColorDefault, :386-392/:24; PointerOver = Accent
    //    Secondary :25; Pressed = AccentTertiary :26) with on-accent foreground (:28-:30). CheckedDisabled sets NO
    //    background — disabled foreground only, no pill (:407-416). ──────────────────────────────────────────────
    Element PrimaryButton(AppBarCommand cmd, int idx, Action<int, KeyEventArgs> onKey)
    {
        bool enabled = cmd.Enabled;
        bool isToggle = cmd.Kind == AppBarCommandKind.ToggleButton;
        bool checkedOn = isToggle && cmd.IsChecked && enabled;   // CheckedDisabled drops the pill/state (:407-416)
        // WinUI parity control (icon-only): a checked toggle paints the accent PILL. The Explorer shell-menu shape
        // (LabeledPrimary): NO pill — the checked glyph is tinted AccentDefault on a transparent plate (the app's toggle
        // language, e.g. the player-bar Like: a filled heart tinted accent), and every button shows a caption label.
        bool pill = checkedOn && !LabeledPrimary;
        var fg = !enabled ? Tok.TextDisabled
               : pill ? Tok.TextOnAccentPrimary
               : checkedOn ? Tok.AccentDefault   // labeled + checked: accent-tinted glyph, no pill
               : Tok.TextPrimary;
        IconRef iconRef = checkedOn ? (cmd.CheckedIcon ?? cmd.Icon) : cmd.Icon;
        float iconSize = LabeledPrimary ? 20f : 16f;

        // Single IconRef render path: layered-vector when the themed name is registered, else the glyph.
        // ForegroundCheckedPressed = TextOnAccentFillColorSecondary (:30); plain pressed = TextSecondary (:14).
        // The Explorer strip renders the layered TWO-TONE themed icon uniformly (glyph only when no themed name is
        // registered). A themed icon carries its own Accent layer (rendered as-is — no double-tint); a glyph
        // fallback picks up the fg tint (neutral TextPrimary, or AccentDefault when checkedOn e.g. the filled heart).
        Element iconEl = IconView.Render(iconRef, iconSize, glyphColor: fg,
            pressedColor: !enabled ? fg : pill ? Tok.TextOnAccentSecondary : checkedOn ? Tok.AccentDefault : Tok.TextSecondary,
            disabledColor: Tok.TextDisabled, enabled: () => enabled, onAccent: pill);

        Element[] content = LabeledPrimary
            ? new Element[]
              {
                  // FIXED 20px icon box so a layered themed stack and a font glyph occupy the same footprint — no
                  // optical size drift between icons in the strip (the accent Play triangle no longer reads larger
                  // than the heart glyph next to it).
                  new BoxEl { Width = 20f, Height = 20f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [iconEl] },
                  // Single-line Caption label centred under the icon; a long label ellipsizes inside its equal
                  // column (Shrink=1 + CharacterEllipsis, clipped) rather than widening the button/menu.
                  new BoxEl
                  {
                      AlignSelf = FlexAlign.Stretch, Direction = 0, Justify = FlexJustify.Center,
                      ClipToBounds = true, Margin = new Edges4(0, 4, 0, 0),
                      Children =
                      [
                          new TextEl(cmd.Label)
                          {
                              Size = 12f, Trim = TextTrim.CharacterEllipsis, Shrink = 1f,
                              Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                          },
                      ],
                  },
              }
            : new Element[] { iconEl };

        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            MinWidth = LabeledPrimary ? LabeledPrimaryBtnMinWidth : AppBarBtnMinWidth,
            MinHeight = LabeledPrimary ? LabeledPrimaryRowHeight - 6f : PrimaryRowHeight,
            // Equal-width columns (Explorer parity): flex basis 0 + grow 1 so every strip button gets an identical
            // slice of the row, capped by MinWidth 64. Basis 0 means a button contributes 0 to the row's NATURAL
            // width, so one long label can't widen the strip — the menu width stays max(overflow content, MinWidth).
            Basis = LabeledPrimary ? 0f : float.NaN,
            Grow = LabeledPrimary ? 1f : 0f,
            ClipToBounds = LabeledPrimary,
            Padding = LabeledPrimary ? new Edges4(4, 4, 4, 4) : default,
            Margin = new Edges4(2, 2, 2, 2),    // CommandBarFlyoutAppBarButtonInnerBorderMargin (:107)
            Corners = Radii.ControlAll,
            Fill = pill ? Tok.AccentDefault : ColorF.Transparent,
            HoverFill = !enabled ? ColorF.Transparent : pill ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = !enabled ? ColorF.Transparent : pill ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
            // AppBarButtonInnerBorder BackgroundTransition = BrushTransition Duration 0:0:0.083 (:282).
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            BrushTransitionMs = Motion.ControlFaster,
            Accelerator = cmd.Accelerator,
            // Standalone CommandBarFlyout primary commands stay open (WinUI closeFlyoutFunc is secondary-only,
            // CommandBarFlyout.cpp:67-74). The Explorer context-menu shape (LabeledPrimary) dismisses on every tap.
            OnClick = enabled ? () => { cmd.Invoke?.Invoke(); if (LabeledPrimary) _requestClose?.Invoke(); } : null,
            OnKeyDown = enabled ? a => onKey(idx, a) : null,
            HitTestVisible = enabled,
            IsEnabled = enabled,
            Focusable = enabled,
            OnRealized = h => { if ((uint)idx < (uint)_primaryNodes.Length) _primaryNodes[idx] = h; },
            Role = isToggle ? AutomationRole.ToggleButton : AutomationRole.Button,
            Children = content,
        };
    }

    // ── A 1px vertical divider between adjacent LabeledPrimary strip buttons (Explorer's Cut|Copy|Rename|Share
    //    dividers). DividerStrokeColorDefault, inset ~12px top/bottom so it spans the icon+label height without
    //    touching the row edges (the AppBarSeparator vertical idiom: 1px wide, stretch, 0.5 corner radius). ───────
    static BoxEl LabeledStripDivider() => new BoxEl
    {
        Width = 1f,
        Fill = Tok.StrokeDividerDefault,           // AppBarSeparatorForeground = DividerStrokeColorDefaultBrush
        Margin = new Edges4(0, 12, 0, 12),         // ~12px top/bottom inset (Explorer icon+label span)
        AlignSelf = FlexAlign.Stretch,
        Corners = CornerRadius4.All(0.5f),         // AppBarSeparatorCornerRadius
    };

    // ── The trailing … ellipsis toggle (EllipsisButton: Width 44, glyph E712 @16, inner margin 2,2,6,2 —
    //    CommandBarFlyout_themeresources.xaml:563-624, :108). The width-expansion storyboard glides this node's
    //    TranslateX delta/2 → 0 (MoreButtonTransform, :772-775). ────────────────────────────────────────────────
    Element MoreButton(Action toggle, Action<int, KeyEventArgs> onKey)
    {
        Action<NodeHandle> capture = h => _moreNode = h;
        var box = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Width = MoreButtonWidth,
            MinHeight = PrimaryRowHeight,
            Margin = new Edges4(2, 2, 6, 2),
            Corners = Radii.ControlAll,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            Focusable = true,
            OnClick = toggle,
            OnKeyDown = a => onKey(Primary.Count, a),
            OnRealized = capture,
            Role = AutomationRole.Button,
            Children =
            [
                new TextEl(Icons.More) { Size = 16f, Color = Tok.TextPrimary, PressedColor = Tok.TextSecondary, FontFamily = Theme.IconFont },
            ],
        };
        var m = Parts.Apply(CommandBarFlyout.PartMoreButton, box);
        return m with
        {
            OnClick = toggle,
            Role = AutomationRole.Button,
            OnKeyDown = box.OnKeyDown,
            OnRealized = TemplateParts.Chain(capture, m.OnRealized),
        };
    }

    // ── OverflowRegion (WinUI OverflowContentRoot / CommandBarOverflowPresenter): MinWidth 136 / MaxWidth 440 /
    //    MaxHeight 480 with vertical auto-scroll (CommandBarFlyout_themeresources.xaml:530-537), rows inside the 3px
    //    ItemsPresenter inset (:556). The expand/collapse clip storyboards drive this node. ─────────────────────
    Element BuildOverflow(bool expandUp, bool secondaryOnly, Action<int, KeyEventArgs> onRowKey)
    {
        bool hasIconColumn = false, hasCheckColumn = false;
        for (int i = 0; i < Secondary.Count; i++)
        {
            var c = Secondary[i];
            if (c.Kind == AppBarCommandKind.Separator) continue;
            if (c.Kind == AppBarCommandKind.ToggleButton) hasCheckColumn = true;
            if (c.Icon.HasContent) hasIconColumn = true;
        }

        var rows = new List<Element>(Secondary.Count);
        for (int j = 0; j < Secondary.Count; j++)
        {
            var cmd = Secondary[j];
            rows.Add(cmd.Kind == AppBarCommandKind.Separator
                ? OverflowSeparator()
                : OverflowRow(cmd, j, hasIconColumn, hasCheckColumn, onRowKey));
        }

        var column = new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(3, 3, 3, 3),   // ItemsPresenter Margin="3" (:556)
            Children = rows.ToArray(),
        };

        Action<NodeHandle> regionCapture = h => { if (_overflowNode is { } r) r.Value = h; };
        var region = new BoxEl
        {
            Direction = 1,
            MinWidth = OverflowMinWidth,
            MaxWidth = OverflowMaxWidth,
            // ExpandedDown joins the primary row above (bottom corners kept, BottomCornerRadiusFilterConverter
            // :980-981); ExpandedUp joins it below (top corners kept, :971-972); secondary-only = a plain menu,
            // all corners rounded.
            Corners = secondaryOnly ? Radii.OverlayAll : expandUp ? Radii.OverlayTop : Radii.OverlayBottom,
            ClipToBounds = true,
            OnRealized = regionCapture,         // the expand/collapse clip storyboards drive this node
            Children =
            [
                // CommandBarOverflowPresenter ScrollViewer: MaxHeight 480, VerticalScrollMode/Visibility Auto
                // (:532-536) — short menus size to their rows, tall ones scroll.
                new ScrollEl { Content = column, ContentSized = true, MaxHeight = OverflowMaxHeight },
            ],
        };
        // Parts: restyle the overflow chrome; the storyboard node capture, the clip and the rows always win.
        if (Parts is { } op)
        {
            var m = op.Apply(CommandBarFlyout.PartOverflow, region);
            region = m with { ClipToBounds = true, Children = region.Children, OnRealized = TemplateParts.Chain(regionCapture, m.OnRealized) };
        }
        return region;
    }

    static Element OverflowSeparator() => AppBarSeparator.Create(overflow: true);

    Element OverflowRow(AppBarCommand cmd, int idx, bool hasIconColumn, bool hasCheckColumn, Action<int, KeyEventArgs> onRowKey)
    {
        bool isToggle = cmd.Kind == AppBarCommandKind.ToggleButton;
        bool isChecked = isToggle && cmd.IsChecked;
        bool enabled = cmd.Enabled;
        bool hasSub = cmd.Flyout is { Count: > 0 };
        // V2 overflow rows NEVER paint the accent pill: OverflowChecked sets ONLY OverflowCheckGlyph.Opacity=1
        // (CommandBarFlyout_themeresources.xaml:434-438); OverflowCheckedPointerOver/Pressed reuse the Subtle
        // BackgroundPointerOver/Pressed with TextFillColorPrimary foreground (:439-458); CheckedDisabled = disabled
        // foregrounds + the visible glyph, still no pill (:407-416).
        ColorF fg = enabled ? Tok.TextPrimary : Tok.TextDisabled;
        ColorF pressedFg = enabled ? Tok.TextSecondary : Tok.TextDisabled;   // ForegroundPressed = TextFillColorSecondary (:14)

        // Metrics note: WinUI's overflow ladder is authored in ROW coordinates (the InnerBorder plate is a 2px-inset
        // sibling, :107/:280) while our row box IS the plate — plate-local lefts/rights below are the WinUI values −2
        // so the absolute leads match: text lead 12 plain / 39 with toggles / 67 with toggles+icons (:301/:152/:180).
        var children = new List<Element>(5);
        // Check glyph: E73E @12, Margin 15,4,14,4 row-coords (:505) ⇒ 13,4,12,4 plate-local (13+12+12 = the 37 ⇒ 39
        // lead); TouchInputMode → 12,10,12,10 (:465) ⇒ 10,10,15,10. Painted when checked (even disabled, :414).
        if (hasCheckColumn)
        {
            Element check = isChecked
                ? new TextEl(Icons.Accept) { Size = 12f, Color = fg, PressedColor = pressedFg, DisabledColor = Tok.TextDisabled, FontFamily = Theme.IconFont }
                : new BoxEl { Width = 12f };
            children.Add(new BoxEl
            {
                Margin = TouchInputMode ? new Edges4(10, 10, 15, 10) : new Edges4(13, 4, 12, 4),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Center,
                Children = [check],
            });
        }
        // Menu icon: 16×16 ContentViewbox — Margin 12,0,12,0 alone (text lead 39, :163/:166) or led by the toggle
        // column (Margin 39,0,12,0 → text lead 67, :177/:180); plate-local −2 on the outer edges.
        if (hasIconColumn)
        {
            bool iconEnabled = cmd.Enabled;
            Element icon = IconView.Render(cmd.Icon, 16f, glyphColor: fg, pressedColor: pressedFg,
                disabledColor: Tok.TextDisabled, enabled: () => iconEnabled);
            children.Add(new BoxEl
            {
                Width = 16f, Height = 16f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Center,
                Margin = hasCheckColumn ? new Edges4(0, 0, 12, 0) : new Edges4(10, 0, 11, 0),
                Children = [icon],
            });
        }
        bool hasTrailing = cmd.AcceleratorText is { Length: > 0 } || hasSub;
        // OverflowTextLabel: Body 14, Padding 0,6,0,7 (:301); TouchInputMode → 0,9,0,11 (:243/:464); Margin
        // 12,0,12,0 row-coords when no leading columns (the 12/39/67 ladder, :301/:152/:180).
        children.Add(new BoxEl
        {
            Grow = 1f,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = TouchInputMode ? new Edges4(0, 9, 0, 11) : new Edges4(0, 6, 0, 7),
            Margin = new Edges4((hasCheckColumn || hasIconColumn) ? 0f : 10f, 0, hasTrailing ? 0f : 10f, 0),
            Children = [new TextEl(cmd.Label) { Size = 14f, Color = fg, PressedColor = pressedFg, DisabledColor = Tok.TextDisabled, Trim = TextTrim.Clip }],
        });
        // KeyboardAcceleratorTextLabel: Caption 12, right-aligned, Margin 24,0,12,0, TextSecondary→pressed Tertiary
        // ramp (:302 + CommandBarFlyoutAppBarButtonKeyboardTextLabelForeground* :16-18).
        if (cmd.AcceleratorText is { Length: > 0 } acc)
            children.Add(new TextEl(acc)
            {
                Size = 12f, Margin = new Edges4(24, 0, hasSub ? 12f : 10f, 0), AlignSelf = FlexAlign.Center,
                Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                PressedColor = enabled ? Tok.TextTertiary : Tok.TextDisabled,
                DisabledColor = Tok.TextDisabled,
            });
        // SubItemChevron: E76C @12, Margin 12,0,12,0 (:303), SubItemChevron foreground ramp (:19-23).
        if (hasSub)
            children.Add(new TextEl(Icons.ChevronRight)
            {
                Size = 12f, FontFamily = Theme.IconFont, Margin = new Edges4(12, 0, 10, 0), AlignSelf = FlexAlign.Center,
                Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                PressedColor = enabled ? Tok.TextTertiary : Tok.TextDisabled,
                DisabledColor = Tok.TextDisabled,
            });

        int j = idx;
        return new BoxEl
        {
            Direction = 0,
            MinHeight = 0f,
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(2, 2, 2, 2),    // the InnerBorder plate inset (InnerBorderMargin = 2, :107)
            Corners = Radii.ControlAll,
            Role = isToggle ? AutomationRole.ToggleButton : AutomationRole.MenuItem,
            // Subtle ramp only — checked rows reveal the glyph, never an accent plate (OverflowChecked :434-438;
            // OverflowCheckedPointerOver/Pressed reuse BackgroundPointerOver/Pressed :443/:453).
            Fill = ColorF.Transparent,
            HoverFill = enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = enabled ? Tok.FillSubtleTertiary : ColorF.Transparent,
            BrushTransitionMs = Motion.ControlFaster,   // 83ms BrushTransition on the row background (:282)
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            Accelerator = cmd.Accelerator,
            OnClick = enabled ? () => RunSecondary(cmd, j) : null,
            OnKeyDown = enabled ? a => onRowKey(j, a) : null,
            HitTestVisible = enabled,
            IsEnabled = enabled,
            Focusable = enabled,
            OnRealized = h => { if ((uint)j < (uint)_secondaryNodes.Length) _secondaryNodes[j] = h; },
            Children = children.ToArray(),
        };
    }

    // Secondary rows: a command WITH a Flyout opens its cascading sub-menu and must NOT close the parent
    // (CommandBarFlyout.cpp:95-108 — the Hide() call is gated on !button.Flyout()); plain buttons AND toggles close —
    // closeFlyoutFunc = Hide() is hooked on Click, Checked AND Unchecked alike (cpp:97/:104-105, :414-419).
    void RunSecondary(AppBarCommand cmd, int idx)
    {
        if (cmd.Flyout is { Count: > 0 } subItems)
        {
            if (subHandleLive is { IsOpen: true } s) { s.Close(); subHandleLive = null; }
            var svc2 = lastService;
            if (svc2 is null) return;
            int j = idx;
            subHandleLive = svc2.Open(
                () => (uint)j < (uint)_secondaryNodes.Length ? _secondaryNodes[j] : default,
                // invoking a sub item closes the WHOLE chain (through the 83ms closing fade)
                () => MenuFlyout.Build(subItems, () => _requestClose?.Invoke()),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(Chrome: PopupChrome.Flyout) { ConstrainToRootBounds = false });
            return;
        }
        cmd.Invoke?.Invoke();
        _requestClose?.Invoke();
    }

    // Cascading sub-flyout plumbing + node captures: UseContext/UseRef are render-scoped; instance fields let the
    // row builders — instance methods — reach them.
    IOverlayService? lastService;
    OverlayHandle? subHandleLive;
    Ref<NodeHandle>? _overflowNode;
    NodeHandle[] _primaryNodes = [];
    NodeHandle[] _secondaryNodes = [];
    NodeHandle _moreNode;
    NodeHandle _rootNode;
    Action? _requestClose;
}
