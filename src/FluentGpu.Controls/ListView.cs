using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>ListView</c> — a thin L4 SKIN over the E11-L3 substrate: <see cref="ItemsView"/> supplies the ONE
/// selection model (<see cref="SelectionModel"/> — None/Single/Multiple/Extended), keyboard navigation
/// (arrows/Home/End/PageUp/Down/Space/Enter/Ctrl+A/typeahead), focus rings and StartBringItemIntoView; this file only
/// authors the WinUI <c>ListViewItemPresenter</c> chrome around each row (the old hand-rolled body is deleted).
///
/// Chrome verified against microsoft-ui-xaml:
/// • Row plate — controls\dev\CommonStyles\ListViewItem_themeresources.xaml (Default==Light for these, all
///   SubtleFill aliases): Background = SubtleFillColorTransparent (:17), PointerOver = SubtleFillColorSecondary (:18),
///   Pressed = SubtleFillColorTertiary (:19), Selected = Secondary (:20), SelectedPointerOver = Tertiary (:21),
///   SelectedPressed = Secondary (:22), SelectedDisabled = Secondary (:74). Foreground = TextFillColorPrimary in
///   every state (:23-28). CornerRadius 4 (:58); backplate margin 4,2 (ListViewBaseItemChrome.cpp s_backplateMargin);
///   content padding 16,0,12,0 (DefaultListViewItemStyle :241); MinHeight 40 (:14); Disabled opacity 0.3 (:6).
/// • Selection indicator — 3×16 @ r1.5 (s_selectionIndicatorSize, ListViewBaseItemChrome.cpp:52;
///   ListViewItemSelectionIndicatorCornerRadius :60), left margin 4 (s_selectionIndicatorMargin :58 {4,20,0,20}),
///   AccentFillColorDefault in all pointer states / AccentFillColorDisabled disabled (:75-78); pressed shrinks it by
///   6px (s_selectionIndicatorHeightShrinkage :55 — engine: uniform PressScale 10/16, the interaction scale is
///   single-axis-less by design). Reveal = spring grow from centre (the NavigationView indicator feel — the chrome's
///   scale animation, ListViewBaseItemChrome.cpp:2855-2865), <see cref="MotionSprings.NavPill"/>.
/// • Multi-select inline checkbox — 20×20 (s_multiSelectSquareSize:61) @ r3 (ListViewItemCheckBoxCornerRadius :59),
///   1px border (s_multiSelectRoundedSquareThickness:67), left margin 14 (s_multiSelectRoundedSquareInlineMargin:73);
///   content shifts +28 (s_multiSelectRoundedContentOffset:92); slide-in/out animates over 333ms with
///   KeySpline 0.1,0.9,0.2,1 (MultiSelectEnabled/Disabled storyboards, ListViewItem_themeresources.xaml:385-430) —
///   exactly <see cref="Easing.FluentDecelerate"/>. Plate = ControlAltFillColorSecondary (ListViewItemCheckBoxBrush
///   :34), border ControlStrongStroke (:70); checked = Accent Default/Secondary/Tertiary ramp (:66-68) with the
///   TextOnAccentPrimary check (:33).
/// • Focus — FocusVisualMargin 1, primary 2px / secondary 1px (:248-252) via the engine focus-visual system.
/// • Reorder — composes the E5 drag engine (BoxEl.CanDrag) + <see cref="ReorderList"/>: 4px drag box promotion,
///   0.80 drag opacity (DragController), the 200ms LISTVIEW_LIVEREORDER_TIMER dwell (ReorderList.ListDwellMs,
///   ListViewBase_Partial_Reorder.cpp:50), displaced siblings FLIP via the projected order, edge auto-scroll through
///   <see cref="ItemsViewController.ScrollBy"/>, and the WinUI RemoveAt+Insert commit (ReorderList.Move).
/// </summary>
public sealed class ListView : Component
{
    // ── WinUI metrics (cites in the class doc) ───────────────────────────────────────────────────
    internal const float MinRowHeight = 40f;          // ListViewItemMinHeight (:14)
    internal const float RowMarginX = 4f, RowMarginY = 2f;   // s_backplateMargin {4,2,4,2}
    internal const float ContentPadLeft = 16f, ContentPadRight = 12f;   // Padding 16,0,12,0 (:241)
    internal const float CheckboxContentOffset = 28f; // s_multiSelectRoundedContentOffset
    internal const float CheckboxLeftMargin = 14f;    // s_multiSelectRoundedSquareInlineMargin
    internal const float CheckboxSize = 20f;          // s_multiSelectSquareSize
    internal const float MultiSelectAnimMs = 333f;    // MultiSelect storyboards (:385-430), KeySpline 0.1,0.9,0.2,1
    internal const float DisabledOpacity = 0.3f;      // ListViewItemDisabledThemeOpacity (:6)
    internal const float IndicatorPressScale = 10f / 16f;   // 16 − s_selectionIndicatorHeightShrinkage(6)

    /// <summary>Default slot stride: MinHeight 40 + the 2+2 backplate margins.</summary>
    public const float DefaultItemExtent = 44f;

    // ── legacy simple surface (source-compatible: CollectionsMenusPages.cs uses Create(items, selected)) ──
    public IReadOnlyList<string> Items = [];
    /// <summary>Controlled single-selection index (kept two-way-synced with the <see cref="SelectionModel"/>).</summary>
    public Signal<int> SelectedIndex = new(-1);
    public Action<int>? OnSelectionChanged;

    // ── full surface ──
    public int ItemCount = -1;                         // −1 ⇒ Items.Count
    public Func<int, Element>? ItemTemplate;
    public Func<int, string>? ItemText;                // typeahead text
    public Func<int, bool>? IsItemEnabled;
    public ItemsSelectionMode SelectionMode = ItemsSelectionMode.Single;
    public SelectionModel? Selection;
    /// <summary>WinUI <c>IsItemClickEnabled</c>+<c>ItemClick</c>: fires on every pointer tap regardless of the
    /// invoke matrix (ListViewBase raises ItemClick on the click edge before selection).</summary>
    public Action<int>? OnItemClick;
    /// <summary>WinUI <c>ItemsView.ItemInvoked</c> gating (Enter/DoubleTap with a selection mode; see ItemsView).</summary>
    public Action<int>? OnItemInvoked;
    /// <summary>WinUI <c>CanReorderItems</c>: rows become drag sources; reorder commits via <see cref="OnReorder"/>.</summary>
    public bool CanReorderItems;
    /// <summary>Reorder commit (fromIndex, toIndex) in the pre-drop order — apply <c>ReorderList.Move</c> to your list.</summary>
    public Action<int, int>? OnReorder;
    /// <summary>Main-axis slot extent per row (uniform virtualization stride).</summary>
    public float ItemExtent = DefaultItemExtent;
    public ItemsViewController? Controller;
    public Func<int, string>? KeyOf;
    public float Width = float.NaN;
    public float Height = float.NaN;
    public float Grow;

    public static Element Create(IReadOnlyList<string> items,
                                 Signal<int>? selectedIndex = null,
                                 Action<int>? onSelectionChanged = null)
        => Embed.Comp(() => new ListView
        {
            Items = items,
            SelectedIndex = selectedIndex ?? new Signal<int>(-1),
            OnSelectionChanged = onSelectionChanged,
        });

    /// <summary>The full WinUI-shaped factory: templated rows over the virtualized stack.</summary>
    public static Element Create(int itemCount, Func<int, Element> itemTemplate,
                                 ItemsSelectionMode selectionMode = ItemsSelectionMode.Single,
                                 SelectionModel? selection = null,
                                 Action<int>? onItemClick = null,
                                 Action<int>? onItemInvoked = null,
                                 Action<int>? onSelectionIndexChanged = null,
                                 bool canReorderItems = false,
                                 Action<int, int>? onReorder = null,
                                 Func<int, string>? itemText = null,
                                 Func<int, bool>? isItemEnabled = null,
                                 ItemsViewController? controller = null,
                                 Func<int, string>? keyOf = null,
                                 float itemExtent = DefaultItemExtent,
                                 float width = float.NaN, float height = float.NaN, float grow = 0f)
        => Embed.Comp(() => new ListView
        {
            ItemCount = itemCount,
            ItemTemplate = itemTemplate,
            SelectionMode = selectionMode,
            Selection = selection,
            OnItemClick = onItemClick,
            OnItemInvoked = onItemInvoked,
            OnSelectionChanged = onSelectionIndexChanged,
            CanReorderItems = canReorderItems,
            OnReorder = onReorder,
            ItemText = itemText,
            IsItemEnabled = isItemEnabled,
            Controller = controller,
            KeyOf = keyOf,
            ItemExtent = itemExtent,
            Width = width, Height = height, Grow = grow,
        });

    public override Element Render()
    {
        int count = ItemCount >= 0 ? ItemCount : Items.Count;
        var controller = UseMemo(() => Controller ?? new ItemsViewController());
        var model = UseMemo<SelectionModel>(() => Selection ?? new SelectionModel());
        var reorder = UseMemo(static () => new ReorderList { DwellMs = ReorderList.ListDwellMs });
        var orderVersion = UseSignal(0);
        var order = UseRef<int[]>([]);
        var lastDwellTick = UseRef(0L);
        var syncedIndex = UseRef(-2);

        // Two-way SelectedIndex ⇄ SelectionModel sync (the legacy controlled-signal surface).
        int wantIndex = SelectedIndex.Value;
        if (wantIndex != syncedIndex.Value)
        {
            syncedIndex.Value = wantIndex;
            if (wantIndex >= 0) model.Select(wantIndex);
            else if (model.SelectedCount > 0 && wantIndex == -1) model.DeselectAll();
        }

        void SelectionToSignal()
        {
            int first = model.FirstSelectedIndex;
            syncedIndex.Value = first;                       // suppress the echo re-sync
            if (SelectedIndex.Peek() != first) SelectedIndex.Value = first;
            OnSelectionChanged?.Invoke(first);
        }

        // ── live-reorder projection: slot s displays item order[s] (identity when idle) ─────────────
        _ = orderVersion.Value;   // subscribe — dwell-commits re-render with the new projection
        bool reordering = reorder.IsActive;
        if (order.Value.Length < count) order.Value = new int[count];
        var slots = order.Value;
        if (reordering) reorder.ProjectOrder(slots.AsSpan(0, count));
        else for (int i = 0; i < count; i++) slots[i] = i;

        // The 200ms live-reorder dwell ticks on the frame clock ONLY while a drag is active (the conditional
        // FrameClock read is safe — UseContext keeps no hook cell).
        if (reordering)
        {
            _ = UseContext(FrameClock.Tick);
            long now = Environment.TickCount64;
            float dt = lastDwellTick.Value == 0 ? 0f : Math.Clamp(now - lastDwellTick.Value, 0, 100);
            lastDwellTick.Value = now;
            if (reorder.Advance(dt)) orderVersion.Value = orderVersion.Peek() + 1;
        }
        else lastDwellTick.Value = 0;

        var ctx = Context;   // for edge auto-scroll (host-node rect)
        void OnDragStarted(int slot, DragEventArgs e)
        {
            reorder.Begin(slot, count, ItemExtent, spacing: 0f);
            reorder.OnCommit = (from, to) =>
            {
                if (Items is IList<string> mutable && OnReorder is null) ReorderList.Move(mutable, from, to);
                OnReorder?.Invoke(from, to);
            };
            orderVersion.Value = orderVersion.Peek() + 1;    // re-render → arms the dwell frame ticks
        }

        void OnDragDelta(DragEventArgs e)
        {
            if (reorder.Update(e.TotalDy)) orderVersion.Value = orderVersion.Peek() + 1;
            // Edge auto-scroll (E5-L3): pointer within 24dip of the viewport edge nudges the scroll.
            var scene = ctx.Scene;
            if (scene is not null && !ctx.HostNode.IsNull && scene.IsLive(ctx.HostNode))
            {
                var r = scene.AbsoluteRect(ctx.HostNode);
                const float edge = 24f, speed = 8f;
                if (e.Absolute.Y < r.Y + edge) controller.ScrollBy(-speed);
                else if (e.Absolute.Y > r.Y + r.H - edge) controller.ScrollBy(speed);
            }
        }

        void OnDragCompleted(DragEventArgs e)
        {
            reorder.Complete();                              // fires OnCommit when the slot changed
            orderVersion.Value = orderVersion.Peek() + 1;
        }

        void OnDragCanceled()
        {
            reorder.Cancel();
            orderVersion.Value = orderVersion.Peek() + 1;
        }

        Func<int, Element> contentOf = ItemTemplate ?? DefaultRowContent;
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

        // L4 chrome around the L3 substrate. Slot → item projection rides the template + key functions, so a
        // dwell-committed live reorder MOVES keyed nodes (the FLIP pipeline animates the displaced siblings while
        // DragController re-anchors the pointer-held visual).
        BoxEl Chrome(int slot, Element content, ItemChromeState state,
                     Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged)
        {
            int item = slot < count ? slotsLocal[slot] : slot;
            Action<ItemContainerTrigger, KeyModifiers> interact = onItemClick is null
                ? onInteraction
                : (t, m) => { if (t == ItemContainerTrigger.Tap) onItemClick(item); onInteraction(t, m); };
            var row = BuildRow(content, state, interact, onFocusChanged);
            if (!canReorder || !state.IsEnabled) return row;
            return row with
            {
                CanDrag = true,
                OnDragStarted = e => OnDragStarted(slot, e),
                OnDragDelta = OnDragDelta,
                OnDragCompleted = OnDragCompleted,
                OnDragCanceled = OnDragCanceled,
            };
        }

        float height = Height;
        // Legacy content-sizing: an unconstrained list realizes all rows at its natural height (the WinUI gallery
        // card shape); apps with big data set Height/Grow and get true windowed virtualization.
        bool natural = float.IsNaN(height) && Grow == 0f;
        if (natural) height = count * ItemExtent;

        return new BoxEl
        {
            Width = Width,
            Height = height,
            Grow = Grow,
            Direction = 1,   // the list axis is vertical (D1 hygiene): the inner ItemsView grows the HEIGHT axis
            Children =
            [
                ItemsView.Create(count,
                    itemTemplate: i => contentOf(slotsLocal[i]),
                    layout: RepeatLayout.Stack(ItemExtent),
                    selectionMode: SelectionMode,
                    selection: model,
                    isItemInvokedEnabled: OnItemInvoked is not null,
                    itemInvoked: OnItemInvoked,
                    selectionChanged: SelectionToSignal,
                    itemText: textFn is null ? null : i => textFn(slotsLocal[i]),
                    isItemEnabled: IsItemEnabled is null ? null : i => IsItemEnabled(slotsLocal[i]),
                    controller: controller,
                    containerFactory: Chrome,
                    keyOf: keyFn is null ? null : i => keyFn(slotsLocal[i]),
                    // Natural (unconstrained) lists size the viewport to its content extent — the auto-WIDTH chain
                    // also needs the non-flexing viewport's availW fallback (the 280-card gallery shape, D1).
                    // Constrained lists (explicit Height or Grow) keep the hard-viewport fill path.
                    grow: natural ? 0f : 1f),
            ],
        };
    }

    private Element DefaultRowContent(int i)
        => new TextEl(i < Items.Count ? Items[i] : string.Empty)
        {
            Size = 14f,
            Color = Tok.TextPrimary,        // ListViewItemForeground = TextFillColorPrimary, every state (:23-28)
            Grow = 1f,
        };

    /// <summary>The ListViewItemPresenter row chrome (see the class doc for per-value cites). Public-shape via
    /// <see cref="ItemContainerFactory"/> so other skins (AutoSuggest popups, pickers) can reuse the exact row.</summary>
    internal static BoxEl BuildRow(Element content, in ItemChromeState state,
                                   Action<ItemContainerTrigger, KeyModifiers> onInteraction,
                                   Action<bool> onFocusChanged)
    {
        bool selected = state.IsSelected, enabled = state.IsEnabled;
        float contentLeft = ContentPadLeft + (state.ShowCheckbox ? CheckboxContentOffset : 0f);

        int n = 1 + (state.ShowCheckbox ? 1 : 0) + (selected && enabled ? 1 : 0);
        var children = new Element[n];
        int w = 0;

        // Content lane: ListViewItem Padding 16,0,12,0 (+28 content offset in multi-select). The lane carries a
        // position FLIP so the ±28 shift slides over the MultiSelect storyboard timing (333ms FluentDecelerate).
        children[w++] = new BoxEl
        {
            Key = "lv-content",
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Grow = 1f,
            Padding = new Edges4(contentLeft, 0, ContentPadRight, 0),
            Animate = new LayoutTransition(TransitionChannels.Position,
                TransitionDynamics.Tween(MultiSelectAnimMs, Easing.FluentDecelerate)),
            Children = [content],
        };

        if (state.ShowCheckbox)
        {
            // Inline multi-select checkbox: 20×20 @ left 14, slide-in from −28 over 333ms (storyboard :385-430).
            children[w++] = new BoxEl
            {
                Key = "lv-check",
                Direction = 0,
                AlignItems = FlexAlign.Center,
                HitTestVisible = false,
                Padding = new Edges4(CheckboxLeftMargin, 0, 0, 0),
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(MultiSelectAnimMs, Easing.FluentDecelerate),
                    Enter: new EnterExit(Dx: -CheckboxContentOffset, Opacity: 0f, Active: true),
                    Exit: new EnterExit(Dx: -CheckboxContentOffset, Opacity: 0f, Active: true)),
                Children = [BuildCheckPlate(state.IsChecked, enabled)],
            };
        }

        if (selected && enabled)
        {
            // Selection indicator: 3×16 @ r1.5, left 4, vertically centred; spring-grows from centre on reveal
            // (NavPill preset); pressed shrinks toward 10/16 (chrome height shrinkage 6).
            children[w++] = new BoxEl
            {
                Key = "lv-pill",
                Width = 3f,
                Height = 16f,
                Margin = new Edges4(4f, 0f, 0f, 0f),       // s_selectionIndicatorMargin.left = 4
                Corners = CornerRadius4.All(1.5f),         // ListViewItemSelectionIndicatorCornerRadius (:60)
                Fill = Tok.AccentDefault,                  // ListViewItemSelectionIndicatorBrush (:75-77, all states)
                AlignSelf = FlexAlign.Center,
                HitTestVisible = false,
                PressScale = IndicatorPressScale,          // s_selectionIndicatorHeightShrinkage analogue (uniform scale)
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    new TransitionDynamics(DynamicsKind.Spring, 0.30f, 0.85f),   // MotionSprings.NavPill values
                    Enter: new EnterExit(Sy: 0f, Opacity: 0f, Active: true)),
            };
        }

        return new BoxEl
        {
            ZStack = true,
            MinHeight = MinRowHeight,
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(RowMarginX, RowMarginY, RowMarginX, RowMarginY),   // s_backplateMargin 4,2
            Corners = Radii.ControlAll,                                            // ListViewItemCornerRadius 4 (:58)
            // Backplate ramp (:17-22, :74): selected rest=Secondary / hover=Tertiary / pressed=Secondary;
            // unselected rest=Transparent / hover=Secondary / pressed=Tertiary.
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Opacity = enabled ? 1f : DisabledOpacity,      // ListViewItemDisabledThemeOpacity (:6)
            IsEnabled = enabled,
            Focusable = true,                              // UseSystemFocusVisuals (:247)
            FocusVisualMargin = Edges4.All(1f),            // FocusVisualMargin = 1 (:248)
            Role = AutomationRole.Button,
            // Press-edge selection with the modifier chord + Enter/Space kept distinct (the ItemContainer idiom).
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

    /// <summary>The 20×20 inline check plate: ControlAltFillColorSecondary plate + ControlStrongStroke 1px @ r3
    /// unchecked (:34/:70/:59); checked = Accent fill + TextOnAccentPrimary drawn checkmark (:66/:33);
    /// disabled checked = AccentFillColorDisabled + TextOnAccentDisabled (:69/:62).</summary>
    private static BoxEl BuildCheckPlate(bool isChecked, bool enabled)
        => new()
        {
            Width = CheckboxSize,
            Height = CheckboxSize,
            Corners = CornerRadius4.All(3f),               // ListViewItemCheckBoxCornerRadius (:59)
            BorderWidth = 1f,                              // s_multiSelectRoundedSquareThickness (:67)
            Fill = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : Tok.FillControlAltSecondary,
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
                        Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled,
                        Thickness = 1.8f,
                        RoundCaps = true,
                    },
                ]
                : [],
        };
}
