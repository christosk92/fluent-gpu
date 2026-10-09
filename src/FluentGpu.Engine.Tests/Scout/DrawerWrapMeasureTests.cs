using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A column (and a ZStack) measured each child at its own content width, while Arrange gives the child that width LESS
/// the child's margin. A wrap row inside a margined child therefore fit on one line in measure and wrapped in arrange: the
/// parent reported its height a line short and the next sibling painted over the extra line. Seen in Wavee's track
/// drawer: the drawer's clip box carries an 8 DIP margin each side, the facts prose fit the 16 DIP wider measure width,
/// wrapped in the real one, and the measured virtual list stacked the next track row 20 DIP into the drawer.
/// Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DrawerWrapMeasureTests
{
    private const int Count = 6;
    private const float RowH = 48f;
    private const float SlotW = 564f;

    /// <summary>172 + 176 + 120 + 2 × 4 gap = 476 DIP: one line at the slot width, two lines (16 + 4 + 16) at 451.</summary>
    private static Element WrapRow() => new BoxEl
    {
        Direction = 0, Wrap = true, Gap = 4f, MinWidth = 0f,
        Children =
        [
            new BoxEl { Width = 172f, Height = 16f }, new BoxEl { Width = 176f, Height = 16f }, new BoxEl { Width = 120f, Height = 16f },
        ],
    };

    /// <summary>The wrap row behind 113 DIP of horizontal margin (97 + 16): 451 DIP of line.</summary>
    private static Element Margined() => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Margin = new Edges4(97f, 0f, 16f, 0f), Children = [WrapRow()],
    };

    private sealed class List(bool zstack) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = SlotW,
            Children = [new VirtualListEl
            {
                ItemCount = Count, ItemLayout = new MeasuredStackVirtualLayout(RowH), Grow = 1f,
                RenderItem = i => i != 0 ? new BoxEl { Height = RowH } : new BoxEl
                {
                    Direction = 1, ZStack = zstack, Children = [Margined()],
                },
            }],
        };
    }

    private sealed class Plain(bool zstack) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = SlotW, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Key = "slot", Direction = 1, Width = SlotW, ZStack = zstack, Children = [Margined()] },
                new BoxEl { Key = "next", Width = 10f, Height = 10f },
            ],
        };
    }

    private static AppHost Host(HeadlessPlatformApp app, StringTable strings, Component root)
    {
        var window = new HeadlessWindow(new WindowDesc("drawer-wrap", new Size2(800, 900), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 6; i++) host.RunFrame();
        return host;
    }

    private static NodeHandle Find(SceneStore s, Func<NodeHandle, bool> match)
    {
        NodeHandle found = default;
        void Visit(NodeHandle n)
        {
            if (n.IsNull || !found.IsNull) return;
            if (match(n)) { found = n; return; }
            for (var ch = s.FirstChild(n); !ch.IsNull; ch = s.NextSibling(ch)) Visit(ch);
        }
        Visit(s.Root);
        Assert.False(found.IsNull);
        return found;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMarginedWrapRow_IsMeasuredAtTheWidthItIsArrangedAt(bool zstack)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, new Plain(zstack));
        var s = host.Scene;
        var slot = Find(s, n => s.DebugKeyOf(n) == "slot");

        Assert.Equal(36f, s.Bounds(slot).H);                                   // two lines (was 16: measured at 564)
        Assert.Equal(s.Bounds(slot).Y + 36f, s.Bounds(s.NextSibling(slot)).Y); // the sibling stacks below both
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMeasuredListSlot_HoldsItsWrappedContent(bool zstack)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, new List(zstack));
        var s = host.Scene;
        var vp = Find(s, n => s.TryGetScroll(n, out var sc) && sc.ItemCount == Count);
        var slot = s.FirstChild(s.FirstChild(vp));

        Assert.Equal(36f, s.Bounds(slot).H);                                   // the slot extent is its wrapped content
        Assert.Equal(s.Bounds(slot).Y + 36f, s.Bounds(s.NextSibling(slot)).Y); // row 2 starts under it, not 20 DIP inside
    }
}
