using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

// ── TeachingTip / Popup / ItemsView / TextBlock / Border / AppBarSeparator demo pages (batch 5) ──────────

[GalleryPage("TeachingTip", "TeachingTip", "Dialogs & flyouts", Icon = Icons.Star)]
sealed class TeachingTipPage : Component
{
    public override Element Render()
    {
        var (closed, setClosed) = UseState("—");
        return GalleryPage.Shell("TeachingTip",
            "A non-modal, contextual callout that highlights a feature or teaches the user something.",
            ExampleCard.Build("A TeachingTip",
                TeachingTip.Create("Show teaching tip", "Save your work", "Click the disk icon, or press Ctrl+S, to save your changes."),
                description: "With no CloseButtonContent the close moves to the 40×40 header (alternate) close button; the tip is not light-dismiss — only the close button or Escape dismisses it.",
                code: """
                TeachingTip.Create("Show teaching tip", "Save your work",
                    "Click the disk icon, or press Ctrl+S, to save your changes.")
                """),
            ExampleCard.Build("A TeachingTip with action and close buttons",
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with buttons",
                    Title = "Try filters",
                    Subtitle = "Narrow results quickly",
                    Body = "Use filters to reduce the list before opening a detail view.",
                    IconGlyph = Icons.Search,
                    ActionButtonContent = "Open filters",
                    ActionButtonIsAccent = true,
                    CloseButtonContent = "Got it",
                    Closed = r => setClosed(r.ToString()),
                }),
                description: "Setting CloseButtonContent moves the close into the footer next to the action button (the WinUI ButtonsStates split).",
                output: BodyStrong($"Closed: {closed}"),
                code: """
                var (closed, setClosed) = UseState("—");

                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with buttons",
                    Title = "Try filters",
                    Subtitle = "Narrow results quickly",
                    Body = "Use filters to reduce the list before opening a detail view.",
                    IconGlyph = Icons.Search,
                    ActionButtonContent = "Open filters",
                    ActionButtonIsAccent = true,
                    CloseButtonContent = "Got it",
                    Closed = r => setClosed(r.ToString()),
                })
                """),
            ExampleCard.Build("Hero content and preferred placement",
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with hero content",
                    Title = "Saving automatically",
                    Subtitle = "Your changes sync as you work.",
                    PreferredPlacement = TeachingTip.PlacementMode.Bottom,
                    IsLightDismissEnabled = true,
                    HeroContent = new BoxEl
                    {
                        Grow = 1f, Height = 100f, Fill = Tok.AccentDefault,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.Picture, 36f).Foreground(Tok.TextOnAccentPrimary)],
                    },
                }),
                description: "HeroContent pins a full-bleed banner to the tip's edge; PreferredPlacement picks one of the 13 WinUI placements (the tail re-targets if the positioner flips). IsLightDismissEnabled lets a click outside dismiss it.",
                code: """
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with hero content",
                    Title = "Saving automatically",
                    Subtitle = "Your changes sync as you work.",
                    PreferredPlacement = TeachingTip.PlacementMode.Bottom,
                    IsLightDismissEnabled = true,
                    HeroContent = new BoxEl
                    {
                        Grow = 1f, Height = 100f, Fill = Tok.AccentDefault,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.Picture, 36f).Foreground(Tok.TextOnAccentPrimary)],
                    },
                })
                """));
    }
}

[GalleryPage("Popup", "Popup", "Dialogs & flyouts", Icon = Icons.More)]
sealed class PopupPage : Component
{
    public override Element Render() => GalleryPage.Shell("Popup",
        "Displays content on top of existing content, shown and hidden programmatically.",
        ExampleCard.Build("A Popup",
            Embed.Comp(() => new PopupDemo
            {
                TriggerLabel = "Show popup",
                Text = "This content is displayed in a popup above the page.",
            }),
            description: "Controlled by a Signal<bool>: the trigger toggles it, and a light-dismiss (click outside) writes the signal back. Setting isOpen from anywhere opens/closes it.",
            code: """
            var isOpen = UseSignal(false);
            return Popup.Create(
                Button.Standard("Show popup", () => isOpen.Value = !isOpen.Value),
                () => new BoxEl { Padding = Edges4.All(16), Children = [Ui.Text("…")] },
                isOpen);
            """),
        ExampleCard.Build("A windowed Popup",
            Embed.Comp(() => new PopupDemo
            {
                TriggerLabel = "Show windowed popup",
                Text = "This popup rides its own top-level window and may escape the app window.",
                Constrain = false,
            }),
            description: "ConstrainToRootBounds = false renders the popup in its own top-level window (the WinUI windowed-popup path); when the platform can't create popup windows it silently falls back to constrained placement.",
            code: """
            Popup.Create(anchor, content, isOpen,
                options: new PopupOptions { ConstrainToRootBounds = false });
            """));
}

/// <summary>Gallery demo of the controlled <see cref="Popup"/> primitive: a trigger button toggling a
/// <c>Signal&lt;bool&gt;</c> that drives an anchored, light-dismissable popup.</summary>
sealed class PopupDemo : Component
{
    public string TriggerLabel = "Show popup";
    public string Text = "Popup content";
    public bool Constrain = true;
    public bool OpenOnMount;   // deterministic visual-shot hook

    public override Element Render()
    {
        var open = UseSignal(OpenOnMount);
        return Popup.Create(
            Button.Standard(TriggerLabel, () => open.Value = !open.Value),
            () => new BoxEl
            {
                Direction = 1,
                Padding = Edges4.All(16),
                MinWidth = 180f,
                Children = [new TextEl(Text) { Size = 14f, Color = Tok.TextPrimary }],
            },
            open,
            options: new PopupOptions { ConstrainToRootBounds = Constrain });
    }
}

[GalleryPage("Toast", "Toast", "Dialogs & flyouts", Icon = Icons.More)]
sealed class ToastPage : Component
{
    public override Element Render() => GalleryPage.Shell("Toast",
        "Transient in-app status messages, stacked at a screen corner and auto-dismissing.",
        ExampleCard.Build("Severities",
            HStack(8f,
                Button.Standard("Info",    () => Toast.Show("Your changes are being synced.", new ToastOptions { Severity = InfoBarSeverity.Informational, Title = "Syncing" })),
                Button.Standard("Success", () => Toast.Show("Playlist saved.", new ToastOptions { Severity = InfoBarSeverity.Success, Title = "Saved" })),
                Button.Standard("Warning", () => Toast.Show("You are offline — changes are queued.", new ToastOptions { Severity = InfoBarSeverity.Warning, Title = "Offline" })),
                Button.Standard("Error",   () => Toast.Show("Failed to reach the server.", new ToastOptions { Severity = InfoBarSeverity.Error, Title = "Error" }))),
            description: "Toast.Show returns a ToastHandle {IsOpen, Close()}. Severity reuses InfoBarSeverity (shared visuals). Toasts auto-dismiss after 5s; hovering the strip pauses the countdown. Up to Toast.MaxVisible (3) show at once; the rest wait in a FIFO queue.",
            code: """
            Toast.Show("Playlist saved.", new ToastOptions {
                Severity = InfoBarSeverity.Success, Title = "Saved",
            });
            """),
        ExampleCard.Build("With an action + sticky",
            HStack(8f,
                Button.Standard("With action", () => Toast.Show("A newer version is available.",
                    new ToastOptions { Severity = InfoBarSeverity.Informational, Title = "Update", ActionLabel = "Reload", OnAction = () => { } })),
                Button.Standard("Sticky", () => Toast.Show("This stays until dismissed.",
                    new ToastOptions { Severity = InfoBarSeverity.Warning, Title = "Sticky", DurationMs = 0f })),
                Button.Standard("Dismiss all", Toast.CloseAll)),
            description: "DurationMs = 0 makes a toast sticky (dismissed only by its close button or Toast.CloseAll()). An ActionLabel/OnAction adds an inline action button.",
            code: """
            Toast.Show("A newer version is available.", new ToastOptions {
                Title = "Update", ActionLabel = "Reload", OnAction = Reload,
            });
            Toast.Show("This stays until dismissed.", new ToastOptions { DurationMs = 0f });
            """),
        // NAMING NOTE (WS3 P6): FluentGpu.Controls.Toast (this page) is the IN-APP toast — a card in the app window.
        // It is distinct from FluentGpu.WindowsApi's OS notification Toast (Action Center), which lives in a different
        // namespace and surfaces at the OS level.
        ExampleCard.Build("In-app toast vs OS notification",
            new BoxEl
            {
                Direction = 1,
                Gap = 4f,
                Children =
                [
                    new TextEl("This Toast (FluentGpu.Controls) shows a card inside the app window.") { Size = 13f, Color = Tok.TextSecondary },
                    new TextEl("For an OS-level notification (Action Center), use FluentGpu.WindowsApi.Notifications.Toast — a different type in a different namespace.") { Size = 13f, Color = Tok.TextSecondary },
                ],
            },
            description: "Two unrelated APIs share the name Toast: the in-app card here, and the OS notification builder in the Windows pillar."));
}

[GalleryPage("ItemsView", "ItemsView", "Collections", Icon = Icons.Grid)]
sealed class ItemsViewPage : Component
{
    static readonly string[] Items = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
    static readonly float[] WallAspects = { 1.0f, 1.5f, 0.75f, 1.8f, 1.2f, 0.9f };

    // The 5 selector-visual names, in SelectorVisual enum order (AccentPill=0, Check=1, FullRow=2, Border=3, None=4) —
    // the interactive picker switches the live ItemsView's selector by casting the chosen index back to the enum.
    static readonly string[] Visuals = { "AccentPill", "Check", "FullRow", "Border", "None" };

    // List-preset demo data (re-homed from the deleted ListViewPage — same coffee/drink data so nothing is lost).
    static readonly string[] Coffees = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };
    // Grid-preset demo data (re-homed from the deleted GridViewPage — same "Item N" labels).
    static readonly string[] GridItems = { "Item 1", "Item 2", "Item 3", "Item 4", "Item 5", "Item 6", "Item 7", "Item 8" };

    // PartDelta demo: ~6 rows tinted by a per-item data property (the newly-legal per-item-customization seam — the
    // tint is baked as a VALUE into the chrome during construction, zero per-item allocation, shape-stable).
    static readonly (string Name, ColorF Tint)[] Swatches =
    {
        ("Crimson", ColorF.FromRgba(0xE8, 0x3C, 0x3C, 0x33)),
        ("Amber",   ColorF.FromRgba(0xF5, 0xC5, 0x18, 0x33)),
        ("Emerald", ColorF.FromRgba(0x18, 0xA0, 0x57, 0x33)),
        ("Azure",   ColorF.FromRgba(0x2D, 0x7D, 0xF6, 0x33)),
        ("Violet",  ColorF.FromRgba(0x8B, 0x3C, 0xC9, 0x33)),
        ("Teal",    ColorF.FromRgba(0x18, 0xA0, 0xA0, 0x33)),
    };

    public override Element Render()
    {
        var multi = UseMemo(static () => new SelectionModel(), DepKey.Empty);
        var wall = UseMemo(static () => new LinedFlowLayout(
            lineHeight: 72f, aspectRatio: i => WallAspects[i % WallAspects.Length], lineSpacing: 8f, minItemSpacing: 8f), DepKey.Empty);
        var (invoked, setInvoked) = UseState("—");

        // Card 1 (picker): a reactive index over the 5 visuals; the live ItemsView re-renders when it changes. A
        // SelectionModel pre-selecting index 0 (created once, in the memo factory) keeps a selected row visible so the
        // chosen selector visual always reads.
        var sel = UseSignal(0);
        var pickSel = UseMemo(static () => { var m = new SelectionModel(); m.Select(0); return m; }, DepKey.Empty);

        // Card-group 2 (List preset) state — re-homed verbatim from the deleted ListViewPage.
        var listSel = UseSignal(0);   // a default selection so the accent pill is visible (WinUI gallery parity)
        var listMulti = UseMemo(static () => new SelectionModel(), DepKey.Empty);
        var drinks = UseMemo(static () => new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" }, DepKey.Empty);
        var listOrder = UseSignal(0);
        _ = listOrder.Value;   // re-render after a drag-reorder commit (refreshes the template closures)

        // Card-group 3 (Grid preset) state — re-homed verbatim from the deleted GridViewPage.
        var gridMulti = UseMemo(static () => new SelectionModel(), DepKey.Empty);
        var colors = UseMemo(static () => new List<string> { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Pink", "Teal" }, DepKey.Empty);
        var gridOrder = UseSignal(0);
        _ = gridOrder.Value;

        return GalleryPage.Shell("ItemsView",
            "The premiere collection control — every layout × selection mode × selector visual, with built-in drag-reorder.",
            // ── the 4 original ItemsView cards (UNCHANGED) ───────────────────────────────────────────────────────
            ExampleCard.Build("An ItemsView", ItemsView.Create(Items, columns: 4),
                description: "Single selection (the default): click a tile or arrow-key between them; type to jump by prefix.",
                code: """
                static readonly string[] Items = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };

                ItemsView.Create(Items, columns: 4)
                """),
            ExampleCard.Build("Multiple selection",
                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Multiple,
                        Selection = multi,
                        ItemText = i => Items[i],
                        Grow = 0f,
                    }),
                description: "SelectionMode.Multiple slides in the ItemContainer checkbox; the SelectionModel stores the selected ranges (Ctrl+A selects all).",
                output: GalleryPage.LiveText(() => { _ = multi.Version.Value; return $"{multi.SelectedCount} selected"; }),
                code: """
                var multi = UseMemo(static () => new SelectionModel(), DepKey.Empty);

                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Multiple,
                        Selection = multi,
                        ItemText = i => Items[i],
                        Grow = 0f,
                    })
                """),
            ExampleCard.Build("Item invocation",
                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.None,
                        IsItemInvokedEnabled = true,
                        OnInvoked = i => setInvoked(Items[i]),
                        ItemText = i => Items[i],
                        Grow = 0f,
                    }),
                description: "With SelectionMode.None and IsItemInvokedEnabled, a tap raises ItemInvoked instead of selecting (the WinUI CanRaiseItemInvoked matrix).",
                output: BodyStrong($"Invoked: {invoked}"),
                code: """
                var (invoked, setInvoked) = UseState("—");

                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.None,
                        IsItemInvokedEnabled = true,
                        OnInvoked = i => setInvoked(Items[i]),
                        ItemText = i => Items[i],
                        Grow = 0f,
                    })
                """),
            ExampleCard.Build("The photo wall (LinedFlowLayout)",
                ItemsView.Create(24, WallTile, RepeatLayout.Custom(wall),
                    new ListOptions
                    {
                        ItemText = i => "Photo " + (i + 1),
                        Grow = 0f,
                    }),
                description: "The WinUI LinedFlowLayout: items flow into uniform-height lines, each item's width = aspect ratio × line height — the gallery photo wall. The layout is stateful, so it is hoisted with UseMemo.",
                code: """
                static readonly float[] WallAspects = { 1.0f, 1.5f, 0.75f, 1.8f, 1.2f, 0.9f };
                var wall = UseMemo(static () => new LinedFlowLayout(
                    lineHeight: 72f, aspectRatio: i => WallAspects[i % WallAspects.Length], lineSpacing: 8f, minItemSpacing: 8f), DepKey.Empty);

                ItemsView.Create(24, WallTile, RepeatLayout.Custom(wall),
                    new ListOptions
                    {
                        ItemText = i => "Photo " + (i + 1),
                        Grow = 0f,
                    })
                """),

            // ── 1) the INTERACTIVE selector-visual picker ────────────────────────────────────────────────────────
            ExampleCard.Build("Pick a selector visual",
                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Stack(44f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Single,
                        Selection = pickSel,
                        Selector = (SelectorVisual)sel.Value,
                        ItemText = i => Items[i],
                        // Constructor args freeze at mount (the reconciler never re-renders a mounted component on a
                        // parent re-render — pitfalls.md "child ignores new data"). A sel-derived Key remounts the view
                        // with the new Selector; pickSel is hoisted above, so the selection survives the remount.
                        Grow = 0f,
                    }) with { Key = "selvis-" + Visuals[sel.Value] },
                description: "Any selector visual works with any layout × any selection mode — no WinUI capability cliffs. AccentPill is the ListView accent bar; Check is the GridView corner check; FullRow is a full-bleed superset; Border is the default ItemContainer ring; None is app-drawn.",
                options: RadioButton.Group(Visuals, sel),
                code: """
                var sel = UseSignal(0);
                static readonly string[] Visuals = { "AccentPill", "Check", "FullRow", "Border", "None" };

                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Stack(44f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Single,
                        Selector = (SelectorVisual)sel.Value,
                        Grow = 0f,
                    }) with { Key = "selvis-" + Visuals[sel.Value] }   // key change ⇒ remount with the new selector
                // …wired to RadioButton.Group(Visuals, sel)
                """),

            // ── 2) List preset (AccentPill) — absorbs the 3 deleted ListViewPage examples ─────────────────────────
            BodyStrong("List preset (AccentPill)"),
            ExampleCard.Build("A simple list",
                ListCard(ItemsView.List(Coffees, listSel)),
                description: "ItemsView.List(items, selection) is the former ListView: a vertical accent-pill list. A default selection keeps the accent bar visible.",
                output: GalleryPage.LiveText(() => listSel.Value >= 0 ? Coffees[listSel.Value] : "—"),
                code: """
                static readonly string[] Coffees = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };
                var selected = UseSignal(0);

                ItemsView.List(Coffees, selected)
                """),
            ExampleCard.Build("Multiple selection",
                ListCard(ItemsView.List(Coffees.Length,
                    i => new TextEl(Coffees[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: listMulti,
                    itemText: i => Coffees[i])),
                description: "SelectionMode.Multiple slides in the inline checkboxes; the SelectionModel stores the selected ranges (Ctrl+A selects all).",
                output: GalleryPage.LiveText(() => { _ = listMulti.Version.Value; return $"{listMulti.SelectedCount} selected"; }),
                code: """
                var multi = UseMemo(static () => new SelectionModel(), DepKey.Empty);

                ItemsView.List(Coffees.Length,
                    i => new TextEl(Coffees[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Coffees[i])
                """),
            ExampleCard.Build("Drag to reorder",
                ListCard(ItemsView.List(drinks.Count,
                    i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(drinks, from, to); listOrder.Value = listOrder.Peek() + 1; },
                    itemText: i => drinks[i],
                    keyOf: i => drinks[i])),
                description: "CanReorderItems: drag a row — displaced rows part after the 200ms WinUI live-reorder dwell, then the commit moves the item.",
                output: GalleryPage.LiveText(() => { _ = listOrder.Value; return string.Join(" · ", drinks); }),
                code: """
                var drinks = UseMemo(static () => new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" }, DepKey.Empty);
                var order = UseSignal(0);

                ItemsView.List(drinks.Count,
                    i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(drinks, from, to); order.Value = order.Peek() + 1; },
                    itemText: i => drinks[i],
                    keyOf: i => drinks[i])
                """),

            // ── 3) Grid preset (Check) — absorbs the 3 deleted GridViewPage examples ──────────────────────────────
            BodyStrong("Grid preset (Check)"),
            ExampleCard.Build("A simple grid", ItemsView.Grid(GridItems, columns: 4),
                description: "ItemsView.Grid(items, columns) is the former GridView: a tile grid with the top-right corner-check selector.",
                code: """
                static readonly string[] GridItems = { "Item 1", "Item 2", "Item 3", "Item 4", "Item 5", "Item 6", "Item 7", "Item 8" };

                ItemsView.Grid(GridItems, columns: 4)
                """),
            ExampleCard.Build("Multiple selection",
                ItemsView.Grid(GridItems.Length, i => Tile(GridItems[i]), columns: 4, tileHeight: 96f,
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: gridMulti,
                    itemText: i => GridItems[i]),
                description: "SelectionMode.Multiple shows the top-right overlay check square; selected tiles get the 2px accent border with the inner ring.",
                output: GalleryPage.LiveText(() => { _ = gridMulti.Version.Value; return $"{gridMulti.SelectedCount} selected"; }),
                code: """
                var multi = UseMemo(static () => new SelectionModel(), DepKey.Empty);

                ItemsView.Grid(GridItems.Length, i => Tile(GridItems[i]), columns: 4, tileHeight: 96f,
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => GridItems[i])
                """),
            ExampleCard.Build("Drag to reorder",
                ItemsView.Grid(colors.Count, i => Tile(colors[i]), columns: 4, tileHeight: 96f,
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(colors, from, to); gridOrder.Value = gridOrder.Peek() + 1; },
                    keyOf: i => colors[i]),
                description: "2-D live reorder: drag a tile — displaced tiles part after the 300ms WinUI grid dwell, then the commit moves the item.",
                output: GalleryPage.LiveText(() => { _ = gridOrder.Value; return string.Join(" · ", colors); }),
                code: """
                var colors = UseMemo(static () => new List<string> { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Pink", "Teal" }, DepKey.Empty);
                var order = UseSignal(0);

                ItemsView.Grid(colors.Count, i => Tile(colors[i]), columns: 4, tileHeight: 96f,
                    canReorderItems: true,
                    onReorder: (from, to) => { ReorderList.Move(colors, from, to); order.Value = order.Peek() + 1; },
                    keyOf: i => colors[i])
                """),

            // ── 4) Per-item customization (PartDelta) — the newly-legal per-item-variation seam ────────────────────
            ExampleCard.Build("Per-item customization (PartDelta)",
                ItemsView.Create(Swatches.Length, i => Tile(Swatches[i].Name), RepeatLayout.Stack(44f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Single,
                        Selector = SelectorVisual.FullRow,
                        PartDelta = (i, st) => new PartDelta(Fill: Swatches[i].Tint, Corners: CornerRadius4.All(8f)),
                        ItemText = i => Swatches[i].Name,
                        Grow = 0f,
                    }),
                description: "PartDelta bakes per-item VALUES (fill/foreground/opacity/corner/padding/glyph) into the chrome during construction — zero extra allocation, provably shape-stable. The legal way to vary item chrome per item (per-item TemplateParts in a recycled scroll path is the banned hazard).",
                code: """
                static readonly (string Name, ColorF Tint)[] Swatches = { … };

                ItemsView.Create(Swatches.Length, i => Tile(Swatches[i].Name), RepeatLayout.Stack(44f),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Single,
                        Selector = SelectorVisual.FullRow,
                        PartDelta = (i, st) => new PartDelta(Fill: Swatches[i].Tint, Corners: CornerRadius4.All(8f)),
                        Grow = 0f,
                    })
                """));
    }

    static Element Tile(string label) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary }],
    };

    static Element WallTile(int i) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = Tok.FillCardSecondary, Corners = Radii.ControlAll,
        Children = [new TextEl("Photo " + (i + 1)) { Size = 12f, Color = Tok.TextSecondary }],
    };

    // The bordered 280-wide host the List preset sits in (the former ListViewPage card — Width=280, no height so the
    // list sizes to its natural rows, 4px top/bottom inset).
    static Element ListCard(Element list) => new BoxEl { Width = 280, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Padding = new Edges4(0, 4, 0, 4), Children = [list] };
}

[GalleryPage("TextBlock", "TextBlock", "Text", Icon = Icons.Font)]
sealed class TextBlockPage : Component
{
    const string LongText = "Text wrapping flows this sentence onto as many lines as its 320 epx column needs, breaking between words exactly like a WinUI TextBlock.";

    public override Element Render() => GalleryPage.Shell("TextBlock",
        "Displays read-only, formatted text — the foundation of the WinUI type ramp.",
        ExampleCard.Build("A simple TextBlock", TextBlocks.Body("I am a TextBlock."),
            code: """
            TextBlocks.Body("I am a TextBlock.")
            """),
        ExampleCard.Build("The type ramp",
            VStack(8,
                TextBlocks.Title("Title"),
                TextBlocks.Subtitle("Subtitle"),
                TextBlocks.BodyStrong("Body strong"),
                TextBlocks.Body("Body — the default paragraph text used across the app."),
                TextBlocks.Caption("Caption — secondary metadata and timestamps.")),
            code: """
            VStack(8,
                TextBlocks.Title("Title"),
                TextBlocks.Subtitle("Subtitle"),
                TextBlocks.BodyStrong("Body strong"),
                TextBlocks.Body("Body — the default paragraph text used across the app."),
                TextBlocks.Caption("Caption — secondary metadata and timestamps."))
            """),
        ExampleCard.Build("A customized TextBlock",
            VStack(8,
                new TextEl("I am a styled TextBlock.") { Size = 18f, Bold = true, Color = Tok.AccentDefault },
                new TextEl("Underlined for emphasis.") { Size = 14f, Underline = true, Color = Tok.TextPrimary },
                new TextEl("No longer relevant.") { Size = 14f, Strikethrough = true, Color = Tok.TextSecondary },
                new TextEl("A monospace code run.") { Size = 14f, FontFamily = "Cascadia Code", Color = Tok.TextPrimary }),
            code: """
            VStack(8,
                new TextEl("I am a styled TextBlock.") { Size = 18f, Bold = true, Color = Tok.AccentDefault },
                new TextEl("Underlined for emphasis.") { Size = 14f, Underline = true, Color = Tok.TextPrimary },
                new TextEl("No longer relevant.") { Size = 14f, Strikethrough = true, Color = Tok.TextSecondary },
                new TextEl("A monospace code run.") { Size = 14f, FontFamily = "Cascadia Code", Color = Tok.TextPrimary })
            """),
        ExampleCard.Build("Text wrapping and trimming",
            VStack(12,
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap }] },
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }] }),
            description: "The first block wraps freely; MaxLines = 2 clamps the second to two lines.",
            code: """
            const string LongText = "Text wrapping flows this sentence onto as many lines as its 320 epx column needs, breaking between words exactly like a WinUI TextBlock.";

            VStack(12,
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap }] },
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }] })
            """));
}

[GalleryPage("Border", "Border", "Layout", Icon = Icons.Grid)]
sealed class BorderPage : Component
{
    public override Element Render()
    {
        var t = UseFloatSignal(0.4f);
        float thickness = 1f + MathF.Round(t.Value * 5f);

        return GalleryPage.Shell("Border",
            "Draws a border, background, and rounded corners around a single child element.",
            ExampleCard.Build("A Border",
                Border.Create(new TextEl("Content inside a Border") { Size = 14f, Color = Tok.TextPrimary }, cornerRadius: 8f, padding: 20f),
                code: """
                Border.Create(new TextEl("Content inside a Border") { Size = 14f, Color = Tok.TextPrimary },
                    cornerRadius: 8f, padding: 20f)
                """),
            ExampleCard.Build("Border thickness and color",
                Border.Create(new TextEl("Content inside an accent Border") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: thickness, cornerRadius: 8f, borderColor: Tok.AccentDefault, padding: 20f),
                options: Slider.Create(t, length: 200f, options: new Slider.SliderOptions { Header = "BorderThickness" }),
                output: BodyStrong($"{thickness:0} epx"),
                code: """
                var (t, setT) = UseState(0.4f);
                float thickness = 1f + MathF.Round(t * 5f);

                Border.Create(new TextEl("Content inside an accent Border") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: thickness, cornerRadius: 8f, borderColor: Tok.AccentDefault, padding: 20f)
                """),
            ExampleCard.Build("A background with no border",
                Border.Create(new TextEl("Background only") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: 0f, cornerRadius: 8f, background: Tok.FillSubtleSecondary, padding: 20f),
                code: """
                Border.Create(new TextEl("Background only") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: 0f, cornerRadius: 8f, background: Tok.FillSubtleSecondary, padding: 20f)
                """));
    }
}

[GalleryPage("AppBarSeparator", "AppBarSeparator", "Menus & toolbars", Icon = Icons.More)]
sealed class AppBarSeparatorPage : Component
{
    public override Element Render() => GalleryPage.Shell("AppBarSeparator",
        "A thin vertical divider that separates groups of commands in a CommandBar.",
        ExampleCard.Build("Commands with a separator",
            new BoxEl
            {
                Direction = 0, Gap = 4, AlignItems = FlexAlign.Center, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    AppBarButton.Create(Icons.Accept, "Add", () => { }),
                    AppBarButton.Create(Icons.Tag, "Edit", () => { }),
                    AppBarSeparator.Create(),
                    AppBarButton.Create(Icons.Cancel, "Delete", () => { }),
                ],
            },
            description: "The default (FullSize/Compact) orientation: a 1px vertical line stretching the bar height with the 2,8,2,8 inset.",
            code: """
            HStack(4,
                AppBarButton.Create(Icons.Accept, "Add", () => { }),
                AppBarButton.Create(Icons.Tag, "Edit", () => { }),
                AppBarSeparator.Create(),
                AppBarButton.Create(Icons.Cancel, "Delete", () => { }))
            """),
        ExampleCard.Build("The Overflow state",
            new BoxEl
            {
                Direction = 1, Width = 220f, Padding = Edges4.All(4), Corners = Radii.OverlayAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    AppBarButton.CreateOverflow(new AppBarCommand(Icons.Copy, "Copy") { AcceleratorText = "Ctrl+C" }, hasToggles: false, hasIcons: true, onInvoke: () => { }),
                    AppBarButton.CreateOverflow(new AppBarCommand(Icons.Tag, "Rename"), hasToggles: false, hasIcons: true, onInvoke: () => { }),
                    AppBarSeparator.Create(overflow: true),
                    AppBarButton.CreateOverflow(new AppBarCommand(Icons.Cancel, "Delete"), hasToggles: false, hasIcons: true, onInvoke: () => { }),
                ],
            },
            description: "In the CommandBar overflow menu the separator flips horizontal (the Overflow visual state): full-width, 1px tall, margin 0,4,0,4.",
            code: """
            VStack(0,
                AppBarButton.CreateOverflow(new AppBarCommand(Icons.Copy, "Copy") { AcceleratorText = "Ctrl+C" }, hasToggles: false, hasIcons: true, onInvoke: () => { }),
                AppBarButton.CreateOverflow(new AppBarCommand(Icons.Tag, "Rename"), hasToggles: false, hasIcons: true, onInvoke: () => { }),
                AppBarSeparator.Create(overflow: true),
                AppBarButton.CreateOverflow(new AppBarCommand(Icons.Cancel, "Delete"), hasToggles: false, hasIcons: true, onInvoke: () => { }))
            """));
}
