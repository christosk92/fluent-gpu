using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ComboBox: a closed field showing the selected item + a chevron that opens a dropdown list (an anchored,
/// light-dismiss overlay whose width matches the field). In <see cref="Editable"/> mode the closed field is an
/// <see cref="EditableText"/> plus the chevron column. Selection is a caller <see cref="Signal{T}"/> so a page can read it.
/// <para>
/// 1:1 with <c>ComboBox_themeresources(_perf2026).xaml</c> + <c>ComboBoxHelper.cpp</c>. Exact tokens/sizes:
/// field MinHeight=32 (<c>ComboBoxMinHeight</c>), padding 12,5,0,7 (<c>ComboBoxPadding</c>), CornerRadius=4
/// (<c>ControlCornerRadius</c>), BorderThickness=1, MinWidth=64 (<c>ComboBoxThemeMinWidth</c>); the border is the
/// <b>ControlElevationBorder</b> gradient (<c>ComboBoxBorderBrush</c>), pressed flattens to a flat stroke
/// (<c>ComboBoxBorderBrushPressed=ControlStrokeColorDefault</c>). Field fills rest/hover/pressed/disabled =
/// ControlFillColor Default/Secondary/Tertiary/Disabled; foreground ramps Primary→(pressed)Secondary→(disabled)Disabled;
/// placeholder Secondary→(pressed)Tertiary. Chevron column width 38, glyph <c></c> 12px, foreground
/// <c>TextFillColorSecondary</c>, right-inset 14 (<c>Margin 0,0,14,0</c>).
/// </para>
/// <para>
/// Items (<c>ComboBoxItem</c>): padding 11,5,11,7 (<c>ComboBoxItemThemePadding</c>), margin 5,2,5,2, CornerRadius=3
/// (<c>ComboBoxItemCornerRadius</c>); the 3×16 left accent pill (<c>ComboBoxItemPill</c>, corner 1.5, fill
/// <c>AccentFillColorDefault</c>, margin 1,0,0,0) shows only on the selected row. Item state matrix mirrors the XAML:
/// rest=SubtleTransparent, PointerOver=SubtleSecondary, Pressed=SubtleTertiary; Selected=SubtleSecondary,
/// SelectedPointerOver=SubtleTertiary, SelectedPressed=SubtleSecondary; foreground Primary, pressed→Secondary.
/// Popup: AcrylicInApp background + <c>SurfaceStrokeColorFlyout</c> 1px + <c>OverlayCornerRadius</c>(8) +
/// presenter margin 0,4 (<c>ComboBoxDropdownContentMargin</c>) + <c>MaxDropDownHeight</c>=504 with internal scroll —
/// all supplied by the overlay host's <c>FlyoutSurface</c>; here we return only the rows.
/// </para>
/// <para>
/// Open/close = the overlay host's WinUI MenuPopupThemeTransition (250ms cubic-bezier(0,0,0,1) clip-reveal + opacity;
/// 83ms close fade) — identical to <c>OverlayOpeningAnimation</c>/<c>OverlayClosingAnimation</c> (ControlNormal/Fast
/// AnimationDuration). Focus is captured on open and restored on close by the host; the popup is opened with
/// <c>PopupOptions(FocusTrap:true)</c> so Tab/arrow roving stays inside the list. Keyboard: when CLOSED, Up/Down/Home/End
/// step the selection in place and Space/Enter/F4/Alt+Down open; when OPEN, Up/Down/Home/End move a highlighted row,
/// Enter/Space commits it, Escape closes (restoring selection). Type-ahead jumps to the next item whose label starts with
/// the typed prefix (WinUI ComboBox text-search), CLOSED or OPEN.
/// </para>
/// <para>Corner-join (<c>ComboBoxHelper.UpdateCornerRadius</c>): while open-DOWN the field squares its BOTTOM corners so
/// the dropdown reads as one piece with the field (the popup squares the abutting TOP corners host-side via the placement
/// result's <see cref="CornerJoin"/>). The non-editable field overlaps the popup by design, so the join is applied while open.</para>
/// </summary>
public sealed class ComboBox : Component
{
    // ── WinUI dims (ComboBox_themeresources.xaml) ──
    public const float MinHeight = 32f;          // ComboBoxMinHeight
    public const float ThemeMinWidth = 64f;      // ComboBoxThemeMinWidth
    public const float ChevronColumn = 38f;      // template column 2 width (the glyph cell)
    public const float ChevronGlyphSize = 12f;   // AnimatedIcon 12×12
    public const float ChevronRightInset = 14f;  // DropDownGlyph Margin 0,0,14,0
    public const float ItemPillScaleMin = 0.625f;// ComboBoxItemPillMinScale (pressed)
    public const float PillScaleMs = 167f;        // ComboBoxItemScaleAnimationDuration / ControlFastAnimationDuration
    public const float MaxDropDownHeight = 504f;  // MaxDropDownHeight

    public IReadOnlyList<string> Items = [];
    public Signal<int> SelectedIndex = new(-1);
    public bool Editable;
    public Signal<string>? Text;
    public string Placeholder = "";
    public float Width = 220f;
    public bool IsEnabled = true;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real popup after first mount
    public Action<int>? OnSelectionChanged;

    public static Element Create(IReadOnlyList<string> items, Signal<int> selectedIndex, bool editable = false,
                                 Signal<string>? text = null, float width = 220f, string placeholder = "",
                                 bool isEnabled = true, Action<int>? onSelectionChanged = null)
        => Embed.Comp(() => new ComboBox
        {
            Items = items, SelectedIndex = selectedIndex, Editable = editable, Text = text,
            Width = width, Placeholder = placeholder, IsEnabled = isEnabled, OnSelectionChanged = onSelectionChanged,
        });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallbackText = UseSignal("");
        var text = Text ?? fallbackText;
        var svc = UseContext(Overlay.Service);

        // 'open' is reactive so the field can re-render its corner-join + chevron flip when the popup opens/closes; the
        // highlighted row index lives in its own signal so the popup body (a Component) re-highlights without re-rendering us.
        // 'openVer' bumps on every open/toggle so the field re-renders its corner-join + the editable chevron column;
        // the live corner-join state itself is then read from the handle (host owns light-dismiss/Escape). 'highlight'
        // is the keyboard-cursor row in the open list, in its own signal so the popup body re-highlights without re-rendering us.
        var openVer = UseSignal(0);
        var highlight = UseSignal(-1);
        var autoOpened = UseRef(false);
        int sel = SelectedIndex.Value;
        _ = openVer.Value;         // subscribe → re-render the field (corner-join / chevron) when we open/close
        float w = MathF.Max(Width, ThemeMinWidth);

        void Commit(int i)
        {
            if (i < 0 || i >= Items.Count) return;
            SelectedIndex.Value = i;
            if (Editable) text.Value = Items[i];
            OnSelectionChanged?.Invoke(i);
        }

        void Choose(int i) { Commit(i); handle.Value?.Close(); handle.Value = null; openVer.Value = openVer.Peek() + 1; }

        void Close() { handle.Value?.Close(); handle.Value = null; highlight.Value = -1; openVer.Value = openVer.Peek() + 1; }

        void OpenPopup()
        {
            if (handle.Value is { IsOpen: true } || !IsEnabled) return;
            highlight.Value = sel;     // open with the keyboard cursor on the current selection (WinUI)
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new ComboBoxList
                {
                    Owner = this, Selected = SelectedIndex, Highlight = highlight, Width = w, OnChoose = Choose,
                }),
                FlyoutPlacement.BottomStretch,
                new PopupOptions(FocusTrap: true));
            handle.Value.ClosedAction = () =>
            {
                handle.Value = null;
                highlight.Value = -1;
                openVer.Value = openVer.Peek() + 1;
            };
            openVer.Value = openVer.Peek() + 1;
        }

        void Toggle() { if (handle.Value is { IsOpen: true }) Close(); else OpenPopup(); }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            OpenPopup();
        }, OpenOnMount);

        // Type-ahead search (WinUI ComboBox): find the next item after a start index whose label starts with the prefix.
        int FindPrefix(string prefix, int startAfter)
        {
            if (Items.Count == 0 || prefix.Length == 0) return -1;
            for (int k = 1; k <= Items.Count; k++)
            {
                int idx = (startAfter + k) % Items.Count;
                if (Items[idx].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return idx;
            }
            return -1;
        }

        void HandleChar(CharEventArgs e)
        {
            if (Editable || e.Codepoint < 0x20 || e.Codepoint == 0x7F) return;   // editable handles its own text
            string ch = char.ConvertFromUtf32(e.Codepoint);
            bool isOpen = handle.Value is { IsOpen: true };
            int from = isOpen ? highlight.Peek() : sel;
            int found = FindPrefix(ch, from < 0 ? -1 : from);
            if (found < 0) return;
            if (isOpen) { highlight.Value = found; } else { Commit(found); }
            e.Handled = true;
        }

        // Closed-field keyboard: Space/Enter open the list; Up/Down/Home/End step the selection in place (WinUI). While
        // the popup is OPEN these keys are handled by the focus-trapped list, so they never reach here. (The slice key
        // event carries no modifier bits, so WinUI's F4 / Alt+Down open-shortcuts fold into Space/Enter — see report.)
        void HandleKey(KeyEventArgs e)
        {
            if (handle.Value is { IsOpen: true }) return;   // open: ComboBoxList owns nav
            switch (e.KeyCode)
            {
                case Keys.Space or Keys.Enter: OpenPopup(); e.Handled = true; break;
                case Keys.Down: Commit(sel < 0 ? 0 : Math.Min(sel + 1, Items.Count - 1)); e.Handled = true; break;
                case Keys.Up: Commit(sel < 0 ? Items.Count - 1 : Math.Max(sel - 1, 0)); e.Handled = true; break;
                case Keys.Home: Commit(0); e.Handled = true; break;
                case Keys.End: Commit(Items.Count - 1); e.Handled = true; break;
            }
        }

        bool isOpenNow = handle.Value is { IsOpen: true };
        // Corner-join (ComboBoxHelper.UpdateCornerRadius): ONLY the EDITABLE ComboBox squares the field corners — the
        // helper early-returns unless IsEditable(). The non-editable field keeps full ControlCornerRadius and the popup
        // overlaps it (XAML PopupBorder Margin 0,-0.5,0,-1). Editable, open-DOWN → square the field's BOTTOM corners
        // (the popup squares its abutting TOP corners host-side via the placement CornerJoin). Read live from the handle.
        var editableCorners = Editable && isOpenNow
            ? new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f)
            : Radii.ControlAll;

        // WinUI DropDownGlyph: AnimatedChevronDownSmall, foreground TextFillColorSecondary, disabled→TextFillColorDisabled.
        var chevron = new TextEl(Icons.ChevronDown)
        {
            Size = ChevronGlyphSize, FontFamily = Theme.IconFont,
            Color = IsEnabled ? Tok.TextSecondary : Tok.TextDisabled,
            Margin = new Edges4(0, 0, ChevronRightInset, 0), AlignSelf = FlexAlign.Center,
        };

        if (Editable)
        {
            return new BoxEl
            {
                Direction = 0, Width = w, MinHeight = MinHeight, AlignItems = FlexAlign.Center,
                Corners = editableCorners, BorderWidth = 1f,
                // WinUI ComboBoxBorderBrush = ControlElevationBorderBrush (gradient); pressed flattens to ControlStrokeColorDefault.
                BorderBrush = IsEnabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
                Fill = IsEnabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
                ClipToBounds = true,
                IsEnabled = IsEnabled,
                Role = AutomationRole.ComboBox,
                OnRealized = h => anchor.Value = h,
                Children =
                [
                    // ComboBoxEditableTextPadding 11,5,38,6 → the 38 right gutter is the chevron column.
                    Embed.Comp(() => new EditableText { Text = text, Width = w - ChevronColumn, Height = MinHeight, Placeholder = Placeholder, IsEnabled = IsEnabled }),
                    new BoxEl
                    {
                        Width = ChevronColumn, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Role = AutomationRole.Button, IsEnabled = IsEnabled,
                        // DropDownOverlay button hover/press (ComboBoxDropDownBackgroundPointerOver/PointerPressed).
                        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                        Corners = CornerRadius4.All(Radii.Control),
                        OnClick = Toggle,
                        Children = [chevron],
                    },
                ],
            };
        }

        string label = sel >= 0 && sel < Items.Count ? Items[sel] : Placeholder;
        bool isPlaceholder = sel < 0 || sel >= Items.Count;
        return new BoxEl
        {
            Direction = 0, Width = w, MinHeight = MinHeight, AlignItems = FlexAlign.Center, Padding = new Edges4(12, 5, 0, 7),
            Corners = Radii.ControlAll, BorderWidth = 1f,   // non-editable: popup overlaps the field; field keeps full ControlCornerRadius
            BorderBrush = IsEnabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
            // CommonStates: rest=Default, PointerOver=Secondary, Pressed=Tertiary, Disabled=Disabled.
            Fill = IsEnabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
            HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            // Pressed flattens the border to ControlStrokeColorDefault (ComboBoxBorderBrushPressed).
            PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),
            IsEnabled = IsEnabled,
            Focusable = IsEnabled,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            OnKeyDown = IsEnabled ? (Action<KeyEventArgs>)HandleKey : null,
            OnCharInput = IsEnabled ? (Action<CharEventArgs>)HandleChar : null,
            Children =
            [
                new TextEl(label)
                {
                    Size = 14f, Grow = 1f,
                    // ComboBoxForeground=Primary / placeholder=Secondary; pressed ramps to Secondary/Tertiary; disabled=Disabled.
                    Color = !IsEnabled ? Tok.TextDisabled : (isPlaceholder ? Tok.TextSecondary : Tok.TextPrimary),
                    PressedColor = isPlaceholder ? Tok.TextTertiary : Tok.TextSecondary,
                    DisabledColor = Tok.TextDisabled,
                },
                chevron,
            ],
        };
    }
}

/// <summary>
/// The dropdown body: a reactive Component that subscribes to the owner's <c>Selected</c> + <c>Highlight</c> signals so
/// each row re-paints its state (selected pill, keyboard-cursor highlight) without re-rendering the field. Returns ONLY
/// the rows — the acrylic surface, 1px <c>SurfaceStrokeColorFlyout</c>, <c>OverlayCornerRadius</c>, shadow and the
/// presenter padding are supplied by the host's <c>FlyoutSurface</c> (same division of labor as <c>SuggestionsList</c>).
/// <para>Keyboard nav (focus-trapped): Up/Down/Home/End move the highlight (clamped, WinUI does NOT wrap inside an open
/// list); Enter/Space commit the highlighted row; Escape closes. The root captures keys via <see cref="BoxEl.OnKeyDown"/>;
/// it is <see cref="BoxEl.Focusable"/> so the overlay's focus capture lands here.</para>
/// </summary>
internal sealed class ComboBoxList : Component
{
    public required ComboBox Owner;
    public required Signal<int> Selected;
    public required Signal<int> Highlight;
    public required float Width;
    public required Action<int> OnChoose;

    public override Element Render()
    {
        int sel = Selected.Value;          // subscribe → re-paint the selected pill
        int hi = Highlight.Value;          // subscribe → re-paint the keyboard-cursor row
        var items = Owner.Items;

        void Move(int next)
        {
            if (items.Count == 0) return;
            Highlight.Value = Math.Clamp(next, 0, items.Count - 1);   // WinUI clamps (no wrap) inside an open list
        }

        void HandleKey(KeyEventArgs e)
        {
            int cur = Highlight.Peek();
            switch (e.KeyCode)
            {
                case Keys.Down: Move(cur < 0 ? 0 : cur + 1); e.Handled = true; break;
                case Keys.Up: Move(cur < 0 ? items.Count - 1 : cur - 1); e.Handled = true; break;
                case Keys.Home: Move(0); e.Handled = true; break;
                case Keys.End: Move(items.Count - 1); e.Handled = true; break;
                case Keys.Enter or Keys.Space:
                    if (cur >= 0) OnChoose(cur);
                    e.Handled = true; break;
            }
        }

        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            int idx = i;
            bool selected = idx == sel;
            bool cursor = idx == hi;

            var label = new TextEl(items[idx]) { Size = 14f, Color = Tok.TextPrimary, PressedColor = Tok.TextSecondary, Grow = 1f };
            // ComboBoxItem template: Pill is a LayoutRoot sibling of ContentPresenter. That means the pill is placed
            // at the ROW edge (Margin 1,0,0,0), while text starts at ContentPresenter.Margin=11,5,11,7. Do not put the
            // pill inside the padded content box or it overlaps the first glyph.
            var content = new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Grow = 1f,
                Padding = new Edges4(11, 5, 11, 7),
                Children = [label],
            };
            Element[] rowChildren = selected
                ? [content, new BoxEl { Width = 3f, Height = 16f, Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault, AlignSelf = FlexAlign.Center, Margin = new Edges4(1, 0, 0, 0) }]
                : [content];

            // Item state matrix (ComboBox_themeresources): unselected rest=Transparent, hover=SubtleSecondary,
            // press=SubtleTertiary; selected rest=SubtleSecondary, hover=SubtleTertiary, press=SubtleSecondary.
            // The keyboard cursor (Highlight) reads as the hover/selected fill so arrow nav is visible without a pointer.
            ColorF rest = selected || cursor ? Tok.FillSubtleSecondary : ColorF.Transparent;
            rows[i] = new BoxEl
            {
                ZStack = true,
                Width = MathF.Max(0f, Width - 10f),   // LayoutRoot Margin 5,2,5,2 inside a width-matched popup
                AlignItems = FlexAlign.Stretch,
                Margin = new Edges4(5, 2, 5, 2),
                Corners = CornerRadius4.All(3f),
                Role = AutomationRole.MenuItem,
                Fill = rest,
                HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
                OnClick = () => OnChoose(idx),
                Children = rowChildren,
            };
        }

        // Inner column. WinUI's
        // ItemsPresenter has ComboBoxDropdownContentMargin=0,4,0,4; FlyoutSurface supplies 2px top/bottom, so add 2px
        // here to reach the 4px dropdown content inset while still measuring short lists to their rows.
        var column = new BoxEl
        {
            Direction = 1,
            MinWidth = Width,
            Margin = new Edges4(0, 2, 0, 2),
            Children = rows,
        };

        return new BoxEl
        {
            Direction = 1,
            Focusable = true,                 // overlay focus capture lands here → keyboard nav works immediately
            Role = AutomationRole.ComboBox,   // dropdown of a ComboBox (no dedicated ListBox role in the slice a11y enum)
            OnKeyDown = HandleKey,
            // Size to content, capped at MaxDropDownHeight with internal scroll. Auto scroll viewports now measure to
            // content, then clamp by MaxHeight, which is the ComboBox drop-down behavior.
            Children =
            [
                new ScrollEl
                {
                    Content = column,
                    ContentSized = true,
                    MinWidth = Width,
                    MaxHeight = ComboBox.MaxDropDownHeight,
                },
            ],
        };
    }
}
