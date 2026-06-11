namespace FluentGpu.Controls;

/// <summary>
/// Standardized glyph constants (Segoe Fluent Icons / Segoe MDL2 Assets private-use codepoints) so icons are
/// consistent across the app. Use with Ui.Icon(Icons.Play) or IconButton.Create(Icons.Play, ...).
/// Font = Theme.IconFont by default; pass a custom "path.ttf#Family" for your own set.
/// </summary>
public static class Icons
{
    public const string Back = "\uE72B";
    public const string Forward = "\uE72A";
    public const string ChevronDown = "\uE70D";
    public const string ChevronDownSmall = "\uE96E";  // AnimatedChevronDownSmall fallback (DropDownButton/SplitButton)
    public const string ChevronUp = "\uE70E";
    public const string ChevronRight = "\uE76C";
    public const string Clock = "\uE823";
    public const string FavoriteStar = "\uE734";
    public const string Code = "\uE943";
    public const string Home = "\uE80F";
    public const string Search = "\uE721";
    public const string Settings = "\uE713";
    public const string More = "\uE712";
    public const string Menu = "\uE700";
    public const string Add = "\uE710";
    public const string Remove = "\uE738";
    public const string Cancel = "\uE711";
    public const string Accept = "\uE73E";
    public const string Refresh = "\uE72C";
    public const string Play = "\uE768";
    public const string Pause = "\uE769";
    public const string Stop = "\uE71A";
    public const string Previous = "\uE892";
    public const string Next = "\uE893";
    public const string Shuffle = "\uE8B1";
    public const string RepeatAll = "\uE8EE";
    public const string RepeatOne = "\uE8ED";
    public const string Volume = "\uE767";
    public const string Mute = "\uE74F";
    public const string Heart = "\uEB51";
    public const string Star = "\uE734";
    public const string List = "\uEA37";
    public const string Grid = "\uE80A";
    public const string MusicNote = "\uEC4F";
    public const string Picture = "\uEB9F";
    public const string Download = "\uE896";
    public const string Share = "\uE72D";
    public const string Folder = "\uE8B7";
    public const string Tag = "\uE8EC";
    public const string Document = "\uE8A5";
    public const string Copy = "\uE8C8";
    public const string Link = "\uE71B";
    public const string Globe = "\uE774";
    public const string OpenInNewWindow = "\uE8A7";
    public const string Font = "\uE8D2";
    public const string Brush = "\uE790";
    public const string Movie = "\uE8B2";
    // -- Window chrome (Segoe Fluent Icons caption set \u2014 the Win11 system caption-button glyphs, drawn at 10px) ------
    public const string ChromeMinimize = "\uE921";   // Minimize \u2014
    public const string ChromeMaximize = "\uE922";   // Maximize \u25A1
    public const string ChromeRestore = "\uE923";    // Restore \u2750 (shown while maximized; FA WindowDecorations.axaml:208)
    public const string ChromeClose = "\uE8BB";      // Close \u2715

    public const string RevealPassword = "\uF78D";   // PasswordBox RevealButton GlyphElement (PasswordBox_themeresources.xaml:100)
    public const string ClearText = "\uE894";        // TextBox DeleteButton glyph (TextBox_themeresources.xaml:246 GlyphElement)
    public const string CaretUpSolid = "\uE70E";     // NumberBox UpSpinButton Content (NumberBox.xaml:174)
    public const string CaretDownSolid = "\uE70D";   // NumberBox DownSpinButton Content (NumberBox.xaml:175)
    public const string NumberBoxPopupIndicator = "\uEC8F"; // NumberBox Compact in-field PopupIndicator (NumberBox.xaml:365)

    // -- Menus (MenuFlyoutSubItem / CommandBarFlyout cascades) ------------------------------------------------------
    /// <summary>The DEFAULT MenuFlyoutSubItem cascade chevron - ChevronRightMed E974 @12px
    /// (MenuFlyout_themeresources.xaml:620 FlyoutButtonChevron + :720 SubItemChevron, both Glyph="&amp;#xE974;").
    /// NOTE: the plain ChevronRight E76C is used only by the RadioMenuFlyoutSubItemStyle
    /// (RadioMenuFlyoutItem_themeresources.xaml:198) and the CommandBarFlyout secondary sub-item
    /// (CommandBarFlyout_themeresources.xaml:303) - see <see cref="ChevronRight"/>.</summary>
    public const string ChevronRightMed = "\uE974";
    /// <summary>RadioMenuFlyoutItem bullet (RadioMenuFlyoutItem_themeresources.xaml:94 CheckGlyph Glyph="&amp;#xE915;").</summary>
    public const string RadioBullet = "\uE915";

    // -- InfoBar standard severity glyphs (InfoBar_themeresources.xaml:70-74, Segoe Fluent Icons) -------------------
    public const string InfoBarBackgroundCircle = "\uF136"; // InfoBarIconBackgroundGlyph - the filled status circle
    public const string StatusInfo = "\uF13F";              // InfoBarInformationalIconGlyph
    public const string StatusError = "\uF13D";             // InfoBarErrorIconGlyph
    public const string StatusWarning = "\uF13C";           // InfoBarWarningIconGlyph
    public const string StatusSuccess = "\uF13E";           // InfoBarSuccessIconGlyph

    // -- InfoBadge severity icon glyphs (InfoBadge_themeresources.xaml:99/111/122/133/144) --------------------------
    public const string Attention = "\uEA38";   // AttentionIconInfoBadgeStyle FontIconSource Glyph="&#xEA38;"
    public const string Important = "\uE171";   // CautionIconInfoBadgeStyle SymbolIconSource Symbol="Important" (Segoe Fluent E171)
}
