using FluentGpu.Controls;

namespace Wavee;

/// <summary>
/// The action icon-KEY table: actions carry a semantic key, and the ONE <see cref="Resolve"/> maps it to an
/// <see cref="IconRef"/> — a layered ThemedIcon name where the starter set covers it (with a Segoe Fluent glyph
/// fallback that renders until the harvest is complete), else a plain glyph. Only this table changes as the ThemedIcon
/// harvest grows — action definitions never do.
/// </summary>
public static class ActionIcons
{
    public const string Play = "play";
    public const string PlayNext = "play-next";
    public const string Queue = "queue";
    public const string Heart = "heart";
    public const string Add = "add";
    public const string Album = "album";
    public const string Artist = "artist";
    public const string Link = "link";
    public const string Remove = "remove";
    public const string Delete = "delete";
    public const string Open = "open";
    public const string Rename = "rename";
    public const string People = "people";
    public const string Globe = "globe";
    public const string Credits = "credits";
    public const string Share = "share";
    public const string CopyUri = "copy-uri";
    public const string OpenWeb = "open-web";
    public const string Radio = "radio";

    /// <summary>Key → an <see cref="IconRef"/>. Keys the ThemedIcon starter set covers resolve to a layered vector name
    /// with a glyph fallback (<c>IconRef.Themed</c>); the rest are plain Segoe Fluent glyphs (implicit string → IconRef).
    /// <paramref name="isChecked"/> picks the filled variant for stateful icons (the saved heart).</summary>
    public static IconRef Resolve(string key, bool isChecked = false) => key switch
    {
        Play => IconRef.Themed("Play", Icons.Play),
        // PlayNext / AddToQueue: the two-tone LAYERED themed marks (list lines + accent play mark), authored to mirror
        // the app's CUSTOM wavee-icons rail glyphs. The rail glyph is kept as the fallback (rendered until the themed
        // registry is warm) — hence the explicit Font so the fallback resolves against the WaveeIcons font.
        PlayNext => new IconRef { ThemedName = "PlayNext", Glyph = WaveeIcons.PlayNext, Font = WaveeIcons.Font },
        Queue => new IconRef { ThemedName = "AddToQueue", Glyph = WaveeIcons.PlayAfter, Font = WaveeIcons.Font },   // AddToQueue == the rail's "Play after"
        // Heart: the layered two-tone Like toggle — a neutral outline "Heart" when unliked, the accent-filled
        // "HeartFill" when liked (Segoe glyph fallbacks until the themed registry is warm). The strip renders the
        // accent from the icon's own Accent layer; the checked-strip fg tint covers the glyph-fallback case.
        Heart => isChecked ? IconRef.Themed("HeartFill", Mdl.HeartFill) : IconRef.Themed("Heart", Icons.Heart),
        Add => IconRef.Themed("Add", Icons.Add),
        Album => Mdl.Album,
        Artist => Icons.Contact,
        Link => IconRef.Themed("Link", Icons.Link),
        Remove => Icons.Remove,
        Delete => IconRef.Themed("Delete", Mdl.Delete),
        Open => IconRef.Themed("Open", Icons.OpenInNewWindow),
        Rename => IconRef.Themed("Rename", Icons.Edit),
        People => Mdl.Friends,
        Globe => Icons.Globe,
        Credits => Icons.Document,
        Share => Icons.Share,
        CopyUri => Icons.Copy,
        OpenWeb => Icons.Globe,
        Radio => Mdl.RadioTower,
        _ => Icons.More,
    };
}
