using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Text input control demo pages (TextBox, PasswordBox, AutoSuggestBox) ──────────

[GalleryPage("TextBox", "TextBox", "Text", Icon = Icons.Font)]
sealed partial class TextBoxPage : Component
{
    static readonly Signal<string> _locked = new("This text is read-only — selection and copy still work; edits are blocked.");
    static readonly Signal<string> _capped = new("");
    static readonly Signal<string> _live = new("");
    static readonly Signal<string> _committed = new("—");

    public override Element Render() => GalleryPage.Shell("TextBox",
        "A single-line text field for entering and editing plain text. (Type to edit; Enter commits, Esc cancels.)",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(HeaderDescriptionSample),
        ExampleCard.Show(ReadOnlySample),
        ExampleCard.Show(MultiLineSample),
        ExampleCard.Show(CharLimitSample),
        ExampleCard.Show(FilteringSample),
        ExampleCard.Show(TwoWaySample));

    [Sample("A simple TextBox")]
    static Element Simple() => TextBox.Create(options: new TextBox.TextBoxOptions { Placeholder = "Enter your name" });

    [Sample("With a header and a description")]
    static Element HeaderDescription() => TextBox.Create(options: new TextBox.TextBoxOptions
    {
        Placeholder = "you@example.com", Width = 280f, Header = "Email",
        Description = "We'll only use this to contact you.",
    });

    [Sample("A read-only TextBox")]
    static Element ReadOnly() => TextBox.Create(_locked, options: new TextBox.TextBoxOptions { Width = 320f, IsReadOnly = true });

    [Sample("A multi-line TextBox", Description = "acceptsReturn turns Enter into a newline; the delete button is hidden for multi-line boxes.")]
    static Element MultiLine() => TextBox.Create(options: new TextBox.TextBoxOptions
    {
        Placeholder = "Type multiple lines of text", Width = 320f, AcceptsReturn = true, Height = 96f,
    });

    [Sample("With a character limit (MaxLength)")]
    static Element CharLimit() => VStack(8,
        TextBox.Create(_capped, options: new TextBox.TextBoxOptions { Placeholder = "Max 12 characters", Width = 280f, MaxLength = 12 }),
        GalleryPage.LiveText(() => $"{_capped.Value.Length}/12"));

    [Sample("Filtering input (BeforeTextChanging)", Description = "The gate receives the proposed full text; returning false rejects the insertion (typing, paste, IME).")]
    static Element Filtering() => TextBox.Create(options: new TextBox.TextBoxOptions
    {
        Placeholder = "Digits only", Width = 280f, BeforeTextChanging = s => s.All(char.IsAsciiDigit),
    });

    [Sample("Two-way text with Enter commit")]
    static Element TwoWay() => VStack(8,
        TextBox.Create(_live, options: new TextBox.TextBoxOptions { Placeholder = "Type, then press Enter", Width = 280f, OnCommit = v => _committed.Value = v }),
        // The live readout rides a compositor-only text binding — no page re-render per keystroke:
        VStack(4,
            GalleryPage.LiveText(() => "Live: " + (_live.Value.Length == 0 ? "—" : _live.Value)),
            GalleryPage.LiveText(() => $"Committed: {_committed.Value}")));
}

[GalleryPage("PasswordBox", "PasswordBox", "Text", Icon = Icons.Settings)]
sealed partial class PasswordBoxPage : Component
{
    static readonly Signal<string> _pw = new("");

    public override Element Render() => GalleryPage.Shell("PasswordBox",
        "A text field that masks its content for password entry.",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(HeaderSample),
        ExampleCard.Show(CustomCharSample),
        ExampleCard.Show(RevealModesSample),
        ExampleCard.Show(ValidatingSample));

    [Sample("A simple PasswordBox", Description = "While typing a fresh password, press and hold the eye to peek (the WinUI Peek reveal mode, the default).")]
    static Element Simple() => PasswordBox.Create();

    [Sample("With a header")]
    static Element Header() => PasswordBox.Create("Password", 280f, "Password");

    [Sample("A custom password character")]
    static Element CustomChar() => PasswordBox.Create("Enter your PIN", 280f, passwordChar: '#');

    [Sample("Reveal modes", Description = "Peek (the default) shows the press-and-hold eye while typing; Hidden never reveals; Visible shows plain text.")]
    static Element RevealModes() => HStack(12,
        PasswordBox.Create(width: 200f, header: "Hidden", revealMode: PasswordRevealMode.Hidden),
        PasswordBox.Create(width: 200f, header: "Visible", revealMode: PasswordRevealMode.Visible));

    [Sample("Validating on PasswordChanged")]
    static Element Validating() => VStack(8,
        PasswordBox.Create("At least 8 characters", 280f, maxLength: 16, password: _pw),
        GalleryPage.LiveText(() => _pw.Value.Length == 0 ? "—" : _pw.Value.Length < 8 ? $"Too short — {_pw.Value.Length}/8" : "Strong enough"));
}

[GalleryPage("AutoSuggestBox", "AutoSuggestBox", "Text", Icon = Icons.List)]
sealed partial class AutoSuggestBoxPage : Component
{
    static readonly string[] Fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Grape", "Mango", "Orange", "Peach", "Pear" };
    static readonly Signal<string> _chosen = new("—");
    static readonly Signal<string> _query = new("—");
    static readonly Signal<string> _reason = new("—");

    public override Element Render() => GalleryPage.Shell("AutoSuggestBox",
        "A text box that offers a filtered list of suggestions as the user types.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(EventsSample),
        ExampleCard.Show(NoQueryButtonSample),
        ExampleCard.Show(ChangeReasonSample));

    [Sample("A basic AutoSuggestBox", Description = "The list opens while the query matches at least one item; Esc restores the typed text and closes.")]
    static Element Basic()
    {
        string[] fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry",
                            "Grape", "Mango", "Orange", "Peach", "Pear" };
        return AutoSuggestBox.Create(fruits, "Search fruits");
    }

    [Sample("Suggestion and query events", Description = "Arrow keys or a row click raise SuggestionChosen; Enter, the search button, or a row click submit the query.")]
    static Element Events() => VStack(8,
        AutoSuggestBox.Create(Fruits, "Type a fruit, then press Enter",
            onSuggestionChosen: v => _chosen.Value = v,
            onQuerySubmitted: v => _query.Value = v),
        VStack(4,
            GalleryPage.LiveText(() => $"Chosen: {_chosen.Value}"),
            GalleryPage.LiveText(() => $"Submitted: {_query.Value}")));

    [Sample("Without a query button")]
    static Element NoQueryButton() => AutoSuggestBox.Create(Fruits, "No search icon", queryIcon: null);

    [Sample("TextChanged with a change reason", Description = "TextChanged is debounced 150 ms and carries the WinUI reason — UserInput, ProgrammaticChange, or SuggestionChosen (the arrow-key preview).")]
    static Element ChangeReason() => VStack(8,
        AutoSuggestBox.Create(Fruits, "Watch the reason",
            textChanged: (q, r) => _reason.Value = $"{r}: \"{q}\""),
        GalleryPage.LiveText(() => _reason.Value));
}
