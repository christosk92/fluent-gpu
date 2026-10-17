using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// P5 (Zune) control-style keys: the overlay, field and status controls a non-Fluent style restyles — MenuFlyout (plate, rows,
/// separator), ContextMenu (header and primary-verb strip presenters), ContentDialog (card, command order and presenter), ComboBox,
/// the TextBox and AutoSuggestBox field surfaces (radius, fill, underline), InfoBar, ToolTip, ProgressRing, ProgressBar, the keys over
/// the existing SelectorBar / SettingsExpander / toast-lane records — and <see cref="TitleBarOptions.Height"/>. Each key is read at
/// RENDER (a mounted headless control, or the factory called under the table, twice over a swap), an EMPTY table is the built-in look
/// (the same records as the explicit Fluent table, the same geometry as before the keys existed), and the tables compare by value.
/// Serial: it writes Tok's process-wide definition and the hosts bind the UI thread.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ControlStyleKeysTests
{
    // Marker colours and widths: unique values that identify the node a style produced.
    private static readonly ColorF PlateMark = ColorF.FromRgba(1, 2, 3);
    private static readonly ColorF SeparatorMark = ColorF.FromRgba(4, 5, 6);
    private static readonly ColorF FieldMark = ColorF.FromRgba(7, 8, 9);
    private static readonly ColorF LineMark = ColorF.FromRgba(10, 11, 12);
    private static readonly ColorF BarMark = ColorF.FromRgba(13, 14, 15);
    private const float HeaderW = 271f, StripW = 263f, CommandW = 97f, WrapW = 333f;

    // ── the table: keys, equality, empty == built-in ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_explicit_Fluent_table_lists_every_new_key_once()
    {
        var table = ControlStyles.FluentExplicit;
        Assert.Equal(30, table.Count);
        Assert.Equal(table.Ids.Count(), table.Ids.Distinct(StringComparer.Ordinal).Count());
        Assert.True(table.Contains(ControlStyles.MenuFlyoutStyle));
        Assert.True(table.Contains(ControlStyles.ContextMenuStyle));
        Assert.True(table.Contains(ControlStyles.ContentDialogStyle));
        Assert.True(table.Contains(ControlStyles.ComboBoxStyle));
        Assert.True(table.Contains(ControlStyles.TextBoxFieldStyle));
        Assert.True(table.Contains(ControlStyles.AutoSuggestBoxFieldStyle));
        Assert.True(table.Contains(ControlStyles.InfoBarStyle));
        Assert.True(table.Contains(ControlStyles.ToolTipStyle));
        Assert.True(table.Contains(ControlStyles.ProgressRingStyle));
        Assert.True(table.Contains(ControlStyles.ProgressBarStyle));
        Assert.True(table.Contains(ControlStyles.SelectorBarStyle));
        Assert.True(table.Contains(ControlStyles.SettingsExpanderStyle));
        Assert.True(table.Contains(ControlStyles.ToastLaneStyle));
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void An_empty_table_resolves_every_new_style_to_its_built_in_default(ThemeKind kind)
    {
        var saved = Tok.Definition; var savedKind = Tok.Theme;
        try
        {
            Tok.Use(ThemeDefinition.FluentDefault, kind);
            string fromEmpty = NewStyles();
            Tok.Use(ThemeDefinition.FluentDefault with { Id = "explicit", Controls = ControlStyles.FluentExplicit }, kind);
            string fromExplicit = NewStyles();
            Assert.Equal(fromEmpty, fromExplicit);
        }
        finally { Tok.Use(saved, savedKind); }

        static string NewStyles() => new StringBuilder()
            .Append(MenuFlyout.DefaultStyle).Append('\n').Append(ContextMenu.DefaultStyle).Append('\n').Append(ContentDialog.DefaultStyle).Append('\n')
            .Append(ComboBox.DefaultStyle).Append('\n').Append(TextBox.DefaultFieldStyle).Append('\n').Append(AutoSuggestBox.DefaultFieldStyle).Append('\n')
            .Append(InfoBar.DefaultStyle).Append('\n').Append(ToolTip.DefaultStyle).Append('\n').Append(ProgressRing.DefaultStyle).Append('\n')
            .Append(ProgressBar.DefaultStyle).Append('\n').Append(SelectorBar.ThemeStyle?.ToString() ?? "stock").Append('\n')
            .Append(SettingsExpander.DefaultStyle).Append('\n')
            .Append(ControlStyles.Resolve(ControlStyles.ToastLaneStyle, static () => ToastLaneStyle.Default)).Append('\n')
            .ToString();
    }

    [Fact]
    public void The_built_in_defaults_are_the_values_the_controls_always_used()
    {
        WithTable(ControlStyleTable.Empty, () =>
        {
            var menu = MenuFlyout.DefaultStyle;
            Assert.Equal(36f, menu.RowHeight);
            Assert.Equal(40f, menu.LargeRowHeight);
            Assert.Equal(Radii.Overlay, menu.Plate.Radius);
            Assert.Equal(Tok.AcrylicFlyout, menu.Plate.Acrylic);
            Assert.Equal(new Edges4(0, 2, 0, 2), menu.Plate.Padding);
            Assert.Equal(Tok.StrokeDividerDefault, menu.SeparatorColor);
            var dialog = ContentDialog.DefaultStyle;
            Assert.Equal((320f, 548f, 184f, 756f), (dialog.MinWidth, dialog.MaxWidth, dialog.MinHeight, dialog.MaxHeight));
            Assert.Equal(new[] { ContentDialogCommand.Primary, ContentDialogCommand.Secondary, ContentDialogCommand.Close }, dialog.CommandOrder);
            Assert.Null(dialog.CommandButton);
            Assert.Null(ContextMenu.DefaultStyle.PrimaryStrip);
            Assert.Null(ContextMenu.DefaultStyle.Header);
            Assert.Equal(0f, TextBox.DefaultFieldStyle.UnderlineThickness);
            Assert.Equal(Radii.Control, TextBox.DefaultFieldStyle.Radius);
            Assert.Null(AutoSuggestBox.DefaultFieldStyle.BorderBrush);
            Assert.Equal(Tok.StrokeControlDefault, AutoSuggestBox.DefaultFieldStyle.BorderColor);
            Assert.Equal(48f, InfoBar.DefaultStyle.MinHeight);
            Assert.Null(InfoBar.DefaultStyle.Background);
            Assert.Equal(320f, ToolTip.DefaultStyle.MaxWidth);
            Assert.Equal(3f, ProgressBar.DefaultStyle.Height);
            Assert.Null(SelectorBar.ThemeStyle);
            Assert.Same(ToastLaneStyle.Default, ControlStyles.Resolve(ControlStyles.ToastLaneStyle, static () => ToastLaneStyle.Default));
        });
    }

    private static MenuFlyout.Style Rows44() => MenuFlyout.FluentStyle() with { RowHeight = 44f };
    private static MenuFlyout.Style Rows48() => MenuFlyout.FluentStyle() with { RowHeight = 48f };

    [Fact]
    public void Tables_over_the_new_keys_compare_by_entries_and_a_change_is_a_non_colour_change()
    {
        var a = ControlStyleTable.Empty.With(ControlStyles.MenuFlyoutStyle, Rows44).With(ControlStyles.InfoBarStyle, InfoBar.FluentStyle);
        var b = ControlStyleTable.Empty.With(ControlStyles.InfoBarStyle, InfoBar.FluentStyle).With(ControlStyles.MenuFlyoutStyle, Rows44);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a.With(ControlStyles.MenuFlyoutStyle, Rows48));
        Assert.NotEqual(a, a.Without(ControlStyles.InfoBarStyle));
        Assert.True(ControlStyleTable.Empty.With(ControlStyles.TextBoxFieldStyle, TextFieldStyle.Fluent)
            .Equals(ControlStyleTable.Empty.With(ControlStyles.TextBoxFieldStyle, TextFieldStyle.Fluent)));
        // The two field keys are distinct entries over the same record type.
        Assert.False(ControlStyleTable.Empty.With(ControlStyles.TextBoxFieldStyle, TextFieldStyle.Fluent).Contains(ControlStyles.AutoSuggestBoxFieldStyle));
        var def = ThemeDefinition.FluentDefault;
        Assert.False(def.DiffersOnlyInColours(def with { Controls = a }));
        Assert.True((def with { Controls = a }).DiffersOnlyInColours(def with { Id = "same-table", Controls = b }));
    }

    // ── MenuFlyout + ContextMenu ─────────────────────────────────────────────────────────────────────────────────────────

    private static ContextMenuModel? PlainMenu() => new ContextMenuModel(
        [new MenuFlyoutItem("First row"), MenuFlyoutItem.Separator, new MenuFlyoutItem("Second row")],
        new ContextMenuHeader(null, "Head title", "Head sub"));

    [Fact]
    public void The_menu_style_restyles_the_plate_rows_and_separator_of_an_open_menu_and_an_empty_table_keeps_WinUI()
    {
        var zune = ControlStyleTable.Empty.With(ControlStyles.MenuFlyoutStyle, static () => MenuFlyout.FluentStyle() with
        {
            Plate = PopupPlate.Fluent() with { Acrylic = null, Fill = PlateMark, Radius = 0f, Shadow = null, BorderWidth = 0f },
            RowHeight = 44f,
            SeparatorColor = SeparatorMark,
        });
        WithTable(zune, () =>
        {
            using var rig = OpenContextMenu(PlainMenu);
            Assert.Equal(44f, rig.Rect(rig.Up(rig.FindText("First row"), AutomationRole.MenuItem)).H);
            var plate = rig.FindFill(PlateMark);
            Assert.False(plate.IsNull);
            Assert.Equal(default(CornerRadius4), rig.Scene.Paint(plate).Corners);
            Assert.False(rig.Scene.TryGetAcrylic(plate, out _));   // a solid plate: no frosted layer, every frame
            Assert.False(rig.FindFill(SeparatorMark).IsNull);
            Assert.False(rig.FindText("Head title").IsNull);       // no header presenter: the built-in identity strip
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = OpenContextMenu(PlainMenu);
            Assert.Equal(36f, rig.Rect(rig.Up(rig.FindText("First row"), AutomationRole.MenuItem)).H);
            Assert.True(rig.FindFill(PlateMark).IsNull);
            Assert.True(rig.FindFill(SeparatorMark).IsNull);
        });
    }

    [Fact]
    public void The_context_menu_header_and_primary_strip_presenters_draw_inside_the_plain_menu_and_the_strip_invokes()
    {
        int played = 0;
        ContextMenuModel? Model() => new ContextMenuModel(
            [new AppBarCommand("", "Play", () => played++)],
            [new MenuFlyoutItem("Go to album"), new MenuFlyoutItem("Go to artist")],
            new ContextMenuHeader(null, "Nightcall", "Kavinsky"));
        var zune = ControlStyleTable.Empty.With(ControlStyles.ContextMenuStyle, static () => ContextMenu.FluentStyle() with
        {
            Header = static (h, _) => new BoxEl { Width = HeaderW, Height = 40f, Children = [new TextEl(h.Title)] },
            HeaderDivider = false,
            PrimaryStrip = static s => new BoxEl
            {
                Width = StripW, Height = 32f, Role = AutomationRole.Button, OnClick = () => s.Invoke(0),
            },
        });
        WithTable(zune, () =>
        {
            using var rig = OpenContextMenu(Model);
            var header = rig.FindWidth(HeaderW);
            var strip = rig.FindWidth(StripW);
            var firstRow = rig.Up(rig.FindText("Go to album"), AutomationRole.MenuItem);
            Assert.False(header.IsNull);
            Assert.False(strip.IsNull);
            Assert.False(firstRow.IsNull);                                      // the rows stay a plain menu
            Assert.True(rig.Rect(header).Y < rig.Rect(strip).Y);               // header, then the strip, then the rows
            Assert.True(rig.Rect(strip).Y < rig.Rect(firstRow).Y);
            rig.Click(Center(rig.Rect(strip)));
            rig.Frames(30);
            Assert.Equal(1, played);
            Assert.True(rig.Find(n => rig.Scene.Interaction(n).Role == AutomationRole.MenuItem).IsNull);   // the menu closed
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = OpenContextMenu(Model);
            Assert.True(rig.FindWidth(StripW).IsNull);   // no presenter: the Win11 command-bar body, as before
            Assert.True(rig.FindWidth(HeaderW).IsNull);
            Assert.False(rig.FindText("Nightcall").IsNull);
        });
    }

    // ── ContentDialog ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element Dialog(Action<ContentDialogResult>? closed = null) => OverlayHost.Create(Embed.Comp(() =>
        new ContentDialog { Title = "Size", Message = "Body", PrimaryText = "Keep", CloseText = "Close", OpenOnMount = true, Closed = closed }));

    [Fact]
    public void The_dialog_style_sets_the_card_and_the_command_order_and_an_empty_table_keeps_WinUI_order()
    {
        var zune = ControlStyleTable.Empty.With(ControlStyles.ContentDialogStyle, static () => ContentDialog.FluentStyle() with
        {
            PlateFill = PlateMark, Radius = 0f, Width = 548f,
            CommandOrder = [ContentDialogCommand.Close, ContentDialogCommand.Primary],
        });
        WithTable(zune, () =>
        {
            using var rig = new Rig(() => Dialog());
            var plate = rig.FindFill(PlateMark);
            Assert.False(plate.IsNull);
            Assert.Equal(default(CornerRadius4), rig.Scene.Paint(plate).Corners);
            Assert.Equal(548f, rig.Rect(plate).W);
            Assert.True(rig.Rect(rig.FindText("Close")).X < rig.Rect(rig.FindText("Keep")).X);
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(() => Dialog());
            Assert.True(rig.FindFill(PlateMark).IsNull);
            Assert.True(rig.Rect(rig.FindText("Keep")).X < rig.Rect(rig.FindText("Close")).X);
        });
    }

    [Fact]
    public void A_dialog_command_presenter_draws_each_command_and_its_invoke_closes_with_the_command_result()
    {
        ContentDialogResult? result = null;
        var zune = ControlStyleTable.Empty.With(ControlStyles.ContentDialogStyle, static () => ContentDialog.FluentStyle() with
        {
            CommandsFill = false,
            CommandButton = static a => new BoxEl
            {
                Width = CommandW, Height = 36f, Role = AutomationRole.Button, OnClick = a.Invoke,
                Fill = a.IsDefault ? BarMark : ColorF.Transparent,
                Children = [new TextEl(a.Text)],
            },
        });
        WithTable(zune, () =>
        {
            using var rig = new Rig(() => Dialog(r => result = r));
            var keep = rig.Up(rig.FindText("Keep"), AutomationRole.Button);
            Assert.Equal(CommandW, rig.Rect(keep).W);
            Assert.Equal(BarMark, rig.Scene.Paint(keep).Fill);          // the primary is the default command
            rig.Click(Center(rig.Rect(keep)));
            rig.Frames(30);
            Assert.Equal(ContentDialogResult.Primary, result);
        });
    }

    // ── the field surfaces ───────────────────────────────────────────────────────────────────────────────────────────────

    private static TextFieldStyle Underlined() => TextFieldStyle.Fluent() with
    {
        Radius = 0f, Fill = FieldMark, HoverFill = FieldMark, BorderBrush = null, BorderWidth = 0f,
        UnderlineThickness = 1f, Underline = LineMark,
    };

    private static Element Column(Element child) => new BoxEl { Direction = 1, Children = [new BoxEl { Direction = 1, Width = WrapW, Children = [child] }] };

    [Fact]
    public void The_TextBox_field_style_sets_the_fill_radius_and_underline_and_an_empty_table_keeps_WinUI()
    {
        WithTable(ControlStyleTable.Empty.With(ControlStyles.TextBoxFieldStyle, Underlined), () =>
        {
            using var rig = new Rig(() => Column(TextBox.Create(new Signal<string>("text"))));
            var field = rig.FindFill(FieldMark);
            var line = rig.FindFill(LineMark);
            Assert.False(field.IsNull);
            Assert.Equal(default(CornerRadius4), rig.Scene.Paint(field).Corners);
            Assert.False(line.IsNull);
            Assert.Equal(1f, rig.Rect(line).H);
            Assert.Equal(rig.Rect(field).Y + rig.Rect(field).H, rig.Rect(line).Y + rig.Rect(line).H, 0.5f);   // on the bottom edge
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(() => Column(TextBox.Create(new Signal<string>("text"))));
            Assert.True(rig.FindFill(FieldMark).IsNull);
            Assert.True(rig.FindFill(LineMark).IsNull);
            var field = rig.Find(n => rig.Scene.Interaction(n).Role == AutomationRole.Text);
            Assert.Equal(32f, rig.Rect(field).H);
            Assert.Equal(CornerRadius4.All(Radii.Control), rig.Scene.Paint(field).Corners);
        });
    }

    [Fact]
    public void The_AutoSuggestBox_field_style_restyles_its_field_and_underlines_it()
    {
        WithTable(ControlStyleTable.Empty.With(ControlStyles.AutoSuggestBoxFieldStyle, Underlined), () =>
        {
            using var rig = new Rig(() => Column(AutoSuggestBox.Create(["alpha", "beta"])));
            var field = rig.FindFill(FieldMark);
            Assert.False(field.IsNull);
            Assert.Equal(AutomationRole.ComboBox, rig.Scene.Interaction(field).Role);
            Assert.False(rig.FindFill(LineMark).IsNull);
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(() => Column(AutoSuggestBox.Create(["alpha", "beta"])));
            Assert.True(rig.FindFill(FieldMark).IsNull);
            Assert.True(rig.FindFill(LineMark).IsNull);
        });
    }

    private static readonly string[] s_items = ["One", "Two", "Three"];

    [Fact]
    public void The_ComboBox_style_restyles_the_closed_field_and_an_empty_table_keeps_the_32_DIP_WinUI_field()
    {
        WithTable(ControlStyleTable.Empty.With(ControlStyles.ComboBoxStyle, static () => ComboBox.FluentStyle() with { Field = Underlined() }), () =>
        {
            using var rig = new Rig(() => Column(ComboBox.Create(s_items, new Signal<int>(0))));
            var field = rig.Up(rig.FindText("One"), AutomationRole.ComboBox);
            Assert.Equal(FieldMark, rig.Scene.Paint(field).Fill);
            Assert.Equal(default(CornerRadius4), rig.Scene.Paint(field).Corners);
            var line = rig.FindFill(LineMark);
            Assert.False(line.IsNull);
            Assert.Equal(rig.Rect(field).Y + rig.Rect(field).H, rig.Rect(line).Y + rig.Rect(line).H, 0.5f);
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(() => Column(ComboBox.Create(s_items, new Signal<int>(0))));
            var field = rig.Up(rig.FindText("One"), AutomationRole.ComboBox);
            Assert.Equal(ComboBox.MinHeight, rig.Rect(field).H);
            Assert.Equal(CornerRadius4.All(Radii.Control), rig.Scene.Paint(field).Corners);
            Assert.True(rig.FindFill(LineMark).IsNull);
        });
    }

    // ── status controls ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_InfoBar_style_sets_the_plate_and_an_empty_table_keeps_the_48_DIP_severity_bar()
    {
        static Element Bar() => Column(InfoBar.Create(InfoBarSeverity.Informational, "Title", "Message"));
        WithTable(ControlStyleTable.Empty.With(ControlStyles.InfoBarStyle, static () => InfoBar.FluentStyle() with
        {
            Background = PlateMark, Radius = 0f, MinHeight = 61f,
        }), () =>
        {
            using var rig = new Rig(Bar);
            var root = rig.Up(rig.FindText("Title"), AutomationRole.InfoBar);
            Assert.Equal(PlateMark, rig.Scene.Paint(root).Fill);
            Assert.Equal(default(CornerRadius4), rig.Scene.Paint(root).Corners);
            Assert.Equal(61f, rig.Rect(root).H);
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(Bar);
            var root = rig.Up(rig.FindText("Title"), AutomationRole.InfoBar);
            Assert.NotEqual(PlateMark, rig.Scene.Paint(root).Fill);
            Assert.Equal(48f, rig.Rect(root).H);
            Assert.Equal(CornerRadius4.All(Radii.Control), rig.Scene.Paint(root).Corners);
        });
    }

    [Fact]
    public void The_ToolTip_style_sets_the_bubble()
    {
        static Element Tip() => OverlayHost.Create(Embed.Comp(() => new ToolTip
        {
            Target = new BoxEl { Width = 40f, Height = 20f }, Text = "tip text", OpenOnMount = true,
        }));
        WithTable(ControlStyleTable.Empty.With(ControlStyles.ToolTipStyle, static () => ToolTip.FluentStyle() with
        {
            Acrylic = null, Fill = PlateMark, Radius = 0f,
        }), () =>
        {
            using var rig = new Rig(Tip);
            rig.Frames(80);   // the show delay (16 ms headless steps)
            var bubble = rig.FindFill(PlateMark);
            Assert.False(bubble.IsNull);
            Assert.Equal(default(CornerRadius4), rig.Scene.Paint(bubble).Corners);
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(Tip);
            rig.Frames(80);
            Assert.True(rig.FindFill(PlateMark).IsNull);
        });
    }

    [Fact]
    public void The_ring_and_bar_styles_are_read_each_time_the_factory_runs_never_captured()
    {
        var zune = ControlStyleTable.Empty
            .With(ControlStyles.ProgressRingStyle, static () => ProgressRing.FluentStyle() with { Foreground = BarMark, StrokeScale = 2f, RoundCaps = false })
            .With(ControlStyles.ProgressBarStyle, static () => ProgressBar.FluentStyle() with { Height = 2f, Foreground = BarMark, IndicatorRadius = 0f });
        ArcSpec RingArc() => ((BoxEl)ProgressRing.Determinate(0.5f).Children[1]).Arc!.Value;
        BoxEl BarFill() => (BoxEl)ProgressBar.Determinate(0.5f).Children[1];

        WithTable(ControlStyleTable.Empty, () =>
        {
            Assert.Equal(Tok.AccentDefault, RingArc().Color);
            Assert.Equal(3f, RingArc().Thickness);
            Assert.True(RingArc().RoundCaps);
            Assert.Equal(3f, BarFill().Height.Value);
            Assert.Equal(CornerRadius4.All(1.5f), BarFill().Corners.Value);
        });
        WithTable(zune, () =>
        {
            Assert.Equal(BarMark, RingArc().Color);
            Assert.Equal(6f, RingArc().Thickness);
            Assert.False(RingArc().RoundCaps);
            Assert.Equal(2f, BarFill().Height.Value);
            Assert.Equal(BarMark, BarFill().Fill.Value);
            Assert.Equal(default(CornerRadius4), BarFill().Corners.Value);
        });
        WithTable(ControlStyleTable.Empty, () => Assert.Equal(3f, RingArc().Thickness));   // swapped back: nothing frozen
    }

    // ── keys over the existing records ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_SelectorBar_key_styles_a_bar_without_a_style_argument_and_an_argument_still_wins()
    {
        WithTable(ControlStyleTable.Empty.With(ControlStyles.SelectorBarStyle, static () => new SelectorBarStyle { ItemHeight = 57f }), () =>
        {
            using (var rig = new Rig(() => Column(SelectorBar.Create(["Alpha", "Beta"]))))
                Assert.True(rig.HasAncestorOfHeight(rig.FindText("Alpha"), 57f));
            using (var rig = new Rig(() => Column(SelectorBar.Create(["Alpha", "Beta"], style: new SelectorBarStyle { ItemHeight = 59f }))))
            {
                Assert.True(rig.HasAncestorOfHeight(rig.FindText("Alpha"), 59f));
                Assert.False(rig.HasAncestorOfHeight(rig.FindText("Alpha"), 57f));
            }
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(() => Column(SelectorBar.Create(["Alpha", "Beta"])));
            Assert.False(rig.HasAncestorOfHeight(rig.FindText("Alpha"), 57f));
        });
    }

    [Fact]
    public void The_SettingsExpander_key_styles_its_item_cards()
    {
        static Element Item() => Column(SettingsExpander.Item("Row header", "Row description"));
        WithTable(ControlStyleTable.Empty.With(ControlStyles.SettingsExpanderStyle, static () => SettingsExpander.FluentStyle() with
        {
            ItemCardStyle = SettingsExpander.FluentStyle().ItemCardStyle with { MinHeight = 91f },
        }), () =>
        {
            using var rig = new Rig(Item);
            Assert.True(rig.HasAncestorOfHeight(rig.FindText("Row header"), 91f));
        });
        WithTable(ControlStyleTable.Empty, () =>
        {
            using var rig = new Rig(Item);
            Assert.False(rig.HasAncestorOfHeight(rig.FindText("Row header"), 91f));
        });
    }

    [Fact]
    public void The_toast_lane_key_sets_the_lane_when_no_Lane_context_is_provided()
    {
        const float W = 640f;
        WithTable(ControlStyleTable.Empty.With(ControlStyles.ToastLaneStyle, static () => new ToastLaneStyle { SideMargin = 8f, FillWidth = true }), () =>
        {
            using var rig = new Rig(() => OverlayHost.Create(new BoxEl { Grow = 1f }), W);
            var handle = Toast.Show("Themed lane", new ToastOptions { DurationMs = 0f });
            rig.Frames(40);
            var card = Card(rig, "Themed lane");
            Assert.Equal(8f, card.X, 0.5f);
            Assert.Equal(W - 16f, card.W, 0.5f);
            handle.Close();
        });
    }

    // ── TitleBar height ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element Bar(Prop<float> height) => TitleBar.Create(new TitleBarOptions { Title = "bar", Height = height });

    private static (RectF Min, RectF Max, RectF Close, RectF Caption) Regions(Rig rig)
    {
        RectF min = default, max = default, close = default, caption = default;
        foreach (var r in rig.Window.LastTitleBarRegions)
        {
            switch (r.Hit)
            {
                case TitleBarHit.MinButton: min = r.RectDip; break;
                case TitleBarHit.MaxButton: max = r.RectDip; break;
                case TitleBarHit.CloseButton: close = r.RectDip; break;
                case TitleBarHit.Caption: caption = r.RectDip; break;
            }
        }
        return (min, max, close, caption);
    }

    [Fact]
    public void A_56_DIP_title_bar_places_its_caption_buttons_and_drag_regions_at_56_and_the_default_stays_48()
    {
        const float W = 900f;
        using (var rig = new Rig(() => Bar(56f), W))
        {
            var (min, max, close, caption) = Regions(rig);
            Assert.Equal(56f, min.H); Assert.Equal(56f, max.H); Assert.Equal(56f, close.H);
            Assert.Equal(56f, caption.H);                                // the whole-bar drag band follows
            Assert.Equal(caption.Y, close.Y, 0.5f);                      // the buttons stretch the full bar, flush to its top…
            Assert.Equal(caption.X + caption.W, close.X + close.W, 0.5f);   // …and its trailing edge
            Assert.True(min.X + min.W <= max.X + 0.5f && max.X + max.W <= close.X + 0.5f);
        }
        using (var rig = new Rig(() => Bar(TitleBar.ExpandedHeight), W))
        {
            var (_, _, close, caption) = Regions(rig);
            Assert.Equal(48f, close.H);
            Assert.Equal(48f, caption.H);
        }
    }

    [Fact]
    public void A_bound_title_bar_height_resizes_live_and_re_reports_the_regions()
    {
        var height = new Signal<float>(48f);
        using var rig = new Rig(() => Bar(height));
        Assert.Equal(48f, Regions(rig).Close.H);
        var before = Regions(rig).Caption;
        var bar = rig.Find(n => rig.Rect(n).Equals(before));   // the bar root (the whole-bar drag band is its rect)
        Assert.False(bar.IsNull);
        height.Value = 56f;
        rig.Frames(4);
        var (min, _, close, caption) = Regions(rig);
        Assert.Equal(56f, close.H);
        Assert.Equal(56f, min.H);
        Assert.Equal(56f, caption.H);
        Assert.True(rig.Scene.IsLive(bar));
        Assert.Equal(56f, rig.Rect(bar).H);   // the same bar node, resized in place (no remount)
    }

    // ── rig ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WithTable(ControlStyleTable table, Action body)
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(table.Count == 0 ? ThemeDefinition.FluentDefault : ThemeDefinition.FluentDefault with { Id = "control-style-keys", Controls = table }, kind);
        try { body(); }
        finally { Tok.Use(saved, kind); }
    }

    private static Point2 Center(RectF r) => new(r.X + r.W * 0.5f, r.Y + r.H * 0.5f);

    /// <summary>The toast card holding <paramref name="message"/>: its widest ancestor narrower than the window.</summary>
    private static RectF Card(Rig rig, string message)
    {
        RectF card = default;
        float window = rig.Rect(rig.Scene.Root).W;
        for (var p = rig.FindText(message); !p.IsNull; p = rig.Scene.Parent(p))
        {
            var r = rig.Rect(p);
            if (r.W < window - 0.5f && r.W > card.W) card = r;
        }
        return card;
    }

    private sealed class Mount : Component
    {
        public required Func<Element> Body;
        public override Element Render() => Body();
    }

    /// <summary>A box carrying a context menu (the attach under test), inside an overlay host.</summary>
    private sealed class MenuTarget : Component
    {
        public required Func<ContextMenuModel?> Factory;
        public override Element Render()
        {
            var svc = UseContext(Overlay.Service);
            return new BoxEl
            {
                Direction = 1,
                Children = [new BoxEl { Width = 120f, Height = 32f, Role = AutomationRole.Button, OnClick = static () => { } }.WithContextMenu(svc, Factory)],
            };
        }
    }

    /// <summary>Mount a menu target, right-click it, and let the menu's open motion settle.</summary>
    private static Rig OpenContextMenu(Func<ContextMenuModel?> factory)
    {
        var rig = new Rig(() => OverlayHost.Create(Embed.Comp(() => new MenuTarget { Factory = factory })), 640f, 480f);
        var target = rig.Find(n => rig.Rect(n) is { W: 120f, H: 32f });
        var p = Center(rig.Rect(target));
        rig.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 1, 0));   // button 1 = right
        rig.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 1, 0));
        rig.Frames(30);
        return rig;
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessWindow Window;
        public readonly AppHost Host;

        public Rig(Func<Element> body, float width = 640f, float height = 480f)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("control-style-keys", new Size2(width, height), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(Strings), Strings, new Mount { Body = body });
            Frames(24);   // mount, open any overlay and settle its motion
        }

        public SceneStore Scene => Host.Scene;

        public void Frames(int n)
        {
            for (int i = 0; i < n; i++) Host.RunFrame();
        }

        public void Click(Point2 p)
        {
            Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
        }

        public RectF Rect(NodeHandle n) => Scene.AbsoluteRect(n);

        public NodeHandle Find(Func<NodeHandle, bool> match) => Find(Scene.Root, match);

        public NodeHandle FindWidth(float width) => Find(n => Scene.Bounds(n).W == width);

        public NodeHandle FindFill(ColorF fill) => Find(n => Scene.Paint(n).Fill.Equals(fill));

        public NodeHandle FindText(string text)
        {
            var id = Strings.Intern(text);
            return Find(n => Scene.Paint(n).VisualKind == VisualKind.Text && Scene.Paint(n).Text == id);
        }

        /// <summary>The nearest ancestor of <paramref name="n"/> (itself included) that carries <paramref name="role"/>.</summary>
        public NodeHandle Up(NodeHandle n, AutomationRole role)
        {
            for (var p = n; !p.IsNull; p = Scene.Parent(p))
                if (Scene.Interaction(p).Role == role) return p;
            return default;
        }

        public bool HasAncestorOfHeight(NodeHandle n, float height)
        {
            for (var p = n; !p.IsNull; p = Scene.Parent(p))
                if (MathF.Abs(Rect(p).H - height) < 0.5f) return true;
            return false;
        }

        private NodeHandle Find(NodeHandle n, Func<NodeHandle, bool> match)
        {
            if (n.IsNull) return n;
            if (match(n)) return n;
            for (var c = Scene.FirstChild(n); !c.IsNull; c = Scene.NextSibling(c))
            {
                var r = Find(c, match);
                if (!r.IsNull) return r;
            }
            return default;
        }

        public void Dispose()
        {
            Host.Dispose();
            App.Dispose();
        }
    }
}
