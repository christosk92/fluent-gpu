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
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>EP16: the TabStrip tier forms resolve against a lane, and the TitleBar's row count flips live (2 rows = a 40 tab
/// row with the captions over the 48 toolbar; 1 row = the toolbar with the captions at its end).</summary>
[Collection(SerialTestCollection.Name)]
public sealed class TitleBarRowsTests
{
    const float Chrome = 54f;   // TabStrip.ChromeFixedWidth(add: true): 8 + 6 + 5 + 35

    [Fact]
    public void Ladder_keeps_its_width_while_it_fits_then_shares_the_lane_then_scrolls_at_its_floor()
    {
        var form = TabStripForm.Ladder(160f, 110f);
        var roomy = form.Resolve(4, 1000f, Chrome);
        Assert.Equal(160f, roomy.TabWidth);
        Assert.False(roomy.Scrolls);

        var shared = form.Resolve(8, 1000f, Chrome);
        Assert.Equal((1000f - Chrome) / 8f, shared.TabWidth, 0.01f);
        Assert.False(shared.Scrolls);

        var full = form.Resolve(10, 1000f, Chrome);
        Assert.Equal(110f, full.TabWidth);
        Assert.True(full.Scrolls);
        Assert.False(full.IconOnlyUnselected);
    }

    [Fact]
    public void Ladder_without_a_lane_keeps_the_preferred_width_and_never_scrolls()
    {
        var r = TabStripForm.Ladder(200f).Resolve(30, float.PositiveInfinity, Chrome);
        Assert.Equal(200f, r.TabWidth);
        Assert.False(r.Scrolls);
        Assert.Equal(200f, TabStripForm.Ladder(200f).MinTabWidth);   // no floor given = no narrowing
    }

    [Fact]
    public void Compact_labels_the_selected_tab_and_makes_the_rest_icon_cells()
    {
        var form = TabStripForm.Compact();
        var r = form.Resolve(4, 400f, Chrome);
        Assert.Equal(140f, r.SelectedWidth);
        Assert.Equal(36f, r.TabWidth);
        Assert.True(r.IconOnlyUnselected);
        Assert.False(r.Scrolls);
        Assert.True(form.Resolve(12, 300f, Chrome).Scrolls);
        Assert.False(TabStripForm.Compact(scrollOnOverflow: false).Resolve(12, 300f, Chrome).Scrolls);
    }

    private sealed class Mount : Component
    {
        public required Func<Element> Body;
        public override Element Render() => Body();
    }

    [Fact]
    public void Rows_flip_live_between_the_two_row_shell_and_the_one_row_toolbar()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var rows = new Signal<int>(2);
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("titlebar-rows", new Size2(1000, 400), 1f));
        window.Show();
        Element Body() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                TitleBar.Create(new TitleBarOptions
                {
                    Rows = rows,
                    Lead = static () => [new TitleBarItem("menu", static () => new BoxEl { Width = 40f, Height = 36f })],
                    ShowCaptionButtons = true,
                }),
                new BoxEl { Grow = 1f },
            ],
        };
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                                     new Mount { Body = Body });
        void Settle() { for (int i = 0; i < 8; i++) host.RunFrame(); }
        Settle();

        static (RectF CatchAll, RectF Close) Read(TitleBarRegion[] regions)
        {
            RectF close = default;
            foreach (var r in regions) if (r.Hit == TitleBarHit.CloseButton) close = r.RectDip;
            return (regions[^1].RectDip, close);
        }

        var two = Read(window.LastTitleBarRegions);
        Assert.Equal(TitleBar.TabRowHeight + TitleBar.ExpandedHeight, two.CatchAll.H, 0.5f);
        Assert.Equal(0f, two.Close.Y, 0.5f);
        Assert.Equal(TitleBar.TabRowHeight, two.Close.H, 0.5f);
        Assert.Equal(1000f, two.Close.Right, 0.5f);

        rows.Value = 1;
        Settle();
        var one = Read(window.LastTitleBarRegions);
        Assert.Equal(TitleBar.ExpandedHeight, one.CatchAll.H, 0.5f);
        Assert.Equal(TitleBar.ExpandedHeight, one.Close.H, 0.5f);
        Assert.Equal(1000f, one.Close.Right, 0.5f);
    }
}
