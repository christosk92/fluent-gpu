using System;
using System.IO;
using FluentGpu.Localization;

/// <summary>
/// Headless validation harness for the localization (i18n) engine — the <c>--loc-probe</c> console mode (routed in
/// <c>Program.Main</c> before any window/GPU spins up, alongside <c>--windowsapi-smoke</c> / <c>--packaging-probe</c>).
/// It loads the sample JSON resources and asserts <c>[PASS]</c>/<c>[FAIL]</c> on every engine feature, exiting with the
/// failure count (0 = all green). No D3D, no window — pure engine.
///
/// Coverage (one assertion family per mission requirement):
/// dotted-key load · named <c>{name}</c> interpolation · ICU plural across en (one/other/=0) AND pl (one/few/many,
/// proving per-language rules) · ICU select (gender) with nested placeholders · per-key parent fallback
/// (fr-FR missing → fr) · missing-key visible form (<c>[key]</c>) · missing-arg visible form (<c>{name}</c>) ·
/// pseudo-localization transform · live <see cref="Localization.SetCulture"/> re-resolution · OS-detected culture
/// non-empty (via the wired <c>GetUserDefaultLocaleName</c> provider).
/// </summary>
internal static class LocProbe
{
    private static int s_pass, s_fail;

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected console may reject this */ }
        Console.WriteLine("== FluentGpu localization probe ==");

        // Wire the OS-culture provider exactly as the live app does (engine stays Win32-free; the app injects it).
        Localization.OsCultureProvider = WindowsApiInterop.GetOsUiCultureName;

        // Locate the sample JSON: <exe dir>/assets/loc (copied by the csproj Content glob). Allow an override arg.
        string dir = ResolveLocDir(args);
        Console.WriteLine($"loc dir: {dir}");
        Localization.Clear();
        Localization.DefaultCulture = "en-US";
        Localization.LoadFolder(dir);

        // 1) Dotted-key load — nested JSON object flattened to "app.title".
        Localization.SetCulture("en-US");
        Check("dotted-key load", Localization.Get("app.title") == "FluentGpu Gallery", Localization.Get("app.title"));
        Check("dotted-key nested namespace", Localization.Get("player.queue") == "Queue", Localization.Get("player.queue"));

        // 2) Named {name} interpolation (NOT positional).
        Check("named-arg interpolation", Localization.Format("app.greeting", ("name", "World")) == "Hello, World!",
              Localization.Format("app.greeting", ("name", "World")));
        Check("named-arg reorder/value", Localization.Format("player.added", ("track", "Hey Jude"), ("playlist", "Favorites"))
              == "Added “Hey Jude” to Favorites",
              Localization.Format("player.added", ("track", "Hey Jude"), ("playlist", "Favorites")));

        // 3a) ICU plural — English one/other plus the =0 exact-match branch (wins over the category).
        Check("plural en =0", Localization.Format("files.count", ("count", 0)) == "No files", Localization.Format("files.count", ("count", 0)));
        Check("plural en one (# subst)", Localization.Format("files.count", ("count", 1)) == "1 file", Localization.Format("files.count", ("count", 1)));
        Check("plural en other (# subst)", Localization.Format("files.count", ("count", 5)) == "5 files", Localization.Format("files.count", ("count", 5)));

        // 3b) ICU plural — Polish one/few/many, the four-form Slavic proof of per-language rules.
        Localization.SetCulture("pl-PL");
        Check("plural pl one", Localization.Format("files.count", ("count", 1)) == "1 plik", Localization.Format("files.count", ("count", 1)));
        Check("plural pl few (2)", Localization.Format("files.count", ("count", 2)) == "2 pliki", Localization.Format("files.count", ("count", 2)));
        Check("plural pl few (23)", Localization.Format("files.count", ("count", 23)) == "23 pliki", Localization.Format("files.count", ("count", 23)));
        Check("plural pl many (5)", Localization.Format("files.count", ("count", 5)) == "5 plików", Localization.Format("files.count", ("count", 5)));
        Check("plural pl many (12)", Localization.Format("files.count", ("count", 12)) == "12 plików", Localization.Format("files.count", ("count", 12)));
        Check("plural pl =0", Localization.Format("files.count", ("count", 0)) == "Brak plików", Localization.Format("files.count", ("count", 0)));

        // 3b-ii) =N exact match wins over the category EVEN WHEN written AFTER it (ICU order-independence). Here the
        // 'one' category branch precedes '=1'; the engine must still pick '=1' for count==1.
        Localization.AddStrings("en-US", new System.Collections.Generic.Dictionary<string, string>
        {
            ["probe.exactAfter"] = "{count, plural, one {# generic} =1 {exactly one} other {# many}}",
        });
        Localization.SetCulture("en-US");
        Check("plural exact-after-category priority", Localization.Format("probe.exactAfter", ("count", 1)) == "exactly one",
              Localization.Format("probe.exactAfter", ("count", 1)));

        // 3c) Nested {name} inside a plural branch body (recursion).
        Localization.SetCulture("en-US");
        Check("plural nested placeholder", Localization.Format("player.songsBy", ("count", 2), ("artist", "Adele")) == "2 songs by Adele",
              Localization.Format("player.songsBy", ("count", 2), ("artist", "Adele")));

        // 4) ICU select (gender) with a nested {name}.
        Check("select male", Localization.Format("profile.invited", ("gender", "male"), ("name", "Sam")) == "He invited Sam",
              Localization.Format("profile.invited", ("gender", "male"), ("name", "Sam")));
        Check("select female", Localization.Format("profile.invited", ("gender", "female"), ("name", "Sam")) == "She invited Sam",
              Localization.Format("profile.invited", ("gender", "female"), ("name", "Sam")));
        Check("select other fallback", Localization.Format("profile.invited", ("gender", "nonbinary"), ("name", "Sam")) == "They invited Sam",
              Localization.Format("profile.invited", ("gender", "nonbinary"), ("name", "Sam")));

        // 5) Per-key parent fallback: fr-FR omits app.subtitle and the whole player.* namespace → resolves via fr.json.
        Localization.SetCulture("fr-FR");
        Check("fallback fr-FR own key", Localization.Get("app.title") == "Galerie FluentGpu", Localization.Get("app.title"));
        Check("fallback fr-FR -> fr (subtitle)", Localization.Get("app.subtitle") == "Un moteur de localisation moderne basé sur JSON",
              Localization.Get("app.subtitle"));
        Check("fallback fr-FR -> fr (player ns)", Localization.Get("player.queue") == "File d’attente", Localization.Get("player.queue"));
        // French plural: 0 and 1 are 'one'.
        Check("plural fr one (0)", Localization.Format("files.count", ("count", 0)) == "Aucun fichier", Localization.Format("files.count", ("count", 0)));
        Check("plural fr one (1)", Localization.Format("files.count", ("count", 1)) == "1 fichier", Localization.Format("files.count", ("count", 1)));
        Check("plural fr other (2)", Localization.Format("files.count", ("count", 2)) == "2 fichiers", Localization.Format("files.count", ("count", 2)));

        // 6) Missing key renders visibly as [key]; missing arg renders visibly as {name}.
        Localization.SetCulture("en-US");
        Check("missing key visible", Localization.Get("does.not.exist") == "[does.not.exist]", Localization.Get("does.not.exist"));
        Check("missing arg visible", Localization.Format("app.greeting") == "Hello, {name}!", Localization.Format("app.greeting"));

        // 7) Pseudo-localization transform: accents + bracket/expand, placeholders preserved.
        Localization.SetCulture(PseudoLocalizer.PseudoCulture);   // auto-enables pseudo, base strings resolve via en-US chain... but qps has no table -> en-US
        // qps-ploc has no table, so it falls back to default en-US, then the transform is applied.
        string pseudo = Localization.Get("app.title");
        Check("pseudo brackets", pseudo.StartsWith("⟦") && pseudo.EndsWith("⟧"), pseudo);
        Check("pseudo accented (no ASCII vowel a/e/o)", !ContainsAsciiLetter(pseudo.Trim('⟦', '⟧', '·')), pseudo);
        Check("pseudo expands length", pseudo.Length > "FluentGpu Gallery".Length, $"len={pseudo.Length}");
        // Pseudo runs AFTER interpolation, so the substituted value "World" is itself accented (W->Ŵ, o->ö, ...). The
        // meaningful assertion: the placeholder was substituted (no literal "{name}" survives) and the accented value is
        // present (proving interpolation happened before the transform, not that the transform skipped the value).
        string pseudoFmt = Localization.Format("app.greeting", ("name", "World"));
        Check("pseudo interpolates then accents value", !pseudoFmt.Contains("{name}") && pseudoFmt.Contains(PseudoLocalizer.Transform("World").Trim('⟦', '⟧', '·')),
              pseudoFmt);
        Localization.PseudoLocalize = false;
        Localization.SetCulture("en-US");

        // 8) Live SetCulture re-resolution: a thunk reading Get reflects the new culture immediately (the bound-node path).
        Func<string> liveTitle = () => Localization.Get("app.title");
        Localization.SetCulture("en-US");
        string before = liveTitle();
        Localization.SetCulture("de-DE");
        string after = liveTitle();
        Check("live SetCulture re-resolution", before == "FluentGpu Gallery" && after == "FluentGpu-Galerie", $"{before} -> {after}");
        // Epoch bumped so a bound text node's effect re-runs.
        int epochBefore = Localization.CultureEpoch.Peek();
        Localization.SetCulture("fr-FR");
        Check("culture epoch bumps on switch", Localization.CultureEpoch.Peek() > epochBefore,
              $"{epochBefore} -> {Localization.CultureEpoch.Peek()}");

        // 9) OS-detected culture non-empty (via GetUserDefaultLocaleName).
        string os = Localization.DetectOsCulture();
        Check("OS culture non-empty", !string.IsNullOrEmpty(os), os);

        Localization.SetCulture("en-US");
        Console.WriteLine($"== {s_pass} passed, {s_fail} failed ==");
        return s_fail;
    }

    private static string ResolveLocDir(string[] args)
    {
        int i = Array.IndexOf(args, "--loc-dir");
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "assets", "loc");
        return candidate;
    }

    private static bool ContainsAsciiLetter(string s)
    {
        foreach (char c in s)
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
        return false;
    }

    private static void Check(string name, bool ok, string actual)
    {
        if (ok) { s_pass++; Console.WriteLine($"[PASS] {name}"); }
        else { s_fail++; Console.WriteLine($"[FAIL] {name}  (got: {actual})"); }
    }
}
