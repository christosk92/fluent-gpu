using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>GridView</c> — a thin L4 SKIN over the E11-L3 substrate: <see cref="ItemsView"/> +
/// <see cref="SelectionModel"/> supply selection (None/Single/Multiple/Extended), grid keyboard navigation
/// (Left/Right ±1, Up/Down ±columns — ItemsViewInteractions.cpp:1051-1067), Home/End/PageUp/Down, Ctrl+A, typeahead
/// and focus rings; this file authors only the WinUI <c>GridViewItem</c> chrome (the old hand-rolled body is deleted).
///
/// Chrome verified against microsoft-ui-xaml controls\dev\CommonStyles\GridViewItem_themeresources.xaml
/// (Default==Light for every value below — all alias the same Subtle/Accent resources):
/// • Tile plate: Background = SubtleFillColorTransparent (:5), PointerOver = SubtleFillColorSecondary (:6),
///   Pressed = SubtleFillColorTertiary (:7), Selected = SubtleFillColorTertiary (:8), SelectedPointerOver =
///   Tertiary (:9), SelectedPressed = Secondary (:10), SelectedDisabled = Secondary (:45). CornerRadius 4 (:23).
/// • Foreground: TextFillColorPrimary rest/selected (:11/:13), TextFillColorSecondary on PointerOver (:12) —
///   carried on the default label via <c>TextEl.HoverColor</c> (app templates own their own text).
/// • Borders: unselected hover = ControlStrokeColorOnAccentTertiary #37000000 both themes (:26 →
///   <see cref="Tok.StrokeControlOnAccentTertiary"/>) at the 1px chrome thickness (ListViewBaseItemChrome.cpp
///   s_borderThickness); selected = 2px (GridViewItemSelectedBorderThickness :25) AccentFillColorDefault (:27),
///   SelectedPointerOver = AccentFillColorSecondary (:28), SelectedPressed = AccentFillColorTertiary (:29),
///   SelectedDisabled = AccentFillColorDisabled (:30); PLUS the 1px inner ring = ControlSolidFillColorDefault
///   (SelectedInnerBorderBrush :31 → Tok.FillControlSolid) inset by the outer 2px
///   (s_innerSelectionBorderThickness, inner corner radius min 3 — s_innerBorderCornerRadius).
/// • Multi-select corner check (CheckMode = Overlay): 20×20 square (s_multiSelectSquareSize) at the TOP-RIGHT with
///   margin 0,2,2,0 (s_multiSelectSquareOverlayMargin), plate = ControlOnImageFillColorDefault (:19 →
///   Tok.FillControlOnImage), border ControlStrongStroke (:41) @ r3 (:24); checked = Accent ramp (:37-39) with the
///   TextOnAccentPrimary check glyph (:18); disabled = AccentFillColorDisabled / TextOnAccentDisabled (:40/:33).
/// • Disabled opacity 0.3 (DisabledOpacity = ListViewItemDisabledThemeOpacity, GridViewItem template :157);
///   item margin 0,0,4,4 (:144); FocusVisualMargin −3, primary 2px / secondary 1px (:149-153).
/// • Reorder: 2-D live reorder — pending slot from the dragged tile's centre against the grid geometry, the 300ms
///   GRIDVIEW_LIVEREORDER_TIMER dwell (<see cref="ReorderList.GridDwellMs"/>, ListViewBase_Partial_Reorder.cpp:51),
///   displaced tiles FLIP via the projected order, RemoveAt+Insert commit (ReorderList.Move semantics).
/// </summary>
public sealed class GridView : Component
{
    internal const float SelectedBorder = 2f;     // GridViewItemSelectedBorderThickness (:25)
    internal const float DisabledOpacity = 0.3f;  // ListViewItemDisabledThemeOpacity (GridViewItem template :157)
    internal const float CheckSize = 20f;         // s_multiSelectSquareSize (ListViewBaseItemChrome.cpp:61)
    internal const float ItemGap = 4f;            // GridViewItem Margin 0,0,4,4 (:144)

    // ── legacy simple surface (CollectionsMenusPages.cs: GridView.Create(Items, columns: 4)) ──
    public IReadOnlyList<string> Items = [];
    public int Columns = 4;
    public float TileSize = 96f;

    // ── full surface ──
    public int ItemCount = -1;
    public Func<int, Element>? ItemTemplate;
    public Func<int, string>? ItemText;
    public Func<int, bool>? IsItemEnabled;
    public ItemsSelectionMode SelectionMode = ItemsSelectionMode.Single;
    public SelectionModel? Selection;
    public Action<int>? OnItemClick;
    public Action<int>? OnItemInvoked;
    public Action? OnSelectionChanged;
    public bool CanReorderItems;
    public Action<int, int>? OnReorder;
    public ItemsViewController? Controller;
    public Func<int, string>? KeyOf;
    public float Width = float.NaN;
    public float Height = float.NaN;
    public float Grow;

    public static Element Create(IReadOnlyList<string> items, int columns = 4, float tileSize = 96f)
        => Embed.Comp(() => new GridView { Items = items, Columns = columns, TileSize = tileSize });

    /// <summary>The full WinUI-shaped factory: templated tiles over the virtualized grid.</summary>
    public static Element Create(int itemCount, Func<int, Element> itemTemplate, int columns, float tileHeight,
                                 ItemsSelectionMode selectionMode = ItemsSelectionMode.Single,
                                 SelectionModel? selection = null,
                                 Action<int>? onItemClick = null,
                                 Action<int>? onItemInvoked = null,
                                 Action? onSelectionChanged = null,
                                 bool canReorderItems = false,
                                 Action<int, int>? onReorder = null,
                                 Func<int, string>? itemText = null,
                                 Func<int, bool>? isItemEnabled = null,
                                 ItemsViewController? controller = null,
                                 Func<int, string>? keyOf = null,
                                 float width = float.NaN, float height = float.NaN, float grow = 0f)
        => Embed.Comp(() => new GridView
        {
            ItemCount = itemCount,
            ItemTemplate = itemTemplate,
            Columns = columns,
            TileSize = tileHeight,
            SelectionMode = selectionMode,
            Selection = selection,
            OnItemClick = onItemClick,
            OnItemInvoked = onItemInvoked,
            OnSelectionChanged = onSelectionChanged,
            CanReorderItems = canReorderItems,
            OnReorder = onReorder,
            ItemText = itemText,
            IsItemEnabled = isItemEnabled,
            Controller = controller,
            KeyOf = keyOf,
            Width = width, Height = height, Grow = grow,
        });

    public override Element Render()
    {
        int count = ItemCount >= 0 ? ItemCount : Items.Count;
        int columns = Math.Max(1, Columns);
        var controller = UseMemo(() => Controller ?? new ItemsViewController());
        var model = UseMemo<SelectionModel>(() => Selection ?? new SelectionModel());
        var orderVersion = UseSignal(0);
        var order = UseRef<int[]>([]);
        var rl = UseMemo(static () => new GridReorder());
        var lastDwellTick = UseRef(0L);

        // ── 2-D live-reorder projection (slot → item) ───────────────────────────────────────────────
        _ = orderVersion.Value;
        bool reordering = rl.Dragged >= 0;
        if (order.Value.Length < count) order.Value = new int[count];
        var slots = order.Value;
        if (reordering) rl.ProjectOrder(slots.AsSpan(0, count));
        else for (int i = 0; i < count; i++) slots[i] = i;

        if (reordering)
        {
            _ = UseContext(FrameClock.Tick);   // safe conditional read (no hook cell) — drives the 300ms dwell
            long now = Environment.TickCount64;
            float dt = lastDwellTick.Value == 0 ? 0f : Math.Clamp(now - lastDwellTick.Value, 0, 100);
            lastDwellTick.Value = now;
            if (rl.Advance(dt)) orderVersion.Value = orderVersion.Peek() + 1;
        }
        else lastDwellTick.Value = 0;

        float stride = TileSize + ItemGap;
        void Bump() => orderVersion.Value = orderVersion.Peek() + 1;

        void OnDragStarted(int slot)
        {
            rl.Begin(slot, count, columns);
            Bump();
        }
        void OnDragDelta(DragEventArgs e)
        {
            // Pending slot from the dragged tile's centre: row from the vertical stride, column from the realized
            // column width (cross/columns — GridVirtualLayout geometry).
            var scene = Context.Scene;
            float colW = stride;
            if (scene is not null && !Context.HostNode.IsNull && scene.IsLive(Context.HostNode))
                colW = MathF.Max(1f, scene.AbsoluteRect(Context.HostNode).W / columns);
            if (rl.Update(e.TotalDx, e.TotalDy, colW, stride)) Bump();
        }
        void OnDragCompleted()
        {
            int from = rl.Dragged, to = rl.Pending >= 0 ? rl.Pending : rl.Dragged;
            rl.Reset();
            if (from >= 0 && to != from)
            {
                if (Items is IList<string> mutable && OnReorder is null) ReorderList.Move(mutable, from, to);
                OnReorder?.Invoke(from, to);
            }
            Bump();
        }
        void OnDragCanceled() { rl.Reset(); Bump(); }

        Func<int, Element> contentOf = ItemTemplate ?? DefaultTile;
        Func<int, string>? keyFn = KeyOf;
        Func<int, string>? textFn = ItemText;
        if (ItemTemplate is null && Items.Count == count)
        {
            var items = Items;
            keyFn ??= i => items[i];
            textFn ??= i => items[i];
        }
        var slotsLocal = slots;
        var onItemClick = OnItemClick;
        bool canReorder = CanReorderItems;

        BoxEl Chrome(int slot, Element content, ItemChromeState state,
                     Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged)
        {
            int item = slot < count ? slotsLocal[slot] : slot;
            Action<ItemContainerTrigger, KeyModifiers> interact = onItemClick is null
                ? onInteraction
                : (t, m) => { if (t == ItemContainerTrigger.Tap) onItemClick(item); onInteraction(t, m); };
            var tile = BuildTile(content, state, interact, onFocusChanged);
            if (!canReorder || !state.IsEnabled) return tile;
            return tile with
            {
                CanDrag = true,
                OnDragStarted = _ => OnDragStarted(slot),
                OnDragDelta = OnDragDelta,
                OnDragCompleted = _ => OnDragCompleted(),
                OnDragCanceled = OnDragCanceled,
            };
        }

        float width = Width, height = Height;
        if (float.IsNaN(width) && Grow == 0f) width = columns * stride - ItemGap;   // legacy fixed-tile demo shape
        if (float.IsNaN(height) && Grow == 0f)
        {
            int rows = (count + columns - 1) / columns;
            height = rows <= 0 ? 0f : rows * TileSize + (rows - 1) * ItemGap;
        }

        return new BoxEl
        {
            Width = width,
            Height = height,
            Grow = Grow,
            Children =
            [
                ItemsView.Create(count,
                    itemTemplate: i => contentOf(slotsLocal[i]),
                    layout: RepeatLayout.Grid(columns, TileSize, ItemGap),
                    selectionMode: SelectionMode,
                    selection: model,
                    isItemInvokedEnabled: OnItemInvoked is not null,
                    itemInvoked: OnItemInvoked,
                    selectionChanged: OnSelectionChanged,
                    itemText: textFn is null ? null : i => textFn(slotsLocal[i]),
                    isItemEnabled: IsItemEnabled is null ? null : i => IsItemEnabled(slotsLocal[i]),
                    controller: controller,
                    containerFactory: Chrome,
                    keyOf: keyFn is null ? null : i => keyFn(slotsLocal[i])),
            ],
        };
    }

    private Element DefaultTile(int i)
        => new BoxEl
        {
            Grow = 1f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(i < Items.Count ? Items[i] : string.Empty)
                {
                    Size = 13f,
                    Color = Tok.TextPrimary,            // GridViewItemForeground (:11)
                    HoverColor = Tok.TextSecondary,     // GridViewItemForegroundPointerOver = TextFillColorSecondary (:12)
                },
            ],
        };

    /// <summary>The GridViewItem tile chrome (per-value cites in the class doc).</summary>
    internal static BoxEl BuildTile(Element content, in ItemChromeState state,
                                    Action<ItemContainerTrigger, KeyModifiers> onInteraction,
                                    Action<bool> onFocusChanged)
    {
        bool selected = state.IsSelected, enabled = state.IsEnabled;

        int n = 1 + (selected && enabled ? 1 : 0) + (state.ShowCheckbox ? 1 : 0);
        var children = new Element[n];
        int w = 0;

        children[w++] = new BoxEl { Key = "gv-content", Children = [content] };

        if (selected && enabled)
        {
            // Inner 1px ControlSolid ring inset by the 2px accent border (SelectedInnerBorderBrush :31;
            // s_innerSelectionBorderThickness; inner corner radius floor 3 — s_innerBorderCornerRadius).
            children[w++] = new BoxEl
            {
                Key = "gv-inner",
                Margin = Edges4.All(SelectedBorder),
                BorderColor = Tok.FillControlSolid,
                BorderWidth = 1f,
                Corners = CornerRadius4.All(3f),
                HitTestVisible = false,
            };
        }

        if (state.ShowCheckbox)
        {
            // Corner check (Overlay mode): 20×20 top-right, margin 0,2,2,0 (s_multiSelectSquareOverlayMargin).
            children[w++] = new BoxEl
            {
                Key = "gv-check",
                Direction = 0,
                Justify = FlexJustify.End,
                AlignItems = FlexAlign.Start,
                HitTestVisible = false,
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(167f, Easing.FluentDecelerate),
                    Enter: new EnterExit(Opacity: 0f, Active: true),
                    Exit: new EnterExit(Opacity: 0f, Active: true)),
                Children = [BuildCornerCheck(state.IsChecked, enabled)],
            };
        }

        return new BoxEl
        {
            ZStack = true,
            Margin = new Edges4(0f, 0f, ItemGap, ItemGap),   // GridViewItem Margin 0,0,4,4 (:144)
            Corners = Radii.ControlAll,                       // GridViewItemCornerRadius 4 (:23)
            // Plate ramp (:5-10, :45): selected rest/hover = Tertiary, pressed = Secondary;
            // unselected rest = Transparent, hover = Secondary, pressed = Tertiary.
            Fill = selected ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            // Border ramp: selected 2px accent (:25/:27) hover→Secondary (:28) press→Tertiary (:29);
            // unselected rest none, hover 1px ControlStrokeColorOnAccentTertiary (:26, chrome s_borderThickness 1).
            BorderWidth = selected ? SelectedBorder : 1f,
            BorderColor = selected ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled) : ColorF.Transparent,
            HoverBorderColor = selected ? Tok.AccentSecondary : Tok.StrokeControlOnAccentTertiary,
            PressedBorderColor = selected ? Tok.AccentTertiary : Tok.StrokeControlOnAccentTertiary,
            Opacity = enabled ? 1f : DisabledOpacity,
            IsEnabled = enabled,
            Focusable = true,
            FocusVisualMargin = Edges4.All(-3f),              // FocusVisualMargin −3 (:149)
            Role = AutomationRole.Button,
            OnPointerPressed = args => onInteraction(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            Children = children,
        };
    }

    /// <summary>The 20×20 overlay check square (per-value cites in the class doc).</summary>
    private static BoxEl BuildCornerCheck(bool isChecked, bool enabled)
        => new()
        {
            Width = CheckSize,
            Height = CheckSize,
            Margin = new Edges4(0f, 2f, 2f, 0f),   // s_multiSelectSquareOverlayMargin {0,2,2,0}
            Corners = CornerRadius4.All(3f),       // GridViewItemCheckBoxCornerRadius (:24)
            BorderWidth = 1f,
            Fill = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : Tok.FillControlOnImage,          // GridViewItemCheckBoxBrush = ControlOnImageFillColorDefault (:19)
            BorderColor = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : (enabled ? Tok.StrokeControlStrongDefault : Tok.StrokeControlStrongDisabled),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = isChecked
                ?
                [
                    new PolylineStrokeEl
                    {
                        Width = 14f,
                        Height = 14f,
                        P0 = new Point2(0.18f * 14f, 0.50f * 14f),
                        P1 = new Point2(0.42f * 14f, 0.72f * 14f),
                        P2 = new Point2(0.80f * 14f, 0.26f * 14f),
                        PointCount = 3,
                        Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled,   // (:18/:33)
                        Thickness = 1.8f,
                        RoundCaps = true,
                    },
                ]
                : [],
        };

    /// <summary>2-D live-reorder state: pending slot from the dragged tile's centre, dwell-committed target
    /// (GRIDVIEW_LIVEREORDER_TIMER = 300ms — ListViewBase_Partial_Reorder.cpp:51), row-major order projection
    /// (the LiveReorderHelper::MovedItems view). Grow-only / alloc-free per move.</summary>
    private sealed class GridReorder
    {
        public int Dragged = -1;
        public int Pending = -1;
        private int _target = -1;
        private int _count, _columns;
        private float _dwellRemainingMs;

        public void Begin(int dragged, int count, int columns)
        {
            Dragged = dragged; Pending = dragged; _target = dragged;
            _count = count; _columns = Math.Max(1, columns);
            _dwellRemainingMs = 0f;
        }

        public bool Update(float totalDx, float totalDy, float colWidth, float rowStride)
        {
            if (Dragged < 0) return false;
            int row0 = Dragged / _columns, col0 = Dragged % _columns;
            int col = Math.Clamp(col0 + (int)MathF.Round(totalDx / MathF.Max(1f, colWidth)), 0, _columns - 1);
            int row = Math.Max(0, row0 + (int)MathF.Round(totalDy / MathF.Max(1f, rowStride)));
            int slot = Math.Clamp(row * _columns + col, 0, _count - 1);
            if (slot == Pending) return false;
            Pending = slot;
            _dwellRemainingMs = ReorderList.GridDwellMs;   // re-arm on every drag-over change (cpp:1068-1074)
            return true;
        }

        public bool Advance(float dtMs)
        {
            if (Dragged < 0 || Pending == _target) return false;
            _dwellRemainingMs -= dtMs;
            if (_dwellRemainingMs > 0f) return false;
            _dwellRemainingMs = 0f;
            _target = Pending;
            return true;
        }

        /// <summary>Original indices in the dwell-committed projected order (ReorderList.ProjectOrder semantics).</summary>
        public void ProjectOrder(Span<int> order)
        {
            for (int i = 0; i < _count && i < order.Length; i++) order[i] = i;
            if (Dragged < 0 || _target < 0 || _target == Dragged) return;
            if (_target > Dragged)
                for (int i = Dragged; i < _target; i++) order[i] = i + 1;
            else
                for (int i = Dragged; i > _target; i--) order[i] = i - 1;
            order[_target] = Dragged;
        }

        public void Reset() { Dragged = -1; Pending = -1; _target = -1; _dwellRemainingMs = 0f; }
    }
}
