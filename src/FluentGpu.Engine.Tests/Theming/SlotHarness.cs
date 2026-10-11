using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>Props of the test slot.</summary>
internal sealed record BarProps(string Title);

internal static class Views
{
    public static readonly ViewKey<BarProps> Bar = new("test.bar@1");
}

/// <summary>What a test template records about itself.</summary>
internal sealed class SlotCounters
{
    public int Runs, Mounted, Cleaned, Ticks;
    public string? LastTitle, LastLabel;
    public bool ReadA, ReadB;
    public IOptionRequests? Requests;
}

/// <summary>A component that builds whatever the test asks for and counts its own renders (the "embedder": a registry swap
/// must not re-render it).</summary>
internal sealed class SlotPage(Func<Element> build) : Component
{
    public int Renders;

    public override Element Render()
    {
        Renders++;
        return build();
    }
}

/// <summary>The root: provides the registry (and optionally an option store) around the page.</summary>
internal sealed class SlotRoot(Signal<ViewRegistry?> registry, OptionStore? store, SlotPage page) : Component
{
    public override Element Render()
    {
        var reg = registry.Value;
        Element content = new BoxEl { Direction = 1, Children = [Embed.Comp(() => page), Embed.Comp(static () => new Heartbeat())] };
        if (store is not null) content = Ctx.Provide(OptionStore.Context, store, content);
        return Ctx.Provide(ViewRegistry.Context, reg, content);
    }
}

internal static class SlotTemplates
{
    /// <summary>A registry whose only view is <see cref="Views.Bar"/>. Every registry from this method runs the same template
    /// code, so the hook cells of two of them share call sites: the same <paramref name="templateId"/> is the same template
    /// (re-run, no remount); a different one is a different template (remount).</summary>
    public static ViewRegistry Make(SlotCounters c, string templateId, string label)
        => new ViewRegistryBuilder().Add(Views.Bar, (BarProps p, in ViewContext ctx) => Render(c, label, p, in ctx), templateId).Build();

    private static Element Render(SlotCounters c, string label, BarProps p, in ViewContext ctx)
    {
        c.Runs++;
        c.LastTitle = p.Title;
        c.LastLabel = label;
        ctx.Hooks.UseEffect(() => { c.Mounted++; return () => c.Cleaned++; }, DepKey.From(0));
        ctx.Hooks.UseInterval(() => c.Ticks++, 16f);
        c.Requests = ctx.OptionRequests;
        return new BoxEl { Height = 10f };
    }
}

/// <summary>Re-renders on every <see cref="SlotHarness.Beat"/> bump. The headless timer clock advances only on frames that
/// render, so a test that waits on an interval needs frames that render something.</summary>
internal sealed class Heartbeat : Component
{
    public override Element Render() => new BoxEl { Width = SlotHarness.Beat.Value % 2, Height = 0f };
}

/// <summary>A headless <see cref="AppHost"/> over a <see cref="SlotRoot"/>.</summary>
internal sealed class SlotHarness : IDisposable
{
    private readonly HeadlessPlatformApp _app = new();
    public AppHost Host { get; }

    public SlotHarness(Component root)
    {
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("view-slots", new Size2(360, 240), 1f));
        window.Show();
        Host = new AppHost(_app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
    }

    /// <summary>Bumped by <see cref="Beat"/>-driven pumps (serial collection: one harness at a time).</summary>
    public static readonly Signal<int> Beat = new(0);

    public void Pump(int frames = 4)
    {
        for (int i = 0; i < frames; i++) Host.RunFrame();
    }

    /// <summary>Run frames that each render (the heartbeat), so the headless timer clock advances one step per frame.</summary>
    public void Tick(int frames)
    {
        for (int i = 0; i < frames; i++) { Beat.Value++; Host.RunFrame(); }
    }

    public void Dispose()
    {
        Host.Dispose();
        _app.Dispose();
    }
}
