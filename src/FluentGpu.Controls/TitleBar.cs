using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;

namespace FluentGpu.Controls;

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

    // ── configuration (set once at construction; live values flow via signals/context) ───────────────────────────────
    public string Title = "";
    public string Subtitle = "";
    /// <summary>App-identity glyph (the gallery uses the accent grid glyph; WinUI uses an ImageIcon). Empty = none.</summary>
    public string IconGlyph = "";
    public ColorF IconColor = Tok.AccentDefault;
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
    /// <summary>False = a standard OS frame owns the caption buttons; the bar keeps a right inset clear of them.</summary>
    public bool ShowCaptionButtons = true;
    public TemplateParts? Parts;

    // Captured part handles (OnRealized fires at mount; the component instance persists across re-renders, so plain
    // fields are the stable store) → the WM_NCHITTEST region report.
    NodeHandle _root, _back, _pane, _content, _min, _max, _close;
    // Reused region buffer: filled in place on each relayout push — no steady-state allocation (7 = islands(3)+buttons(3)+caption).
    readonly TitleBarRegion[] _regions = new TitleBarRegion[7];

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        int epoch = hooks.WindowChromeEpoch?.Value ?? 0;          // subscribe: re-render on activation/placement change
        var viewport = UseContext(Viewport.Size);                 // subscribe: re-report regions on window resize/DPI hop
        bool active = hooks.IsWindowActive?.Invoke() ?? true;
        bool maximized = hooks.GetWindowState?.Invoke() == WindowState.Maximized;

        // Report the drag/button regions after THIS render's layout settles (phase 6.5) — deps cover everything that
        // moves the parts (resize, maximize→WM_SIZE→viewport, DPI hop→DIP viewport change).
        UseLayoutEffect(() => PushRegions(hooks),
            viewport.Width, viewport.Height, epoch, ShowBackButton, ShowPaneToggle, ShowCaptionButtons);

        // WinUI Deactivated state: back/pane foreground → tertiary (fills unchanged).
        var navStyle = IconButton.DefaultStyle with
        {
            Size = NavButtonSize,
            Foreground = active ? Tok.TextPrimary : Tok.TextTertiary,
        };

        var kids = new List<Element>(14);

        if (ShowBackButton)
        {
            var back = IconButton.Create(Icons.Back, () => OnBack?.Invoke(), navStyle, isEnabled: BackEnabled);
            var applied = Parts.Apply(PartBackButton, back);
            kids.Add(applied with
            {
                OnClick = back.OnClick, Role = AutomationRole.Button, Children = back.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _back = h, applied.OnRealized),
            });
        }
        if (ShowBackButton && ShowPaneToggle)
            kids.Add(new BoxEl { Width = 4f });                   // the adjacent 2px+2px button margins
        if (ShowPaneToggle)
        {
            var pane = IconButton.Create(Icons.Menu, () => OnPaneToggle?.Invoke(), navStyle);
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

        // The content column's available width: viewport minus every fixed part. The title/subtitle advance uses a
        // ~6.5px/char Caption-12 estimate — it only sets the SHRINK BREAKPOINT (the px where content starts giving
        // way), never a drawn size, so estimate slop is invisible. Keeps the caption buttons pinned at every width.
        float fixedDip = 2f + LeftHeaderPad + MinDragStrip
                       + (ShowBackButton ? NavButtonSize : 0f)
                       + (ShowBackButton && ShowPaneToggle ? 4f : 0f)
                       + (ShowPaneToggle ? NavButtonSize : 0f)
                       + (IconGlyph.Length > 0 ? IconSize + 16f : 0f)
                       + (Title.Length > 0 ? Title.Length * 6.5f + 8f : 0f)
                       + (Subtitle.Length > 0 ? Subtitle.Length * 6.5f + 16f : 0f)
                       + (ShowCaptionButtons ? 3f * CaptionButton.Width : 140f);
        float contentAvail = MathF.Max(0f, viewport.Width - fixedDip);

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
            Grow = 1, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Height = ExpandedHeight,
            Opacity = active ? 1f : 0.5f,                          // WinUI deactivated content dim
            Children = [island],
        };
        kids.Add(Parts.Apply(PartContent, content) with { Children = content.Children });

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
        return appliedRoot with
        {
            Height = ExpandedHeight, Children = root.Children,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => _root = h, appliedRoot.OnRealized),
        };
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
    /// islands first, buttons next, the whole-bar Caption band last (first match wins in WM_NCHITTEST).</summary>
    void PushRegions(InputHooks hooks)
    {
        if (hooks.SetTitleBarRegions is not { } push || hooks.GetNodeRect is not { } rectOf) return;
        int n = 0;
        if (ShowBackButton && !_back.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_back), TitleBarHit.Client);
        if (ShowPaneToggle && !_pane.IsNull) _regions[n++] = new TitleBarRegion(rectOf(_pane), TitleBarHit.Client);
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
