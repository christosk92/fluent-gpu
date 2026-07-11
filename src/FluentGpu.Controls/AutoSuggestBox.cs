using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Visual chrome variants for <see cref="AutoSuggestBox"/>.</summary>
public enum AutoSuggestBoxChrome
{
    /// <summary>The WinUI control-default field surface.</summary>
    Standard,
    /// <summary>A Files-style elevated pill: circle-elevation stroke at rest and a two-DIP accent focus ring.</summary>
    ElevatedPill,
}

/// <summary>
/// State supplied to a custom AutoSuggestBox popup presenter. The box continues to own the anchor, popup lifetime,
/// text editor, keyboard dismissal and query submission; the presenter owns only the popup's content.
/// </summary>
public sealed record AutoSuggestBoxPresenterContext(
    Signal<string> Text,
    IReadSignal<float> Width,
    Action<string> Submit,
    Action Close);

/// <summary>
/// Optional rich-suggestion presenter. This is deliberately narrower than templating the whole control: consumers can
/// render artwork and typed result models without replacing the FluentGpu AutoSuggestBox field or overlay mechanics.
/// </summary>
public sealed record AutoSuggestBoxPresenter(
    Func<AutoSuggestBoxPresenterContext, Element> Build,
    Action<int>? MoveSelection = null,
    Func<bool>? SubmitSelection = null,
    Action? ResetSelection = null);

/// <summary>
/// A WinUI AutoSuggestBox: an <see cref="EditableText"/> field whose filtered suggestions are hosted in a light-dismiss
/// <b>popup</b> (an anchored overlay), not inline — so the list floats over content instead of reflowing the page and
/// auto-flips above the box when there is no room below (same plumbing as <see cref="ComboBox"/>).
/// <para>
/// The list is opened iff the query is non-empty and there is at least one match (WinUI <c>UpdateSuggestionListVisibility</c>:
/// open iff <c>maxHeight &gt; 0 ∧ count &gt; 0</c>), driven from a <c>UseEffect</c> keyed on the query so the open/close runs
/// after the render commits (no side effects in render). The popup body is wrapped in its own <see cref="Embed.Comp"/>
/// <see cref="SuggestionsList"/> Component that subscribes to the shared query/highlight <see cref="Signal{T}"/>s, so it
/// RE-FILTERS on every keystroke — an overlay content thunk is otherwise a one-shot snapshot (only re-evaluated when the
/// OverlayHost version bumps on open/close).
/// </para>
/// <para>
/// <b>TextChanged + reason (AutoSuggestBox_Partial.cpp OnTextBoxTextChanged :451–503).</b> EVERY text change — user
/// edit, programmatic restore, suggestion preview — Stop()+Start()s the 150ms timer (<c>s_textChangedEventTimerDuration</c>
/// = 1500000 ticks) carrying the change's <see cref="TextChangeReason"/> (cpp:464), so the public <see cref="TextChanged"/>
/// fires once typing pauses, with the LAST reason. Only <c>UserInput</c> changes cache <c>m_userTypedText</c>, reset the
/// keyboard cursor and update the list visibility (cpp:477–496). Set <see cref="DebounceMs"/>=0 to fire synchronously.
/// </para>
/// <para>
/// <b>SuggestionChosen + arrow preview (cpp OnSuggestionSelectionChanged :2298–2411).</b> Moving the suggestion-list
/// selection (arrow keys, row click) raises <c>SuggestionChosen</c> for the newly selected item; when
/// <see cref="UpdateTextOnSelect"/> (default true, cpp:2366–2381) the item text is also previewed into the field with
/// reason <see cref="TextChangeReason.SuggestionChosen"/> (cpp:2380). Stepping PAST either end restores the user-typed
/// text with reason <c>ProgrammaticChange</c> and clears the selection (cpp:1101/1109). Enter submits the query
/// (QueryText = the field text, ChosenSuggestion = the highlighted item) WITHOUT re-raising SuggestionChosen
/// (cpp:1149–1160 + SubmitQuery :857–878). A row click runs SelectionChanged → SuggestionChosen → QuerySubmitted
/// sequentially (cpp OnListViewItemClick :2413–2437). Escape restores the typed text and closes (cpp:634–643).
/// </para>
/// <para>
/// <b>Corner-joining (WinUI <c>AutoSuggestBoxHelper::UpdateCornerRadius</c> / <c>KeepInteriorCornersSquare=true</c>).</b> While
/// the list is open the field squares the corners that abut the popup so the field+list read as one piece. WinUI squares the
/// field BOTTOM (open-down) or TOP (open-up) and the matching popup edge; we square the field bottom corners (open-down — the
/// dominant case) here. The popup-surface corner squaring is host-owned: <see cref="FlyoutSurface"/> already receives the
/// <see cref="CornerJoin"/> from <see cref="FlyoutPositioner"/> on its <see cref="OverlayEntry"/> but currently renders
/// <c>Radii.OverlayAll</c> unconditionally — see the deferred note in the task report (cannot be fixed from this file).
/// </para>
/// </summary>
public sealed class AutoSuggestBox : Component
{
    // ── WinUI dims ──
    // generic.xaml:  AutoSuggestListMaxHeight=374; AutoSuggestListBorderThemeThickness=1; AutoSuggestListMargin=0,2,0,2
    //                (popup Border padding, supplied by FlyoutSurface); AutoSuggestListPadding=-1,0,-1,0 (inner ListView
    //                margin). Suggestion rows are DEFAULT ListViewItems: ContentMargin/Padding 16,0,12,0
    //                (ListViewItem_themeresources.xaml:241), MinHeight=40 (:14), rounded backplate CornerRadius 4 (:58)
    //                inset 4,2 (the WinUI 3 ListViewItemPresenter plate).
    // themeresources: AutoSuggestBoxIconFontSize=12 (:25); AutoSuggestBoxRightButtonMargin=4 (reserved grid column 3,
    //                AutoSuggestBox_themeresources.xaml:230); QueryButton Width=32 Height=28 Margin=2,0,0,0 (:242);
    //                AutoSuggestBoxInnerButtonMargin=1,3 (:24, the ContentPresenter plate inset :101);
    //                AutoSuggestBox CornerRadius=ControlCornerRadius (4).
    // AutoSuggestBox_Partial.cpp: s_textChangedEventTimerDuration = 1500000 ticks = 150ms (TextChanged debounce).
    public const float ItemMinHeight = 40f;
    public const float IconFontSize = 12f;
    public const float MaxPopupHeight = 374f;
    public const float RightButtonMargin = 4f;   // AutoSuggestBoxRightButtonMargin
    public const float QueryButtonWidth = 32f;
    public const float QueryButtonHeight = 28f;
    public const float QueryButtonLeftMargin = 2f;
    public const float TextChangedDebounceMs = 150f;

    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The field surface root (the chrome + popup anchor). Owned: OnKeyDown (the Up/Down suggestion nav),
    /// Role, Children, OnRealized (the anchor capture, chained). The open-state corner squaring (KeepInteriorCornersSquare)
    /// is recomputed per render BEFORE the modifier — override Corners to opt out.</summary>
    public const string PartRoot = "Root";
    /// <summary>The query-button plate (WinUI QueryButton — the INNER presenter that carries the TextControlButton
    /// chrome; the fixed 32×28 outer slot is not a part). Owned: OnClick (submit), Role.</summary>
    public const string PartQueryButton = "QueryButton";
    /// <summary>The query glyph (a TextEl — use <c>Parts.Set&lt;TextEl&gt;</c>; the glyph STRING is the
    /// <see cref="QueryIcon"/> prop). Owned: none.</summary>
    public const string PartQueryIcon = "QueryIcon";
    /// <summary>The popup scroll host (WinUI SuggestionsList; a ScrollEl — use <c>Parts.Set&lt;ScrollEl&gt;</c>).
    /// Owned: Content (the row column), ContentSized (the size-to-rows mechanic). Modifiers run inside the popup
    /// body's render, so they re-evaluate per keystroke.</summary>
    public const string PartSuggestionsList = "SuggestionsList";
    /// <summary>EVERY suggestion row plate (a repeated part — the popup list is built per render, NOT
    /// virtualized/recycled). Owned: OnClick (choose + submit), Role. The keyboard-cursor/selected Fill is recomputed
    /// per render BEFORE the modifier.</summary>
    public const string PartSuggestionItem = "SuggestionItem";
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract. The inner field chrome is the composed <see cref="EditableText"/>'s
    /// own (not forwarded — its part names would collide with this control's).</summary>
    public TemplateParts? Parts;

    public IReadOnlyList<string> Suggestions = [];
    /// <summary>LIVE suggestions (overrides <see cref="Suggestions"/> when set). A Component freezes its ctor args at
    /// mount, so a box whose suggestions change as the user types (an async/online provider) must feed them through a
    /// signal — reading it in the popup body re-filters the dropdown when fresh results arrive.</summary>
    public IReadSignal<IReadOnlyList<string>>? SuggestionsSignal;
    /// <summary>The effective suggestion set — the live signal's value if present, else the static list. Reading it in a
    /// render subscribes that component to suggestion updates.</summary>
    internal IReadOnlyList<string> LiveSuggestions => SuggestionsSignal is { } s ? s.Value : Suggestions;
    /// <summary>True while an async suggestion fetch is in flight: the popup shows an indeterminate bar at the top and
    /// KEEPS the prior results (instead of flashing "No results found") until the fresh set lands.</summary>
    public IReadSignal<bool>? LoadingSignal;
    public string Placeholder = "Search";
    public float Width = 280f;
    /// <summary>Flex-fill factor. When &gt; 0 the field fills its parent's WIDTH on its own — no wrapper, no external
    /// <see cref="WidthSignal"/>: the root drops its explicit <see cref="Width"/> (so a COLUMN parent cross-stretches it
    /// and a ROW parent grows it), self-measures via <c>OnBoundsChanged</c>, and clamps its main-axis to
    /// <see cref="FieldMinHeight"/> so a column can't grow it vertically. Pair with <see cref="MaxFillWidth"/> to cap it
    /// (the omnibar uses <c>grow:1, maxFillWidth:720</c>).</summary>
    public float Grow;
    /// <summary>In fill mode (<see cref="Grow"/> &gt; 0), the maximum width (DIP) the field grows to; 0 = unbounded
    /// (fills the parent). Applied as the root's <c>MaxWidth</c> so the layout clamps the stretch/grow directly.</summary>
    public float MaxFillWidth;
    /// <summary>LIVE available width (DIP) — when set, the field renders at <c>min(Width, WidthSignal.Value)</c>
    /// (<see cref="Width"/> becomes the maximum). Component plain fields freeze at mount, so a search box that must
    /// give way as its host narrows takes the host's measured slot signal here (the titlebar passes
    /// <see cref="TitleBar.ContentAvail"/>); the popup surfaces follow the same effective width.</summary>
    public IReadSignal<float>? WidthSignal;
    // Self-measured width when Grow > 0 and no external WidthSignal: updated via OnBoundsChanged on the root BoxEl.
    readonly Signal<float> _selfW = new(0f);
    public Signal<string>? Text;                       // caller-owned query (two-way), like ComboBox.Text
    public Action<string>? OnTextChanged;              // legacy alias of TextChanged (no reason argument)
    /// <summary>WinUI <c>TextChanged</c> with <c>AutoSuggestBoxTextChangedEventArgs.Reason</c> (debounced 150ms;
    /// raised for EVERY change — UserInput / ProgrammaticChange / SuggestionChosen, cpp:451–469).</summary>
    public Action<string, TextChangeReason>? TextChanged;
    public Action<string>? OnSuggestionChosen;         // WinUI SuggestionChosen (selection moved onto an item)
    public Action<string>? OnQuerySubmitted;           // WinUI QuerySubmitted (Enter / query-icon / row click)
    public string? QueryIcon = Icons.Search;           // null = no query button; default = the search glyph
    public float MaxHeight = MaxPopupHeight;           // AutoSuggestListMaxHeight
    public float DebounceMs = TextChangedDebounceMs;   // 0 = fire TextChanged synchronously (no debounce)
    /// <summary>WinUI <c>UpdateTextOnSelect</c> (default true): arrow-cycling previews the highlighted item's text into
    /// the field (reason SuggestionChosen); false leaves the field text untouched while cycling — SuggestionChosen
    /// still fires per selection change (cpp:2361–2398 gates only the text write, :2367).</summary>
    public bool UpdateTextOnSelect = true;
    public Field<string>? Field;                       // form-validation.md: invalid border + touched-on-blur + message row
    /// <summary>Field surface min-height (WinUI default 32). Raise for a tall "pill" search box (e.g. an Omnibar at 38).</summary>
    public float FieldMinHeight = 32f;
    /// <summary>Field corner radius (0 = ControlCornerRadius). Set ≈ half the height for a full pill.</summary>
    public float FieldRadius;
    /// <summary>Bold the matched query substring in each suggestion row (the rest dims) — the Spotify omnibar look.</summary>
    public bool BoldMatch;
    /// <summary>Optional leading glyph per suggestion row (e.g. <see cref="Icons.Search"/>), like Spotify's dropdown.</summary>
    public string? ItemGlyph;
    /// <summary>Optional rich popup content. When null, the built-in string suggestion list is used.</summary>
    public AutoSuggestBoxPresenter? Presenter;
    /// <summary>The field's visual chrome. Interaction and popup behavior are identical across variants.</summary>
    public AutoSuggestBoxChrome Chrome;

    public static Element Create(
        IReadOnlyList<string> suggestions,
        string placeholder = "Search",
        float width = 280f,
        Signal<string>? text = null,
        Action<string>? onTextChanged = null,
        Action<string>? onSuggestionChosen = null,
        Action<string>? onQuerySubmitted = null,
        string? queryIcon = Icons.Search,
        float maxPopupHeight = MaxPopupHeight,
        float debounceMs = TextChangedDebounceMs,
        Action<string, TextChangeReason>? textChanged = null,
        bool updateTextOnSelect = true,
        TemplateParts? parts = null,
        IReadSignal<float>? widthSignal = null,
        Field<string>? field = null,
        float minHeight = 32f,
        float cornerRadius = 0f,
        float grow = 0f,
        float maxFillWidth = 0f,
        bool boldMatch = false,
        string? itemGlyph = null,
        IReadSignal<IReadOnlyList<string>>? suggestionsSignal = null,
        IReadSignal<bool>? loadingSignal = null,
        AutoSuggestBoxPresenter? presenter = null,
        AutoSuggestBoxChrome chrome = AutoSuggestBoxChrome.Standard)
        => Embed.Comp(() => new AutoSuggestBox
        {
            Suggestions = suggestions, SuggestionsSignal = suggestionsSignal, LoadingSignal = loadingSignal,
            Placeholder = placeholder, Width = width, Grow = grow, MaxFillWidth = maxFillWidth,
            WidthSignal = widthSignal, Text = text,
            OnTextChanged = onTextChanged, OnSuggestionChosen = onSuggestionChosen,
            OnQuerySubmitted = onQuerySubmitted, QueryIcon = queryIcon, MaxHeight = maxPopupHeight, DebounceMs = debounceMs,
            TextChanged = textChanged, UpdateTextOnSelect = updateTextOnSelect, Parts = parts, Field = field,
            FieldMinHeight = minHeight, FieldRadius = cornerRadius, BoldMatch = boldMatch, ItemGlyph = itemGlyph,
            Presenter = presenter, Chrome = chrome,
        });

    // The rendered width — what sizes the inner editor and the popup. Reading it inside a Render subscribes THAT
    // component (the field here, the popup body in SuggestionsList) to live resizes.
    //  • WidthSignal (legacy/explicit): min(Width, signal) — Width is the cap.
    //  • Grow > 0 (fill mode): the SELF-MEASURED width (_selfW from OnBoundsChanged). The MaxFillWidth cap is enforced
    //    by the layout (root MaxWidth), so _selfW already reflects it — no extra clamp here. Before the first bounds
    //    report (_selfW == 0) fall back to the cap, else the fixed Width, for one frame.
    //  • otherwise: the fixed Width.
    internal float EffectiveWidth =>
        WidthSignal is { } ws ? MathF.Min(Width, ws.Value) :
        Grow > 0f            ? (_selfW.Value > 0f ? _selfW.Value : (MaxFillWidth > 0f ? MaxFillWidth : Width)) :
        Width;

    // Re-filter helper shared by the root (open-decision) and the popup body (render). Case-insensitive substring;
    // empty query → no matches (WinUI: list opens iff query non-empty AND count > 0).
    internal static List<string> Filter(IReadOnlyList<string> items, string query)
    {
        var matches = new List<string>();
        if (query.Length == 0) return matches;
        var q = query.ToLowerInvariant();
        foreach (var s in items)
            if (s.ToLowerInvariant().Contains(q)) matches.Add(s);
        return matches;
    }

    private EditableText? _edit;

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallbackText = UseSignal("");
        var query = Text ?? fallbackText;
        var userTyped = UseSignal("");                 // m_userTypedText, restored on arrow-out / Escape
        var highlight = UseSignal(-1);                 // keyboard-cursor index into the live match set
        var open = UseSignal(false);                   // IsSuggestionListOpen — drives the field corner-squaring
        var focused = UseSignal(false);
        var svc = UseContext(Overlay.Service);
        var popupWidth = UseComputed(() => EffectiveWidth);

        // The reason of an ASB-initiated write (arrow preview = SuggestionChosen, restore = ProgrammaticChange) —
        // consumed by the post-commit effect. Null → the change came through EditableText (read ITS LastChangeReason:
        // UserInput for typing, ProgrammaticChange for an external caller write of the query signal).
        var pendingReason = UseRef<TextChangeReason?>(null);

        // Debounce plumbing: a deadline (TickCount64 ms) + the text/reason snapshot to emit; the DebounceTicker fires
        // when due. 'pending' is a SIGNAL (not a ref) so arming it inside the post-commit effect re-renders to MOUNT
        // the ticker (a ref write wouldn't re-render); the deadline/text/reason are refs (read inside the ticker).
        var debounceDeadline = UseRef(0L);
        var debouncePending = UseSignal(false);
        var debounceText = UseRef("");
        var debounceReason = UseRef(TextChangeReason.UserInput);

        // Subscribe to the query so each keystroke re-renders → re-runs the open/close effect below.
        var q = query.Value;

        // The MATCH SET keys off the USER-TYPED text, not the live query: an arrow-preview write must not re-filter
        // the open list out from under the keyboard cursor (WinUI sets m_ignoreTextChanges around the preview writes
        // so the app's ItemsSource filter never re-runs for them — AutoSuggestBox_Partial.cpp:2347–2359 + :475).
        List<string> Live() => Filter(LiveSuggestions, userTyped.Peek());

        void Close()
        {
            handle.Value?.Close();
            handle.Value = null;
            highlight.Value = -1;
            Presenter?.ResetSelection?.Invoke();
            open.Value = false;
        }

        void SubmitPresented(string text)
        {
            SetTextWithReason(text, TextChangeReason.SuggestionChosen);
            OnSuggestionChosen?.Invoke(text);
            OnQuerySubmitted?.Invoke(text);
            Close();
        }

        void OpenPopup()
        {
            open.Value = true;
            if (handle.Value is { IsOpen: true }) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                // The list renders against the USER-TYPED query signal: arrow previews must not re-filter the rows.
                () => Presenter is { } presenter
                    ? presenter.Build(new AutoSuggestBoxPresenterContext(query, popupWidth, SubmitPresented, Close))
                    : Embed.Comp(() => new SuggestionsList
                    {
                        Owner = this, Query = userTyped, Highlight = highlight, OnChoose = ChooseAndSubmit,
                    }),
                FlyoutPlacement.BottomStretch,
                // Chrome=Static: the WinUI SuggestionsPopup is a bare Popup with NO transitions (the AutoSuggestBox
                // template, generic.xaml — no TransitionCollection; AutoSuggestBox_Partial.cpp attaches none): the
                // suggestion list appears/disappears instantly. The surface still carries the SuggestionsContainer
                // chrome (AcrylicBackgroundFillColorDefault + 1px border + OverlayCornerRadius + 0,2 padding,
                // AutoSuggestBox_themeresources.xaml:283 + generic.xaml:119).
                new PopupOptions(Chrome: PopupChrome.Static));
            handle.Value.ClosedAction = () =>
            {
                handle.Value = null;
                highlight.Value = -1;
                Presenter?.ResetSelection?.Invoke();
                open.Value = false;
            };
        }

        // Side effects run AFTER render commits, keyed on the query — mirrors OnTextBoxTextChanged (cpp:451–503):
        // EVERY change Stop()+Start()s the public-TextChanged timer with its reason (cpp:457–469); only UserInput
        // caches m_userTypedText, resets the keyboard cursor and updates the list visibility (cpp:477–496).
        var firstRun = UseRef(true);
        UseEffect(() =>
        {
            bool first = firstRun.Value;
            firstRun.Value = false;
            if (first && q == userTyped.Peek()) return;   // mount with no seeded query → no TextChanged, nothing to do

            TextChangeReason reason = first
                // A mount-SEEDED query behaves like a fresh user edit (cache + open the list) — the established
                // engine contract; EditableText's first signal→doc sync is reason-silent.
                ? TextChangeReason.UserInput
                : pendingReason.Value
                  ?? _edit?.LastChangeReason
                  ?? TextChangeReason.UserInput;
            pendingReason.Value = null;   // m_textChangeReason resets after each change (cpp:500)

            if (DebounceMs <= 0f) RaiseTextChanged(q, reason);
            else
            {
                debounceText.Value = q;
                debounceReason.Value = reason;
                debounceDeadline.Value = Environment.TickCount64 + (long)DebounceMs;
                debouncePending.Value = true;
            }

            if (reason == TextChangeReason.UserInput)
            {
                Presenter?.ResetSelection?.Invoke();
                userTyped.Value = q;                    // m_userTypedText = strQueryText (only on _UserInput, cpp:479)
                highlight.Value = -1;                   // a fresh user edit resets the keyboard cursor (cpp:484–496)
                if (q.Length > 0) OpenPopup();          // keep the attached popup open for no-results
                else Close();
            }
        }, q);

        // ASB-initiated field write (arrow preview / restore) with its WinUI reason (UpdateTextBoxText, cpp:2669–2688).
        void SetTextWithReason(string s, TextChangeReason reason)
        {
            if (query.Peek() == s) return;
            pendingReason.Value = reason;
            query.Value = s;
        }

        // WinUI SubmitQuery (cpp:857–878): QueryText = the CURRENT field text; closes the list.
        void SubmitQuery()
        {
            OnQuerySubmitted?.Invoke(query.Peek());
            Close();
        }

        // Selection moved onto a row (arrow / click): preview the text iff UpdateTextOnSelect (reason SuggestionChosen,
        // cpp:2366–2381), then raise SuggestionChosen (cpp:2392–2397) — the WinUI OnSuggestionSelectionChanged order.
        void SelectionChangedTo(int i)
        {
            var matches = Live();
            if (i < 0 || i >= matches.Count) return;
            if (UpdateTextOnSelect)
                SetTextWithReason(matches[i], TextChangeReason.SuggestionChosen);
            OnSuggestionChosen?.Invoke(matches[i]);
        }

        // Row click (cpp OnListViewItemClick :2413–2437): SelectionChanged (highlight + preview + SuggestionChosen)
        // first, THEN QuerySubmitted — sequential.
        void ChooseAndSubmit(int i)
        {
            var matches = Live();
            if (i < 0 || i >= matches.Count) { SubmitQuery(); return; }
            highlight.Value = i;
            SelectionChangedTo(i);
            SubmitQuery();
        }

        // Enter (cpp:1149–1160): submit with the highlighted item as ChosenSuggestion — the field text was already
        // previewed by the selection change; SuggestionChosen is NOT re-raised here.
        void OnEnter(string _)
        {
            if (Presenter?.SubmitSelection?.Invoke() == true) { Close(); return; }
            SubmitQuery();
        }

        void OnEscape()
        {
            // Reset the text to what the user had typed + close (cpp:634–643); reason = ProgrammaticChange (cpp:636).
            SetTextWithReason(userTyped.Peek(), TextChangeReason.ProgrammaticChange);
            Close();
        }

        // Up/Down bubble here from the focused EditableText (no case in EditableText.HandleKey for arrows). Move the
        // highlight with wrap; stepping PAST an end restores the user-typed text (reason ProgrammaticChange) and clears
        // the highlight (cpp:1028–1125). Down on a CLOSED list that has matches re-opens it first.
        void HandleNavKeys(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Down && e.KeyCode != Keys.Up) return;

            if (handle.Value is not { IsOpen: true })
            {
                if (e.KeyCode == Keys.Down && q.Length > 0) { OpenPopup(); e.Handled = true; }
                return;
            }

            if (Presenter?.MoveSelection is { } moveSelection)
            {
                moveSelection(e.KeyCode == Keys.Down ? 1 : -1);
                e.Handled = true;
                return;
            }

            var matches = Live();
            if (matches.Count == 0) return;
            int cur = highlight.Peek();

            void RestoreTyped()
            {
                highlight.Value = -1;
                SetTextWithReason(userTyped.Peek(), TextChangeReason.ProgrammaticChange);   // cpp:1101/1109
            }
            void MoveTo(int i)
            {
                highlight.Value = i;
                SelectionChangedTo(i);
            }

            if (e.KeyCode == Keys.Down)
            {
                int next = cur + 1;
                if (next >= matches.Count) RestoreTyped();
                else MoveTo(next);
                e.Handled = true;
            }
            else // Up
            {
                if (cur < 0) MoveTo(matches.Count - 1);
                else if (cur - 1 < 0) RestoreTyped();
                else MoveTo(cur - 1);
                e.Handled = true;
            }
        }

        bool hasIcon = QueryIcon is not null;
        // QueryButton (Width 32 + Margin 2,0,0,0) + the reserved AutoSuggestBoxRightButtonMargin column (4) at the
        // right edge (AutoSuggestBox_themeresources.xaml:242 + :226–230).
        float iconCol = hasIcon ? QueryButtonWidth + QueryButtonLeftMargin + RightButtonMargin : 0f;

        float width = EffectiveWidth;   // subscribe: a live width move re-renders this field at the new size
        // The inner field's width as a STABLE derived signal: the EditableText is its own mounted component (its
        // plain Width freezes at ITS mount, and a parent re-render reuses it without re-running the factory), so it
        // subscribes to this memo and resizes itself when the live width moves.
        var innerWidth = UseComputed(() => EffectiveWidth - iconCol);

        var editor = Embed.Comp(() =>
        {
            var e = new EditableText
            {
                Text = query, Width = width - iconCol, WidthSignal = innerWidth,
                Height = Chrome == AutoSuggestBoxChrome.ElevatedPill ? MathF.Max(32f, FieldMinHeight - 4f) : 32f,
                Placeholder = Placeholder,
                Chromeless = true,
                OnCommit = OnEnter, OnCancel = OnEscape,
                OnFocusChanged = f =>
                {
                    focused.Value = f;
                    if (!f) Field?.MarkTouched();
                },
            };
            _edit = e;
            return e;
        });
        var children = new List<Element>
        {
            // FILL MODE — the editor is wrapped in a Basis=0 box. EditableText is a FIXED-WIDTH control (its root always
            // sets an explicit Width), so without this its measured width would flow up as our content width → as the
            // BASE width of every ancestor COLUMN (PageHeader → card → content), which (Shrink=0) can't shrink to the
            // window → horizontal overflow, and since we self-measure that width back into the editor it DIVERGED
            // (infinite expansion). FlexLayout zeroes a Basis=0 child's measured main (FlexLayout.cs:217), so this box
            // contributes 0 to our measured width — we collapse to just the query button — while Grow=1 still arranges
            // the editor to fill. (Non-fill keeps the editor inline: the explicit Width is the INTENDED fixed size.)
            Grow > 0f
                ? new BoxEl { Grow = 1f, Basis = 0f, Shrink = 1f, ClipToBounds = true,
                              AlignItems = FlexAlign.Stretch, Children = [editor] }
                : editor,
        };

        if (hasIcon)
        {
            // QueryButton (AutoSuggestBox_themeresources.xaml:242): the outer 32×28 slot at Margin 2,0,0,0 (+4 reserved
            // column); the INNER ContentPresenter carries the chrome at Margin = AutoSuggestBoxInnerButtonMargin 1,3
            // (:24 + :101) with the TextControlButton ramp: rest = TextControlButtonBackground transparent
            // (generic.xaml:889), hover = SubtleFillColorSecondary #0FFFFFFF/#09000000, press = SubtleFillColorTertiary
            // #0AFFFFFF/#06000000 (TextBox_themeresources.xaml:40–41/147–148); glyph E721 @ AutoSuggestBoxIconFontSize
            // 12, TextFillColorSecondary → pressed TextFillColorTertiary (:45–47/152–154).
            var queryPlate = new BoxEl
            {
                Grow = 1f, Margin = new Edges4(1, 3, 1, 3),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Chrome == AutoSuggestBoxChrome.ElevatedPill
                    ? CornerRadius4.All(MathF.Max(0f, (FieldRadius > 0f ? FieldRadius : Radii.Control) - 2f))
                    : Radii.ControlAll,
                Role = AutomationRole.Button,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                OnClick = SubmitQuery,
                Children = [Parts.Apply(PartQueryIcon, new TextEl(QueryIcon!)
                {
                    Size = IconFontSize, FontFamily = Theme.IconFont,
                    Color = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                })],
            };
            // Parts: restyle the plate (fills, corners, plate inset…); the submit mechanics always win.
            queryPlate = Parts.Apply(PartQueryButton, queryPlate) with { OnClick = SubmitQuery, Role = AutomationRole.Button };
            children.Add(new BoxEl
            {
                Width = QueryButtonWidth, Height = QueryButtonHeight,
                Margin = new Edges4(QueryButtonLeftMargin, 0, RightButtonMargin, 0),
                AlignItems = FlexAlign.Stretch,
                Children = [queryPlate],
            });
        }

        // The debounce ticker is mounted only while a debounce is pending; it polls the frame clock and fires the
        // (deferred) TextChanged with the captured reason once the deadline passes, then unmounts.
        if (debouncePending.Value)
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = debounceDeadline, Pending = debouncePending,
                Fire = () => RaiseTextChanged(debounceText.Value, debounceReason.Value),
            }));

        // KeepInteriorCornersSquare: while the list is open (open-down), square the field's BOTTOM corners so it joins the
        // popup; full radius (ControlCornerRadius, or the caller's pill radius) otherwise.
        float fieldR = FieldRadius > 0f ? FieldRadius : Radii.Control;
        bool elevated = Chrome == AutoSuggestBoxChrome.ElevatedPill;
        var fieldCorners = !elevated && open.Value
            ? new CornerRadius4(fieldR, fieldR, 0f, 0f)
            : CornerRadius4.All(fieldR);
        bool hasFocus = focused.Value;

        Action<NodeHandle> anchorCapture = h => { anchor.Value = h; if (Field is { } fn) fn.Node.Value = h; };
        Element[] rootKids = children.ToArray();
        var root = new BoxEl
        {
            // WinUI AutoSuggestBox field surface: ControlCornerRadius, 1px ControlStrokeColorDefault, ControlFillColorDefault.
            Direction = 0,
            // Fill mode (Grow > 0): fill the parent's WIDTH while keeping a fixed height.
            //  • Width=NaN          → a COLUMN parent cross-stretches us to its width; a ROW parent grows us in.
            //  • Height (explicit)  → the height we contribute to a column (so it allocates the field's row) AND the
            //                         cross size in a row. NOTE: do NOT use Basis=0 on this root — Basis is the MAIN
            //                         axis, which is VERTICAL in a column, so it would zero our height contribution and
            //                         the next row would overlap us. The editor-wrapper Basis=0 (below) is what keeps
            //                         our WIDTH from over-propagating; the root keeps its natural (tiny) content width.
            //  • Grow + MaxHeight   → Grow fills the width (row) but is also vertical in a column; MaxHeight clamps that
            //                         (FlexLayout clamps grow by ClampMain → MaxH) so a column can't stretch us tall.
            //  • MaxWidth           → caps the fill (omnibar = 720; 0 = unbounded).
            Width = Grow > 0f ? float.NaN : width,
            Height = Grow > 0f ? FieldMinHeight : float.NaN,
            Grow = Grow,
            Shrink = Grow > 0f ? 1f : 0f,
            MaxWidth = Grow > 0f && MaxFillWidth > 0f ? MaxFillWidth : float.NaN,
            MaxHeight = Grow > 0f ? FieldMinHeight : float.NaN,
            MinHeight = FieldMinHeight, AlignItems = FlexAlign.Center,
            Corners = fieldCorners,
            BorderWidth = elevated && hasFocus ? 2f : 1f,
            BorderColor = elevated ? ColorF.Transparent : Tok.StrokeControlDefault,
            BorderBrush = elevated
                ? (hasFocus ? GradientSpec.Solid(Tok.AccentDefault) : Tok.CircleElevationBorder)
                : null,
            HoverBorderBrush = elevated && !hasFocus ? Tok.CircleElevationBorder : null,
            PressedBorderBrush = elevated && !hasFocus ? Tok.CircleElevationBorder : null,
            Fill = elevated && hasFocus ? Tok.FillControlInputActive : Tok.FillControlDefault,
            // The field surface (WinUI TextBox BorderElement) owns the PointerOver fill at the field's CORNER RADIUS, and
            // CLIPS — so the inner Chromeless editor's square hover fill follows the (possibly pill) corners instead of
            // bleeding past them as a rectangle (the squaring is intentional per EditableText: "the composer rounds + clips").
            HoverFill = elevated && hasFocus ? Tok.FillControlInputActive : Tok.FillControlSecondary,
            PressedFill = elevated && hasFocus ? Tok.FillControlInputActive : Tok.FillControlTertiary,
            ClipToBounds = true,
            // form-validation.md: the field chrome owns the invalid border (the inner editor is borderless inside it).
            Validation = Field is { } fb ? Prop.Of(() => fb.Error.Value.IsValid ? ValidationState.None : ValidationState.Error) : default,
            Role = AutomationRole.ComboBox,
            OnRealized = anchorCapture,
            OnKeyDown = HandleNavKeys,                 // Up/Down bubble up to here from the focused field
            // Grow mode: self-measure width for EffectiveWidth so internal children + popup size correctly.
            OnBoundsChanged = Grow > 0f && WidthSignal is null
                ? r => { if (r.W > 0f && MathF.Abs(r.W - _selfW.Peek()) > 0.5f) _selfW.Value = r.W; }
                : null,
            Children = rootKids,
        };
        if (Parts is { } rp)
        {
            var m = rp.Apply(PartRoot, root);
            root = m with
            {
                Role = AutomationRole.ComboBox,
                OnKeyDown = HandleNavKeys,
                Children = rootKids,
                OnRealized = TemplateParts.Chain(anchorCapture, m.OnRealized),
            };
        }
        // form-validation.md: stack the reveal-animated error message row under the field (popup still anchors to `root`).
        // Fill mode: the wrapper fills width like the root (Width=NaN + Grow); no Basis=0 (this is a COLUMN, so Basis is
        // the vertical axis — zeroing it would collapse the field+message height) and no height lock (the message needs room).
        if (Field is { } vfield)
            return new BoxEl
            {
                Direction = 1,
                Width = Grow > 0f ? float.NaN : width, Grow = Grow,
                Children = [root, FieldVisuals.MessageRow(vfield.Error)],
            };
        return root;
    }

    private void RaiseTextChanged(string text, TextChangeReason reason)
    {
        TextChanged?.Invoke(text, reason);
        OnTextChanged?.Invoke(text);
    }
}

/// <summary>
/// The popup body: a reactive Component that subscribes to the owner's shared <c>Query</c> + <c>Highlight</c> signals so it
/// re-filters and re-highlights on every keystroke / arrow move (the overlay content thunk is otherwise a one-shot snapshot).
/// Returns ONLY the inner scroll + rows — the acrylic surface, 1px flyout stroke, OverlayCornerRadius, shadow and the
/// (0,2,0,2) presenter padding are supplied by the host's FlyoutSurface (same division of labor as MenuFlyout.Build).
/// </summary>
internal sealed class SuggestionsList : Component
{
    public required AutoSuggestBox Owner;
    public required Signal<string> Query;
    public required Signal<int> Highlight;
    public required Action<int> OnChoose;

    public override Element Render()
    {
        var q = Query.Value;                           // subscribe → granular re-render of ONLY this popup subtree
        int hi = Highlight.Value;                      // subscribe → re-highlight on arrow nav
        float width = Owner.EffectiveWidth;            // subscribe → an open popup follows a live field resize
        var matches = AutoSuggestBox.Filter(Owner.LiveSuggestions, q);   // reads SuggestionsSignal → re-filters when async results arrive

        bool loading = Owner.LoadingSignal?.Value ?? false;   // subscribe → the top bar + keep-prior-results behaviour

        // No matches: while a fetch is IN FLIGHT show nothing here (the top indeterminate bar carries the state) so the
        // popup never flashes "No results found" over a load — only a SETTLED empty set shows that message.
        if (matches.Count == 0)
            return WithBar(loading, width, loading
                ? new BoxEl { Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight }
                : new BoxEl
                {
                    Direction = 1,
                    Width = width,
                    MinWidth = width,
                    ClipToBounds = true,
                    Margin = new Edges4(-1, 0, -1, 0),
                    Children =
                    [
                        new BoxEl
                        {
                            MinHeight = AutoSuggestBox.ItemMinHeight,
                            AlignItems = FlexAlign.Center,
                            Padding = new Edges4(24, 0, 24, 0),
                            Children = [new TextEl("No results found") { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }],
                        },
                    ],
                });

        var parts = Owner.Parts;   // popup-local, NON-virtualized rows: per-row part modifiers are safe here
        var rows = new Element[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            int idx = i;
            bool selected = idx == hi;
            Action choose = () => OnChoose(idx);
            var row = new BoxEl
            {
                MinHeight = AutoSuggestBox.ItemMinHeight,   // ListViewItemMinHeight = 40 (ListViewItem_themeresources.xaml:14)
                AlignItems = FlexAlign.Center,
                // DefaultListViewItemStyle content padding = 16,0,12,0 (ListViewItem_themeresources.xaml:241) measured
                // from the ITEM edge; the rounded backplate is inset 4,2 (the WinUI 3 ListViewItemPresenter plate,
                // CornerRadius = ListViewItemCornerRadius 4, :58) → inside the plate the content sits at 12,0,8,0.
                Padding = new Edges4(12, 0, 8, 0),
                Margin = new Edges4(4, 2, 4, 2),
                Corners = Radii.ControlAll,
                Role = AutomationRole.MenuItem,
                Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,   // keyboard-cursor / selected fill
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                OnClick = choose,
                Children = RowContent(Owner, matches[idx], q),
            };
            if (parts is not null)
                row = parts.Apply(AutoSuggestBox.PartSuggestionItem, row) with { OnClick = choose, Role = AutomationRole.MenuItem };
            rows[i] = row;
        }

        // Cap the list at AutoSuggestListMaxHeight (374), clipping the overflow; size to content. AutoSuggestListPadding
        // (-1,0,-1,0) is the WinUI inner-ListView negative h-margin that lets rows bleed to the acrylic edge. (A scroll
        // viewport reports ~0 desired height → would collapse the list and make rows unclickable; scroll is deferred.)
        var column = new BoxEl
        {
            Direction = 1,
            Width = width,
            MinWidth = width,
            Margin = new Edges4(-1, 0, -1, 0), Children = rows,
        };
        var list = new ScrollEl
        {
            Content = column,
            ContentSized = true,
            Width = width,
            MinWidth = width,
            MaxHeight = Owner.MaxHeight,
        };
        if (parts is not null)
            list = parts.Apply(AutoSuggestBox.PartSuggestionsList, list) with { Content = column, ContentSized = true };
        return WithBar(loading, width, list);
    }

    // Prepend a thin indeterminate progress bar at the TOP while a fetch is in flight; the results (kept from the prior
    // query until the fresh set lands) sit below it — so re-querying never blanks the list.
    static Element WithBar(bool loading, float width, Element body)
        => loading
            ? new BoxEl { Direction = 1, Width = width, MinWidth = width, Children = [ProgressBar.Indeterminate(width), body] }
            : body;

    // Row content: an optional leading glyph + the suggestion text with the matched query substring brightened/bolded and
    // the remainder dimmed (the Spotify omnibar look). Plain bright text when BoldMatch is off.
    static Element[] RowContent(AutoSuggestBox owner, string text, string query)
    {
        var kids = new List<Element>(4);
        if (owner.ItemGlyph is { } g)
            kids.Add(new TextEl(g) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, Margin = new Edges4(0, 0, 12, 0) });

        int mi = owner.BoldMatch && query.Length > 0
            ? text.ToLowerInvariant().IndexOf(query.ToLowerInvariant(), System.StringComparison.Ordinal) : -1;
        if (mi < 0)
        {
            kids.Add(new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            return kids.ToArray();
        }

        if (mi > 0) kids.Add(Seg(text.Substring(0, mi), false, false));
        kids.Add(Seg(text.Substring(mi, query.Length), true, false));
        int after = mi + query.Length;
        kids.Add(after < text.Length ? Seg(text.Substring(after), false, true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Seg(string s, bool match, bool grow) => new TextEl(s)
        {
            Size = 14f, Weight = (ushort)(match ? 700 : 400),
            Color = match ? Tok.TextPrimary : Tok.TextSecondary,
            Grow = grow ? 1f : 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}

/// <summary>
/// WinUI's <c>AutoSuggestBox</c> 150ms TextChanged <c>DispatcherTimer</c>, expressed as an engine-idiomatic per-frame poller
/// (the same pattern as <see cref="OverlayCloseDriver"/>). Mounted only while a debounce is pending; subscribes to the host
/// frame clock so it re-renders every frame, fires <see cref="Fire"/> once <c>TickCount64</c> reaches <see cref="DeadlineMs"/>,
/// clears <see cref="Pending"/> (which unmounts it, letting the frame loop idle), and is re-armed by pushing the deadline +
/// flipping Pending back on (Stop()+Start()). The deadline is read each frame so a fresh keystroke extends it correctly.
/// </summary>
internal sealed class DebounceTicker : Component
{
    public required Ref<long> DeadlineMs;
    public required Signal<bool> Pending;
    public required Action Fire;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (during a pending debounce)
        UseEffect(() =>
        {
            if (!Pending.Peek()) return;
            if (Environment.TickCount64 >= DeadlineMs.Value)
            {
                Pending.Value = false;             // Stop() — re-renders the owner to unmount this ticker
                Fire();                            // raise the (debounced) TextChanged
            }
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}
