using System;
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
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The compact (touch) size knobs of the stock controls: a compact window's hit-target rule (every target at least 40 x 40) is
/// set per instance through each control's own idiom. Each fact mounts the control headless and measures the laid-out part:
/// the default is the pre-knob metric (32 fields, 36 menu rows, WinUI crumbs, the 14 / 48 title-bar spacers, the 24-in toast
/// lane) and the knob sizes the part. Serial: the hosts bind the UI thread and the toast lane registers a process default.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ControlSizeKnobTests
{
    // Unique widths identify the measured wrappers.
    private const float WrapW = 333f, MarkLead = 17f, MarkTrail = 11f, MarkCaption = 13f;

    private sealed class Mount : Component
    {
        public required Func<Element> Body;
        public override Element Render() => Body();
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly AppHost Host;

        public Rig(Func<Element> body, float width = 640f, float height = 480f)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var window = new HeadlessWindow(new WindowDesc("size-knobs", new Size2(width, height), 1f));
            window.Show();
            Host = new AppHost(App, window, new HeadlessGpuDevice(), new HeadlessFontSystem(Strings), Strings, new Mount { Body = body });
            Paint(24);   // mount, open any overlay and settle its motion
        }

        public SceneStore Scene => Host.Scene;

        public void Paint(int frames = 1)
        {
            for (int i = 0; i < frames; i++) Host.Paint(0);
        }

        public NodeHandle Find(Func<NodeHandle, bool> match) => Find(Scene.Root, match);

        public NodeHandle FindWidth(float width) => Find(n => Scene.Bounds(n).W == width);

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

        public RectF Rect(NodeHandle n) => Scene.AbsoluteRect(n);

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

    /// <summary>A column of a known width the control under test sits in (its height is the control's: the outer column, the window's root, is the one that fills).</summary>
    private static Element Column(Element child) => new BoxEl { Direction = 1, Children = [new BoxEl { Direction = 1, Width = WrapW, Children = [child] }] };

    // ── ContentDialog: the command buttons ───────────────────────────────────────────────────────────────────────────────────

    private static Element Dialog(Action<ContentDialog> configure) => OverlayHost.Create(Embed.Comp(() =>
    {
        var d = new ContentDialog { Title = "Size", Message = "Body", PrimaryText = "Keep", CloseText = "Close", OpenOnMount = true };
        configure(d);
        return d;
    }));

    [Fact]
    public void Dialog_commands_default_to_the_32_tall_130_wide_WinUI_buttons()
    {
        using var rig = new Rig(() => Dialog(static _ => { }));
        var keep = rig.Up(rig.FindText("Keep"), AutomationRole.Button);
        Assert.False(keep.IsNull);
        Assert.Equal(32f, rig.Rect(keep).H);
        Assert.True(rig.Rect(keep).W >= 130f);
    }

    [Fact]
    public void Dialog_command_size_lifts_the_buttons_to_40_and_uniform_columns_share_the_row_equally()
    {
        using var rig = new Rig(() => Dialog(static d =>
        {
            d.CommandSize = ControlSize.Large;
            d.CommandMinWidth = 0f;
            d.UniformCommandWidths = true;
            d.CloseText = "A much longer close label";
        }));
        var keep = rig.Up(rig.FindText("Keep"), AutomationRole.Button);
        var close = rig.Up(rig.FindText("A much longer close label"), AutomationRole.Button);
        Assert.Equal(40f, rig.Rect(keep).H);
        Assert.Equal(40f, rig.Rect(close).H);
        Assert.Equal(rig.Rect(keep).W, rig.Rect(close).W, 0.5f);
    }

    // ── ComboBox: the field ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] s_items = ["One", "Two", "Three"];

    [Fact]
    public void ComboBox_field_defaults_to_32_and_Large_lifts_it_to_40()
    {
        using (var rig = new Rig(() => Column(ComboBox.Create(s_items, new Signal<int>(0)))))
            Assert.Equal(ComboBox.MinHeight, rig.Rect(rig.FindWidth(WrapW)).H);
        using (var rig = new Rig(() => Column(ComboBox.Create(s_items, new Signal<int>(0), size: ControlSize.Large))))
        {
            Assert.Equal(40f, rig.Rect(rig.FindWidth(WrapW)).H);
            var field = rig.Up(rig.FindText("One"), AutomationRole.ComboBox);
            Assert.Equal(220f, rig.Rect(field).W);   // the default width holds
        }
    }

    [Fact]
    public void ComboBox_with_a_NaN_width_fills_its_column()
    {
        using var rig = new Rig(() => Column(ComboBox.Create(s_items, new Signal<int>(1), width: float.NaN, size: ControlSize.Large)));
        var field = rig.Up(rig.FindText("Two"), AutomationRole.ComboBox);
        Assert.Equal(WrapW, rig.Rect(field).W);
        Assert.Equal(40f, rig.Rect(field).H);
    }

    // ── NumberBox: the field ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NumberBox_field_defaults_to_32_and_the_options_style_sizes_it()
    {
        using (var rig = new Rig(() => Column(NumberBox.Create(new Signal<double>(3)))))
            Assert.Equal(32f, rig.Rect(rig.FindWidth(WrapW)).H);
        using (var rig = new Rig(() => Column(NumberBox.Create(new Signal<double>(3), null,
                   new NumberBox.NumberBoxOptions { Style = NumberBox.DefaultStyle with { FieldHeight = 40f } }))))
            Assert.Equal(40f, rig.Rect(rig.FindWidth(WrapW)).H);
    }

    // ── MenuFlyout: the rows ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element Menu(ControlSize size) => OverlayHost.Create(Column(
        MenuFlyout.Create([new MenuFlyoutItem("First row"), new MenuFlyoutItem("Second row")], static () => { }, size: size)));

    [Fact]
    public void MenuFlyout_rows_default_to_36_and_Large_rows_are_40()
    {
        using (var rig = new Rig(() => Menu(ControlSize.Medium)))
            Assert.Equal(36f, rig.Rect(rig.Up(rig.FindText("First row"), AutomationRole.MenuItem)).H);
        using (var rig = new Rig(() => Menu(ControlSize.Large)))
            Assert.Equal(40f, rig.Rect(rig.Up(rig.FindText("First row"), AutomationRole.MenuItem)).H);
        Assert.Equal(36f, MenuFlyout.RowHeightFor(ControlSize.Small));   // no compact row: Small is Medium
    }

    // ── BreadcrumbBar: the crumbs ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Breadcrumb_crumbs_keep_the_WinUI_size_by_default_and_Large_crumbs_are_at_least_40_by_40()
    {
        using (var rig = new Rig(() => Column(BreadcrumbBar.Create(["A", "B"]))))
            Assert.True(rig.Rect(rig.Up(rig.FindText("A"), AutomationRole.Button)).H < 40f);
        using (var rig = new Rig(() => Column(BreadcrumbBar.Create(["A", "B"], size: ControlSize.Large))))
        {
            var crumb = rig.Rect(rig.Up(rig.FindText("A"), AutomationRole.Button));
            Assert.Equal(40f, crumb.H);
            Assert.True(crumb.W >= 40f);
        }
    }

    // ── TitleBar: the lead pad and the drag strip ────────────────────────────────────────────────────────────────────────────

    private static Element Bar(Func<TitleBarSpacing>? spacing) => TitleBar.Create(new TitleBarOptions
    {
        Title = "bar",
        Tabs = static () => new BoxEl { Width = MarkLead, Height = 10f },
        Trailing = static () => new BoxEl { Width = MarkTrail, Height = 10f },
        CaptionLeading = static () => new BoxEl { Width = MarkCaption, Height = 10f },
        RowSpacing = spacing,
    });

    private static (float Lead, float Strip) Spacers(Rig rig)
    {
        var lead = rig.Rect(rig.FindWidth(MarkLead));
        var trail = rig.Rect(rig.FindWidth(MarkTrail));
        var caption = rig.Rect(rig.FindWidth(MarkCaption));
        return (lead.X, caption.X - (trail.X + trail.W));
    }

    [Fact]
    public void TitleBar_spacing_defaults_to_14_and_48_and_the_thunk_narrows_both_live()
    {
        float defaultLead;
        using (var rig = new Rig(() => Bar(null)))
        {
            var (lead, strip) = Spacers(rig);
            Assert.Equal(TitleBarSpacing.Default.MinDragStrip, strip, 0.5f);
            defaultLead = lead;
        }
        var spacing = new Signal<TitleBarSpacing>(TitleBarSpacing.Default);
        using (var rig = new Rig(() => Bar(() => spacing.Value)))
        {
            Assert.Equal(defaultLead, Spacers(rig).Lead, 0.5f);
            spacing.Value = new TitleBarSpacing(LeadPad: 0f, MinDragStrip: 16f);
            rig.Paint(3);
            var (lead, strip) = Spacers(rig);
            Assert.Equal(defaultLead - TitleBarSpacing.Default.LeadPad, lead, 0.5f);
            Assert.Equal(16f, strip, 0.5f);
        }
    }

    // ── Toast: the lane and the action ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toast_lane_defaults_to_24_in_and_300_to_380_cards_and_the_lane_context_fills_and_lifts_the_action()
    {
        const float W = 640f;
        using (var rig = new Rig(() => OverlayHost.Create(new BoxEl { Grow = 1f }), W))
        {
            var handle = Toast.Show("Default lane", new ToastOptions { ActionLabel = "Undo", DurationMs = 0f });
            rig.Paint(40);   // the card's enter motion (16 ms headless steps)
            var action = rig.Rect(rig.Up(rig.FindText("Undo"), AutomationRole.Button));
            Assert.Equal(32f, action.H);
            var card = Card(rig, "Default lane");
            Assert.InRange(card.W, 300f, 380f);
            Assert.Equal(W - 24f, card.X + card.W, 0.5f);
            handle.Close();
        }
        var lane = new ToastLaneStyle { SideMargin = 8f, FillWidth = true, ActionSize = ControlSize.Large };
        using (var rig = new Rig(() => Ctx.Provide(Toast.Lane, () => lane, OverlayHost.Create(new BoxEl { Grow = 1f })), W))
        {
            var handle = Toast.Show("Filled lane", new ToastOptions { ActionLabel = "Undo", DurationMs = 0f });
            rig.Paint(40);   // the card's enter motion (16 ms headless steps)
            var action = rig.Rect(rig.Up(rig.FindText("Undo"), AutomationRole.Button));
            Assert.Equal(40f, action.H);
            var card = Card(rig, "Filled lane");
            Assert.Equal(8f, card.X, 0.5f);
            Assert.Equal(W - 16f, card.W, 0.5f);
            handle.Close();
        }
    }

    /// <summary>The toast card holding <paramref name="message"/>: its widest ancestor that is narrower than the window (the lane is the window's width).</summary>
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
}
