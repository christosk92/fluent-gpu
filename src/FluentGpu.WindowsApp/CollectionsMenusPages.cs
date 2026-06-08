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
    public override Element Render() => GalleryPage.Shell("ListView",
        "A vertical, single-select list of data items.",
        ControlExample.Build("A ListView",
            new BoxEl { Width = 280, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(0, 4, 0, 4), Children = [ListView.Create(Items)] }));
}

sealed class GridViewPage : Component
{
    static readonly string[] Items = { "Item 1", "Item 2", "Item 3", "Item 4", "Item 5", "Item 6", "Item 7", "Item 8" };
    public override Element Render() => GalleryPage.Shell("GridView",
        "A grid of data items — for image-rich collections.",
        ControlExample.Build("A GridView", GridView.Create(Items, columns: 4)));
}

sealed class FlipViewPage : Component
{
    static readonly string[] Items = { "Page 1", "Page 2", "Page 3", "Page 4" };
    public override Element Render() => GalleryPage.Shell("FlipView",
        "Lets you flip through a collection of items, one at a time.",
        ControlExample.Build("A FlipView", FlipView.Create(Items, width: 420f, height: 220f)));
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
    public override Element Render() => GalleryPage.Shell("TreeView",
        "Displays hierarchical data with expandable and collapsible nodes.",
        ControlExample.Build("A TreeView",
            new BoxEl { Width = 320, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(0, 6, 0, 6), Children = [TreeView.Create(Roots)] }));
}

sealed class AppBarButtonPage : Component
{
    public override Element Render() => GalleryPage.Shell("AppBarButton",
        "A command button for a CommandBar — a vertical icon over a label.",
        ControlExample.Build("A CommandBar of AppBarButtons",
            new BoxEl
            {
                Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
                BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    AppBarButton.Create(Icons.Accept, "Add", () => { }),
                    AppBarButton.Create(Icons.Tag, "Edit", () => { }),
                    AppBarButton.Create(Icons.Share, "Share", () => { }),
                    AppBarButton.Create(Icons.Cancel, "Delete", () => { }),
                    AppBarButton.Create(Icons.Settings, "Settings", () => { }),
                ],
            }));
}

sealed class MenuBarPage : Component
{
    public override Element Render() => GalleryPage.Shell("MenuBar",
        "Provides a quick and organized way to expose commands in top-level menus.",
        ControlExample.Build("A MenuBar", MenuBar.Create(new[]
        {
            new MenuBarItem("File", new[] { new MenuFlyoutItem("New"), new MenuFlyoutItem("Open"), MenuFlyoutItem.Separator, new MenuFlyoutItem("Exit") }),
            new MenuBarItem("Edit", new[] { new MenuFlyoutItem("Cut"), new MenuFlyoutItem("Copy"), new MenuFlyoutItem("Paste") }),
            new MenuBarItem("View", new[] { new MenuFlyoutItem("Zoom In"), new MenuFlyoutItem("Zoom Out"), new MenuFlyoutItem("Full Screen") }),
        })));
}

// Category overview pages.
sealed class CollectionsOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Collections",
        "Controls for showing collections of data: ListView, GridView, FlipView, TreeView, ItemsRepeater.");
}

sealed class MenusOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Menus & toolbars",
        "Command surfaces: MenuBar, AppBarButton / CommandBar, MenuFlyout.");
}
