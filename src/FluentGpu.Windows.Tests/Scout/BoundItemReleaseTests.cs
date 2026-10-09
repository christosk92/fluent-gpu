using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A bound slot's item memo (the gated <c>BoundItemsSource&lt;T&gt;.BindItem</c> overload that
/// <c>ItemsView.CreateBound&lt;T&gt;</c> calls once per slot) has no owner: once its slot is gone it must not stay
/// subscribed to a snapshot that outlives the list (a list re-keyed on every find-box keystroke over a page snapshot).</summary>
public sealed class BoundItemReleaseTests
{
    [Fact]
    public void Unmounted_slot_memos_leave_the_long_lived_snapshot()
    {
        var rt = new ReactiveRuntime();
        var snapshot = new Signal<IReadOnlyList<int>>(new[] { 1, 2, 3 });
        var source = BoundItems.From<int>(snapshot, -1);

        for (int remount = 0; remount < 50; remount++)
        {
            var slot = new Signal<int>(remount % 3);
            var item = source.BindItem(slot, rt);
            var bind = new Effect(rt, () => _ = item.Value);
            bind.Dispose();                                                  // the slot unmounts: its binds are disposed
            snapshot.Value = new[] { remount, remount + 1, remount + 2 };    // the page's snapshot keeps moving
            rt.Flush();
        }

        Assert.Equal(0, snapshot.SubscriberCount);
    }

    [Fact]
    public void A_never_read_slot_memo_is_not_subscribed()
    {
        var rt = new ReactiveRuntime();
        var snapshot = new Signal<IReadOnlyList<int>>(new[] { 1, 2, 3 });
        var source = BoundItems.From<int>(snapshot, -1);

        var item = source.BindItem(new Signal<int>(1), rt);   // a template that never reads scope.Item
        Assert.Equal(0, snapshot.SubscriberCount);
        snapshot.Value = new[] { 4, 5, 6 };
        Assert.Equal(5, item.Peek());                         // an invoke handler still resolves the current item
        Assert.Equal(0, snapshot.SubscriberCount);
    }

    [Fact]
    public void An_observed_slot_memo_stays_live_and_equality_gated()
    {
        var rt = new ReactiveRuntime();
        var snapshot = new Signal<IReadOnlyList<int>>(new[] { 1, 2, 3 });
        var source = BoundItems.From<int>(snapshot, -1);
        var slot = new Signal<int>(1);
        var item = source.BindItem(slot, rt);
        int runs = 0, seen = 0;
        using var bind = new Effect(rt, () => { runs++; seen = item.Value; });

        snapshot.Value = new[] { 9, 2, 9 };                   // equal item at this slot: no downstream run
        rt.Flush();
        Assert.Equal(1, runs);

        snapshot.Value = new[] { 9, 7, 9 };
        rt.Flush();
        Assert.Equal(2, runs);
        Assert.Equal(7, seen);

        slot.Value = 2;                                       // a recycle
        rt.Flush();
        Assert.Equal(3, runs);
        Assert.Equal(9, seen);
        Assert.Equal(1, snapshot.SubscriberCount);
    }

    [Fact]
    public void A_projected_slot_signal_releases_the_item_memo_behind_it()
    {
        var rt = new ReactiveRuntime();
        var snapshot = new Signal<IReadOnlyList<int>>(new[] { 1, 2, 3 });
        var source = BoundItems.From<int>(snapshot, -1);

        for (int remount = 0; remount < 20; remount++)
        {
            var slot = new Signal<int>(0);
            var row = new RowScope(slot, static () => false, static () => false, static () => true, static (_, _) => { }, static _ => { }) { Runtime = rt };
            var scope = new BoundItemScope<int>(row, source.BindItem(slot, rt));
            var odd = scope.Signal(static x => (x & 1) != 0);   // a sub-component's own reactive input
            var reader = new Effect(rt, () => _ = odd.Value);
            reader.Dispose();
            snapshot.Value = new[] { remount + 10, 0, 0 };
            rt.Flush();
        }

        Assert.Equal(0, snapshot.SubscriberCount);
    }
}
