using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// View slots (<see cref="Slots.View{TProps}"/>): a template is resolved by the nearest <see cref="ViewRegistry"/>. A
/// registry swap re-runs only the slot hosts; a changed template id remounts the slot and disposes the old template's
/// hooks; equal props run no template; sibling slots keep identity under caller keys; a missing key renders nothing and
/// logs once. Serial: it constructs hosts and swaps the process-wide <see cref="Diag.Sink"/>.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ViewSlotTests
{
    [Fact]
    public void RegistrySwap_ReRunsTheTemplateWithoutReRenderingTheEmbedder()
    {
        var c = new SlotCounters();
        var registry = new Signal<ViewRegistry?>(SlotTemplates.Make(c, "classic", "v1"));
        var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
        using var h = new SlotHarness(new SlotRoot(registry, null, page));
        h.Pump();
        Assert.Equal(1, c.Runs);
        Assert.Equal(1, c.Mounted);
        Assert.Equal(1, page.Renders);

        registry.Value = SlotTemplates.Make(c, "classic", "v2");   // same template id, newer epoch: re-run in place
        h.Pump();

        Assert.Equal(2, c.Runs);
        Assert.Equal("v2", c.LastLabel);
        Assert.Equal(1, page.Renders);   // the embedder is autonomous: only the slot host reacted
        Assert.Equal(1, c.Mounted);      // not a remount
        Assert.Equal(0, c.Cleaned);
    }

    [Fact]
    public void TemplateChange_RemountsTheSlotAndDisposesTheOldTemplate()
    {
        var oldC = new SlotCounters();
        var newC = new SlotCounters();
        var registry = new Signal<ViewRegistry?>(SlotTemplates.Make(oldC, "classic.bar", "classic"));
        var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
        using var h = new SlotHarness(new SlotRoot(registry, null, page));
        h.Pump(8);
        Assert.Equal(1, oldC.Mounted);
        Assert.True(oldC.Ticks > 0, "the old template's interval should be running");

        registry.Value = SlotTemplates.Make(newC, "zune.bar", "zune");   // a different template id for the same key
        h.Pump(2);

        Assert.Equal(1, oldC.Cleaned);   // the old template's UseEffect cleanup ran
        Assert.Equal(1, newC.Runs);
        Assert.Equal(1, newC.Mounted);
        Assert.Equal(1, page.Renders);
        int frozen = oldC.Ticks;
        h.Tick(8);   // frames that render: the headless timer clock steps only on those
        Assert.Equal(frozen, oldC.Ticks);   // the old interval stopped
        Assert.True(newC.Ticks > 0, "the new template's interval should be running");
        Assert.Equal(1, newC.Mounted);

        registry.Value = SlotTemplates.Make(oldC, "classic.bar", "classic");   // and back: another remount
        h.Pump(2);
        Assert.Equal(1, newC.Cleaned);
        Assert.Equal(2, oldC.Mounted);
    }

    [Fact]
    public void EqualProps_SkipTheTemplate_ChangedPropsRunIt()
    {
        var c = new SlotCounters();
        var registry = new Signal<ViewRegistry?>(SlotTemplates.Make(c, "classic", "v1"));
        var tick = new Signal<int>(0);
        var title = new Signal<string>("a");
        var page = new SlotPage(() =>
        {
            _ = tick.Value;
            return Slots.View(Views.Bar, new BarProps(title.Value));   // a fresh, value-equal record on every render
        });
        using var h = new SlotHarness(new SlotRoot(registry, null, page));
        h.Pump();
        Assert.Equal(1, c.Runs);

        for (int i = 1; i <= 3; i++)
        {
            tick.Value = i;
            h.Pump(2);
        }
        Assert.Equal(4, page.Renders);   // the embedder re-rendered every time ...
        Assert.Equal(1, c.Runs);         // ... and the template never ran again

        title.Value = "b";
        h.Pump(2);
        Assert.Equal(2, c.Runs);
        Assert.Equal("b", c.LastTitle);
        Assert.Equal(1, c.Mounted);
    }

    [Fact]
    public void SiblingSlotsWithTheSameKeyAndDifferentCallerKeys_KeepIdentityAcrossAReorder()
    {
        const int N = 48;   // above the 32-sibling mark where child reconciliation switches strategy
        var c = new SlotCounters();
        var registry = new Signal<ViewRegistry?>(SlotTemplates.Make(c, "classic", "v1"));
        var reversed = new Signal<bool>(false);
        var page = new SlotPage(() =>
        {
            var kids = new Element[N];
            for (int i = 0; i < N; i++)
            {
                int n = reversed.Value ? N - 1 - i : i;
                kids[i] = Slots.View(Views.Bar, new BarProps("t" + n), callerKey: "row-" + n);
            }
            return new BoxEl { Direction = 1, Children = kids };
        });
        using var h = new SlotHarness(new SlotRoot(registry, null, page));
        h.Pump();
        Assert.Equal(N, c.Runs);
        Assert.Equal(N, c.Mounted);

        reversed.Value = true;
        h.Pump(2);

        Assert.Equal(2, page.Renders);
        Assert.Equal(N, c.Runs);      // every host kept its props: no template ran again
        Assert.Equal(N, c.Mounted);   // nothing mounted twice
        Assert.Equal(0, c.Cleaned);   // nothing unmounted: identity followed the caller key
    }

    [Fact]
    public void ACallersOwnKeyIsNeverOverwritten()
    {
        var withCaller = Slots.View(Views.Bar, new BarProps("a"), callerKey: "x") with { Key = "mine" };
        Assert.Equal("mine", withCaller.Key);
        Assert.Equal("test.bar|x", Slots.View(Views.Bar, new BarProps("a"), callerKey: "x").Key);
        Assert.Null(Slots.View(Views.Bar, new BarProps("a")).Key);
    }

    [Fact]
    public void AMissingKey_RendersNothingAndLogsOnce_ThenRendersWhenARegistryProvidesIt()
    {
        var lines = new List<string>();
        var previous = Diag.Sink;
        Diag.Sink = lines.Add;
        try
        {
            var c = new SlotCounters();
            var registry = new Signal<ViewRegistry?>(new ViewRegistryBuilder().Build());
            var tick = new Signal<int>(0);
            var page = new SlotPage(() =>
            {
                _ = tick.Value;
                return Slots.View(Views.Bar, new BarProps("a"));
            });
            using var h = new SlotHarness(new SlotRoot(registry, null, page));
            h.Pump();
            tick.Value = 1;
            h.Pump(2);
            tick.Value = 2;
            h.Pump(2);

            Assert.Single(lines, l => l.Contains("test.bar@1"));
            Assert.Equal(0, c.Runs);

            registry.Value = SlotTemplates.Make(c, "classic", "v1");
            h.Pump(2);
            Assert.Equal(1, c.Runs);
            Assert.Equal(1, c.Mounted);
            Assert.Single(lines, l => l.Contains("test.bar@1"));   // no new report
        }
        finally { Diag.Sink = previous; }
    }

    [Fact]
    public void NoRegistryAtAll_RendersNothingAndLogsOnce()
    {
        var lines = new List<string>();
        var previous = Diag.Sink;
        Diag.Sink = lines.Add;
        try
        {
            var key = new ViewKey<BarProps>("test.noregistry@1");
            var tick = new Signal<int>(0);
            var page = new SlotPage(() =>
            {
                _ = tick.Value;
                return Slots.View(key, new BarProps("a"));
            });
            using var h = new SlotHarness(new NoRegistryRoot(page));
            h.Pump();
            tick.Value = 1;
            h.Pump(2);
            Assert.Single(lines, l => l.Contains("test.noregistry@1"));
        }
        finally { Diag.Sink = previous; }
    }

    private sealed class NoRegistryRoot(SlotPage page) : Component
    {
        public override Element Render() => Embed.Comp(() => page);
    }

    private sealed record Roles(string Name);
    private sealed record Metrics(int Row);

    [Fact]
    public void ATemplateReadsAppRolesAndStylesAndNestsAnotherSlot()
    {
        var outer = new ViewKey<BarProps>("test.outer@1");
        var inner = new ViewKey<BarProps>("test.inner@1");
        var metrics = new StyleKey<Metrics>("test.metrics");
        string? roleName = null;
        int row = 0, innerRuns = 0;

        var registry = new ViewRegistryBuilder()
            .AddStyle(metrics, new Metrics(44))
            .Add(outer, (BarProps p, in ViewContext ctx) =>
            {
                roleName = ctx.App<Roles>().Name;
                row = ctx.StyleOf(metrics).Row;
                return new BoxEl { Children = [ctx.View(inner, new BarProps("in"), "k")] };
            }, "outer.t")
            .Add(inner, (BarProps p, in ViewContext ctx) =>
            {
                innerRuns++;
                return new BoxEl { Height = 10f };
            }, "inner.t")
            .Build();

        var roles = new Roles("wavee");
        var page = new SlotPage(() => Ctx.Provide(ViewContext.AppRolesContext, roles, Slots.View(outer, new BarProps("o"))));
        using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), null, page));
        h.Pump();

        Assert.Equal("wavee", roleName);
        Assert.Equal(44, row);
        Assert.Equal(1, innerRuns);
    }

    [Fact]
    public void ARegistry_RejectsDuplicates_StampsEpochs_AndAnswersLookups()
    {
        var a = new ViewRegistryBuilder().Add(Views.Bar, static (BarProps p, in ViewContext ctx) => new BoxEl(), "t1").Build();
        var b = new ViewRegistryBuilder().Build();
        Assert.True(b.Epoch > a.Epoch);
        Assert.True(a.Contains("test.bar@1"));
        Assert.Equal("t1", a.TemplateIdOf("test.bar@1"));
        Assert.Null(a.TemplateIdOf("test.other@1"));
        Assert.Equal(1, a.ViewCount);
        Assert.True(a.TryGetView<BarProps>("test.bar@1", out _, out string id));
        Assert.Equal("t1", id);
        Assert.False(a.TryGetView<Roles>("test.bar@1", out _, out _));   // registered for other props

        var builder = new ViewRegistryBuilder().Add(Views.Bar, static (BarProps p, in ViewContext ctx) => new BoxEl(), "t1");
        Assert.Throws<InvalidOperationException>(() =>
            builder.Add(Views.Bar, static (BarProps p, in ViewContext ctx) => new BoxEl(), "t2"));
        Assert.Throws<InvalidOperationException>(() =>
            new ViewRegistryBuilder().AddStyle(new StyleKey<Metrics>("m"), new Metrics(1)).AddStyle(new StyleKey<Metrics>("m"), new Metrics(2)));
        Assert.Equal(7, new ViewRegistryBuilder().Build(7).Epoch);
    }
}
