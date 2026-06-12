using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Text input control demo pages (TextBox, PasswordBox, AutoSuggestBox) ──────────

sealed class TextBoxPage : Component
{
    public override Element Render()
    {
        var locked = UseSignal("This text is read-only — selection and copy still work; edits are blocked.");
        var capped = UseSignal("");
        var live = UseSignal("");
        var (committed, setCommitted) = UseState("—");
        return GalleryPage.Shell("TextBox",
            "A single-line text field for entering and editing plain text. (Type to edit; Enter commits, Esc cancels.)",
            ControlExample.Build("A simple TextBox", TextBox.Create("Enter your name"),
                code: """
                TextBox.Create("Enter your name")
                """),
            ControlExample.Build("With a header and a description", TextBox.Create("you@example.com", 280f, "Email", description: "We'll only use this to contact you."),
                code: """
                TextBox.Create("you@example.com", 280f, "Email",
                    description: "We'll only use this to contact you.")
                """),
            ControlExample.Build("A read-only TextBox", TextBox.Create(width: 320f, text: locked, isReadOnly: true),
                code: """
                var text = UseSignal("This text is read-only — selection and copy still work; edits are blocked.");

                TextBox.Create(width: 320f, text: text, isReadOnly: true)
                """),
            ControlExample.Build("A multi-line TextBox", TextBox.Create("Type multiple lines of text", 320f, acceptsReturn: true, height: 96f),
                description: "acceptsReturn turns Enter into a newline; the delete button is hidden for multi-line boxes.",
                code: """
                TextBox.Create("Type multiple lines of text", 320f,
                    acceptsReturn: true, height: 96f)
                """),
            ControlExample.Build("With a character limit (MaxLength)", TextBox.Create("Max 12 characters", 280f, text: capped, maxLength: 12),
                output: GalleryPage.LiveText(() => $"{capped.Value.Length}/12"),
                code: """
                var text = UseSignal("");

                TextBox.Create("Max 12 characters", 280f, text: text, maxLength: 12)
                """),
            ControlExample.Build("Filtering input (BeforeTextChanging)", TextBox.Create("Digits only", 280f, beforeTextChanging: s => s.All(char.IsAsciiDigit)),
                description: "The gate receives the proposed full text; returning false rejects the insertion (typing, paste, IME).",
                code: """
                TextBox.Create("Digits only", 280f,
                    beforeTextChanging: s => s.All(char.IsAsciiDigit))
                """),
            ControlExample.Build("Two-way text with Enter commit", TextBox.Create("Type, then press Enter", 280f, text: live, onCommit: setCommitted),
                output: VStack(4,
                    GalleryPage.LiveText(() => "Live: " + (live.Value.Length == 0 ? "—" : live.Value)),
                    BodyStrong($"Committed: {committed}")),
                code: """
                var text = UseSignal("");
                var (committed, setCommitted) = UseState("—");

                TextBox.Create("Type, then press Enter", 280f, text: text, onCommit: setCommitted)

                // The live readout rides a compositor-only text binding — no page re-render per keystroke:
                new TextEl("") { Text = text }
                """));
    }
}

sealed class PasswordBoxPage : Component
{
    public override Element Render()
    {
        var pw = UseSignal("");
        return GalleryPage.Shell("PasswordBox",
            "A text field that masks its content for password entry.",
            ControlExample.Build("A simple PasswordBox", PasswordBox.Create(),
                description: "While typing a fresh password, press and hold the eye to peek (the WinUI Peek reveal mode, the default).",
                code: """
                PasswordBox.Create()
                """),
            ControlExample.Build("With a header", PasswordBox.Create("Password", 280f, "Password"),
                code: """
                PasswordBox.Create("Password", 280f, "Password")
                """),
            ControlExample.Build("A custom password character", PasswordBox.Create("Enter your PIN", 280f, passwordChar: '#'),
                code: """
                PasswordBox.Create("Enter your PIN", 280f, passwordChar: '#')
                """),
            ControlExample.Build("Reveal modes",
                HStack(12,
                    PasswordBox.Create(width: 200f, header: "Hidden", revealMode: PasswordRevealMode.Hidden),
                    PasswordBox.Create(width: 200f, header: "Visible", revealMode: PasswordRevealMode.Visible)),
                description: "Peek (the default) shows the press-and-hold eye while typing; Hidden never reveals; Visible shows plain text.",
                code: """
                HStack(12,
                    PasswordBox.Create(width: 200f, header: "Hidden", revealMode: PasswordRevealMode.Hidden),
                    PasswordBox.Create(width: 200f, header: "Visible", revealMode: PasswordRevealMode.Visible))
                """),
            ControlExample.Build("Validating on PasswordChanged", PasswordBox.Create("At least 8 characters", 280f, maxLength: 16, password: pw),
                output: GalleryPage.LiveText(() => pw.Value.Length == 0 ? "—" : pw.Value.Length < 8 ? $"Too short — {pw.Value.Length}/8" : "Strong enough"),
                code: """
                var pw = UseSignal("");

                PasswordBox.Create("At least 8 characters", 280f, maxLength: 16, password: pw)
                """));
    }
}

sealed class AutoSuggestBoxPage : Component
{
    static readonly string[] Fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Grape", "Mango", "Orange", "Peach", "Pear" };
    public override Element Render()
    {
        var (chosen, setChosen) = UseState("—");
        var (query, setQuery) = UseState("—");
        var (reason, setReason) = UseState("—");
        return GalleryPage.Shell("AutoSuggestBox",
            "A text box that offers a filtered list of suggestions as the user types.",
            ControlExample.Build("A basic AutoSuggestBox", AutoSuggestBox.Create(Fruits, "Search fruits"),
                description: "The list opens while the query matches at least one item; Esc restores the typed text and closes.",
                code: """
                string[] fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry",
                                    "Grape", "Mango", "Orange", "Peach", "Pear" };

                AutoSuggestBox.Create(fruits, "Search fruits")
                """),
            ControlExample.Build("Suggestion and query events",
                AutoSuggestBox.Create(Fruits, "Type a fruit, then press Enter", onSuggestionChosen: setChosen, onQuerySubmitted: setQuery),
                description: "Arrow keys or a row click raise SuggestionChosen; Enter, the search button, or a row click submit the query.",
                output: VStack(4, BodyStrong($"Chosen: {chosen}"), BodyStrong($"Submitted: {query}")),
                code: """
                AutoSuggestBox.Create(fruits, "Type a fruit, then press Enter",
                    onSuggestionChosen: setChosen,
                    onQuerySubmitted: setQuery)
                """),
            ControlExample.Build("Without a query button", AutoSuggestBox.Create(Fruits, "No search icon", queryIcon: null),
                code: """
                AutoSuggestBox.Create(fruits, "No search icon", queryIcon: null)
                """),
            ControlExample.Build("TextChanged with a change reason",
                AutoSuggestBox.Create(Fruits, "Watch the reason", textChanged: (q, r) => setReason($"{r}: \"{q}\"")),
                description: "TextChanged is debounced 150 ms and carries the WinUI reason — UserInput, ProgrammaticChange, or SuggestionChosen (the arrow-key preview).",
                output: BodyStrong(reason),
                code: """
                AutoSuggestBox.Create(fruits, "Watch the reason",
                    textChanged: (q, r) => setReason($"{r}: \"{q}\""))
                """));
    }
}
