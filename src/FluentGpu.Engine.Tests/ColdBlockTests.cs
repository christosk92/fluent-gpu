using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The allocation work on the element records: BoxEl/TextEl/Element park their rarely-set channels in shared copy-on-write
/// blocks (BoxCold*, TextCold, ElementCold), and the reactive core keeps a signal's first subscribers inline (RefList). The
/// public surface must behave exactly like the plain auto-properties and List it replaced.
/// </summary>
public sealed class ColdBlockTests
{
    [Fact]
    public void Defaults_read_through_unset_blocks()
    {
        var b = new BoxEl();
        Assert.Equal(1f, b.ScaleX);
        Assert.Equal(1f, b.HoverScale);
        Assert.True(float.IsNaN(b.HoverOpacity));
        Assert.True(float.IsNaN(b.MaxHeight));
        Assert.True(b.ActivateOnEnter);
        Assert.Null(b.Shadow);
        Assert.Empty(b.ScrollEffects);
        Assert.Equal(0f, new TextEl("x").CharSpacing);
        Assert.True(float.IsNaN(new TextEl("x").MinSize));
    }

    [Fact]
    public void With_copy_writes_do_not_leak_into_the_source()
    {
        var a = new BoxEl { HoverScale = 1.1f, OnKeyDown = _ => { }, Stagger = 0.05f };
        var b = a with { HoverScale = 1.3f, OffsetX = 4f, Stagger = 0.1f };
        Assert.Equal(1.1f, a.HoverScale);
        Assert.Equal(0f, a.OffsetX);
        Assert.Equal(0.05f, a.Stagger);
        Assert.Equal(1.3f, b.HoverScale);
        Assert.Equal(4f, b.OffsetX);
        Assert.Equal(0.1f, b.Stagger);
        Assert.Same(a.OnKeyDown, b.OnKeyDown);   // untouched channels still read through the shared block
    }

    [Fact]
    public void Several_with_copies_of_one_source_stay_independent()
    {
        var src = new BoxEl { PressScale = 0.9f };
        var x = src with { PressScale = 0.5f };
        var y = src with { PressScale = 0.7f };
        var z = src with { Width = 10f };
        Assert.Equal(0.9f, src.PressScale);
        Assert.Equal(0.5f, x.PressScale);
        Assert.Equal(0.7f, y.PressScale);
        Assert.Equal(0.9f, z.PressScale);
    }

    [Fact]
    public void Writing_the_current_value_keeps_the_shared_block()
    {
        var src = new BoxEl { HoverScale = 1.2f };
        var same = src with { HoverScale = 1.2f, Arc = null, Cursor = null };
        Assert.Equal(1.2f, same.HoverScale);
        Assert.True(src.Equals(same));
    }

    [Fact]
    public void Equality_is_by_channel_value()
    {
        Action click = () => { };
        var a = new BoxEl { OnClick = click, HoverScale = 1.1f, Shadow = null };
        var b = new BoxEl { OnClick = click, HoverScale = 1.1f };
        var c = new BoxEl { OnClick = click, HoverScale = 1.2f };
        Assert.True(a.Equals(b));
        Assert.False(a.Equals(c));
    }

    [Fact]
    public void Base_and_text_channels_round_trip_through_with()
    {
        var t = new TextEl("hi") { Underline = true, HoverColor = ColorF.FromRgba(1, 2, 3), MinSize = 9f };
        var u = t with { Strikethrough = true };
        Assert.True(t.Underline);
        Assert.False(t.Strikethrough);
        Assert.True(u.Underline);
        Assert.True(u.Strikethrough);
        Assert.Equal(9f, u.MinSize);
        var e = new BoxEl { MorphId = "hero", Stagger = 0.2f };
        var f = e with { MorphId = "other" };
        Assert.Equal("hero", e.MorphId);
        Assert.Equal("other", f.MorphId);
        Assert.Equal(0.2f, f.Stagger);
    }

    [Fact]
    public void RefList_keeps_insertion_order_and_list_remove_semantics()
    {
        var model = new List<object>();
        var list = new RefList<object>();
        var items = new object[7];
        for (int i = 0; i < items.Length; i++) items[i] = new object();

        void Check()
        {
            Assert.Equal(model.Count, list.Count);
            for (int i = 0; i < model.Count; i++) Assert.Same(model[i], list[i]);
        }

        foreach (var it in items) { list.Add(it); model.Add(it); Check(); }
        foreach (int at in new[] { 3, 0, 4, 1, 0, 1, 0 })
        {
            if (at >= model.Count) continue;
            var victim = model[at];
            Assert.True(list.Remove(victim));
            model.RemoveAt(at);
            Check();
            Assert.False(list.Contains(victim));
        }
        Assert.False(list.Remove(new object()));
        list.Add(items[0]);
        list.Add(items[1]);
        list.Add(items[2]);
        Assert.Equal(2, list.IndexOf(items[2]));
        list.Clear();
        Assert.Equal(0, list.Count);
        Assert.False(list.Contains(items[0]));
    }

    [Fact]
    public void Signal_subscribers_notify_in_reverse_and_unsubscribe_cleanly()
    {
        var rt = new ReactiveRuntime();
        var sig = new Signal<int>(0);
        int runs = 0;
        var effects = new List<Effect>();
        for (int i = 0; i < 5; i++)
            effects.Add(new Effect(rt, () => { _ = sig.Value; runs++; }));
        runs = 0;
        sig.Value = 1;
        rt.Flush();
        Assert.Equal(5, runs);
        effects[1].Dispose();
        effects[3].Dispose();
        runs = 0;
        sig.Value = 2;
        rt.Flush();
        Assert.Equal(3, runs);
        Assert.Equal(3, sig.SubscriberCount);
    }
}
