using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI <c>PersonPicture</c>: a circular avatar that shows — in WinUI's order of precedence — a circular-cropped photo
/// (<see cref="Create(string,float,string?,bool,int,string?,ColorF?)"/> via <paramref name="imageSource"/>), otherwise
/// the contact's initials, otherwise a generic Contact/People fallback glyph, all on the quaternary alt control fill
/// (<c>ControlAltFillColorQuarternary</c>) ringed by a 1px card stroke. An optional accent badge (number / glyph) floats
/// at the top-right. Default diameter 96; the initials font is 42% of the diameter (the WinUI design rule), the badge
/// plate is 50% of the diameter, and the badge font is 60% of the badge plate.
/// </summary>
public static class PersonPicture
{
    // Segoe Fluent Icons / Segoe MDL2 fallback glyphs, matching PersonPicture.xaml's NoPhotoOrInitials / Group setters:
    //   NoPhotoOrInitials -> "Contact" (single person)  U+E77B
    //   Group             -> "People"  (group)          U+E716
    private const string ContactGlyph = "";
    private const string GroupGlyph = "";

    // ── WinUI design constants (PersonPicture.cpp OnSizeChanged + _themeresources) ─────────────────────────────────
    private const float InitialsFontFraction = 0.42f;  // "font size to be 42% of the container"
    private const float BadgePlateFraction = 0.50f;    // "badging plate to be about 50% of the control"
    private const float BadgeFontFraction = 0.60f;     // "font size to be 60% of the badging plate"

    /// <summary>Computed geometry/content WinUI bakes into <c>PersonPictureTemplateSettings</c> + its size-driven font
    /// rules. Pure factory (no signals): the resolved initials/glyph string, which face renders it, and the size-derived
    /// font/badge metrics — fed straight into the static element props (this is a cold, reconcile-time composition).</summary>
    public readonly record struct PersonPictureTemplateSettings(
        string ActualInitials,   // the resolved glyph-or-initials string (empty when a photo is shown)
        bool UseSymbolFont,      // true ⇒ render ActualInitials with the symbol font (Group / NoPhotoOrInitials glyph)
        float InitialsFontSize,  // Width * 0.42, min 1
        float BadgePlateSize,    // Width * 0.50
        float BadgeFontSize)     // BadgePlateSize * 0.60, min 1
    {
        public static PersonPictureTemplateSettings For(string actualInitials, bool useSymbolFont, float size)
        {
            float plate = size * BadgePlateFraction;
            return new PersonPictureTemplateSettings(
                actualInitials,
                useSymbolFont,
                MathF.Max(1f, size * InitialsFontFraction),
                plate,
                MathF.Max(1f, plate * BadgeFontFraction));
        }
    }

    /// <param name="initials">Explicit initials (highest precedence). When empty, <paramref name="displayName"/> drives
    /// the initials via the WinUI InitialsGenerator rules; when both are empty, the Contact/People fallback glyph shows.</param>
    /// <param name="size">Diameter (Width == Height); the control stays circular. Default 96.</param>
    /// <param name="displayName">Contact display name; initials are generated from it (strip-trailing-brackets, first
    /// letter of first + last word, uppercased) only when <paramref name="initials"/> is empty.</param>
    /// <param name="isGroup">When true, the avatar shows the People (group) glyph regardless of initials.</param>
    /// <param name="badgeNumber">When &gt; 0, draws an accent number badge (top-right): ≤99 shows the number, &gt;99 shows
    /// "99+". A NEGATIVE value shows no badge at all (WinUI: a non-zero BadgeNumber owns the badge slot and ≤0 maps to
    /// NoBadge — the glyph never substitutes, PersonPicture.cpp:191-198).</param>
    /// <param name="badgeGlyph">When set (and <paramref name="badgeNumber"/> is 0), draws an accent glyph badge
    /// (top-right) in the symbol font.</param>
    /// <param name="imageSourcePath">When set, shows a circular-cropped photo (UniformToFill) instead of initials/glyph
    /// — WinUI ProfilePicture. Takes precedence over initials, but not over <paramref name="isGroup"/>.</param>
    /// <param name="fill">Override the ellipse fill (default <c>ControlAltFillColorQuarternary</c>); also the photo placeholder tint.</param>
    public static BoxEl Create(
        string initials,
        float size = 96f,
        string? displayName = null,
        bool isGroup = false,
        int badgeNumber = 0,
        string? badgeGlyph = null,
        string? imageSourcePath = null,
        ColorF? fill = null)
    {
        // ── Initials precedence: explicit Initials  >  DisplayName-derived  >  (none) ──────────────────────────────
        // (PersonPicture::GetInitials. Contact-object initials collapse into the DisplayName path for our string API.)
        string resolvedInitials =
            !string.IsNullOrWhiteSpace(initials) ? initials :
            !string.IsNullOrWhiteSpace(displayName) ? InitialsFromDisplayName(displayName!) :
            "";

        // Group OUTRANKS the photo (UpdateIfReady's GoToState order, PersonPicture.cpp:162-170): isGroup shows the
        // People glyph even when a photo is supplied.
        bool hasPhoto = !string.IsNullOrEmpty(imageSourcePath) && !isGroup;

        // Visual-state resolution mirroring UpdateIfReady's GoToState order:
        //   Group  >  Photo  >  Initials  >  NoPhotoOrInitials
        string actualText;
        bool useSymbolFont;
        if (isGroup)                              { actualText = GroupGlyph;   useSymbolFont = true;  }   // Group
        else if (hasPhoto)                        { actualText = "";           useSymbolFont = false; }   // Photo (text hidden)
        else if (resolvedInitials.Length != 0)    { actualText = resolvedInitials; useSymbolFont = false; } // Initials
        else                                      { actualText = ContactGlyph; useSymbolFont = true;  }   // NoPhotoOrInitials

        var ts = PersonPictureTemplateSettings.For(actualText, useSymbolFont, size);

        var circle = Radii.Circle(size);

        // The face layer: either the photo (PersonPictureEllipse / ActualImageBrush, UniformToFill, circular-cropped) or
        // the initials/glyph TextBlock. FontWeight SemiBold == Bold. IsTextScaleFactorEnabled=False ⇒ fixed font size.
        // The text rides in a SIZED flex wrapper because ZStack pins children HORIZONTALLY at the content origin
        // (only the vertical axis honors AlignSelf/AlignItems — FlexLayout.ArrangeZStack) — WinUI centers the
        // InitialsTextBlock both ways (PersonPicture.xaml:66 HorizontalAlignment/VerticalAlignment="Center"); the
        // sized wrapper's own flex centering places the measured run in the middle of the circle on both axes.
        Element face = hasPhoto
            ? new ImageEl
            {
                Source = imageSourcePath!,
                Width = size,
                Height = size,
                Corners = circle,                                  // circular crop (Ellipse-clipped photo)
                Placeholder = fill ?? Tok.FillControlAltQuaternary,
            }
            : new BoxEl
            {
                Width = size,
                Height = size,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children =
                [
                    new TextEl(actualText)
                    {
                        Size = ts.InitialsFontSize,                // Width * 0.42 (WinUI OnSizeChanged), min 1
                        Bold = true,                               // InitialsTextBlock FontWeight=SemiBold in EVERY state (initials AND Group/Contact glyph)
                        Color = Tok.TextPrimary,                   // PersonPictureForegroundThemeBrush = TextFillColorPrimary
                        FontFamily = useSymbolFont ? Theme.IconFont // SymbolThemeFontFamily for Group / NoPhotoOrInitials
                                                   : Theme.BodyFont, // ContentControlThemeFontFamily for initials
                    },
                ],
            };

        // Badge resolution (PersonPicture.cpp:191-198, :227-232): a non-zero BadgeNumber OWNS the badge slot — positive
        // shows the number, negative shows NOTHING (the glyph is never a fallback for a bad number); the glyph renders
        // only when BadgeNumber == 0.
        bool numberBadge = badgeNumber > 0;
        bool glyphBadge = badgeNumber == 0 && !string.IsNullOrEmpty(badgeGlyph);
        var children = numberBadge || glyphBadge
            ? new[] { face, BuildBadge(ts, size, badgeNumber, glyphBadge ? badgeGlyph : null) }
            : new[] { face };

        return new BoxEl
        {
            Width = size,
            Height = size,
            Corners = circle,
            Fill = fill ?? Tok.FillControlAltQuaternary,           // PersonPictureEllipseFillThemeBrush = ControlAltFillColorQuarternary
            BorderWidth = 1f,                                      // PersonPictureEllipseStrokeThickness = 1
            BorderColor = Tok.StrokeCardDefault,                  // PersonPictureEllipseFillStrokeBrush = CardStrokeColorDefault
            ZStack = true,                                         // face fills; badge floats top-right over it
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            // NO ClipToBounds: WinUI's RootGrid is unclipped (PersonPicture.xaml:68) — the badge deliberately
            // overflows the circle by 4px top/right; the photo is already circle-cropped by ImageEl.Corners.
            // WinUI sets AutomationProperties.AccessibilityView=Raw + IsTabStop=False: a passive, non-focusable image
            // surface (no special control role, not in the control tree). Map to None (raw) + the default non-focusable BoxEl.
            Role = AutomationRole.None,
            Children = children,
        };
    }

    // BadgeStates: BadgeWithoutImageSource. Badge plate = 50% of the control, top-right, margin 0,-4,-4,0.
    // Fill = AccentFillColorDefault; foreground = TextOnAccentFillColorPrimary; stroke = transparent (2px, invisible).
    private static BoxEl BuildBadge(PersonPictureTemplateSettings ts, float size, int badgeNumber, string? badgeGlyph)
    {
        bool isGlyph = !string.IsNullOrEmpty(badgeGlyph);   // caller resolved precedence: glyph only when badgeNumber == 0
        string text = isGlyph ? badgeGlyph! : (badgeNumber <= 99 ? badgeNumber.ToString() : "99+");

        var label = new TextEl(text)
        {
            Size = ts.BadgeFontSize,                               // BadgePlateSize * 0.60, min 1
            Color = Tok.TextOnAccentPrimary,                       // PersonPictureEllipseBadgeForegroundThemeBrush
            FontFamily = isGlyph ? Theme.IconFont : Theme.BodyFont, // BadgeGlyphIcon uses SymbolThemeFontFamily
            Bold = true,                                           // SemiBold FontWeight on the badge label in every state (WinUI)
        };

        // Position at the top-right corner. WinUI: BadgeGrid VerticalAlignment=Top, HorizontalAlignment=Right,
        // Margin 0,-4,-4,0 (PersonPicture.xaml:68) → the plate's RIGHT edge sits 4px outside the control's right edge,
        // i.e. left = size + 4 − plate. The engine's ZStack lays every layer at the content origin (left 0, and
        // AlignSelf=Start pins the top), so the full target offset is applied as a translation — computed from the
        // CONTROL size, not from any assumption that plate == size/2, so it survives a plate-fraction change.
        // Vertically the -4 top margin lifts the plate 4px above the top edge (OffsetY = -4).
        float plate = ts.BadgePlateSize;
        return new BoxEl
        {
            Width = plate,
            Height = plate,
            MaxHeight = plate,
            Corners = Radii.Circle(plate),
            Fill = Tok.AccentDefault,                              // PersonPictureEllipseBadgeFillThemeBrush = AccentFillColorDefault
            BorderWidth = 0f,                                      // stroke brush is ControlFillColorTransparent (invisible)
            AlignSelf = FlexAlign.Start,                           // BadgeGrid VerticalAlignment=Top (lay out at the top, not centred)
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            ClipToBounds = true,
            OffsetX = size + 4f - plate,                           // right-aligned + the 4px outward (-4 right) margin
            OffsetY = -4f,                                         // the -4 top margin lifts it 4px above the top edge
            Children = [label],
        };
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  InitialsGenerator (faithful port of InitialsGenerator.cpp): DisplayName -> 1-2 uppercase initials.
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Port of <c>InitialsGenerator::InitialsFromDisplayName</c>: strip a trailing bracket group, split on
    /// spaces, take the first full character of the first (and, if more than one word, the last) word, uppercased.
    /// Returns "" for symbolic/glyph scripts (CJK, Arabic, Greek, etc.) so the caller falls back to the Contact glyph.</summary>
    public static string InitialsFromDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName) || GetCharacterType(displayName) != CharacterType.Standard)
            return "";

        string name = StripTrailingBrackets(displayName);

        // Split on space, dropping empty tokens (matches the C++ Split which skips empty tokens).
        string first = ""; string last = ""; int wordCount = 0;
        int i = 0, n = name.Length;
        while (i < n)
        {
            while (i < n && name[i] == ' ') i++;
            int start = i;
            while (i < n && name[i] != ' ') i++;
            if (i > start)
            {
                string word = name.Substring(start, i - start);
                if (wordCount == 0) first = word;
                last = word;
                wordCount++;
            }
        }

        if (wordCount == 0) return "";

        string result = wordCount == 1
            ? GetFirstFullCharacter(first)
            : GetFirstFullCharacter(first) + GetFirstFullCharacter(last);

        return result.ToUpperInvariant();
    }

    // InitialsGenerator::GetFirstFullCharacter — skip leading punctuation, then take the base char plus any
    // trailing combining diacritical marks (U+0300..U+036F).
    private static string GetFirstFullCharacter(string str)
    {
        int len = str.Length;
        if (len == 0) return "";

        int start = 0;
        while (start < len)
        {
            char c = str[start];
            // Omit  ! " # $ % & ' ( ) * + , - . /   (U+0021..U+002F)
            if (c >= 0x0021 && c <= 0x002F) { start++; continue; }
            // Omit  : ; < = > ? @            (U+003A..U+0040)
            if (c >= 0x003A && c <= 0x0040) { start++; continue; }
            // Omit  { | } ~                  (U+007B..U+007E)
            if (c >= 0x007B && c <= 0x007E) { start++; continue; }
            break;
        }

        // If everything was punctuation, restart at index 0 (the C++ fallback).
        if (start >= len) start = 0;

        // Include trailing combining diacritical marks (U+0300..U+036F).
        int index = start + 1;
        while (index < len)
        {
            char c = str[index];
            if (c < 0x0300 || c > 0x036F) break;
            index++;
        }

        return str.Substring(start, index - start);
    }

    // InitialsGenerator::StripTrailingBrackets — drop a final {...} / (...) / [...] group ("John Smith (OSG)" -> "John Smith ").
    private static string StripTrailingBrackets(string source)
    {
        if (source.Length == 0) return source;
        char lastChar = source[source.Length - 1];
        char open = lastChar switch { '}' => '{', ')' => '(', ']' => '[', _ => '\0' };
        if (open == '\0') return source;
        int start = source.LastIndexOf(open);
        if (start < 0) return source;
        return source.Substring(0, start);
    }

    private enum CharacterType : byte { Other, Standard, Symbolic, Glyph }

    // InitialsGenerator::GetCharacterType(string) — examine the first three chars; precedence Glyph > Symbolic > Standard.
    private static CharacterType GetCharacterType(string str)
    {
        var result = CharacterType.Other;
        int n = Math.Min(3, str.Length);
        for (int i = 0; i < n; i++)
        {
            char c = str[i];
            if (c == '\0' || c == '﻿') break;
            var t = GetCharacterType(c);
            switch (t)
            {
                case CharacterType.Glyph: result = CharacterType.Glyph; break;
                case CharacterType.Symbolic: if (result != CharacterType.Glyph) result = CharacterType.Symbolic; break;
                case CharacterType.Standard: if (result != CharacterType.Glyph && result != CharacterType.Symbolic) result = CharacterType.Standard; break;
            }
        }
        return result;
    }

    // InitialsGenerator::GetCharacterType(char) — the allowed-list of Unicode blocks (Glyph / Symbolic / Standard / Other).
    private static CharacterType GetCharacterType(char ch)
    {
        int c = ch;

        // GLYPH (scripts whose initials don't decompose to Latin letters → use the generic icon)
        if (c is (>= 0x0250 and <= 0x02AF)   // IPA Extensions
              or (>= 0x0600 and <= 0x06FF)   // Arabic
              or (>= 0x0750 and <= 0x077F)   // Arabic Supplement
              or (>= 0x08A0 and <= 0x08FF)   // Arabic Extended-A
              or (>= 0xFB50 and <= 0xFDFF)   // Arabic Presentation Forms-A
              or (>= 0xFE70 and <= 0xFEFF)   // Arabic Presentation Forms-B
              or (>= 0x0900 and <= 0x097F)   // Devanagari
              or (>= 0xA8E0 and <= 0xA8FF)   // Devanagari Extended
              or (>= 0x0980 and <= 0x09FF)   // Bangla
              or (>= 0x0A00 and <= 0x0A7F)   // Gurmukhi
              or (>= 0x0A80 and <= 0x0AFF)   // Gujarati
              or (>= 0x0B00 and <= 0x0B7F)   // Odia
              or (>= 0x0B80 and <= 0x0BFF)   // Tamil
              or (>= 0x0C00 and <= 0x0C7F)   // Telugu
              or (>= 0x0C80 and <= 0x0CFF)   // Kannada
              or (>= 0x0D00 and <= 0x0D7F)   // Malayalam
              or (>= 0x0D80 and <= 0x0DFF)   // Sinhala
              or (>= 0x0E00 and <= 0x0E7F)   // Thai
              or (>= 0x0E80 and <= 0x0EFF))  // Lao
            return CharacterType.Glyph;

        // SYMBOLIC (CJK + Greek/Hebrew/Armenian — take one or two characters as-is, but our string API treats as glyph)
        if (c is (>= 0x4E00 and <= 0x9FFF)   // CJK Unified Ideographs
              or (>= 0x3400 and <= 0x4DBF)   // CJK Ext A
              or (>= 0x2E80 and <= 0x2EFF)   // CJK Radicals Supplement
              or (>= 0x3000 and <= 0x303F)   // CJK Symbols and Punctuation
              or (>= 0x31C0 and <= 0x31EF)   // CJK Strokes
              or (>= 0x3200 and <= 0x32FF)   // Enclosed CJK Letters and Months
              or (>= 0x3300 and <= 0x33FF)   // CJK Compatibility
              or (>= 0xF900 and <= 0xFAFF)   // CJK Compatibility Ideographs
              or (>= 0xFE30 and <= 0xFE4F)   // CJK Compatibility Forms
              or (>= 0x0370 and <= 0x03FF)   // Greek and Coptic
              or (>= 0x0590 and <= 0x05FF)   // Hebrew
              or (>= 0x0530 and <= 0x058F))  // Armenian
            return CharacterType.Symbolic;

        // STANDARD (Latin + Cyrillic + combining marks → real initials)
        if (c is (> 0x0000 and <= 0x007F)    // Basic Latin
              or (>= 0x0080 and <= 0x00FF)   // Latin-1 Supplement
              or (>= 0x0100 and <= 0x017F)   // Latin Extended-A
              or (>= 0x0180 and <= 0x024F)   // Latin Extended-B
              or (>= 0x2C60 and <= 0x2C7F)   // Latin Extended-C
              or (>= 0xA720 and <= 0xA7FF)   // Latin Extended-D
              or (>= 0xAB30 and <= 0xAB6F)   // Latin Extended-E
              or (>= 0x1E00 and <= 0x1EFF)   // Latin Extended Additional
              or (>= 0x0400 and <= 0x04FF)   // Cyrillic
              or (>= 0x0500 and <= 0x052F)   // Cyrillic Supplement
              or (>= 0x0300 and <= 0x036F))  // Combining Diacritical Marks
            return CharacterType.Standard;

        return CharacterType.Other;
    }
}
