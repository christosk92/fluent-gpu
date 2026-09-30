using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>One option in a <see cref="Segmented"/> control. Content is deliberately text-only in the focused v1;
/// <see cref="Icon"/> uses the shared AOT-safe icon value and <see cref="IsEnabled"/> disables only this item.</summary>
public readonly record struct SegmentedItem(string Content, IconRef Icon = default, bool IsEnabled = true);

/// <summary>
/// A compact CommunityToolkit-style segmented selector for two to five mutually-exclusive choices. The selected value
/// follows the universal control contract: a concrete index signal in, <c>onChange</c> out, and an internally-owned
/// signal when none is supplied. Arrow/Home/End move the single roving focus stop without changing selection;
/// click/Space/Enter commit selection.
/// </summary>
public static class Segmented
{
    public const string PartRoot = "Root";
    public const string PartItem = "Item";
    public const string PartContent = "Content";
    public const string PartLabel = "Label";
    public const string PartSelectionPill = "SelectionPill";
    /// <summary>The 3px pill-row underneath the selected item's content (the whole row, not just the pill itself —
    /// see <see cref="PartSelectionPill"/>). A caller restyles or hides the row via this part, e.g. a page-title pivot
    /// preset that drops the pill in favour of larger type, or a compact-band preset that shrinks it.</summary>
    public const string PartSelectionPillSlot = "SelectionPillSlot";

    public sealed record Style
    {
        public float Height { get; init; } = 34f;
        public float ItemMinWidth { get; init; } = 52f;
        public float FontSize { get; init; } = 14f;
        public float IconSize { get; init; } = 16f;
        public float IconGap { get; init; } = 8f;
        public float CornerRadius { get; init; } = 4f;
        public float ItemCornerRadius { get; init; } = 4f;
        public Edges4 Padding { get; init; } = Edges4.All(2f);
        public Edges4 FocusVisualMargin { get; init; } = Edges4.All(-3f);
        public ColorF Background { get; init; }
        public ColorF Border { get; init; }
        public ColorF ItemHover { get; init; }
        public ColorF ItemPressed { get; init; }
        public ColorF SelectedBackground { get; init; }
        public ColorF SelectedHover { get; init; }
        public ColorF SelectedPressed { get; init; }
        public GradientSpec? SelectedBorder { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF SelectedForeground { get; init; }
        public ColorF DisabledForeground { get; init; }
        public ColorF SelectionPill { get; init; }
        public ushort SelectedFontWeight { get; init; } = 400;
        /// <summary>The root's item-row <c>Gap</c> (E13, Wavee Home redesign) — a token bundle value, not a per-item
        /// margin. Default 0f is today's behaviour: items sit flush and the selected item's own fill/border carry
        /// the visual separation.</summary>
        public float ItemGap { get; init; } = 0f;
        /// <summary>Each item root's own <c>Padding</c> (E19, Wavee Home redesign) — lets a preset (e.g. the compact
        /// facet band, `(12,0,12,0)`) give items horizontal breathing room independent of <see cref="ItemMinWidth"/>.
        /// Default <c>default</c> (all-zero <see cref="Edges4"/>) is today's behaviour: no padding on the item root.</summary>
        public Edges4 ItemPadding { get; init; } = default;
    }

    public sealed record SegmentedOptions
    {
        /// <summary>Select the first enabled item when the supplied/owned index has no initial selection.</summary>
        public bool AutoSelection { get; init; } = true;
        public bool IsEnabled { get; init; } = true;
        /// <summary>Left/Right wrap the roving focus stop from the last enabled item to the first, and vice versa,
        /// instead of stopping at the edge. Default off matches today's clamped behaviour.</summary>
        public bool WrapFocus { get; init; } = false;
        /// <summary>The item's accessibility role. Default <see cref="AutomationRole.RadioButton"/> (today's
        /// behaviour — a segmented control is a mutually-exclusive choice group). Set <see cref="AutomationRole.Tab"/>
        /// for a facet/pivot preset presented as a tablist.</summary>
        public AutomationRole ItemRole { get; init; } = AutomationRole.RadioButton;
        public Style? Style { get; init; }
        public TemplateParts? Parts { get; init; }
        /// <summary>Fires on hover EDGES only (E13, Wavee Home redesign) — once with the item index when the pointer
        /// enters an item, once with -1 when it leaves the control's item row (never a continuous stream of moves,
        /// never a duplicate fire for the same edge). Null (default) wires nothing — byte-identical to pre-E13
        /// output.</summary>
        public Action<int>? OnItemHoverChanged { get; init; }
        /// <summary>Fires on focus EDGES only (E13, Wavee Home redesign) — once with the item index when an item
        /// gains keyboard/programmatic focus, once with -1 when focus leaves it. This tracks the roving FOCUS stop,
        /// not selection (arrows move focus without selecting — see <see cref="Segmented"/>'s keyboard model). Null
        /// (default) wires nothing — byte-identical to pre-E13 output.</summary>
        public Action<int>? OnItemFocusChanged { get; init; }
    }

    static readonly SegmentedOptions DefaultOptions = new();

    public static Style DefaultStyle => new()
    {
        Background = Tok.FillControlAltSecondary,
        Border = Tok.StrokeControlDefault,
        ItemHover = Tok.FillSubtleSecondary,
        ItemPressed = Tok.FillSubtleTertiary,
        SelectedBackground = Tok.FillControlDefault,
        SelectedHover = Tok.FillControlSecondary,
        SelectedPressed = Tok.FillControlTertiary,
        SelectedBorder = Tok.ControlElevationBorder,
        Foreground = Tok.TextSecondary,
        SelectedForeground = Tok.TextPrimary,
        DisabledForeground = Tok.TextDisabled,
        SelectionPill = Tok.AccentDefault,
    };

    /// <summary>Create a horizontal, equal-width, single-selection segmented control. <paramref name="selectedIndex"/>
    /// is a caller-owned concrete signal (null means auto-materialize); interaction writes it before firing
    /// <paramref name="onChange"/>. Required item content stays first and the non-value tail lives in
    /// <paramref name="options"/>.</summary>
    public static Element Create(
        IReadOnlyList<SegmentedItem> items,
        Signal<int>? selectedIndex = null,
        Action<int>? onChange = null,
        SegmentedOptions? options = null)
    {
        var o = options ?? DefaultOptions;
        return Embed.Comp(
            new Props(items, selectedIndex, onChange, o.AutoSelection, o.IsEnabled, o.WrapFocus, o.ItemRole,
                o.Style ?? DefaultStyle, o.Parts, o.OnItemHoverChanged, o.OnItemFocusChanged),
            () => new SegmentedCore());
    }

    internal sealed record Props(
        IReadOnlyList<SegmentedItem> Items,
        Signal<int>? SelectedIndex,
        Action<int>? OnChange,
        bool AutoSelection,
        bool IsEnabled,
        bool WrapFocus,
        AutomationRole ItemRole,
        Style Style,
        TemplateParts? Parts,
        Action<int>? OnItemHoverChanged,
        Action<int>? OnItemFocusChanged);
}

internal sealed class SegmentedCore : Component
{
    static readonly LayoutTransition PillTransition = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(167f, Easing.FluentPopOpen),
        Enter: new EnterExit(Sx: 0.25f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 0.25f, Opacity: 0f, Active: true));

    public override Element Render()
    {
        var p = UseProps<Segmented.Props>();
        var hooks = UseContext(InputHooks.Current);
        int count = p.Items.Count;
        int firstEnabled = FirstEnabled(p, 0, +1);
        var own = UseSignal(p.AutoSelection ? firstEnabled : -1);
        var selectedIndex = p.SelectedIndex ?? own;
        var handles = UseRef(new List<NodeHandle>()).Value;
        int selected = selectedIndex.Value;

        // E13: hover/focus edge tracking. Each item's own OnHoverMove/OnFocusChanged fires continuously (every
        // pointer move within it; every focus transition Windows reports) — these refs dedupe to a single fire per
        // EDGE (enter/gain and leave/lose), so a caller never sees a duplicate index or a stream of moves.
        var hoverRef = UseRef(-1);
        var focusRef = UseRef(-1);

        while (handles.Count < count) handles.Add(NodeHandle.Null);
        if (handles.Count > count) handles.RemoveRange(count, handles.Count - count);

        // AutoSelection is an initialization policy, not a permanent coercion: a later programmatic -1 remains valid.
        UseEffect(() =>
        {
            if (p.AutoSelection && selectedIndex.Peek() < 0 && firstEnabled >= 0)
                selectedIndex.Value = firstEnabled;
        }, DepKey.Empty);

        int tabStop = IsEnabled(p, selected) ? selected : firstEnabled;

        void Focus(int target)
        {
            if (!IsEnabled(p, target) || target >= handles.Count || handles[target].IsNull) return;
            (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(handles[target]);
        }

        void OnItemKey(int index, KeyEventArgs args)
        {
            if (args.Handled) return;
            int target = args.KeyCode switch
            {
                Keys.Left => FirstEnabled(p, index - 1, -1),
                Keys.Right => FirstEnabled(p, index + 1, +1),
                Keys.Home => firstEnabled,
                Keys.End => FirstEnabled(p, count - 1, -1),
                _ => -1,
            };
            if (target < 0 && p.WrapFocus)
            {
                if (args.KeyCode == Keys.Left) target = FirstEnabled(p, count - 1, -1);
                else if (args.KeyCode == Keys.Right) target = FirstEnabled(p, 0, +1);
            }
            if (target < 0) return;
            args.Handled = true;
            Focus(target);
        }

        var children = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var item = p.Items[index];
            bool enabled = p.IsEnabled && item.IsEnabled;
            bool isSelected = selected == index;
            ColorF foreground = isSelected ? p.Style.SelectedForeground : p.Style.Foreground;
            Action select = () =>
            {
                if (!enabled || selectedIndex.Peek() == index) return;
                selectedIndex.Value = index;
                p.OnChange?.Invoke(index);
            };
            Action<NodeHandle> capture = h =>
            {
                if (index < handles.Count) handles[index] = h;
            };

            // E13: only wired when the caller supplies a callback — no options ⇒ the pre-E13 tree (gate.segmented.byte-identical).
            Action<Point2>? onHoverMove = p.OnItemHoverChanged is null ? null : _ =>
            {
                if (hoverRef.Value == index) return;
                hoverRef.Value = index;
                p.OnItemHoverChanged(index);
            };
            Action? onPointerExit = p.OnItemHoverChanged is null ? null : () =>
            {
                if (hoverRef.Value != index) return;
                hoverRef.Value = -1;
                p.OnItemHoverChanged(-1);
            };
            Action<bool>? onFocusChanged = p.OnItemFocusChanged is null ? null : focused =>
            {
                if (focused)
                {
                    if (focusRef.Value == index) return;
                    focusRef.Value = index;
                    p.OnItemFocusChanged(index);
                }
                else
                {
                    if (focusRef.Value != index) return;
                    focusRef.Value = -1;
                    p.OnItemFocusChanged(-1);
                }
            };

            var contentChildren = new List<Element>(2);
            if (!item.Icon.IsNone)
                contentChildren.Add(IconView.Render(
                    item.Icon,
                    p.Style.IconSize,
                    foreground,
                    disabledColor: p.Style.DisabledForeground));
            contentChildren.Add(p.Parts.Apply(Segmented.PartLabel, new TextEl(item.Content)
            {
                Size = p.Style.FontSize,
                Color = foreground,
                HoverColor = isSelected ? p.Style.SelectedForeground : Tok.TextPrimary,
                PressedColor = foreground,
                DisabledColor = p.Style.DisabledForeground,
                Weight = (ushort)(isSelected ? p.Style.SelectedFontWeight : 400),
            }));

            var content = p.Parts.Apply(Segmented.PartContent, new BoxEl
            {
                Direction = 0,
                Grow = 1f,
                Gap = p.Style.IconGap,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = contentChildren.ToArray(),
            });

            var pill = new BoxEl
            {
                Key = "SelectionPill",
                Width = 24f,
                Height = 3f,
                Corners = CornerRadius4.All(1.5f),
                Fill = p.Style.SelectionPill,
                Animate = PillTransition,
            };
            var pillSlot = p.Parts.Apply(Segmented.PartSelectionPillSlot, new BoxEl
            {
                Direction = 0,
                Height = 3f,
                AlignSelf = FlexAlign.Stretch,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = isSelected ? [p.Parts.Apply(Segmented.PartSelectionPill, pill)] : [],
            });

            var root = new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                MinWidth = p.Style.ItemMinWidth,
                Height = p.Style.Height - 2f * p.Style.Padding.Top,
                Padding = p.Style.ItemPadding,
                AlignItems = FlexAlign.Stretch,
                Corners = CornerRadius4.All(p.Style.ItemCornerRadius),
                Fill = isSelected ? p.Style.SelectedBackground : ColorF.Transparent,
                HoverFill = isSelected ? p.Style.SelectedHover : p.Style.ItemHover,
                PressedFill = isSelected ? p.Style.SelectedPressed : p.Style.ItemPressed,
                BorderWidth = isSelected ? 1f : 0f,
                BorderBrush = isSelected ? p.Style.SelectedBorder : null,
                BrushTransitionMs = 83f,
                PressScale = enabled ? 0.96f : 1f,
                PressDurationMs = 167f,
                PressEasing = Easing.FluentPopOpen,
                Focusable = enabled,
                TabStop = enabled && index == tabStop,
                FocusVisualMargin = p.Style.FocusVisualMargin,
                Role = p.ItemRole,
                IsEnabled = enabled,
                OnClick = select,
                OnKeyDown = a => OnItemKey(index, a),
                OnRealized = capture,
                OnHoverMove = onHoverMove,
                OnPointerExit = onPointerExit,
                OnFocusChanged = onFocusChanged,
                Children = [content, pillSlot],
            };
            var styled = p.Parts.Apply(Segmented.PartItem, root);
            children[index] = styled with
            {
                OnClick = select,
                OnKeyDown = root.OnKeyDown,
                OnRealized = TemplateParts.Chain(capture, styled.OnRealized),
                OnHoverMove = onHoverMove,
                OnPointerExit = onPointerExit,
                OnFocusChanged = onFocusChanged,
                Role = p.ItemRole,
                IsEnabled = enabled,
                Focusable = enabled,
                TabStop = root.TabStop,
                Children = root.Children,
            };
        }

        var control = new BoxEl
        {
            Direction = 0,
            Gap = p.Style.ItemGap,
            Height = p.Style.Height,
            Padding = p.Style.Padding,
            Corners = CornerRadius4.All(p.Style.CornerRadius),
            Fill = p.Style.Background,
            BorderWidth = 1f,
            BorderColor = p.Style.Border,
            ClipToBounds = true,
            IsEnabled = p.IsEnabled,
            Children = children,
        };
        return p.Parts.Apply(Segmented.PartRoot, control) with
        {
            Children = children,
            IsEnabled = p.IsEnabled,
        };
    }

    static bool IsEnabled(Segmented.Props p, int index) =>
        p.IsEnabled && (uint)index < (uint)p.Items.Count && p.Items[index].IsEnabled;

    static int FirstEnabled(Segmented.Props p, int start, int direction)
    {
        if (direction == 0) return -1;
        for (int i = start; (uint)i < (uint)p.Items.Count; i += direction)
            if (IsEnabled(p, i)) return i;
        return -1;
    }
}
