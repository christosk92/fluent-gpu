using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Collections / Menus & toolbars demo pages (WinUI Gallery parity, batch 3) ──────────

sealed class ListViewPage : Component
{
    static readonly string[] Items = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };

    public override Element Render()
    {
        var selected = UseSignal(0);   // a default selection so the accent selection bar is visible (WinUI gallery parity)
        var multi = UseMemo(static () => new SelectionModel());
        var drinks = UseMemo(static () => new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" });
        var order = UseSignal(0);
        _ = order.Value;   // re-render after a drag-reorder commit (refreshes the template closures)

        return GalleryPage.Shell("ListView",
            "A vertical, selectable list of data items.",
            ControlExample.Build("A simple ListView",
                Card(ListView.Create(Items, selected)),
                output: GalleryPage.LiveText(() => selected.Value >= 0 ? Items[selected.Value] : "—"),
                code: """
                static readonly string[] Items = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };
                var selected = UseSignal(0);

                ListView.Create(Items, selected)
                """),
            ControlExample.Build("Multiple selection",
                Card(ListView.Create(Items.Length,
                    i => new TextEl(Items[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Items[i])),
                description: "SelectionMode.Multiple slides in the inline checkboxes; the SelectionModel stores the selected ranges (Ctrl+A selects all).",
                output: GalleryPage.LiveText(() => { _ = multi.Version.Value; return $"{multi.SelectedCount} selected"; }),
                code: """
                var multi = UseMemo(static () => new SelectionModel());

                ListView.Create(Items.Length,
                    i => new TextEl(Items[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Items[i])
                """),
            ControlExample.Build("Drag to reorder",
                Card(ListView.Create(drinks.Count,
                    i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(drinks, from, to); order.Value = order.Peek() + 1; },
                    itemText: i => drinks[i],
                    keyOf: i => drinks[i])),
                description: "CanReorderItems: drag a row — displaced rows part after the 200ms WinUI live-reorder dwell, then the commit moves the item.",
                output: GalleryPage.LiveText(() => { _ = order.Value; return string.Join(" · ", drinks); }),
                code: """
                var drinks = UseMemo(static () => new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" });
                var order = UseSignal(0);

                ListView.Create(drinks.Count,
                    i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(drinks, from, to); order.Value = order.Peek() + 1; },
                    itemText: i => drinks[i],
                    keyOf: i => drinks[i])
                """));
    }

    static Element Card(Element list) => new BoxEl { Width = 280, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(0, 4, 0, 4), Children = [list] };
}

sealed class GridViewPage : Component
{
    static readonly string[] Items = { "Item 1", "Item 2", "Item 3", "Item 4", "Item 5", "Item 6", "Item 7", "Item 8" };

    public override Element Render()
    {
        var multi = UseMemo(static () => new SelectionModel());
        var colors = UseMemo(static () => new List<string> { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Pink", "Teal" });
        var order = UseSignal(0);
        _ = order.Value;   // re-render after a drag-reorder commit (refreshes the template closures)

        return GalleryPage.Shell("GridView",
            "A grid of data items — for image-rich collections.",
            ControlExample.Build("A GridView", GridView.Create(Items, columns: 4),
                code: """
                static readonly string[] Items = { "Item 1", "Item 2", "Item 3", "Item 4", "Item 5", "Item 6", "Item 7", "Item 8" };

                GridView.Create(Items, columns: 4)
                """),
            ControlExample.Build("Multiple selection",
                GridView.Create(Items.Length, i => Tile(Items[i]), columns: 4, tileHeight: 96f,
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Items[i]),
                description: "SelectionMode.Multiple shows the top-right overlay check square; selected tiles get the 2px accent border with the inner ring.",
                output: GalleryPage.LiveText(() => { _ = multi.Version.Value; return $"{multi.SelectedCount} selected"; }),
                code: """
                var multi = UseMemo(static () => new SelectionModel());

                Element Tile(string label) => new BoxEl
                {
                    Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary }],
                };

                GridView.Create(Items.Length, i => Tile(Items[i]), columns: 4, tileHeight: 96f,
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Items[i])
                """),
            ControlExample.Build("Drag to reorder",
                GridView.Create(colors.Count, i => Tile(colors[i]), columns: 4, tileHeight: 96f,
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(colors, from, to); order.Value = order.Peek() + 1; },
                    keyOf: i => colors[i]),
                description: "2-D live reorder: drag a tile — displaced tiles part after the 300ms WinUI grid dwell, then the commit moves the item.",
                output: GalleryPage.LiveText(() => { _ = order.Value; return string.Join(" · ", colors); }),
                code: """
                var colors = UseMemo(static () => new List<string> { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Pink", "Teal" });
                var order = UseSignal(0);

                GridView.Create(colors.Count, i => Tile(colors[i]), columns: 4, tileHeight: 96f,
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(colors, from, to); order.Value = order.Peek() + 1; },
                    keyOf: i => colors[i])
                """));
    }

    static Element Tile(string label) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary }],
    };
}

sealed class FlipViewPage : Component
{
    static readonly string[] Items = { "Page 1", "Page 2", "Page 3", "Page 4" };
    static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };

    public override Element Render() => GalleryPage.Shell("FlipView",
        "Lets you flip through a collection of items, one at a time.",
        ControlExample.Build("A FlipView", FlipView.Create(Items, width: 420f, height: 220f),
            description: "The thin edge bars flip between items (WinUI's HorizontalPrevious/Next buttons); navigation wraps around.",
            code: """
            static readonly string[] Items = { "Page 1", "Page 2", "Page 3", "Page 4" };

            FlipView.Create(Items, width: 420f, height: 220f)
            """),
        ControlExample.Build("A compact FlipView", FlipView.Create(Seasons, width: 240f, height: 140f),
            description: "Width and height size the clipped frame.",
            code: """
            static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };

            FlipView.Create(Seasons, width: 240f, height: 140f)
            """));
}

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
            ControlExample.Build("A TreeView",
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
            ControlExample.Build("Multi-select with checkboxes",
                Card(TreeView.Create(Roots, null, isMultiSelectEnabled: true,
                    selectionChanged: (n, on) => setToggled($"{n.Label}: {(on ? "selected" : "cleared")}"))),
                description: "Toggling a parent cascades through its subtree; a partially-selected parent shows the indeterminate dash.",
                output: BodyStrong(toggled),
                code: """
                TreeView.Create(Roots, null, isMultiSelectEnabled: true,
                    selectionChanged: (n, on) => setToggled($"{n.Label}: {(on ? "selected" : "cleared")}"))
                """),
            ControlExample.Build("Drag to reorder siblings",
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

sealed class AppBarButtonPage : Component
{
    public override Element Render()
    {
        var (last, setLast) = UseState("—");
        var (compactLast, setCompactLast) = UseState("—");
        var (saves, setSaves) = UseState(0);

        return GalleryPage.Shell("AppBarButton",
            "A command button for a CommandBar — a vertical icon over a label.",
            ControlExample.Build("A bar of AppBarButtons",
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
            ControlExample.Build("Compact AppBarButtons",
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
            ControlExample.Build("A disabled AppBarButton",
                Strip(AppBarButton.Create(Icons.Share, "Share", () => { }, enabled: false)),
                code: """
                AppBarButton.Create(Icons.Share, "Share", () => { }, enabled: false)
                """),
            ControlExample.Build("A keyboard accelerator",
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
            ControlExample.Build("A simple MenuBar",
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
            ControlExample.Build("Submenus, toggles, and radio items",
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
sealed class CollectionsOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Collections", "Controls for showing collections of data.",
            GalleryPage.CategoryGrid("Collections", navigate));
    }
}

sealed class MenusOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Menus & toolbars", "Command surfaces: menus, app bars, and toolbars.",
            GalleryPage.CategoryGrid("Menus & toolbars", navigate));
    }
}
