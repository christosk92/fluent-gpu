using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Navigation / Layout / Media / Dialogs control demo pages (WinUI Gallery parity, batch 2) ──────────

sealed class SplitViewPage : Component
{
    public override Element Render()
    {
        var open = UseSignal(true);
        return GalleryPage.Shell("SplitView",
            "A container with two views: a side pane and the main content area.",
            ControlExample.Build("A SplitView", Frame(SplitView.Create(DemoPane(), DemoContent(), paneWidth: 200f)),
                code: """
                var pane = new BoxEl { Padding = Edges4.All(12), Children = [VStack(8, BodyStrong("Pane"), Body("Navigation").Secondary())] };
                var content = new BoxEl { Padding = Edges4.All(16), Children = [Body("Main content area.")] };

                SplitView.Create(pane, content, paneWidth: 200f)
                """),
            ControlExample.Build("Toggling the pane (isPaneOpen)", Frame(SplitView.Create(DemoPane(), DemoContent(), paneWidth: 200f, isPaneOpen: open)),
                options: ToggleSwitch.Create(open, header: "IsPaneOpen"),
                code: """
                var open = UseSignal(true);

                SplitView.Create(pane, content, paneWidth: 200f, isPaneOpen: open)

                // Paired with:
                ToggleSwitch.Create(open, header: "IsPaneOpen")
                """),
            ControlExample.Build("A custom pane width", Frame(SplitView.Create(DemoPane(), DemoContent(), paneWidth: 120f)),
                code: """
                SplitView.Create(pane, content, paneWidth: 120f)
                """));
    }

    static Element DemoPane() => new BoxEl { Padding = Edges4.All(12), Children = [VStack(8, BodyStrong("Pane"), Body("Navigation").Secondary())] };
    static Element DemoContent() => new BoxEl { Padding = Edges4.All(16), Children = [Body("Main content area.")] };
    static Element Frame(Element splitView) => new BoxEl
    {
        Width = 480, Height = 220, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children = [splitView],
    };
}

sealed class BreadcrumbBarPage : Component
{
    static readonly string[] Crumbs = { "Home", "Documents", "Design", "Specs" };
    public override Element Render()
    {
        var (depth, setDepth) = UseState(Crumbs.Length);
        return GalleryPage.Shell("BreadcrumbBar",
            "Shows the trail of navigation from the home/root location to the current one.",
            ControlExample.Build("A BreadcrumbBar", BreadcrumbBar.Create(Crumbs),
                code: """
                static readonly string[] Crumbs = { "Home", "Documents", "Design", "Specs" };

                BreadcrumbBar.Create(Crumbs)
                """),
            ControlExample.Build("Navigating with onChange", BreadcrumbBar.Create(Crumbs[..depth], i => setDepth(i + 1)),
                description: "Clicking a crumb trims the trail back to it (the WinUI ItemClicked pattern).",
                output: VStack(8,
                    BodyStrong($"Location: {Crumbs[depth - 1]}"),
                    Button.Standard("Reset trail", () => setDepth(Crumbs.Length))),
                code: """
                var (depth, setDepth) = UseState(Crumbs.Length);

                BreadcrumbBar.Create(Crumbs[..depth], i => setDepth(i + 1))
                """));
    }
}

sealed class SelectorBarPage : Component
{
    static readonly string[] Items = { "All", "Photos", "Videos", "Folders" };
    static readonly string[] Views = { "Recent", "Shared", "Favorites" };
    public override Element Render()
    {
        var sel = UseSignal(0);
        var view = UseSignal(0);
        return GalleryPage.Shell("SelectorBar",
            "A horizontal, single-select list with an accent underline on the selected item.",
            ControlExample.Build("A SelectorBar", SelectorBar.Create(Items, sel),
                output: GalleryPage.LiveText(() => Items[sel.Value]),
                code: """
                static readonly string[] Items = { "All", "Photos", "Videos", "Folders" };
                var sel = UseSignal(0);

                SelectorBar.Create(Items, sel)
                """),
            ControlExample.Build("Switching views with a SelectorBar",
                VStack(12,
                    SelectorBar.Create(Views, view),
                    new BoxEl
                    {
                        Width = 360, Height = 100, Padding = Edges4.All(16), Fill = Tok.FillSolidBase,
                        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
                        Children = [Body($"Content of the {Views[view.Value]} view.")],
                    }),
                code: """
                static readonly string[] Views = { "Recent", "Shared", "Favorites" };
                var view = UseSignal(0);

                VStack(12,
                    SelectorBar.Create(Views, view),
                    new BoxEl
                    {
                        Width = 360, Height = 100, Padding = Edges4.All(16), Fill = Tok.FillSolidBase,
                        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
                        Children = [Body($"Content of the {Views[view.Value]} view.")],
                    })
                """));
    }
}

sealed class TabViewPage : Component
{
    static readonly string[] Tabs = { "Document 1", "Document 2", "Document 3" };
    public override Element Render()
    {
        // The "+" appends a new tab, mirroring the WinUI Gallery's TabView_AddButtonClick (TabViewPage.xaml.cs:51-54
        // → CreateNewTab: Header "Document {n}", Symbol.Document). A render-stable counter keeps the numbering
        // monotonic across re-renders (Render() re-runs but the control seeds its list once at mount).
        var nextDoc = UseRef(Tabs.Length + 1);
        Func<TabViewItem?> addDoc = () => new TabViewItem { Header = $"Document {nextDoc.Value++}", Icon = Icons.Document };

        // Template-parts restyle: a tinted strip + larger tab labels (selection mechanics untouched).
        var parts = new TemplateParts { [TabView.PartStrip] = s => s with { Fill = Tok.FillSolidBaseAlt } };
        parts.Set<TextEl>(TabView.PartTabLabel, t => t with { Size = 14f });

        return GalleryPage.Shell("TabView",
            "Displays a set of tabs and their content — for managing multiple documents or pages.",
            ControlExample.Build("A TabView", Frame(TabView.Create(Tabs, onAddTabButtonClick: addDoc)),
                description: "The \"+\" adds a new document tab; tabs reflow smoothly as they are added or closed.",
                code: """
                static readonly string[] Tabs = { "Document 1", "Document 2", "Document 3" };
                var nextDoc = UseRef(Tabs.Length + 1);   // monotonic numbering across re-renders

                TabView.Create(Tabs,
                    onAddTabButtonClick: () => new TabViewItem { Header = $"Document {nextDoc.Value++}", Icon = Icons.Document })
                """),
            ControlExample.Build("Restyling tabs with template parts", Frame(Embed.Comp(() => new TabView { Tabs = Tabs, Parts = parts, OnAddTabButtonClick = addDoc })),
                description: "Template parts restyle the strip and tab labels without re-templating the control.",
                code: """
                var parts = new TemplateParts { [TabView.PartStrip] = s => s with { Fill = Tok.FillSolidBaseAlt } };
                parts.Set<TextEl>(TabView.PartTabLabel, t => t with { Size = 14f });

                Embed.Comp(() => new TabView
                {
                    Tabs = Tabs, Parts = parts,
                    OnAddTabButtonClick = () => new TabViewItem { Header = $"Document {nextDoc.Value++}", Icon = Icons.Document },
                })
                """));
    }

    static Element Frame(Element tabView) => new BoxEl
    {
        Height = 240, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children = [tabView],
    };
}

sealed class PersonPicturePage : Component
{
    public override Element Render() => GalleryPage.Shell("PersonPicture",
        "Displays the avatar image or initials for a person.",
        ControlExample.Build("Initials at different sizes",
            HStack(12, PersonPicture.Create("JD"), PersonPicture.Create("AB", 64f), PersonPicture.Create("CK", 32f)),
            code: """
            HStack(12,
                PersonPicture.Create("JD"),        // default diameter 96
                PersonPicture.Create("AB", 64f),
                PersonPicture.Create("CK", 32f))
            """),
        ControlExample.Build("A profile photo",
            HStack(12,
                PersonPicture.Create("JD", imageSourcePath: Assets.ControlImage("PersonPicture.png")),
                PersonPicture.Create("JD", 64f, imageSourcePath: Assets.ControlImage("PersonPicture.png"))),
            description: "The photo is circle-cropped (UniformToFill) and takes precedence over the initials.",
            code: """
            // Any local image path works; the photo wins over the initials once it loads.
            HStack(12,
                PersonPicture.Create("JD", imageSourcePath: Assets.ControlImage("PersonPicture.png")),
                PersonPicture.Create("JD", 64f, imageSourcePath: Assets.ControlImage("PersonPicture.png")))
            """),
        ControlExample.Build("Badges",
            HStack(12,
                PersonPicture.Create("JD", badgeNumber: 5),
                PersonPicture.Create("JD", badgeNumber: 150),
                PersonPicture.Create("JD", badgeGlyph: Icons.Accept)),
            description: "A number badge caps at 99+; a glyph badge renders only when badgeNumber is 0.",
            code: """
            HStack(12,
                PersonPicture.Create("JD", badgeNumber: 5),
                PersonPicture.Create("JD", badgeNumber: 150),          // >99 renders "99+"
                PersonPicture.Create("JD", badgeGlyph: Icons.Accept))
            """),
        ControlExample.Build("A group avatar",
            PersonPicture.Create("", isGroup: true),
            description: "isGroup shows the People glyph — it outranks initials and the photo.",
            code: """
            PersonPicture.Create("", isGroup: true)
            """),
        ControlExample.Build("Initials from a display name",
            HStack(12,
                Labeled(PersonPicture.Create("", displayName: "John Doe"), "John Doe"),
                Labeled(PersonPicture.Create("", displayName: "Maria de la Cruz (Contoso)"), "Maria de la Cruz (Contoso)"),
                Labeled(PersonPicture.Create(""), "(no name)")),
            description: "Initials derive from the display name (first letter of the first and last word, trailing brackets stripped); with neither, the generic contact glyph shows.",
            code: """
            PersonPicture.Create("", displayName: "John Doe")                     // JD
            PersonPicture.Create("", displayName: "Maria de la Cruz (Contoso)")  // MC — "(Contoso)" is stripped
            PersonPicture.Create("")                                              // no name -> contact glyph
            """));

    static Element Labeled(Element avatar, string caption)
        => new BoxEl { Direction = 1, Gap = 8, AlignItems = FlexAlign.Center, Children = [avatar, Caption(caption)] };
}

sealed class FlyoutPage : Component
{
    public override Element Render()
    {
        var (msg, setMsg) = UseState("—");
        return GalleryPage.Shell("Flyout",
            "A lightweight contextual popup that shows arbitrary content, dismissed by clicking away.",
            ControlExample.Build("A Flyout", FlyoutButton.Create("Open flyout",
                    () => VStack(8, BodyStrong("All items will be removed."), Button.Accent("Yes, delete", () => setMsg("Deleted")))),
                output: BodyStrong(msg),
                code: """
                var (msg, setMsg) = UseState("—");

                FlyoutButton.Create("Open flyout",
                    () => VStack(8,
                        BodyStrong("All items will be removed."),
                        Button.Accent("Yes, delete", () => setMsg("Deleted"))))
                """),
            ControlExample.Build("Flyout placement",
                HStack(8,
                    FlyoutButton.Create("Top", () => Body("Placed above the button."), FlyoutPlacement.Top),
                    FlyoutButton.Create("Bottom", () => Body("Placed below the button."), FlyoutPlacement.Bottom),
                    FlyoutButton.Create("Left", () => Body("Placed to the left."), FlyoutPlacement.Left),
                    FlyoutButton.Create("Right", () => Body("Placed to the right."), FlyoutPlacement.Right)),
                description: "The positioner flips to the opposite side automatically when the flyout does not fit.",
                code: """
                FlyoutButton.Create("Top", () => Body("Placed above the button."), FlyoutPlacement.Top)
                FlyoutButton.Create("Bottom", () => Body("Placed below the button."), FlyoutPlacement.Bottom)
                FlyoutButton.Create("Left", () => Body("Placed to the left."), FlyoutPlacement.Left)
                FlyoutButton.Create("Right", () => Body("Placed to the right."), FlyoutPlacement.Right)
                """));
    }
}

// Category overview pages (the expandable group keys land here when selected).
sealed class NavigationOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Navigation", "Controls for moving through app content.",
            GalleryPage.CategoryGrid("Navigation", navigate));
    }
}

sealed class DialogsOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Dialogs & flyouts", "Contextual popups and dialogs.",
            GalleryPage.CategoryGrid("Dialogs & flyouts", navigate));
    }
}

sealed class MediaOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Media", "Media and people controls.",
            GalleryPage.CategoryGrid("Media", navigate));
    }
}
