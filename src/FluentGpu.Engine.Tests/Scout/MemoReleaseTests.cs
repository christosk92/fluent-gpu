using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A memo built with <c>releaseWhenUnobserved</c> is linked to its sources only while something subscribes to
/// it: an owner-less memo whose readers are gone must not stay in a longer-lived source's subscriber list, while an
/// observed one keeps the equality cut-off.</summary>
public sealed class MemoReleaseTests
{
    [Fact]
    public void A_disposed_reader_takes_the_memo_off_its_source()
    {
        var rt = new ReactiveRuntime();
        var src = new Signal<int>(1);
        for (int i = 0; i < 50; i++)
        {
            var memo = new Memo<int>(rt, () => src.Value * 2, releaseWhenUnobserved: true);
            var reader = new Effect(rt, () => _ = memo.Value);
            Assert.Equal(1, src.SubscriberCount);
            reader.Dispose();                     // the slot unmounts: its binds are disposed
            src.Value = i + 2;                    // the long-lived source keeps moving
            rt.Flush();
        }
        Assert.Equal(0, src.SubscriberCount);
    }

    [Fact]
    public void A_never_read_memo_holds_no_link_and_a_peek_still_resolves()
    {
        var rt = new ReactiveRuntime();
        var src = new Signal<int>(1);
        var memo = new Memo<int>(rt, () => src.Value + 1, releaseWhenUnobserved: true);
        Assert.Equal(0, src.SubscriberCount);
        src.Value = 5;
        Assert.Equal(6, memo.Peek());
        Assert.Equal(0, src.SubscriberCount);
        Assert.Equal(6, memo.Value);              // an untracked read computes too, and keeps no link
        Assert.Equal(0, src.SubscriberCount);
    }

    [Fact]
    public void An_observed_memo_keeps_the_equality_cut_off_across_reader_reruns()
    {
        var rt = new ReactiveRuntime();
        var src = new Signal<int>(2);
        var other = new Signal<int>(0);
        var parity = new Memo<int>(rt, () => src.Value & 1, releaseWhenUnobserved: true);
        int runs = 0, seen = -1;
        using var reader = new Effect(rt, () => { runs++; _ = other.Value; seen = parity.Value; });

        src.Value = 4; rt.Flush();                // equal parity: the reader stays put
        Assert.Equal(1, runs);
        other.Value = 1; rt.Flush();              // the reader re-runs on its own source: release + re-link
        Assert.Equal(2, runs);
        Assert.Equal(1, src.SubscriberCount);
        src.Value = 5; rt.Flush();                // the re-linked memo still notifies a real change
        Assert.Equal(3, runs);
        Assert.Equal(1, seen);
        src.Value = 7; rt.Flush();
        Assert.Equal(3, runs);
    }

    [Fact]
    public void A_chain_of_releasing_memos_lets_go_of_a_source_both_read()
    {
        var rt = new ReactiveRuntime();
        var src = new Signal<int>(1);
        var item = new Memo<int>(rt, () => src.Value * 10, releaseWhenUnobserved: true);
        var projected = new Memo<int>(rt, () => item.Value + src.Value, releaseWhenUnobserved: true);
        int seen = 0;
        var reader = new Effect(rt, () => seen = projected.Value);
        Assert.Equal(11, seen);
        src.Value = 2; rt.Flush();
        Assert.Equal(22, seen);
        Assert.Equal(2, src.SubscriberCount);    // item and projected

        reader.Dispose();                        // projected loses its reader, then item loses projected
        Assert.Equal(0, src.SubscriberCount);
        src.Value = 3; rt.Flush();
        Assert.Equal(33, projected.Peek());
        Assert.Equal(0, src.SubscriberCount);
    }
}
