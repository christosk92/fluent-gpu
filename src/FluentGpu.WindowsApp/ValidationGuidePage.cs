using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── The "Validation" FUNDAMENTALS documentation page ───────────────────────────────────────────────────────────────────
// A reference-grade walkthrough of FluentGpu's signals-native form validation (src/FluentGpu.Engine/Forms — Field / Form /
// Msg / Rules / Validator + the UseField/UseForm hooks). Unlike the "Validation" SAMPLE page (which is a single live form),
// this is the *manual*: the model, the full built-in Rules surface, CUSTOM RULES (inline / Predicate / reusable factory /
// cross-field / async), validation timing, forms + submit gating + focus-first-error, the source-generator story, and a
// small self-contained live demo at the end. It mirrors the LocalizationPage doc-card idiom verbatim:
// GalleryPage.ShellKeyed + ExampleCard.Build cards + Subtitle/Body section headings + a collapsible Notes appendix.
//
// Every code snippet uses the EXACT public signatures from FluentGpu.Forms. The only loc keys resolved LIVE are the ones
// shipped in assets/loc/*.json under "validation" (required/minlen/maxlen/range/email/match); illustrative custom keys
// (validation.reserved, validation.nospaces, …) appear ONLY inside code: text blocks, never in compiled bindings.
[GalleryPage("validation-guide", "Validation", "App services", Icon = Icons.Accept)]
sealed class ValidationGuidePage : Component
{
    public override Element Render()
    {
        // Ensure the validation.* message keys are loaded (idempotent — LoadFolder replaces tables). Same mount-once
        // pattern as the Localization / Validation-sample pages.
        UseEffect(() =>
        {
            Localization.DefaultCulture = "en-US";
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "loc");
            Localization.LoadFolder(dir);
            if (string.IsNullOrEmpty(Localization.CurrentCulture)) Localization.SetCulture("en-US");
        }, MountOnce);

        return GalleryPage.ShellKeyed("validation-guide", "Validation",
            "FluentGpu validation is signals-native: a field's validity is a derived Memo<FieldError> computed over the " +
            "field's own controlled signal, so it re-folds automatically whenever the value — or any sibling signal a " +
            "rule reads — changes. There is no INotifyDataErrorInfo dictionary, no ErrorsChanged event, and no per-control " +
            "wiring: cross-field and conditional rules are FREE because reading another signal's .Value inside a rule body " +
            "subscribes the error memo to it. Rules are plain Validator<T> delegates (no attributes, no reflection), so the " +
            "whole stack is NativeAOT-clean and zero-allocation on the keystroke path; messages are localization keys, so " +
            "they are i18n + culture-reactive at the bound text node. This page is the reference manual — the model, the " +
            "full Rules API, custom rules, timing, async/server checks, forms, and the source-generator story — ending in a " +
            "small live sign-up form.",
            Embed.Comp(() => new ValidationGuideBody()));
    }

    static readonly FluentGpu.Hooks.DepKey MountOnce = FluentGpu.Hooks.DepKey.Empty;
}

/// <summary>The documentation body + the embedded live demo. A plain <see cref="Component"/>: the prose is static, and the
/// one interactive card delegates to <see cref="ValidationGuideDemo"/> (its own small sign-up form) so its hooks stay
/// scoped.</summary>
sealed class ValidationGuideBody : Component
{
    public override Element Render()
    {
        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                // ══ SECTION 1: THE MODEL ════════════════════════════════════════════════════════════════════════════
                Subtitle("The model"),
                Body("Validation rides the same controlled-signal model as every input on the engine. You own a " +
                     "Signal<T> for each field's value; UseField wraps it with one or more rules and returns a Field<T> " +
                     "whose Error is a derived Memo<FieldError>. Because it is a memo over the value signal, it re-evaluates " +
                     "automatically — and a rule that reads a sibling signal (Rules.Equals, Rules.When) auto-subscribes to " +
                     "that signal too. This is the gap WinUI never closed: no INotifyDataErrorInfo error dictionary, no " +
                     "ErrorsChanged plumbing, no manual re-validation calls.").Secondary(),

                ExampleCard.Build("The types you touch",
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f,
                        Children =
                        [
                            TypeLine("Validator<T>", "delegate: MsgId Validator(T value) — a rule. Returns MsgId.None when valid."),
                            TypeLine("MsgId", "a message identity — a loc KEY (preferred) or a literal. MsgId.None == valid."),
                            TypeLine("FieldError", "(MsgId First, byte FailCount). FieldError.Valid; IsValid => FailCount == 0."),
                            TypeLine("Field<T>", "what UseField returns: Value, Error, IsValid, IsValidating, Touched, …"),
                            TypeLine("FormScope", "what UseForm returns: SubmitAttempted, IsValid, IsValidating, Validate()."),
                        ],
                    },
                    description: "A rule is a one-line delegate, not an attribute. A Field<T> is a record of signals/memos: " +
                                 "reading Error in a control subscribes only that control, with zero per-frame allocation. " +
                                 "MsgId holds a loc key by design — the message is resolved (and localized) at the bound text " +
                                 "node, never inside the rule, so the rule allocates nothing on a keystroke.",
                    code: """
                    namespace FluentGpu.Forms;

                    // A rule: returns MsgId.None when valid, else a message identity.
                    public delegate MsgId Validator<in T>(T value);

                    public readonly record struct MsgId(string? Text, bool IsLiteral)
                    {
                        public static readonly MsgId None = default;   // the "valid" sentinel
                        public bool IsEmpty { get; }                   // == valid
                    }
                    public readonly record struct FieldError(MsgId First, byte FailCount)
                    {
                        public static readonly FieldError Valid;
                        public bool IsValid => FailCount == 0;
                    }

                    // The handle UseField returns — every member is a signal/memo:
                    public sealed record Field<T>(
                        IReadSignal<T> Value, Memo<FieldError> Error, Memo<bool> IsValid,
                        Memo<bool> IsValidating, Signal<bool> Touched, Signal<NodeHandle> Node,
                        Action MarkTouched, Action<MsgId> SetServerError);
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 2: SETUP ════════════════════════════════════════════════════════════════════════════════
                Subtitle("Setup"),
                Body("Three pieces wire a validated field: a controlled signal for the value, a UseField call binding the " +
                     "rules, and the field: prop on a control. That one prop wires everything — the invalid border, the " +
                     "error message TextEl, and touched-on-blur — so the control turns red only after the user has left it.").Secondary(),

                ExampleCard.Build("1 · Load the validation.* message keys",
                    GalleryPage.LiveText(() => $"validation.required → \"{Loc.Get("validation.required")}\""),
                    description: "Rule factories default to loc keys (validation.required, validation.email, …). Ship them in " +
                                 "assets/loc/<culture>.json and load the folder once at startup (idempotent; safe from a mount " +
                                 "effect). On a table hit the message resolves with zero allocation; switching culture " +
                                 "re-resolves the bound error text in place (no re-render).",
                    code: """
                    // assets/loc/en-US.json
                    { "validation": {
                        "required": "This field is required.",
                        "minlen":   "Must be at least 8 characters.",
                        "email":    "Enter a valid email address.",
                        "match":    "Passwords don’t match."
                    } }

                    // once at startup (e.g. a UseEffect mount effect):
                    Localization.DefaultCulture = "en-US";
                    Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc"));
                    """),

                ExampleCard.Build("2 · A controlled signal + UseField + the field: prop",
                    Embed.Comp(() => new EmailOnlyDemo()),
                    description: "_email is the controlled value signal. UseField wraps it with two rules and returns a " +
                                 "Field<string>. Passing field: to TextBox.Create wires the invalid border, the message line, " +
                                 "and marks the field touched on blur. Leave the box empty or type a bad address, then click " +
                                 "away — the message appears only after blur (the default OnTouched timing).",
                    code: """
                    static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
                    readonly Signal<string> _email = new("");

                    var email = UseField(_email,
                        Rules.Required(),                       // -> "validation.required"
                        Rules.Matches(EmailRx, "validation.email"));

                    // ONE prop wires border + message + touched-on-blur:
                    TextBox.Create(_email, options: new TextBox.TextBoxOptions { Header = "Email", Placeholder = "you@example.com",
                                   Width = 380f, Field = email });
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 3: THE RULE MODEL + BUILT-IN RULES ══════════════════════════════════════════════════════
                Subtitle("Built-in rules"),
                Body("Rules is a static factory of common validators. Each factory interns its message MsgId ONCE (captured " +
                     "by value in the returned delegate), so calling the rule on every keystroke allocates nothing. A field " +
                     "may take any number of rules; FirstFailing surfaces the first failing message (FieldError.First) and " +
                     "counts the rest into FailCount.").Secondary(),

                ExampleCard.Build("Rules.Required / MinLength / MaxLength / Matches / Range",
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f,
                        Children =
                        [
                            RuleLine("Rules.Required(locKey = \"validation.required\")", "Validator<string> — non-empty / non-whitespace."),
                            RuleLine("Rules.MinLength(int n, locKey = \"validation.minlen\")", "Validator<string> — at least n chars."),
                            RuleLine("Rules.MaxLength(int n, locKey = \"validation.maxlen\")", "Validator<string> — at most n chars."),
                            RuleLine("Rules.Matches(Regex rx, string locKey)", "Validator<string> — matches the pattern."),
                            RuleLine("Rules.Range(double lo, double hi, locKey = \"validation.range\")", "Validator<double> — within [lo, hi]."),
                        ],
                    },
                    description: "Build the Regex for Matches once at the call site (it is captured cold). Range treats an " +
                                 "empty (NaN) value as valid — pair it with a presence rule for required numbers. Pass a " +
                                 "custom locKey to any factory to override the default message.",
                    code: """
                    var name = UseField(_name, Rules.Required(), Rules.MaxLength(40));
                    var pwd  = UseField(_pwd,  Rules.Required(), Rules.MinLength(8));

                    static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
                    var email = UseField(_email, Rules.Matches(EmailRx, "validation.email"));

                    // numeric (Range is Validator<double>):
                    var qty = UseField(_qty, Rules.Range(1, 99, "validation.range"));
                    """),

                ExampleCard.Build("FieldError / MsgId — what a control reads",
                    new TextEl("control reads field.Error.Value → FieldError(First, FailCount); display via Msg.Resolve in a bind thunk")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "A control displays field.Error: when FailCount == 0 it is valid (no border, no text); " +
                                 "otherwise it resolves First through Msg.Resolve INSIDE a bind thunk (which subscribes to the " +
                                 "culture epoch, so the message localizes live). The field: prop on the built-in TextBox does " +
                                 "all of this for you — you only reach for FieldError directly in a custom control.",
                    code: """
                    FieldError e = field.Error.Value;      // gated by timing
                    if (!e.IsValid)
                    {
                        // resolve ONLY in a bind thunk (subscribes to the culture epoch):
                        new TextEl("") { Color = Tok.SystemFillCaution,
                                         Text = Prop.Of(() => Msg.Resolve(e.First)) };
                    }
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 4: CUSTOM RULES (the centerpiece) ═══════════════════════════════════════════════════════
                Subtitle("Custom rules"),
                Body("A rule is just Validator<T> — MsgId of (T value) — so you write your own four ways, from a throwaway " +
                     "lambda to a reusable zero-alloc factory. Nothing is generated and nothing is reflected: you hand the " +
                     "delegate to UseField exactly like a built-in. The key zero-alloc discipline for a reusable rule is to " +
                     "intern Msg.Key ONCE outside the returned closure (mirroring the built-in Rules), so the per-keystroke " +
                     "call only compares.").Secondary(),

                ExampleCard.Build("1 · An inline lambda",
                    new TextEl("Reject a single reserved value with a throwaway delegate — no ceremony.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "The simplest custom rule is a lambda matching Validator<string>: return a MsgId when the " +
                                 "value is bad, MsgId.None when it is fine. Hand it straight to UseField alongside any " +
                                 "built-ins. (Msg.Key here allocates per keystroke; for a hot field prefer form 3.)",
                    code: """
                    // Validator<string> is `MsgId (string value)`:
                    Validator<string> notReserved =
                        v => v == "admin" ? Msg.Key("validation.reserved") : MsgId.None;

                    var name = UseField(_name, Rules.Required(), notReserved);
                    """),

                ExampleCard.Build("2 · Rules.Predicate — a one-off bool test",
                    new TextEl("Wrap any Func<T,bool> as a rule; supply the message key.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "Rules.Predicate<T>(Func<T,bool> ok, string locKey) turns a boolean test into a Validator<T>. " +
                                 "It interns the key once, so it is a tidy zero-alloc way to express a one-off condition over " +
                                 "any type without writing the MsgId branch yourself.",
                    code: """
                    // Predicate<T>(Func<T,bool> ok, string locKey):
                    var age = UseField(_age, Rules.Predicate<int>(a => a >= 18, "validation.adult"));

                    // works for strings too:
                    var slug = UseField(_slug, Rules.Predicate<string>(s => s.Length is > 2 and < 32, "validation.slug"));
                    """),

                ExampleCard.Build("3 · A reusable, zero-alloc factory",
                    Embed.Comp(() => new NoSpacesDemo()),
                    description: "Mirror the built-in Rules: a static method that interns Msg.Key ONCE, then returns a delegate " +
                                 "that only reads the value. The interned message is captured by value, so the per-keystroke " +
                                 "path allocates nothing — exactly how Rules.Required is built. The live box below uses this " +
                                 "NoSpaces rule.",
                    code: """
                    public static class MyRules
                    {
                        // intern the message ONCE (outside the returned closure) — per keystroke is then zero-alloc:
                        public static Validator<string> NoSpaces(string locKey = "validation.nospaces")
                        {
                            var msg = Msg.Key(locKey);
                            return v => v?.Contains(" ") == true ? msg : MsgId.None;
                        }
                    }

                    var user = UseField(_user, Rules.Required(), MyRules.NoSpaces());
                    """),

                ExampleCard.Build("4 · Cross-field & conditional (free)",
                    new TextEl("A rule that reads a sibling signal's .Value auto-subscribes the error memo to it.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "Because Error is a memo, reading another signal's .Value inside a rule body subscribes this " +
                                 "field to that signal — so editing the sibling re-validates here with no wiring at all. " +
                                 "Rules.Equals(other, locKey) is the password↔confirm case; Rules.When(cond, inner) is the " +
                                 "required-if case (e.g. require a shipping address only when a checkbox is on).",
                    code: """
                    // Cross-field: re-checks whenever EITHER password changes.
                    var confirm = UseField(_confirm, Rules.Equals(_pwd, "validation.match"));

                    // Conditional: required only when _hasShipping is on (reads its .Value).
                    var address = UseField(_address,
                        Rules.When(() => _hasShipping.Value, Rules.Required()));

                    // Hand-rolled cross-field is just a lambda reading the sibling:
                    Validator<string> sameAsPwd =
                        v => v == _pwd.Value ? MsgId.None : Msg.Key("validation.match");
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 5: VALIDATION TIMING ════════════════════════════════════════════════════════════════════
                Subtitle("Validation timing"),
                Body("When an error first SURFACES is independent of when it is computed (the memo always knows the truth). " +
                     "ValidationTiming gates the error a control DISPLAYS. The default, OnTouched, means no red on load — a " +
                     "pristine field shows nothing until the user has been in and out of it (or a submit reveals it). Set it " +
                     "via FieldOptions.Timing.").Secondary(),

                ExampleCard.Build("The 5 ValidationTiming modes",
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f,
                        Children =
                        [
                            RuleLine("OnTouched  (default)", "Shows after the field has lost focus once — no red on load."),
                            RuleLine("OnBlur", "Shows on blur (same surfacing trigger as OnTouched in practice)."),
                            RuleLine("OnChange", "Shows immediately on every keystroke (eager)."),
                            RuleLine("OnSubmit", "Stays silent until form.Validate() flips SubmitAttempted."),
                            RuleLine("OnChangeAfterFirstError", "Silent until touched, then live on every keystroke."),
                        ],
                    },
                    description: "A server error always bypasses the gate (it must be visible even on a pristine field). A " +
                                 "failed submit (form.Validate()) reveals every gated field at once regardless of its timing.",
                    code: """
                    // eager (validate as the user types):
                    var name = UseField(_name,
                        new FieldOptions<string> { Timing = ValidationTiming.OnChange },
                        Rules.Required());

                    // hold all errors until the submit attempt:
                    var code = UseField(_code,
                        new FieldOptions<string> { Timing = ValidationTiming.OnSubmit },
                        Rules.Required(), Rules.MinLength(6));
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 6: ASYNC / SERVER VALIDATION ════════════════════════════════════════════════════════════
                Subtitle("Async / server validation"),
                Body("A server check (\"is this email taken?\") goes through FieldOptions.Async: Func<T, CancellationToken, " +
                     "Task<MsgId>>. It is debounced (AsyncDebounceMs, default 400) and cancel-stale — a single reused timer " +
                     "re-arms on each keystroke off the paint path, and an out-of-order completion cannot corrupt state " +
                     "because the result lands in an equality-gated signal the error memo merges. field.IsValidating is true " +
                     "while a check is pending (drive a spinner); a server error always displays (it bypasses the touched " +
                     "gate). SetServerError injects one imperatively after a real submit.").Secondary(),

                ExampleCard.Build("FieldOptions.Async — debounced, cancel-stale, race-immune",
                    new TextEl("Sync rules run first (free); the async check runs only after the value settles.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "Pass Async on FieldOptions; the sync rules (Required/Matches) still run synchronously on every " +
                                 "keystroke and gate the form, while the network check runs debounced. The per-keystroke UI " +
                                 "path stays allocation-free (the timer is reused, not re-created).",
                    code: """
                    var email = UseField(_email,
                        new FieldOptions<string>
                        {
                            AsyncDebounceMs = 400,
                            Async = async (v, ct) =>
                                await IsTaken(v, ct) ? Msg.Key("validation.taken") : MsgId.None,
                        },
                        Rules.Required(), Rules.Matches(EmailRx, "validation.email"));

                    // a spinner while the server check is in flight:
                    if (email.IsValidating.Value) /* show ProgressRing */;

                    // inject a server error imperatively after a real submit:
                    email.SetServerError(Msg.Key("validation.taken"));   // always shows
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 7: FORMS ════════════════════════════════════════════════════════════════════════════════
                Subtitle("Forms"),
                Body("UseForm() returns a FormScope and marks itself the form-under-construction, so the UseField calls that " +
                     "follow in the same render auto-join it (and deregister on unmount — no leak). FormScope.IsValid is a " +
                     "derived conjunction over the registered fields' UNGATED validity, so it gates a submit button truthfully " +
                     "even while errors are still hidden. form.Validate() reveals every error, focuses the first invalid field, " +
                     "and returns the overall validity.").Secondary(),

                ExampleCard.Build("UseForm + auto-join + submit gating",
                    new TextEl("One UseForm(); the following UseFields join it; the button reads form.IsValid.Value.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "Validate() flips SubmitAttempted (revealing all gated errors), walks for the first invalid " +
                                 "field, posts a focus-move to it (deferred — never a synchronous flush inside the click " +
                                 "handler), and returns validity. Read form.IsValid.Value in the submit row to auto-disable the " +
                                 "button; reading it subscribes only that row, so validity flips re-render just the button.",
                    code: """
                    var form = UseForm();   // the following UseFields auto-join it
                    var email = UseField(_email, Rules.Required(), Rules.Matches(EmailRx, "validation.email"));
                    var pwd   = UseField(_pwd,   Rules.Required(), Rules.MinLength(8));

                    // submit gated by the WHOLE form's validity; a click while invalid reveals everything:
                    Button.Accent("Create account",
                        () => { if (form.Validate()) Save(); },
                        isEnabled: form.IsValid.Value);
                    """),

                ExampleCard.Build("Nested forms — a child component owning fields",
                    new TextEl("A child component's fields join the parent form via FormScope.Context.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "Auto-join only works for UseFields in the SAME component as UseForm(). When fields live in a " +
                                 "child component, provide the scope explicitly: wrap the child in Ctx.Provide(FormScope.Context, " +
                                 "scope, child); those fields resolve it via UseContext and register the same way.",
                    code: """
                    var form = UseForm();
                    // a child that owns its own UseFields joins THIS form:
                    Ctx.Provide(FormScope.Context, form, Embed.Comp(() => new AddressSection()));

                    // inside AddressSection.Render():
                    var zip = UseField(_zip, Rules.Required());   // joins via FormScope.Context
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 8: SOURCE GENERATOR ═════════════════════════════════════════════════════════════════════
                Subtitle("Source generator"),
                Body("Validation messages ARE localization keys — so the EXISTING localization source generator already makes " +
                     "them compile-safe. With the validation.* keys in your base JSON and the generator wired (the same csproj " +
                     "AdditionalFiles + analyzer ProjectReference shown on the Localization page), Strings.Validation.Required " +
                     "/ Strings.Validation.Email are generated consts equal to the dotted key. Use them instead of the magic " +
                     "string and a renamed or mistyped key becomes a COMPILE error.").Secondary(),

                ExampleCard.Build("Typed message keys — compile-safe, refactor-safe",
                    new TextEl("Rules.Required(Strings.Validation.Required) — a renamed key fails the build, not at runtime.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "The generator emits one const per leaf of the base-culture JSON (Strings.Validation.Required " +
                                 "== \"validation.required\"). Passing it to a rule factory is identical at runtime but typo-proof " +
                                 "and find-all-references-able. See the Localization page for wiring the generator (AdditionalFiles " +
                                 "+ the analyzer ProjectReference).",
                    code: """
                    using FluentGpu.WindowsApp;   // generated: Strings.Validation.*

                    // magic string (works, but a typo is a silent runtime [key]):
                    var email = UseField(_email, Rules.Required("validation.required"));

                    // generated const (a renamed/typo'd key is a COMPILE error):
                    var safe = UseField(_email,
                        Rules.Required(Strings.Validation.Required),
                        Rules.Matches(EmailRx, Strings.Validation.Email));
                    """),

                ExampleCard.Build("Why the rule layer itself is NOT generated",
                    new TextEl("Rules are hand-written delegates by design — not attributes mined by reflection.")
                        { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "There is deliberately no [Required]/[Range] attribute generator. Attribute-driven validation " +
                                 "needs reflection (or a heavyweight generator) to discover and invoke rules — at odds with " +
                                 "NativeAOT + full trimming and the zero-alloc keystroke path. A Validator<T> delegate is " +
                                 "already the minimal, trim-safe, allocation-free form, and cross-field rules (reading a sibling " +
                                 "signal) fall out of it for free — something attribute metadata cannot express cleanly. So the " +
                                 "generator's role for validation is the MESSAGE-KEY surface, not the rules.",
                    code: """
                    // NOT this (reflection / attribute discovery — not the FluentGpu model):
                    //   [Required, EmailAddress] public string Email { get; set; }

                    // THIS — a plain delegate, AOT-clean and zero-alloc, cross-field for free:
                    var email   = UseField(_email,   Rules.Required(), Rules.Matches(EmailRx, "validation.email"));
                    var confirm = UseField(_confirm, Rules.Equals(_pwd, "validation.match"));
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 9: LIVE DEMO ════════════════════════════════════════════════════════════════════════════
                Subtitle("Live demo"),
                Body("A complete sign-up form wired with the pieces above: three controlled signals, three UseFields under one " +
                     "UseForm, the field: prop on each TextBox, and a submit row reading form.IsValid. Confirm uses a " +
                     "cross-field Rules.Equals(_pwd) — type in Password and watch Confirm re-check live. Errors stay silent " +
                     "until you leave a field; the button enables only when the whole form is valid; clicking it while invalid " +
                     "reveals every error and focuses the first.").Secondary(),

                Embed.Comp(() => new ValidationGuideDemo()),

                new BoxEl { Height = 12f },

                // ══ SECTION 10: NOTES (collapsible appendix) ════════════════════════════════════════════════════════
                Subtitle("Notes"),
                Embed.Comp(() => new Expander
                {
                    Header = "NativeAOT · zero-alloc keystroke path · i18n",
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = 10f,
                        Children =
                        [
                            NoteRow("Reflection-free / NativeAOT",
                                "Rules are plain Validator<T> delegates — no attributes, no reflection, no runtime rule " +
                                "discovery. The whole stack survives TrimMode=full + NativeAOT. This is also why there is no " +
                                "attribute/property-bag generator: a delegate is already the minimal trim-safe form."),
                            NoteRow("Zero-alloc on the keystroke path",
                                "Each Rules factory interns its message MsgId once (captured by value), so a rule call on every " +
                                "keystroke only compares and allocates nothing. FirstFailing is a plain loop (no LINQ). The async " +
                                "debounce reuses a single Timer rather than re-creating one per keystroke. Write your own reusable " +
                                "rules the same way — intern Msg.Key OUTSIDE the returned closure."),
                            NoteRow("i18n / culture-reactive messages",
                                "A MsgId holds a loc KEY, not English text. It is resolved through Loc.Get at the bound error " +
                                "TextEl, so switching culture re-resolves the visible message in place with no re-render — and " +
                                "the validation.* keys feed the localization source generator for compile-safe Strings.Validation.* " +
                                "constants. Use Msg.Literal only for already-localized or developer-facing text."),
                            NoteRow("Signals-native (no error dictionary)",
                                "Validity is a derived Memo<FieldError> over the field's value signal — there is no " +
                                "INotifyDataErrorInfo dictionary and no ErrorsChanged event. Cross-field and conditional rules " +
                                "are free: reading a sibling signal's .Value inside a rule body auto-subscribes the error memo, " +
                                "so the field re-validates whenever that sibling changes."),
                        ],
                    },
                }),
            ],
        };
    }

    // ── small doc-card helpers ──────────────────────────────────────────────────────────────────────────────────────
    static Element TypeLine(string name, string desc) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, FontFamily = "Cascadia Code", MinWidth = 150f },
            new TextEl(desc) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
        ],
    };

    static Element RuleLine(string sig, string desc) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(sig) { Size = 13f, Color = Tok.AccentDefault, FontFamily = "Cascadia Code", MinWidth = 250f, Wrap = TextWrap.Wrap },
            new TextEl(desc) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
        ],
    };

    static Element NoteRow(string heading, string body) => new BoxEl
    {
        Direction = 1, Gap = 2f,
        Children =
        [
            new TextEl(heading) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new TextEl(body) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
        ],
    };
}

/// <summary>Section 2's mini demo: a single email field showing the controlled-signal + UseField + field:-prop wiring in
/// isolation (Required + Matches, default OnTouched timing).</summary>
sealed class EmailOnlyDemo : Component
{
    static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    readonly Signal<string> _email = new("");

    public override Element Render()
    {
        var email = UseField(_email,
            Rules.Required("validation.required"),
            Rules.Matches(EmailRx, "validation.email"));

        return TextBox.Create(_email, options: new TextBox.TextBoxOptions { Header = "Email", Placeholder = "you@example.com", Width = 380f, Field = email });
    }
}

/// <summary>Section 4's mini demo: the reusable zero-alloc NoSpaces factory in action on a username field.</summary>
sealed class NoSpacesDemo : Component
{
    readonly Signal<string> _user = new("");

    // A reusable custom rule — interns the message MsgId ONCE so the per-keystroke path stays zero-alloc (mirrors Rules.*).
    static Validator<string> NoSpaces(string locKey = "validation.nospaces")
    {
        var msg = Msg.Key(locKey);
        return v => v?.Contains(" ") == true ? msg : MsgId.None;
    }

    public override Element Render()
    {
        // Reuse the shipped "match" message text for this demo (no validation.nospaces key in the sample table); the
        // factory body is the point — interning once, comparing per keystroke.
        var user = UseField(_user, Rules.Required("validation.required"), NoSpaces("validation.match"));
        return TextBox.Create(_user, options: new TextBox.TextBoxOptions { Header = "Username (no spaces)", Placeholder = "no spaces allowed", Width = 380f, Field = user });
    }
}

/// <summary>The live sign-up form for Section 9 — its OWN small form (independent of the Validation SAMPLE page's
/// ValidationDemo). Three controlled signals feed three UseFields under one UseForm; Confirm is cross-field via
/// Rules.Equals(_pwd); the submit row reads FormScope.IsValid.</summary>
sealed class ValidationGuideDemo : Component
{
    static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    readonly Signal<string> _email = new("");
    readonly Signal<string> _pwd = new("");
    readonly Signal<string> _confirm = new("");

    public override Element Render()
    {
        var form = UseForm();   // the UseFields below auto-join it

        var email = UseField(_email,
            Rules.Required("validation.required"),
            Rules.Matches(EmailRx, "validation.email"));

        var pwd = UseField(_pwd,
            Rules.Required("validation.required"),
            Rules.MinLength(8, "validation.minlen"));

        // Cross-field: the rule reads _pwd.Value, so editing EITHER password re-validates Confirm once it is touched.
        var confirm = UseField(_confirm, Rules.Equals(_pwd, "validation.match"));

        return ExampleCard.Build("Sign-up form",
            new BoxEl
            {
                Direction = 1, Gap = 14f, MaxWidth = 460f,
                Children =
                [
                    TextBox.Create(_email, options: new TextBox.TextBoxOptions { Header = "Email", Placeholder = "you@example.com", Width = 380f, Field = email }),
                    TextBox.Create(_pwd, options: new TextBox.TextBoxOptions { Header = "Password", Width = 380f, Field = pwd }),
                    TextBox.Create(_confirm, options: new TextBox.TextBoxOptions { Header = "Confirm password", Width = 380f, Field = confirm }),
                    Embed.Comp(() => new ValidationGuideSubmitRow(form)),
                ],
            },
            description: "Errors appear only after you leave a field (OnTouched — no red on load). Typing in Password " +
                         "instantly re-checks Confirm (cross-field is free). The button enables only when the whole form is " +
                         "valid; clicking it while invalid reveals every error and focuses the first.",
            code: """
            var form = UseForm();
            var email   = UseField(_email,   Rules.Required(), Rules.Matches(EmailRx, "validation.email"));
            var pwd     = UseField(_pwd,     Rules.Required(), Rules.MinLength(8));
            var confirm = UseField(_confirm, Rules.Equals(_pwd, "validation.match"));   // cross-field, free

            TextBox.Create(_email, options: new TextBox.TextBoxOptions { Header = "Email", Field = email });   // one prop wires border + message + touched

            Button.Accent("Create account",
                () => { if (form.Validate()) Save(); },
                isEnabled: form.IsValid.Value);
            """);
    }
}

/// <summary>The submit row for the live demo — a scoped component reading FormScope.IsValid (so a validity flip re-renders
/// only this row) to gate the accent button, with a success line after a valid submit.</summary>
sealed class ValidationGuideSubmitRow : Component
{
    private readonly FormScope _form;
    public ValidationGuideSubmitRow(FormScope form) => _form = form;

    public override Element Render()
    {
        bool valid = _form.IsValid.Value;                 // subscribe → re-render this row on a validity flip
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
