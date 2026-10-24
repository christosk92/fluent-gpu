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
    /// <summary>Live enabled state of the pane toggle (see <see cref="TitleBar.PaneToggleEnabledSignal"/>).</summary>
    public IReadSignal<bool>? PaneToggleEnabledSignal { get; init; }
    /// <summary>The pane toggle's tooltip, read per render (see <see cref="TitleBar.PaneToggleToolTip"/>).</summary>
    public Func<string>? PaneToggleToolTip { get; init; }
    public Func<IReadSignal<float>, Element>? Content { get; init; }
    public Func<Element>? Tabs { get; init; }
    public Func<int>? TabsVersion { get; init; }
    /// <summary>In merged mode, make the tabs island the row's ONE shrinkable lane: it still HUGS its content (so all
    /// trailing slack stays caption drag band, never a Client region), but it is now the only island that gives when the
    /// row overruns — the centre column stops flexing and becomes app-sized. Use with a tab strip that owns a real
    /// horizontal viewport, so overflow clips and scrolls instead of squeezing the search field or the identity cluster.</summary>
    public bool TabsElasticLane { get; init; }
    /// <summary>In merged mode, while this answers true the tabs island FILLS the row's free lane instead of hugging (a search field that takes the
    /// lead island at a narrow window): the drag bands beside the centre column stop growing, so the field gets the room; the fixed drag strip before
    /// the captions stays. Read each render (a signal read here re-renders the bar).</summary>
    public Func<bool>? TabsFill { get; init; }
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
    /// <summary>The row's fixed spacing, read per render (see <see cref="TitleBar.RowSpacing"/>); null keeps <see cref="TitleBarSpacing.Default"/>.</summary>
    public Func<TitleBarSpacing>? RowSpacing { get; init; }
    /// <summary>The bar's height (see <see cref="TitleBar.BarHeight"/>): <see cref="TitleBar.ExpandedHeight"/> (48) by default. A static
    /// value, or a thunk / signal read at every render (a window tier: 56 at Huge4K), so the bar resizes live without a remount. The
    /// caption buttons stretch to it, every island and drag band takes it, and the reported drag / button regions follow it.</summary>
    public Prop<float> Height { get; init; } = TitleBar.ExpandedHeight;

    // ── EP16: the shell rows (see TitleBar.Rows). Any of these switches the bar to the shell layout; unset, the bar is the
    //    one-row bar above, unchanged. ───────────────────────────────────────────────────────────────────────────────
    /// <summary>1 or 2, read at every render (see <see cref="TitleBar.Rows"/>). 2 = a <see cref="TopRowHeight"/> tab row
    /// with the caption buttons over a <see cref="Height"/> toolbar row.</summary>
    public Prop<int> Rows { get; init; } = 1;
    /// <summary>The tab row's height when <see cref="Rows"/> is 2 (see <see cref="TitleBar.TopRowHeight"/>); 40 by default.</summary>
    public Prop<float> TopRowHeight { get; init; } = TitleBar.TabRowHeight;
    /// <summary>Keyed toolbar children at the row's leading edge (see <see cref="TitleBar.Lead"/>).</summary>
    public Func<IReadOnlyList<TitleBarItem>>? Lead { get; init; }
    /// <summary>The non-interactive app mark after <see cref="Lead"/> (see <see cref="TitleBar.Wordmark"/>).</summary>
    public Func<Element>? Wordmark { get; init; }
    /// <summary>Keyed toolbar children at the row's trailing edge (see <see cref="TitleBar.TrailingItems"/>).</summary>
    public Func<IReadOnlyList<TitleBarItem>>? TrailingItems { get; init; }
    /// <summary>Where the shell toolbar centres <see cref="CenterContent"/> (see <see cref="TitleBar.CenterAlign"/>).</summary>
    public Prop<TitleBarCenterAlign> CenterAlign { get; init; } = TitleBarCenterAlign.BetweenClusters;
    /// <summary>The shell toolbar's cluster gap and insets (see <see cref="TitleBar.ToolbarSpacing"/>).</summary>
    public Func<TitleBarToolbarSpacing>? ToolbarSpacing { get; init; }
}

/// <summary>The shell toolbar's spacing: the <paramref name="Gap"/> between a cluster's children (outside every child, so
/// it is drag space), the leading cluster's <paramref name="LeadInset"/> and, in a two-row bar, the trailing cluster's
/// <paramref name="TrailInset"/> (a one-row bar ends in the caption buttons instead).</summary>
public readonly record struct TitleBarToolbarSpacing(float Gap, float LeadInset, float TrailInset)
{
    /// <summary>The WinUI shell toolbar: 4 between buttons, 4 before the first, 8 after the last.</summary>
    public static TitleBarToolbarSpacing Default => new(4f, 4f, 8f);
}

/// <summary>One KEYED title-bar child (<see cref="TitleBar.Lead"/>, <see cref="TitleBar.TrailingItems"/>). The bar reports each
/// child's own laid-out rect as a <see cref="FluentGpu.Pal.TitleBarHit.Client"/> region, so the gaps between children stay
/// drag space, and re-reports in the SAME frame a child is swapped (a new <paramref name="Key"/>), added, removed or resized:
/// no version counter to feed. <paramref name="Build"/> runs when the bar rebuilds; content that changes on its own state
/// should be its own component (it subscribes itself, and its resize still re-reports through the bar's bounds watch).</summary>
/// <param name="Key">Stable identity within its slot. A different key is a different child (remounted).</param>
/// <param name="Build">The child's element (should HUG: its rect is the reported Client region).</param>
public readonly record struct TitleBarItem(string Key, Func<Element> Build);

/// <summary>Where the shell toolbar row places its centre island.</summary>
public enum TitleBarCenterAlign : byte
{
    /// <summary>In the free space between the leading and trailing clusters (the island centres in that space).</summary>
    BetweenClusters,
    /// <summary>On the window's centre line: both sides take an equal share (CSS <c>1fr auto 1fr</c>), so the island stays
    /// centred whatever the clusters weigh. <see cref="TitleBar.CenterAvail"/> is then the widest centred span that clears
    /// the heavier cluster.</summary>
    Window,
}

/// <summary>The title bar row's two fixed spacers: the lead pad after the back / pane buttons (WinUI's left-header padding column,
/// 14) and the guaranteed-grabbable drag strip before the caption cluster (WinUI's minimum drag column, 48). A narrow window narrows
/// them so its buttons fit; a drag strip of 0 leaves only the bar's own grow bands as drag space, so keep one wide enough to grab.</summary>
/// <param name="LeadPad">The lead pad's width.</param>
/// <param name="MinDragStrip">The drag strip's width.</param>
public readonly record struct TitleBarSpacing(float LeadPad, float MinDragStrip)
{
    /// <summary>WinUI's TitleBar: a 14-DIP lead pad and a 48-DIP drag strip.</summary>
    public static TitleBarSpacing Default => new(14f, 48f);
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
    /// <summary>SHELL ROWS: the two-row bar's top (tab) row. Owned: Children, Height.</summary>
    public const string PartTopRow = "TopRow";
    /// <summary>SHELL ROWS: the toolbar row (the one row of a one-row shell bar is the root). Owned: Children, Height.</summary>
    public const string PartToolbarRow = "ToolbarRow";
    /// <summary>SHELL ROWS: the non-interactive wordmark host. Owned: Children, HitTestVisible (false: it is drag space).</summary>
    public const string PartWordmark = "Wordmark";

    /// <summary>The two-row bar's tab row height (WinUI TabView strip on Mica Alt).</summary>
    public const float TabRowHeight = 40f;

    /// <summary>WinUI TitleBar tall mode — the height when Content is set (the gallery look).</summary>
    public const float ExpandedHeight = 48f;
    /// <summary>WinUI TitleBar compact mode (no content; reserved — this control always renders tall for now).</summary>
    public const float CompactHeight = 32f;

    const float NavButtonSize = 40f;     // back/pane button width (WinUI TitleBar back/pane = 40w)
    const float IconSize = 16f;          // WinUI icon Viewbox 16×16

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
    /// <summary>Live enabled state of the pane toggle. Null ⇒ enabled. Reading it subscribes the bar, so the toggle
    /// re-glyphs enabled↔disabled in place (the <see cref="BackEnabledSignal"/> pattern) — a shell that pins its pane
    /// open (an edit mode) binds this instead of hiding the button.</summary>
    public IReadSignal<bool>? PaneToggleEnabledSignal;
    /// <summary>The pane toggle's tooltip text, invoked per render (WinUI's PaneToggleButton names its action:
    /// "Collapse navigation" / "Expand navigation"). Null or "" ⇒ no tooltip. Its text is part of the memo key.</summary>
    public Func<string>? PaneToggleToolTip;
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
    /// <summary>The tabs island fills the free lane while this answers true (see <see cref="TitleBarOptions.TabsFill"/>).</summary>
    public Func<bool>? TabsFill;
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
    /// <summary>The row's lead pad and drag strip (<see cref="TitleBarSpacing"/>), read at EVERY render — not mount-time: a thunk
    /// that reads a signal (a window tier) re-renders the bar and re-reports its regions when it changes. Null keeps
    /// <see cref="TitleBarSpacing.Default"/> (WinUI's 14 and 48).</summary>
    public Func<TitleBarSpacing>? RowSpacing;
    /// <summary>The lead pad's width ease when <see cref="RowSpacing"/> changes it (a window tier that moves the app's gutter): null snaps it, the
    /// default. A size channel through real layout (a <see cref="SizeMode.Reflow"/> on the width axis), so the islands after it ride the re-solve;
    /// while the pad eases the non-client regions re-push from its bounds, so the islands' hit regions follow it frame by frame.</summary>
    public LayoutTransition? LeadPadMotion;
    /// <summary>The same for the drag strip before the caption cluster (<see cref="TitleBarSpacing.MinDragStrip"/>): null snaps it, the default.</summary>
    public LayoutTransition? DragStripMotion;
    /// <summary>The bar's height, read at EVERY render (a static value, a thunk or a signal; <see cref="TitleBarOptions.Height"/>):
    /// <see cref="ExpandedHeight"/> (48) by default. The caption buttons are this tall, the back / pane buttons are 4 shorter (WinUI's
    /// 44 in 48), the islands and drag bands take it, and a change re-reports the non-client regions in the same frame.</summary>
    public Prop<float> BarHeight = ExpandedHeight;

    // ── EP16: the shell rows ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>1 or 2 rows, read at EVERY render (a static value, a thunk or a signal: tabs on/off flips it live).
    /// <para><b>2 rows:</b> a <see cref="TopRowHeight"/> (40) tab row holding <see cref="Tabs"/>, the free drag band, the
    /// drag strip, <see cref="CaptionLeading"/> and the caption buttons (as tall as the row), over a <see cref="BarHeight"/>
    /// (48) toolbar row: the built-in back / pane / icon / title, <see cref="Lead"/>, <see cref="Wordmark"/>, the centre
    /// island (<see cref="CenterContent"/>), <see cref="TrailingItems"/> and <see cref="Trailing"/>. The tab strip gets a
    /// <see cref="TabStripHost"/> (lane + row height) through <see cref="TabStrip.HostContext"/>.</para>
    /// <para><b>The shell layout</b> (2 rows, or 1 row with any of <see cref="Lead"/>, <see cref="Wordmark"/>,
    /// <see cref="TrailingItems"/>): the toolbar row alone, captions at its end. Drag space is every pixel of either row
    /// that no island, keyed child or button covers (one Caption catch-all spans both rows), and every island re-reports
    /// in the frame it moves: <see cref="ContentVersion"/> is not needed there. Without those slots and with 1 row the bar
    /// is the one-row bar, unchanged.</para></summary>
    public Prop<int> Rows = 1;
    /// <summary>The tab row's height in a two-row bar, read at every render: <see cref="TabRowHeight"/> (40) by default.</summary>
    public Prop<float> TopRowHeight = TabRowHeight;
    /// <summary>SHELL: keyed children at the toolbar's leading edge (hamburger, Back, Forward), after the built-in parts.
    /// Each child is its own Client region (the gaps stay drag space); see <see cref="TitleBarItem"/>. Invoked at every render
    /// (the key list is part of the render memo), so a list that reads a signal re-forms the bar when it changes.</summary>
    public Func<IReadOnlyList<TitleBarItem>>? Lead;
    /// <summary>SHELL: the app mark after <see cref="Lead"/>. NOT interactive: hit-test invisible and reported as drag space
    /// (WinUI treats the app title as caption).</summary>
    public Func<Element>? Wordmark;
    /// <summary>SHELL: keyed children at the toolbar's trailing edge (pin, Friends, bell, avatar), before the legacy
    /// <see cref="Trailing"/> island; each is its own Client region. Same contract as <see cref="Lead"/>.</summary>
    public Func<IReadOnlyList<TitleBarItem>>? TrailingItems;
    /// <summary>SHELL: where the centre island sits (<see cref="TitleBarCenterAlign"/>), read at every render.</summary>
    public Prop<TitleBarCenterAlign> CenterAlign = TitleBarCenterAlign.BetweenClusters;
    /// <summary>SHELL: the toolbar clusters' gap and edge insets (<see cref="TitleBarToolbarSpacing"/>), read at every render.
    /// Null keeps <see cref="TitleBarToolbarSpacing.Default"/>.</summary>
    public Func<TitleBarToolbarSpacing>? ToolbarSpacing;
    /// <summary>SHELL, 2 rows: the width the tab strip may use (the tabs island plus the free drag band after it), measured
    /// after layout. The same signal the strip reads through its <see cref="TabStripHost"/>.</summary>
    public IReadSignal<float> TabsLane => _tabsLane;

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
                PaneToggleEnabledSignal = options.PaneToggleEnabledSignal,
                PaneToggleToolTip = options.PaneToggleToolTip,
                Tabs = options.Tabs, TabsVersion = options.TabsVersion, TabsElasticLane = options.TabsElasticLane, TabsFill = options.TabsFill,
                Trailing = options.Trailing, CaptionLeading = options.CaptionLeading,
                ContentVersion = options.ContentVersion,
                ShowRailBaseline = options.ShowRailBaseline,
                ShowCaptionButtons = options.ShowCaptionButtons, Parts = options.Parts, RowSpacing = options.RowSpacing,
                BarHeight = options.Height,
                Rows = options.Rows, TopRowHeight = options.TopRowHeight, Lead = options.Lead, Wordmark = options.Wordmark,
                TrailingItems = options.TrailingItems, CenterAlign = options.CenterAlign, ToolbarSpacing = options.ToolbarSpacing,
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
        _rowHooks = hooks;
        int epoch = hooks.WindowChromeEpoch?.Value ?? 0;          // subscribe: re-render on activation/placement change
        var viewport = UseContext(Viewport.Size);                 // subscribe: re-report regions on window resize/DPI hop
        bool active = hooks.IsWindowActive?.Invoke() ?? true;
        bool maximized = hooks.GetWindowState?.Invoke() == WindowState.Maximized;
        int tabsVer = TabsVersion?.Invoke() ?? 0;                 // subscribe: re-render + re-push regions on tab add/remove
        int contentVer = ContentVersion?.Invoke() ?? 0;           // subscribe: re-render + re-push regions on island resize/shape change
        bool backEnabled = BackEnabledSignal is { } bes ? bes.Value : BackEnabled;   // subscribe: re-glyph the back button live
        bool paneEnabled = PaneToggleEnabledSignal is { } pes ? pes.Value : true;   // subscribe: re-glyph the toggle live
        string paneTip = PaneToggleToolTip?.Invoke() ?? "";
        var spacing = RowSpacing?.Invoke() ?? TitleBarSpacing.Default;   // subscribe: a thunk over a tier re-renders on a change
        float leadPad = MathF.Max(0f, spacing.LeadPad), dragStrip = MathF.Max(0f, spacing.MinDragStrip);
        float barH = MathF.Max(1f, BarHeight.Current());             // subscribe: a bound height (a tier) re-renders + re-reports
        int rows = Rows.Current() >= 2 ? 2 : 1;                      // subscribe: tabs on/off flips the row count live
        bool wasShell = _shellMode;
        _shellMode = rows == 2 || Lead is not null || Wordmark is not null || TrailingItems is not null;
        if (wasShell != _shellMode) { _cachedTree = null; _shellBuilt = false; }   // never serve the other layout's tree
        if (_shellMode)
            return RenderShell(hooks, viewport, epoch, active, maximized, tabsVer, contentVer, backEnabled, paneEnabled, paneTip,
                               leadPad, dragStrip, barH, rows);

        // Report the drag/button regions after THIS render's layout settles (phase 6.5) — deps cover everything that
        // moves the parts (resize, maximize→WM_SIZE→viewport, DPI hop→DIP viewport change, the measured-width feedback
        // render whose island rect must re-push, and the tab-set revision so the strip island re-reports on change).
        // ContentVersion rides BOTH keys: a merged island that resizes without moving any other dep (an expanding search
        // box) must re-render AND re-push its region in the same frame, or the stale rect leaves dead drag space.
        UseLayoutEffect(() => PushRegions(hooks),
            DepKey.From(HashCode.Combine(HashCode.Combine(viewport.Width, viewport.Height, epoch, _availDip.Peek(), tabsVer, ShowBackButton, ShowPaneToggle, ShowCaptionButtons), contentVer, paneEnabled, leadPad, dragStrip, barH)));

        // Memo gate: a resize-only re-render returns the cached tree alloc-free (the layout effect above already re-ran
        // — its viewport deps changed — so regions re-push without a rebuild). Key excludes the viewport on purpose.
        // Tok.Epoch is in the key so a live theme switch busts the cache — otherwise RethemeAll re-runs this effect but
        // the memo returns the OLD-theme tree (the caption glyphs/foregrounds would stay stale).
        int key = unchecked(((((((epoch * 397 ^ tabsVer) * 397 ^ contentVer) * 397 ^ _availDip.Peek().GetHashCode()) * 397 ^ Tok.Epoch) * 397
                              ^ StringComparer.Ordinal.GetHashCode(paneTip)) * 397)
            ^ ((active ? 1 : 0) | (maximized ? 2 : 0) | (ShowBackButton ? 4 : 0) | (ShowPaneToggle ? 8 : 0) | (ShowCaptionButtons ? 16 : 0) | (backEnabled ? 32 : 0) | (ShowRailBaseline ? 64 : 0) | (TabsElasticLane ? 128 : 0) | (paneEnabled ? 256 : 0)));
        key = HashCode.Combine(key, leadPad, dragStrip, barH);
        if (_cachedTree is { } cached && key == _cacheKey) return cached;

        var kids = new List<Element>(14);
        AddNavCluster(kids, barH, active, backEnabled, paneEnabled, paneTip, leadPad, alwaysLeadPad: true);

        bool merged = Merged;
        // The bar's flexible/fixed drag bands. `rail` = does this shape carry the Notepad TabView hairline through its
        // caption band (tabbed or merged chrome), and did the caller keep it. Keeping the ink OUTSIDE every island is
        // what preserves the HTCAPTION contract — the band is drag space that happens to be painted.
        bool rail = (Tabs is not null || merged) && ShowRailBaseline;
        BoxEl Band(float width, float grow, float shrink) => rail
            ? TabStrip.RailBaselineHost(width, grow) with { Shrink = shrink, Height = barH }
            : new BoxEl { Width = width, Grow = grow, Shrink = shrink, Height = barH };

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
        bool tabsFill = merged && Tabs is not null && TabsFill?.Invoke() == true;
        BoxEl TabsIsland(Func<Element> f) => new()
        {
            Direction = 0, AlignItems = FlexAlign.Stretch,
            Grow = tabsFill ? 1f : 0f, Shrink = 1, MinWidth = 0f, Height = barH,
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
            kids.Add(Band(float.NaN, tabsFill ? 0f : 1f, 1f));

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
                Height = barH,
                Opacity = active ? 1f : 0.5f,                      // WinUI deactivated content dim
                Children = [centerIsland],
            };
            var appliedCenter = Parts.Apply(PartCenterContent, centerCol);
            kids.Add(appliedCenter with
            {
                Children = centerCol.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _centerCol = h, appliedCenter.OnRealized),
            });

            kids.Add(Band(float.NaN, tabsFill ? 0f : 1f, 1f));

            if (Trailing is { } trailingFunc)
            {
                // Hugs: its laid-out rect is reported wholesale as Client, so any slack inside it is dead drag space.
                var trailingIsland = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = barH,
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
                Height = barH,
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
        kids.Add((Tabs is not null || merged
            ? Band(dragStrip, 0f, 0f)
            : new BoxEl { Width = dragStrip }) with
            {
                Animate = DragStripMotion,
                OnBoundsChanged = DragStripMotion is null ? null : _leadPadBounds ??= _ => RequestRegionPush(),
            });

        if (CaptionLeading is { } captionLeadingFunc)
        {
            var captionLeading = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = barH,
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
                             CaptionButton.MinMax, active, barH, h => _min = h));
            kids.Add(Caption(PartCaptionMax, maxGlyph, () => hooks.WindowToggleMaximize?.Invoke(),
                             CaptionButton.MinMax, active, barH, h => _max = h));
            kids.Add(Caption(PartCaptionClose, Icons.ChromeClose, () => hooks.WindowClose?.Invoke(),
                             CaptionButton.Close, active, barH, h => _close = h));
        }
        else
        {
            // Standard OS frame: keep the bar's content clear of the shell-drawn caption buttons.
            kids.Add(new BoxEl { Width = 140f });
        }

        var root = new BoxEl
        {
            Direction = 0, Height = barH, AlignItems = FlexAlign.Center,
            Padding = new Edges4(2f, 0f, 0f, 0f),                  // WinUI rest left-padding column
            ClipToBounds = true,                                   // a mis-sized bar must never paint over the page
            Children = kids.ToArray(),
        };
        var appliedRoot = Parts.Apply(PartRoot, root);
        var result = appliedRoot with
        {
            Height = barH, Children = root.Children,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => _root = h, appliedRoot.OnRealized),
        };
        _cachedTree = result;
        _cacheKey = key;
        return result;
    }

    // ── EP16: the shell rows ───────────────────────────────────────────────────────────────────────────────────────
    //  [row 1, 2 rows only]  [tabs island (hug)] [grow drag band] [drag strip] [CaptionLeading] [captions, row-1 tall]
    //  [toolbar row]         [left cluster: nav + Lead items + Wordmark] [centre column → hugging island] [right cluster:
    //                         TrailingItems + Trailing (+ drag strip, CaptionLeading, captions in a one-row bar)]
    //  Everything not covered by an island, keyed child or button is the Caption catch-all (one rect over both rows). Every
    //  reported node carries the shared bounds watch, so a child that moves or resizes re-reports in the same frame.

    sealed class KeyedSlot
    {
        public NodeHandle Node;
        public readonly Action<NodeHandle> Capture;
        public bool Seen;
        public KeyedSlot() => Capture = h => Node = h;
    }

    bool _shellMode, _shellBuilt, _shellPushQueued;
    InputHooks? _shellHooks;
    Action? _shellPush, _shellPushQueuedAction;
    Action<RectF>? _islandBounds;
    int _shellRows = 1;
    TitleBarCenterAlign _shellAlign;
    readonly Signal<float> _tabsLane = new(float.PositiveInfinity);
    TabStripHost? _stripHost;
    NodeHandle _topRow, _toolbarRow, _tabsBand, _leftCluster, _rightCluster;
    readonly Dictionary<string, KeyedSlot> _keyed = new(StringComparer.Ordinal);
    readonly List<KeyedSlot> _leadSlots = new(), _trailSlots = new();
    TitleBarRegion[] _shellRegions = new TitleBarRegion[16];

    static int KeysHash(IReadOnlyList<TitleBarItem> items)
    {
        int h = items.Count;
        for (int i = 0; i < items.Count; i++) h = unchecked(h * 397 ^ StringComparer.Ordinal.GetHashCode(items[i].Key));
        return h;
    }

    InputHooks? _rowHooks;
    Action<RectF>? _leadPadBounds;
    bool _rowPushQueued;
    Action? _rowPushQueuedAction;

    /// <summary>Re-push the regions after this frame's layout (the lead pad eased: every island after it moved). Coalesced to one push a frame.</summary>
    void RequestRegionPush()
    {
        if (_shellMode) { RequestShellPush(); return; }
        if (_rowPushQueued || _rowHooks?.QueueAfterLayout is not { } queue) return;
        _rowPushQueued = true;
        queue(_rowPushQueuedAction ??= () =>
        {
            _rowPushQueued = false;
            if (!_shellMode && _rowHooks is { } h) PushRegions(h);
        });
    }

    void RequestShellPush()
    {
        if (_shellPushQueued || _shellHooks?.QueueAfterLayout is not { } queue) return;
        _shellPushQueued = true;
        queue(_shellPushQueuedAction ??= () =>
        {
            _shellPushQueued = false;
            if (_shellMode && _shellHooks is { } h) PushShellRegions(h);
        });
    }

    bool LiveNode(NodeHandle n) => !n.IsNull && (Context.Scene is not { } s || s.IsLive(n));

    Element RenderShell(InputHooks hooks, Size2 viewport, int epoch, bool active, bool maximized, int tabsVer, int contentVer,
                        bool backEnabled, bool paneEnabled, string paneTip, float leadPad, float dragStrip, float barH, int rows)
    {
        _shellHooks = hooks;
        _shellPush ??= () => PushShellRegions(_shellHooks!);
        _islandBounds ??= _ => RequestShellPush();
        float topH = rows == 2 ? MathF.Max(1f, TopRowHeight.Current()) : 0f;   // subscribe: a tier-bound row height
        var align = CenterAlign.Current();                                      // subscribe: a tier-bound centring
        var lead = Lead?.Invoke() ?? [];                                        // subscribe: the key lists ARE the shape
        var trail = TrailingItems?.Invoke() ?? [];
        int keys = unchecked(KeysHash(lead) * 31 ^ KeysHash(trail));
        float centerAvail = _availDip.Value;                                    // subscribe: the centre island clamps to it
        _shellRows = rows;
        _shellAlign = align;

        UseLayoutEffect(_shellPush,
            DepKey.From(HashCode.Combine(
                HashCode.Combine(viewport.Width, viewport.Height, epoch, centerAvail, tabsVer, ShowBackButton, ShowPaneToggle, ShowCaptionButtons),
                HashCode.Combine(contentVer, paneEnabled, leadPad, dragStrip, barH),
                HashCode.Combine(topH, (int)align, keys, rows))));

        int key = HashCode.Combine(
            HashCode.Combine(epoch, tabsVer, contentVer, centerAvail, Tok.Epoch, StringComparer.Ordinal.GetHashCode(paneTip)),
            (active ? 1 : 0) | (maximized ? 2 : 0) | (ShowBackButton ? 4 : 0) | (ShowPaneToggle ? 8 : 0) | (ShowCaptionButtons ? 16 : 0)
                | (backEnabled ? 32 : 0) | (ShowRailBaseline ? 64 : 0) | (paneEnabled ? 256 : 0),
            HashCode.Combine(leadPad, dragStrip, barH, topH, (int)align, keys, rows),
            ToolbarSpacing?.Invoke() ?? TitleBarToolbarSpacing.Default);
        if (_shellBuilt && _cachedTree is { } cached && key == _cacheKey) return cached;

        float dim = active ? 1f : 0.5f;
        BoxEl Island(Element child, float height, Action<NodeHandle> capture) => new()
        {
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = height, Opacity = dim,
            OnRealized = capture, OnBoundsChanged = _islandBounds, Children = [child],
        };

        // ── keyed children: one hugging wrapper per key, its own Client region ──
        foreach (var slot in _keyed.Values) slot.Seen = false;
        void Keyed(IReadOnlyList<TitleBarItem> items, string prefix, List<KeyedSlot> slots, List<Element> into)
        {
            slots.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                string k = prefix + items[i].Key;
                if (!_keyed.TryGetValue(k, out var slot)) _keyed[k] = slot = new KeyedSlot();
                slot.Seen = true;
                slots.Add(slot);
                into.Add(new BoxEl
                {
                    Key = k, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Opacity = dim,
                    OnRealized = slot.Capture, OnBoundsChanged = _islandBounds,
                    Children = [items[i].Build()],
                });
            }
        }

        // ── the toolbar row ──
        var left = new List<Element>(8);
        AddNavCluster(left, barH, active, backEnabled, paneEnabled, paneTip, leadPad, alwaysLeadPad: false);
        Keyed(lead, "lead:", _leadSlots, left);
        if (Wordmark is { } wordmark)
        {
            var mark = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, HitTestVisible = false, Opacity = dim,
                Children = [wordmark()],
            };
            left.Add(Parts.Apply(PartWordmark, mark) with { Children = mark.Children, HitTestVisible = false });
        }
        if (rows == 1 && Tabs is { } rowTabs)
            left.Add(Island(rowTabs(), barH, h => _tabs = h) with { Shrink = 1f, MinWidth = 0f, ClipToBounds = true });

        var right = new List<Element>(8);
        Keyed(trail, "trail:", _trailSlots, right);
        if (Trailing is { } trailingFunc)
        {
            var island = Island(trailingFunc(), barH, h => _trailing = h);
            var applied = Parts.Apply(PartTrailing, island);
            right.Add(applied with
            {
                Children = island.Children, OnBoundsChanged = _islandBounds,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _trailing = h, applied.OnRealized),
            });
        }

        // Prune the children that left both slots (their handles are dead).
        if (_keyed.Count > _leadSlots.Count + _trailSlots.Count)
        {
            List<string>? gone = null;
            foreach (var kv in _keyed) if (!kv.Value.Seen) (gone ??= new()).Add(kv.Key);
            if (gone is not null) foreach (var k in gone) _keyed.Remove(k);
        }

        void AddCaptions(List<Element> into, float height)
        {
            into.Add(new BoxEl { Width = dragStrip, Shrink = 0f });
            if (CaptionLeading is { } captionLeadingFunc)
            {
                var island = Island(captionLeadingFunc(), height, h => _captionLeading = h);
                var applied = Parts.Apply(PartCaptionLeading, island);
                into.Add(applied with
                {
                    Children = island.Children, OnBoundsChanged = _islandBounds,
                    OnRealized = TemplateParts.Chain<NodeHandle>(h => _captionLeading = h, applied.OnRealized),
                });
            }
            if (ShowCaptionButtons)
            {
                string maxGlyph = maximized ? Icons.ChromeRestore : Icons.ChromeMaximize;
                into.Add(Caption(PartCaptionMin, Icons.ChromeMinimize, () => hooks.WindowMinimize?.Invoke(),
                                 CaptionButton.MinMax, active, height, h => _min = h));
                into.Add(Caption(PartCaptionMax, maxGlyph, () => hooks.WindowToggleMaximize?.Invoke(),
                                 CaptionButton.MinMax, active, height, h => _max = h));
                into.Add(Caption(PartCaptionClose, Icons.ChromeClose, () => hooks.WindowClose?.Invoke(),
                                 CaptionButton.Close, active, height, h => _close = h));
            }
            else into.Add(new BoxEl { Width = 140f, Shrink = 0f });   // a standard OS frame owns the captions
        }
        // One row: the captions end the toolbar, outside the gapped trailing cluster (they abut, flush right).
        Element[] captionGroup = [];
        if (rows == 1)
        {
            var caps = new List<Element>(5);
            AddCaptions(caps, barH);
            captionGroup = caps.ToArray();
        }
        var spacing = ToolbarSpacing?.Invoke() ?? TitleBarToolbarSpacing.Default;   // subscribe: a tier-bound spacing

        // The centre: a column that owns the slack, around an island that HUGS (its flanks stay drag space).
        var centerBuild = CenterContent ?? Content;
        var centerIsland = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            OnRealized = h => _center = h, OnBoundsChanged = _islandBounds,
            Children = centerBuild is { } cb ? [cb(centerAvail)] : [],
        };
        bool windowCentred = align == TitleBarCenterAlign.Window;
        var centerCol = new BoxEl
        {
            Grow = windowCentred ? 0f : 1f, Shrink = 1f, MinWidth = 0f, Direction = 0,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Height = barH, Opacity = dim,
            Children = [centerIsland],
        };
        var appliedCenter = Parts.Apply(PartCenterContent, centerCol);
        var center = appliedCenter with
        {
            Children = centerCol.Children,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => _centerCol = h, appliedCenter.OnRealized),
            OnBoundsChanged = _islandBounds,
        };

        var leftCluster = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = rows == 1 && Tabs is not null ? 1f : 0f, MinWidth = 0f,
            Gap = spacing.Gap, Padding = new Edges4(spacing.LeadInset, 0f, 0f, 0f),
            Height = barH, OnRealized = h => _leftCluster = h, OnBoundsChanged = _islandBounds, Children = left.ToArray(),
        };
        var rightCluster = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = barH,
            Gap = spacing.Gap, Padding = new Edges4(0f, 0f, rows == 2 ? spacing.TrailInset : 0f, 0f),
            OnRealized = h => _rightCluster = h, OnBoundsChanged = _islandBounds, Children = right.ToArray(),
        };
        Element[] rightKids = captionGroup.Length == 0 ? [rightCluster] : [rightCluster, .. captionGroup];
        Element[] toolbarKids = windowCentred
            ? [
                // CSS `1fr auto 1fr`: equal zero-basis sides keep the centre on the window's centre line.
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Height = barH,
                            Children = [leftCluster] },
                center,
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Grow = 1f, Basis = 0f, Shrink = 1f,
                            MinWidth = 0f, Height = barH, Children = rightKids },
              ]
            : [leftCluster, center, .. rightKids];
        var toolbar = new BoxEl
        {
            Direction = 0, Height = barH, AlignItems = FlexAlign.Center, ClipToBounds = true, Children = toolbarKids,
        };
        var appliedToolbar = Parts.Apply(PartToolbarRow, toolbar);
        var toolbarRow = appliedToolbar with
        {
            Height = barH, Children = toolbar.Children,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => _toolbarRow = h, appliedToolbar.OnRealized),
        };

        BoxEl root;
        if (rows == 2)
        {
            bool rail = Tabs is not null && ShowRailBaseline;
            BoxEl Band(float width, float grow) => rail
                ? TabStrip.RailBaselineHost(width, grow) with { Shrink = grow > 0f ? 1f : 0f, Height = topH }
                : new BoxEl { Width = width, Grow = grow, Shrink = grow > 0f ? 1f : 0f, Height = topH };

            var top = new List<Element>(8);
            if (Tabs is { } tabsFunc)
            {
                _stripHost ??= new TabStripHost(_tabsLane, TopRowHeight);
                top.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 1f, MinWidth = 0f, Height = topH,
                    ClipToBounds = true, Opacity = dim,
                    OnRealized = h => _tabs = h, OnBoundsChanged = _islandBounds,
                    Children = [Ctx.Provide(TabStrip.HostContext, _stripHost, tabsFunc())],
                });
            }
            top.Add(Band(float.NaN, 1f) with { OnRealized = h => _tabsBand = h, OnBoundsChanged = _islandBounds });
            var captions = new List<Element>(5);
            AddCaptions(captions, topH);
            captions[0] = Band(dragStrip, 0f);                      // the drag strip carries the rail like the band
            top.AddRange(captions);

            var topRow = new BoxEl
            {
                Direction = 0, Height = topH, AlignItems = FlexAlign.Center, ClipToBounds = true, Children = top.ToArray(),
            };
            var appliedTop = Parts.Apply(PartTopRow, topRow);
            root = new BoxEl
            {
                Direction = 1, Height = topH + barH, ClipToBounds = true,
                Children =
                [
                    appliedTop with
                    {
                        Height = topH, Children = topRow.Children,
                        OnRealized = TemplateParts.Chain<NodeHandle>(h => _topRow = h, appliedTop.OnRealized),
                    },
                    toolbarRow,
                ],
            };
        }
        else root = new BoxEl { Direction = 1, Height = barH, ClipToBounds = true, Children = [toolbarRow] };

        var appliedRoot = Parts.Apply(PartRoot, root);
        var result = appliedRoot with
        {
            Height = root.Height, Children = root.Children,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => _root = h, appliedRoot.OnRealized),
        };
        _cachedTree = result;
        _cacheKey = key;
        _shellBuilt = true;
        return result;
    }

    /// <summary>The shell rows' region report + measurements. Order: every Client island and keyed child (top row, then
    /// the toolbar left to right), the three buttons, then ONE Caption rect over both rows (first match wins).</summary>
    void PushShellRegions(InputHooks hooks)
    {
        if (hooks.GetNodeRect is not { } rectOf) return;

        // The centre's available width: the column itself between the clusters; on the window centre line, the widest
        // span centred in the row that clears the heavier cluster.
        if (LiveNode(_toolbarRow) && LiveNode(_centerCol))
        {
            float w;
            if (_shellAlign == TitleBarCenterAlign.Window && LiveNode(_leftCluster) && LiveNode(_rightCluster))
            {
                RectF row = rectOf(_toolbarRow);
                // Each side's weight runs from the row edge to the far edge of its cluster (captions included).
                float heavier = MathF.Max(rectOf(_leftCluster).Right - row.X, row.Right - rectOf(_rightCluster).X);
                w = MathF.Max(0f, row.W - 2f * heavier);
            }
            else w = rectOf(_centerCol).W;
            if (MathF.Abs(w - _availDip.Peek()) > 0.5f) _availDip.Value = w;
        }
        // The tab lane: the strip island plus the free band after it (the band is 0 once the strip fills its lane).
        if (_shellRows == 2 && Tabs is not null && LiveNode(_tabs) && LiveNode(_tabsBand))
        {
            float lane = rectOf(_tabs).W + rectOf(_tabsBand).W;
            if (MathF.Abs(lane - _tabsLane.Peek()) > 0.5f) _tabsLane.Value = lane;
        }

        if (hooks.SetTitleBarRegions is not { } push) return;
        int need = 12 + _leadSlots.Count + _trailSlots.Count;
        if (_shellRegions.Length < need) _shellRegions = new TitleBarRegion[need + 8];
        var regions = _shellRegions;
        int n = 0;
        if (_shellRows == 2)
        {
            if (Tabs is not null) AddClient(regions, ref n, _tabs);
            if (CaptionLeading is not null) AddClient(regions, ref n, _captionLeading);
        }
        if (ShowBackButton) AddClient(regions, ref n, _back);
        if (ShowPaneToggle) AddClient(regions, ref n, _pane);
        foreach (var slot in _leadSlots) AddClient(regions, ref n, slot.Node);
        if (_shellRows == 1 && Tabs is not null) AddClient(regions, ref n, _tabs);
        if (CenterContent is not null || Content is not null) AddClient(regions, ref n, _center);
        foreach (var slot in _trailSlots) AddClient(regions, ref n, slot.Node);
        if (Trailing is not null) AddClient(regions, ref n, _trailing);
        if (_shellRows == 1 && CaptionLeading is not null) AddClient(regions, ref n, _captionLeading);
        if (ShowCaptionButtons)
        {
            if (LiveNode(_min)) regions[n++] = new TitleBarRegion(rectOf(_min), TitleBarHit.MinButton);
            if (LiveNode(_max)) regions[n++] = new TitleBarRegion(rectOf(_max), TitleBarHit.MaxButton);
            if (LiveNode(_close)) regions[n++] = new TitleBarRegion(rectOf(_close), TitleBarHit.CloseButton);
        }
        if (LiveNode(_root)) regions[n++] = new TitleBarRegion(rectOf(_root), TitleBarHit.Caption);
        push(regions, n);
    }

    void AddClient(TitleBarRegion[] regions, ref int n, NodeHandle h)
    {
        if (!LiveNode(h) || _shellHooks?.GetNodeRect is not { } rectOf) return;
        RectF r = rectOf(h);
        if (r.W > 0f && r.H > 0f) regions[n++] = new TitleBarRegion(r, TitleBarHit.Client);
    }

    /// <summary>The bar's built-in lead cluster: back, pane toggle, the lead pad, the identity icon, title and subtitle.
    /// <paramref name="alwaysLeadPad"/> keeps the one-row bar's unconditional lead pad; the shell rows add it only after a
    /// built-in part.</summary>
    void AddNavCluster(List<Element> kids, float barH, bool active, bool backEnabled, bool paneEnabled, string paneTip,
                       float leadPad, bool alwaysLeadPad)
    {
        // WinUI back/pane: 40w × 44h with Margin 2 (the hover backplate spans y=2..46 of the 48px bar; adjacent
        // margins give the 4px back↔pane gap and the 2px before the 14px header pad = the 16px pane→icon gap).
        // Deactivated state: foreground → tertiary (fills unchanged).
        var navStyle = IconButton.DefaultStyle with
        {
            Size = NavButtonSize,
            Height = barH - 4f,                                    // 44 in the 48 bar
            Foreground = active ? Tok.TextPrimary : Tok.TextTertiary,
        };
        var navMargin = new Edges4(2f, 2f, 2f, 2f);

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
            var pane = IconButton.Create(Icons.Menu, () => OnPaneToggle?.Invoke(), navStyle, isEnabled: paneEnabled)
                with { Margin = navMargin };
            var applied = Parts.Apply(PartPaneToggle, pane);
            Element paneEl = applied with
            {
                OnClick = pane.OnClick, Role = AutomationRole.Button, Children = pane.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _pane = h, applied.OnRealized),
            };
            kids.Add(paneTip.Length > 0 ? ToolTip.Wrap(paneEl, paneTip) : paneEl);
        }

        if (alwaysLeadPad || ShowBackButton || ShowPaneToggle || IconGlyph.Length > 0 || Title.Length > 0)
            kids.Add(new BoxEl
            {
                Width = leadPad, Animate = LeadPadMotion,
                OnBoundsChanged = LeadPadMotion is null ? null : _leadPadBounds ??= _ => RequestRegionPush(),
            });

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
            kids.Add(Parts is null ? icon : Parts.Apply(PartIcon, icon) with { Children = icon.Children });
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
    }

    BoxEl Caption(string part, string glyph, Action onClick, CaptionButton.Style style, bool active, float height,
                  Action<NodeHandle> capture)
    {
        var b = CaptionButton.Create(glyph, onClick, style, active, height);
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
