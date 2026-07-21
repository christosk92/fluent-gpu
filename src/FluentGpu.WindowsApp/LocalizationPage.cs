using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApp;   // generated compile-safe loc keys: Strings.App.Title, Strings.Player.Queue, …
using static FluentGpu.Dsl.Ui;
// The control kit now ships its own FluentGpu.Controls.Strings; this page uses the gallery's generated keys, so pin
// the bare `Strings` name to the app's table (G5j made the kit's Strings public → both are in-scope via usings).
using Strings = FluentGpu.WindowsApp.Strings;

// ── The "Localization" gallery page ───────────────────────────────────────────────────────────────────────────────────
// A live showcase of the FluentGpu i18n engine (src/FluentGpu.Engine/Localization): a JSON-resource, signal-backed,
// AOT-clean localization stack — the modern alternative to WinUI's RESW/ResourceManager. The page picks a language and
// every L()/Lf()-bound text re-resolves IN PLACE with no re-render (the binding-not-re-render path): each TextEl.Text is
// a Prop.Of(() => Localization.Get(key)) thunk that reads the culture epoch, so SetCulture bumps the epoch and only the
// bound text nodes re-resolve. The demos cover: dotted-key strings, named {name} interpolation, ICU plural (driven by a
// live slider — watch the form change across en one/other and pl one/few/many), ICU select (gender), and a pseudo-loc
// toggle. The language picker re-renders ONLY itself (a scoped Component reading UseLocale) to move the selection.

[GalleryPage("localization", "Localization", "App services")]
[Route("localization")]
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
            "FluentGpu.Localization is a JSON-resource, signal-backed, NativeAOT-clean i18n engine — the modern " +
            "replacement for WinUI's RESW / ResourceManager / satellite-assembly stack. Strings live in per-culture " +
            "JSON files (nested namespaces flatten to dotted keys); resolution applies a per-key fallback chain, named " +
            "{name} interpolation, an ICU plural/select subset (CLDR-lite plural rules that work even under " +
            "InvariantGlobalization), an optional source generator for compile-safe typed keys, and a pseudo-loc QA " +
            "transform. This page is both a reference and a live demo: pick a language anywhere below and every bound " +
            "text re-resolves IN PLACE with no re-render — each label is a Prop.Of(() => Loc.Get(key)) thunk that reads " +
            "the culture epoch, so SetCulture bumps the epoch and only the bound nodes re-resolve.",
            Embed.Comp(() => new LocLivePanel()));
    }

    static readonly FluentGpu.Hooks.DepKey LocMountOnce = FluentGpu.Hooks.DepKey.Empty;
}

/// <summary>
/// The live demo body, a run-once <see cref="Component"/> — its <see cref="Render"/> reads no signals directly, so it
/// renders exactly ONCE; the fact that the strings still change on a language switch PROVES the no-re-render path:
/// every dynamic label is a bound thunk (<c>L</c>/<c>Lf</c> or a <see cref="GalleryPage.LiveText"/> reading the culture
/// epoch + the count signal), not a re-rendered value. The only thing that re-renders is the inner
/// <see cref="LanguagePicker"/> (to move its selection).
/// </summary>
sealed class LocLivePanel : Component
{
    public override Element Render()
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
                // ══ SECTION 1: SETUP ════════════════════════════════════════════════════════════════════════════════
                Subtitle("Setup"),
                Body("Three steps wire an app for localization: ship per-culture JSON resources, point the engine at the " +
                     "folder, and pick the startup culture. The portable engine has no Win32, so OS-culture detection is " +
                     "host-injected via OsCultureProvider.").Secondary(),

                ControlExample.Build("1 · Author the JSON resources",
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f,
                        Children =
                        [
                            new TextEl("assets/loc/en-US.json   (base / DefaultCulture)") { Size = 13f, Color = Tok.TextPrimary, FontFamily = "Cascadia Code" },
                            new TextEl("assets/loc/fr-FR.json   (overrides en-US per key)") { Size = 13f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
                            new TextEl("assets/loc/fr.json      (parent fallback for fr-*)") { Size = 13f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
                            new TextEl("assets/loc/de-DE.json · pl-PL.json …") { Size = 13f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
                        ],
                    },
                    description: "One file per culture, named <culture>.json. Nested JSON objects flatten to dotted keys " +
                                 "(\"app\": { \"title\": … } resolves as \"app.title\"). An optional \"$culture\" key inside " +
                                 "a file self-describes it (preferred over the filename); \"$comment\" keys are metadata.",
                    code: """
                    // assets/loc/en-US.json — the base culture (also DefaultCulture)
                    {
                      "$culture": "en-US",
                      "app":     { "title": "FluentGpu Gallery", "greeting": "Hello, {name}!" },
                      "player":  { "queue": "Queue", "added": "Added “{track}” to {playlist}" },
                      "files":   { "count": "{count, plural, =0 {No files} one {# file} other {# files}}" }
                    }
                    // fr-FR.json may OMIT keys (e.g. the whole "player" namespace) — they fall back per key to fr.json -> en-US.
                    """),

                ControlExample.Build("2 · Wire OS-culture detection + the terminal fallback",
                    GalleryPage.LiveText(() => $"OS UI culture: {Localization.DetectOsCulture()}   ·   DefaultCulture: {Localization.DefaultCulture}"),
                    description: "OsCultureProvider is host-injected (the Windows app supplies GetUserDefaultLocaleName via " +
                                 "WindowsApiInterop.GetOsUiCultureName). DefaultCulture is the terminal fallback when a key " +
                                 "is missing in every other table. LoadFolder loads every *.json next to the exe; it is " +
                                 "idempotent (it replaces tables), so it is safe to call from a mount effect.",
                    code: """
                    // Once at startup (e.g. a UseEffect mount effect or App init):
                    Localization.OsCultureProvider ??= WindowsApiInterop.GetOsUiCultureName;  // GetUserDefaultLocaleName
                    Localization.DefaultCulture = "en-US";                                    // terminal fallback

                    string dir = Path.Combine(AppContext.BaseDirectory, "assets", "loc");
                    Localization.LoadFolder(dir);                                             // load every *.json
                    """),

                ControlExample.Build("3 · Pick the startup culture",
                    Embed.Comp(() => new LanguagePicker()),
                    description: "UseOsCulture() detects the OS UI culture and switches to it IF a table exists (else no-op); " +
                                 "or call SetCulture(name) directly. SetCulture is UI-thread-only — it bumps CultureEpoch, " +
                                 "which is what re-resolves every bound text below. (Selecting \"qps-ploc\" auto-enables the " +
                                 "pseudo transform.)",
                    code: """
                    Localization.UseOsCulture();        // detect + switch if we have that table…
                    // …or be explicit:
                    Localization.SetCulture("fr-FR");   // bumps CultureEpoch -> bound texts re-resolve (no re-render)
                    """,
                    output: new BoxEl
                    {
                        Direction = 1, Gap = 4f, MinWidth = 220f,
                        Children =
                        [
                            new TextEl("") { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = L(Strings.Lang.Picker) },
                            GalleryPage.LiveText(() => Strings.Lang.Current(Localization.CurrentCulture)),
                        ],
                    }),

                new BoxEl { Height = 12f },

                // ══ SECTION 2: BINDING TEXT ═════════════════════════════════════════════════════════════════════════
                Subtitle("Binding text"),
                Body("L(key) and Lf(key, args) return a Prop<string> thunk — Prop.Of(() => Loc.Get(key)) — that reads the " +
                     "culture epoch. Assign it to TextEl.Text and a language switch re-resolves ONLY that node; the " +
                     "component does not re-render. Reach for the re-rendering UseLocale() hook only when render itself " +
                     "must branch on the culture (e.g. a language picker moving its selection).").Secondary(),

                ControlExample.Build("L(key) / Lf(key, args) — reactive, no re-render",
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            new TextEl("") { Size = 18f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = L(Strings.App.Title) },
                            new TextEl("") { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Text = L(Strings.App.Subtitle) },
                            new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = Lf(Strings.App.GreetingKey, ("name", "Ada")) },
                        ],
                    },
                    description: "L resolves a plain key; Lf formats one with named args. Both subscribe to the culture " +
                                 "epoch through the thunk, so switching language above updates these in place.",
                    code: """
                    new TextEl("") { Text = L("app.title") }                        // L(key) = Prop.Of(() => Loc.Get(key))
                    new TextEl("") { Text = Lf("app.greeting", ("name", "Ada")) }   // -> "Hello, Ada!"

                    // When render must BRANCH on the culture (e.g. a picker's selected index), use the re-rendering hook:
                    var (culture, setCulture) = UseLocale();                        // re-renders THIS component on culture change
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 3: SOURCE GENERATOR ═════════════════════════════════════════════════════════════════════
                Subtitle("Source generator"),
                Body("The optional source generator reads the base-culture JSON and emits a typed Strings.* surface — " +
                     "consts equal to the dotted key (typo-proof, refactor-safe, find-all-references) plus typed format " +
                     "methods for parameterized templates. It is AOT-clean: only const string + plain methods calling " +
                     "Loc.Format — no reflection, no satellite assemblies. Every Strings.* identifier on this page is " +
                     "generator output.").Secondary(),

                ControlExample.Build("Wire the generator (csproj)",
                    new TextEl("Mark the base JSON FluentGpuLocBase=\"true\"; reference the generator as an Analyzer.") { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    description: "AdditionalFiles feeds the base culture to the generator; CompilerVisibleItemMetadata " +
                                 "surfaces the FluentGpuLocBase flag; the ProjectReference loads it as an analyzer (kept out " +
                                 "of the runtime refs). Diagnostics: FLLOC001 (base JSON parse failure), FLLOC002 (no base " +
                                 "resource marked).",
                    code: """
                    <!-- Feed the base-culture JSON to the generator -->
                    <ItemGroup>
                      <AdditionalFiles Include="assets\loc\en-US.json" FluentGpuLocBase="true" />
                    </ItemGroup>
                    <!-- Surface the custom metadata to the analyzer -->
                    <ItemGroup>
                      <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="FluentGpuLocBase" />
                    </ItemGroup>
                    <!-- Load the generator as an analyzer; keep it out of the runtime refs -->
                    <ItemGroup>
                      <ProjectReference Include="..\FluentGpu.Localization.SourceGen\FluentGpu.Localization.SourceGen.csproj"
                                        OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                    </ItemGroup>
                    """),

                ControlExample.Build("Typed keys & format methods",
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            new TextEl("") { Size = 16f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = L(Strings.Player.Queue) },
                            new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Text = Lf(Strings.App.GreetingKey, ("name", "Ada")) },
                            // The TYPED method: Strings.App.Greeting(name) -> Loc.Format("app.greeting", ("name", name)).
                            GalleryPage.LiveText(() => Strings.App.Greeting("Grace")),
                        ],
                    },
                    description: "A plain leaf emits a const (Strings.Player.Queue == \"player.queue\"). A parameterized leaf " +
                                 "emits a <Name>Key const PLUS a typed method that calls Loc.Format — e.g. " +
                                 "Strings.App.Greeting(object name). The typed method makes a missing or wrong-named argument " +
                                 "a COMPILE error instead of a runtime {name} placeholder.",
                    code: """
                    using FluentGpu.WindowsApp;   // generated: Strings.*

                    // 1) Plain leaf -> const == the dotted key (typo-proof, refactor-safe):
                    new TextEl("") { Text = L(Strings.Player.Queue) }            // const "player.queue"

                    // 2) Parameterized leaf -> <Name>Key const + a typed Format method:
                    new TextEl("") { Text = Lf(Strings.App.GreetingKey, ("name", "Ada")) }   // explicit args
                    new TextEl("") { Text = Strings.App.Greeting("Ada") }                     // typed -> Loc.Format("app.greeting", ("name","Ada"))

                    // GENERATED SHAPE (emitted into your project's namespace):
                    public static partial class Strings {
                      public static class App {
                        public const string Title = "app.title";
                        public const string GreetingKey = "app.greeting";
                        public static string Greeting(object name) => Loc.Format("app.greeting", ("name", name));
                      }
                      public static class Player { public const string Queue = "player.queue"; /* … */ }
                    }
                    """),

                new BoxEl { Height = 12f },

                // ══ SECTION 4: MESSAGE FEATURES (live demos) ════════════════════════════════════════════════════════
                Subtitle("Message features"),
                Body("The formatter supports named {name} interpolation and an ICU plural/select subset. The demos below " +
                     "are live — switch language above and watch the forms change.").Secondary(),

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
                                    Slider.Create(count, length: 220f, options: new Slider.SliderOptions { Header = "count (0–30)" }),
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

                new BoxEl { Height = 12f },

                // ══ SECTION 5: NOTES (collapsible appendix) ═════════════════════════════════════════════════════════
                Subtitle("Notes"),
                Embed.Comp(() => new Expander
                {
                    Header = "Fallback chain · hot-reload · AOT / InvariantGlobalization",
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = 10f,
                        Children =
                        [
                            NoteRow("Fallback chain",
                                "Resolution is PER KEY: active culture → its parent (region stripped, fr-FR → fr) → " +
                                "DefaultCulture → DefaultCulture's parent. A key absent from fr-FR.json but present in " +
                                "fr.json falls through; only when it is missing in EVERY table does it render the visible " +
                                "[key] sentinel. Culture names normalize _→- and compare case-insensitively."),
                            NoteRow("Pseudo-localization",
                                "SetCulture(\"qps-ploc\") (or Localization.PseudoLocalize = true) accents every ASCII letter, " +
                                "brackets the string with ⟦…⟧, and pads it ~+40% with ·, so un-externalized literals and " +
                                "layout overflow jump out. {…} placeholders and ICU spans are never touched."),
                            NoteRow("Hot-reload (dev only)",
                                "new LocalizationWatcher(dir, HostDispatch.Post).Start() watches the JSON folder and reloads " +
                                "via LoadFolder on save (150 ms debounce, marshalled to the UI thread). No re-render — the " +
                                "bound thunks re-resolve. Gate it behind a dev opt-in; never ship it."),
                            NoteRow("AOT / InvariantGlobalization",
                                "Cultures are keyed by NAME string, never CultureInfo (none exists under " +
                                "InvariantGlobalization=true). Plural categories come from a hand-rolled CLDR-lite PluralRules " +
                                "table (en/fr/de/pl). The source generator emits only const string + Loc.Format calls — zero " +
                                "reflection, zero satellite assemblies — so the whole stack survives full trimming + NativeAOT."),
                        ],
                    },
                }),
            ],
        };
    }

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

        var sel = UseSignal(selected);
        return RadioButtons.Create(labels, sel, onChange: i =>
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
        var on = UseSignal(Localization.PseudoLocalize);
        return ToggleSwitch.Create(on, onChange: v => Localization.PseudoLocalize = v,
            header: null, onContent: "On", offContent: "Off");
    }
}
