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
/// The list is opened iff the query is non-empty and there is at least one match (WinUI <c>UpdateSuggestionListVisibility</c>:
/// open iff <c>maxHeight &gt; 0 ∧ count &gt; 0</c>), driven from a <c>UseEffect</c> keyed on the query so the open/close runs
/// after the render commits (no side effects in render). The popup body is wrapped in its own <see cref="Embed.Comp"/>
/// <see cref="SuggestionsList"/> Component that subscribes to the shared query/highlight <see cref="Signal{T}"/>s, so it
/// RE-FILTERS on every keystroke — an overlay content thunk is otherwise a one-shot snapshot (only re-evaluated when the
/// OverlayHost version bumps on open/close).
/// </para>
/// <para>
/// <b>Debounce.</b> WinUI raises the public <c>TextChanged</c> through a 150ms <c>DispatcherTimer</c> that is Stop()+Start()'d
/// on every keystroke (<c>AutoSuggestBox::s_textChangedEventTimerDuration = 1500000</c> ticks = 150ms), so the app's handler
/// fires only after typing pauses; <c>UpdateSuggestionListVisibility</c> runs immediately (NOT debounced). We mirror that
/// exactly: the live filter + open/close decision are synchronous per keystroke, while the caller's <see cref="OnTextChanged"/>
/// is fired by a <see cref="DebounceTicker"/> (a frame-clock wall-time poller, mounted only while a debounce is pending —
/// same idiom as the overlay close driver) 150ms after the last edit. Set <see cref="DebounceMs"/>=0 to fire synchronously.
/// </para>
/// <para>
/// Keyboard nav arrives by BUBBLING: Up/Down have no case in <see cref="EditableText"/>'s key handler, so they bubble past
/// it to this root <see cref="BoxEl"/>'s <see cref="BoxEl.OnKeyDown"/> (which moves a highlighted index, with wrap +
/// restore-typed-text at the ends, and marks the event handled — WinUI <c>AutoSuggestBox::OnKeyDown</c>). Down on a closed
/// list that has matches re-opens it (WinUI shows the list on arrow when matches exist). Enter routes through
/// <see cref="EditableText.OnCommit"/> (choose the highlighted item, then submit the query); Escape via
/// <see cref="EditableText.OnCancel"/> restores the user-typed text and closes the popup. Light-dismiss (click outside) and
/// Escape-to-close are supplied by the overlay host; focus is captured on open and restored on close (host-wired).
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
    //                margin); AutoSuggestListViewItemMargin=12,11,0,13 (10,11,0,13 in perf2026 — used here).
    // themeresources: AutoSuggestBoxIconFontSize=12; AutoSuggestBoxRightButtonMargin=4; QueryButton Width=32 Height=28
    //                Margin=2,0,0,0; ListViewItem MinHeight=40; AutoSuggestBox CornerRadius=ControlCornerRadius (4).
    // AutoSuggestBox_Partial.cpp: s_textChangedEventTimerDuration = 1500000 ticks = 150ms (TextChanged debounce).
    public const float ItemMinHeight = 40f;
    public const float IconFontSize = 12f;
    public const float MaxPopupHeight = 374f;
    public const float RightButtonMargin = 4f;   // AutoSuggestBoxRightButtonMargin
    public const float QueryButtonWidth = 32f;
    public const float QueryButtonHeight = 28f;
    public const float QueryButtonLeftMargin = 2f;
    public const float TextChangedDebounceMs = 150f;

    public IReadOnlyList<string> Suggestions = [];
    public string Placeholder = "Search";
    public float Width = 280f;
    public Signal<string>? Text;                       // caller-owned query (two-way), like ComboBox.Text
    public Action<string>? OnTextChanged;              // WinUI TextChanged (raised, DEBOUNCED, on user edits)
    public Action<string>? OnSuggestionChosen;         // WinUI SuggestionChosen (item highlighted / chosen)
    public Action<string>? OnQuerySubmitted;           // WinUI QuerySubmitted (Enter / query-icon / row click)
    public string? QueryIcon = Icons.Search;           // null = no query button; default = the search glyph
    public float MaxHeight = MaxPopupHeight;           // AutoSuggestListMaxHeight
    public float DebounceMs = TextChangedDebounceMs;   // 0 = fire OnTextChanged synchronously (no debounce)

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
        float debounceMs = TextChangedDebounceMs)
        => Embed.Comp(() => new AutoSuggestBox
        {
            Suggestions = suggestions, Placeholder = placeholder, Width = width, Text = text,
            OnTextChanged = onTextChanged, OnSuggestionChosen = onSuggestionChosen,
            OnQuerySubmitted = onQuerySubmitted, QueryIcon = queryIcon, MaxHeight = maxPopupHeight, DebounceMs = debounceMs,
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
        var open = UseSignal(false);                   // IsSuggestionListOpen — drives the field corner-squaring
        var svc = UseContext(Overlay.Service);

        // 'programmatic' flags writes that came from arrow-preview / restore (so they don't re-fire TextChanged or
        // re-cache userTyped), mirroring AutoSuggestionBoxTextChangeReason_ProgrammaticChange vs _UserInput.
        var programmatic = UseRef(false);

        // Debounce plumbing: a deadline (TickCount64 ms) + the text snapshot to emit; the DebounceTicker fires when due.
        // 'pending' is a SIGNAL (not a ref) so arming it inside the post-commit effect re-renders to MOUNT the ticker
        // (a ref write wouldn't re-render); the deadline/text are refs (read inside the ticker, no reactivity needed).
        var debounceDeadline = UseRef(0L);
        var debouncePending = UseSignal(false);
        var debounceText = UseRef("");

        // Subscribe to the query so each keystroke re-renders → re-runs the open/close effect below.
        var q = query.Value;

        List<string> Live() => Filter(Suggestions, query.Peek());

        void Close()
        {
            handle.Value?.Close();
            handle.Value = null;
            highlight.Value = -1;
            open.Value = false;
        }

        void OpenPopup()
        {
            open.Value = true;
            if (handle.Value is { IsOpen: true }) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new SuggestionsList
                {
                    Owner = this, Query = query, Highlight = highlight, OnChoose = ChooseAndSubmit,
                }),
                FlyoutPlacement.BottomStretch);
            handle.Value.ClosedAction = () =>
            {
                handle.Value = null;
                highlight.Value = -1;
                open.Value = false;
            };
        }

        // Side effects (TextChanged debounce arming, userTyped cache, open/close) happen AFTER render commits — keyed on the
        // query. Mirrors OnTextBoxTextChanged: UpdateSuggestionListVisibility runs immediately; TextChanged is deferred.
        UseEffect(() =>
        {
            if (programmatic.Value) { programmatic.Value = false; return; }   // arrow-preview / restore: not a user edit
            if (userTyped.Peek() != q)
            {
                userTyped.Value = q;                    // m_userTypedText = strQueryText (only on _UserInput)
                if (DebounceMs <= 0f) OnTextChanged?.Invoke(q);
                else
                {
                    // Stop()+Start() the 150ms timer: push the deadline forward and (re)arm the ticker.
                    debounceText.Value = q;
                    debounceDeadline.Value = Environment.TickCount64 + (long)DebounceMs;
                    debouncePending.Value = true;
                }
            }
            highlight.Value = -1;                       // a fresh user edit resets the keyboard cursor (SelectedIndex = -1)

            if (q.Length > 0) OpenPopup();              // keep the attached popup open for no-results
            else Close();
        }, q);

        // Programmatically write the field (arrow preview / restore) WITHOUT re-firing TextChanged or resetting highlight.
        void SetTextProgrammatic(string s) { programmatic.Value = true; query.Value = s; }

        // Enter / row click / query-icon: pick the highlighted item (if any), then submit the query string (WinUI SubmitQuery).
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
            SetTextProgrammatic(userTyped.Peek());      // restore m_userTypedText
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
        // highlight with wrap; stepping PAST an end restores the user-typed text and clears the highlight (WinUI OnKeyDown).
        // Down on a CLOSED list that has matches re-opens it first (WinUI shows the list on arrow when matches exist).
        void HandleNavKeys(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Down && e.KeyCode != Keys.Up) return;

            if (handle.Value is not { IsOpen: true })
            {
                if (e.KeyCode == Keys.Down && q.Length > 0) { OpenPopup(); e.Handled = true; }
                return;
            }

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
            else // Up
            {
                if (cur < 0) { highlight.Value = matches.Count - 1; Preview(matches.Count - 1); }
                else if (cur - 1 < 0) { highlight.Value = -1; SetTextProgrammatic(userTyped.Peek()); }
                else { highlight.Value = cur - 1; Preview(cur - 1); }
                e.Handled = true;
            }
        }

        bool hasIcon = QueryIcon is not null;
        // QueryButton (Width 32 + left margin 2) + AutoSuggestBoxRightButtonMargin (4) reserved at the right edge.
        float iconCol = hasIcon ? QueryButtonWidth + QueryButtonLeftMargin + RightButtonMargin : 0f;

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
                // WinUI QueryButton: Width 32, Height 28, Margin 2,0,0,0, FontSize=AutoSuggestBoxIconFontSize (12),
                // CornerRadius=ControlCornerRadius. PointerOver/Pressed use the TextControlButton ramp.
                Width = QueryButtonWidth, Height = QueryButtonHeight, Margin = new Edges4(QueryButtonLeftMargin, 0, RightButtonMargin, 0),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, Role = AutomationRole.Button,
                HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                OnClick = () => SubmitQuery(query.Peek()),
                Children = [new TextEl(QueryIcon!) { Size = IconFontSize, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
            });
        }

        // The debounce ticker is mounted only while a debounce is pending; it polls the frame clock and fires OnTextChanged
        // (the deferred TextChanged) once the deadline passes, then unmounts (so the frame loop can idle).
        if (debouncePending.Value)
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = debounceDeadline, Pending = debouncePending,
                Fire = () => OnTextChanged?.Invoke(debounceText.Value),
            }));

        // KeepInteriorCornersSquare: while the list is open (open-down), square the field's BOTTOM corners so it joins the
        // popup; full ControlCornerRadius otherwise.
        var fieldCorners = open.Value
            ? new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f)
            : Radii.ControlAll;

        return new BoxEl
        {
            // WinUI AutoSuggestBox field surface: ControlCornerRadius, 1px ControlStrokeColorDefault, ControlFillColorDefault.
            Direction = 0, Width = Width, MinHeight = 32f, AlignItems = FlexAlign.Center,
            Corners = fieldCorners, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, Fill = Tok.FillControlDefault,
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

    public override Element Render()
    {
        var q = Query.Value;                           // subscribe → granular re-render of ONLY this popup subtree
        int hi = Highlight.Value;                      // subscribe → re-highlight on arrow nav
        var matches = AutoSuggestBox.Filter(Owner.Suggestions, q);

        // Mirror UpdateSuggestionListVisibility: no matches → close (deferred to an effect so render stays pure and
        // doesn't write the overlay version signal mid-render).
        if (matches.Count == 0)
            return new BoxEl
            {
                Direction = 1,
                Width = Owner.Width,
                MinWidth = Owner.Width,
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
            };

        var rows = new Element[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            int idx = i;
            bool selected = idx == hi;
            rows[i] = new BoxEl
            {
                MinHeight = AutoSuggestBox.ItemMinHeight,   // WinUI ListViewItem MinHeight = 40
                AlignItems = FlexAlign.Center,
                // AutoSuggestListViewItemMargin (perf2026) = 10,11,0,13 → content padding; row gets a small h-inset margin.
                Padding = new Edges4(10, 0, 11, 0),
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

        // Cap the list at AutoSuggestListMaxHeight (374), clipping the overflow; size to content. AutoSuggestListPadding
        // (-1,0,-1,0) is the WinUI inner-ListView negative h-margin that lets rows bleed to the acrylic edge. (A scroll
        // viewport reports ~0 desired height → would collapse the list and make rows unclickable; scroll is deferred.)
        var column = new BoxEl
        {
            Direction = 1,
            Width = Owner.Width,
            MinWidth = Owner.Width,
            Margin = new Edges4(-1, 0, -1, 0), Children = rows,
        };
        return new ScrollEl
        {
            Content = column,
            ContentSized = true,
            Width = Owner.Width,
            MinWidth = Owner.Width,
            MaxHeight = Owner.MaxHeight,
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
