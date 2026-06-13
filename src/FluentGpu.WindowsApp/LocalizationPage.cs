using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApp;   // generated compile-safe loc keys: Strings.App.Title, Strings.Player.Queue, …
using static FluentGpu.Dsl.Ui;

// ── The "Localization" gallery page ───────────────────────────────────────────────────────────────────────────────────
// A live showcase of the FluentGpu i18n engine (src/FluentGpu.Engine/Localization): a JSON-resource, signal-backed,
// AOT-clean localization stack — the modern alternative to WinUI's RESW/ResourceManager. The page picks a language and
// every L()/Lf()-bound text re-resolves IN PLACE with no re-render (the binding-not-re-render path): each TextEl.Text is
// a Prop.Of(() => Localization.Get(key)) thunk that reads the culture epoch, so SetCulture bumps the epoch and only the
// bound text nodes re-resolve. The demos cover: dotted-key strings, named {name} interpolation, ICU plural (driven by a
// live slider — watch the form change across en one/other and pl one/few/many), ICU select (gender), and a pseudo-loc
// toggle. The language picker re-renders ONLY itself (a scoped Component reading UseLocale) to move the selection.

sealed class LocalizationPage : Component
{
    // Cultures offered by the picker (must have a loaded JSON table). qps-ploc is the pseudo dev-locale.
    static readonly (string Culture, string Label)[] Languages =
    {
        ("en-US", "English (en-US)"),
        ("fr-FR", "Français (fr-FR)"),
        ("de-DE", "Deutsch (de-DE)"),
        ("pl-PL", "Polski (pl-PL)"),
        ("qps-ploc", "Pseudo (qps-ploc)"),
    };

    public override Element Render()
    {
        // Load the sample resources once per process (idempotent: LoadFolder replaces tables). The folder ships next to
        // the exe via the csproj assets\** Content glob; under --screenshot the working dir is the build output.
        UseEffect(() =>
        {
            Localization.OsCultureProvider ??= WindowsApiInterop.GetOsUiCultureName;
            Localization.DefaultCulture = "en-US";
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "loc");
            Localization.LoadFolder(dir);
            // Screenshot/deep-link override: FLUENTGPU_LOC_CULTURE lets the shot harness render a specific language
            // (e.g. pl-PL to capture the four-form plural) without clicking. Falls back to en-US.
            string? forced = Environment.GetEnvironmentVariable("FLUENTGPU_LOC_CULTURE");
            if (!string.IsNullOrEmpty(forced)) Localization.SetCulture(forced);
            else if (string.IsNullOrEmpty(Localization.CurrentCulture) || !Localization.Has(Strings.App.Title))
                Localization.SetCulture("en-US");
        }, LocMountOnce);

        return GalleryPage.ShellKeyed("localization", "Localization",
            "FluentGpu.Localization — a JSON-resource, signal-backed i18n engine. Strings live in per-culture JSON " +
            "(nested namespaces flatten to dotted keys); resolution applies a per-key fallback chain, named {name} " +
            "interpolation, an ICU plural/select subset (CLDR-lite plural rules, mandatory under InvariantGlobalization), " +
            "and an optional pseudo-localization QA transform. Pick a language: every bound text below re-resolves in " +
            "place with NO re-render — each label is a Prop.Of(() => Loc.Get(key)) thunk over the culture epoch.",
            Embed.Comp(() => new LocLivePanel()));
    }

    static readonly object[] LocMountOnce = new object[] { "loc-mount" };
}

/// <summary>
/// The live demo body, a <see cref="ReactiveComponent"/> — its <see cref="Setup"/> runs ONCE, so the fact that the
/// strings still change on a language switch PROVES the no-re-render path: every dynamic label is a bound thunk
/// (<c>L</c>/<c>Lf</c> or a <see cref="GalleryPage.LiveText"/> reading the culture epoch + the count signal), not a
/// re-rendered value. The only thing that re-renders is the inner <see cref="LanguagePicker"/> (to move its selection).
/// </summary>
sealed class LocLivePanel : ReactiveComponent
{
    public override Element Setup()
    {
        // The plural-demo count, a hot-path scalar bound straight into the count thunks (no re-render on scrub). The
        // engine Slider rides a normalized 0..1 signal; we map it to an integer 0..30 count for the plural operand.
        const int MaxCount = 30;
        var count = UseFloatSignal(2f / MaxCount);   // start at count = 2 (pl 'few', en 'other')

        // Resolve helpers: read the culture epoch (via Loc.Get/Format) AND the count signal inside a thunk → the text
        // node re-resolves when EITHER changes. Count is rounded to an int for the plural operand.
        int Count() => (int)MathF.Round(count.Value * MaxCount);

        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                // ── Language picker + active-culture readout ──────────────────────────────────────────────────────
                ControlExample.Build("Language",
                    Embed.Comp(() => new LanguagePicker()),
                    description: "RadioButtons bound to Localization.SetCulture. Selecting a language bumps the culture " +
                                 "epoch; the bound texts below re-resolve with no page re-render.",
                    output: new BoxEl
                    {
                        Direction = 1, Gap = 4f, MinWidth = 220f,
                        Children =
                        [
                            // L()-bound: re-resolves on culture change, no re-render.
                            new TextEl("") { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = L(Strings.Lang.Picker) },
                            GalleryPage.LiveText(() => Strings.Lang.Current(Localization.CurrentCulture)),
                        ],
                    }),

                // ── Dotted-key strings ────────────────────────────────────────────────────────────────────────────
                ControlExample.Build("Dotted-key strings",
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            new TextEl("") { Size = 18f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = L(Strings.App.Title) },
                            new TextEl("") { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Text = L(Strings.App.Subtitle) },
                            new TextEl("") { Size = 13f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Text = L(Strings.Player.Queue) },
                        ],
                    },
                    description: "Nested JSON objects (\"app\": { \"title\": … }) flatten to \"app.title\". " +
                                 "fr-FR omits app.subtitle + the player.* namespace → they fall back per-key to fr.json.",
                    code: """
                    new TextEl("") { Text = L("app.title") }      // L(key) = Prop.Of(() => Loc.Get(key))
                    new TextEl("") { Text = L("app.subtitle") }   // fr-FR -> fr fallback
                    """),

                // ── Named placeholders ────────────────────────────────────────────────────────────────────────────
                ControlExample.Build("Named placeholders {name}",
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = Lf(Strings.App.GreetingKey, ("name", "Ada")) },
                            new TextEl("") { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Text = Lf(Strings.Player.AddedKey, ("track", "Clair de Lune"), ("playlist", "Focus")) },
                        ],
                    },
                    description: "Arguments bind by NAME (not positional {0}) so translators see meaning and may reorder. " +
                                 "A missing argument renders visibly as {name}, never throws.",
                    code: """
                    Text = Lf("app.greeting", ("name", "Ada"))   // "Hello, Ada!"
                    """),

                // ── ICU plural (live count) ───────────────────────────────────────────────────────────────────────
                ControlExample.Build("ICU plural {count, plural, …}",
                    new BoxEl
                    {
                        Direction = 1, Gap = 10f,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center,
                                Children =
                                [
                                    Slider.Bind(count, width: 220f, header: "count (0–30)"),
                                    GalleryPage.LiveText(() => $"count = {Count()}"),
                                ],
                            },
                            // The headline: the plural FORM changes with the count AND the language (en one/other,
                            // pl one/few/many). Bound thunk reads both the epoch and the count signal.
                            new TextEl("") { Size = 18f, Weight = 600, Color = Tok.AccentDefault, Wrap = TextWrap.Wrap,
                                             Text = Prop.Of(() => Strings.Files.Count(Count())) },
                            new TextEl("") { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                                             Text = Prop.Of(() => Strings.Player.Songs(Count())) },
                            new TextEl("") { Size = 13f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
                                             Text = Prop.Of(() => Strings.Player.SongsBy(Count(), "Satie")) },
                        ],
                    },
                    description: "Plural categories come from a hand-rolled CLDR-lite table (mandatory under " +
                                 "InvariantGlobalization). Try Polish: 1 → \"plik\", 2/3/4 → \"pliki\", 5+ → \"plików\". " +
                                 "An =0 branch wins over the category; # is the count.",
                    code: """
                    "files.count": "{count, plural, =0 {No files} one {# file} other {# files}}"   // en-US
                    "files.count": "{count, plural, =0 {Brak plików} one {# plik} few {# pliki} many {# plików} other {…}}"  // pl-PL
                    Text = Prop.Of(() => Loc.Format("files.count", ("count", n)))
                    """),

                // ── ICU select (gender) ───────────────────────────────────────────────────────────────────────────
                ControlExample.Build("ICU select {gender, select, …}",
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = Lf(Strings.Profile.InvitedKey, ("gender", "female"), ("name", "Mira")) },
                            new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = Lf(Strings.Profile.InvitedKey, ("gender", "male"), ("name", "Theo")) },
                            new TextEl("") { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Text = Lf(Strings.Profile.InvitedKey, ("gender", "other"), ("name", "the team")) },
                        ],
                    },
                    description: "select chooses a branch by an argument's value (gender here), each branch a full " +
                                 "message that may nest {name}. 'other' is the required fallback.",
                    code: """
                    "profile.invited": "{gender, select, male {He invited {name}} female {She invited {name}} other {They invited {name}}}"
                    """),

                // ── Pseudo-localization toggle ────────────────────────────────────────────────────────────────────
                ControlExample.Build("Pseudo-localization",
                    Embed.Comp(() => new PseudoToggle()),
                    description: "A dev QA transform RESW lacks: accents every letter (á-é-ö …) and expands ~+40% with " +
                                 "brackets ⟦…⟧, so un-localized literals and layout overflow jump out. Placeholders are " +
                                 "preserved. (Selecting the qps-ploc language enables it automatically.)",
                    output: new BoxEl
                    {
                        Direction = 1, Gap = 4f, MinWidth = 220f,
                        Children =
                        [
                            new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = L(Strings.App.Title) },
                            new TextEl("") { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Text = Lf(Strings.App.GreetingKey, ("name", "World")) },
                        ],
                    }),
            ],
        };
    }
}

/// <summary>The language RadioButtons — a scoped <see cref="Component"/> that re-renders ONLY itself on a culture change
/// (it reads <see cref="RenderContext.UseLocale"/> to move the selection), leaving the L()-bound demo texts to update
/// via their bindings with no re-render.</summary>
sealed class LanguagePicker : Component
{
    public override Element Render()
    {
        var (culture, setCulture) = UseLocale();   // subscribe: re-render this picker when the culture changes
        int selected = IndexOf(culture);
        var labels = new string[LocalizationPage_Languages.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = LocalizationPage_Languages[i].Label;

        return RadioButtons.Create(labels, selected, i =>
        {
            if ((uint)i < (uint)LocalizationPage_Languages.Length) setCulture(LocalizationPage_Languages[i].Culture);
        }, header: null);
    }

    static int IndexOf(string culture)
    {
        for (int i = 0; i < LocalizationPage_Languages.Length; i++)
            if (string.Equals(LocalizationPage_Languages[i].Culture, culture, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // Mirror of LocalizationPage.Languages (that one is private to its page type).
    public static readonly (string Culture, string Label)[] LocalizationPage_Languages =
    {
        ("en-US", "English (en-US)"),
        ("fr-FR", "Français (fr-FR)"),
        ("de-DE", "Deutsch (de-DE)"),
        ("pl-PL", "Polski (pl-PL)"),
        ("qps-ploc", "Pseudo (qps-ploc)"),
    };
}

/// <summary>The pseudo-localization on/off switch — a scoped <see cref="Component"/> reading the culture epoch so its
/// ToggleSwitch reflects the live <see cref="Localization.PseudoLocalize"/> state (which the qps-ploc language also
/// flips).</summary>
sealed class PseudoToggle : Component
{
    public override Element Render()
    {
        _ = Localization.CultureEpoch.Value;   // reflect auto-enable when qps-ploc is selected
        bool on = Localization.PseudoLocalize;
        return ToggleSwitch.Create(on, () => Localization.PseudoLocalize = !on,
            header: null, onContent: "On", offContent: "Off");
    }
}
