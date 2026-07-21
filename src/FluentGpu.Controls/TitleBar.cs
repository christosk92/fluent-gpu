using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The <see cref="TitleBar.Create"/> options record — wraps the TitleBar property-init config. The
/// <see cref="Content"/> builder receives the LIVE content-slot width signal (subscribe it for content that must
/// resize with the slot at runtime, e.g. <c>AutoSuggestBox.Create(widthSignal: …)</c>; read <c>.Peek()</c> to pick the
/// shape per render). Field defaults mirror the control's own.</summary>
public sealed record TitleBarOptions
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string IconGlyph { get; init; } = "";
    public ColorF? IconColor { get; init; }
    public bool ShowBackButton { get; init; }
    public bool BackEnabled { get; init; }
    public Action? OnBack { get; init; }
    public bool ShowPaneToggle { get; init; }
    public Action? OnPaneToggle { get; init; }
    public Func<IReadSignal<float>, Element>? Content { get; init; }
    public Func<Element>? Tabs { get; init; }
    public Func<int>? TabsVersion { get; init; }
    public bool ShowCaptionButtons { get; init; } = true;
    public TemplateParts? Parts { get; init; }
}

/// <summary>
/// The WinUI 3 <c>TitleBar</c> control (WinAppSDK 1.7, microsoft-ui-xaml controls\dev\TitleBar) over a custom frame
/// (<see cref="WindowDesc.CustomFrame"/>): back + pane-toggle buttons (40w, 16px glyphs), a 16×16 app-identity icon,
/// 12px Caption title/subtitle, a centered content column (the gallery's AutoSuggestBox), and — unlike WinUI, which
/// reserves space for SHELL-drawn caption buttons — three ENGINE-drawn min/max/close <see cref="CaptionButton"/>s
/// (46w, full bar height, close = shell red) so the whole bar is one GPU-rendered surface.
///
/// Non-client plumbing: the control captures its part handles (<c>OnRealized</c>) and, in a layout effect (after
/// layout, before paint — push-on-relayout only, never per frame), reports <see cref="TitleBarRegion"/>s through
/// <see cref="InputHooks.SetTitleBarRegions"/>: interactive islands (back/pane/content) FIRST as
/// <see cref="TitleBarHit.Client"/>, then the three button rects (→ HTMIN/HTMAX/HTCLOSE — the Win11 snap-layouts
/// flyout requires HTMAXBUTTON), then the whole bar as the catch-all <see cref="TitleBarHit.Caption"/> drag band
/// (first match wins in WM_NCHITTEST). Window activation/placement changes arrive via the host-bumped
/// <see cref="InputHooks.WindowChromeEpoch"/> signal: deactivation dims title→tertiary, icon/content→50% opacity,
/// caption glyphs→disabled (the WinUI Deactivated visual state); maximize re-glyphs max↔restore.
/// </summary>
public sealed class TitleBar : Component
{
    // Template parts (see TemplateParts; docs/guide/control-fidelity.md §6).
    /// <summary>The 48px bar row. Owned: OnRealized (drag-band capture), Children, Height.</summary>
    public const string PartRoot = "Root";
    /// <summary>The back button root (an IconButton). Owned: OnClick, Role, OnRealized (island capture), Children.</summary>
    public const string PartBackButton = "BackButton";
    /// <summary>The pane-toggle (hamburger) button root. Owned: OnClick, Role, OnRealized, Children.</summary>
    public const string PartPaneToggle = "PaneToggle";
    /// <summary>The 16×16 app-identity icon wrapper. Owned: Children.</summary>
    public const string PartIcon = "Icon";
    /// <summary>The title TextEl. Owned: none.</summary>
    public const string PartTitle = "Title";
    /// <summary>The subtitle TextEl. Owned: none.</summary>
    public const string PartSubtitle = "Subtitle";
    /// <summary>The centered, flexible content column (the search box host). Owned: OnRealized (island capture), Children.</summary>
    public const string PartContent = "Content";
    /// <summary>The minimize caption button. Owned: OnClick, Role, OnRealized, Children.</summary>
    public const string PartCaptionMin = "CaptionMin";
    /// <summary>The maximize/restore caption button. Owned: OnClick, Role, OnRealized, Children.</summary>
    public const string PartCaptionMax = "CaptionMax";
    /// <summary>The close caption button. Owned: OnClick, Role, OnRealized, Children.</summary>
    public const string PartCaptionClose = "CaptionClose";

    /// <summary>WinUI TitleBar tall mode — the height when Content is set (the gallery look).</summary>
    public const float ExpandedHeight = 48f;
    /// <summary>WinUI TitleBar compact mode (no content; reserved — this control always renders tall for now).</summary>
    public const float CompactHeight = 32f;

    const float NavButtonSize = 40f;     // back/pane button width (WinUI TitleBar back/pane = 40w)
    const float LeftHeaderPad = 14f;     // WinUI left-header padding column
    const float IconSize = 16f;          // WinUI icon Viewbox 16×16
    const float MinDragStrip = 48f;      // WinUI min drag-region column before the caption buttons

    // ── configuration ─────────────────────────────────────────────────────────────────────────────────────────────
    // MOUNT-TIME config: the reconciler reuses the component instance on parent re-render without re-applying these
    // plain fields (constructor args freeze at mount — pitfalls.md). Anything that must change at runtime flows via
    // signals/context (activation, window state and the measured content width already do).
    public string Title = "";
    public string Subtitle = "";
    /// <summary>App-identity glyph (the gallery uses the accent grid glyph; WinUI uses an ImageIcon). Empty = none.</summary>
    public string IconGlyph = "";
    private ColorF? _iconColor;
    /// <summary>Explicit app-icon color, or the live accent token when left unset.</summary>
    public ColorF IconColor { get => _iconColor ?? Tok.AccentDefault; set => _iconColor = value; }
    /// <summary>WinUI IsBackButtonVisible. Pair with <see cref="BackEnabled"/> (visible-but-disabled = no history).</summary>
    public bool ShowBackButton;
    /// <summary>WinUI IsBackEnabled.</summary>
    public bool BackEnabled;
    public Action? OnBack;
    /// <summary>WinUI IsPaneToggleButtonVisible.</summary>
    public bool ShowPaneToggle;
    public Action? OnPaneToggle;
    /// <summary>The centered content column (the gallery's AutoSuggestBox). Invoked per render with the column's
    /// AVAILABLE width (DIP) so fixed-width content can clamp itself (WinUI: the content area shrinks first; the
    /// caption buttons never move). Return a 0-sized element to collapse when too narrow.</summary>
    public Func<float, Element>? Content;
    /// <summary>The measured content-slot width as a LIVE signal — the value behind the <see cref="Content"/>
    /// lambda's argument. Component plain fields freeze at mount, so the lambda argument can pick the content's
    /// SHAPE per render but cannot resize an already-mounted component; content that must track the slot at runtime
    /// subscribes to this instead (e.g. <see cref="AutoSuggestBox.WidthSignal"/>).</summary>
    public IReadSignal<float> ContentAvail => _availDip;
    /// <summary>A LEFT-aligned tab strip (browser-style tabs, e.g. a music app's open pages). When set it REPLACES the
    /// centered <see cref="Content"/> column: the strip is reported as a single <see cref="TitleBarHit.Client"/> island
    /// hugging the left, and the flexible space after it (before the caption buttons) becomes the Caption drag band — the
    /// WinUI TabView + TabStripFooter shape. A <c>Func</c> (not a frozen Element) so it can read the app's tab signals and
    /// re-render the bar when tabs change. Pair with <see cref="TabsVersion"/> so the non-client regions re-push too.</summary>
    public Func<Element>? Tabs;
    /// <summary>A monotonic revision of the tab set (e.g. a version signal's value) read each render. Because component
    /// fields freeze at mount, the bar only re-renders/re-pushes its regions when a signal it READ changes — reading this
    /// each render makes adding/removing/reordering a tab re-report the (now wider/narrower) strip island. Required with
    /// <see cref="Tabs"/>.</summary>
    public Func<int>? TabsVersion;
    /// <summary>False = a standard OS frame owns the caption buttons; the bar keeps a right inset clear of them.</summary>
    public bool ShowCaptionButtons = true;
    public TemplateParts? Parts;

    /// <summary>The one canonical TitleBar factory (WS3 creation idiom). Wraps the property-init surface in a
    /// <see cref="TitleBarOptions"/> record; the options' <see cref="TitleBarOptions.Content"/> builder is handed the
    /// live <see cref="ContentAvail"/> signal so it can wire content that resizes with the slot without needing the
    /// instance. Property-init stays available for the in-repo probes/shells that compose the bar directly, but this is
    /// the documented public path.</summary>
    public static Element Create(TitleBarOptions options)
        => Embed.Comp(() =>
        {
            var tb = new TitleBar
            {
                Title = options.Title, Subtitle = options.Subtitle, IconGlyph = options.IconGlyph,
                ShowBackButton = options.ShowBackButton, BackEnabled = options.BackEnabled, OnBack = options.OnBack,
                ShowPaneToggle = options.ShowPaneToggle, OnPaneToggle = options.OnPaneToggle,
                Tabs = options.Tabs, TabsVersion = options.TabsVersion,
                ShowCaptionButtons = options.ShowCaptionButtons, Parts = options.Parts,
            };
            if (options.IconColor is { } ic) tb.IconColor = ic;
            if (options.Content is { } content) tb.Content = _ => content(tb.ContentAvail);
            return tb;
        });

    // Captured part handles (OnRealized fires at mount; the component instance persists across re-renders, so plain
    // fields are the stable store) → the WM_NCHITTEST region report.
    NodeHandle _root, _back, _pane, _contentCol, _content, _tabs, _min, _max, _close;
    // Reused region buffer: filled in place on each relayout push — no steady-state allocation
    // (8 = islands(back/pane/content-or-tabs) + buttons(3) + caption, with headroom for the tabs island).
    readonly TitleBarRegion[] _regions = new TitleBarRegion[8];
    // The content column's MEASURED width (DIP), fed back from the layout effect: the column is the row's ONE
    // Grow=1 + Shrink=1 child, so its laid-out width IS the true available space between the clusters in BOTH
    // directions — no text-width estimating, and on a narrowing window the column (never the caption cluster) gives
    // way. Starts unmeasured (infinity → content renders at its natural max); the first layout corrects it within
    // one frame, as does every resize.
    readonly Signal<float> _availDip = new(float.PositiveInfinity);

    // Render memo: the bar's TREE is viewport-independent for a tabbed bar (the tab strip + a Grow=1 caption band absorb
    // a resize; a non-tabbed bar's content column tracks _availDip, which IS in the key). The viewport subscription below
    // still re-renders this component every resize tick — but ONLY so the region-report layout effect re-runs; rebuilding
    // the element tree each time was ~12-24KB/resize, the dominant GC source behind the drag hiccup. So we cache the built
    // tree keyed on everything that affects it (NOT the viewport) and return it alloc-free when nothing real changed.
    Element? _cachedTree;
    int _cacheKey = int.MinValue;

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        int epoch = hooks.WindowChromeEpoch?.Value ?? 0;          // subscribe: re-render on activation/placement change
        var viewport = UseContext(Viewport.Size);                 // subscribe: re-report regions on window resize/DPI hop
        bool active = hooks.IsWindowActive?.Invoke() ?? true;
        bool maximized = hooks.GetWindowState?.Invoke() == WindowState.Maximized;
        int tabsVer = TabsVersion?.Invoke() ?? 0;                 // subscribe: re-render + re-push regions on tab add/remove

        // Report the drag/button regions after THIS render's layout settles (phase 6.5) — deps cover everything that
        // moves the parts (resize, maximize→WM_SIZE→viewport, DPI hop→DIP viewport change, the measured-width feedback
        // render whose island rect must re-push, and the tab-set revision so the strip island re-reports on change).
        UseLayoutEffect(() => PushRegions(hooks),
            DepKey.From(HashCode.Combine(viewport.Width, viewport.Height, epoch, _availDip.Peek(), tabsVer, ShowBackButton, ShowPaneToggle, ShowCaptionButtons)));

        // Memo gate: a resize-only re-render returns the cached tree alloc-free (the layout effect above already re-ran
        // — its viewport deps changed — so regions re-push without a rebuild). Key excludes the viewport on purpose.
        // Tok.Epoch is in the key so a live theme switch busts the cache — otherwise RethemeAll re-runs this effect but
        // the memo returns the OLD-theme tree (the caption glyphs/foregrounds would stay stale).
        int key = unchecked(((((epoch * 397 ^ tabsVer) * 397 ^ _availDip.Peek().GetHashCode()) * 397 ^ Tok.Epoch) * 397)
            ^ ((active ? 1 : 0) | (maximized ? 2 : 0) | (ShowBackButton ? 4 : 0) | (ShowPaneToggle ? 8 : 0) | (ShowCaptionButtons ? 16 : 0) | (BackEnabled ? 32 : 0)));
        if (_cachedTree is { } cached && key == _cacheKey) return cached;

        // WinUI back/pane: 40w × 44h with Margin 2 (the hover backplate spans y=2..46 of the 48px bar; adjacent
        // margins give the 4px back↔pane gap and the 2px before the 14px header pad = the 16px pane→icon gap).
        // Deactivated state: foreground → tertiary (fills unchanged).
        var navStyle = IconButton.DefaultStyle with
        {
            Size = NavButtonSize,
            Height = 44f,
            Foreground = active ? Tok.TextPrimary : Tok.TextTertiary,
        };
        var navMargin = new Edges4(2f, 2f, 2f, 2f);

        var kids = new List<Element>(14);

        if (ShowBackButton)
        {
            var back = IconButton.Create(Icons.Back, () => OnBack?.Invoke(), navStyle, isEnabled: BackEnabled)
                with { Margin = navMargin };
            var applied = Parts.Apply(PartBackButton, back);
            kids.Add(applied with
            {
                OnClick = back.OnClick, Role = AutomationRole.Button, Children = back.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _back = h, applied.OnRealized),
            });
        }
        if (ShowPaneToggle)
        {
            var pane = IconButton.Create(Icons.Menu, () => OnPaneToggle?.Invoke(), navStyle)
                with { Margin = navMargin };
            var applied = Parts.Apply(PartPaneToggle, pane);
            kids.Add(applied with
            {
                OnClick = pane.OnClick, Role = AutomationRole.Button, Children = pane.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _pane = h, applied.OnRealized),
            });
        }

        kids.Add(new BoxEl { Width = LeftHeaderPad });

        if (IconGlyph.Length > 0)
        {
            // The identity icon dims to 50% opacity on deactivation (WinUI dims the icon/content presenters).
            var icon = new BoxEl
            {
                Width = IconSize, Height = IconSize, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Opacity = active ? 1f : 0.5f,
                Children = [Ui.Icon(IconGlyph, IconSize).Foreground(IconColor)],
            };
            kids.Add(Parts.Apply(PartIcon, icon) with { Children = icon.Children });
            kids.Add(new BoxEl { Width = 16f });                  // WinUI icon margin-right
        }

        if (Title.Length > 0)
        {
            kids.Add(Parts.Apply(PartTitle, new TextEl(Title)
            {
                Size = 12f,                                        // CaptionTextBlockStyle
                Color = active ? Tok.TextPrimary : Tok.TextTertiary,   // TitleBar(Deactivated)ForegroundBrush
            }));
            kids.Add(new BoxEl { Width = 8f });                   // WinUI title margin-right
        }
        if (Subtitle.Length > 0)
        {
            kids.Add(Parts.Apply(PartSubtitle, new TextEl(Subtitle)
            {
                Size = 12f,
                Color = active ? Tok.TextSecondary : Tok.TextTertiary, // TitleBarSubtitle(Deactivated)ForegroundBrush
            }));
            kids.Add(new BoxEl { Width = 16f });                  // WinUI subtitle margin-right
        }

        if (Tabs is { } tabsFunc)
        {
            // A LEFT-aligned tab strip hugging its content (Shrink=1 so a too-full strip gives way before the captions).
            // It is the ONE Client island; the Grow=1 spacer after it is the Caption drag band (WinUI TabStripFooter).
            var tabsIsland = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 1, Height = ExpandedHeight,
                OnRealized = h => _tabs = h,
                Children = [tabsFunc()],
            };
            kids.Add(tabsIsland);
            kids.Add(new BoxEl { Grow = 1, Shrink = 1, Height = ExpandedHeight });   // flexible Caption drag band
        }
        else
        {
            // The content column's available width — the MEASURED Grow=1 column width from the previous layout
            // (subscribing here re-renders this component when the measurement changes, e.g. on window resize).
            // WinUI sizing contract: the content area shrinks first; the caption buttons never move.
            float contentAvail = _availDip.Value;

            // The centered, flexible content column. The interactive island (HTCLIENT) is the inner box that HUGS the
            // content's natural width (the gallery's search box) — NOT the flexible column: the empty flex space
            // flanking the search box must stay part of the Caption drag band.
            var island = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center,
                OnRealized = h => _content = h,
                Children = Content is { } c ? [c(contentAvail)] : [],
            };
            var content = new BoxEl
            {
                // Grow + Shrink: the column is the row's ONE flexible child, so it absorbs all free space AND all
                // overflow — the fixed caption cluster after it never moves or clips (the WinUI sizing contract), and
                // the arranged width PushRegions feeds back is the honest available space even on resize-down (without
                // Shrink the column could only track the viewport UP and _availDip would floor at the content's width).
                Grow = 1, Shrink = 1, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Height = ExpandedHeight,
                Opacity = active ? 1f : 0.5f,                          // WinUI deactivated content dim
                Children = [island],
            };
            var applied = Parts.Apply(PartContent, content);
            kids.Add(applied with
            {
                Children = content.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _contentCol = h, applied.OnRealized),
            });
        }

        kids.Add(new BoxEl { Width = MinDragStrip });             // the guaranteed-grabbable drag strip

        if (ShowCaptionButtons)
        {
            string maxGlyph = maximized ? Icons.ChromeRestore : Icons.ChromeMaximize;
            kids.Add(Caption(PartCaptionMin, Icons.ChromeMinimize, () => hooks.WindowMinimize?.Invoke(),
                             CaptionButton.MinMax, active, h => _min = h));
            kids.Add(Caption(PartCaptionMax, maxGlyph, () => hooks.WindowToggleMaximize?.Invoke(),
                             CaptionButton.MinMax, active, h => _max = h));
            kids.Add(Caption(PartCaptionClose, Icons.ChromeClose, () => hooks.WindowClose?.Invoke(),
                             CaptionButton.Close, active, h => _close = h));
        }
        else
        {
            // Standard OS frame: keep the bar's content clear of the shell-drawn caption buttons.
            kids.Add(new BoxEl { Width = 140f });
        }

        var root = new BoxEl
        {
            Direction = 0, Height = ExpandedHeight, AlignItems = FlexAlign.Center,
            Padding = new Edges4(2f, 0f, 0f, 0f),                  // WinUI rest left-padding column
            ClipToBounds = true,                                   // a mis-sized bar must never paint over the page
            Children = kids.ToArray(),
        };
        var appliedRoot = Parts.Apply(PartRoot, root);
        var result = appliedRoot with
        {
            Height = ExpandedHeight, Children = root.Children,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => _root = h, appliedRoot.OnRealized),
        };
        _cachedTree = result;
        _cacheKey = key;
        return result;
    }

    BoxEl Caption(string part, string glyph, Action onClick, CaptionButton.Style style, bool active,
                  Action<NodeHandle> capture)
    {
        var b = CaptionButton.Create(glyph, onClick, style, active);
        var applied = Parts.Apply(part, b);
        return applied with
        {
            OnClick = onClick, Role = AutomationRole.Button, Children = b.Children,
            OnRealized = TemplateParts.Chain(capture, applied.OnRealized),
        };
    }

    /// <summary>Build + push the non-client region report (CLIENT DIP). Order is the hit-test contract: interactive
    /// islands first, buttons next, the whole-bar Caption band last (first match wins in WM_NCHITTEST).
    /// Also feeds back the measured content-column width that <see cref="Content"/> clamps against.</summary>
    void PushRegions(InputHooks hooks)
    {
        if (hooks.GetNodeRect is not { } rectOf) return;
        // Grow=1 + Shrink=1 ⇒ the column's laid-out width IS the available content space, tracking the viewport in
        // BOTH directions. Equality-gated signal write: re-renders (and re-pushes) only when the measurement
        // actually changed (e.g. a window resize).
        if (!_contentCol.IsNull)
        {
            float w = rectOf(_contentCol).W;
            if (MathF.Abs(w - _availDip.Peek()) > 0.5f) _availDip.Value = w;
        }
        if (hooks.SetTitleBarRegions is not { } push) return;
        int n = 0;
        if (ShowBackButton && !_back.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_back), TitleBarHit.Client);
        if (ShowPaneToggle && !_pane.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_pane), TitleBarHit.Client);
        if (Tabs is not null && !_tabs.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_tabs), TitleBarHit.Client);
        if (Content is not null && !_content.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_content), TitleBarHit.Client);
        if (ShowCaptionButtons)
        {
            if (!_min.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_min), TitleBarHit.MinButton);
            if (!_max.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_max), TitleBarHit.MaxButton);
            if (!_close.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_close), TitleBarHit.CloseButton);
        }
        if (!_root.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_root), TitleBarHit.Caption);
        push(_regions, n);
    }
}
