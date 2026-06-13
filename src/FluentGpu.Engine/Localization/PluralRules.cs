namespace FluentGpu.Localization;

/// <summary>The CLDR plural categories. A given language uses a subset; the rest collapse onto <see cref="Other"/>.
/// These are exactly the ICU MessageFormat plural keywords (the <c>zero/one/two/few/many/other</c> set) plus the
/// explicit <c>=N</c> form, which is handled separately as a literal match in <see cref="MessageFormatter"/>.</summary>
public enum PluralCategory
{
    /// <summary>The catch-all category every language defines (and the default for any unlisted language).</summary>
    Other = 0,
    Zero,
    One,
    Two,
    Few,
    Many,
}

/// <summary>
/// A hand-rolled, CLDR-lite plural-category selector — the engine's answer to "which plural form does <c>n</c> take in
/// language X?". This table is <b>mandatory and load-bearing</b> under <c>InvariantGlobalization=true</c>: with
/// invariant globalization the BCL exposes no per-culture <c>CultureInfo</c>/<c>PluralRules</c> data, so the ICU
/// <c>{n, plural, …}</c> selector CANNOT defer to the framework — the rules must be coded here. The set is honestly
/// scoped: English, French, German, and Polish are implemented from the CLDR rules (the marquee proof that the rule
/// set is real and per-language, including a four-form Slavic language); every other language falls back to
/// <see cref="PluralCategory.Other"/> (i.e. only <c>=N</c>/<c>other</c> branches fire), which is correct-but-coarse and
/// the documented extension point — adding a language is adding one <c>case</c> here.
///
/// <para><b>Source.</b> Rules transcribed from the Unicode CLDR "Language Plural Rules" chart
/// (<c>cldr/common/supplemental/plurals.xml</c>), the same data ICU4C/ICU4J and i18next's pluralization tables derive
/// from. Only the integer-<c>n</c> path is implemented (operand <c>n</c> = the absolute value; <c>i</c> = integer
/// digits; <c>v</c> = visible fraction digit count = 0 here since we format whole counts) — fractional plural operands
/// (<c>f</c>/<c>t</c>) are out of scope because localized UI counts are integers.</para>
/// </summary>
public static class PluralRules
{
    /// <summary>Select the CLDR plural category for integer <paramref name="n"/> in the language identified by
    /// <paramref name="culture"/> (a BCP-47 name like <c>"fr-FR"</c> or <c>"pl"</c>; matched on the primary subtag).
    /// Unlisted languages ⇒ <see cref="PluralCategory.Other"/>.</summary>
    public static PluralCategory Select(string? culture, long n)
    {
        long abs = n < 0 ? -n : n;
        return PrimaryLanguage(culture) switch
        {
            "en" => English(abs),
            "fr" => French(abs),
            "de" => Germanic(abs),
            "pl" => Polish(abs),
            _ => PluralCategory.Other,
        };
    }

    /// <summary>The primary language subtag, lower-cased: <c>"fr-FR" → "fr"</c>, <c>"pl" → "pl"</c>. Pure string work
    /// (no <c>CultureInfo</c>) so it is invariant-globalization-safe.</summary>
    private static string PrimaryLanguage(string? culture)
    {
        if (string.IsNullOrEmpty(culture)) return string.Empty;
        int dash = culture.IndexOf('-');
        string primary = dash < 0 ? culture : culture.Substring(0, dash);
        return primary.ToLowerInvariant();
    }

    // ── Per-language CLDR rules (integer n) ──────────────────────────────────────────────────────────────────────────

    /// <summary>English (and the many <c>one/other</c>-only languages): <c>n == 1 → one</c>, else <c>other</c>.
    /// CLDR: <c>one: i = 1 and v = 0</c>.</summary>
    private static PluralCategory English(long n) => n == 1 ? PluralCategory.One : PluralCategory.Other;

    /// <summary>French: <c>n ∈ {0, 1} → one</c>, else <c>other</c> (French treats 0 as singular: "0 jour", "1 jour",
    /// "2 jours"). CLDR: <c>one: i = 0 or i = 1</c>.</summary>
    private static PluralCategory French(long n) => (n == 0 || n == 1) ? PluralCategory.One : PluralCategory.Other;

    /// <summary>German (same shape as English — proves the table is per-language even when two languages share a rule):
    /// <c>n == 1 → one</c>, else <c>other</c>. CLDR: <c>one: i = 1 and v = 0</c>.</summary>
    private static PluralCategory Germanic(long n) => n == 1 ? PluralCategory.One : PluralCategory.Other;

    /// <summary>Polish — a four-form Slavic rule, the proof that the category machinery is genuinely multi-form (not an
    /// English-shaped stub). CLDR:
    /// <list type="bullet">
    /// <item><c>one: i = 1 and v = 0</c></item>
    /// <item><c>few: v = 0 and i % 10 = 2..4 and i % 100 != 12..14</c></item>
    /// <item><c>many: v = 0 and i != 1 and (i % 10 = 0..1 or i % 10 = 5..9 or i % 100 = 12..14)</c></item>
    /// <item><c>other</c> (the fractional residue — unreachable for integer counts, so integer Polish is one/few/many).</item>
    /// </list>
    /// e.g. 1 → one ("1 plik"); 2,3,4,22,23,24 → few ("2 pliki"); 0,5..21,25.. → many ("5 plików").</summary>
    private static PluralCategory Polish(long n)
    {
        long i10 = n % 10;
        long i100 = n % 100;
        if (n == 1) return PluralCategory.One;
        if (i10 >= 2 && i10 <= 4 && !(i100 >= 12 && i100 <= 14)) return PluralCategory.Few;
        // i != 1, and (i%10 ∈ {0,1} or i%10 ∈ 5..9 or i%100 ∈ 12..14)
        if (i10 == 0 || i10 == 1 || (i10 >= 5 && i10 <= 9) || (i100 >= 12 && i100 <= 14)) return PluralCategory.Many;
        return PluralCategory.Other;
    }

    /// <summary>Map a CLDR <c>plural</c> keyword token (as written in the message body: <c>zero/one/two/few/many/other</c>)
    /// to its <see cref="PluralCategory"/>. Unknown tokens ⇒ <see langword="null"/> (the parser treats them as a literal
    /// selector mismatch). Case-insensitive on the ASCII keyword.</summary>
    public static PluralCategory? KeywordToCategory(string keyword) => keyword switch
    {
        "other" => PluralCategory.Other,
        "zero" => PluralCategory.Zero,
        "one" => PluralCategory.One,
        "two" => PluralCategory.Two,
        "few" => PluralCategory.Few,
        "many" => PluralCategory.Many,
        _ => null,
    };
}
