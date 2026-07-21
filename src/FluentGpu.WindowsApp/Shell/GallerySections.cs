using FluentGpu.Controls;

// The WS7 information architecture: the 8 IA sections → the granular [GalleryPage] categories each contains, in nav
// order. A section that lists a same-named category FLATTENS its pages as direct leaves (Fundamentals/Design/Patterns/
// App services/Samples); the "Controls" section NESTS its 11 category subgroups (the WinUI-gallery control taxonomy).
// "Home" is a top-level leaf added by GalleryShell (not a section group). "Overview"/Hidden pages never appear here
// (they're ShowInNav=false in the registry-derived RouteRegistry). Performance/Windows sections land in G8b when their
// pages exist. This is the one hand-authored table; everything else derives from the generated GalleryRegistry.
static class GallerySections
{
    public static readonly (string Section, string Icon, string[] Categories)[] Sections =
    {
        ("Fundamentals", Icons.Document, new[] { "Fundamentals" }),
        ("Design", Icons.Brush, new[] { "Design" }),
        ("Controls", Icons.Grid, new[]
        {
            "Basic input", "Status & info", "Layout", "Scrolling", "Navigation",
            "Dialogs & flyouts", "Text", "Media", "Collections", "Menus & toolbars", "Date & time",
        }),
        ("Patterns", Icons.Movie, new[] { "Patterns" }),
        ("App services", Icons.Globe, new[] { "App services" }),
        ("Samples", Icons.MusicNote, new[] { "Samples" }),
    };
}
