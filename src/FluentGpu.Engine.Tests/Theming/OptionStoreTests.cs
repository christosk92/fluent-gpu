using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// Option cells (<see cref="OptionStore"/>): one signal per option id, tracked reads, writes routed to the app's
/// <see cref="IOptionRequests"/> sink. Serial: the host tests construct hosts and one test swaps <see cref="Diag.Sink"/>.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class OptionStoreTests
{
    private static readonly OptionKey<bool> A = new("test.a", false);
    private static readonly OptionKey<bool> B = new("test.b", false);
    private static readonly OptionKey<int> N = new("test.n", 3);

    private sealed class MapPersistence : IOptionPersistence
    {
        public readonly Dictionary<string, object?> Values = new();
        public int Saves;
        public bool TryLoad(string optionId, out object? raw) => Values.TryGetValue(optionId, out raw);
        public void Save(string optionId, object? raw) { Values[optionId] = raw; Saves++; }
    }

    [Fact]
    public void AnUnsetOption_ReadsItsDefault_AndPublishCommitsAndBumpsTheEpoch()
    {
        var store = new OptionStore();
        Assert.Equal(3, store.Get(N));
        Assert.Equal(0, store.Epoch);

        Assert.True(store.Publish(N, 9));
        Assert.Equal(9, store.Get(N));
        Assert.Equal(1, store.Epoch);

        Assert.False(store.Publish(N, 9));   // an equal value changes nothing
        Assert.Equal(1, store.Epoch);
    }

    [Fact]
    public void Persistence_SeedsACellOfTheRightType_AndSavesEveryCommit()
    {
        var persistence = new MapPersistence();
        persistence.Values["test.a"] = true;
        persistence.Values["test.n"] = "not an int";   // wrong raw type: ignored, the default wins
        var store = new OptionStore(persistence);

        Assert.True(store.Get(A));
        Assert.Equal(3, store.Get(N));

        store.Publish(B, true);
        Assert.Equal(true, persistence.Values["test.b"]);
        store.Publish(B, true);   // unchanged: nothing to save
        Assert.Equal(1, persistence.Saves);
        store.Publish(N, 4, persist: false);
        Assert.Equal(1, persistence.Saves);
    }

    [Fact]
    public void ReadingAnOptionWithAnotherValueType_Throws()
    {
        var store = new OptionStore();
        store.Get(new OptionKey<bool>("test.same", false));
        Assert.Throws<InvalidOperationException>(() => store.Get(new OptionKey<int>("test.same", 0)));
    }

    [Fact]
    public void WritesGoThroughTheInstalledSink_NeverDirectly()
    {
        var store = new OptionStore();
        var seen = new List<(string Id, object? Value)>();
        var forwarder = store.RequestForwarder;   // captured BEFORE a sink exists, like a handler built during render

        var lines = new List<string>();
        var previous = Diag.Sink;
        Diag.Sink = lines.Add;
        try { forwarder.Set(A, true); }
        finally { Diag.Sink = previous; }
        Assert.Single(lines, l => l.Contains("test.a"));   // no sink installed: dropped and reported
        Assert.False(store.Get(A));                          // and nothing was written

        store.Requests = new DelegateOptionRequests((id, v) => seen.Add((id, v)));
        forwarder.Set(A, true);
        Assert.Single(seen);
        Assert.Equal("test.a", seen[0].Id);
        Assert.Equal(true, seen[0].Value);
        Assert.False(store.Get(A));   // a request alone never changes a cell; the app commits with Publish

        store.Publish(A, true);
        Assert.True(store.Get(A));
    }

    [Fact]
    public void ATemplateRerunsWhenAnOptionItReadChanges_AndNotWhenAnotherOptionDoes()
    {
        var c = new SlotCounters();
        var store = new OptionStore();
        var registry = new ViewRegistryBuilder()
            .Add(Views.Bar, (BarProps p, in ViewContext ctx) =>
            {
                c.Runs++;
                c.ReadA = ctx.Options.Get(A);
                return new BoxEl { Height = 10f };
            }, "opt")
            .Build();
        var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
        using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), store, page));
        h.Pump();
        Assert.Equal(1, c.Runs);
        Assert.False(c.ReadA);

        store.Publish(B, true);   // an option the template never read
        h.Pump(2);
        Assert.Equal(1, c.Runs);

        store.Publish(A, true);
        h.Pump(2);
        Assert.Equal(2, c.Runs);
        Assert.True(c.ReadA);
        Assert.Equal(1, page.Renders);   // only the slot's body reacted

        store.Publish(A, true);   // same value: coalesced
        h.Pump(2);
        Assert.Equal(2, c.Runs);
    }

    [Fact]
    public void ARequestFromATemplateReachesTheAppSink_WhichCommitsAndRerendersIt()
    {
        var c = new SlotCounters();
        var store = new OptionStore();
        store.Requests = new DelegateOptionRequests((id, v) =>
        {
            if (id == A.Id) store.Publish(A, (bool)v!);   // the app's Themes.Apply, reduced to its commit
        });
        var registry = new ViewRegistryBuilder()
            .Add(Views.Bar, (BarProps p, in ViewContext ctx) =>
            {
                c.Runs++;
                c.ReadA = ctx.Options.Get(A);
                c.Requests = ctx.OptionRequests;
                return new BoxEl { Height = 10f };
            }, "opt")
            .Build();
        var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
        using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), store, page));
        h.Pump();
        Assert.False(c.ReadA);

        c.Requests!.Set(A, true);
        h.Pump(2);

        Assert.True(c.ReadA);
        Assert.Equal(2, c.Runs);
    }

    [Fact]
    public void WithoutAStore_OptionsReadAsTheirDefaults_AndRequestsAreDropped()
    {
        var c = new SlotCounters();
        var registry = new ViewRegistryBuilder()
            .Add(Views.Bar, (BarProps p, in ViewContext ctx) =>
            {
                c.Runs++;
                c.ReadA = ctx.Options.Get(new OptionKey<bool>("test.nostore", true));
                return new BoxEl { Height = 10f };
            }, "opt")
            .Build();
        var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
        using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), null, page));
        h.Pump();
        Assert.Equal(1, c.Runs);
        Assert.True(c.ReadA);
    }
}
