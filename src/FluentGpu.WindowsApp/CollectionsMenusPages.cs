using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Collections / Menus & toolbars demo pages (WinUI Gallery parity, batch 3) ──────────

// ListViewPage and GridViewPage were deleted here: ListView/GridView are no longer standalone controls — their
// coverage moved onto the flagship ItemsViewPage (MiscPages.cs) as the List/Grid preset card groups (simple +
// multiple + reorder, same Coffees/drinks/GridItems/colors data), via ItemsView.List(...) / ItemsView.Grid(...).

[GalleryPage("FlipView", "FlipView", "Collections", Icon = Icons.Picture)]
sealed class FlipViewPage : Component
{
    static readonly string[] Items = { "Page 1", "Page 2", "Page 3", "Page 4" };
    static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };

    public override Element Render() => GalleryPage.Shell("FlipView",
        "Lets you flip through a collection of items, one at a time.",
        ExampleCard.Build("A FlipView", FlipView.Create(Items, width: 420f, height: 220f),
            description: "The thin edge bars flip between items (WinUI's HorizontalPrevious/Next buttons); navigation wraps around.",
            code: """
            static readonly string[] Items = { "Page 1", "Page 2", "Page 3", "Page 4" };

            FlipView.Create(Items, width: 420f, height: 220f)
            """),
        ExampleCard.Build("A compact FlipView", FlipView.Create(Seasons, width: 240f, height: 140f),
            description: "Width and height size the clipped frame.",
            code: """
            static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };

            FlipView.Create(Seasons, width: 240f, height: 140f)
            """));
}

[GalleryPage("TreeView", "TreeView", "Collections", Icon = Icons.List)]
sealed class TreeViewPage : Component
{
    static readonly TreeNode[] Roots =
    {
        new TreeNode("Documents",
            new TreeNode("Work", new TreeNode("Q1 Report.docx"), new TreeNode("Q2 Report.docx")),
            new TreeNode("Personal", new TreeNode("Notes.txt"))),
        new TreeNode("Pictures",
            new TreeNode("Vacation"),
            new TreeNode("Family")),
    };

    // Root siblings as a List so the root-level drag commit can RemoveAt+Insert in place.
    static readonly List<TreeNode> Chapters = new()
    {
        new TreeNode("Chapter 1", new TreeNode("Section 1.1"), new TreeNode("Section 1.2")),
        new TreeNode("Chapter 2"),
        new TreeNode("Chapter 3", new TreeNode("Section 3.1")),
        new TreeNode("Chapter 4"),
    };

    public override Element Render()
    {
        var (invoked, setInvoked) = UseState("—");
        var (toggled, setToggled) = UseState("—");

        return GalleryPage.Shell("TreeView",
            "Displays hierarchical data with expandable and collapsible nodes.",
            ExampleCard.Build("A TreeView",
                Card(TreeView.Create(Roots, null, itemInvoked: n => setInvoked(n.Label))),
                description: "The chevron is its own hit target (expand-only); clicking a row selects it and raises ItemInvoked.",
                output: BodyStrong($"Invoked: {invoked}"),
                code: """
                static readonly TreeNode[] Roots =
                {
                    new TreeNode("Documents",
                        new TreeNode("Work", new TreeNode("Q1 Report.docx"), new TreeNode("Q2 Report.docx")),
                        new TreeNode("Personal", new TreeNode("Notes.txt"))),
                    new TreeNode("Pictures",
                        new TreeNode("Vacation"),
                        new TreeNode("Family")),
                };

                TreeView.Create(Roots, null, itemInvoked: n => setInvoked(n.Label))
                """),
            ExampleCard.Build("Multi-select with checkboxes",
                Card(TreeView.Create(Roots, null, isMultiSelectEnabled: true,
                    selectionChanged: (n, on) => setToggled($"{n.Label}: {(on ? "selected" : "cleared")}"))),
                description: "Toggling a parent cascades through its subtree; a partially-selected parent shows the indeterminate dash.",
                output: BodyStrong(toggled),
                code: """
                TreeView.Create(Roots, null, isMultiSelectEnabled: true,
                    selectionChanged: (n, on) => setToggled($"{n.Label}: {(on ? "selected" : "cleared")}"))
                """),
            ExampleCard.Build("Drag to reorder siblings",
                Card(TreeView.Create(Chapters, null, canReorderItems: true)),
                description: "Drag a node among its siblings (whole subtrees move together; an expanded node collapses first) — or Ctrl+Up/Down on the focused node.",
                code: """
                static readonly List<TreeNode> Chapters = new()
                {
                    new TreeNode("Chapter 1", new TreeNode("Section 1.1"), new TreeNode("Section 1.2")),
                    new TreeNode("Chapter 2"),
                    new TreeNode("Chapter 3", new TreeNode("Section 3.1")),
                    new TreeNode("Chapter 4"),
                };

                // With no onReorder handler the tree moves the node in place (WinUI mutates its own node tree).
                TreeView.Create(Chapters, null, canReorderItems: true)
                """));
    }

    static Element Card(Element tree) => new BoxEl { Width = 320, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(0, 6, 0, 6), Children = [tree] };
}

[GalleryPage("AppBarButton", "AppBarButton", "Menus & toolbars", Icon = Icons.Accept)]
sealed class AppBarButtonPage : Component
{
    public override Element Render()
    {
        var (last, setLast) = UseState("—");
        var (compactLast, setCompactLast) = UseState("—");
        var (saves, setSaves) = UseState(0);

        return GalleryPage.Shell("AppBarButton",
            "A command button for a CommandBar — a vertical icon over a label.",
            ExampleCard.Build("A bar of AppBarButtons",
                Strip(
                    AppBarButton.Create(Icons.Add, "Add", () => setLast("Add")),
                    AppBarButton.Create(Icons.Tag, "Edit", () => setLast("Edit")),
                    AppBarButton.Create(Icons.Share, "Share", () => setLast("Share")),
                    AppBarButton.Create(Icons.Cancel, "Delete", () => setLast("Delete")),
                    AppBarButton.Create(Icons.Settings, "Settings", () => setLast("Settings"))),
                output: BodyStrong($"Last: {last}"),
                code: """
                HStack(4,
                    AppBarButton.Create(Icons.Add, "Add", () => setLast("Add")),
                    AppBarButton.Create(Icons.Tag, "Edit", () => setLast("Edit")),
                    AppBarButton.Create(Icons.Share, "Share", () => setLast("Share")),
                    AppBarButton.Create(Icons.Cancel, "Delete", () => setLast("Delete")),
                    AppBarButton.Create(Icons.Settings, "Settings", () => setLast("Settings")))
                """),
            ExampleCard.Build("Compact AppBarButtons",
                Strip(
                    AppBarButton.Create(Icons.Play, "Play", () => setCompactLast("Play"), isCompact: true),
                    AppBarButton.Create(Icons.Pause, "Pause", () => setCompactLast("Pause"), isCompact: true),
                    AppBarButton.Create(Icons.Stop, "Stop", () => setCompactLast("Stop"), isCompact: true)),
                description: "isCompact collapses the label — icon-only at the 48px compact height (the closed CommandBar state).",
                output: BodyStrong($"Last: {compactLast}"),
                code: """
                HStack(4,
                    AppBarButton.Create(Icons.Play, "Play", () => setCompactLast("Play"), isCompact: true),
                    AppBarButton.Create(Icons.Pause, "Pause", () => setCompactLast("Pause"), isCompact: true),
                    AppBarButton.Create(Icons.Stop, "Stop", () => setCompactLast("Stop"), isCompact: true))
                """),
            ExampleCard.Build("A disabled AppBarButton",
                Strip(AppBarButton.Create(Icons.Share, "Share", () => { }, enabled: false)),
                code: """
                AppBarButton.Create(Icons.Share, "Share", () => { }, enabled: false)
                """),
            ExampleCard.Build("A keyboard accelerator",
                Strip(AppBarButton.Create(Icons.Document, "Save", () => setSaves(saves + 1),
                    accelerator: new KeyAccelerator(Keys.S, KeyModifiers.Ctrl))),
                description: "The Ctrl+S chord invokes the button from anywhere in the window (WinUI KeyboardAccelerator).",
                output: BodyStrong($"Saved {saves}× — try Ctrl+S"),
                code: """
                AppBarButton.Create(Icons.Document, "Save", () => setSaves(saves + 1),
                    accelerator: new KeyAccelerator(Keys.S, KeyModifiers.Ctrl))
                """));
    }

    static Element Strip(params Element[] buttons) => new BoxEl
    {
        Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children = buttons,
    };
}

[GalleryPage("MenuBar", "MenuBar", "Menus & toolbars", Icon = Icons.More)]
sealed class MenuBarPage : Component
{
    public override Element Render()
    {
        var (cmd1, setCmd1) = UseState("—");
        var (cmd2, setCmd2) = UseState("—");
        var (wrap, setWrap) = UseState(true);
        var (theme, setTheme) = UseState("System");

        return GalleryPage.Shell("MenuBar",
            "Provides a quick and organized way to expose commands in top-level menus.",
            ExampleCard.Build("A simple MenuBar",
                MenuBar.Create(new[]
                {
                    new MenuBarItem("File", new[] { new MenuFlyoutItem("New", null, true, () => setCmd1("New")), new MenuFlyoutItem("Open", null, true, () => setCmd1("Open")), MenuFlyoutItem.Separator, new MenuFlyoutItem("Exit", null, true, () => setCmd1("Exit")) }),
                    new MenuBarItem("Edit", new[] { new MenuFlyoutItem("Cut", null, true, () => setCmd1("Cut")), new MenuFlyoutItem("Copy", null, true, () => setCmd1("Copy")), new MenuFlyoutItem("Paste", null, true, () => setCmd1("Paste")) }),
                    new MenuBarItem("Help", new[] { new MenuFlyoutItem("About", null, true, () => setCmd1("About")) }),
                }),
                description: "Alt+F / Alt+E / Alt+H open the menus; while one is open, hover or Left/Right switches to the adjacent menu.",
                output: BodyStrong($"Last: {cmd1}"),
                code: """
                MenuBar.Create(new[]
                {
                    new MenuBarItem("File", new[] { new MenuFlyoutItem("New", null, true, () => setCmd("New")), new MenuFlyoutItem("Open", null, true, () => setCmd("Open")), MenuFlyoutItem.Separator, new MenuFlyoutItem("Exit", null, true, () => setCmd("Exit")) }),
                    new MenuBarItem("Edit", new[] { new MenuFlyoutItem("Cut", null, true, () => setCmd("Cut")), new MenuFlyoutItem("Copy", null, true, () => setCmd("Copy")), new MenuFlyoutItem("Paste", null, true, () => setCmd("Paste")) }),
                    new MenuBarItem("Help", new[] { new MenuFlyoutItem("About", null, true, () => setCmd("About")) }),
                })
                """),
            ExampleCard.Build("Submenus, toggles, and radio items",
                MenuBar.Create(new[]
                {
                    new MenuBarItem("File", new[]
                    {
                        MenuFlyoutItem.SubMenu("New", new[]
                        {
                            new MenuFlyoutItem("Document", Icons.Document, true, () => setCmd2("New document")),
                            new MenuFlyoutItem("Window", Icons.OpenInNewWindow, true, () => setCmd2("New window")),
                        }),
                        new MenuFlyoutItem("Open…", Icons.Folder, true, () => setCmd2("Open")) { AcceleratorText = "Ctrl+O" },
                        new MenuFlyoutItem("Save", Icons.Document, true, () => setCmd2("Save")) { AcceleratorText = "Ctrl+S" },
                        MenuFlyoutItem.Separator,
                        new MenuFlyoutItem("Exit", null, true, () => setCmd2("Exit")),
                    }),
                    new MenuBarItem("View", new[]
                    {
                        MenuFlyoutItem.Toggle("Word wrap", wrap, () => setWrap(!wrap)),
                        MenuFlyoutItem.Separator,
                        MenuFlyoutItem.RadioItem("Light", theme == "Light", () => setTheme("Light")),
                        MenuFlyoutItem.RadioItem("Dark", theme == "Dark", () => setTheme("Dark")),
                        MenuFlyoutItem.RadioItem("System", theme == "System", () => setTheme("System")),
                    }),
                }),
                description: "Cascading submenus open on hover or Right-arrow; Toggle and RadioItem rows carry the check/bullet column; AcceleratorText is the trailing hint.",
                output: BodyStrong($"Last: {cmd2} · Word wrap: {(wrap ? "on" : "off")} · Theme: {theme}"),
                code: """
                MenuBar.Create(new[]
                {
                    new MenuBarItem("File", new[]
                    {
                        MenuFlyoutItem.SubMenu("New", new[]
                        {
                            new MenuFlyoutItem("Document", Icons.Document, true, () => setCmd("New document")),
                            new MenuFlyoutItem("Window", Icons.OpenInNewWindow, true, () => setCmd("New window")),
                        }),
                        new MenuFlyoutItem("Open…", Icons.Folder, true, () => setCmd("Open")) { AcceleratorText = "Ctrl+O" },
                        new MenuFlyoutItem("Save", Icons.Document, true, () => setCmd("Save")) { AcceleratorText = "Ctrl+S" },
                        MenuFlyoutItem.Separator,
                        new MenuFlyoutItem("Exit", null, true, () => setCmd("Exit")),
                    }),
                    new MenuBarItem("View", new[]
                    {
                        MenuFlyoutItem.Toggle("Word wrap", wrap, () => setWrap(!wrap)),
                        MenuFlyoutItem.Separator,
                        MenuFlyoutItem.RadioItem("Light", theme == "Light", () => setTheme("Light")),
                        MenuFlyoutItem.RadioItem("Dark", theme == "Dark", () => setTheme("Dark")),
                        MenuFlyoutItem.RadioItem("System", theme == "System", () => setTheme("System")),
                    }),
                })
                """));
    }
}

// Category overview pages.
[GalleryPage("collections", "Collections", "Overview", Hidden = true)]
sealed class CollectionsOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Collections", "Controls for showing collections of data.",
            GalleryPage.CategoryGrid("Collections", navigate));
    }
}

[GalleryPage("menus", "Menus & toolbars", "Overview", Hidden = true)]
sealed class MenusOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Menus & toolbars", "Command surfaces: menus, app bars, and toolbars.",
            GalleryPage.CategoryGrid("Menus & toolbars", navigate));
    }
}

// Context menus attached with ContextMenu.Attach / .WithContextMenu — the one-liner over any element. Right-click, the
// Menu key / Shift+F10 on a focused row, or a touch long-press all open it. A non-empty Primary strip yields the
// Explorer command-bar shape; an empty Primary yields a plain vertical menu; a null / all-disabled model opens nothing.
[GalleryPage("ContextMenu", "ContextMenu", "Menus & toolbars", Icon = Icons.More)]
sealed class ContextMenuPage : Component
{
    static readonly string[] Tracks = { "Bohemian Rhapsody", "Stairway to Heaven", "Hotel California", "Imagine" };

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var (last, setLast) = UseState("—");
        var (liked, setLiked) = UseState(false);

        // 1) Track list — plain MENU style (empty Primary). The factory is lazy: it captures the row's title and the
        //    current Liked state and runs only at open, so each row's menu reflects live state.
        var rows = new Element[Tracks.Length];
        for (int i = 0; i < Tracks.Length; i++)
        {
            string title = Tracks[i];
            rows[i] = new BoxEl
            {
                Height = 36, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(12, 0, 12, 0),
                Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, Role = AutomationRole.Button,
                OnClick = () => setLast($"Played “{title}”"),
                Children = [new TextEl(title) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }],
            }.WithContextMenu(svc, () => new ContextMenuModel(new[]
            {
                new MenuFlyoutItem("Play", IconRef.Themed("Play"), Invoke: () => setLast($"Play “{title}”")),
                new MenuFlyoutItem("Add to queue", IconRef.Themed("AddToQueue"), Invoke: () => setLast($"Queued “{title}”")),
                MenuFlyoutItem.Toggle("Save to Liked Songs", liked, () => setLiked(!liked), IconRef.Themed("Like")),
                MenuFlyoutItem.SubMenu("Add to playlist", new[]
                {
                    new MenuFlyoutItem("New playlist", IconRef.Themed("Add"), Invoke: () => setLast("New playlist")),
                    new MenuFlyoutItem("Chill Vibes", Invoke: () => setLast("→ Chill Vibes")),
                    new MenuFlyoutItem("Focus", Invoke: () => setLast("→ Focus")),
                }, IconRef.Themed("Folder")),
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem("Go to album", IconRef.Themed("GoTo"), Invoke: () => setLast("Go to album")),
                new MenuFlyoutItem("Copy link", IconRef.Themed("Link"), Invoke: () => setLast("Copied link")) { AcceleratorText = "Ctrl+C" },
            }));
        }
        var trackList = new BoxEl
        {
            Direction = 1, Width = 320, Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
            BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(4, 4, 4, 4),
            Children = rows,
        };

        // 2) Rich target — COMMAND-BAR style (non-empty Primary): a horizontal quick-action strip over the labeled rows.
        var card = new BoxEl
        {
            Width = 200, Height = 120, Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
            BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl("Album card") { Size = 14f, Color = Tok.TextSecondary }],
        }.WithContextMenu(svc, () => new ContextMenuModel(
            new AppBarCommand[]
            {
                new(IconRef.Themed("Play"), "Play", () => setLast("Play")),
                new(IconRef.Themed("AddToQueue"), "Queue", () => setLast("Queue")),
                new(IconRef.Themed("Share"), "Share", () => setLast("Share")),
                new(IconRef.Themed("Like"), "Like", () => setLiked(!liked), Kind: AppBarCommandKind.ToggleButton, IsChecked: liked),
            },
            new MenuFlyoutItem[]
            {
                new("Go to album", IconRef.Themed("GoTo"), Invoke: () => setLast("Go to album")),
                new("Go to artist", IconRef.Themed("GoTo"), Invoke: () => setLast("Go to artist")),
                MenuFlyoutItem.Separator,
                new("Copy link", IconRef.Themed("Link"), Invoke: () => setLast("Copied link")) { AcceleratorText = "Ctrl+C" },
            }));

        // 3) Disabled / empty — an all-disabled model opens nothing (the right-click is inert).
        var emptyCard = new BoxEl
        {
            Width = 200, Height = 80, Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
            BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl("No actions") { Size = 14f, Color = Tok.TextSecondary }],
        }.WithContextMenu(svc, () => new ContextMenuModel(new[] { new MenuFlyoutItem("Unavailable", Enabled: false) }));

        return GalleryPage.Shell("Context menu",
            "Attach a Win11-style context menu to any element in one line with ContextMenu.Attach / .WithContextMenu — "
            + "right-click, the Menu key (or Shift+F10) on a focused row, or a touch long-press all open it.",
            ExampleCard.Build("Track rows (menu style)", trackList,
                description: "Right-click a track (or focus it and press the Menu key / Shift+F10). Items build lazily at open time, so the "
                + "toggle reflects the current “Liked” state; right-clicking another row opens its menu in one gesture; Esc or an outside click dismisses.",
                output: BodyStrong($"Last: {last}"),
                code: """
                row.WithContextMenu(svc, () => new ContextMenuModel(new[]
                {
                    new MenuFlyoutItem("Play", Icons.Play, Invoke: () => Play(track)),
                    MenuFlyoutItem.Toggle("Save to Liked Songs", liked, () => ToggleLike(), Icons.Accept),
                    MenuFlyoutItem.SubMenu("Add to playlist", playlistItems, Icons.Folder),
                    MenuFlyoutItem.Separator,
                    new MenuFlyoutItem("Copy link", Icons.Link) { AcceleratorText = "Ctrl+C" },
                }))
                """),
            ExampleCard.Build("Rich target (command-bar style)", card,
                description: "A non-empty Primary strip switches the body to the Explorer command-bar shape — a horizontal quick-action row over the labeled rows.",
                output: BodyStrong($"Last: {last}"),
                code: """
                card.WithContextMenu(svc, () => new ContextMenuModel(
                    Primary: new AppBarCommand[]
                    {
                        new(Icons.Play, "Play", Play),
                        new(Icons.Accept, "Like", ToggleLike, Kind: AppBarCommandKind.ToggleButton, IsChecked: liked),
                    },
                    Rows: new MenuFlyoutItem[]
                    {
                        new("Go to album", Icons.OpenInNewWindow, Invoke: GoToAlbum),
                        new("Copy link", Icons.Link) { AcceleratorText = "Ctrl+C" },
                    }))
                """),
            ExampleCard.Build("Disabled / empty", emptyCard,
                description: "A null factory result, or a model whose entries are all disabled/separators, opens nothing — the right-click is inert.",
                code: """
                card.WithContextMenu(svc, () => new ContextMenuModel(
                    new[] { new MenuFlyoutItem("Unavailable", Enabled: false) }))
                """));
    }
}
