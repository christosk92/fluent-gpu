using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Render.Tiles;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The .NET 10.0.8 dropped-zeroing miscompile (docs/plans/dotnet-jit-zeroing-miscompile.md), pinned against every
/// engine store that hands out a REUSED slot after zeroing it. Once the tier-1 (PGO) JIT has compiled the caller, a
/// whole-slot zero through a reference (<c>ref T s = ref a[i]; s = default;</c>, <c>this = default</c>,
/// <c>Unsafe.InitBlock*</c>) is DROPPED when the next store writes a zero struct into the slot's first field: the slot
/// keeps the previous tenant's values. Only the optimising JIT does it, so these facts only bite in <c>-c Release</c>;
/// each loops well past the tier-up point (the first stale slot appears at round ~161 on 10.0.8).
///
/// <para>A generic store is probed with <see cref="Worst"/> and the minimal trigger (<c>First = default</c> after the
/// hand-out); a concrete site is driven through its own API with its own writes.</para></summary>
public sealed class SlotZeroingTests
{
    private const int Rounds = 3000;
    private const int Slots = 40;

    private struct Pair { public long X, Y; }

    /// <summary>The trigger's layout: a struct-typed first field, then fields a previous tenant can leave behind.</summary>
    private struct Worst
    {
        public Pair First;
        public long Left, Behind;
        public int Kind;
    }

    private static bool Clean(in Worst w) => w.Left == 0 && w.Behind == 0 && w.Kind == 0;

    /// <summary>A pooled append list in the miscompile's exact shape, handing out through <see cref="FreshSlot.Of{T}"/>.
    /// With <c>ref Worst r = ref a[n++]; r = default;</c> in its place this goes stale at round 161 on .NET 10.0.8.</summary>
    private sealed class PooledList
    {
        private Worst[] _a = new Worst[16];
        public int Count;

        public ref Worst Add()
        {
            if (Count == _a.Length) System.Array.Resize(ref _a, _a.Length * 2);
            return ref FreshSlot.Of(_a, Count++);
        }

        public ref Worst this[int i] => ref _a[i];
    }

    [Fact]
    public void FreshSlot_hands_out_a_zeroed_slot_after_the_jit_tiers_up()
    {
        var list = new PooledList();
        for (int round = 0; round < Rounds; round++)
        {
            list.Count = 0;
            for (int i = 0; i < Slots; i++)
            {
                ref Worst full = ref list.Add();
                full.Left = 7; full.Behind = 9; full.Kind = 2;
            }
            list.Count = 0;
            for (int i = 0; i < Slots; i++) list.Add().First = default;
            for (int i = 0; i < Slots; i++)
                Assert.True(Clean(in list[i]), $"round {round} row {i}: left {list[i].Left} behind {list[i].Behind} kind {list[i].Kind}");
        }
    }

    [Fact]
    public void A_reused_ColdSlab_row_is_handed_out_zeroed_after_the_jit_tiers_up()
    {
        var slab = new ColdSlab<Worst>();
        for (int round = 0; round < Rounds; round++)
        {
            for (int i = 0; i < Slots; i++)
            {
                ref Worst full = ref slab.GetOrAdd(i);
                full.Left = 7; full.Behind = 9; full.Kind = 2;
            }
            for (int i = 0; i < Slots; i++) slab.Remove(i);
            for (int i = 0; i < Slots; i++)
            {
                ref Worst thin = ref slab.GetOrAdd(i);
                thin.First = default;
            }
            for (int i = 0; i < Slots; i++)
            {
                ref Worst w = ref slab.GetOrAdd(i);
                Assert.True(Clean(in w), $"round {round} node {i}: left {w.Left} behind {w.Behind} kind {w.Kind}");
            }
            for (int i = 0; i < Slots; i++) slab.Remove(i);
        }
    }

    [Fact]
    public void A_reused_SlabAllocator_slot_is_handed_out_zeroed_after_the_jit_tiers_up()
    {
        var slab = new SlabAllocator<Worst>(64);
        var handles = new Handle[Slots];
        for (int round = 0; round < Rounds; round++)
        {
            for (int i = 0; i < Slots; i++)
            {
                handles[i] = slab.Alloc();
                ref Worst full = ref slab.Get(handles[i]);
                full.Left = 7; full.Behind = 9; full.Kind = 2;
            }
            for (int i = 0; i < Slots; i++) slab.Free(handles[i]);
            for (int i = 0; i < Slots; i++)
            {
                handles[i] = slab.Alloc();
                slab.Get(handles[i]).First = default;
            }
            for (int i = 0; i < Slots; i++)
            {
                ref Worst w = ref slab.Get(handles[i]);
                Assert.True(Clean(in w), $"round {round} slot {handles[i].Index}: left {w.Left} behind {w.Behind} kind {w.Kind}");
            }
            for (int i = 0; i < Slots; i++) slab.Free(handles[i]);
        }
    }

    [Fact]
    public void A_reopened_gesture_arena_seat_carries_nothing_from_the_previous_contact()
    {
        var arena = new GestureArena();
        for (int round = 0; round < Rounds; round++)
        {
            for (uint p = 1; p <= GestureArena.MaxArenas; p++)
            {
                int slot = arena.OpenArena(p, openedUs: 1000 + p);
                arena.SetHeld(slot, true);
                arena.CloseArena(slot);
            }
            for (int s = 0; s < GestureArena.MaxArenas; s++) arena.CloseAndFree(s);
            for (uint p = 1; p <= GestureArena.MaxArenas; p++)
            {
                int slot = arena.OpenArena(100 + p);
                ref GestureArenaState a = ref arena.ArenaAt(slot);
                Assert.True(!a.Closed && !a.Held && a.OpenedUs == 0 && a.WinnerSlot == -1 && a.MemberLen == 0,
                    $"round {round} seat {slot}: closed {a.Closed} held {a.Held} opened {a.OpenedUs} winner {a.WinnerSlot}");
            }
            for (int s = 0; s < GestureArena.MaxArenas; s++) arena.CloseAndFree(s);
        }
    }

    [Fact]
    public void A_reopened_slice_row_carries_nothing_from_the_retired_slice()
    {
        var table = new SliceTable();
        var frame = new SliceFrame(0, 0, 0f, 0f, 1f);
        var bounds = new RectF(0f, 0f, 256f, 256f);
        int frameId = 0;
        for (int round = 0; round < Rounds; round++)
        {
            table.BeginFrame(++frameId);
            for (int i = 0; i < 8; i++)
            {
                int id = table.OpenSlice(10 + i, 1, SliceKind.Scroll, in frame, in bounds);
                ref SliceRow row = ref table.Row(id);
                row.DrawListStart = 11; row.DrawListLength = 12; row.SpanIndexStart = 13; row.SpanIndexCount = 14; row.StreamBase = 15;
            }
            table.EndFrame();
            table.BeginFrame(++frameId);
            table.EndFrame();                                    // nothing opened: every slice retires
            table.BeginFrame(++frameId);
            for (int i = 0; i < 8; i++)
            {
                int id = table.OpenSlice(500 + i, 3, SliceKind.Scroll, in frame, in bounds);
                ref SliceRow row = ref table.Row(id);
                Assert.True(row.DrawListStart == 0 && row.DrawListLength == 0 && row.SpanIndexStart == 0 && row.SpanIndexCount == 0
                    && row.StreamBase == 0,
                    $"round {round} slice {id}: dl {row.DrawListStart}+{row.DrawListLength} spans {row.SpanIndexStart}+{row.SpanIndexCount} base {row.StreamBase}");
            }
            table.EndFrame();
            table.BeginFrame(++frameId);
            table.EndFrame();
        }
    }
}
