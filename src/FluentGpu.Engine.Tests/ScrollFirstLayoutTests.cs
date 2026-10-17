using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E8 (EP12, Classic tuning F12): a scroller with a <c>ScrollKey</c> restores its remembered offset (ScrollPositionMemory) in
/// the FIRST layout of the remount, so a returning page never paints at offset 0 and then jumps. A virtual list realizes the
/// window around the restored offset in that same frame (no top rows are built first); content shorter than the remembered
/// offset clamps; an unknown key starts at 0; a KeepAlive re-activation keeps its position. Every check reads the state after
/// exactly ONE frame of the remount.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ScrollFirstLayoutTests
{
    private const float ViewH = 400f, RowH = 40f;

    private sealed record Page(string Kind, string Key, int Size);   // Kind: plain (Size = rows) | virt (Size = items)

    private sealed class Pages : Component
    {
        public readonly Signal<Page?> Current = new(null);
        public NodeHandle Vp, Marker;
        public readonly List<int> Built = new();

        public override Element Render()
        {
            var page = Current.Value;
            if (page is null) return new BoxEl { Width = 300f, Height = ViewH };
            if (page.Kind == "virt")
                return new VirtualListEl
                {
                    ItemCount = page.Size, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
                    Width = 300f, Height = ViewH, ScrollKey = page.Key, OnRealized = h => Vp = h,
                    RenderItem = i => { Built.Add(i); return new BoxEl { Height = RowH, Width = 280f, Fill = ColorF.FromRgba(40, 40, 40) }; },
                };
            var rows = new Element[page.Size];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl
                {
                    Height = RowH, Fill = ColorF.FromRgba(40, 40, (byte)(i % 2 == 0 ? 40 : 60)),
                    OnRealized = i == 20 ? h => Marker = h : null,
                };
            return new ScrollEl
            {
                Width = 300f, Height = ViewH, ScrollKey = page.Key, OnRealized = h => Vp = h,
                Content = new BoxEl { Direction = 1, Children = rows },
            };
        }
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public AppHost Host { get; }
        public Pages Root { get; } = new();

        public Rig()
        {
            var strings = new StringTable();
            var window = new HeadlessWindow(new WindowDesc("scroll-first-layout", new Size2(300, ViewH), 1f));
            window.Show();
            Host = new AppHost(App, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, Root);
            Host.RunFrame();
        }

        public ScrollState State => Host.Scene.TryGetScroll(Root.Vp, out var s) ? s : default;

        /// <summary>Show <paramref name="page"/>, scroll it to <paramref name="offset"/>, then unmount it (the host saves the position).</summary>
        public void Visit(Page page, double offset)
        {
            Root.Current.Value = page;
            for (int i = 0; i < 4; i++) Host.RunFrame();
            Host.TryGetScrollHandle(Root.Vp)!.ScrollTo(offset, ScrollMove.Immediate);
            for (int i = 0; i < 4; i++) Host.RunFrame();
            Assert.Equal(offset, State.Offset, 0.5);                    // the visit really was at that offset
            Root.Current.Value = null;
            for (int i = 0; i < 4; i++) Host.RunFrame();
        }

        /// <summary>Mount <paramref name="page"/> and run EXACTLY one frame; the builds recorded so far are cleared first.</summary>
        public void Return(Page page)
        {
            Root.Built.Clear();
            Root.Current.Value = page;
            Host.RunFrame();
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    // ── plain ScrollEl ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlainScroller_RemountPresentsAtTheRememberedOffsetInTheFirstFrame()
    {
        using var rig = new Rig();
        var page = new Page("plain", "plain:a", 100);          // 4000 DIP of rows
        rig.Visit(page, 500);

        rig.Return(page);                                       // ONE frame
        Assert.Equal(500.0, rig.State.Offset, 0.5);
        var vpRect = rig.Host.Scene.AbsoluteRect(rig.Root.Vp);
        var marker = rig.Host.Scene.AbsoluteRect(rig.Root.Marker);   // row 20 sits at content y 800
        Assert.Equal(vpRect.Y + 800f - 500f, marker.Y, 1f);    // drawn at the remembered position, not at the top

        rig.Host.RunFrame();                                    // and nothing jumps afterwards
        Assert.Equal(500.0, rig.State.Offset, 0.5);
        Assert.Equal(vpRect.Y + 800f - 500f, rig.Host.Scene.AbsoluteRect(rig.Root.Marker).Y, 1f);
    }

    [Fact]
    public void APlainScroller_ClampsARememberedOffsetToShorterContent()
    {
        using var rig = new Rig();
        rig.Visit(new Page("plain", "plain:b", 100), 3000);
        rig.Return(new Page("plain", "plain:b", 25));          // 1000 DIP: the end is at 600
        Assert.Equal(600.0, rig.State.Offset, 0.5);
        rig.Host.RunFrame();
        Assert.Equal(600.0, rig.State.Offset, 0.5);
    }

    [Fact]
    public void APlainScroller_WithAnUnknownKeyStartsAtTheTop()
    {
        using var rig = new Rig();
        rig.Visit(new Page("plain", "plain:c", 100), 700);
        rig.Return(new Page("plain", "plain:never-seen", 100));
        Assert.Equal(0.0, rig.State.Offset, 0.5);
    }

    // ── a 2,000-row virtual list ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVirtualList_RemountRealizesTheWindowAroundTheRememberedOffsetInTheFirstFrame()
    {
        using var rig = new Rig();
        var page = new Page("virt", "virt:a", 2000);
        rig.Visit(page, 40_000);                                // item 1000

        rig.Return(page);
        var sc = rig.State;
        Assert.Equal(40_000.0, sc.Offset, 0.5);
        Assert.True(sc.FirstRealized <= 1000 && 1000 < sc.LastRealized, $"realized [{sc.FirstRealized},{sc.LastRealized})");
        Assert.NotEmpty(rig.Root.Built);
        Assert.True(rig.Root.Built.Min() > 900, $"no top rows are built first (lowest built index {rig.Root.Built.Min()})");

        rig.Host.RunFrame();
        Assert.Equal(40_000.0, rig.State.Offset, 0.5);
        Assert.True(rig.Root.Built.Min() > 900);
    }

    [Fact]
    public void AVirtualList_ClampsARememberedOffsetToAShorterList_AndRealizesTheEndWindow()
    {
        using var rig = new Rig();
        rig.Visit(new Page("virt", "virt:b", 2000), 40_000);
        rig.Return(new Page("virt", "virt:b", 100));            // 4000 DIP: the end is at 3600
        var sc = rig.State;
        Assert.Equal(3600.0, sc.Offset, 0.5);
        Assert.True(sc.LastRealized == 100, $"realized [{sc.FirstRealized},{sc.LastRealized})");
        Assert.True(rig.Root.Built.Min() > 40, $"no top rows are built first (lowest built index {rig.Root.Built.Min()})");
    }

    [Fact]
    public void AVirtualList_WithAnUnknownKeyStartsAtTheTop()
    {
        using var rig = new Rig();
        rig.Visit(new Page("virt", "virt:c", 2000), 20_000);
        rig.Return(new Page("virt", "virt:never-seen", 2000));
        Assert.Equal(0.0, rig.State.Offset, 0.5);
        Assert.Contains(0, rig.Root.Built);
    }

    // ── KeepAlive re-activation ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class KeepAliveRoot : Component
    {
        public readonly Signal<string> Active = new("a");
        public NodeHandle Vp;

        public override Element Render() => Flow.KeepAlive(() => Active.Value, static k => k, k => k == "a"
            ? (Element)new ScrollEl
            {
                Width = 300f, Height = ViewH, ScrollKey = "keepalive:a", OnRealized = h => Vp = h,
                Content = new BoxEl { Direction = 1, Height = 4000f },
            }
            : new BoxEl { Width = 300f, Height = ViewH });
    }

    [Fact]
    public void AKeepAliveReactivation_ShowsThePageAtItsPositionInTheFirstFrame()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-first-layout-ka", new Size2(300, ViewH), 1f));
        window.Show();
        var root = new KeepAliveRoot();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 4; i++) host.RunFrame();
        host.TryGetScrollHandle(root.Vp)!.ScrollTo(500, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        Assert.Equal(500.0, host.Scene.ScrollRef(root.Vp).Offset, 0.5);

        root.Active.Value = "b";
        for (int i = 0; i < 6; i++) host.RunFrame();
        root.Active.Value = "a";
        host.RunFrame();                                        // ONE frame after coming back

        Assert.Equal(500.0, host.Scene.ScrollRef(root.Vp).Offset, 0.5);
    }
}
