using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>SplitViewDisplayMode</c> (XCPTypesAutoGen Modules/Controls/SplitView/SplitView.cs:19-25).
/// Overlay/CompactOverlay float the open pane ABOVE the content (no reflow) and are light-dismissible;
/// Inline/CompactInline reserve layout width for it. Compact modes keep a <c>CompactPaneLength</c> rail visible
/// while closed (the pane is CLIPPED to the rail, never resized — core SplitView.cpp:343-377 template settings).</summary>
public enum SplitViewDisplayMode : byte { Overlay = 0, Inline = 1, CompactOverlay = 2, CompactInline = 3 }

/// <summary>WinUI <c>SplitViewPanePlacement</c> (XCPTypesAutoGen SplitView.cs:11-15).</summary>
public enum SplitViewPanePlacement : byte { Left = 0, Right = 1 }

/// <summary>WinUI <c>SplitViewPaneClosingEventArgs</c> (XCPTypesAutoGen SplitView.cs:31-39): set <see cref="Cancel"/>
/// to keep the pane open. Honored on LIGHT-DISMISS closes only (core SplitView.cpp:212-244 TryCloseLightDismissiblePane
/// → CSplitViewPaneClosingExecutor:27-38); an app-initiated close raises the event but ignores Cancel
/// (core SplitView.cpp:432-456).</summary>
public sealed class SplitViewPaneClosingArgs
{
    public bool Cancel;
}

/// <summary>Per-render bridge between the stateless <see cref="SplitView.Create"/> factory and the mounted
/// <see cref="SplitViewPaneWatcher"/>. A mounted component's PROPS freeze (the reconciler reuses it and ignores new
/// factory closures — Reconciler.cs Update/ComponentEl), so the live open state + callbacks travel by CONTEXT instead:
/// the factory provides a fresh link each render; the watcher subscribes via <c>UseContext</c>.
/// <see cref="PaneNode"/> is written by the pane's <c>OnRealized</c> at mount; <see cref="Close"/> is filled by the
/// watcher with its light-dismiss <c>TryClose</c> so the factory-built dismiss layer / Escape handler can invoke it.</summary>
internal sealed record SplitViewPaneLink(
    bool IsOpen,
    bool LightDismissible,
    Action<bool>? SetIsPaneOpen,
    Action? OnPaneOpening,
    Action? OnPaneOpened,
    Action<SplitViewPaneClosingArgs>? OnPaneClosing,
    Action? OnPaneClosed,
    Ref<NodeHandle> PaneNode,
    Ref<Action?> Close);

/// <summary>A WinUI SplitView: a side <c>pane</c> and a flexible <c>content</c> area, composed per
/// <c>SplitViewDisplayMode</c> × <c>PanePlacement</c> × <c>IsPaneOpen</c> (the WinUI visual-state table,
/// SplitView_Partial.cpp:34-59):
/// <list type="bullet">
/// <item><b>Overlay</b> (the WinUI default) — the open pane floats over the content without reflow (PaneRoot
/// Canvas.ZIndex=1 over a ColumnSpan=2 ContentRoot, SplitView_themeresources.xaml:700, :718) and light-dismisses on
/// content click / Escape / a host resize, with the WinUI 0.35s/0.12s slide.</item>
/// <item><b>Inline</b> — the open pane reserves <c>paneWidth</c> of layout; the content pre-slides in sync
/// (ContentTransform, SplitView_themeresources.xaml:345-348) over the 0.2s/0.1s inline curves.</item>
/// <item><b>CompactOverlay / CompactInline</b> — closed leaves a <c>compactPaneLength</c> (48) rail: the SAME pane,
/// clipped to the rail (a clip-reveal, never a resize — core SplitView.cpp:343-377).</item>
/// </list>
/// State is caller-owned (<paramref name="isPaneOpen"/> is a prop, the WinUI <c>IsPaneOpen</c> DP): wire
/// <paramref name="setIsPaneOpen"/> so light dismiss can write the close back. PaneOpening/PaneOpened/
/// PaneClosing(cancelable on light dismiss)/PaneClosed mirror the WinUI events (XCPTypesAutoGen SplitView.cs:82-96).</summary>
public static class SplitView
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The pane surface (WinUI PaneRoot's inner Border) — mounted while the pane is open OR while a compact
    /// rail shows. Owned: Width (always the full OpenPaneLength — the compact rail CLIPS it), OnRealized (the
    /// focus-contract handle capture, chained), Children (the pane content slot).</summary>
    public const string PartPane = "Pane";
    /// <summary>The flexible content area (WinUI ContentRoot). Owned: Animate (the inline content pre-slide),
    /// Children (the content slot).</summary>
    public const string PartContent = "Content";

    /// <summary>The factory→watcher context channel (one static channel; nesting scopes via nearest-provider).</summary>
    internal static readonly Context<SplitViewPaneLink?> PaneLink = new(null);

    // WinUI SplitView motion (controls/dev/SplitView/SplitView_themeresources.xaml):
    //   overlay  open  PaneTransform.TranslateX ∓OpenPaneLength→0, 0.35s KeySpline 0.1,0.9 0.2,1.0 (:63-66; Right :94-97)
    //   overlay  close 0.12s, same spline (:185-187; Right :214-216)
    //   inline   open  0.2s  KeySpline 0.0,0.35 0.15,1.0 (SplitViewPaneAnimationOpenDuration :10; PaneTransform :341-344)
    //   inline   close 0.1s  (SplitViewPaneAnimationCloseDuration :12; PaneTransform :315-317)
    //   content  pre-slide 0.19999s ≈ the open duration (SplitViewPaneAnimationOpenPreDuration :11; ContentTransform :345-348)
    //   compact-inline open/close clip both run the 0.2s open duration (:376-379 open, :400-403 close)
    //   compact-overlay clip reveal 0.35s open (:128-131) / 0.12s close (:246-249)
    static readonly EasingSpec OverlaySpline = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);
    static readonly EasingSpec InlineSpline = EasingSpec.CubicBezier(0.0f, 0.35f, 0.15f, 1.0f);

    /// <param name="pane">The pane content (WinUI <c>SplitView.Pane</c>).</param>
    /// <param name="content">The main content (WinUI <c>SplitView.Content</c>).</param>
    /// <param name="paneWidth">WinUI <c>OpenPaneLength</c> — default 320 (SplitViewOpenPaneThemeLength,
    /// SplitView_themeresources.xaml:5).</param>
    /// <param name="isPaneOpen">WinUI <c>IsPaneOpen</c> — CLR default false (XCPTypesAutoGen SplitView.cs:58-60;
    /// PaneRoot starts Collapsed, SplitView_themeresources.xaml:700).</param>
    /// <param name="displayMode">Default Overlay (the WinUI DP default — SplitView_Partial.cpp:136, :446).</param>
    /// <param name="compactPaneLength">WinUI <c>CompactPaneLength</c> — default 48 (SplitViewCompactPaneThemeLength,
    /// SplitView_themeresources.xaml:6).</param>
    /// <param name="setIsPaneOpen">Write-back for the caller-owned open state — light dismiss (content click,
    /// Escape, host resize) closes through this. Null ⇒ light dismiss is inert (state cannot be flipped).</param>
    /// <param name="onPaneClosing">Cancelable on light dismiss only; an app-initiated close (the caller flipping
    /// <paramref name="isPaneOpen"/>) raises it but ignores Cancel (core SplitView.cpp:432-456).</param>
    public static Element Create(Element pane, Element content, float paneWidth = 320f, bool isPaneOpen = false,
                                 SplitViewDisplayMode displayMode = SplitViewDisplayMode.Overlay,
                                 SplitViewPanePlacement panePlacement = SplitViewPanePlacement.Left,
                                 float compactPaneLength = 48f,
                                 Action<bool>? setIsPaneOpen = null,
                                 Action? onPaneOpening = null, Action? onPaneOpened = null,
                                 Action<SplitViewPaneClosingArgs>? onPaneClosing = null, Action? onPaneClosed = null,
                                 TemplateParts? parts = null)
    {
        bool right = panePlacement == SplitViewPanePlacement.Right;
        bool compact = displayMode is SplitViewDisplayMode.CompactOverlay or SplitViewDisplayMode.CompactInline;
        // IsLightDismissible = DisplayMode is not Inline/CompactInline (core SplitView.cpp:176-182) — exactly the
        // modes whose open pane floats over the content.
        bool overlayPane = displayMode is SplitViewDisplayMode.Overlay or SplitViewDisplayMode.CompactOverlay;
        // The horizontal slide terminal: the pane parks one OpenPaneLength toward its own edge
        // (TemplateSettings.NegativeOpenPaneLength left / OpenPaneLength right — core SplitView.cpp:356-366).
        float slideDx = right ? paneWidth : -paneWidth;

        var link = new SplitViewPaneLink(isPaneOpen, overlayPane, setIsPaneOpen,
            onPaneOpening, onPaneOpened, onPaneClosing, onPaneClosed,
            new Ref<NodeHandle>(NodeHandle.Null), new Ref<Action?>(null));
        Action<NodeHandle> capturePane = h => link.PaneNode.Value = h;

        var contentBox = new BoxEl
        {
            Key = "sv-content",
            Grow = 1f,
            ClipToBounds = true,
            // Inline modes: the content pre-slides ±OpenPaneLength→0 as the pane opens/closes (ContentTransform —
            // open 0.19999s, SplitView_themeresources.xaml:345-348; close 0.1s, :318-321; compact close keeps the
            // 0.2s open-duration leg, :396-399). Overlay modes never move the content (ContentRoot has no
            // ContentTransform keyframes in the overlay transitions).
            Animate = overlayPane ? (LayoutTransition?)null : new LayoutTransition(TransitionChannels.Position,
                TransitionDynamics.Tween(isPaneOpen || compact ? 200f : 100f, InlineSpline)),
            Children = [content],
        };
        {
            var m = parts.Apply(PartContent, contentBox);
            contentBox = m with { Key = "sv-content", Animate = contentBox.Animate, Children = contentBox.Children };   // structure = the content slot
        }

        // The pane surface, ALWAYS at the full open width: a compact rail shows its leading compactPaneLength through
        // the clip (WinUI PaneRoot Width=TemplateSettings.OpenPaneLength always, SplitView_themeresources.xaml:700;
        // ClosedCompact* only translate PaneClipRectangleTransform, :543-562 — pane content never rewraps).
        var paneBox = new BoxEl
        {
            Width = paneWidth,
            Direction = 1,
            // WinUI PaneBackground default = SystemControlPageBackgroundChromeLowBrush (SplitView_themeresources.xaml:46);
            // nearest engine surface token. (No divider: WinUI's HCPaneBorder is a zero-layout-width, transparent-in-
            // normal-themes 1px rule inside PaneRoot — generic template :715.)
            Fill = Tok.FillSolidBaseAlt,
            OnRealized = capturePane,
            Children = [pane],
        };
        {
            var m = parts.Apply(PartPane, paneBox);
            paneBox = m with
            {
                Width = paneWidth,
                OnRealized = TemplateParts.Chain(capturePane, m.OnRealized),
                Children = paneBox.Children,   // structure = the pane slot
            };
        }

        var watcher = new BoxEl { Key = "sv-watch", Width = 0f, Height = 0f, Children = [Embed.Comp(static () => new SplitViewPaneWatcher())] };

        // The always-mounted compact pane: width 48 ↔ OpenPaneLength as a presented-size clip-reveal
        // (SizeMode.Reveal = translate + clip, no relayout of the pane content — the engine twin of WinUI's
        // PaneClipRectangleTransform translate).
        BoxEl PaneCompact(float openMs, float closeMs, EasingSpec spline) => new()
        {
            Key = "sv-pane",
            Width = isPaneOpen ? paneWidth : compactPaneLength,
            ClipToBounds = true,
            Animate = new LayoutTransition(TransitionChannels.Bounds,
                TransitionDynamics.Tween(isPaneOpen ? openMs : closeMs, spline),
                Size: SizeMode.Reveal),
            Children = [paneBox],
        };

        Element root;
        if (!overlayPane)
        {
            // Inline / CompactInline: the pane column RESERVES width (open = OpenPaneLength, compact-closed =
            // CompactPaneLength); Right placement mirrors the order (g_visualStateTable, SplitView_Partial.cpp:42-46,
            // :54-58; OpenInlineRight template :607-634).
            Element? paneCol = displayMode == SplitViewDisplayMode.CompactInline
                ? PaneCompact(openMs: 200f, closeMs: 200f, InlineSpline)
                : isPaneOpen
                    ? new BoxEl
                    {
                        Key = "sv-pane",
                        Width = paneWidth,
                        ClipToBounds = true,
                        // PaneTransform.TranslateX ∓OpenPaneLength→0 over 0.2s open (SplitView_themeresources.xaml:341-344)
                        // / 0.1s close (:315-317), KeySpline 0.0,0.35 0.15,1.0; the control root's ClipToBounds plays
                        // WinUI's screen-fixed PaneClipRectangle.
                        Animate = new LayoutTransition(TransitionChannels.Position,
                            TransitionDynamics.Tween(200f, InlineSpline),
                            Enter: new EnterExit(Dx: slideDx, Active: true),
                            Exit: new EnterExit(Dx: slideDx, Active: true),
                            ExitDynamics: TransitionDynamics.Tween(100f, InlineSpline)),
                        Children = [paneBox],
                    }
                    : null;

            Element[] kids = paneCol is null
                ? [watcher, contentBox]
                : right ? [watcher, contentBox, paneCol] : [watcher, paneCol, contentBox];
            root = new BoxEl { Direction = 0, Grow = 1f, ClipToBounds = true, Children = kids };
        }
        else
        {
            // Overlay / CompactOverlay: the open pane floats ABOVE the content with NO reflow (PaneRoot Canvas.ZIndex=1
            // spanning the grid, ContentRoot Grid.ColumnSpan=2 — SplitView_themeresources.xaml:700, :718); compact
            // reserves only the rail width for the content (ClosedCompactLeft ColumnDefinition1 = CompactPaneGridLength,
            // :543-551).
            bool compactOverlay = displayMode == SplitViewDisplayMode.CompactOverlay;
            Element[] baseKids = compactOverlay
                ? right ? [watcher, contentBox, new BoxEl { Width = compactPaneLength }]
                        : [watcher, new BoxEl { Width = compactPaneLength }, contentBox]
                : [watcher, contentBox];
            var baseRow = new BoxEl { Key = "sv-base", Direction = 0, Grow = 1f, Children = baseKids };

            Element? paneLayer = compactOverlay
                ? PaneCompact(openMs: 350f, closeMs: 120f, OverlaySpline)
                : isPaneOpen
                    ? new BoxEl
                    {
                        Key = "sv-pane",
                        Width = paneWidth,
                        ClipToBounds = true,
                        Children = [paneBox],
                    }
                    : null;

            // Placement wrapper: a full-size, paint-less row that Start/End-aligns the floating pane (WinUI PaneRoot
            // HorizontalAlignment Left/Right — SplitView_themeresources.xaml:700, :581-583). For pure Overlay it also
            // carries the slide: TranslateX ∓OpenPaneLength→0 over 0.35s open (:63-66) / 0.12s close (:185-187),
            // KeySpline 0.1,0.9 0.2,1.0, clipped at the control edge by the root's ClipToBounds.
            Element Aligned(Element p, bool slide) => new BoxEl
            {
                Key = "sv-panelayer",
                Direction = 0,
                Justify = right ? FlexJustify.End : FlexJustify.Start,
                Animate = slide
                    ? new LayoutTransition(TransitionChannels.Position,
                        TransitionDynamics.Tween(350f, OverlaySpline),
                        Enter: new EnterExit(Dx: slideDx, Active: true),
                        Exit: new EnterExit(Dx: slideDx, Active: true),
                        ExitDynamics: TransitionDynamics.Tween(120f, OverlaySpline))
                    : (LayoutTransition?)null,
                Children = [p],
            };

            // LightDismissLayer (SplitView_themeresources.xaml:723): a transparent catcher over the content, mounted
            // while the overlay pane is open; closes on the pointer-release click (core SplitView.cpp:638-651
            // OnLightDismissLayerPointerReleased). Not a tab stop, and a press never moves focus — focus is parked in
            // the pane while light-dismissible (core SplitView.cpp:251-303).
            Element dismiss = new BoxEl
            {
                Key = "sv-dismiss",
                TabStop = false,
                AllowFocusOnInteraction = false,
                OnClick = () => link.Close.Value?.Invoke(),
            };

            Element[] zk = paneLayer is null
                ? [baseRow]
                : isPaneOpen ? [baseRow, dismiss, Aligned(paneLayer, slide: !compactOverlay)]
                             : [baseRow, Aligned(paneLayer, slide: false)];

            // VK_ESCAPE (and gamepad B) light-dismisses the open overlay pane (core SplitView.cpp:574-578) —
            // bubbled here from the focused pane descendant, the WinUI KeyDown-on-the-control routing.
            Action<KeyEventArgs>? rootKeys = null;
            if (isPaneOpen)
            {
                rootKeys = e =>
                {
                    if (e.Handled || e.KeyCode != Keys.Escape) return;
                    var close = link.Close.Value;
                    if (close is null) return;
                    close();
                    e.Handled = true;
                };
            }

            root = new BoxEl
            {
                ZStack = true,
                Grow = 1f,
                ClipToBounds = true,
                OnKeyDown = rootKeys,
                Children = zk,
            };
        }

        return Ctx.Provide(PaneLink, link, root);
    }
}

/// <summary>The SplitView pane lifecycle, mounted with the control in every mode: raises PaneOpening/PaneOpened/
/// PaneClosing/PaneClosed on IsPaneOpen flips (SplitView_Partial.cpp:470-515 OnIsPaneOpenChanged, :761-781
/// OnPaneOpenedOrClosed), owns the light-dismiss close (cancelable PaneClosing — core SplitView.cpp:212-249), the
/// light-dismiss focus contract (save + focus pane on open, Tab-cycle within it, restore on close — core
/// SplitView.cpp:421-430, :486-551, :251-303) and the host-resize auto-close (SplitView_Partial.cpp:230-256).
/// Receives the live state via <see cref="SplitView.PaneLink"/> context (mounted-component props freeze).</summary>
internal sealed class SplitViewPaneWatcher : Component
{
    public override Element Render()
    {
        var link = UseContext(SplitView.PaneLink);
        var hooks = UseContext(InputHooks.Current);
        var viewport = UseContext(Viewport.Size);
        var paneNode = UseRef(NodeHandle.Null);          // stable copy of the per-render OnRealized capture
        var savedFocus = UseRef(NodeHandle.Null);        // m_spPrevFocusedElementWeakRef (core SplitView.cpp:495)
        var pushedScope = UseRef(NodeHandle.Null);       // the focus-scope root we pushed (pop exactly that)
        var lastOpen = UseRef(false);                    // false-seeded so an initially-open pane runs the open leg
        var closingByLightDismiss = UseRef(false);       // m_isPaneClosingByLightDismiss (core SplitView.h:69)
        var lastViewport = UseRef(viewport);

        // OnRealized writes the pane handle at pane MOUNT only — keep the last non-null sighting (compact rails
        // realize once at first render; overlay/inline panes re-realize on every open).
        if (link is not null && !link.PaneNode.Value.IsNull)
            paneNode.Value = link.PaneNode.Value;

        // TryCloseLightDismissiblePane (core SplitView.cpp:212-244), gated by CanLightDismiss (:184-187):
        // PaneClosing is cancelable on THIS path only (OnCancelClosing :246-249).
        void TryClose()
        {
            if (link is null || !link.IsOpen || !link.LightDismissible || closingByLightDismiss.Value
                || link.SetIsPaneOpen is null)
            {
                return;
            }
            var args = new SplitViewPaneClosingArgs();
            link.OnPaneClosing?.Invoke(args);
            if (args.Cancel) return;
            closingByLightDismiss.Value = true;
            link.SetIsPaneOpen(false);
        }
        if (link is not null) link.Close.Value = TryClose;

        // The IsPaneOpen transition work (SplitView_Partial.cpp:470-515).
        UseEffect(() =>
        {
            if (link is null || link.IsOpen == lastOpen.Value) return;
            lastOpen.Value = link.IsOpen;
            var scene = Context.Scene;
            if (link.IsOpen)
            {
                link.OnPaneOpening?.Invoke();   // PaneOpening fires BEFORE the focus move (SplitView_Partial.cpp:481-487)
                if (link.LightDismissible)
                {
                    // SetFocusToPane (core SplitView.cpp:486-517): save the previous focus, move focus into the pane,
                    // and trap Tab inside it (ProcessTabStop wraps to the pane's first/last focusable — :251-303).
                    savedFocus.Value = hooks.GetFocus?.Invoke() ?? NodeHandle.Null;
                    var paneH = paneNode.Value;
                    if (!paneH.IsNull && scene is not null && scene.IsLive(paneH))
                    {
                        var first = hooks.FirstFocusableIn?.Invoke(paneH) ?? NodeHandle.Null;
                        if (!first.IsNull)
                        {
                            hooks.FocusNode?.Invoke(first, true);
                            hooks.PushFocusScope?.Invoke(paneH);
                            pushedScope.Value = paneH;
                        }
                    }
                }
                link.OnPaneOpened?.Invoke();
            }
            else
            {
                // An app-initiated close still raises PaneClosing, but Cancel is NOT honored (core SplitView.cpp:432-456);
                // the light-dismiss path already raised it cancelably in TryClose.
                if (!closingByLightDismiss.Value)
                    link.OnPaneClosing?.Invoke(new SplitViewPaneClosingArgs());
                closingByLightDismiss.Value = false;
                if (link.LightDismissible)
                {
                    if (!pushedScope.Value.IsNull)
                    {
                        hooks.PopFocusScope?.Invoke(pushedScope.Value);
                        pushedScope.Value = NodeHandle.Null;
                    }
                    // RestoreSavedFocusElement (core SplitView.cpp:519-551).
                    if (!savedFocus.Value.IsNull && scene is not null && scene.IsLive(savedFocus.Value))
                        hooks.RestoreFocus?.Invoke(savedFocus.Value);
                    savedFocus.Value = NodeHandle.Null;
                }
                link.OnPaneClosed?.Invoke();
            }
        }, link?.IsOpen ?? false);

        // A host/root size change light-dismisses the open pane — skipping the initial size
        // (SplitView_Partial.cpp:230-244 OnSizeChanged; :246-256 OnXamlRootChanged).
        UseEffect(() =>
        {
            if (viewport == lastViewport.Value) return;
            bool initial = lastViewport.Value == default;
            lastViewport.Value = viewport;
            if (!initial) TryClose();
        }, DepKey.From(HashCode.Combine(viewport.Width, viewport.Height)));

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
