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
    public static readonly string Pin = Of(0xE718);            // pin / unpin a sidebar row
    public static readonly string FolderOpen = Of(0xE838);     // expanded playlist folder
}
