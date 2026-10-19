using System;
using System.Collections.Generic;
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

/// <summary>A formed <see cref="TabStrip"/>'s tabs EASE their width when the form re-resolves (a ladder rung at a tier
/// crossing): no frame shows the final width at once, the selected plate follows its cell every frame, the tabs after it
/// glide, and a new rung mid-flight bends the motion from where it is.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class TabStripFormMotionTests
{
    private sealed class Mount : Component
    {
        public required Func<Element> Body;
        public override Element Render() => Body();
    }

    private static NodeHandle Find(SceneStore s, string key)
    {
        var keys = new List<(NodeHandle Node, string Key)>();
        s.CopyDebugKeys(keys);
        foreach (var (node, k) in keys)
            if (k == key) return node;
        Assert.Fail($"no node keyed '{key}'");
        return default;
    }

    private static IReadOnlyList<TabViewItem> Tabs(int n)
    {
        var items = new TabViewItem[n];
        for (int i = 0; i < n; i++) items[i] = new TabViewItem { Key = "t" + i, Header = "Tab " + i };
        return items;
    }

    private sealed class Rig : IDisposable
    {
        public readonly Signal<TabStripForm> Form = new(TabStripForm.Ladder(200f));
        public readonly Signal<int> Selected = new(1);
        readonly HeadlessPlatformApp _app = new();
        public readonly AppHost Host;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var strings = new StringTable();
            var window = new HeadlessWindow(new WindowDesc("tab-form-motion", new Size2(1400, 400), 1f));
            window.Show();
            var items = Tabs(3);
            Element Body() => new BoxEl
            {
                Direction = 1,
                Children =
                [
                    TitleBar.Create(new TitleBarOptions
                    {
                        Rows = 2,
                        Tabs = () => Embed.Comp(() => new TabStrip
                        {
                            Appearance = TabStripAppearance.Chrome,
                            Items = items,
                            SelectedIndex = Selected,
                            Form = () => Form.Value,
                            TabHeight = 36f,
                        }),
                        ShowCaptionButtons = true,
                    }),
                    new BoxEl { Grow = 1f },
                ],
            };
            Host = new AppHost(_app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Mount { Body = Body });
            Settle();
        }

        public void Settle() { for (int i = 0; i < 40; i++) Host.RunFrame(); }
        public float W(string key) => Host.Scene.AbsoluteRect(Find(Host.Scene, key)).W;
        public float X(string key) => Host.Scene.AbsoluteRect(Find(Host.Scene, key)).X;
        public void Dispose() { Host.Dispose(); _app.Dispose(); }
    }

    [Fact]
    public void A_ladder_rung_change_eases_every_tab_and_the_selected_plate_follows_its_cell()
    {
        using var rig = new Rig();
        Assert.Equal(200f, rig.W("t0"), 0.5f);
        Assert.Equal(208f, rig.W("selected-bg"), 0.5f);   // the plate overhangs its cell by its 4-DIP flares

        foreach (float to in new[] { 240f, 160f })
        {
            float from = rig.W("t0");
            rig.Form.Value = TabStripForm.Ladder(to);
            var seen = new List<float>();
            for (int f = 0; f < 20; f++)
            {
                rig.Host.RunFrame();
                float w0 = rig.W("t0"), w1 = rig.W("t1");
                seen.Add(w0);
                Assert.Equal(w0, w1, 0.5f);                                       // one ladder: every tab the same width
                Assert.Equal(rig.X("t0") + w0, rig.X("t1"), 0.5f);                // the next tab rides the eased edge
                Assert.Equal(w1 + 8f, rig.W("selected-bg"), 0.5f);               // the plate follows its cell, every frame
                Assert.Equal(rig.X("t1") - 4f, rig.X("selected-bg"), 0.5f);
                // ReflowUnclipped: the cell never clips mid-flight, so the selected plate keeps its flares.
                Assert.Equal(NodeFlags.None, rig.Host.Scene.Flags(Find(rig.Host.Scene, "t1")) & NodeFlags.ClipsToBounds);
            }
            // It glided: some frame sat strictly between the rungs, and it landed on the new one.
            Assert.Contains(seen, w => MathF.Min(from, to) + 2f < w && w < MathF.Max(from, to) - 2f);
            Assert.Equal(to, seen[^1], 0.5f);
        }
    }

    [Fact]
    public void A_new_rung_mid_flight_bends_the_motion_from_where_it_is()
    {
        using var rig = new Rig();
        rig.Form.Value = TabStripForm.Ladder(240f);
        for (int f = 0; f < 4; f++) rig.Host.RunFrame();
        float mid = rig.W("t0");
        Assert.InRange(mid, 202f, 238f);

        rig.Form.Value = TabStripForm.Ladder(160f);
        rig.Host.RunFrame();
        float next = rig.W("t0");
        Assert.True(MathF.Abs(next - mid) < 30f, $"the retarget jumped {mid} -> {next}");
        rig.Settle();
        Assert.Equal(160f, rig.W("t0"), 0.5f);
        Assert.Equal(168f, rig.W("selected-bg"), 0.5f);
    }
}
