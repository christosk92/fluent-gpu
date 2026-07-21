using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Navigation / Layout / Media / Dialogs control demo pages (WinUI Gallery parity, batch 2) ──────────

[GalleryPage("SplitView", "SplitView", "Layout", Icon = Icons.Grid)]
sealed partial class SplitViewPage : Component
{
    public override Element Render() => GalleryPage.Shell("SplitView",
        "A container with two views: a side pane and the main content area.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(TogglingSample),
        ExampleCard.Show(CustomWidthSample));

    [Sample("A SplitView")]
    static Element Basic()
    {
        var pane = new BoxEl { Padding = Edges4.All(12), Children = [VStack(8, BodyStrong("Pane"), Body("Navigation").Secondary())] };
        var content = new BoxEl { Padding = Edges4.All(16), Children = [Body("Main content area.")] };
        return Frame(SplitView.Create(pane, content, paneWidth: 200f));
    }

    [Sample("Toggling the pane (isPaneOpen)")]
    static Element Toggling(Knobs k)
    {
        var open = k.Toggle("IsPaneOpen", true);
        return Frame(SplitView.Create(DemoPane(), DemoContent(), paneWidth: 200f, isPaneOpen: open));
    }

    [Sample("A custom pane width")]
    static Element CustomWidth() => Frame(SplitView.Create(DemoPane(), DemoContent(), paneWidth: 120f));

    static Element DemoPane() => new BoxEl { Padding = Edges4.All(12), Children = [VStack(8, BodyStrong("Pane"), Body("Navigation").Secondary())] };
    static Element DemoContent() => new BoxEl { Padding = Edges4.All(16), Children = [Body("Main content area.")] };
    static Element Frame(Element splitView) => new BoxEl
    {
        Width = 480, Height = 220, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children = [splitView],
    };
}

[GalleryPage("BreadcrumbBar", "BreadcrumbBar", "Navigation", Icon = Icons.List)]
sealed partial class BreadcrumbBarPage : Component
{
    static readonly string[] Crumbs = { "Home", "Documents", "Design", "Specs" };
    static readonly Signal<int> _depth = new(Crumbs.Length);

    public override Element Render() => GalleryPage.Shell("BreadcrumbBar",
        "Shows the trail of navigation from the home/root location to the current one.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(NavigatingSample));

    [Sample("A BreadcrumbBar")]
    static Element Basic()
    {
        string[] crumbs = { "Home", "Documents", "Design", "Specs" };
        return BreadcrumbBar.Create(crumbs);
    }

    [Sample("Navigating with onChange", Description = "Clicking a crumb trims the trail back to it (the WinUI ItemClicked pattern).")]
    static Element Navigating() => VStack(8,
        BreadcrumbBar.Create(Crumbs[.._depth.Value], i => _depth.Value = i + 1),
        GalleryPage.LiveText(() => $"Location: {Crumbs[_depth.Value - 1]}"),
        Button.Standard("Reset trail", () => _depth.Value = Crumbs.Length));
}

[GalleryPage("SelectorBar", "SelectorBar", "Navigation", Icon = Icons.List)]
sealed partial class SelectorBarPage : Component
{
    static readonly string[] Items = { "All", "Photos", "Videos", "Folders" };
    static readonly string[] Views = { "Recent", "Shared", "Favorites" };
    static readonly Signal<int> _sel = new(0);
    static readonly Signal<int> _view = new(0);

    public override Element Render() => GalleryPage.Shell("SelectorBar",
        "A horizontal, single-select list with an accent underline on the selected item.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(SwitchingViewsSample));

    [Sample("A SelectorBar")]
    static Element Basic() => VStack(8,
        SelectorBar.Create(Items, _sel),
        GalleryPage.LiveText(() => Items[_sel.Value]));

    [Sample("Switching views with a SelectorBar")]
    static Element SwitchingViews() => VStack(12,
        SelectorBar.Create(Views, _view),
        new BoxEl
        {
            Width = 360, Height = 100, Padding = Edges4.All(16), Fill = Tok.FillSolidBase,
            BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
            Children = [GalleryPage.LiveText(() => $"Content of the {Views[_view.Value]} view.")],
        });
}

[GalleryPage("TabView", "TabView", "Navigation", Icon = Icons.Document)]
sealed partial class TabViewPage : Component
{
    static readonly string[] Tabs = { "Document 1", "Document 2", "Document 3" };

    // The "+" appends a new tab, mirroring the WinUI Gallery's TabView_AddButtonClick. A render-stable counter keeps
    // the numbering monotonic across re-renders (the control seeds its list once at mount).
    static int _nextDoc = Tabs.Length + 1;

    public override Element Render() => GalleryPage.Shell("TabView",
        "Displays a set of tabs and their content — for managing multiple documents or pages.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(RestyledSample));

    [Sample("A TabView", Description = "The \"+\" adds a new document tab; tabs reflow smoothly as they are added or closed.")]
    static Element Basic()
    {
        Func<TabViewItem?> addDoc = () => new TabViewItem { Header = $"Document {_nextDoc++}", Icon = Icons.Document };
        return Frame(TabView.Create(Tabs, onAddTabButtonClick: addDoc));
    }

    [Sample("Restyling tabs with template parts", Description = "Template parts restyle the strip and tab labels without re-templating the control.")]
    static Element Restyled()
    {
        var parts = new TemplateParts { [TabView.PartStrip] = s => s with { Fill = Tok.FillSolidBaseAlt } };
        parts.Set<TextEl>(TabView.PartTabLabel, t => t with { Size = 14f });
        Func<TabViewItem?> addDoc = () => new TabViewItem { Header = $"Document {_nextDoc++}", Icon = Icons.Document };
        return Frame(Embed.Comp(() => new TabView { Tabs = Tabs, Parts = parts, OnAddTabButtonClick = addDoc }));
    }

    static Element Frame(Element tabView) => new BoxEl
    {
        Height = 240, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children = [tabView],
    };
}

[GalleryPage("PersonPicture", "PersonPicture", "Media", Icon = Icons.FavoriteStar)]
sealed partial class PersonPicturePage : Component
{
    public override Element Render() => GalleryPage.Shell("PersonPicture",
        "Displays the avatar image or initials for a person.",
        ExampleCard.Show(InitialsSample),
        ExampleCard.Show(ProfilePhotoSample),
        ExampleCard.Show(BadgesSample),
        ExampleCard.Show(GroupAvatarSample),
        ExampleCard.Show(FromDisplayNameSample));

    [Sample("Initials at different sizes")]
    static Element Initials() => HStack(12,
        PersonPicture.Create("JD"),        // default diameter 96
        PersonPicture.Create("AB", 64f),
        PersonPicture.Create("CK", 32f));

    [Sample("A profile photo", Description = "The photo is circle-cropped (UniformToFill) and takes precedence over the initials.")]
    static Element ProfilePhoto() => HStack(12,
        // Any local image path works; the photo wins over the initials once it loads.
        PersonPicture.Create("JD", imageSourcePath: Assets.ControlImage("PersonPicture.png")),
        PersonPicture.Create("JD", 64f, imageSourcePath: Assets.ControlImage("PersonPicture.png")));

    [Sample("Badges", Description = "A number badge caps at 99+; a glyph badge renders only when badgeNumber is 0.")]
    static Element Badges() => HStack(12,
        PersonPicture.Create("JD", badgeNumber: 5),
        PersonPicture.Create("JD", badgeNumber: 150),          // >99 renders "99+"
        PersonPicture.Create("JD", badgeGlyph: Icons.Accept));

    [Sample("A group avatar", Description = "isGroup shows the People glyph — it outranks initials and the photo.")]
    static Element GroupAvatar() => PersonPicture.Create("", isGroup: true);

    [Sample("Initials from a display name", Description = "Initials derive from the display name (first letter of the first and last word, trailing brackets stripped); with neither, the generic contact glyph shows.")]
    static Element FromDisplayName() => HStack(12,
        Labeled(PersonPicture.Create("", displayName: "John Doe"), "John Doe"),                             // JD
        Labeled(PersonPicture.Create("", displayName: "Maria de la Cruz (Contoso)"), "Maria de la Cruz (Contoso)"),   // MC — "(Contoso)" is stripped
        Labeled(PersonPicture.Create(""), "(no name)"));                                                    // no name -> contact glyph

    static Element Labeled(Element avatar, string caption)
        => new BoxEl { Direction = 1, Gap = 8, AlignItems = FlexAlign.Center, Children = [avatar, Caption(caption)] };
}

[GalleryPage("Flyout", "Flyout", "Dialogs & flyouts", Icon = Icons.More)]
sealed partial class FlyoutPage : Component
{
    static readonly Signal<string> _msg = new("—");

    public override Element Render() => GalleryPage.Shell("Flyout",
        "A lightweight contextual popup that shows arbitrary content, dismissed by clicking away.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(PlacementSample));

    [Sample("A Flyout")]
    static Element Basic() => VStack(8,
        FlyoutButton.Create("Open flyout",
            () => VStack(8,
                BodyStrong("All items will be removed."),
                Button.Accent("Yes, delete", () => _msg.Value = "Deleted"))),
        GalleryPage.LiveText(() => _msg.Value));

    [Sample("Flyout placement", Description = "The positioner flips to the opposite side automatically when the flyout does not fit.")]
    static Element Placement() => HStack(8,
        FlyoutButton.Create("Top", () => Body("Placed above the button."), FlyoutPlacement.Top),
        FlyoutButton.Create("Bottom", () => Body("Placed below the button."), FlyoutPlacement.Bottom),
        FlyoutButton.Create("Left", () => Body("Placed to the left."), FlyoutPlacement.Left),
        FlyoutButton.Create("Right", () => Body("Placed to the right."), FlyoutPlacement.Right));
}

// Category overview pages (the expandable group keys land here when selected).
[GalleryPage("navigation-cat", "Navigation", "Overview", Hidden = true)]
sealed class NavigationOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Navigation", "Controls for moving through app content.",
            GalleryPage.CategoryGrid("Navigation", navigate));
    }
}

[GalleryPage("dialogs", "Dialogs & flyouts", "Overview", Hidden = true)]
sealed class DialogsOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Dialogs & flyouts", "Contextual popups and dialogs.",
            GalleryPage.CategoryGrid("Dialogs & flyouts", navigate));
    }
}

[GalleryPage("media", "Media", "Overview", Hidden = true)]
sealed class MediaOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Media", "Media and people controls.",
            GalleryPage.CategoryGrid("Media", navigate));
    }
}
