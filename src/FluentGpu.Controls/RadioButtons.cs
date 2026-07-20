using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// The WinUI RadioButtons container (microsoft-ui-xaml controls\dev\RadioButtons): a header + a column-major grid of
/// mutually-exclusive <see cref="RadioButton"/> items with the container keyboard contract.
///
/// Layout = ColumnMajorUniformToLargestGridLayout (RadioButtons.xaml:22-26): items flow top-to-bottom column-major into
/// at most <c>maxColumns</c> columns — the first <c>count % columns</c> columns take one extra item
/// (ColumnMajorUniformToLargestGridLayout.cpp ArrangeOverride :48-120; CalculateColumns clamps to
/// <c>min(MaxColumns, count)</c>, :132-163 — the available-width clamp needs measure and is not applied here).
/// ColumnSpacing = 7, RowSpacing = 8, header margin 0,0,0,8 (RadioButtons_themeresources.xaml:18-20); header foreground
/// TextFillColorPrimary / Disabled (:4-10).
///
/// Keyboard (RadioButtons.cpp): ONE tab stop — the container is IsTabStop=False + TabNavigation=Once
/// (RadioButtons.xaml:5-6) and focus entering the group lands on the selected item (OnGettingFocus redirect, :80-97);
/// here only the selected (or first) item is <c>Focusable</c>, the roving-tab-stop equivalent. Up/Down move focus ±1 in
/// data order (OnChildPreviewKeyDown :135-165 → MoveFocus, :414-447); Left/Right move across columns (:167-183, the
/// XYFocus directional move constrained to the repeater); SELECTION FOLLOWS FOCUS unless Ctrl is held (:100-107). At
/// the edges the key is swallowed when the source is the first/last item (HandleEdgeCaseFocus, :216-242 — WinUI then
/// tries to move focus OUT of the group, which a control factory cannot do; deviation noted). Controlled.
/// </summary>
public static partial class RadioButtons
{
    public const float ColumnSpacing = 7f;   // RadioButtonsColumnSpacing (RadioButtons_themeresources.xaml:18)
    public const float RowSpacing = 8f;      // RadioButtonsRowSpacing (RadioButtons_themeresources.xaml:19)
    public const float HeaderGap = 8f;       // RadioButtonsTopHeaderMargin 0,0,0,8 (RadioButtons_themeresources.xaml:20)

    /// <summary>String items (the WinUI ItemsSource-of-strings shape). <paramref name="parts"/> = the per-item
    /// <see cref="RadioButton"/> template parts (PartRing/PartDot/…), applied to EVERY item (not virtualized).</summary>
    public static Element Create(IReadOnlyList<string> items, int selectedIndex, Action<int> onSelect,
                                 string? header = null, int maxColumns = 1, bool isEnabled = true,
                                 RadioButton.Style? style = null, TemplateParts? parts = null)
        => Embed.Comp(new Props(items.Count, items, null, selectedIndex, onSelect, header, maxColumns, isEnabled,
                                style ?? RadioButton.DefaultStyle, parts),
                      () => new RadioButtonsCore());

    /// <summary>Element-factory items: <paramref name="itemContent"/>(i) renders each item's content in place of the
    /// text label (the WinUI arbitrary-content item wrapped in a RadioButton).</summary>
    public static Element Create(int itemCount, Func<int, Element> itemContent, int selectedIndex, Action<int> onSelect,
                                 string? header = null, int maxColumns = 1, bool isEnabled = true,
                                 RadioButton.Style? style = null, TemplateParts? parts = null)
        => Embed.Comp(new Props(itemCount, null, itemContent, selectedIndex, onSelect, header, maxColumns, isEnabled,
                                style ?? RadioButton.DefaultStyle, parts),
                      () => new RadioButtonsCore());

    /// <summary>Controlled props RE-PUSHED to the core (<c>Embed.Comp(props, …)</c>) — a reused ComponentEl never
    /// re-runs its factory — so props are delivered live (equality-gated); the core reads them with <c>UseProps</c>.</summary>
    internal sealed record Props(int Count, IReadOnlyList<string>? Labels, Func<int, Element>? Content, int Selected,
                                 Action<int> OnSelect, string? Header, int MaxColumns, bool IsEnabled,
                                 RadioButton.Style Style, TemplateParts? Parts = null);
}

/// <summary>The stateful core: captures item node handles (for the roving focus moves) and routes the arrow keys.</summary>
internal sealed class RadioButtonsCore : Component
{
    public override Element Render()
    {
        // Hooks — stable order, unconditionally, before any early-out.
        var p = UseProps<RadioButtons.Props>();
        var hooks = UseContext(InputHooks.Current);
        var handles = UseRef(new List<NodeHandle>()).Value;

        int n = p.Count;
        var s = p.Style;

        // ── column-major arrangement (ColumnMajorUniformToLargestGridLayout.cpp) ──
        int cols = Math.Max(1, Math.Min(p.MaxColumns, Math.Max(n, 1)));   // CalculateColumns :146-147,160-162
        int minPer = cols > 0 ? n / cols : n;
        int extra = cols > 0 ? n % cols : 0;                              // first `extra` columns take one more (:53-55)
        int ColSize(int c) => minPer + (c < extra ? 1 : 0);
        int ColStart(int c)
        {
            int start = 0;
            for (int k = 0; k < c; k++) start += ColSize(k);
            return start;
        }
        (int Col, int Row) Locate(int i)
        {
            int c = 0, start = 0;
            while (c < cols - 1 && i >= start + ColSize(c)) { start += ColSize(c); c++; }
            return (c, i - start);
        }

        while (handles.Count < n) handles.Add(NodeHandle.Null);

        // The single roving tab stop: the selected item, or the first when nothing is selected
        // (OnGettingFocus redirects entering focus to the selected item, RadioButtons.cpp:80-97).
        int tabStop = (uint)p.Selected < (uint)n ? p.Selected : 0;

        void MoveTo(int target, bool ctrl)
        {
            if ((uint)target >= (uint)n) return;
            if (!ctrl) p.OnSelect(target);                                 // selection follows focus unless Ctrl (cpp:100-107)
            if (target < handles.Count && !handles[target].IsNull)
                (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(handles[target]);   // keyboard move → focus visual
        }

        void OnItemKey(int i, KeyEventArgs a)
        {
            if (a.Handled || n == 0) return;
            int target;
            switch (a.KeyCode)
            {
                case Keys.Down: target = i + 1 < n ? i + 1 : -1; break;    // MoveFocusNext (cpp:139-151/414-447)
                case Keys.Up: target = i - 1 >= 0 ? i - 1 : -1; break;     // MoveFocusPrevious (cpp:153-165)
                case Keys.Right or Keys.Left:                              // directional column move (cpp:167-183)
                {
                    var (c, r) = Locate(i);
                    int tc = c + (a.KeyCode == Keys.Right ? 1 : -1);
                    target = (uint)tc < (uint)cols && ColSize(tc) > 0
                        ? ColStart(tc) + Math.Min(r, ColSize(tc) - 1)
                        : -1;
                    break;
                }
                default: return;
            }
            // Swallow at the edges (HandleEdgeCaseFocus, cpp:216-242). WinUI would additionally try to move focus OUT
            // of the group when an edge is hit — not reachable from a control factory; the key simply stops here.
            a.Handled = true;
            if (target >= 0) MoveTo(target, a.Ctrl);
        }

        Element Item(int i)
        {
            int idx = i;
            Element? content = p.Content?.Invoke(i);
            string? label = p.Labels is not null && i < p.Labels.Count ? p.Labels[i] : null;
            return RadioButton.Build(
                label, content,
                selected: i == p.Selected,
                onSelect: () => p.OnSelect(idx),
                s, p.IsEnabled,
                focusable: i == tabStop,                                   // roving single tab stop (RadioButtons.xaml:5-6)
                onKeyDown: a => OnItemKey(idx, a),
                onRealized: h => { while (handles.Count <= idx) handles.Add(NodeHandle.Null); handles[idx] = h; },
                parts: p.Parts);
        }

        // Columns of items, column-major (ArrangeOverride, ColumnMajorUniformToLargestGridLayout.cpp:48-120).
        var columns = new Element[cols];
        int next = 0;
        for (int c = 0; c < cols; c++)
        {
            int size = ColSize(c);
            var kids = new Element[size];
            for (int r = 0; r < size; r++) kids[r] = Item(next++);
            columns[c] = new BoxEl { Direction = 1, Gap = RadioButtons.RowSpacing, Children = kids };
        }
        var grid = new BoxEl
        {
            Direction = 0,
            Gap = RadioButtons.ColumnSpacing,
            AlignItems = FlexAlign.Start,
            Children = columns,
        };

        if (p.Header is { Length: > 0 })
            return new BoxEl
            {
                Direction = 1,
                Gap = RadioButtons.HeaderGap,                              // TopHeaderMargin 0,0,0,8 (themeresources:20)
                Children =
                [
                    // RadioButtonsHeaderForeground → TextFillColorPrimary / Disabled (themeresources:4-10). The header
                    // sits outside the items' IsEnabled scope, so the disabled leg is picked here, not inherited.
                    new TextEl(p.Header) { Size = s.FontSize, Color = p.IsEnabled ? Tok.TextPrimary : Tok.TextDisabled },
                    grid,
                ],
            };
        return grid;
    }
}
