using FluentGpu.Controls;

// The WS7 → G8c2 information architecture: a FLAT top-level nav spine (the Compose Material Catalog precedent). The
// former single "Controls" wrapper group is gone — its 11 control categories are now TOP-LEVEL expanders, siblings of
// the Fundamentals / Design / Patterns / App services / Samples roots (and the Home leaf, added by GalleryShell). Every
// top-level group's children are its non-hidden [GalleryPage]s (registry-derived); the group header itself routes to a
// registry-projected overview grid (GalleryShell.Page → OverviewPage), so a group can never fall through to Home.
// This is the one hand-authored table — the order + per-group icon + one-line blurb; everything else derives from the
// generated GalleryRegistry.
static class GallerySections
{
    /// <summary>The top-level nav order: each category → its section glyph. "Home" is a separate leaf (added by the
    /// shell) and never appears here; "Overview" pages are Hidden and never appear. Category names must match the
    /// <c>[GalleryPage(..., category)]</c> spelling exactly.</summary>
    public static readonly (string Category, string Icon)[] Order =
    {
        ("Fundamentals", Icons.Document),
        ("Design", Icons.Brush),
        ("Basic input", Icons.Accept),
        ("Status & info", Icons.Tag),
        ("Layout", Icons.ViewGrid),
        ("Scrolling", Icons.List),
        ("Navigation", Icons.Home),
        ("Dialogs & flyouts", Icons.More),
        ("Text", Icons.Font),
        ("Media", Icons.Picture),
        ("Collections", Icons.ViewList),
        ("Menus & toolbars", Icons.List),
        ("Date & time", Icons.Clock),
        ("Patterns", Icons.Movie),
        ("App services", Icons.Globe),
        ("Samples", Icons.MusicNote),
    };

    /// <summary>The 11 WinUI-taxonomy control categories (the subset of <see cref="Order"/> that is "controls") — drives
    /// the All-controls grid + its difficulty filter facet.</summary>
    public static readonly string[] ControlCategories =
    {
        "Basic input", "Status & info", "Layout", "Scrolling", "Navigation",
        "Dialogs & flyouts", "Text", "Media", "Collections", "Menus & toolbars", "Date & time",
    };

    /// <summary>The overview-grid subtitle for a top-level group (the landing-page blurb). Falls back to a generic line.</summary>
    public static string Blurb(string category) => category switch
    {
        "Fundamentals" => "The engine model — the React/Reactor surface, layout, virtualization, and the motion/compositor pipeline.",
        "Design" => "Design guidance — the Fluent type ramp, iconography, and the generated design-token browser.",
        "Basic input" => "Buttons, selection, and value controls — the WinUI Gallery Basic input set.",
        "Status & info" => "Progress, badges, tips, and status surfaces that keep the user informed.",
        "Layout" => "Panels and containers — the building blocks that arrange other controls.",
        "Scrolling" => "Scroll viewers and bars — layout-free, inertial, auto-hiding.",
        "Navigation" => "Move between views — nav view, tabs, breadcrumbs, pagers.",
        "Dialogs & flyouts" => "Transient surfaces — dialogs, flyouts, teaching tips, and pickers.",
        "Text" => "Read and edit text — blocks, boxes, and rich runs on DirectWrite.",
        "Media" => "Images and video — async decode, GPU residency, and playback.",
        "Collections" => "Data-driven lists and grids over the virtualization substrate.",
        "Menus & toolbars" => "Command surfaces — menu bars, command bars, and app bar buttons.",
        "Date & time" => "Pick dates and times — calendars and spinners.",
        "Patterns" => "UX recipes built on the engine — the Expressive Motion Kit and skeleton/shimmer loading.",
        "App services" => "Engine features WinUI lacks — localization, signals-native validation, and the OS-services pillars.",
        "Samples" => "End-to-end showcases — the driving app (Wavee) and protected-media pipelines.",
        _ => "Explore the pages in this section.",
    };
}
