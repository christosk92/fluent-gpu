using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── TeachingTip / Popup / ItemsView / TextBlock / Border / AppBarSeparator demo pages (batch 5) ──────────

[GalleryPage("TeachingTip", "TeachingTip", "Dialogs & flyouts", Icon = Icons.Star)]
sealed partial class TeachingTipPage : Component
{
    static readonly Signal<string> _closed = new("—");

    public override Element Render() => GalleryPage.Shell("TeachingTip",
        "A non-modal, contextual callout that highlights a feature or teaches the user something.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(ActionCloseSample),
        ExampleCard.Show(HeroContentSample));

    [Sample("A TeachingTip", Description = "With no CloseButtonContent the close moves to the 40×40 header (alternate) close button; the tip is not light-dismiss — only the close button or Escape dismisses it.")]
    static Element Basic() => TeachingTip.Create("Show teaching tip", "Save your work",
        "Click the disk icon, or press Ctrl+S, to save your changes.");

    [Sample("A TeachingTip with action and close buttons", Description = "Setting CloseButtonContent moves the close into the footer next to the action button (the WinUI ButtonsStates split).")]
    static Element ActionClose() => VStack(8,
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
            Closed = r => _closed.Value = r.ToString(),
        }),
        GalleryPage.LiveText(() => $"Closed: {_closed.Value}"));

    [Sample("Hero content and preferred placement", Description = "HeroContent pins a full-bleed banner to the tip's edge; PreferredPlacement picks one of the 13 WinUI placements (the tail re-targets if the positioner flips). IsLightDismissEnabled lets a click outside dismiss it.")]
    static Element HeroContent() => Embed.Comp(() => new TeachingTip
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
    });
}

[GalleryPage("Popup", "Popup", "Dialogs & flyouts", Icon = Icons.More,
    WaveeUse = "Add-to-playlist picker", WaveePath = "src/apps/Wavee/Features/Detail/PlaylistPicker.cs")]
sealed partial class PopupPage : Component
{
    static readonly Signal<bool> _open = new(false);
    static readonly Signal<bool> _windowed = new(false);

    public override Element Render() => GalleryPage.Shell("Popup",
        "Displays content on top of existing content, shown and hidden programmatically.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(WindowedSample));

    [Sample("A Popup", Description = "Controlled by a Signal<bool>: the trigger toggles it, and a light-dismiss (click outside) writes the signal back. Setting isOpen from anywhere opens/closes it.")]
    static Element Basic() => Popup.Create(
        Button.Standard("Show popup", () => _open.Value = !_open.Value),
        () => new BoxEl { Direction = 1, Padding = Edges4.All(16), MinWidth = 180f, Children = [new TextEl("This content is displayed in a popup above the page.") { Size = 14f, Color = Tok.TextPrimary }] },
        _open);

    [Sample("A windowed Popup", Description = "ConstrainToRootBounds = false renders the popup in its own top-level window (the WinUI windowed-popup path); when the platform can't create popup windows it silently falls back to constrained placement.")]
    static Element Windowed() => Popup.Create(
        Button.Standard("Show windowed popup", () => _windowed.Value = !_windowed.Value),
        () => new BoxEl { Direction = 1, Padding = Edges4.All(16), MinWidth = 180f, Children = [new TextEl("This popup rides its own top-level window and may escape the app window.") { Size = 14f, Color = Tok.TextPrimary }] },
        _windowed,
        options: new PopupOptions { ConstrainToRootBounds = false });
}

/// <summary>Gallery demo of the controlled <see cref="Popup"/> primitive: a trigger button toggling a
/// <c>Signal&lt;bool&gt;</c> that drives an anchored, light-dismissable popup. Kept for the deterministic visual-shot
/// scene (<c>OpenOnMount</c>), referenced by ShotScene.</summary>
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

[GalleryPage("Toast", "Toast", "Dialogs & flyouts", Icon = Icons.More,
    WaveeUse = "Playback & save-to-library toasts", WaveePath = "src/apps/Wavee/App/NotificationCenterBridge.cs")]
sealed partial class ToastPage : Component
{
    public override Element Render() => GalleryPage.Shell("Toast",
        "Transient in-app status messages, stacked at a screen corner and auto-dismissing.",
        ExampleCard.Show(SeveritiesSample),
        ExampleCard.Show(ActionStickySample),
        ExampleCard.Show(VsOsSample));

    [Sample("Severities", Description = "Toast.Show returns a ToastHandle {IsOpen, Close()}. Severity reuses InfoBarSeverity (shared visuals). Toasts auto-dismiss after 5s; hovering the strip pauses the countdown. Up to Toast.MaxVisible (3) show at once; the rest wait in a FIFO queue.")]
    static Element Severities() => HStack(8f,
        Button.Standard("Info",    () => Toast.Show("Your changes are being synced.", new ToastOptions { Severity = InfoBarSeverity.Informational, Title = "Syncing" })),
        Button.Standard("Success", () => Toast.Show("Playlist saved.", new ToastOptions { Severity = InfoBarSeverity.Success, Title = "Saved" })),
        Button.Standard("Warning", () => Toast.Show("You are offline — changes are queued.", new ToastOptions { Severity = InfoBarSeverity.Warning, Title = "Offline" })),
        Button.Standard("Error",   () => Toast.Show("Failed to reach the server.", new ToastOptions { Severity = InfoBarSeverity.Error, Title = "Error" })));

    [Sample("With an action + sticky", Description = "DurationMs = 0 makes a toast sticky (dismissed only by its close button or Toast.CloseAll()). An ActionLabel/OnAction adds an inline action button.")]
    static Element ActionSticky() => HStack(8f,
        Button.Standard("With action", () => Toast.Show("A newer version is available.",
            new ToastOptions { Severity = InfoBarSeverity.Informational, Title = "Update", ActionLabel = "Reload", OnAction = () => { } })),
        Button.Standard("Sticky", () => Toast.Show("This stays until dismissed.",
            new ToastOptions { Severity = InfoBarSeverity.Warning, Title = "Sticky", DurationMs = 0f })),
        Button.Standard("Dismiss all", Toast.CloseAll));

    // NAMING NOTE (WS3 P6): FluentGpu.Controls.Toast (this page) is the IN-APP toast — a card in the app window.
    // It is distinct from FluentGpu.WindowsApi's OS notification Toast (Action Center), which lives in a different
    // namespace and surfaces at the OS level.
    [Sample("In-app toast vs OS notification", Description = "Two unrelated APIs share the name Toast: the in-app card here, and the OS notification builder in the Windows pillar.")]
    static Element VsOs() => new BoxEl
    {
        Direction = 1,
        Gap = 4f,
        Children =
        [
            new TextEl("This Toast (FluentGpu.Controls) shows a card inside the app window.") { Size = 13f, Color = Tok.TextSecondary },
            new TextEl("For an OS-level notification (Action Center), use FluentGpu.WindowsApi.Notifications.Toast — a different type in a different namespace.") { Size = 13f, Color = Tok.TextSecondary },
        ],
    };
}

[GalleryPage("ItemsView", "ItemsView", "Collections", Icon = Icons.Grid, Level = GalleryLevel.RealWorld,
    WaveeUse = "Virtualized album & playlist track lists", WaveePath = "src/apps/Wavee/Features/Detail/DetailTracks.cs")]
sealed partial class ItemsViewPage : Component
{
    static readonly string[] Items = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
    static readonly float[] WallAspects = { 1.0f, 1.5f, 0.75f, 1.8f, 1.2f, 0.9f };

    // The 5 selector-visual names, in SelectorVisual enum order (AccentPill=0, Check=1, FullRow=2, Border=3, None=4).
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

    // Selection / layout state (page-static so it survives sample re-mounts — the [Sample] factories are static and
    // cannot call Use* hooks). These were UseMemo(DepKey.Empty) singletons in the pre-samples page.
    static readonly SelectionModel _multi = new();
    static readonly LinedFlowLayout _wall = new(
        lineHeight: 72f, aspectRatio: i => WallAspects[i % WallAspects.Length], lineSpacing: 8f, minItemSpacing: 8f);
    static readonly Signal<string> _invoked = new("—");

    // Card 1 (picker): a SelectionModel pre-selecting index 0 keeps a selected row visible so the chosen selector
    // visual always reads. The selector index is a live Knob.
    static readonly SelectionModel _pickSel = MakePickSel();
    static SelectionModel MakePickSel() { var m = new SelectionModel(); m.Select(0); return m; }

    // List preset state.
    static readonly Signal<int> _listSel = new(0);
    static readonly SelectionModel _listMulti = new();
    static readonly List<string> _drinks = new() { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
    static readonly Signal<int> _listOrder = new(0);

    // Grid preset state.
    static readonly SelectionModel _gridMulti = new();
    static readonly List<string> _colors = new() { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Pink", "Teal" };
    static readonly Signal<int> _gridOrder = new(0);

    public override Element Render() => GalleryPage.Shell("ItemsView",
        "The premiere collection control — every layout × selection mode × selector visual, with built-in drag-reorder.",
        // ── the 4 original ItemsView cards ───────────────────────────────────────────────────────────────────────
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(MultipleSelectionSample),
        ExampleCard.Show(ItemInvocationSample),
        ExampleCard.Show(PhotoWallSample),
        ExampleCard.Show(SelectorPickerSample),
        // ── List preset (AccentPill) — absorbs the deleted ListViewPage examples ───────────────────────────────────
        BodyStrong("List preset (AccentPill)"),
        ExampleCard.Show(ListSimpleSample),
        ExampleCard.Show(ListMultipleSample),
        ExampleCard.Show(ListReorderSample),
        // ── Grid preset (Check) — absorbs the deleted GridViewPage examples ────────────────────────────────────────
        BodyStrong("Grid preset (Check)"),
        ExampleCard.Show(GridSimpleSample),
        ExampleCard.Show(GridMultipleSample),
        ExampleCard.Show(GridReorderSample),
        // ── Per-item customization (PartDelta) — the newly-legal per-item-variation seam ───────────────────────────
        ExampleCard.Show(PerItemSample));

    [Sample("An ItemsView", Description = "Single selection (the default): click a tile or arrow-key between them; type to jump by prefix.")]
    static Element Simple() => ItemsView.Create(Items, columns: 4);

    [Sample("Multiple selection", Description = "SelectionMode.Multiple slides in the ItemContainer checkbox; the SelectionModel stores the selected ranges (Ctrl+A selects all).")]
    static Element MultipleSelection() => VStack(8,
        ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.Multiple,
                Selection = _multi,
                ItemText = i => Items[i],
                Grow = 0f,
            }),
        GalleryPage.LiveText(() => { _ = _multi.Version.Value; return $"{_multi.SelectedCount} selected"; }));

    [Sample("Item invocation", Description = "With SelectionMode.None and IsItemInvokedEnabled, a tap raises ItemInvoked instead of selecting (the WinUI CanRaiseItemInvoked matrix).")]
    static Element ItemInvocation() => VStack(8,
        ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                IsItemInvokedEnabled = true,
                OnInvoked = i => _invoked.Value = Items[i],
                ItemText = i => Items[i],
                Grow = 0f,
            }),
        GalleryPage.LiveText(() => $"Invoked: {_invoked.Value}"));

    [Sample("The photo wall (LinedFlowLayout)", Description = "The WinUI LinedFlowLayout: items flow into uniform-height lines, each item's width = aspect ratio × line height — the gallery photo wall. The layout is stateful, so it is hoisted to a static field.")]
    static Element PhotoWall() => ItemsView.Create(24, WallTile, RepeatLayout.Custom(_wall),
        new ListOptions
        {
            ItemText = i => "Photo " + (i + 1),
            Grow = 0f,
        });

    [Sample("Pick a selector visual", Description = "Any selector visual works with any layout × any selection mode — no WinUI capability cliffs. AccentPill is the ListView accent bar; Check is the GridView corner check; FullRow is a full-bleed superset; Border is the default ItemContainer ring; None is app-drawn.")]
    static Element SelectorPicker(Knobs k)
    {
        var sel = k.Choice("Selector", Visuals, 0);
        // A sel-derived Key remounts the view with the new Selector; _pickSel is static, so selection survives the remount.
        return ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Stack(44f),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.Single,
                Selection = _pickSel,
                Selector = (SelectorVisual)sel.Value,
                ItemText = i => Items[i],
                Grow = 0f,
            }) with { Key = "selvis-" + Visuals[sel.Value] };
    }

    [Sample("A simple list", Description = "ItemsView.List(items, selection) is the former ListView: a vertical accent-pill list. A default selection keeps the accent bar visible.")]
    static Element ListSimple() => VStack(8,
        ListCard(ItemsView.List(Coffees, _listSel)),
        GalleryPage.LiveText(() => _listSel.Value >= 0 ? Coffees[_listSel.Value] : "—"));

    [Sample("Multiple selection (list)", Description = "SelectionMode.Multiple slides in the inline checkboxes; the SelectionModel stores the selected ranges (Ctrl+A selects all).")]
    static Element ListMultiple() => VStack(8,
        ListCard(ItemsView.List(Coffees.Length,
            i => new TextEl(Coffees[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
            selectionMode: ItemsSelectionMode.Multiple,
            selection: _listMulti,
            itemText: i => Coffees[i])),
        GalleryPage.LiveText(() => { _ = _listMulti.Version.Value; return $"{_listMulti.SelectedCount} selected"; }));

    [Sample("Drag to reorder (list)", Description = "CanReorderItems: drag a row — displaced rows part after the 200ms WinUI live-reorder dwell, then the commit moves the item.")]
    static Element ListReorder()
    {
        _ = _listOrder.Value;   // re-render after a drag-reorder commit (refreshes the template closures)
        return VStack(8,
            ListCard(ItemsView.List(_drinks.Count,
                i => new TextEl(_drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                canReorderItems: true,
                onReorder: (from, to) => { ReorderList.Move(_drinks, from, to); _listOrder.Value = _listOrder.Peek() + 1; },
                itemText: i => _drinks[i],
                keyOf: i => _drinks[i])),
            GalleryPage.LiveText(() => { _ = _listOrder.Value; return string.Join(" · ", _drinks); }));
    }

    [Sample("A simple grid", Description = "ItemsView.Grid(items, columns) is the former GridView: a tile grid with the top-right corner-check selector.")]
    static Element GridSimple() => ItemsView.Grid(GridItems, columns: 4);

    [Sample("Multiple selection (grid)", Description = "SelectionMode.Multiple shows the top-right overlay check square; selected tiles get the 2px accent border with the inner ring.")]
    static Element GridMultiple() => VStack(8,
        ItemsView.Grid(GridItems.Length, i => Tile(GridItems[i]), columns: 4, tileHeight: 96f,
            selectionMode: ItemsSelectionMode.Multiple,
            selection: _gridMulti,
            itemText: i => GridItems[i]),
        GalleryPage.LiveText(() => { _ = _gridMulti.Version.Value; return $"{_gridMulti.SelectedCount} selected"; }));

    [Sample("Drag to reorder (grid)", Description = "2-D live reorder: drag a tile — displaced tiles part after the 300ms WinUI grid dwell, then the commit moves the item.")]
    static Element GridReorder()
    {
        _ = _gridOrder.Value;
        return VStack(8,
            ItemsView.Grid(_colors.Count, i => Tile(_colors[i]), columns: 4, tileHeight: 96f,
                canReorderItems: true,
                onReorder: (from, to) => { ReorderList.Move(_colors, from, to); _gridOrder.Value = _gridOrder.Peek() + 1; },
                keyOf: i => _colors[i]),
            GalleryPage.LiveText(() => { _ = _gridOrder.Value; return string.Join(" · ", _colors); }));
    }

    [Sample("Per-item customization (PartDelta)", Description = "PartDelta bakes per-item VALUES (fill/foreground/opacity/corner/padding/glyph) into the chrome during construction — zero extra allocation, provably shape-stable. The legal way to vary item chrome per item (per-item TemplateParts in a recycled scroll path is the banned hazard).")]
    static Element PerItem() => ItemsView.Create(Swatches.Length, i => Tile(Swatches[i].Name), RepeatLayout.Stack(44f),
        new ListOptions
        {
            SelectionMode = ItemsSelectionMode.Single,
            Selector = SelectorVisual.FullRow,
            PartDelta = (i, st) => new PartDelta(Fill: Swatches[i].Tint, Corners: CornerRadius4.All(8f)),
            ItemText = i => Swatches[i].Name,
            Grow = 0f,
        });

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
sealed partial class TextBlockPage : Component
{
    const string LongText = "Text wrapping flows this sentence onto as many lines as its 320 epx column needs, breaking between words exactly like a WinUI TextBlock.";

    public override Element Render() => GalleryPage.Shell("TextBlock",
        "Displays read-only, formatted text — the foundation of the WinUI type ramp.",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(TypeRampSample),
        ExampleCard.Show(CustomizedSample),
        ExampleCard.Show(WrappingSample));

    [Sample("A simple TextBlock")]
    static Element Simple() => TextBlocks.Body("I am a TextBlock.");

    [Sample("The type ramp")]
    static Element TypeRamp() => VStack(8,
        TextBlocks.Title("Title"),
        TextBlocks.Subtitle("Subtitle"),
        TextBlocks.BodyStrong("Body strong"),
        TextBlocks.Body("Body — the default paragraph text used across the app."),
        TextBlocks.Caption("Caption — secondary metadata and timestamps."));

    [Sample("A customized TextBlock")]
    static Element Customized() => VStack(8,
        new TextEl("I am a styled TextBlock.") { Size = 18f, Bold = true, Color = Tok.AccentDefault },
        new TextEl("Underlined for emphasis.") { Size = 14f, Underline = true, Color = Tok.TextPrimary },
        new TextEl("No longer relevant.") { Size = 14f, Strikethrough = true, Color = Tok.TextSecondary },
        new TextEl("A monospace code run.") { Size = 14f, FontFamily = "Cascadia Code", Color = Tok.TextPrimary });

    [Sample("Text wrapping and trimming", Description = "The first block wraps freely; MaxLines = 2 clamps the second to two lines.")]
    static Element Wrapping() => VStack(12,
        new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap }] },
        new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }] });
}

[GalleryPage("Border", "Border", "Layout", Icon = Icons.Grid)]
sealed partial class BorderPage : Component
{
    public override Element Render() => GalleryPage.Shell("Border",
        "Draws a border, background, and rounded corners around a single child element.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(ThicknessColorSample),
        ExampleCard.Show(BackgroundOnlySample));

    [Sample("A Border")]
    static Element Basic() => Border.Create(new TextEl("Content inside a Border") { Size = 14f, Color = Tok.TextPrimary },
        cornerRadius: 8f, padding: 20f);

    [Sample("Border thickness and color")]
    static Element ThicknessColor(Knobs k)
    {
        var t = k.Slider("BorderThickness", 0.4f);
        float thickness = 1f + MathF.Round(t.Value * 5f);
        return VStack(8,
            Border.Create(new TextEl("Content inside an accent Border") { Size = 14f, Color = Tok.TextPrimary },
                borderWidth: thickness, cornerRadius: 8f, borderColor: Tok.AccentDefault, padding: 20f),
            GalleryPage.LiveText(() => $"{1f + MathF.Round(t.Value * 5f):0} epx"));
    }

    [Sample("A background with no border")]
    static Element BackgroundOnly() => Border.Create(new TextEl("Background only") { Size = 14f, Color = Tok.TextPrimary },
        borderWidth: 0f, cornerRadius: 8f, background: Tok.FillSubtleSecondary, padding: 20f);
}

[GalleryPage("AppBarSeparator", "AppBarSeparator", "Menus & toolbars", Icon = Icons.More)]
sealed partial class AppBarSeparatorPage : Component
{
    public override Element Render() => GalleryPage.Shell("AppBarSeparator",
        "A thin vertical divider that separates groups of commands in a CommandBar.",
        ExampleCard.Show(WithSeparatorSample),
        ExampleCard.Show(OverflowSample));

    [Sample("Commands with a separator", Description = "The default (FullSize/Compact) orientation: a 1px vertical line stretching the bar height with the 2,8,2,8 inset.")]
    static Element WithSeparator() => new BoxEl
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
    };

    [Sample("The Overflow state", Description = "In the CommandBar overflow menu the separator flips horizontal (the Overflow visual state): full-width, 1px tall, margin 0,4,0,4.")]
    static Element Overflow() => new BoxEl
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
    };
}
