using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI AutoSuggestBox: an <see cref="EditableText"/> field whose filtered suggestions are hosted in a light-dismiss
/// <b>popup</b> (an anchored overlay), not inline — so the list floats over content instead of reflowing the page and
/// auto-flips above the box when there is no room below (same plumbing as <see cref="ComboBox"/>).
/// <para>
/// The list is opened iff the query is non-empty and there is at least one match (WinUI <c>UpdateSuggestionListVisibility</c>),
/// driven from a <c>UseEffect</c> keyed on the query so the open/close runs after the render commits (no side effects in
/// render). The popup body is wrapped in its own <see cref="Embed.Comp"/> <see cref="SuggestionsList"/> Component that
/// subscribes to the shared query/highlight <see cref="Signal{T}"/>s, so it RE-FILTERS on every keystroke — an overlay
/// content thunk is otherwise a one-shot snapshot (only re-evaluated when the OverlayHost version bumps on open/close).
/// </para>
/// <para>
/// Keyboard nav arrives by BUBBLING: Up/Down have no case in <see cref="EditableText"/>'s key handler, so they bubble past
/// it to this root <see cref="BoxEl"/>'s <see cref="BoxEl.OnKeyDown"/> (which moves a highlighted index, with wrap +
/// restore-typed-text at the ends, and marks the event handled). Enter routes through <see cref="EditableText.OnCommit"/>
/// (choose the highlighted item, then submit the query); Escape via <see cref="EditableText.OnCancel"/> restores the
/// user-typed text and closes the popup. Light-dismiss (click outside) and Escape-to-close are supplied by the overlay host.
/// </para>
/// <para>Deferred (no engine seam): per-keystroke TextChanged debounce — WinUI debounces via a DispatcherTimer; here
/// TextChanged fires synchronously on each user edit.</para>
/// </summary>
public sealed class AutoSuggestBox : Component
{
    // ── WinUI dims (generic.xaml: AutoSuggestListMaxHeight=374, AutoSuggestListBorderThemeThickness=1,
    // AutoSuggestListMargin=0,2,0,2; AutoSuggestBoxIconFontSize=12; ListViewItem MinHeight=40). ──
    public const float ItemMinHeight = 40f;
    public const float IconFontSize = 12f;
    public const float MaxPopupHeight = 374f;

    public IReadOnlyList<string> Suggestions = [];
    public string Placeholder = "Search";
    public float Width = 280f;
    public Signal<string>? Text;                       // caller-owned query (two-way), like ComboBox.Text
    public Action<string>? OnTextChanged;              // WinUI TextChanged (raised on user edits)
    public Action<string>? OnSuggestionChosen;         // WinUI SuggestionChosen (item highlighted / chosen)
    public Action<string>? OnQuerySubmitted;           // WinUI QuerySubmitted (Enter / query-icon / row click)
    public string? QueryIcon = Icons.Search;           // null = no query button; default = the search glyph
    public float MaxHeight = MaxPopupHeight;           // AutoSuggestListMaxHeight

    public static Element Create(
        IReadOnlyList<string> suggestions,
        string placeholder = "Search",
        float width = 280f,
        Signal<string>? text = null,
        Action<string>? onTextChanged = null,
        Action<string>? onSuggestionChosen = null,
        Action<string>? onQuerySubmitted = null,
        string? queryIcon = Icons.Search,
        float maxPopupHeight = MaxPopupHeight)
        => Embed.Comp(() => new AutoSuggestBox
        {
            Suggestions = suggestions, Placeholder = placeholder, Width = width, Text = text,
            OnTextChanged = onTextChanged, OnSuggestionChosen = onSuggestionChosen,
            OnQuerySubmitted = onQuerySubmitted, QueryIcon = queryIcon, MaxHeight = maxPopupHeight,
        });

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

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallbackText = UseSignal("");
        var query = Text ?? fallbackText;
        var userTyped = UseSignal("");                 // last user-typed text, restored on arrow-out / Escape
        var highlight = UseSignal(-1);                 // keyboard-cursor index into the live match set
        var svc = UseContext(Overlay.Service);

        // 'programmatic' flags writes that came from arrow-preview / restore (so they don't re-fire TextChanged or
        // re-cache userTyped), mirroring AutoSuggestionBoxTextChangeReason.
        var programmatic = UseRef(false);

        // Subscribe to the query so each keystroke re-renders → re-runs the open/close effect below.
        var q = query.Value;

        List<string> Live() => Filter(Suggestions, query.Peek());

        void Close()
        {
            handle.Value?.Close();
            handle.Value = null;
            highlight.Value = -1;
        }

        void OpenPopup()
        {
            if (handle.Value is { IsOpen: true }) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new SuggestionsList
                {
                    Owner = this, Query = query, Highlight = highlight, OnChoose = ChooseAndSubmit, OnEmpty = Close,
                }),
                FlyoutPlacement.BottomStretch);
        }

        // Side effects (TextChanged, userTyped cache, open/close) happen AFTER render commits — keyed on the query.
        UseEffect(() =>
        {
            if (programmatic.Value) { programmatic.Value = false; return; }   // arrow-preview / restore: not a user edit
            if (userTyped.Peek() != q) { userTyped.Value = q; OnTextChanged?.Invoke(q); }
            highlight.Value = -1;                       // a fresh user edit resets the keyboard cursor

            if (Live().Count > 0) OpenPopup();
            else Close();
        }, q);

        // Programmatically write the field (arrow preview / restore) WITHOUT re-firing TextChanged or resetting highlight.
        void SetTextProgrammatic(string s) { programmatic.Value = true; query.Value = s; }

        // Enter / row click / query-icon: pick the highlighted item (if any), then submit the query string.
        void SubmitQuery(string text)
        {
            OnQuerySubmitted?.Invoke(text);
            Close();
        }

        void ChooseAndSubmit(int i)
        {
            var matches = Live();
            if (i < 0 || i >= matches.Count) { SubmitQuery(query.Peek()); return; }
            SetTextProgrammatic(matches[i]);
            OnSuggestionChosen?.Invoke(matches[i]);
            SubmitQuery(matches[i]);
        }

        void OnEnter(string _)
        {
            int h = highlight.Peek();
            if (h >= 0) { ChooseAndSubmit(h); return; }
            SubmitQuery(query.Peek());
        }

        void OnEscape()
        {
            SetTextProgrammatic(userTyped.Peek());      // restore user-typed text
            Close();
        }

        // Preview the highlighted item into the field (UpdateTextOnSelect = true) without re-firing TextChanged.
        void Preview(int i)
        {
            var matches = Live();
            if (i < 0 || i >= matches.Count) return;
            SetTextProgrammatic(matches[i]);
            OnSuggestionChosen?.Invoke(matches[i]);
        }

        // Up/Down bubble here from the focused EditableText (no case in EditableText.HandleKey for arrows). Move the
        // highlight with wrap; stepping PAST an end restores the user-typed text and clears the highlight (WinUI).
        void HandleNavKeys(KeyEventArgs e)
        {
            if (handle.Value is not { IsOpen: true }) return;
            var matches = Live();
            if (matches.Count == 0) return;
            int cur = highlight.Peek();

            if (e.KeyCode == Keys.Down)
            {
                int next = cur + 1;
                if (next >= matches.Count) { highlight.Value = -1; SetTextProgrammatic(userTyped.Peek()); }
                else { highlight.Value = next; Preview(next); }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                if (cur < 0) { highlight.Value = matches.Count - 1; Preview(matches.Count - 1); }
                else if (cur - 1 < 0) { highlight.Value = -1; SetTextProgrammatic(userTyped.Peek()); }
                else { highlight.Value = cur - 1; Preview(cur - 1); }
                e.Handled = true;
            }
        }

        bool hasIcon = QueryIcon is not null;
        float iconCol = hasIcon ? 34f : 0f;            // QueryButton Width 32 + a hair of inset

        var children = new List<Element>
        {
            Embed.Comp(() => new EditableText
            {
                Text = query, Width = Width - iconCol, Height = 32f, Placeholder = Placeholder,
                OnCommit = OnEnter, OnCancel = OnEscape,
            }),
        };

        if (hasIcon)
        {
            children.Add(new BoxEl
            {
                // WinUI QueryButton: Width 32, Height 28, FontSize=AutoSuggestBoxIconFontSize (12).
                Width = 32f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, Role = AutomationRole.Button,
                HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                OnClick = () => SubmitQuery(query.Peek()),
                Children = [new TextEl(QueryIcon!) { Size = IconFontSize, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
            });
        }

        return new BoxEl
        {
            // WinUI AutoSuggestBox field surface: ControlCornerRadius, 1px ControlStrokeColorDefault, ControlFillColorDefault.
            Direction = 0, Width = Width, MinHeight = 32f, AlignItems = FlexAlign.Center,
            Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, Fill = Tok.FillControlDefault,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnKeyDown = HandleNavKeys,                 // Up/Down bubble up to here from the focused field
            Children = children.ToArray(),
        };
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
    public required Action OnEmpty;

    public override Element Render()
    {
        var q = Query.Value;                           // subscribe → granular re-render of ONLY this popup subtree
        int hi = Highlight.Value;                      // subscribe → re-highlight on arrow nav
        var matches = AutoSuggestBox.Filter(Owner.Suggestions, q);

        // Mirror UpdateSuggestionListVisibility: no matches → close (deferred to an effect so render stays pure and
        // doesn't write the overlay version signal mid-render).
        UseEffect(() => { if (matches.Count == 0) OnEmpty(); }, matches.Count == 0);

        if (matches.Count == 0)
            return new BoxEl { HitTestVisible = false };

        var rows = new Element[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            int idx = i;
            bool selected = idx == hi;
            rows[i] = new BoxEl
            {
                MinHeight = AutoSuggestBox.ItemMinHeight,   // WinUI ListViewItem MinHeight = 40
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(11, 0, 11, 0),
                Margin = new Edges4(4, 2, 4, 2),
                Corners = Radii.ControlAll,
                Role = AutomationRole.MenuItem,
                Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,   // keyboard-cursor / selected fill
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                OnClick = () => OnChoose(idx),
                Children = [new TextEl(matches[idx]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }],
            };
        }

        // Cap the list at AutoSuggestListMaxHeight (374) with internal scroll; clip the overflow.
        var column = new BoxEl { Direction = 1, MaxHeight = Owner.MaxHeight, ClipToBounds = true, Children = rows };
        return Ui.ScrollView(column);
    }
}
