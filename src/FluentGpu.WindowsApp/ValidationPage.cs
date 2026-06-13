using System;
using System.Text.RegularExpressions;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApp;
using static FluentGpu.Dsl.Ui;

// ── The "Validation" gallery page ─────────────────────────────────────────────────────────────────────────────────────
// A live showcase of native form validation (src/FluentGpu.Engine/Forms — form-validation.md): the feature WinUI lacks.
// Validity is a derived Memo<FieldError> over each field's controlled signal, so cross-field and conditional rules are
// free (a rule that reads a sibling signal auto-re-validates), errors stay silent until a field is touched (no red on
// load), the submit button auto-disables until the whole form is valid, and a failed submit reveals every error. It is
// reflection-free (NativeAOT), zero-allocation on the keystroke path, and i18n — messages are localization keys.
sealed class ValidationPage : Component
{
    public override Element Render()
    {
        // Ensure the validation.* message keys are loaded (idempotent — LoadFolder replaces tables).
        UseEffect(() =>
        {
            Localization.DefaultCulture = "en-US";
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "loc");
            Localization.LoadFolder(dir);
            if (string.IsNullOrEmpty(Localization.CurrentCulture)) Localization.SetCulture("en-US");
        }, MountOnce);

        return GalleryPage.ShellKeyed("validation", "Validation",
            "Native, signals-native form validation (form-validation.md) — the gap WinUI never closed. Validity is a " +
            "derived Memo<FieldError> over each field's signal: cross-field and conditional rules are FREE (a rule that " +
            "reads a sibling signal auto-re-validates — no INotifyDataErrorInfo dictionary), errors stay silent until a " +
            "field is touched (no red on load), the submit button auto-disables until the form is valid, and a failed " +
            "submit reveals every error. Reflection-free (NativeAOT), zero-alloc on the keystroke path, and i18n — every " +
            "message is a localization key resolved at the bound text node.",
            Embed.Comp(() => new ValidationDemo()));
    }

    static readonly object[] MountOnce = new object[] { "validation-mount" };
}

/// <summary>The live sign-up form. Three controlled signals feed three <c>UseField</c>s under one <c>UseForm</c>; the
/// TextBoxes surface the invalid border + message via their <c>Field</c> prop; the submit row reads
/// <see cref="FormScope.IsValid"/>.</summary>
sealed class ValidationDemo : Component
{
    static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    readonly Signal<string> _email = new("");
    readonly Signal<string> _pwd = new("");
    readonly Signal<string> _confirm = new("");

    public override Element Render()
    {
        var form = UseForm();   // sets the form-under-construction so the UseField calls below auto-join it

        var email = UseField(_email,
            Rules.Required("validation.required"),
            Rules.Matches(EmailRx, "validation.email"));

        var pwd = UseField(_pwd,
            Rules.Required("validation.required"),
            Rules.MinLength(8, "validation.minlen"));

        // Cross-field: the rule reads _pwd.Value INSIDE the error memo, so once Confirm is touched, editing EITHER
        // password re-validates it. Timing is the default OnTouched — it stays silent until the user has actually been
        // in the Confirm box (no premature "don't match" while they're still typing the password). No dependency wiring.
        var confirm = UseField(_confirm, Rules.Equals(_pwd, "validation.match"));

        return new BoxEl
        {
            Direction = 1, Gap = 16f, MaxWidth = 460f,
            Children =
            [
                ControlExample.Build("Sign-up form",
                    new BoxEl
                    {
                        Direction = 1, Gap = 14f,
                        Children =
                        [
                            TextBox.Create(header: "Email", placeholder: "you@example.com", width: 380f, text: _email, field: email),
                            TextBox.Create(header: "Password", width: 380f, text: _pwd, field: pwd),
                            TextBox.Create(header: "Confirm password", width: 380f, text: _confirm, field: confirm),
                            Embed.Comp(() => new SubmitRow(form)),
                        ],
                    },
                    description: "Errors appear only after you leave a field (touched-gating — no red on load). Typing in " +
                                 "Password instantly re-checks Confirm (cross-field is free). The button enables only when " +
                                 "the whole form is valid; clicking it while invalid reveals every error at once.",
                    code: """
                    var form = UseForm();
                    var email = UseField(_email,
                        Rules.Required(), Rules.Matches(EmailRx, "err.email"));
                    var pwd = UseField(_pwd,
                        Rules.Required(), Rules.MinLength(8));
                    // cross-field: reads _pwd in the memo, re-checks live
                    var confirm = UseField(_confirm, Rules.Equals(_pwd));

                    // one prop wires the border + message + touched:
                    TextBox.Create(header: "Email", text: _email,
                                   field: email);

                    // submit auto-gated by the whole form's validity:
                    Button.Accent("Create account",
                        () => { if (form.Validate()) Save(); },
                        isEnabled: form.IsValid.Value);
                    """),
            ],
        };
    }
}

/// <summary>The submit row: a scoped component that reads <see cref="FormScope.IsValid"/> (subscribes, so it re-renders
/// only itself when validity flips) to gate the accent button, and shows a success line after a valid submit.</summary>
sealed class SubmitRow : Component
{
    private readonly FormScope _form;
    public SubmitRow(FormScope form) => _form = form;

    public override Element Render()
    {
        bool valid = _form.IsValid.Value;                  // subscribe → re-render this row on a validity flip
        var (done, setDone) = UseState(false);

        return new BoxEl
        {
            Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0, 4, 0, 0),
            Children =
            [
                Button.Accent("Create account", () => { if (_form.Validate()) setDone(true); }, isEnabled: valid),
                done
                    ? new TextEl("Account created ✓") { Size = 14f, Weight = 600, Color = Tok.SystemFillSuccess }
                    : new TextEl(valid ? "Ready to submit" : "Fill the form to continue") { Size = 13f, Color = Tok.TextTertiary },
            ],
        };
    }
}
