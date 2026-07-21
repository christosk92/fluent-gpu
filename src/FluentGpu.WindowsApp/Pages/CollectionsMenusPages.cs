using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Collections / Menus & toolbars demo pages (WinUI Gallery parity, batch 3) ──────────

// ListViewPage and GridViewPage were deleted here: ListView/GridView are no longer standalone controls — their
// coverage moved onto the flagship ItemsViewPage (MiscPages.cs) as the List/Grid preset card groups (simple +
// multiple + reorder, same Coffees/drinks/GridItems/colors data), via ItemsView.List(...) / ItemsView.Grid(...).

[GalleryPage("FlipView", "FlipView", "Collections", Icon = Icons.Picture)]
sealed partial class FlipViewPage : Component
{
    static readonly string[] Items = { "Page 1", "Page 2", "Page 3", "Page 4" };
    static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };

    public override Element Render() => GalleryPage.Shell("FlipView",
        "Lets you flip through a collection of items, one at a time.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(CompactSample));

    [Sample("A FlipView", Description = "The thin edge bars flip between items (WinUI's HorizontalPrevious/Next buttons); navigation wraps around.")]
    static Element Basic()
    {
        string[] items = { "Page 1", "Page 2", "Page 3", "Page 4" };
        return FlipView.Create(items, width: 420f, height: 220f);
    }

    [Sample("A compact FlipView", Description = "Width and height size the clipped frame.")]
    static Element Compact()
    {
        string[] seasons = { "Spring", "Summer", "Autumn", "Winter" };
        return FlipView.Create(seasons, width: 240f, height: 140f);
    }
}

[GalleryPage("TreeView", "TreeView", "Collections", Icon = Icons.List)]
sealed partial class TreeViewPage : Component
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

    static readonly Signal<string> _invoked = new("—");
    static readonly Signal<string> _toggled = new("—");

    public override Element Render() => GalleryPage.Shell("TreeView",
        "Displays hierarchical data with expandable and collapsible nodes.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(MultiSelectSample),
        ExampleCard.Show(ReorderSample));

    [Sample("A TreeView", Description = "The chevron is its own hit target (expand-only); clicking a row selects it and raises ItemInvoked.")]
    static Element Basic() => VStack(8,
        Card(TreeView.Create(Roots, null, itemInvoked: n => _invoked.Value = n.Label)),
        GalleryPage.LiveText(() => $"Invoked: {_invoked.Value}"));

    [Sample("Multi-select with checkboxes", Description = "Toggling a parent cascades through its subtree; a partially-selected parent shows the indeterminate dash.")]
    static Element MultiSelect() => VStack(8,
        Card(TreeView.Create(Roots, null, isMultiSelectEnabled: true,
            selectionChanged: (n, on) => _toggled.Value = $"{n.Label}: {(on ? "selected" : "cleared")}")),
        GalleryPage.LiveText(() => _toggled.Value));

    [Sample("Drag to reorder siblings", Description = "Drag a node among its siblings (whole subtrees move together; an expanded node collapses first) — or Ctrl+Up/Down on the focused node.")]
    static Element Reorder()
    {
        // With no onReorder handler the tree moves the node in place (WinUI mutates its own node tree).
        return Card(TreeView.Create(Chapters, null, canReorderItems: true));
    }

    static Element Card(Element tree) => new BoxEl { Width = 320, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(0, 6, 0, 6), Children = [tree] };
}

[GalleryPage("AppBarButton", "AppBarButton", "Menus & toolbars", Icon = Icons.Accept)]
sealed partial class AppBarButtonPage : Component
{
    static readonly Signal<string> _last = new("—");
    static readonly Signal<string> _compactLast = new("—");
    static readonly Signal<int> _saves = new(0);

    public override Element Render() => GalleryPage.Shell("AppBarButton",
        "A command button for a CommandBar — a vertical icon over a label.",
        ExampleCard.Show(BarSample),
        ExampleCard.Show(CompactSample),
        ExampleCard.Show(DisabledSample),
        ExampleCard.Show(AcceleratorSample));

    [Sample("A bar of AppBarButtons")]
    static Element Bar() => VStack(8,
        Strip(
            AppBarButton.Create(Icons.Add, "Add", () => _last.Value = "Add"),
            AppBarButton.Create(Icons.Tag, "Edit", () => _last.Value = "Edit"),
            AppBarButton.Create(Icons.Share, "Share", () => _last.Value = "Share"),
            AppBarButton.Create(Icons.Cancel, "Delete", () => _last.Value = "Delete"),
            AppBarButton.Create(Icons.Settings, "Settings", () => _last.Value = "Settings")),
        GalleryPage.LiveText(() => $"Last: {_last.Value}"));

    [Sample("Compact AppBarButtons", Description = "isCompact collapses the label — icon-only at the 48px compact height (the closed CommandBar state).")]
    static Element Compact() => VStack(8,
        Strip(
            AppBarButton.Create(Icons.Play, "Play", () => _compactLast.Value = "Play", isCompact: true),
            AppBarButton.Create(Icons.Pause, "Pause", () => _compactLast.Value = "Pause", isCompact: true),
            AppBarButton.Create(Icons.Stop, "Stop", () => _compactLast.Value = "Stop", isCompact: true)),
        GalleryPage.LiveText(() => $"Last: {_compactLast.Value}"));

    [Sample("A disabled AppBarButton")]
    static Element Disabled() => Strip(AppBarButton.Create(Icons.Share, "Share", () => { }, enabled: false));

    [Sample("A keyboard accelerator", Description = "The Ctrl+S chord invokes the button from anywhere in the window (WinUI KeyboardAccelerator).")]
    static Element Accelerator() => VStack(8,
        Strip(AppBarButton.Create(Icons.Document, "Save", () => _saves.Value++,
            accelerator: new KeyAccelerator(Keys.S, KeyModifiers.Ctrl))),
        GalleryPage.LiveText(() => $"Saved {_saves.Value}× — try Ctrl+S"));

    static Element Strip(params Element[] buttons) => new BoxEl
    {
        Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children = buttons,
    };
}

[GalleryPage("MenuBar", "MenuBar", "Menus & toolbars", Icon = Icons.More)]
sealed partial class MenuBarPage : Component
{
    static readonly Signal<string> _cmd1 = new("—");
    static readonly Signal<string> _cmd2 = new("—");
    static readonly Signal<bool> _wrap = new(true);
    static readonly Signal<string> _theme = new("System");

    public override Element Render() => GalleryPage.Shell("MenuBar",
        "Provides a quick and organized way to expose commands in top-level menus.",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(SubmenusSample));

    [Sample("A simple MenuBar", Description = "Alt+F / Alt+E / Alt+H open the menus; while one is open, hover or Left/Right switches to the adjacent menu.")]
    static Element Simple() => VStack(8,
        MenuBar.Create(new[]
        {
            new MenuBarItem("File", new[] { new MenuFlyoutItem("New", null, true, () => _cmd1.Value = "New"), new MenuFlyoutItem("Open", null, true, () => _cmd1.Value = "Open"), MenuFlyoutItem.Separator, new MenuFlyoutItem("Exit", null, true, () => _cmd1.Value = "Exit") }),
            new MenuBarItem("Edit", new[] { new MenuFlyoutItem("Cut", null, true, () => _cmd1.Value = "Cut"), new MenuFlyoutItem("Copy", null, true, () => _cmd1.Value = "Copy"), new MenuFlyoutItem("Paste", null, true, () => _cmd1.Value = "Paste") }),
            new MenuBarItem("Help", new[] { new MenuFlyoutItem("About", null, true, () => _cmd1.Value = "About") }),
        }),
        GalleryPage.LiveText(() => $"Last: {_cmd1.Value}"));

    [Sample("Submenus, toggles, and radio items", Description = "Cascading submenus open on hover or Right-arrow; Toggle and RadioItem rows carry the check/bullet column; AcceleratorText is the trailing hint.")]
    static Element Submenus() => VStack(8,
        MenuBar.Create(new[]
        {
            new MenuBarItem("File", new[]
            {
                MenuFlyoutItem.SubMenu("New", new[]
                {
                    new MenuFlyoutItem("Document", Icons.Document, true, () => _cmd2.Value = "New document"),
                    new MenuFlyoutItem("Window", Icons.OpenInNewWindow, true, () => _cmd2.Value = "New window"),
                }),
                new MenuFlyoutItem("Open…", Icons.Folder, true, () => _cmd2.Value = "Open") { AcceleratorText = "Ctrl+O" },
                new MenuFlyoutItem("Save", Icons.Document, true, () => _cmd2.Value = "Save") { AcceleratorText = "Ctrl+S" },
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem("Exit", null, true, () => _cmd2.Value = "Exit"),
            }),
            new MenuBarItem("View", new[]
            {
                MenuFlyoutItem.Toggle("Word wrap", _wrap.Value, () => _wrap.Value = !_wrap.Value),
                MenuFlyoutItem.Separator,
                MenuFlyoutItem.RadioItem("Light", _theme.Value == "Light", () => _theme.Value = "Light"),
                MenuFlyoutItem.RadioItem("Dark", _theme.Value == "Dark", () => _theme.Value = "Dark"),
                MenuFlyoutItem.RadioItem("System", _theme.Value == "System", () => _theme.Value = "System"),
            }),
        }),
        GalleryPage.LiveText(() => $"Last: {_cmd2.Value} · Word wrap: {(_wrap.Value ? "on" : "off")} · Theme: {_theme.Value}"));
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
            // sample-drift-risk: the live element's .WithContextMenu(svc, …) needs svc = UseContext(Overlay.Service);
            // a static [Sample] factory can't consume context, so these three examples stay ExampleCard.Build + code strings.
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
