using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ComboBox: a closed field showing the selected item + a chevron that opens a dropdown list (an anchored,
/// light-dismiss overlay whose width matches the field). In <see cref="Editable"/> mode ONE chrome-owning box (the
/// template's single Background border spanning BOTH grid columns, ComboBox_themeresources.xaml:571
/// <c>Grid.ColumnSpan="2"</c>) hosts a chromeless <see cref="EditableText"/> spanning the full width (:580
/// <c>ColumnSpan="2"</c>, <c>BorderBrush="Transparent"</c>, padding ComboBoxEditableTextPadding 11,5,38,6 :342) with
/// the 30-wide DropDownOverlay button (:581) and the 12×12 chevron glyph (:582–587) overlaid at the right — never a
/// second bordered box. Selection is a caller <see cref="Signal{T}"/> so a page can read it.
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
/// <c>AccentFillColorDefault</c>, flush left — no margin in the ITEM template) shows only on the selected row. Item
/// state matrix mirrors the XAML:
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
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The field root — the template's Background border (editable: the single ColumnSpan=2 chrome box;
    /// non-editable: the closed field row). State fills/borders (rest/hover/pressed/focused/error ramps) are computed
    /// per render BEFORE the modifier — override at will. Owned: OnRealized (anchor capture, chained), OnClick
    /// (toggle, non-editable), OnKeyDown/OnCharInput/OnFocusChanged, Focusable, Role, Children (value label / TextBox
    /// part + overlay + glyph).</summary>
    public const string PartField = "Field";
    /// <summary>The 12×12 DropDownGlyph cell (both modes). Owned: HitTestVisible (the glyph is decoration — the
    /// field / DropDownOverlay button owns the click).</summary>
    public const string PartChevron = "Chevron";
    /// <summary>One dropdown row (the ComboBoxItem LayoutRoot) — popup-built each open, NOT recycled, so per-row
    /// modifiers are safe. State fills (selected/cursor rest + hover/press ramp) are computed per render BEFORE the
    /// modifier. Owned: OnClick (commit), Role, Children (pill + content).</summary>
    public const string PartItemRow = "ItemRow";
    /// <summary>The 3×16 accent pill (ComboBoxItemPill), mounted only on the selected row. Owned: none.</summary>
    public const string PartItemPill = "ItemPill";

    // ── WinUI dims (ComboBox_themeresources.xaml) ──
    public const float MinHeight = 32f;          // ComboBoxMinHeight
    public const float ThemeMinWidth = 64f;      // ComboBoxThemeMinWidth
    public const float ChevronColumn = 38f;      // template column 2 width (the glyph cell) = the editable text right padding (ComboBoxEditableTextPadding 11,5,38,6)
    public const float ChevronGlyphSize = 12f;   // AnimatedIcon 12×12
    public const float ChevronRightInset = 14f;  // DropDownGlyph Margin 0,0,14,0
    public const float OverlayButtonWidth = 30f; // DropDownOverlay Width=30 (ComboBox_themeresources.xaml:581)
    public const float OverlayButtonInset = 4f;  // DropDownOverlay Margin=4,4,4,4 (:581)
    public const float ItemPillScaleMin = 0.625f;// ComboBoxItemPillMinScale (pressed)
    public const float PillScaleMs = 167f;        // ComboBoxItemScaleAnimationDuration / ControlFastAnimationDuration
    public const float MaxDropDownHeight = 504f;  // MaxDropDownHeight (generic.xaml:8887 style setter)
    // Item geometry (ComboBoxItem template): content padding 11,5,11,7 (ComboBoxItemThemePadding) around a 20px
    // 14pt line + LayoutRoot margin 5,2,5,2 → a 36px row pitch; touch mode pads 11,11,11,13 (ComboBoxItemThemeTouchPadding,
    // generic.xaml:131) → 48px pitch. The 4px dropdown content inset is ComboBoxDropdownContentMargin 0,4,0,4.
    internal static readonly Edges4 ItemPadding = new(11, 5, 11, 7);
    internal static readonly Edges4 ItemTouchPadding = new(11, 11, 11, 13);
    internal const float DropdownContentInset = 4f;
    internal const float ItemLineHeight = 20f;

    public IReadOnlyList<string> Items = [];
    /// <summary>Optional per-item description lines (WinUI ComboBoxItem content with a secondary TextBlock). When set,
    /// dropdown rows render title + caption; the closed field still shows <see cref="Items"/> only.</summary>
    public IReadOnlyList<string>? ItemDescriptions;
    /// <summary>Optional per-item enabled flags. Disabled rows are greyed out and cannot be selected.</summary>
    public IReadOnlyList<bool>? ItemEnabled;
    public Signal<int> SelectedIndex = new(-1);
    public bool Editable;
    public Signal<string>? Text;
    public string Placeholder = "";
    public float Width = 220f;
    public bool IsEnabled = true;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real popup after first mount
    /// <summary>WinUI <c>Header</c> (HeaderContentPresenter, generic.xaml:9155-9166): a label row above the field,
    /// FontWeight Normal (ComboBoxHeaderThemeFontWeight), margin 0,0,0,4 (ComboBoxTopHeaderMargin, generic.xaml:5911).</summary>
    public string Header = "";
    /// <summary>WinUI <c>Description</c> (DescriptionPresenter, generic.xaml:9233-9239): helper text below the field
    /// in SystemControlDescriptionTextForegroundBrush.</summary>
    public string Description = "";
    /// <summary>Input-validation error message (WinUI InputValidation InlineErrors state, generic.xaml:9118-9127):
    /// non-empty → the field border swaps to SystemControlErrorTextForegroundBrush and the message renders below the
    /// field (replacing the Description row, per the InlineErrors setters).</summary>
    public string ErrorText = "";
    /// <summary>form-validation.md: a reactive validation field (over <see cref="SelectedIndex"/>). When set, its gated
    /// error drives the SAME InlineErrors visual as <see cref="ErrorText"/> (border swap + message row); an explicit
    /// <see cref="ErrorText"/> still wins. Marked touched on a selection commit.</summary>
    public Field<int>? Field;
    /// <summary>WinUI TouchInputMode/GameControllerInputMode: items take ComboBoxItemThemeTouchPadding 11,11,11,13
    /// (generic.xaml:131) instead of the pointer padding 11,5,11,7.</summary>
    public bool TouchInputMode;
    public Action<int>? OnSelectionChanged;
    /// <summary>WinUI <c>TextSubmitted</c> with the Handled contract (ComboBoxTextSubmittedEventArgs): raised on commit
    /// (Enter / Tab / focus loss) when the typed text matched NO item during search (ComboBox_Partial.cpp:2487–2513).
    /// Return true = handled (the app accepted the custom value; the default matching is skipped); false/null → the
    /// default: exact-match the items case-insensitively and select on a hit (cpp:2516–2543), else the text stays as a
    /// custom value with <see cref="SelectedIndex"/> = −1.</summary>
    public Func<string, bool>? OnTextSubmitted;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary>LIVE enabled flag re-pushed to the mounted core (<c>Embed.Comp(props, …)</c>): <see cref="IsEnabled"/>
    /// is a plain field, so via a propless <c>Embed.Comp</c> it freezes at mount — toggling a setting that enables/
    /// disables a dropdown would be silently dropped. <see cref="Create"/> routes it through re-pushed props so the
    /// flag re-renders reactively; the frozen field is the fallback for direct callers. A tiny record carries the bool
    /// because re-pushed props are class-typed.</summary>
    internal sealed record EnabledProps(bool IsEnabled);

    public static Element Create(IReadOnlyList<string> items, Signal<int> selectedIndex, bool editable = false,
                                 Signal<string>? text = null, float width = 220f, string placeholder = "",
                                 bool isEnabled = true, Action<int>? onSelectionChanged = null,
                                 Func<string, bool>? onTextSubmitted = null,
                                 string header = "", string description = "", string errorText = "",
                                 bool touchInputMode = false, Field<int>? field = null,
                                 IReadOnlyList<string>? itemDescriptions = null,
                                 IReadOnlyList<bool>? itemEnabled = null)
        => Embed.Comp(new EnabledProps(isEnabled), () => new ComboBox
        {
            Items = items, ItemDescriptions = itemDescriptions, ItemEnabled = itemEnabled,
            SelectedIndex = selectedIndex, Editable = editable, Text = text,
            Width = width, Placeholder = placeholder, IsEnabled = isEnabled, OnSelectionChanged = onSelectionChanged,
            OnTextSubmitted = onTextSubmitted,
            Header = header, Description = description, ErrorText = errorText, TouchInputMode = touchInputMode, Field = field,
        });

    internal bool IsItemEnabled(int index)
        => ItemEnabled is not { } flags || index < 0 || index >= flags.Count || flags[index];

    internal static float ItemRowPitch(ComboBox owner, int index)
    {
        var p = owner.TouchInputMode ? ItemTouchPadding : ItemPadding;
        float text = ItemLineHeight;
        if (owner.ItemDescriptions is { } d && index >= 0 && index < d.Count && d[index] is { Length: > 0 } desc)
            text += 2f + 16f;   // 2px gap + 12pt caption line
        return p.Top + p.Bottom + text + 4f;   // + LayoutRoot margin 5,2,5,2
    }

    private EditableText? _edit;

    public override Element Render()
    {
        // Read the enabled flag reactively: the provider (Create) wins over the frozen field. A shadowing local named
        // IsEnabled so every downstream read in this method — the field chrome, OpenPopup gate, key/click handlers —
        // sees the live value with no further edits. `this.IsEnabled` is the mount-time fallback for direct callers.
        bool IsEnabled = UsePropsOrDefault<EnabledProps>()?.IsEnabled ?? this.IsEnabled;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallbackText = UseSignal("");
        var text = Text ?? fallbackText;
        var svc = UseContext(Overlay.Service);
        var hooks = UseContext(InputHooks.Current);

        // ── Editable-mode search state (ComboBox_Partial.cpp m_searchResultIndex/m_searchResultIndexSet) ────────
        var searchIdx = UseRef(-1);
        var searchSet = UseRef(false);
        var restoreIdx = UseRef(-1);     // m_indexToRestoreOnCancel — the selection when editing began (cpp:2448–2455)
        var cancelling = UseRef(false);  // Escape path: skip the commit that the EditableText blur would otherwise run

        // 'open' is reactive so the field can re-render its corner-join + chevron flip when the popup opens/closes; the
        // highlighted row index lives in its own signal so the popup body (a Component) re-highlights without re-rendering us.
        // 'openVer' bumps on every open/toggle so the field re-renders its corner-join + the editable chevron column;
        // the live corner-join state itself is then read from the handle (host owns light-dismiss/Escape). 'highlight'
        // is the keyboard-cursor row in the open list, in its own signal so the popup body re-highlights without re-rendering us.
        var openVer = UseSignal(0);
        var highlight = UseSignal(-1);
        // Editable: the TextBox part's focus drives the OUTER box's focused chrome (the 2px accent bottom bar) —
        // WinUI's EditableModeStates TextBoxFocused family keys off the inner TextBox focus, not the combo root.
        var editFocusedSig = UseSignal(false);
        var autoOpened = UseRef(false);
        int sel = SelectedIndex.Value;
        _ = openVer.Value;         // subscribe → re-render the field (corner-join / chevron) when we open/close
        float w = MathF.Max(Width, ThemeMinWidth);

        void Commit(int i)
        {
            if (i < 0 || i >= Items.Count || !IsItemEnabled(i)) return;
            // Selector semantics: writing the SAME index is a no-op — no SelectionChanged, no text rewrite (WinUI
            // put_SelectedIndex short-circuits unchanged values), so the popup-close CommitSearch never double-fires
            // after a row click already committed the index.
            if (SelectedIndex.Peek() == i) return;
            SelectedIndex.Value = i;
            Field?.MarkTouched();   // form-validation.md: a selection commit arms the OnTouched gate
            if (Editable)
            {
                // Selector_SelectedItem change → SetSearchResultIndex(selectedIndex) (ComboBox_Partial.cpp:1328–1336):
                // sync the search state so a row click after typing can never re-commit a stale search index.
                searchSet.Value = true;
                searchIdx.Value = i;
                // Commit updates the field SYNCHRONOUSLY with the item text selected-all (UpdateEditableTextBox
                // selectAll:true, ComboBox_Partial.cpp:2585); a bare signal write defers the document fold.
                if (_edit is { } e) e.ReplaceText(Items[i], 0, Items[i].Length);
                else text.Value = Items[i];
            }
            OnSelectionChanged?.Invoke(i);
        }

        void Choose(int i) { Commit(i); handle.Value?.Close(); handle.Value = null; openVer.Value = openVer.Peek() + 1; }

        void Close() { handle.Value?.Close(); handle.Value = null; highlight.Value = -1; openVer.Value = openVer.Peek() + 1; }

        void OpenPopup()
        {
            if (handle.Value is { IsOpen: true } || !IsEnabled) return;
            highlight.Value = searchSet.Value ? searchIdx.Value : sel;   // keyboard cursor on the search result / selection
            Func<NodeHandle> anchorOf = () => anchor.Value;
            Func<Element> body = () => Embed.Comp(() => new ComboBoxList
            {
                Owner = this, Selected = SelectedIndex, Highlight = highlight, Width = w, OnChoose = Choose,
            });
            // Editable mode keeps focus IN the text field while the list is open (WinUI: arrows preview from the
            // TextBox, ComboBox_Partial.cpp:2840–2886) — no focus trap; the non-editable list takes focus + owns keys.
            // Chrome=Dropdown: the ComboBox dropdown animates with SplitOpen/SplitCloseThemeAnimation (the template's
            // DropDownStates Opened/Closed storyboards, generic.xaml:9047/9056) — clip reveal with no translate/fade
            // on open, 167ms clip collapse + late fade on close — NOT the menus' MenuPopupThemeTransition.
            // ConstrainToRootBounds=false: the ComboBox Popup is WINDOWED (generic.xaml:9248
            // <Popup x:Name="Popup" ShouldConstrainToRootBounds="False">) — a tall dropdown may escape the window.
            if (Editable)
            {
                // Editable: the dropdown attaches BELOW the field, width-matched + corner-joined (the AutoSuggest
                // shape; ComboBoxHelper.UpdateCornerRadius only squares corners for IsEditable()).
                handle.Value = svc.Open(anchorOf, body, FlyoutPlacement.BottomStretch,
                    new PopupOptions(Chrome: PopupChrome.Dropdown) { ConstrainToRootBounds = false });
            }
            else
            {
                // Non-editable: the popup lays OVER the field with the SELECTED item aligned on top of it (the WinUI
                // carousel placement — TemplateSettings.DropDownOffset shifts the popup up by the selected item's
                // offset; PopupBorder Margin 0,-0.5,0,-1 overlaps the field). The anchor-rect thunk re-reads the
                // field rect each placement pass and pre-shifts it up by the selected row's offset inside the popup
                // (content inset 4 + row pitch × index + row top margin 2). Long lists clamp into the work area, so
                // the alignment degrades gracefully at the edges (WinUI clamps identically).
                int selNow = SelectedIndex.Peek();
                // SplitOpen/SplitClose seam: SeamOffsetY = selected row centre Y minus popup centre Y.
                float? seamY = null;
                if (selNow >= 0 && selNow < Items.Count)
                {
                    float rowsH = 0f;
                    for (int i = 0; i < Items.Count; i++) rowsH += ComboBox.ItemRowPitch(this, i);
                    float popupH = MathF.Min(rowsH + 4f, MaxDropDownHeight) + 4f;
                    float rowCenter = SelectedRowOffset(selNow);
                    seamY = Math.Clamp(rowCenter, 0f, popupH) - popupH * 0.5f;
                }
                handle.Value = svc.OpenAt(
                    () =>
                    {
                        var scene = Context.Scene;
                        var node = anchor.Value;
                        RectF f = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                        int s = Math.Max(0, SelectedIndex.Peek());
                        float off = 0f;
                        for (int i = 0; i < s; i++) off += ComboBox.ItemRowPitch(this, i);
                        off += DropdownContentInset + 2f;
                        return new RectF(f.X, f.Y - off, f.W, f.H);
                    },
                    body, FlyoutPlacement.OverlapStretch,
                    new PopupOptions(FocusTrap: true, Chrome: PopupChrome.Dropdown) { ConstrainToRootBounds = false, SeamOffsetY = seamY },
                    owner: anchorOf);
            }
            handle.Value.ClosedAction = () =>
            {
                handle.Value = null;
                highlight.Value = -1;
                openVer.Value = openVer.Peek() + 1;
                // WinUI commits the editable search whenever the popup closes non-cancelled (OnIsDropDownOpenChanged →
                // CommitRevertEditableSearch(m_isClosingDueToCancel), ComboBox_Partial.cpp:1757) — covers light-dismiss.
                if (Editable && !cancelling.Value) CommitSearch();
            };
            openVer.Value = openVer.Peek() + 1;
        }

        void Toggle() { if (handle.Value is { IsOpen: true }) Close(); else OpenPopup(); }

        // The editable DropDownOverlay click. The overlay border is never a focus target (the dispatcher's pointer
        // activation finds no focusable in its chain, so focus stays unchanged — the WinUI IsTabStop=False shape);
        // opening from an unfocused combo puts focus in the TextBox part first (WinUI's editable open paths focus
        // EditableText), a no-op when the field already holds it.
        void OverlayToggle()
        {
            if (_edit is { } ed && !ed.RootNode.IsNull) hooks?.RestoreFocus?.Invoke(ed.RootNode);
            Toggle();
        }

        float SelectedRowOffset(int index)
        {
            float y = DropdownContentInset + 2f;
            for (int i = 0; i < index; i++)
                y += ComboBox.ItemRowPitch(this, i);
            return y + ComboBox.ItemRowPitch(this, index) * 0.5f;
        }

        // ── Editable mode: search-as-you-type / arrow preview / commit-or-TextSubmitted / Escape revert ──────────
        // (ComboBox_Partial.cpp — ProcessSearch :3934–4009, SearchItemSourceIndex :4011–4132,
        //  CommitRevertEditableSearch :2441–2599, arrows :2838–2886 + :2925–2949, keys :3007–3063.)

        int FindMatch(string t, bool exact)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                // Items compare with leading spaces trimmed, case-insensitively (cpp:4096 + StartsWithIgnoreLinguisticSemantics :4134).
                string item = Items[i].TrimStart();
                if (exact ? string.Equals(item, t, StringComparison.OrdinalIgnoreCase)
                          : item.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        void ResetSearch() { searchSet.Value = false; searchIdx.Value = -1; }   // cpp:2601–2605

        // Search on a user edit. A DELETION searches exact-match only so auto-complete cannot fight backspacing
        // (cpp:4098–4106); an insertion that prefix-matches AUTO-COMPLETES the item into the field with the completed
        // suffix selected for quick replacement (cpp:4112–4116 + UpdateEditableTextBox :1543–1551). The match is NOT
        // committed while typing (SelectionChangedTrigger default = Committed); an open dropdown shows it as the
        // cursor row (OverrideSelectedIndexForVisualStates, cpp:3976–3980).
        void ProcessSearch(bool deletion)
        {
            string t = text.Peek();
            int found = t.Length == 0 ? -1 : FindMatch(t, exact: deletion);
            searchSet.Value = true;
            searchIdx.Value = found;
            if (found >= 0 && !deletion && _edit is { } e)
            {
                string item = Items[found];
                if (!string.Equals(item, t, StringComparison.Ordinal))
                {
                    int selStart = e.Core.Selection.Start;   // caret before the write (cpp:1544–1546)
                    e.ReplaceText(item, selStart, item.Length - selStart);
                }
            }
            if (handle.Value is { IsOpen: true }) highlight.Value = found;
        }

        // Commit (Enter / Tab / focus departure / popup close): a search hit selects it (cpp:2482–2486); otherwise a
        // non-blank custom text raises TextSubmitted (cpp:2507–2513) — unhandled falls back to a case-insensitive
        // EXACT match (cpp:2516–2519: select on hit, cpp:2540–2543: else the text stays a custom value, selection −1).
        void CommitSearch()
        {
            string t = text.Peek();
            if (!searchSet.Value)
            {
                int cur = SelectedIndex.Peek();
                if (t.Length == 0 || (cur >= 0 && cur < Items.Count && string.Equals(Items[cur], t, StringComparison.Ordinal)))
                    return;                                   // text already reflects the selection — nothing to commit
                searchSet.Value = true;
                searchIdx.Value = FindMatch(t, exact: false); // ensure Text matches an index (cpp:2473–2479)
            }
            int found = searchIdx.Value;
            ResetSearch();
            if (found > -1) Commit(found);                    // select the search hit (cpp:2482–2486)
            else if (t.Trim().Length > 0)                     // IsSearchStringValid (cpp:2494 + :4183–4194)
            {
                bool handled = OnTextSubmitted?.Invoke(t) ?? false;
                if (!handled)                                 // handled: the app accepted the custom value (cpp:2515–2516)
                {
                    int exact = FindMatch(t, exact: true);
                    if (exact >= 0) Commit(exact);
                    else SelectedIndex.Value = -1;            // custom value active: text kept, no item selected
                }
            }
            // A completed commit clears the Escape-restore state (cpp:2594–2596) — a later Escape in the same focus
            // session keeps the just-committed selection instead of reverting past it.
            restoreIdx.Value = SelectedIndex.Peek();
        }

        // Arrows while the FIELD has focus (popup open or closed, cpp:2838–2886): move the search index ±1 CLAMPED to
        // [0, count) (cpp:2877–2882 + :2930), preview the item into the field SELECT-ALL (cpp:2939–2949) — no commit.
        void HandleEditableKeys(KeyEventArgs e)
        {
            if (Items.Count == 0 || (e.KeyCode != Keys.Down && e.KeyCode != Keys.Up)) return;
            int cur = searchSet.Value ? searchIdx.Value : SelectedIndex.Peek();   // cpp:2843–2851
            int next = Math.Clamp(cur + (e.KeyCode == Keys.Down ? 1 : -1), 0, Items.Count - 1);
            searchSet.Value = true;
            searchIdx.Value = next;
            _edit?.ReplaceText(Items[next], 0, Items[next].Length);
            if (handle.Value is { IsOpen: true }) highlight.Value = next;
            e.Handled = true;
        }

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
                // Closed-field stepping CLAMPS symmetrically: with no selection BOTH Up and Down land on item 0
                // (audit keyboard-symmetry fix — WinUI does not wrap the closed field).
                case Keys.Down: Commit(sel < 0 ? 0 : Math.Min(sel + 1, Items.Count - 1)); e.Handled = true; break;
                case Keys.Up: Commit(sel < 0 ? 0 : Math.Max(sel - 1, 0)); e.Handled = true; break;
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

        // KEYBOARD focus on the closed field → the WinUI Focused visual state swaps the HighlightBackground border to
        // ComboBoxBackgroundBorderBrushFocused = FocusStrokeColorOuterBrush (ComboBox_themeresources.xaml:38;
        // generic.xaml:8995-8996 Focused storyboard). Pointer focus shows nothing (the PointerFocused state is empty).
        // The engine's keyboard-only focus-ring adorner still draws OUTSIDE — this is the template's own border swap.
        var focusedSig = UseSignal(false);
        bool focusedRing = focusedSig.Value;
        void OnFieldFocus(bool got)
        {
            var scene = Context.Scene;
            var node = anchor.Value;
            bool keyboard = got && scene is not null && !node.IsNull && scene.IsLive(node)
                            && (scene.Flags(node) & NodeFlags.FocusVisual) != 0;
            if (focusedSig.Peek() != keyboard) focusedSig.Value = keyboard;
        }

        // Validation error (InputValidationErrorStates InlineErrors, generic.xaml:9118-9127): the field border swaps
        // to SystemControlErrorTextForegroundBrush (= SystemErrorTextColor #FFF000 dark / #C50500 light,
        // generic.xaml:227/:4152) and the message renders below the field, replacing the Description row.
        // form-validation.md: the error shows EITHER a manually-set ErrorText OR the field's gated error (resolved loc
        // key). Reading Field.Error.Value subscribes this render so a validity flip (a rare event) re-renders the field.
        string errorMsg = ErrorText.Length > 0 ? ErrorText
            : Field is { } vf && !vf.Error.Value.IsValid ? Msg.Resolve(vf.Error.Value.First)
            : "";
        bool hasError = errorMsg.Length > 0;
        ColorF errorColor = Tok.Theme == ThemeKind.Light
            ? ColorF.FromRgba(0xC5, 0x05, 0x00)    // SystemErrorTextColor (Light), generic.xaml:4152
            : ColorF.FromRgba(0xFF, 0xF0, 0x00);   // SystemErrorTextColor (Default/dark), generic.xaml:227

        GradientSpec restBorder =
            hasError ? GradientSpec.Solid(errorColor)
            : focusedRing ? GradientSpec.Solid(Tok.FocusOuter)
            : IsEnabled ? Tok.ControlElevationBorder
            : GradientSpec.Solid(Tok.StrokeControlDefault);

        Action<NodeHandle> anchorCapture = h => { anchor.Value = h; if (Field is { } fn) fn.Node.Value = h; };

        // WinUI DropDownGlyph: AnimatedChevronDownSmall (12×12), foreground TextFillColorSecondary →
        // disabled TextFillColorDisabled. The AnimatedIcon's Pressed segment nudges the chevron — until the real
        // AnimatedIcon state machine lands (Wave 4), the eased PressScale on the glyph box is the engine stand-in.
        var chevron = new BoxEl
        {
            Width = ChevronGlyphSize, Height = ChevronGlyphSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.Center,
            Margin = new Edges4(0, 0, ChevronRightInset, 0),
            HitTestVisible = false,
            PressScale = 0.85f,                                  // AnimatedChevronDownSmall pressed-nudge stand-in
            PressDurationMs = Motion.ControlFaster,              // 83ms, ControlFastOutSlowInKeySpline
            PressEasing = Easing.FluentPopOpen,
            Children =
            [
                new TextEl(Icons.ChevronDown)
                {
                    Size = ChevronGlyphSize, FontFamily = Theme.IconFont,
                    Color = IsEnabled ? Tok.TextSecondary : Tok.TextDisabled,
                    DisabledColor = Tok.TextDisabled,
                },
            ],
        };
        // Parts: restyle the glyph cell (press-nudge, margin, colors via the inner TextEl…); it stays decoration.
        if (Parts is { } cp) chevron = cp.Apply(PartChevron, chevron) with { HitTestVisible = false };

        // Header (HeaderContentPresenter, generic.xaml:9155-9166) / Description (DescriptionPresenter :9233-9239) /
        // error message rows around the field. Returns the bare field when none are set (the common case).
        Element WithChrome(Element field)
        {
            if (Header.Length == 0 && Description.Length == 0 && !hasError) return field;
            var rows = new List<Element>(3);
            if (Header.Length > 0)
                rows.Add(new TextEl(Header)
                {
                    Size = 14f,                                   // ComboBoxHeaderThemeFontWeight = Normal
                    Color = IsEnabled ? Tok.TextPrimary : Tok.TextDisabled,
                    Margin = new Edges4(0, 0, 0, 4),              // ComboBoxTopHeaderMargin 0,0,0,4 (generic.xaml:5911)
                    Wrap = TextWrap.Wrap, MaxWidth = w,
                });
            rows.Add(field);
            if (hasError)
                rows.Add(new TextEl(errorMsg) { Size = 12f, Color = errorColor, Wrap = TextWrap.Wrap, MaxWidth = w, Margin = new Edges4(0, 4, 0, 0) });
            else if (Description.Length > 0)
                rows.Add(new TextEl(Description) { Size = 12f, Color = Tok.TextControlDescriptionForeground, Wrap = TextWrap.Wrap, MaxWidth = w, Margin = new Edges4(0, 4, 0, 0) });
            return new BoxEl { Direction = 1, AlignItems = FlexAlign.Start, Children = rows.ToArray() };
        }

        if (Editable)
        {
            // DropDownGlyph (:582–587): 12×12 E70D, Margin 0,0,14,0, IsHitTestVisible=False, painted ABOVE the
            // overlay (tree order = paint order). Foreground stays TextFillColorSecondary in every editable state
            // (ComboBoxDropDownGlyphForeground :58 = ComboBoxEditableDropDownGlyphForeground :105/:315); disabled
            // = TextFillColorDisabled (:59). No press nudge: the EditableModeStates only recolor the overlay/glyph.
            var editGlyph = new BoxEl
            {
                Width = ChevronGlyphSize, Height = ChevronGlyphSize,
                OffsetX = w - ChevronGlyphSize - ChevronRightInset,
                OffsetY = (MinHeight - ChevronGlyphSize) * 0.5f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Children =
                [
                    new TextEl(Icons.ChevronDown)
                    {
                        Size = ChevronGlyphSize, FontFamily = Theme.IconFont,
                        Color = IsEnabled ? Tok.TextSecondary : Tok.TextDisabled,
                        DisabledColor = Tok.TextDisabled,
                    },
                ],
            };
            // Parts: restyle the glyph cell; it stays decoration (the DropDownOverlay below owns the click).
            if (Parts is { } gp) editGlyph = gp.Apply(PartChevron, editGlyph) with { HitTestVisible = false };

            // ── ONE box owns ALL the chrome (the WinUI template: a single Background border spans BOTH grid columns,
            // ComboBox_themeresources.xaml:571 Grid.ColumnSpan="2"; the TextBox part sits INSIDE it with
            // BorderBrush="Transparent" + Style=ComboBoxTextBoxStyle, :580). The chromeless part paints only the
            // TextControl hover/focused fills over this box's rest fill (ComboBoxTextBoxStyle :786–803 — the WinUI
            // BorderElement-over-Background layering); the focused affordance = that input-active fill + the 2px
            // accent bottom bar on THIS box (TextControlBorderThemeThicknessFocused 1,1,1,2,
            // Common_themeresources.xaml:11, with TextControlElevationBorderFocusedBrush's accent bottom stop,
            // TextBox_themeresources.xaml:57–65 — the established 2px-bottom-bar equivalence).
            bool editFocused = editFocusedSig.Value;
            var editChildren = new List<Element>(4)
            {
                // The TextBox part: FULL width (:580 ColumnSpan=2); ComboBoxEditableTextPadding 11,5,38,6 (:342)
                // keeps the text clear of the chevron column.
                Embed.Comp(() =>
                {
                    var e = new EditableText
                    {
                        Text = text, Width = w, Height = MinHeight,
                        Chromeless = true,
                        LanePadding = new Edges4(11, 5, ChevronColumn, 6),
                        Placeholder = Placeholder, IsEnabled = IsEnabled,
                        // Enter commits the search (MainKeyDown Enter → CommitRevertEditableSearch(false),
                        // ComboBox_Partial.cpp:3046–3050) and closes the dropdown.
                        OnCommit = _ => { CommitSearch(); if (handle.Value is { IsOpen: true }) Close(); },
                        // Escape reverts: EditableText already restored the focus-time text; restore the
                        // edit-begin selection and close WITHOUT committing (cpp:3009–3016 + :2448–2468).
                        OnCancel = () =>
                        {
                            cancelling.Value = true;   // cleared by the blur that follows the Escape
                            if (restoreIdx.Value != SelectedIndex.Peek()) SelectedIndex.Value = restoreIdx.Value;
                            ResetSearch();
                            if (handle.Value is { IsOpen: true }) Close();
                        },
                        OnFocusChanged = f =>
                        {
                            editFocusedSig.Value = f;   // the OUTER box re-renders its focused chrome (accent bar)
                            if (f)
                            {
                                restoreIdx.Value = SelectedIndex.Peek();   // m_indexToRestoreOnCancel (cpp:2448–2455)
                                ResetSearch();
                            }
                            else
                            {
                                bool wasCancel = cancelling.Value;
                                cancelling.Value = false;
                                // Commit when focus leaves the control (OnLostFocus, cpp:2386–2391). Focus moving
                                // INTO the open popup (a row click in flight) is not a departure — the click /
                                // popup-close path commits instead.
                                if (!wasCancel && handle.Value is not { IsOpen: true }) CommitSearch();
                            }
                        },
                    };
                    // Search-as-you-type on USER edits only (cpp OnCharacterReceived :3820–3841); a deletion
                    // (Backspace/Delete/cut) restricts the search to exact matches so auto-complete never fights
                    // backspace (cpp:4098–4106 keys off VK_BACK).
                    e.OnTextChanged = _ =>
                    {
                        if (e.LastChangeReason == TextChangeReason.UserInput) ProcessSearch(e.LastEditWasDeletion);
                    };
                    _edit = e;
                    return e;
                }),
                // DropDownOverlay (:581): Width=30, Margin=4, CornerRadius=4
                // (ComboBoxDropDownButtonBackgroundCornerRadius :344), transparent until hover/press
                // (ComboBoxDropDownBackgroundPointerOver=SubtleFillColorSecondary :311,
                // ComboBoxDropDownBackgroundPointerPressed=SubtleFillColorTertiary :312), never a focus target.
                new BoxEl
                {
                    Width = OverlayButtonWidth, Height = MinHeight - 2f * OverlayButtonInset,
                    OffsetX = w - OverlayButtonWidth - OverlayButtonInset, OffsetY = OverlayButtonInset,
                    Corners = CornerRadius4.All(4f),
                    Fill = ColorF.Transparent,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, IsEnabled = IsEnabled,
                    TabStop = false,
                    OnClick = OverlayToggle,
                },
                // The DropDownGlyph cell (editGlyph above — built before the list so PartChevron routes it).
                editGlyph,
            };
            // Focused: the 2px accent bottom bar on the OUTER box (TextControlBorderThemeThicknessFocused 1,1,1,2 +
            // the TextControlElevationBorderFocusedBrush accent bottom stop — the 2px-bottom-bar equivalence; the
            // input-active fill is painted by the chromeless part). Appended LAST so it paints over the bottom edge.
            if (editFocused && IsEnabled)
                editChildren.Add(new BoxEl { Width = w, Height = 2f, OffsetY = MinHeight - 2f, Fill = Tok.AccentDefault });

            var editKids = editChildren.ToArray();
            var editField = new BoxEl
            {
                ZStack = true, Width = w, Height = MinHeight,
                Corners = editableCorners, BorderWidth = 1f,
                // WinUI ComboBoxBorderBrush = ControlElevationBorderBrush (gradient); the InlineErrors state swaps it
                // to the error stroke; disabled flattens to ControlStrokeColorDefault.
                BorderBrush = hasError ? GradientSpec.Solid(errorColor)
                    : IsEnabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
                Fill = IsEnabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
                ClipToBounds = true,
                IsEnabled = IsEnabled,
                Role = AutomationRole.ComboBox,
                OnRealized = anchorCapture,
                OnKeyDown = IsEnabled ? HandleEditableKeys : null,   // Up/Down bubble out of the single-line field
                Children = editKids,
            };
            // Parts: restyle the chrome box; the anchor capture, arrow preview and part structure always win.
            if (Parts is { } ep)
            {
                var m = ep.Apply(PartField, editField);
                editField = m with
                {
                    OnRealized = TemplateParts.Chain(anchorCapture, m.OnRealized),
                    OnKeyDown = IsEnabled ? (Action<KeyEventArgs>)HandleEditableKeys : null,
                    Role = AutomationRole.ComboBox,
                    Children = editKids,
                };
            }
            return WithChrome(editField);
        }

        string label = sel >= 0 && sel < Items.Count ? Items[sel] : Placeholder;
        bool isPlaceholder = sel < 0 || sel >= Items.Count;
        Element[] fieldKids =
        [
            new TextEl(label)
            {
                Size = 14f, Grow = 1f,
                // ComboBoxForeground=Primary / placeholder=Secondary; pressed ramps to Secondary/Tertiary; disabled=Disabled.
                // Focused keeps Primary / placeholder Secondary (ComboBoxForegroundFocused /
                // ComboBoxPlaceHolderForegroundFocused, ComboBox_themeresources.xaml:44/:52) — no swap needed.
                Color = !IsEnabled ? Tok.TextDisabled : (isPlaceholder ? Tok.TextSecondary : Tok.TextPrimary),
                PressedColor = isPlaceholder ? Tok.TextTertiary : Tok.TextSecondary,
                DisabledColor = Tok.TextDisabled,
            },
            chevron,
        ];
        var field = new BoxEl
        {
            Direction = 0, Width = w, MinHeight = MinHeight, AlignItems = FlexAlign.Center, Padding = new Edges4(12, 5, 0, 7),
            Corners = Radii.ControlAll, BorderWidth = 1f,   // non-editable: popup overlaps the field; field keeps full ControlCornerRadius
            // Rest border: error > keyboard-focused (FocusStrokeColorOuter) > ControlElevationBorder > disabled flat.
            BorderBrush = restBorder,
            // CommonStates: rest=Default, PointerOver=Secondary, Pressed=Tertiary, Disabled=Disabled.
            Fill = IsEnabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
            HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            // Pressed flattens the border to ControlStrokeColorDefault (ComboBoxBorderBrushPressed); the focused ring
            // and the error border hold through a press (the FocusedPressed/error states keep their border).
            PressedBorderBrush = (focusedRing || hasError) ? restBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
            IsEnabled = IsEnabled,
            Focusable = IsEnabled,
            Role = AutomationRole.ComboBox,
            OnRealized = anchorCapture,
            OnClick = Toggle,
            OnFocusChanged = IsEnabled ? OnFieldFocus : null,
            OnKeyDown = IsEnabled ? (Action<KeyEventArgs>)HandleKey : null,
            OnCharInput = IsEnabled ? (Action<CharEventArgs>)HandleChar : null,
            Children = fieldKids,
        };
        // Parts: restyle anything (the state fills/borders above are pre-modifier); the open/nav mechanics always win.
        if (Parts is { } fp)
        {
            var m = fp.Apply(PartField, field);
            field = m with
            {
                OnRealized = TemplateParts.Chain(anchorCapture, m.OnRealized),
                OnClick = Toggle,
                OnFocusChanged = IsEnabled ? (Action<bool>)OnFieldFocus : null,
                OnKeyDown = IsEnabled ? (Action<KeyEventArgs>)HandleKey : null,
                OnCharInput = IsEnabled ? (Action<CharEventArgs>)HandleChar : null,
                Focusable = IsEnabled,
                Role = AutomationRole.ComboBox,
                Children = fieldKids,
            };
        }
        return WithChrome(field);
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
            int clamped = Math.Clamp(next, 0, items.Count - 1);
            if (!Owner.IsItemEnabled(clamped))
            {
                int cur = Highlight.Peek();
                if (cur < clamped)
                {
                    for (int i = clamped + 1; i < items.Count; i++)
                        if (Owner.IsItemEnabled(i)) { Highlight.Value = i; return; }
                    for (int i = clamped - 1; i >= 0; i--)
                        if (Owner.IsItemEnabled(i)) { Highlight.Value = i; return; }
                }
                else
                {
                    for (int i = clamped - 1; i >= 0; i--)
                        if (Owner.IsItemEnabled(i)) { Highlight.Value = i; return; }
                    for (int i = clamped + 1; i < items.Count; i++)
                        if (Owner.IsItemEnabled(i)) { Highlight.Value = i; return; }
                }
                return;
            }
            Highlight.Value = clamped;
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
                    if (cur >= 0 && Owner.IsItemEnabled(cur)) OnChoose(cur);
                    e.Handled = true; break;
            }
        }

        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            int idx = i;
            bool selected = idx == sel;
            bool cursor = idx == hi;
            bool enabled = Owner.IsItemEnabled(idx);
            string? desc = Owner.ItemDescriptions is { } ds && idx < ds.Count ? ds[idx] : null;

            Element labelCol;
            if (desc is { Length: > 0 })
            {
                labelCol = new BoxEl
                {
                    Direction = 1,
                    Gap = 2f,
                    Grow = 1f,
                    MinWidth = 0f,
                    Children =
                    [
                        new TextEl(items[idx])
                        {
                            Size = 14f,
                            Color = enabled ? Tok.TextPrimary : Tok.TextDisabled,
                            PressedColor = Tok.TextSecondary,
                            Grow = 1f,
                        },
                        new TextEl(desc)
                        {
                            Size = 12f,
                            Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                            PressedColor = Tok.TextTertiary,
                            Wrap = TextWrap.Wrap,
                        },
                    ],
                };
            }
            else
            {
                labelCol = new TextEl(items[idx])
                {
                    Size = 14f,
                    Color = enabled ? Tok.TextPrimary : Tok.TextDisabled,
                    PressedColor = Tok.TextSecondary,
                    Grow = 1f,
                };
            }

            var content = new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Grow = 1f,
                Padding = Owner.TouchInputMode ? ComboBox.ItemTouchPadding : ComboBox.ItemPadding,
                Children = [labelCol],
            };
            Element[] rowChildren = selected
                ? [Owner.Parts.Apply(ComboBox.PartItemPill,
                       new BoxEl { Width = 3f, Height = 16f, Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault, AlignSelf = FlexAlign.Center }), content]
                : [content];

            // Item state matrix (ComboBox_themeresources): unselected rest=Transparent, hover=SubtleSecondary,
            // press=SubtleTertiary; selected rest=SubtleSecondary, hover=SubtleTertiary, press=SubtleSecondary.
            // The keyboard cursor (Highlight) reads as the hover/selected fill so arrow nav is visible without a pointer.
            ColorF rest = !enabled ? ColorF.Transparent
                : selected || cursor ? Tok.FillSubtleSecondary : ColorF.Transparent;
            Action choose = enabled ? () => OnChoose(idx) : static () => { };
            var row = new BoxEl
            {
                ZStack = true,
                Width = MathF.Max(0f, Width - 10f),
                AlignItems = FlexAlign.Stretch,
                Margin = new Edges4(5, 2, 5, 2),
                Corners = CornerRadius4.All(3f),
                Role = AutomationRole.MenuItem,
                Fill = rest,
                HoverFill = !enabled ? ColorF.Transparent
                    : selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                PressedFill = !enabled ? ColorF.Transparent
                    : selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
                IsEnabled = enabled,
                OnClick = choose,
                Children = rowChildren,
            };
            // Rows are popup-built per render (NOT recycled — no virtualization hazard), so per-row Parts routing is
            // safe; the state fills above are pre-modifier, the commit mechanics always win.
            rows[i] = Owner.Parts is { } rp
                ? rp.Apply(ComboBox.PartItemRow, row) with { OnClick = choose, Role = AutomationRole.MenuItem, Children = rowChildren }
                : row;
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
