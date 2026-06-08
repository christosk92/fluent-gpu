using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Navigation / Layout / Media / Dialogs control demo pages (WinUI Gallery parity, batch 2) ──────────

sealed class SplitViewPage : Component
{
    public override Element Render() => GalleryPage.Shell("SplitView",
        "A container with two views: a side pane and the main content area.",
        ControlExample.Build("A SplitView",
            new BoxEl
            {
                Height = 220, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
                Children =
                [
                    SplitView.Create(
                        new BoxEl { Padding = Edges4.All(12), Children = [VStack(8, BodyStrong("Pane"), Body("Navigation").Secondary())] },
                        new BoxEl { Padding = Edges4.All(16), Children = [Body("Main content area.")] },
                        paneWidth: 200f),
                ],
            }));
}

sealed class BreadcrumbBarPage : Component
{
    static readonly string[] Crumbs = { "Home", "Documents", "Design", "Specs" };
    public override Element Render() => GalleryPage.Shell("BreadcrumbBar",
        "Shows the trail of navigation from the home/root location to the current one.",
        ControlExample.Build("A BreadcrumbBar", BreadcrumbBar.Create(Crumbs)));
}

sealed class SelectorBarPage : Component
{
    static readonly string[] Items = { "All", "Photos", "Videos", "Folders" };
    public override Element Render()
    {
        var (sel, setSel) = UseState(0);
        return GalleryPage.Shell("SelectorBar",
            "A horizontal, single-select list with an accent underline on the selected item.",
            ControlExample.Build("A SelectorBar", SelectorBar.Create(Items, sel, setSel), output: GalleryPage.LiveText(() => Items[sel])));
    }
}

sealed class TabViewPage : Component
{
    static readonly string[] Tabs = { "Document 1", "Document 2", "Document 3" };
    public override Element Render() => GalleryPage.Shell("TabView",
        "Displays a set of tabs and their content — for managing multiple documents or pages.",
        ControlExample.Build("A TabView",
            new BoxEl { Height = 240, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true, Children = [TabView.Create(Tabs)] }));
}

sealed class PersonPicturePage : Component
{
    public override Element Render() => GalleryPage.Shell("PersonPicture",
        "Displays the avatar image or initials for a person.",
        ControlExample.Build("Initials", HStack(12, PersonPicture.Create("JD"), PersonPicture.Create("AB", 64f), PersonPicture.Create("CK", 32f))));
}

sealed class FlyoutPage : Component
{
    public override Element Render() => GalleryPage.Shell("Flyout",
        "A lightweight contextual popup that shows arbitrary content, dismissed by clicking away.",
        ControlExample.Build("A Flyout", FlyoutButton.Create("Open flyout",
            () => VStack(8, BodyStrong("All items will be removed."), Button.Accent("Yes, delete", () => { })))));
}

// Category overview pages (the expandable group keys land here when selected).
sealed class NavigationOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Navigation",
        "Controls for moving through app content: BreadcrumbBar, SelectorBar, TabView.");
}

sealed class DialogsOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Dialogs & flyouts",
        "Contextual popups and dialogs: Flyout.");
}

sealed class MediaOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Media",
        "Media controls: PersonPicture, Image.");
}
