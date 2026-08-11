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
    /// <summary>Live back-enabled override (see <see cref="TitleBar.BackEnabledSignal"/>) — bind this when the back
    /// button reflects a runtime navigation stack.</summary>
    public IReadSignal<bool>? BackEnabledSignal { get; init; }
    public Action? OnBack { get; init; }
    public bool ShowPaneToggle { get; init; }
    public Action? OnPaneToggle { get; init; }
    public Func<IReadSignal<float>, Element>? Content { get; init; }
    public Func<Element>? Tabs { get; init; }
    public Func<int>? TabsVersion { get; init; }
    /// <summary>In merged mode, make the tabs island the row's ONE shrinkable lane: it still HUGS its content (so all
    /// trailing slack stays caption drag band, never a Client region), but it is now the only island that gives when the
    /// row overruns — the centre column stops flexing and becomes app-sized. Use with a tab strip that owns a real
    /// horizontal viewport, so overflow clips and scrolls instead of squeezing the search field or the identity cluster.</summary>
    public bool TabsElasticLane { get; init; }
    /// <summary>MERGED-ROW slot: the flexible, centred island that sits between the tab strip and the trailing island.
    /// Like <see cref="Content"/> the builder receives the LIVE measured width of its flexible column
    /// (<see cref="TitleBar.CenterAvail"/>) so an expanding search island can clamp itself. Setting this (or
    /// <see cref="Trailing"/>) switches the bar into merged mode; <see cref="Content"/> is then ignored.</summary>
    public Func<IReadSignal<float>, Element>? CenterContent { get; init; }
    /// <summary>MERGED-ROW slot: a hugging island immediately before the drag strip + caption buttons (identity/avatar).</summary>
    public Func<Element>? Trailing { get; init; }
    /// <summary>MERGED-ROW slot immediately before the caption buttons. Unlike <see cref="Trailing"/>, this island is
    /// placed after the drag strip so compact controls can sit flush against Minimize.</summary>
    public Func<Element>? CaptionLeading { get; init; }
    /// <summary>Revision of the <see cref="CenterContent"/>/<see cref="Trailing"/> content — see
    /// <see cref="TitleBar.ContentVersion"/>. Bump it whenever an island CHANGES SIZE OR SHAPE.</summary>
    public Func<int>? ContentVersion { get; init; }
    /// <summary>False suppresses the 1px rail seam drawn through the title bar's drag bands (the drag bands stay).</summary>
    public bool ShowRailBaseline { get; init; } = true;
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
    /// <summary>MERGED ROW: the flexible centre column that hosts <see cref="CenterContent"/>. Owned: OnRealized (column
    /// capture), Children.</summary>
    public const string PartCenterContent = "CenterContent";
    /// <summary>MERGED ROW: the hugging trailing island (identity/avatar). Owned: OnRealized (island capture), Children.</summary>
    public const string PartTrailing = "Trailing";
    /// <summary>MERGED ROW: the hugging client island directly before Minimize. Owned: OnRealized, Children.</summary>
    public const string PartCaptionLeading = "CaptionLeading";
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
    /// <summary>Live override of <see cref="BackEnabled"/>: when set, the back button's enabled state tracks this signal
    /// (component fields freeze at mount, so a shell whose back-availability changes at runtime — a navigation back
    /// stack — binds this instead). Reading it subscribes the bar, so the button re-glyphs enabled↔disabled in place.</summary>
    public IReadSignal<bool>? BackEnabledSignal;
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
    /// <summary>In merged mode, make the tabs island the row's ONE shrinkable lane (it still hugs — see
    /// <see cref="TitleBarOptions.TabsElasticLane"/>).</summary>
    public bool TabsElasticLane;
    /// <summary>MERGED ROW (the one-row chrome): the FLEXIBLE, centred island that lives between the tab strip and the
    /// trailing island — a window search box, a now-playing chip, … Setting this (or <see cref="Trailing"/>) switches the
    /// bar from the classic <c>Content</c>-XOR-<c>Tabs</c> shape into
    /// <c>[back][pane][icon/title][Tabs island][drag][CENTER column][drag][Trailing island][48 drag][captions]</c>.
    /// Invoked per render with the centre column's MEASURED available width (DIP), exactly like <see cref="Content"/>;
    /// the LIVE signal behind that argument is <see cref="CenterAvail"/>.
    /// <para><b>The island must HUG.</b> Whatever this returns is reported wholesale as one
    /// <see cref="TitleBarHit.Client"/> region: dead space inside it is dead DRAG space. Never pad it out with a
    /// <c>Grow</c> filler — the flexible column around it already owns the slack, and that slack stays Caption.</para>
    /// <para><see cref="Content"/> is IGNORED in merged mode (the centre column supersedes it).</para></summary>
    public Func<float, Element>? CenterContent;
    /// <summary>MERGED ROW: a HUGGING island right before the guaranteed drag strip + caption buttons (the identity /
    /// account cluster). Same hug contract as <see cref="CenterContent"/> — its laid-out rect IS the reported
    /// <see cref="TitleBarHit.Client"/> region.</summary>
    public Func<Element>? Trailing;
    /// <summary>A hugging client island after the drag strip and directly before the caption buttons.</summary>
    public Func<Element>? CaptionLeading;
    /// <summary>The measured CENTRE-column width as a LIVE signal — the merged-mode mirror of
    /// <see cref="ContentAvail"/> (they are the same underlying measurement: the bar has exactly one flexible column,
    /// and this is it). Content whose SIZE must track the column at runtime subscribes to this rather than reading the
    /// <see cref="CenterContent"/> lambda argument, because a mounted component's plain fields freeze.</summary>
    public IReadSignal<float> CenterAvail => _availDip;
    /// <summary>A monotonic revision of the <see cref="CenterContent"/>/<see cref="Trailing"/> content — the exact
    /// mirror of <see cref="TabsVersion"/>, and the ONE thing an app must remember to feed.
    /// <para><b>The trap:</b> the region report is pushed from a layout effect keyed on the bar's own deps. An island
    /// that changes SIZE without changing any of those deps (a search box expanding on focus, an avatar gaining a
    /// badge, a chip appearing) relayouts under a report that still describes the OLD rect — so the newly-covered
    /// pixels stay HTCAPTION (clicks start a window drag) while the vacated pixels stay HTCLIENT (dead drag space).
    /// Bump this on the SAME state change that resizes the island and the regions re-push in the same frame.</para>
    /// Read each render, so it is also part of the render memo (the tree rebuilds when it changes).</summary>
    public Func<int>? ContentVersion;
    /// <summary>The 1px rail seam the tabbed/merged bar carries through its drag bands (the Notepad TabView hairline).
    /// False keeps the drag bands (hit-testing is unchanged) and drops only the ink — for a chrome that has no rail.</summary>
    public bool ShowRailBaseline = true;
    /// <summary>False = a standard OS frame owns the caption buttons; the bar keeps a right inset clear of them.</summary>
    public bool ShowCaptionButtons = true;
    public TemplateParts? Parts;

    /// <summary>Merged mode = at least one of the merged-row slots is present. Mount-time (both fields freeze at mount),
    /// so the branch below is stable for the life of the instance.</summary>
    bool Merged => CenterContent is not null || Trailing is not null || CaptionLeading is not null;

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
                ShowBackButton = options.ShowBackButton, BackEnabled = options.BackEnabled,
                BackEnabledSignal = options.BackEnabledSignal, OnBack = options.OnBack,
                ShowPaneToggle = options.ShowPaneToggle, OnPaneToggle = options.OnPaneToggle,
                Tabs = options.Tabs, TabsVersion = options.TabsVersion, TabsElasticLane = options.TabsElasticLane,
                Trailing = options.Trailing, CaptionLeading = options.CaptionLeading,
                ContentVersion = options.ContentVersion,
                ShowRailBaseline = options.ShowRailBaseline,
                ShowCaptionButtons = options.ShowCaptionButtons, Parts = options.Parts,
            };
            if (options.IconColor is { } ic) tb.IconColor = ic;
            if (options.Content is { } content) tb.Content = _ => content(tb.ContentAvail);
            if (options.CenterContent is { } center) tb.CenterContent = _ => center(tb.CenterAvail);
            return tb;
        });

    // Captured part handles (OnRealized fires at mount; the component instance persists across re-renders, so plain
    // fields are the stable store) → the WM_NCHITTEST region report.
    NodeHandle _root, _back, _pane, _contentCol, _content, _tabs, _centerCol, _center, _trailing, _captionLeading, _min, _max, _close;
    // Reused region buffer: filled in place on each relayout push — no steady-state allocation.
    // Merged-row worst case = back + pane + tabs + centre + trailing + 3 buttons + the whole-root Caption = 9
    // (+ the classic `content` island, which never coexists with `centre`, = 10); 12 leaves headroom.
    readonly TitleBarRegion[] _regions = new TitleBarRegion[12];
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
        int contentVer = ContentVersion?.Invoke() ?? 0;           // subscribe: re-render + re-push regions on island resize/shape change
        bool backEnabled = BackEnabledSignal is { } bes ? bes.Value : BackEnabled;   // subscribe: re-glyph the back button live

        // Report the drag/button regions after THIS render's layout settles (phase 6.5) — deps cover everything that
        // moves the parts (resize, maximize→WM_SIZE→viewport, DPI hop→DIP viewport change, the measured-width feedback
        // render whose island rect must re-push, and the tab-set revision so the strip island re-reports on change).
        // ContentVersion rides BOTH keys: a merged island that resizes without moving any other dep (an expanding search
        // box) must re-render AND re-push its region in the same frame, or the stale rect leaves dead drag space.
        UseLayoutEffect(() => PushRegions(hooks),
            DepKey.From(HashCode.Combine(HashCode.Combine(viewport.Width, viewport.Height, epoch, _availDip.Peek(), tabsVer, ShowBackButton, ShowPaneToggle, ShowCaptionButtons), contentVer)));

        // Memo gate: a resize-only re-render returns the cached tree alloc-free (the layout effect above already re-ran
        // — its viewport deps changed — so regions re-push without a rebuild). Key excludes the viewport on purpose.
        // Tok.Epoch is in the key so a live theme switch busts the cache — otherwise RethemeAll re-runs this effect but
        // the memo returns the OLD-theme tree (the caption glyphs/foregrounds would stay stale).
        int key = unchecked(((((((epoch * 397 ^ tabsVer) * 397 ^ contentVer) * 397 ^ _availDip.Peek().GetHashCode()) * 397 ^ Tok.Epoch) * 397))
            ^ ((active ? 1 : 0) | (maximized ? 2 : 0) | (ShowBackButton ? 4 : 0) | (ShowPaneToggle ? 8 : 0) | (ShowCaptionButtons ? 16 : 0) | (backEnabled ? 32 : 0) | (ShowRailBaseline ? 64 : 0) | (TabsElasticLane ? 128 : 0)));
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
            var back = IconButton.Create(Icons.Back, () => OnBack?.Invoke(), navStyle, isEnabled: backEnabled)
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

        bool merged = Merged;
        // The bar's flexible/fixed drag bands. `rail` = does this shape carry the Notepad TabView hairline through its
        // caption band (tabbed or merged chrome), and did the caller keep it. Keeping the ink OUTSIDE every island is
        // what preserves the HTCAPTION contract — the band is drag space that happens to be painted.
        bool rail = (Tabs is not null || merged) && ShowRailBaseline;
        BoxEl Band(float width, float grow, float shrink) => rail
            ? TabStrip.RailBaselineHost(width, grow) with { Shrink = shrink, Height = ExpandedHeight }
            : new BoxEl { Width = width, Grow = grow, Shrink = shrink, Height = ExpandedHeight };

        // A LEFT-aligned tab strip hugging its content (Shrink=1 so a too-full strip gives way before the captions).
        // It is a Client island; the Grow=1 band after it is Caption drag space (WinUI TabStripFooter).
        // The island's laid-out rect IS the reported Client region, so it must END where the strip's last real child
        // does — a strip that padded itself out with a Grow filler would hand that padding to HTCLIENT and make it
        // undraggable (TabStrip therefore hugs; see its Render).
        // MinWidth=0 + ClipToBounds: on a narrow window the island is the one part that yields (the WinUI contract —
        // the caption cluster never moves), so let it shrink past the strip's natural min and clip what doesn't fit
        // instead of letting the strip paint out over the drag strip and the caption buttons.
        // Opacity: the WinUI deactivated dim the content column has always had — the tabs island had none, which read
        // as a title bar half-lit on blur. Composited only; layout and hit-testing are untouched.
        BoxEl TabsIsland(Func<Element> f) => new()
        {
            Direction = 0, AlignItems = FlexAlign.Stretch,
            Shrink = 1, MinWidth = 0f, Height = ExpandedHeight,
            ClipToBounds = true,
            Opacity = active ? 1f : 0.5f,
            OnRealized = h => _tabs = h,
            Children = [f()],
        };

        if (merged)
        {
            // ── the MERGED one-row chrome ────────────────────────────────────────────────────────────────────────
            // [tabs island (hug)] [grow drag band] [flexible CENTRE column → hugging island] [grow drag band]
            // [trailing island (hug)] — then the shared MinDragStrip + caption cluster below.
            // Both grow bands carry equal weight, so the centre island lands midway between the END of the left
            // cluster and the START of the right cluster. (That is NOT the window centre unless the two clusters are
            // the same width; an app that needs true window-centring pads the lighter side inside its own islands.)
            if (Tabs is { } mergedTabs) kids.Add(TabsIsland(mergedTabs));
            kids.Add(Band(float.NaN, 1f, 1f));

            float centerAvail = _availDip.Value;                   // subscribe: re-render when the column is re-measured
            // The interactive island is the INNER box that HUGS its content — never the flexible column, whose empty
            // flanks must stay part of the Caption drag band.
            var centerIsland = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center,
                OnRealized = h => _center = h,
                Children = CenterContent is { } cc ? [cc(centerAvail)] : [],
            };
            var centerCol = new BoxEl
            {
                // The row's ONE Grow+Shrink column: it absorbs all free space AND all overflow, so the caption cluster
                // never moves or clips and the arranged width fed back through _availDip is honest in both directions.
                // TabsElasticLane hands both jobs to the tabs island instead: the flanking drag bands take the free
                // space (keeping this island centred between the clusters) and the tab viewport takes the overflow, so
                // an app-sized search field is never squeezed by a long tab strip.
                Grow = TabsElasticLane ? 0f : 1f, Shrink = TabsElasticLane ? 0f : 1, MinWidth = 0f, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Height = ExpandedHeight,
                Opacity = active ? 1f : 0.5f,                      // WinUI deactivated content dim
                Children = [centerIsland],
            };
            var appliedCenter = Parts.Apply(PartCenterContent, centerCol);
            kids.Add(appliedCenter with
            {
                Children = centerCol.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _centerCol = h, appliedCenter.OnRealized),
            });

            kids.Add(Band(float.NaN, 1f, 1f));

            if (Trailing is { } trailingFunc)
            {
                // Hugs: its laid-out rect is reported wholesale as Client, so any slack inside it is dead drag space.
                var trailingIsland = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = ExpandedHeight,
                    Opacity = active ? 1f : 0.5f,
                    Children = [trailingFunc()],
                };
                var appliedTrailing = Parts.Apply(PartTrailing, trailingIsland);
                kids.Add(appliedTrailing with
                {
                    Children = trailingIsland.Children,
                    OnRealized = TemplateParts.Chain<NodeHandle>(h => _trailing = h, appliedTrailing.OnRealized),
                });
            }
        }
        else if (Tabs is { } tabsFunc)
        {
            kids.Add(TabsIsland(tabsFunc));
            // Notepad carries the TabView bottom hairline through its TabStripFooter/caption drag band.
            kids.Add(Band(float.NaN, 1f, 1f));
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

        // The guaranteed-grabbable drag strip. It stays FIXED even under TabsElasticLane: a CaptionLeading control is
        // already flush against Minimize with the strip at full width, so shrinking it would trade real drag space for
        // nothing.
        kids.Add(Tabs is not null || merged
            ? Band(MinDragStrip, 0f, 0f)
            : new BoxEl { Width = MinDragStrip });

        if (CaptionLeading is { } captionLeadingFunc)
        {
            var captionLeading = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = ExpandedHeight,
                Opacity = active ? 1f : 0.5f,
                Children = [captionLeadingFunc()],
            };
            var appliedCaptionLeading = Parts.Apply(PartCaptionLeading, captionLeading);
            kids.Add(appliedCaptionLeading with
            {
                Children = captionLeading.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _captionLeading = h, appliedCaptionLeading.OnRealized),
            });
        }

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
        // Merged mode measures the CENTRE column (the merged row's single flexible child); the classic bar measures its
        // content column. Exactly one of the two is ever realized, so this picks the live one.
        var flexCol = !_centerCol.IsNull ? _centerCol : _contentCol;
        if (!flexCol.IsNull)
        {
            float w = rectOf(flexCol).W;
            if (MathF.Abs(w - _availDip.Peek()) > 0.5f) _availDip.Value = w;
        }
        if (hooks.SetTitleBarRegions is not { } push) return;
        int n = 0;
        if (ShowBackButton && !_back.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_back), TitleBarHit.Client);
        if (ShowPaneToggle && !_pane.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_pane), TitleBarHit.Client);
        if (Tabs is not null && !_tabs.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_tabs), TitleBarHit.Client);
        if (Content is not null && !_content.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_content), TitleBarHit.Client);
        // Merged islands, LEFT-to-RIGHT and before the buttons (first match wins in WM_NCHITTEST).
        if (CenterContent is not null && !_center.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_center), TitleBarHit.Client);
        if (Trailing is not null && !_trailing.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_trailing), TitleBarHit.Client);
        if (CaptionLeading is not null && !_captionLeading.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_captionLeading), TitleBarHit.Client);
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
