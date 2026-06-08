using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Text input control demo pages (TextBox, PasswordBox, AutoSuggestBox) ──────────

sealed class TextBoxPage : Component
{
    public override Element Render() => GalleryPage.Shell("TextBox",
        "A single-line text field for entering and editing plain text. (Type to edit; Enter commits, Esc cancels.)",
        ControlExample.Build("A simple TextBox", TextBox.Create("Enter your name")),
        ControlExample.Build("With a header", TextBox.Create("you@example.com", 280f, "Email")));
}

sealed class PasswordBoxPage : Component
{
    public override Element Render() => GalleryPage.Shell("PasswordBox",
        "A text field that masks its content for password entry.",
        ControlExample.Build("A PasswordBox", PasswordBox.Create("Password", 280f, "Password")));
}

sealed class AutoSuggestBoxPage : Component
{
    static readonly string[] Fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Grape", "Mango", "Orange", "Peach", "Pear" };
    public override Element Render() => GalleryPage.Shell("AutoSuggestBox",
        "A text box that offers a filtered list of suggestions as the user types.",
        ControlExample.Build("Type to filter fruits", AutoSuggestBox.Create(Fruits, "Search fruits")));
}
