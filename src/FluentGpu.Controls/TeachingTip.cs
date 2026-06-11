using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>TeachingTip</c> (controls/dev/TeachingTip/TeachingTip.xaml + _themeresources + .cpp/.idl): an anchored
/// (targeted) or window-relative (untargeted) non-modal callout that teaches/highlights a feature. The body is a SOLID
/// surface — <c>SolidBackgroundFillColorTertiary</c> background, <c>SurfaceStrokeColorDefault</c> 1px border,
/// <c>OverlayCornerRadius</c> (8), an elevation shadow, and the 1px <c>TeachingTipTopHighlightBrush</c> top highlight
/// (#0DFFFFFF dark / #99FFFFFF light, TeachingTip_themeresources.xaml:6/:28) — with (top→bottom) an optional hero
/// image, an icon + title + subtitle row, the main content, and a footer button panel (Action + Close). A targeted tip
/// grows a 16×8 tail/beak that points at the target; an untargeted tip has no tail. NOT light-dismiss by default
/// (<c>IsLightDismissEnabled=false</c>): only the close button, Escape, or a programmatic close dismisses it.
///
/// The FULL 13-placement matrix (TeachingTip.xaml PlacementStates :115-264) is honored — Top/Bottom/Left/Right,
/// the 8 corner variants, and Center — each mapping to the matching FlyoutPositioner placement with the tail pinned
/// to the joined edge per the state's alignment setters (e.g. TopRight → tail at the LEFT of the bottom edge,
/// :160-170). <see cref="PlacementMargin"/> nudges the tip away from the target (TeachingTip.cpp:480/:588 offset);
/// <see cref="ShouldConstrainToRootBounds"/> (default true, the WinUI idl default) opts the popup out of the window
/// bounds via the E4 windowed-popup seam when false.
///
/// 1:1 tokens/sizes (TeachingTip_themeresources.xaml, unitless = epx):
///  • MinWidth 320, MaxWidth 336, MinHeight 40, MaxHeight 520 (TeachingTipMin/MaxWidth/Height).
///  • Content margin 12 on all sides (TeachingTipContentMargin); button panel top margin 0,12,0,0
///    (TeachingTipButtonPanelMargin); both-buttons split — Action 0,12,4,0 (Left), Close 4,12,0,0 (Right).
///  • MainContent present-margin 0,12,0,0 (gap below the title block when content exists); icon→title gap 12
///    (TeachingTipIconPresenterMarginWithIcon); title-stack right margin 28 when the header (alternate) close button shows.
///  • Title FontSize 16 SemiBold; Subtitle FontSize 14; body content FontSize 14 (Foreground = TextPrimary).
///  • Alternate (header) close button: 40×40 (TeachingTipAlternateCloseButtonSize), E711 glyph at 16px, SubtleFill
///    ramp, ControlCornerRadius. The footer ActionButton's DEFAULT style is the STANDARD button
///    (TeachingTip.xaml:10 ActionButtonStyle = DefaultButtonStyle) — set <see cref="ActionButtonIsAccent"/> for the
///    AccentButtonStyle the gallery samples use.
///
/// Open/close motion (host-driven, PopupChrome.TeachingTip): expand scale → 1 over 300ms cubic-bezier(0.1,0.9,0.2,1);
/// contract scale → 20/Width over 200ms cubic-bezier(0.7,0,1,0.5) (TeachingTip.cpp:1660-1712; .h:234-235 +
/// :304-307 control points) — both at full opacity (the storyboards carry no opacity keyframes).
/// </summary>
public sealed class TeachingTip : Component
{
    /// <summary>WinUI <c>TeachingTipCloseReason</c> — surfaced on Closing/Closed.</summary>
    public enum CloseReason : byte { CloseButton, LightDismiss, Programmatic }

    /// <summary>WinUI <c>TeachingTipPlacementMode</c> — all 13 modes are positioned (TeachingTip.xaml PlacementStates).</summary>
    public enum PlacementMode : byte { Auto, Top, Bottom, Left, Right, TopRight, TopLeft, BottomRight, BottomLeft, LeftTop, LeftBottom, RightTop, RightBottom, Center }

    /// <summary>WinUI <c>TeachingTipTailVisibility</c>.</summary>
    public enum TailVisibilityMode : byte { Auto, Visible, Collapsed }

    /// <summary>WinUI <c>TeachingTipHeroContentPlacementMode</c>.</summary>
    public enum HeroPlacement : byte { Auto, Top, Bottom }

    /// <summary>Which tip edge carries the tail (derived from the placement: the edge that faces the target).</summary>
    internal enum TailSide : byte { None, Top, Bottom, Left, Right }

    // ── Template parts (see TemplateParts) — each const's doc lists the props the control OWNS (re-asserted
    // after any modifier — a Parts customization cannot win those). ──────────────────────────────────────────
    /// <summary>The solid callout card (WinUI ContentRootGrid): fill, border, corners, shadow, min/max sizes…
    /// The tail/beak and the 1px top highlight are composed AGAINST the card OUTSIDE this part (tail mechanics
    /// stay control-owned — restyling the card fill does not recolor the beak). Owned: Children (the
    /// hero · content · footer structure).</summary>
    public const string PartBubble = "Bubble";
    /// <summary>The title text — a TextEl part: customize via
    /// <c>Parts.Set&lt;TextEl&gt;(TeachingTip.PartTitle, t =&gt; t with { … })</c>. Owned: none (pure styling).</summary>
    public const string PartTitle = "Title";
    /// <summary>Whichever close button is in use: the 40×40 header (alternate) close button when
    /// <see cref="CloseButtonContent"/> is empty, else the labelled footer close (WinUI AlternateCloseButton /
    /// CloseButton). Owned: OnClick (the Closing→Closed pipeline), Role.</summary>
    public const string PartCloseButton = "CloseButton";
    /// <summary>The full-bleed hero banner wrapper (WinUI HeroContentPresenter host). Owned: Children (the
    /// <see cref="HeroContent"/>/<see cref="HeroImage"/> slot).</summary>
    public const string PartHero = "Hero";

    // ── Content model (TeachingTip.idl) ─────────────────────────────────────────────────────────────────────
    /// <summary>Optional convenience trigger button label — WinUI's TeachingTip has NO trigger UI of its own (it is
    /// opened via <c>IsOpen</c> only, TeachingTip.idl); empty (the default) renders no trigger. Set it for the
    /// gallery-style "button that shows the tip" composition (the wrapper then doubles as the anchor).</summary>
    public string TriggerLabel = "";
    public string Title = "";
    public string Subtitle = "";
    /// <summary>Body content (WinUI <c>Content</c>). When empty, the MainContentPresenter collapses (NoContent VSM).</summary>
    public string Body = "";
    /// <summary>Glyph icon (Segoe Fluent Icons codepoint) shown left of the title (WinUI <c>IconSource</c>). Empty = none.</summary>
    public string IconGlyph = "";
    /// <summary>Hero image source (WinUI <c>HeroContent</c>): a full-bleed banner pinned to the top or bottom edge.</summary>
    public string HeroImage = "";
    /// <summary>Arbitrary hero content slot (overrides <see cref="HeroImage"/> when set) — WinUI HeroContent is a
    /// UIElement, not just an image.</summary>
    public Element? HeroContent;
    public HeroPlacement HeroContentPlacement = HeroPlacement.Auto;

    // ── Buttons (TeachingTip.idl). An empty string collapses that button (ButtonsStates VSM keys on content presence). ──
    /// <summary>WinUI <c>ActionButtonContent</c> — the footer action button. Empty = collapsed.</summary>
    public string ActionButtonContent = "";
    public Action? ActionButtonClick;
    /// <summary>The DEFAULT ActionButtonStyle is the standard button (TeachingTip.xaml:10); true opts into
    /// AccentButtonStyle (the common app override).</summary>
    public bool ActionButtonIsAccent = false;
    /// <summary>WinUI <c>CloseButtonContent</c> — the standard footer close button. Empty = the close moves to the 40×40
    /// header (alternate) close button (WinUI HeaderCloseButton VSM); set it to show a labelled footer close instead.</summary>
    public string CloseButtonContent = "";
    public Action? CloseButtonClick;

    // ── Behavior (TeachingTip.idl) ───────────────────────────────────────────────────────────────────────────
    /// <summary>WinUI <c>IsLightDismissEnabled</c> (default false). When true, clicking outside dismisses the tip
    /// (and the surface swaps to the transient acrylic background — the LightDismiss VSM, TeachingTip.xaml:19-26).</summary>
    public bool IsLightDismissEnabled = false;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real tip after first mount
    /// <summary>WinUI <c>Boolean IsOpen</c> (TeachingTip.idl, MUX_DEFAULT_VALUE false) as a CONTROLLED two-way signal:
    /// when set, writes from anywhere open/close the tip with the full expand/contract motion, and EVERY close path
    /// (close button, light dismiss, Escape, programmatic) writes false back — WinUI's OnCloseButtonClicked and its
    /// light-dismiss handler both end in <c>IsOpen(false)</c> (TeachingTip.cpp:1271-1276, :1382-1388). A cancelled
    /// Closing restores true (the WinUI deferral revert). Null = open via the trigger/<see cref="OpenOnMount"/> only.</summary>
    public Signal<bool>? IsOpen;
    /// <summary>WinUI <c>FrameworkElement Target</c> (TeachingTip.idl) as the engine's anchor-thunk seam: the node the
    /// targeted tip points at (capture it via <see cref="BoxEl.OnRealized"/> on the target element). Null with
    /// <see cref="HasTarget"/>=true anchors to this component's own wrapper/trigger (the pre-Target compat behavior);
    /// <see cref="HasTarget"/>=false ignores it (untargeted window-edge placement).</summary>
    public Func<NodeHandle>? Target;
    /// <summary>WinUI <c>PreferredPlacement</c> (default Auto = Top for a targeted tip).</summary>
    public PlacementMode PreferredPlacement = PlacementMode.Auto;
    public TailVisibilityMode TailVisibility = TailVisibilityMode.Auto;
    /// <summary>WinUI <c>PlacementMargin</c> (idl:119): extra distance between the target and the tip, applied on the
    /// placement axis (TeachingTip.cpp:480/:588 read it as the offset).</summary>
    public float PlacementMargin = 0f;
    /// <summary>WinUI <c>ShouldConstrainToRootBounds</c> (default TRUE — the tip stays inside the window). False →
    /// the popup goes windowed via the E4 seam and may escape the window.</summary>
    public bool ShouldConstrainToRootBounds = true;
    /// <summary>WinUI <c>Target</c> presence. When false the tip is untargeted (no beak unless
    /// <see cref="TailVisibility"/> forces one) and is placed against the WINDOW edge — WinUI
    /// PositionUntargetedPopup: default Bottom of the window, <see cref="UntargetedEdgeMargin"/> = 24px from the
    /// edges, <see cref="PreferredPlacement"/> picking the corner/edge (TeachingTip.cpp:571-668, :2074-2090).</summary>
    public bool HasTarget = true;

    // ── Events (TeachingTip.idl: Opened / Closing(+Cancel,+Deferral) / Closed) ───────────────────────────────
    public Action? Opened;
    /// <summary>WinUI <c>Closing</c> — fires before close with the reason; setting <see cref="ClosingEventArgs.Cancel"/>
    /// aborts the close (the synchronous slice of the deferral pipeline).</summary>
    public Action<ClosingEventArgs>? Closing;
    public Action<CloseReason>? Closed;

    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary>WinUI <c>TeachingTipClosingEventArgs</c> (synchronous Cancel slice; async Deferral is deep-deferred).</summary>
    public sealed class ClosingEventArgs
    {
        public CloseReason Reason { get; }
        public bool Cancel { get; set; }
        internal ClosingEventArgs(CloseReason reason) => Reason = reason;
    }

    // ── WinUI sizing/spacing/timing constants (TeachingTip_themeresources.xaml + .cpp) ───────────────────────
    const float MinW = 320f, MaxW = 336f;     // TeachingTipMin/MaxWidth
    const float MinH = 40f, MaxH = 520f;       // TeachingTipMin/MaxHeight
    const float ContentMargin = 12f;           // TeachingTipContentMargin (all sides)
    const float ButtonPanelTop = 12f;          // TeachingTipButtonPanelMargin 0,12,0,0
    const float BothButtonsGap = 8f;           // TeachingTipLeftButtonMargin 0,12,4,0 + RightButtonMargin 4,12,0,0 ⇒ 4+4 gap
    const float MainContentTop = 12f;          // TeachingTipMainContentPresentMargin 0,12,0,0
    const float IconGap = 12f;                 // TeachingTipIconPresenterMarginWithIcon 0,0,12,0
    const float HeaderCloseInset = 28f;        // TeachingTipTitleStackPanelMarginWithHeaderCloseButton 0,0,28,0
    const float TitleSize = 16f;               // Title FontSize (SemiBold)
    const float SubtitleSize = 14f;            // Subtitle FontSize
    const float BodySize = 14f;                // ControlContentThemeFontSize
    const float IconSize = 16f;
    const float AltCloseSize = 40f;            // TeachingTipAlternateCloseButtonSize
    const float AltCloseGlyph = 16f;           // TeachingTipAlternateCloseButtonGlyphSize
    internal const float TailLong = 16f;       // TailLongSideLength (the rotated-square diagonal footprint)
    internal const float TailShort = 8f;       // TailShortSideLength (occluded height; the visible beak)

    /// <summary>TeachingTipTopHighlightBrush — the decorative 1px top edge highlight
    /// (TeachingTip_themeresources.xaml:6 dark #0DFFFFFF / :28 light #99FFFFFF).</summary>
    internal static ColorF TopHighlight => Tok.Theme == ThemeKind.Light
        ? ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x99)
        : ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0D);

    /// <summary>Embed a TeachingTip with a trigger label, title, and body (kept call-site-compatible with the prior API).</summary>
    public static Element Create(string triggerLabel, string title, string body) =>
        Embed.Comp(() => new TeachingTip { TriggerLabel = triggerLabel, Title = title, Body = body });

    // PlacementStates matrix (TeachingTip.xaml:115-264): mode → (positioner placement, tail edge, tail pin).
    // The tail's HorizontalAlignment/VerticalAlignment setter in each state dictates which popup edge segment the
    // beak pins to: Center (the anchor-tracked default), or the near/far end for the corner variants.
    internal enum TailPin : byte { AnchorCenter, Near, Far }

    static (FlyoutPlacement Placement, TailSide Side, TailPin Pin) Map(PlacementMode m) => m switch
    {
        PlacementMode.Top => (FlyoutPlacement.Top, TailSide.Bottom, TailPin.AnchorCenter),                 // tail bottom-center (:116)
        PlacementMode.Bottom => (FlyoutPlacement.Bottom, TailSide.Top, TailPin.AnchorCenter),              // tail top-center (:127)
        PlacementMode.Left => (FlyoutPlacement.Left, TailSide.Right, TailPin.AnchorCenter),                // tail right-center (:138)
        PlacementMode.Right => (FlyoutPlacement.Right, TailSide.Left, TailPin.AnchorCenter),               // tail left-center (:149)
        PlacementMode.TopRight => (FlyoutPlacement.TopEdgeAlignedLeft, TailSide.Bottom, TailPin.Near),     // tail bottom-LEFT (:160-170)
        PlacementMode.TopLeft => (FlyoutPlacement.TopEdgeAlignedRight, TailSide.Bottom, TailPin.Far),      // tail bottom-RIGHT (:171-181)
        PlacementMode.BottomRight => (FlyoutPlacement.BottomEdgeAlignedLeft, TailSide.Top, TailPin.Near),  // tail top-LEFT (:182-192)
        PlacementMode.BottomLeft => (FlyoutPlacement.BottomEdgeAlignedRight, TailSide.Top, TailPin.Far),   // tail top-RIGHT (:193-203)
        PlacementMode.LeftTop => (FlyoutPlacement.LeftEdgeAlignedBottom, TailSide.Right, TailPin.Far),     // tail right-BOTTOM (:204-215)
        PlacementMode.LeftBottom => (FlyoutPlacement.LeftEdgeAlignedTop, TailSide.Right, TailPin.Near),    // tail right-TOP (:216-226)
        PlacementMode.RightTop => (FlyoutPlacement.RightEdgeAlignedBottom, TailSide.Left, TailPin.Far),    // tail left-BOTTOM (:227-237)
        PlacementMode.RightBottom => (FlyoutPlacement.RightEdgeAlignedTop, TailSide.Left, TailPin.Near),   // tail left-TOP (:238-248)
        PlacementMode.Center => (FlyoutPlacement.Top, TailSide.Bottom, TailPin.AnchorCenter),              // tail bottom-center, tip over the target CENTER (:249-259)
        _ => (FlyoutPlacement.Top, TailSide.Bottom, TailPin.AnchorCenter),                                 // Auto = Top for targeted tips
    };

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);
        var closeReason = UseRef(CloseReason.Programmatic);
        var autoOpened = UseRef(false);

        bool hasTitle = Title.Length > 0;
        bool hasSubtitle = Subtitle.Length > 0;
        bool hasBody = Body.Length > 0;
        bool hasIcon = IconGlyph.Length > 0;
        bool hasHero = HeroContent is not null || HeroImage.Length > 0;
        bool hasAction = ActionButtonContent.Length > 0;
        bool hasFooterClose = CloseButtonContent.Length > 0;

        // WinUI CloseButtonLocations VSM (TeachingTip.xaml:75-87): the footer close shows iff CloseButtonContent is
        // set; otherwise the 40×40 header (alternate) close button — button-content presence ONLY, independent of
        // IsLightDismissEnabled (the audit's conflation fix; light dismiss affects DISMISSAL, not button location).
        bool footerCloseUsed = hasFooterClose;
        bool headerCloseUsed = !footerCloseUsed;

        var (flyoutPlacement, tailSide, tailPin) = Map(PreferredPlacement);

        // Tail is shown for a targeted tip unless explicitly collapsed; never for an untargeted tip (Untargeted VSM).
        bool tailVisible = HasTarget && TailVisibility != TailVisibilityMode.Collapsed;
        if (!HasTarget) tailSide = TailSide.None;

        CloseReason ReasonFor(OverlayCloseCause cause) => cause switch
        {
            OverlayCloseCause.LightDismiss => CloseReason.LightDismiss,
            OverlayCloseCause.Escape => CloseReason.Programmatic,
            _ => closeReason.Value,
        };

        bool BeforeHostClose(OverlayCloseCause cause)
        {
            var reason = ReasonFor(cause);
            var args = new ClosingEventArgs(reason);
            Closing?.Invoke(args);
            if (args.Cancel) return false;
            closeReason.Value = reason;
            return true;
        }

        void AfterHostClosed(OverlayCloseCause cause)
        {
            var reason = ReasonFor(cause);
            h.Value = null;
            Closed?.Invoke(reason);
        }

        // The reason→event close pipeline. WinUI: RaiseClosingEvent(reason) → (unless Cancelled) ClosePopup → Closed(reason).
        void RequestClose(CloseReason reason)
        {
            if (h.Value is not { IsOpen: true }) return;
            closeReason.Value = reason;
            h.Value.Close();
        }

        void Toggle()
        {
            if (h.Value is { IsOpen: true }) { RequestClose(CloseReason.Programmatic); return; }

            // IsLightDismissEnabled=false ⇒ DismissBehavior.None (no click-outside; only the close button / Escape).
            var dismiss = IsLightDismissEnabled ? DismissBehavior.LightDismiss : DismissBehavior.None;
            closeReason.Value = CloseReason.Programmatic;
            var options = new PopupOptions(FocusTrap: false, DismissBehavior: dismiss, Chrome: PopupChrome.TeachingTip)
            { ConstrainToRootBounds = ShouldConstrainToRootBounds };
            // The surface is a placement-aware component: a VERTICAL tail follows the EFFECTIVE placement (the
            // positioner's fallback may flip Top↔Bottom — WinUI's DetermineEffectivePlacement re-targets the tail
            // edge the same way), read live from the host's placement signal.
            Func<Element> content = () => Embed.Comp(() => new TeachingTipSurfaceHost
            {
                Owner = this,
                HasTitle = hasTitle, HasSubtitle = hasSubtitle, HasBody = hasBody, HasIcon = hasIcon,
                HasHero = hasHero, HasAction = hasAction, HasFooterClose = hasFooterClose,
                FooterCloseUsed = footerCloseUsed, HeaderCloseUsed = headerCloseUsed,
                TailVisible = tailVisible, RequestedSide = tailSide, Pin = tailPin,
                RequestClose = RequestClose,
            });

            float margin = PlacementMargin;
            bool centerMode = PreferredPlacement == PlacementMode.Center;
            if (margin > 0f || centerMode)
            {
                // PlacementMargin / Center mode anchor to a DERIVED rect: the target rect inflated by the margin on
                // the placement axis (cpp:480/:588), or the target's center POINT for Center (:249-259 — the tail
                // points at the target middle).
                var node = anchor.Value;
                h.Value = svc.OpenAt(
                    () =>
                    {
                        var scene = Context.Scene;
                        RectF r = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                        if (centerMode) r = new RectF(r.X + r.W * 0.5f, r.Y + r.H * 0.5f, 0f, 0f);
                        return margin > 0f ? new RectF(r.X - margin, r.Y - margin, r.W + margin * 2f, r.H + margin * 2f) : r;
                    },
                    content, flyoutPlacement, options, owner: () => anchor.Value);
            }
            else
            {
                h.Value = svc.Open(() => anchor.Value, content, flyoutPlacement, options);
            }
            h.Value.ClosingAction = BeforeHostClose;
            h.Value.ClosedWithCauseAction = AfterHostClosed;

            Opened?.Invoke();   // WinUI raises Opened once the popup is shown (synchronous slice)
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            Toggle();
        }, OpenOnMount);

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            Role = AutomationRole.Button,
            OnRealized = x => anchor.Value = x,
            Children = [Button.Accent(TriggerLabel, Toggle)],
        };
    }

    /// <summary>Placement-aware surface wrapper: re-renders off the host's placement signal so a vertical tail
    /// re-targets when the positioner's fallback flips the effective side (WinUI DetermineEffectivePlacement).</summary>
    internal sealed class TeachingTipSurfaceHost : Component
    {
        public required TeachingTip Owner;
        public bool HasTitle, HasSubtitle, HasBody, HasIcon, HasHero, HasAction, HasFooterClose;
        public bool FooterCloseUsed, HeaderCloseUsed, TailVisible;
        public TailSide RequestedSide;
        public TailPin Pin;
        public required Action<CloseReason> RequestClose;

        public override Element Render()
        {
            var placement = UseContext(Overlay.Placement);
            var side = RequestedSide;
            if (side is TailSide.Top or TailSide.Bottom && placement is { } sig)
            {
                // OpensUp == the popup resolved ABOVE the anchor → the tail points down from the BOTTOM edge.
                var info = sig.Value;   // subscribe → re-render when the host re-places the popup
                if (info.PopupHeight > 0f) side = info.OpensUp ? TailSide.Bottom : TailSide.Top;
            }
            return Owner.BuildSurface(
                HasTitle, HasSubtitle, HasBody, HasIcon, HasHero, HasAction, HasFooterClose,
                FooterCloseUsed, HeaderCloseUsed, TailVisible, side, Pin, RequestClose);
        }
    }

    // Builds the full TeachingTip surface: [beak] composed against the solid card
    // [hero? · (icon+titles row | header-close) · content · footer], plus the 1px top highlight.
    Element BuildSurface(
        bool hasTitle, bool hasSubtitle, bool hasBody, bool hasIcon, bool hasHero, bool hasAction, bool hasFooterClose,
        bool footerCloseUsed, bool headerCloseUsed, bool tailVisible, TailSide tailSide, TailPin tailPin,
        Action<CloseReason> requestClose)
    {
        // ── Title / subtitle stack (TitlesStackPanel) ──────────────────────────────────────────────────────────
        var titleStack = new List<Element>(2);
        if (hasTitle)
            titleStack.Add(Parts.Apply(PartTitle, new TextEl(Title) { Size = TitleSize, Bold = true, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords }));
        if (hasSubtitle)
            titleStack.Add(new TextEl(Subtitle) { Size = SubtitleSize, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords });

        var titlesPanel = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            // 0,0,28,0 when the header close button overlays the top-right (so titles don't run under it).
            Margin = headerCloseUsed ? new Edges4(0, 0, HeaderCloseInset, 0) : default,
            Children = titleStack.ToArray(),
        };

        // Icon (left) + titles (right) row.
        var headerRowChildren = new List<Element>(2);
        if (hasIcon)
            headerRowChildren.Add(new BoxEl
            {
                Margin = new Edges4(0, 0, IconGap, 0),   // TeachingTipIconPresenterMarginWithIcon
                Children = [Ui.Icon(IconGlyph, IconSize, Tok.TextPrimary)],
            });
        if (titleStack.Count > 0)
            headerRowChildren.Add(titlesPanel);

        // ── Non-hero content column: header row · main content · footer buttons ──────────────────────────────────
        var contentCol = new List<Element>(3);
        if (headerRowChildren.Count > 0)
            contentCol.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Start, Children = headerRowChildren.ToArray() });

        if (hasBody)
            contentCol.Add(new TextEl(Body)
            {
                Size = BodySize,
                Color = Tok.TextPrimary,
                Wrap = TextWrap.Wrap,
                // MainContentPresenter present-margin 0,12,0,0 (gap below the title block) only when a header row exists.
                Margin = headerRowChildren.Count > 0 ? new Edges4(0, MainContentTop, 0, 0) : default,
            });

        // Footer button panel (ButtonsStates VSM). Both → Action(Left, Grow) | Close(Right, Grow) split with the
        // 4+4 margin gap; Action-only → full-width; footer-close-only → full-width standard.
        var footer = BuildFooter(hasAction, hasFooterClose, footerCloseUsed, requestClose);
        if (footer is not null) contentCol.Add(footer);

        // The content region carries the WinUI 12px content margin (TeachingTipContentMargin) as padding.
        var contentRegion = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Padding = Edges4.All(ContentMargin),
            Children = contentCol.ToArray(),
        };

        // HeaderCloseButton VSM: a 40×40 alternate close button pinned to the top-right corner (AlternateCloseButtonStyle:
        // SubtleFill ramp, E711 glyph @16px, ControlCornerRadius). Used when there's no footer close button.
        Element nonHero;
        if (headerCloseUsed)
        {
            void OnHeaderClose() { CloseButtonClick?.Invoke(); requestClose(CloseReason.CloseButton); }
            var headerCloseStyle = IconButton.DefaultStyle with { Size = AltCloseSize, GlyphSize = AltCloseGlyph, CornerRadius = Radii.Control };
            // Restyle via [PartCloseButton]; the close pipeline (OnClick) always wins.
            var headerClose = Parts.Apply(PartCloseButton, IconButton.Create(Icons.Cancel, OnHeaderClose, headerCloseStyle))
                with { OnClick = OnHeaderClose, Role = AutomationRole.Button };
            // ZStack: content under a top-right-pinned 40×40 close button (NonHeroContentRootGrid layering).
            nonHero = new BoxEl
            {
                ZStack = true,
                Grow = 1f,
                Children =
                [
                    contentRegion,
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
                        Children = [headerClose],
                    },
                ],
            };
        }
        else
        {
            nonHero = contentRegion;
        }

        // Hero banner (HeroContent): full-bleed content pinned top (default/Top) or bottom. Squares the corner it abuts.
        var bodyChildren = new List<Element>(4);
        bool heroTop = HeroContentPlacement != HeroPlacement.Bottom;   // Auto/Top → top
        Element? hero = null;
        if (hasHero)
        {
            Element heroInner = HeroContent ?? Ui.Image(HeroImage, MaxW, 100f, 0f);
            var heroBox = new BoxEl
            {
                Width = MaxW,
                Height = HeroContent is null ? 100f : float.NaN,
                ClipToBounds = true,
                Corners = heroTop ? Radii.OverlayTop : Radii.OverlayBottom,
                Children = [heroInner],
            };
            hero = Parts.Apply(PartHero, heroBox) with { Children = heroBox.Children };   // structure = the hero slot
        }

        // The 1px top highlight (TeachingTipTopHighlightBrush): hugs the inside of the top edge, inset past the
        // corner radii; when the TAIL sits on the TOP edge the highlight splits around it (the computed
        // TemplateSettings.TopLeftHighlightMargin/TopRightHighlightMargin pair, TeachingTip.cpp:687-708).
        Element TopHighlightBar() => Embed.Comp(() => new TeachingTipTopHighlight { SplitAroundTail = tailVisible && tailSide == TailSide.Top, TailPin = tailPin });

        if (hero is not null && heroTop) bodyChildren.Add(hero);
        bodyChildren.Add(nonHero);
        if (hero is not null && !heroTop) bodyChildren.Add(hero);

        // ── The solid TeachingTip card (ContentRootGrid): SolidBackgroundFillColorTertiary, SurfaceStroke 1px,
        // OverlayCornerRadius 8, content elevation shadow. ClipToBounds so the hero/fill honor the rounded corners. ──
        var card = new BoxEl
        {
            Direction = 1,
            MinWidth = MinW, MaxWidth = MaxW,
            MinHeight = MinH, MaxHeight = MaxH,
            Fill = Tok.FillSolidTertiary,           // TeachingTipBackgroundBrush = SolidBackgroundFillColorTertiary
            BorderColor = Tok.StrokeSurfaceDefault, // TeachingTipBorderBrush = SurfaceStrokeColorDefault
            BorderWidth = 1f,
            Corners = Radii.OverlayAll,             // OverlayCornerRadius = 8
            Shadow = Elevation.Flyout,              // ContentElevation 32 → flyout-depth shadow
            ClipToBounds = true,
            Children = bodyChildren.ToArray(),
        };
        // Restyle the card via [PartBubble]; the beak/top-highlight composition (tail mechanics) stays control-owned.
        card = Parts.Apply(PartBubble, card) with { Children = card.Children };

        // Card + top highlight overlay (the highlight rides INSIDE the border, full width minus the corner inset).
        var cardWithHighlight = new BoxEl
        {
            ZStack = true,
            Direction = 1,
            Children = [card, TopHighlightBar()],
        };

        if (!tailVisible || tailSide == TailSide.None)
            return cardWithHighlight;

        Element Beak() => Embed.Comp(() => new TeachingTipBeak { Side = tailSide, Pin = tailPin });

        // Compose the beak against the joined edge so it points AT the target. The spacer band reserves the visible
        // 8px short side; the beak paints AFTER the card so the card's border doesn't strike through its base.
        switch (tailSide)
        {
            case TailSide.Top:
                return new BoxEl
                {
                    ZStack = true,
                    MinWidth = MinW, MaxWidth = MaxW,
                    AlignSelf = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl { Direction = 1, Children = [new BoxEl { Height = TailShort }, cardWithHighlight] },
                        Beak(),
                    ],
                };
            case TailSide.Bottom:
                return new BoxEl
                {
                    Direction = 1,
                    AlignSelf = FlexAlign.Start,
                    Children =
                    [
                        cardWithHighlight,
                        new BoxEl { Height = TailShort, ZStack = true, Children = [new BoxEl { OffsetY = -TailShort, Children = [Beak()] }] },
                    ],
                };
            case TailSide.Left:
                return new BoxEl
                {
                    ZStack = true,
                    AlignSelf = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl { Direction = 0, Children = [new BoxEl { Width = TailShort }, cardWithHighlight] },
                        Beak(),
                    ],
                };
            default:   // TailSide.Right
                return new BoxEl
                {
                    Direction = 0,
                    AlignSelf = FlexAlign.Start,
                    Children =
                    [
                        cardWithHighlight,
                        new BoxEl { Width = TailShort, ZStack = true, Children = [new BoxEl { OffsetX = -TailShort, Children = [Beak()] }] },
                    ],
                };
        }
    }

    /// <summary>The 1px decorative top highlight, inset past the 8px corners; splits around a top-edge tail.</summary>
    sealed class TeachingTipTopHighlight : Component
    {
        public bool SplitAroundTail;
        public TailPin TailPin;

        public override Element Render()
        {
            var placement = UseContext(Overlay.Placement);
            var p = placement?.Value ?? default;
            float w = p.PopupWidth > 0f ? p.PopupWidth : MinW;
            float inset = Radii.Overlay;   // keep clear of the rounded corners
            if (!SplitAroundTail)
                return new BoxEl
                {
                    Height = 1f,
                    Margin = new Edges4(inset, 1f, inset, 0f),   // 1px inside the border stroke
                    Fill = TopHighlight,
                    HitTestVisible = false,
                    AlignSelf = FlexAlign.Stretch,
                };

            // Split: left segment | 16px tail gap | right segment (TopLeft/TopRightHighlightMargin pair).
            float anchorX = p.AnchorCenterX > 0f ? p.AnchorCenterX : w * 0.5f;
            float gapCenter = TailPin switch
            {
                TailPin.Near => Math.Clamp(anchorX, inset + TailLong, w * 0.5f),
                TailPin.Far => Math.Clamp(anchorX, w * 0.5f, w - inset - TailLong),
                _ => Math.Clamp(anchorX, inset + TailLong * 0.5f, w - inset - TailLong * 0.5f),
            };
            float gapL = Math.Clamp(gapCenter - TailLong * 0.5f, inset, MathF.Max(inset, w - inset));
            float gapR = Math.Clamp(gapCenter + TailLong * 0.5f, gapL, w - inset);
            return new BoxEl
            {
                Direction = 0,
                Height = 1f,
                Margin = new Edges4(0f, 1f, 0f, 0f),
                HitTestVisible = false,
                AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Width = MathF.Max(0f, gapL - inset), Height = 1f, Margin = new Edges4(inset, 0, 0, 0), Fill = TopHighlight },
                    new BoxEl { Width = MathF.Max(0f, gapR - gapL), Height = 1f },
                    new BoxEl { Grow = 1f, Height = 1f, Margin = new Edges4(0, 0, inset, 0), Fill = TopHighlight },
                ],
            };
        }
    }

    /// <summary>The tail/beak: a 45°-rotated square half-occluded by the card edge (visible short side 8px) with a
    /// 1px stroke along its two exposed sides — one per <see cref="TailSide"/>, pinned per the placement state.</summary>
    sealed class TeachingTipBeak : Component
    {
        public TailSide Side;
        public TailPin Pin;

        public override Element Render()
        {
            var placement = UseContext(Overlay.Placement);
            var p = placement?.Value ?? default;
            bool horizontal = Side is TailSide.Top or TailSide.Bottom;
            float popupExtent = horizontal
                ? (p.PopupWidth > 0f ? p.PopupWidth : MinW)
                : (p.PopupHeight > 0f ? p.PopupHeight : MinH);
            float anchorAlong = horizontal
                ? (p.AnchorCenterX > 0f ? p.AnchorCenterX : popupExtent * 0.5f)
                : (p.AnchorCenterY > 0f ? p.AnchorCenterY : popupExtent * 0.5f);
            // Corner variants pin the tail toward the near/far end of the edge but never past the anchor.
            float along = Pin switch
            {
                TailPin.Near => MathF.Min(anchorAlong, Radii.Overlay + TailLong),
                TailPin.Far => MathF.Max(anchorAlong, popupExtent - Radii.Overlay - TailLong * 2f),
                _ => anchorAlong,
            };
            float pos = Math.Clamp(along - TailLong * 0.5f, Radii.Overlay, MathF.Max(Radii.Overlay, popupExtent - TailLong - Radii.Overlay));
            Console.Error.WriteLine($"[beak] render side={Side} hasSig={placement is not null} acx={p.AnchorCenterX:0.0} pw={p.PopupWidth:0.0} pos={pos:0.0}");
            if (Context.Scene is { } dbgScene)
            {
                var chain = new System.Text.StringBuilder();
                for (var n = Context.AnchorNode; !n.IsNull; n = dbgScene.Parent(n)) chain.Append(n.Raw.Index).Append(' ');
                Console.Error.WriteLine($"[beak] anchor-chain: {chain}");
            }

            // The two exposed sides of the rotated square, as a polyline (apex points AWAY from the card).
            PolylineStrokeEl stroke = Side switch
            {
                TailSide.Top => new PolylineStrokeEl
                {
                    Width = TailLong, Height = TailLong,
                    P0 = new Point2(0f, TailShort), P1 = new Point2(TailLong * 0.5f, 0f), P2 = new Point2(TailLong, TailShort),
                    PointCount = 3, Color = Tok.StrokeSurfaceDefault, Thickness = 1f, RoundCaps = false,
                },
                TailSide.Bottom => new PolylineStrokeEl
                {
                    Width = TailLong, Height = TailLong,
                    P0 = new Point2(0f, TailShort), P1 = new Point2(TailLong * 0.5f, TailLong), P2 = new Point2(TailLong, TailShort),
                    PointCount = 3, Color = Tok.StrokeSurfaceDefault, Thickness = 1f, RoundCaps = false,
                },
                TailSide.Left => new PolylineStrokeEl
                {
                    Width = TailLong, Height = TailLong,
                    P0 = new Point2(TailShort, 0f), P1 = new Point2(0f, TailLong * 0.5f), P2 = new Point2(TailShort, TailLong),
                    PointCount = 3, Color = Tok.StrokeSurfaceDefault, Thickness = 1f, RoundCaps = false,
                },
                _ => new PolylineStrokeEl
                {
                    Width = TailLong, Height = TailLong,
                    P0 = new Point2(TailShort, 0f), P1 = new Point2(TailLong, TailLong * 0.5f), P2 = new Point2(TailShort, TailLong),
                    PointCount = 3, Color = Tok.StrokeSurfaceDefault, Thickness = 1f, RoundCaps = false,
                },
            };

            return new BoxEl
            {
                Width = TailLong,
                Height = TailLong,
                OffsetX = horizontal ? pos : 0f,
                OffsetY = horizontal ? 0f : pos,
                ZStack = true,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl { Width = TailLong, Height = TailLong, Rotation = 45f, Fill = Tok.FillSolidTertiary, HitTestVisible = false },
                    stroke,
                ],
            };
        }
    }

    // ButtonsStates VSM → the footer panel. Returns null when neither footer button is shown (NoButtonsVisible).
    Element? BuildFooter(bool hasAction, bool hasFooterClose, bool footerCloseUsed, Action<CloseReason> requestClose)
    {
        bool closeInFooter = hasFooterClose && footerCloseUsed;
        if (!hasAction && !closeInFooter) return null;

        var actionLabel = ActionButtonContent;
        var closeLabel = CloseButtonContent;

        void OnAction() { ActionButtonClick?.Invoke(); requestClose(CloseReason.CloseButton); }
        void OnFooterClose() { CloseButtonClick?.Invoke(); requestClose(CloseReason.CloseButton); }

        // ActionButtonStyle default = DefaultButtonStyle (TeachingTip.xaml:10) — STANDARD chrome unless the app
        // opts into the accent style (ActionButtonIsAccent, the gallery-sample override).
        BoxEl ActionBtn() => ActionButtonIsAccent ? Button.Accent(actionLabel, OnAction) : Button.Standard(actionLabel, OnAction);
        // Footer close: restyle via [PartCloseButton]; the close pipeline (OnClick) always wins.
        BoxEl FooterCloseBtn() => Parts.Apply(PartCloseButton, Button.Standard(closeLabel, OnFooterClose) with { Grow = 1f, Justify = FlexJustify.Center })
            with { OnClick = OnFooterClose, Role = AutomationRole.Button };

        var buttons = new List<Element>(2);
        if (hasAction && closeInFooter)
        {
            // BothButtonsVisible: Action on the left, Close (standard) on the right; equal-width split.
            buttons.Add(ActionBtn() with { Grow = 1f, Justify = FlexJustify.Center, Margin = new Edges4(0, 0, BothButtonsGap, 0) });
            buttons.Add(FooterCloseBtn());
        }
        else if (hasAction)
        {
            // ActionButtonVisible: full-width (ColumnSpan 2).
            buttons.Add(ActionBtn() with { Grow = 1f, Justify = FlexJustify.Center });
        }
        else
        {
            // CloseButtonVisible: full-width standard close (ColumnSpan 2).
            buttons.Add(FooterCloseBtn());
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Stretch,
            Margin = new Edges4(0, ButtonPanelTop, 0, 0),   // TeachingTipButtonPanelMargin 0,12,0,0
            Children = buttons.ToArray(),
        };
    }
}
