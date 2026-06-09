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
    public const string Font = "\uE8D2";
    public const string Brush = "\uE790";
    public const string Movie = "\uE8B2";
    public const string RevealPassword = "\uF78D";   // PasswordBox RevealButton GlyphElement (PasswordBox_themeresources.xaml:100)
    public const string ClearText = "\uE894";        // TextBox DeleteButton glyph (TextBox_themeresources.xaml:246 GlyphElement)
    public const string CaretUpSolid = "\uE70E";     // NumberBox UpSpinButton Content (NumberBox.xaml:174)
    public const string CaretDownSolid = "\uE70D";   // NumberBox DownSpinButton Content (NumberBox.xaml:175)
    public const string NumberBoxPopupIndicator = "\uEC8F"; // NumberBox Compact in-field PopupIndicator (NumberBox.xaml:365)
}
