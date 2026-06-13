using System.Text;

namespace FluentGpu.Localization;

/// <summary>
/// Pseudo-localization — a professional i18n QA transform RESW lacks. When enabled (a dev/opt-in mode, typically the
/// <c>qps-ploc</c> culture), every resolved string is rewritten so that:
/// <list type="number">
/// <item><b>each ASCII letter gains a diacritic</b> (<c>a→á</c>, <c>e→ě</c>, <c>R→Ŕ</c> …) — the text stays readable
/// but is visibly "not English", so any string that is NOT pseudo-localized is instantly spotted as a hard-coded /
/// un-externalized literal that escaped <see cref="Localization.Get"/>;</item>
/// <item><b>the string is padded ~+40% and bracketed</b> (<c>⟦…⟧</c> + filler) — surfacing layout overflow / clipping /
/// truncation BEFORE a real translation (German, Finnish) is in hand, since many languages run substantially longer
/// than English.</item>
/// </list>
///
/// <para><b>Placeholder-safe.</b> The transform must never corrupt the message grammar, so it does NOT touch anything
/// inside <c>{ … }</c> — named placeholders (<c>{name}</c>), plural/select blocks (<c>{n, plural, …}</c>), and the
/// <c>#</c> count token pass through verbatim; only the literal letters between braces are accented. It runs at the
/// FINAL resolve step (after interpolation/plural selection produce the surface string), so brace-spans in the OUTPUT
/// are already substituted away — but we still guard braces defensively for the rare literal-brace case and so it is
/// safe to apply to a raw template too. Whitespace and punctuation are preserved (only letters map), keeping word
/// boundaries intact for the reviewer.</para>
///
/// <para>This is the i18next <c>pseudo</c> / WinUI <c>qps-ploc</c> convention, hand-rolled and AOT-clean (a fixed
/// transliteration table + a StringBuilder, no <c>CultureInfo</c>, no reflection).</para>
/// </summary>
public static class PseudoLocalizer
{
    /// <summary>The conventional BCP-47 pseudo-locale tag (Windows' "qps" = pseudo, "ploc" = pseudo-localized). Selecting
    /// this culture via <see cref="Localization.SetCulture"/> auto-enables the transform.</summary>
    public const string PseudoCulture = "qps-ploc";

    // Open/close brackets that make the start+end of every string obvious (a truncation that eats the close bracket is
    // immediately visible). U+27E6/U+27E7 MATHEMATICAL WHITE SQUARE BRACKETS.
    private const string Open = "⟦";
    private const string Close = "⟧";

    // Filler appended to reach the expansion target — a run of these makes over-expansion visible without looking like
    // real words. The count is computed from the source length to hit ~+40%.
    private const char Filler = '·';   // MIDDLE DOT
    private const float ExpansionFactor = 0.40f;

    /// <summary>Apply the pseudo-localization transform to a (already-resolved) string. Braces and their contents are
    /// left intact; ASCII letters are accented; the result is bracketed and padded to ~+40% length. An empty/whitespace
    /// input returns unchanged (no point bracketing a separator).</summary>
    public static string Transform(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        var sb = new StringBuilder(s.Length + s.Length / 2 + 4);
        sb.Append(Open);

        int letters = 0;
        bool inBrace = false;
        foreach (char c in s)
        {
            if (c == '{') { inBrace = true; sb.Append(c); continue; }
            if (c == '}') { inBrace = false; sb.Append(c); continue; }
            if (inBrace) { sb.Append(c); continue; }   // preserve placeholder / ICU-block contents verbatim

            char mapped = Accent(c);
            if (mapped != c || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letters++;
            sb.Append(mapped);
        }

        // Expansion: append filler dots to reach ~+40% over the source's letter count (so longer strings expand more,
        // matching how real translations grow proportionally). At least 1 dot for any non-empty letter content.
        int pad = (int)(letters * ExpansionFactor);
        if (letters > 0 && pad == 0) pad = 1;
        for (int i = 0; i < pad; i++) sb.Append(Filler);

        sb.Append(Close);
        return sb.ToString();
    }

    /// <summary>Map one ASCII letter to a visually-similar accented codepoint; non-letters pass through. The table is
    /// fixed and deterministic (round-trippable by eye), covering a–z / A–Z.</summary>
    private static char Accent(char c) => c switch
    {
        'a' => 'á', 'b' => 'ƀ', 'c' => 'ç', 'd' => 'ð', 'e' => 'ě', 'f' => 'ƒ', 'g' => 'ĝ', 'h' => 'ĥ',
        'i' => 'í', 'j' => 'ĵ', 'k' => 'ķ', 'l' => 'ĺ', 'm' => 'ḿ', 'n' => 'ñ', 'o' => 'ö', 'p' => 'þ',
        'q' => 'ɋ', 'r' => 'ŕ', 's' => 'š', 't' => 'ţ', 'u' => 'ú', 'v' => 'ṽ', 'w' => 'ŵ', 'x' => 'ẋ',
        'y' => 'ý', 'z' => 'ž',
        'A' => 'Á', 'B' => 'Ɓ', 'C' => 'Ç', 'D' => 'Ð', 'E' => 'Ě', 'F' => 'Ƒ', 'G' => 'Ĝ', 'H' => 'Ĥ',
        'I' => 'Í', 'J' => 'Ĵ', 'K' => 'Ķ', 'L' => 'Ĺ', 'M' => 'Ḿ', 'N' => 'Ñ', 'O' => 'Ö', 'P' => 'Þ',
        'Q' => 'Ɋ', 'R' => 'Ŕ', 'S' => 'Š', 'T' => 'Ţ', 'U' => 'Ú', 'V' => 'Ṽ', 'W' => 'Ŵ', 'X' => 'Ẋ',
        'Y' => 'Ý', 'Z' => 'Ž',
        _ => c,
    };
}
