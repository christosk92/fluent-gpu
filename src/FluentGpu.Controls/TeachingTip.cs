using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>TeachingTip</c> (controls/dev/TeachingTip/TeachingTip.xaml + _themeresources + .cpp/.idl): an anchored
/// (targeted) or window-relative (untargeted) non-modal callout that teaches/highlights a feature. The body is a SOLID
/// surface — <c>SolidBackgroundFillColorTertiary</c> background, <c>SurfaceStrokeColorDefault</c> 1px border,
/// <c>OverlayCornerRadius</c> (8), an elevation shadow — with (top→bottom) an optional hero image, an icon + title +
/// subtitle row, the main content, and a footer button panel (Action + Close). A targeted tip grows a 16×8 tail/beak that
/// points at the target; an untargeted tip has no tail. NOT light-dismiss by default (<c>IsLightDismissEnabled=false</c>):
/// only the close button, Escape, or a programmatic close dismisses it; with light-dismiss enabled, click-outside also
/// closes it. Routed through <c>UseContext(Overlay.Service)</c> so the host owns placement (anchor rect → flip/nudge),
/// focus capture/restore, and the open clip-reveal+fade / close fade.
///
/// 1:1 tokens/sizes (TeachingTip_themeresources.xaml, unitless = epx):
///  • MinWidth 320, MaxWidth 336, MinHeight 40, MaxHeight 520 (TeachingTipMin/MaxWidth/Height).
///  • Content margin 12 on all sides (TeachingTipContentMargin); button panel top margin 0,12,0,0
///    (TeachingTipButtonPanelMargin); both-buttons split — Action 0,12,4,0 (Left), Close 4,12,0,0 (Right).
///  • MainContent present-margin 0,12,0,0 (gap below the title block when content exists); icon→title gap 12
///    (TeachingTipIconPresenterMarginWithIcon); title-stack right margin 28 when the header (alternate) close button shows.
///  • Title FontSize 16 SemiBold = TeachingTipTitleForegroundBrush (TextFillColorPrimary); Subtitle FontSize 14 =
///    TeachingTipSubtitleForegroundBrush (TextFillColorPrimary); body content FontSize 14 (Foreground = TextPrimary).
///  • Background = TeachingTipBackgroundBrush (SolidBackgroundFillColorTertiary); Border = TeachingTipBorderBrush
///    (SurfaceStrokeColorDefault), 1px; CornerRadius = OverlayCornerRadius (8). Shadow = ContentElevation 32 (Flyout depth).
///  • Tail: a 16×8 (TailLongSide 16 / TailShortSide 8) shape, drawn as a 45°-rotated square half-occluded by the body
///    (s_tailOcclusionAmount 2) so its border follows the tip edge; centered on the joined edge.
///  • Alternate (header) close button: 40×40 (TeachingTipAlternateCloseButtonSize), E711 glyph at 16px
///    (TeachingTipAlternateCloseButtonGlyphSize), SubtleFill ramp, ControlCornerRadius.
///
/// Open/close animation (TeachingTip.cpp m_expand/contractAnimationDuration): expand 300ms, contract 200ms (custom
/// easing curves 0.1,0.9→0.2,1.0 expand / 0.7,0.0→1.0,0.5 contract). The shared OverlayHost reveal drives a clip-unfold
/// (250ms FastOutSlowIn 0,0,0,1) + fade (83ms linear) here — see the deferral note on the bespoke 300/200ms curves.
/// </summary>
public sealed class TeachingTip : Component
{
    /// <summary>WinUI <c>TeachingTipCloseReason</c> — surfaced on Closing/Closed.</summary>
    public enum CloseReason : byte { CloseButton, LightDismiss, Programmatic }

    /// <summary>WinUI <c>TeachingTipPlacementMode</c>. Only the vertical axis is honored by the shared positioner today
    /// (Top → above the target, beak on the bottom edge; Bottom/Auto → below the target, beak on the top edge); the 12
    /// directional + Left/Right + Center modes are accepted but fall back to the nearest vertical placement — see the
    /// deferral note. Untargeted (no <see cref="Target"/>) ignores the beak entirely.</summary>
    public enum PlacementMode : byte { Auto, Top, Bottom, Left, Right, TopRight, TopLeft, BottomRight, BottomLeft, LeftTop, LeftBottom, RightTop, RightBottom, Center }

    /// <summary>WinUI <c>TeachingTipTailVisibility</c>.</summary>
    public enum TailVisibilityMode : byte { Auto, Visible, Collapsed }

    /// <summary>WinUI <c>TeachingTipHeroContentPlacementMode</c>.</summary>
    public enum HeroPlacement : byte { Auto, Top, Bottom }

    // ── Content model (TeachingTip.idl) ─────────────────────────────────────────────────────────────────────
    public string TriggerLabel = "Show tip";
    public string Title = "";
    public string Subtitle = "";
    /// <summary>Body content (WinUI <c>Content</c>). When empty, the MainContentPresenter collapses (NoContent VSM).</summary>
    public string Body = "";
    /// <summary>Glyph icon (Segoe Fluent Icons codepoint) shown left of the title (WinUI <c>IconSource</c>). Empty = none.</summary>
    public string IconGlyph = "";
    /// <summary>Hero image source (WinUI <c>HeroContent</c>): a full-bleed banner pinned to the top or bottom edge.</summary>
    public string HeroImage = "";
    public HeroPlacement HeroContentPlacement = HeroPlacement.Auto;

    // ── Buttons (TeachingTip.idl). An empty string collapses that button (ButtonsStates VSM keys on content presence). ──
    /// <summary>WinUI <c>ActionButtonContent</c> — the accent footer button. Empty = collapsed.</summary>
    public string ActionButtonContent = "";
    public Action? ActionButtonClick;
    /// <summary>WinUI <c>CloseButtonContent</c> — the standard footer close button. Empty = the close moves to the 40×40
    /// header (alternate) close button (WinUI HeaderCloseButton VSM); set it to show a labelled footer close instead.</summary>
    public string CloseButtonContent = "";
    public Action? CloseButtonClick;

    // ── Behavior (TeachingTip.idl) ───────────────────────────────────────────────────────────────────────────
    /// <summary>WinUI <c>IsLightDismissEnabled</c> (default false). When true, clicking outside dismisses the tip.</summary>
    public bool IsLightDismissEnabled = false;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real tip after first mount
    /// <summary>WinUI <c>PreferredPlacement</c> (default Auto). Only the vertical axis is currently positioned.</summary>
    public PlacementMode PreferredPlacement = PlacementMode.Auto;
    public TailVisibilityMode TailVisibility = TailVisibilityMode.Auto;
    /// <summary>WinUI <c>Target</c> presence. When false the tip is untargeted (no beak; the positioner still anchors it
    /// to the trigger as the slice has no free-floating window placement — see the deferral note).</summary>
    public bool HasTarget = true;

    // ── Events (TeachingTip.idl: Opened / Closing(+Cancel,+Deferral) / Closed) ───────────────────────────────
    public Action? Opened;
    /// <summary>WinUI <c>Closing</c> — fires before close with the reason; setting <see cref="ClosingEventArgs.Cancel"/>
    /// aborts the close (the synchronous slice of the deferral pipeline — see the deferral note).</summary>
    public Action<ClosingEventArgs>? Closing;
    public Action<CloseReason>? Closed;

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
    const float TailLong = 16f;                // TailLongSideLength (the rotated-square diagonal footprint)
    const float TailShort = 8f;                // TailShortSideLength (occluded height; the visible beak)

    /// <summary>Embed a TeachingTip with a trigger label, title, and body (kept call-site-compatible with the prior API).</summary>
    public static Element Create(string triggerLabel, string title, string body) =>
        Embed.Comp(() => new TeachingTip { TriggerLabel = triggerLabel, Title = title, Body = body });

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
        bool hasHero = HeroImage.Length > 0;
        bool hasAction = ActionButtonContent.Length > 0;
        bool hasFooterClose = CloseButtonContent.Length > 0;

        // WinUI CloseButtonLocations VSM: a footer close button is used iff CloseButtonContent is set OR an action button
        // is present (BothButtonsVisible → FooterCloseButton); otherwise the close collapses to the 40×40 header button.
        bool footerCloseUsed = hasFooterClose || IsLightDismissEnabled;
        bool headerCloseUsed = !footerCloseUsed;

        // WinUI targeted tips center the beak on the target. The card can be much wider than the invoker, so the popup
        // itself must be centered on the target, not left-aligned to it.
        bool opensAbove = PreferredPlacement is PlacementMode.Top or PlacementMode.TopRight or PlacementMode.TopLeft;
        var flyoutPlacement = opensAbove ? FlyoutPlacement.TopCenter : FlyoutPlacement.BottomCenter;

        // Tail is shown for a targeted tip unless explicitly collapsed; never for an untargeted tip (WinUI Untargeted VSM).
        bool tailVisible = HasTarget && TailVisibility != TailVisibilityMode.Collapsed;
        bool beakOnBottom = opensAbove;   // tip above target → beak points DOWN (bottom edge)

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
        // Cancel is honored synchronously; the async Deferral is a deep-deferred gap (see note).
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
            // Escape maps to a programmatic close via the host key-preview; light-dismiss maps to LightDismiss reason.
            var dismiss = IsLightDismissEnabled ? DismissBehavior.LightDismiss : DismissBehavior.None;
            closeReason.Value = CloseReason.Programmatic;
            h.Value = svc.Open(
                () => anchor.Value,
                () => BuildSurface(
                    hasTitle, hasSubtitle, hasBody, hasIcon, hasHero, hasAction, hasFooterClose,
                    footerCloseUsed, headerCloseUsed, tailVisible, beakOnBottom, RequestClose),
                flyoutPlacement,
                new PopupOptions(FocusTrap: false, DismissBehavior: dismiss, Chrome: PopupChrome.TeachingTip));
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

    // Builds the full TeachingTip surface: [beak?] over the solid card [hero? · (icon+titles row | header-close) · content · footer].
    Element BuildSurface(
        bool hasTitle, bool hasSubtitle, bool hasBody, bool hasIcon, bool hasHero, bool hasAction, bool hasFooterClose,
        bool footerCloseUsed, bool headerCloseUsed, bool tailVisible, bool beakOnBottom, Action<CloseReason> requestClose)
    {
        // ── Title / subtitle stack (TitlesStackPanel) ──────────────────────────────────────────────────────────
        var titleStack = new List<Element>(2);
        if (hasTitle)
            titleStack.Add(new TextEl(Title) { Size = TitleSize, Bold = true, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords });
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
        // 4+4 margin gap; Action-only → full-width accent; footer-close-only → full-width standard.
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
            var headerCloseStyle = IconButton.DefaultStyle with { Size = AltCloseSize, GlyphSize = AltCloseGlyph };
            // ZStack: content under a top-right-pinned 40×40 close button (NonHeroContentRootGrid layering). The button
            // row fills the region and right-/top-aligns the button (VerticalAlignment=Top, HorizontalAlignment=Right).
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
                        Children = [IconButton.Create(Icons.Cancel, OnHeaderClose, headerCloseStyle)],
                    },
                ],
            };
        }
        else
        {
            nonHero = contentRegion;
        }

        // Hero banner (HeroContent): full-bleed image pinned top (default/Top) or bottom. Squares the corner it abuts.
        var bodyChildren = new List<Element>(3);
        bool heroTop = HeroContentPlacement != HeroPlacement.Bottom;   // Auto/Top → top
        Element? hero = hasHero
            ? new BoxEl
            {
                Width = MaxW,
                Height = 100f,                     // hero banner band; WinUI sizes to the image — capped to the body width here
                ClipToBounds = true,
                Corners = heroTop ? Radii.OverlayTop : Radii.OverlayBottom,
                Children = [Ui.Image(HeroImage, MaxW, 100f, 0f)],
            }
            : null;

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

        if (!tailVisible)
            return card;

        // ── Tail / beak: a 45°-rotated square, side = TailLong/√2-ish, half-occluded by the card edge so its visible
        // height reads as the 8px short side. Centered on the joined edge; same solid fill + stroke as the card. The
        // negative margin pulls it to overlap the card edge (the card's fill occludes the inner half). ──
        Element Beak() => Embed.Comp(() => new TeachingTipBeak { BeakOnBottom = beakOnBottom });

        // Stack the beak above (points up) or below (points down) the card so it points AT the target.
        if (!beakOnBottom)
        {
            // Tip BELOW target: the beak sits on the TOP edge. It must paint AFTER the card, otherwise the card's top
            // border draws a horizontal line across the beak base. The 8px spacer reserves the visible short side.
            return new BoxEl
            {
                ZStack = true,
                MinWidth = MinW,
                MaxWidth = MaxW,
                AlignSelf = FlexAlign.Start,
                Children =
                [
                    new BoxEl { Direction = 1, Children = [new BoxEl { Height = TailShort }, card] },
                    Beak(),
                ],
            };
        }

        var stack = new List<Element>(2) { card, new BoxEl { Height = TailShort, ZStack = true, Children = [new BoxEl { OffsetY = -TailShort, Children = [Beak()] }] } };   // tip above target → beak below, pointing down

        return new BoxEl
        {
            Direction = 1,
            AlignSelf = FlexAlign.Start,
            Children = stack.ToArray(),
        };
    }

    sealed class TeachingTipBeak : Component
    {
        public bool BeakOnBottom;

        public override Element Render()
        {
            var placement = UseContext(Overlay.Placement);
            var p = placement?.Value ?? default;
            float popupW = p.PopupWidth > 0f ? p.PopupWidth : MinW;
            float anchorX = p.AnchorCenterX > 0f ? p.AnchorCenterX : popupW * 0.5f;
            float x = Math.Clamp(anchorX - TailLong * 0.5f, 0f, MathF.Max(0f, popupW - TailLong));
            var beakStroke = BeakOnBottom
                ? new PolylineStrokeEl
                {
                    Width = TailLong, Height = TailLong,
                    P0 = new Point2(0f, TailShort), P1 = new Point2(TailLong * 0.5f, TailLong), P2 = new Point2(TailLong, TailShort),
                    PointCount = 3, Color = Tok.StrokeSurfaceDefault, Thickness = 1f, RoundCaps = false,
                }
                : new PolylineStrokeEl
                {
                    Width = TailLong, Height = TailLong,
                    P0 = new Point2(0f, TailShort), P1 = new Point2(TailLong * 0.5f, 0f), P2 = new Point2(TailLong, TailShort),
                    PointCount = 3, Color = Tok.StrokeSurfaceDefault, Thickness = 1f, RoundCaps = false,
                };

            return new BoxEl
            {
                Width = TailLong,
                Height = TailLong,
                OffsetX = x,
                ZStack = true,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl { Width = TailLong, Height = TailLong, Rotation = 45f, Fill = Tok.FillSolidTertiary, HitTestVisible = false },
                    beakStroke,
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

        var buttons = new List<Element>(2);
        if (hasAction && closeInFooter)
        {
            // BothButtonsVisible: Action (accent) on the left, Close (standard) on the right; equal-width split.
            buttons.Add(Button.Accent(actionLabel, OnAction) with { Grow = 1f, Justify = FlexJustify.Center, Margin = new Edges4(0, 0, BothButtonsGap, 0) });
            buttons.Add(Button.Standard(closeLabel, OnFooterClose) with { Grow = 1f, Justify = FlexJustify.Center });
        }
        else if (hasAction)
        {
            // ActionButtonVisible: full-width accent (ColumnSpan 2).
            buttons.Add(Button.Accent(actionLabel, OnAction) with { Grow = 1f, Justify = FlexJustify.Center });
        }
        else
        {
            // CloseButtonVisible: full-width standard close (ColumnSpan 2).
            buttons.Add(Button.Standard(closeLabel, OnFooterClose) with { Grow = 1f, Justify = FlexJustify.Center });
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
