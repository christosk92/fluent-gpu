using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A WinUI Pivot: a row of large (24px SemiLight, −25/1000em tracking) text headers above a content region.
/// Selection is shown by a 3px accent pipe bottom-pinned under the selected header (NOT bold weight): the selected
/// header is <see cref="Tok.TextPrimary"/>, the rest <see cref="Tok.TextSecondary"/>; hover/press move BOTH legs to
/// SystemControlHighlightAltBaseMediumHigh (Pivot_themeresources.xaml:48-52). Clicking a header — or Left/Right/Home/
/// End on the focused header strip (ONE tab stop, Pivot_Partial.cpp:2939-2982) and Ctrl+PageUp/PageDown from anywhere
/// inside (:950-966) — swaps the content below with the WinUI directional fly-in. Uncontrolled by default (owns its
/// selection); pass <c>selectedIndex</c> to control it and observe changes through <c>onSelectionChanged</c> (WinUI
/// SelectedIndex + SelectionChanged, Pivot_Partial.h:375/:384). Per-tab content comes from the optional <c>content</c>
/// factory (WinUI PivotItem). Per-part restyling goes through <see cref="TemplateParts"/>.</summary>
public sealed class Pivot : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>Each clickable header box (WinUI PivotHeaderItem). The SAME modifier runs for every header — branch
    /// on app state for per-header styling. Owned: OnClick (select), Role, TabStop (false — the strip is the ONE
    /// tab stop, PivotHeaderItem IsTabStop=False, Pivot_themeresources.xaml:485).</summary>
    public const string PartHeaderItem = "HeaderItem";
    /// <summary>The 3px selection pipe under each header (WinUI SelectedPipe; rendered transparent when unselected).
    /// Owned: nothing — pure styling.</summary>
    public const string PartPipe = "Pipe";
    /// <summary>The content region below the headers (WinUI PivotItemPresenter). Owned: Children (the content
    /// factory's element / the placeholder), OnRealized (fly-in node capture, chained).</summary>
    public const string PartContent = "Content";

    // WinUI PivotAnimator fly-in (Pivot_Partial.cpp:3834-3836): TranslateX ±20px → 0 over 767ms + Opacity 0→1 over
    // 333ms, KeySpline (0.1,0.9)/(0.2,1.0) (:4256-4257); the sign is where the NEW item comes FROM (:4078). The
    // 83ms/7px fly-OUT of the OLD content (:3830-3832) would need the outgoing subtree kept mounted — not played.
    private const float FlyInDistancePx = 20f;
    private const float FlyInMs = 767f;
    private const float FlyInFadeMs = 333f;

    /// <summary>Controlled props flow through a provider — a reused ComponentEl never re-runs its factory — so
    /// Headers/SelectedIndex stay LIVE across parent re-renders (the RadioButtons pattern).</summary>
    internal sealed record Props(IReadOnlyList<string> Headers, Func<int, Element>? Content, int? SelectedIndex,
                                 Action<int>? OnSelectionChanged, TemplateParts? Parts)
    {
        internal static readonly Context<Props?> Channel = new(null);
    }

    /// <summary><paramref name="content"/> builds the selected tab's content (the WinUI PivotItem; null keeps a
    /// placeholder label). <paramref name="selectedIndex"/> controls the selection when set (WinUI SelectedIndex DP —
    /// the control stops owning it); <paramref name="onSelectionChanged"/> = WinUI SelectionChanged, fired with the
    /// new index on every user-initiated change (controlled or not).</summary>
    public static Element Create(IReadOnlyList<string> headers, Func<int, Element>? content = null,
                                 int? selectedIndex = null, Action<int>? onSelectionChanged = null,
                                 TemplateParts? parts = null)
        => Ctx.Provide(Props.Channel, new Props(headers, content, selectedIndex, onSelectionChanged, parts),
                       Embed.Comp(() => new Pivot()));

    /// <summary>SystemControlHighlightAltBaseMediumHighBrush — the hover AND pressed header foreground for both the
    /// selected and unselected legs (Pivot_themeresources.xaml:48-49, :51-52): SystemBaseMediumHighColor
    /// #CCFFFFFF dark / #CC000000 light (generic.xaml:210 / :4135).</summary>
    private static ColorF HighlightAltBaseMediumHigh => Tok.Theme == ThemeKind.Light
        ? ColorF.FromRgba(0x00, 0x00, 0x00, 0xCC)
        : ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xCC);

    public override Element Render()
    {
        // Hooks — stable order, unconditionally, before any early-out.
        var props = UseContext(Props.Channel);
        var (localSel, setLocalSel) = UseState(0);
        var contentNode = UseRef<NodeHandle>(default);
        var prevSel = UseRef(-1);

        int count = props?.Headers.Count ?? 0;
        int selRaw = props?.SelectedIndex ?? localSel;
        int selected = count == 0 ? 0 : Math.Clamp(selRaw, 0, count - 1);

        // Directional content fly-in on selection change (PivotStateMachine fly-out→fly-in, Pivot_Partial.cpp:
        // 3810-3821): moving to a HIGHER index brings the new item in from the RIGHT (fromLeft=false → +20px, :4078).
        UseLayoutEffect(() =>
        {
            int prev = prevSel.Value;
            prevSel.Value = selected;
            if (prev < 0 || prev == selected) return;                  // first mount: no entrance
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = contentNode.Value;
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node)) return;
            float fromX = (selected > prev ? 1f : -1f) * FlyInDistancePx;
            var spline = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1f); // PivotAnimator KeySpline (Pivot_Partial.cpp:4256-4257)
            anim.Animate(node, AnimChannel.TranslateX, fromX, 0f, FlyInMs, spline);
            anim.Animate(node, AnimChannel.Opacity, 0f, 1f, FlyInFadeMs, spline);
        }, selected);

        if (props is null || count == 0)
            return new BoxEl { Direction = 1, Grow = 1f };

        var headers = props.Headers;
        var parts = props.Parts;

        void Select(int index)
        {
            if (index == selected || (uint)index >= (uint)count) return;
            setLocalSel(index);                              // uncontrolled selection (ignored while controlled)
            props.OnSelectionChanged?.Invoke(index);         // WinUI SelectionChanged (Pivot_Partial.h:375)
        }

        // Pivot::OnHeaderKeyDown (Pivot_Partial.cpp:2939-2981): Left/Right move ±1, Home/End jump to first/last;
        // handled only when the selected index actually moved (:2947 — unmoved arrows fall out to auto-focus).
        // Static headers never wrap (ShouldWrap() = !m_usingStaticHeaders, :3622-3627 — Redstone+ dropped wrapping
        // for static headers, our only header mode).
        void OnHeaderKey(KeyEventArgs a)
        {
            if (a.Handled) return;
            int target = a.KeyCode switch
            {
                Keys.Left => selected - 1,
                Keys.Right => selected + 1,
                Keys.Home => 0,
                Keys.End => count - 1,
                _ => int.MinValue,
            };
            if (target == int.MinValue || target == selected || (uint)target >= (uint)count) return;
            Select(target);
            a.Handled = true;
        }

        // Pivot::OnKeyDownImpl (Pivot_Partial.cpp:950-966): Ctrl+PageDown → next, Ctrl+PageUp → previous — marked
        // handled even when the edge blocks the move (:954/:963); no wrap with static headers (ShouldWrap()=false).
        // WinUI also cycles on Ctrl(+Shift)+Tab (:968-983) — unreachable here: the dispatcher consumes Tab for focus
        // movement before OnKeyDown routing (InputDispatcher.cs:791-795).
        void OnPivotKey(KeyEventArgs a)
        {
            if (a.Handled || !a.Ctrl || (a.KeyCode != Keys.PageDown && a.KeyCode != Keys.PageUp)) return;
            int target = a.KeyCode == Keys.PageDown ? selected + 1 : selected - 1;
            a.Handled = true;
            if ((uint)target < (uint)count) Select(target);
        }

        var headerItems = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            Action select = () => Select(index);

            // WinUI SelectedPipe: Height=3, HorizontalAlignment=Stretch, VerticalAlignment=Bottom, Margin="0,0,0,2"
            // (Pivot_themeresources.xaml:587) — a ZStack overlay pinned to the header BOTTOM (y 43..46 inside the
            // 48px item) so the label centers independently of it.
            var pipe = new BoxEl
            {
                AlignSelf = FlexAlign.End,       // ZStack vertical placement: bottom edge
                Height = 3f,
                Corners = Radii.Circle(3f),      // PivotHeaderItemSelectedPipeCornerRadius = 1.5 (:487, :587 binding)
                Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,   // PivotHeaderItemSelectedPipeFill = AccentFillColorDefault (:55)
                Margin = new Edges4(0, 0, 0, 2),
            };

            // Foreground (Pivot_themeresources.xaml): Unselected = SystemControlForegroundBaseMedium (:47) ≈
            // TextSecondary; Selected = SystemControlHighlightAltBaseHigh (:50) ≈ TextPrimary; hover/press on BOTH
            // legs → AltBaseMediumHigh (:48-49, :51-52) — unselected brightens, selected dims. Backgrounds stay
            // transparent in every state (:40-46), so no Hover/PressedFill.
            var label = new TextEl(headers[index])
            {
                Size = 24f,                      // PivotHeaderItemFontSize (:7)
                Weight = 350,                    // PivotHeaderItemThemeFontWeight = SemiLight (:17)
                CharSpacing = -25f,              // PivotHeaderItemCharacterSpacing −25/1000 em (:10)
                Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary,
                HoverColor = HighlightAltBaseMediumHigh,
                PressedColor = HighlightAltBaseMediumHigh,
                AlignSelf = FlexAlign.Center,    // VerticalContentAlignment=Center (:484)
            };

            var item = new BoxEl
            {
                Height = 48f,                            // PivotHeaderItem Height=48 (:483)
                Padding = new Edges4(12, 0, 12, 0),      // PivotHeaderItemMargin 12,0,12,0 as the item Padding (:482, :11) — header spacing comes from here, not a strip Gap
                Corners = Radii.ControlAll,
                Role = AutomationRole.Tab,
                TabStop = false,                         // IsTabStop=False (:485): the strip is the single tab stop
                OnClick = select,
                ZStack = true,                           // the WinUI Grid overlay: centered label + bottom-pinned pipe
                Children = [label, parts.Apply(PartPipe, pipe)],
            };
            // Parts: restyle anything; the select mechanics + the single-tab-stop contract always win.
            headerItems[index] = parts.Apply(PartHeaderItem, item) with
            {
                OnClick = select,
                Role = AutomationRole.Tab,
                TabStop = false,
            };
        }

        // Per-tab content (WinUI PivotItem): margin = PivotItemMargin 12,0,12,0 (Pivot_themeresources.xaml:12,
        // :452), PivotItem Padding=0 (:453). The node handle is captured for the selection fly-in.
        Element body = props.Content is { } factory
            ? factory(selected)
            : new TextEl($"Content for {headers[selected]}") { Size = 14f, Color = Tok.TextPrimary };
        Action<NodeHandle> captureContent = h => contentNode.Value = h;
        var content = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Padding = new Edges4(12, 0, 12, 0),
            OnRealized = captureContent,
            Children = [body],
        };
        var styledContent = parts.Apply(PartContent, content);
        content = styledContent with
        {
            Children = content.Children,
            OnRealized = TemplateParts.Chain(captureContent, styledContent.OnRealized),
        };

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            OnKeyDown = OnPivotKey,   // Ctrl+PageUp/PageDown bubble here from any focused descendant (InputDispatcher.cs:835-841)
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    Gap = 0f,                 // PivotHeaderPanel stacks the headers adjacently (Pivot_themeresources.xaml:421-425)
                    AlignItems = FlexAlign.End,
                    Focusable = true,         // ONE tab stop for the whole header row (WinUI focuses HeaderClipper, :408; items are IsTabStop=False :485)
                    OnKeyDown = OnHeaderKey,  // Left/Right/Home/End move the selection (Pivot::OnHeaderKeyDown)
                    Children = headerItems,
                },
                content,
            ],
        };
    }
}
