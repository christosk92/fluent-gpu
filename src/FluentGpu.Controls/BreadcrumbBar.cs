using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A WinUI BreadcrumbBar (controls\dev\Breadcrumb): a horizontal trail of items separated by right chevrons
/// (BreadcrumbBarChevronLeftToRight = E974, BreadcrumbBar_themeresources.xaml:5). The last item is the current page —
/// TextFillColorPrimary at FontWeight Normal like every other crumb (BreadcrumbBarItemFontWeight,
/// BreadcrumbBar_themeresources.xaml:8) and non-interactive (the LastItem state collapses PART_ItemButton,
/// BreadcrumbBar.xaml:89-96); earlier items are clickable, dim to TextFillColorSecondary on hover and
/// TextFillColorTertiary on press (themeresources:10-11), and raise <c>onSelect(index)</c>. Keyboard: every crumb is
/// a tab stop (DefaultBreadcrumbBarItemStyle IsTabStop=True, BreadcrumbBar.xaml:13); Left/Right move focus
/// crumb-to-crumb (OnChildPreviewKeyDown, BreadcrumbBar.cpp:514-560). NOT implemented: the overflow ellipsis +
/// hidden-items flyout (BreadcrumbLayout.cpp:62-69 collapses leading crumbs when the accumulated width exceeds the
/// available size) — that needs measured-width feedback the engine does not surface yet (layout effects re-run only
/// on re-render, never on a window-resize relayout). Per-part restyling goes through the optional <c>parts</c>
/// (see <see cref="TemplateParts"/> for the contract).</summary>
public static class BreadcrumbBar
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a parts customization cannot win those).
    /// <summary>Each crumb's box — the SAME modifier runs for every crumb, the last/current one included. Owned:
    /// OnClick (navigate — re-asserted on the clickable, non-last crumbs only), Role, TabStop, OnKeyDown (arrow
    /// focus moves), OnRealized (focus-handle capture, chained).</summary>
    public const string PartItem = "Item";
    /// <summary>The right-chevron separator between crumbs (WinUI PART_ChevronTextBlock) — a <see cref="TextEl"/>,
    /// so style it via the generic map: <c>parts.Set&lt;TextEl&gt;(BreadcrumbBar.PartSeparator, t =&gt; t with { … })</c>.
    /// Owned: nothing (pure styling).</summary>
    public const string PartSeparator = "Separator";

    public static Element Create(IReadOnlyList<string> items, Action<int>? onChange = null, TemplateParts? parts = null)
        => Embed.Comp(new Props(items, onChange, parts), () => new BreadcrumbBarCore());

    /// <summary>Controlled props are RE-PUSHED live to the reused core (<c>Embed.Comp(props, …)</c>) — a reused
    /// ComponentEl never re-runs its factory — so the trail stays LIVE across parent re-renders; the core reads them
    /// with <c>UseProps</c> (the SelectorBar/RadioButtons convention).</summary>
    internal sealed record Props(IReadOnlyList<string> Items, Action<int>? OnChange, TemplateParts? Parts);
}

/// <summary>The stateful core: captures crumb node handles so Left/Right can move focus crumb-to-crumb, the
/// SelectorBar/RadioButtons convention.</summary>
internal sealed class BreadcrumbBarCore : Component
{
    public override Element Render()
    {
        // Hooks — stable order, unconditionally, before any early-out.
        var p = UseProps<BreadcrumbBar.Props>();   // re-pushed live props (items/selection stay current across re-renders)
        var hooks = UseContext(InputHooks.Current);
        var handles = UseRef(new List<NodeHandle>()).Value;   // crumb node per index (arrow-key focus targets)

        var parts = p.Parts;
        int count = p.Items?.Count ?? 0;
        while (handles.Count < count) handles.Add(NodeHandle.Null);

        // Left/Right move focus to the previous/next crumb (OnChildPreviewKeyDown → MoveFocusPrevious/MoveFocusNext →
        // MoveFocus, BreadcrumbBar.cpp:514-560, :427-511). Handled only when the move happens — at the trail edges
        // WinUI lets the plain arrow bubble (only the gamepad DPad original key falls out to a page-wide
        // FocusManager::TryMoveFocus, :528-538/:548-558). Focus moves do NOT navigate.
        void OnCrumbKey(int i, KeyEventArgs a)
        {
            if (a.Handled || count == 0) return;
            int target = a.KeyCode switch
            {
                Keys.Left => i - 1,
                Keys.Right => i + 1,
                _ => int.MinValue,
            };
            if ((uint)target >= (uint)count) return;
            if (target < handles.Count && !handles[target].IsNull)
            {
                a.Handled = true;
                (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(handles[target]);   // keyboard move → focus visual
            }
        }

        var children = new List<Element>(count * 2);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isLast = index == count - 1;
            Action<KeyEventArgs> onKey = a => OnCrumbKey(index, a);
            Action<NodeHandle> capture = h => { if (index < handles.Count) handles[index] = h; };

            // Every crumb shapes at FontWeight Normal — the current/last item differs only by non-interactivity, not
            // weight (BreadcrumbBarItemFontWeight = Normal in all themes, BreadcrumbBar_themeresources.xaml:8, bound
            // at BreadcrumbBar.xaml:9; the LastItem presenter re-binds the same FontWeight, :295).
            var label = new TextEl(p.Items![index])
            {
                Size = 14f,                        // BreadcrumbBarItemThemeFontSize = ControlContentThemeFontSize (themeresources:32)
                LineHeight = 20f,                  // PART_ItemContentPresenter / PART_LastItemContentPresenter LineHeight=20 (BreadcrumbBar.xaml:272, :298)
                Color = Tok.TextPrimary,           // BreadcrumbBarNormalForegroundBrush / CurrentNormal = TextFillColorPrimary (themeresources:9, :14)
                DisabledColor = Tok.TextDisabled,  // BreadcrumbBarDisabledForegroundBrush / CurrentDisabled = TextFillColorDisabled (themeresources:12, :17)
            };
            // WinUI breadcrumb buttons keep a transparent background in every state; PointerOver/Pressed is a
            // TEXT-color change, not a fill (CommonStates set only PART_ContentPresenter.Foreground,
            // BreadcrumbBar.xaml:194-214). WinUI swaps the brush in 0ms discrete keyframes; ours rides the eased
            // hover/press progress — the engine-wide foreground-ramp convention.
            if (!isLast)
                label = label with
                {
                    HoverColor = Tok.TextSecondary,   // BreadcrumbBarHoverForegroundBrush = TextFillColorSecondary (themeresources:10)
                    PressedColor = Tok.TextTertiary,  // BreadcrumbBarPressedForegroundBrush = TextFillColorTertiary (themeresources:11)
                };

            var onChange = p.OnChange;
            Action? click = isLast || onChange is null ? null : () => onChange(index);
            var crumb = new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(1, 3, 1, 3),  // PART_ItemButton / PART_LastItemContentPresenter Padding="1,3" (BreadcrumbBar.xaml:168, :298)
                Corners = Radii.ControlAll,        // CornerRadius = ControlCornerRadius (BreadcrumbBar.xaml:17)
                Role = AutomationRole.Button,      // BreadcrumbBarItemAutomationPeer reports a Button control type
                // Every item is a keyboard stop, the non-clickable current one included (IsTabStop=True,
                // BreadcrumbBar.xaml:13; the LastItem state moves the focus target onto the presenter, :92-93).
                TabStop = true,
                OnClick = click,
                OnKeyDown = onKey,
                OnRealized = capture,
                Children = [label],
            };
            // Parts: restyle anything; the navigate/keyboard mechanics always win.
            var styled = parts.Apply(BreadcrumbBar.PartItem, crumb);
            children.Add(styled with
            {
                OnClick = click,
                Role = AutomationRole.Button,
                TabStop = true,
                OnKeyDown = onKey,
                OnRealized = TemplateParts.Chain(capture, styled.OnRealized),
            });

            if (!isLast)
                children.Add(parts.Apply(BreadcrumbBar.PartSeparator,
                    // The Default visual state swaps the template's E76C fallback to BreadcrumbBarChevronLeftToRight
                    // = E974 (BreadcrumbBar.xaml:79 overriding :321; themeresources:5) — E974 is what renders LTR.
                    new TextEl(Icons.ChevronRightMed)
                    {
                        Size = 12f,                       // BreadcrumbBarChevronFontSize (themeresources:33)
                        Color = Tok.TextPrimary,          // PART_ChevronTextBlock = BreadcrumbBarNormalForegroundBrush = TextFillColorPrimary (BreadcrumbBar.xaml:320, themeresources:9)
                        FontFamily = Theme.IconFont,
                        Margin = new Edges4(2, 0, 2, 0),  // BreadcrumbBarChevronPadding = 2,0 (BreadcrumbBar.xaml:321, themeresources:7)
                    }));
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            // No container Role and not a stop itself (BreadcrumbBar IsTabStop=False, BreadcrumbBar.xaml:330) —
            // only the items expose peers.
            Children = children.ToArray(),
        };
    }
}
