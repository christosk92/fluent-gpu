namespace Wavee;

// Extra Segoe Fluent Icons glyphs the engine's Icons.* set doesn't expose. Built from hex codepoints at runtime so the
// SOURCE stays pure-ASCII — raw PUA chars / \u escapes get mangled by the edit/encoding chain (per the engine's own rule).
internal static class Mdl
{
    static string Of(int cp) => ((char)cp).ToString();

    public static readonly string Contact = Of(0xE77B);       // artists
    public static readonly string Microphone = Of(0xE720);     // podcasts
    public static readonly string Add = Of(0xE710);            // create (+)
    public static readonly string Settings = Of(0xE713);       // diagnostics (gear)
    public static readonly string ChevronLeft = Of(0xE76B);    // rail page back
    public static readonly string ChevronRight = Of(0xE76C);   // rail page forward
    public static readonly string Album = Of(0xE93C);          // albums
    public static readonly string Equalizer = Of(0xE9E9);      // now-playing track indicator
    public static readonly string Friends = Of(0xE716);        // friends
    public static readonly string Bell = Of(0xEA8F);           // notifications
    public static readonly string Moon = Of(0xE708);           // theme (dark → show moon)
    public static readonly string Sun = Of(0xE706);            // theme (light → show sun)
    public static readonly string SplitView = Of(0xE8A0);      // right panel
    public static readonly string RadioTower = Of(0xEC05);     // podcasts
    public static readonly string Check = Of(0xE73E);
    public static readonly string Queue = Of(0xE14C);
    public static readonly string Lyrics = Of(0xE90A);
    public static readonly string Device = Of(0xE770);
    public static readonly string CellPhone = Of(0xE8EA);      // device picker: Phone
    public static readonly string Speakers = Of(0xE7F5);       // device picker: Speaker / AVR / cast audio
    public static readonly string TvMonitor = Of(0xE7F4);      // device picker: TV / console / cast video
    public static readonly string ThisPc = Of(0xE977);         // device picker: This PC / Computer
    public static readonly string Pin = Of(0xE718);            // pin / unpin a sidebar row
    public static readonly string FolderOpen = Of(0xE838);     // expanded playlist folder
    public static readonly string HeartFill = Of(0xEB52);      // saved/liked (filled heart) — the Mutations on-state
    public static readonly string Sort = Of(0xE8CB);           // library sort/view dropdown trigger
    public static readonly string ViewList = Of(0xE14C);       // view-as list
    public static readonly string ViewGrid = Of(0xF0E2);       // view-as grid
    public static readonly string GripperBar = Of(0xE76F);     // drag handle
    public static readonly string ChevronUp = Of(0xE70E);      // sort direction ascending
    public static readonly string ChevronDown = Of(0xE70D);    // sort direction descending
    public static readonly string FavoriteStarFill = Of(0xE735);   // album top-track star (the most-played row)
    public static readonly string CaretSolidUp = Of(0xF090);   // track-list sort-direction caret (rotated 180° for descending)
    public static readonly string Calendar = Of(0xE787);       // upcoming concerts (date stub)
    public static readonly string Link = Of(0xE71B);           // external/social link pill
    public static readonly string Share = Of(0xE72D);          // hero share action
    public static readonly string Play = Of(0xE768);           // play (hero/pinned/radio actions)
    public static readonly string MapPin = Of(0xE707);         // concert location
    public static readonly string Globe = Of(0xE774);          // world rank ("#N in the world")
}
