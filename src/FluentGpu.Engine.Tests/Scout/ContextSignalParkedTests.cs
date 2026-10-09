using FluentGpu.Hooks;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A bind reading a PROVIDED (non-ambient) context through <c>UseContextSignal</c> keeps the provider while its
/// KeepAlive page is parked (detached): it neither falls back to the context Default nor drops its subscription.</summary>
public sealed class ContextSignalParkedTests
{
    [Fact]
    public void Context_signal_bind_keeps_provider_while_parked()
    {
        var rt = new ReactiveRuntime();
        var units = new Context<string>("km");
        var provided = new Signal<object?>("mi");
        bool attached = true;   // false = the page root is detached: the anchor's parent walk no longer reaches the provider
        var c = new RenderContext
        {
            Runtime = rt,
            ResolveContextSignal = (_, ch) => attached && ReferenceEquals(ch, units) ? provided : null,
        };
        c.BeginRender();
        var sig = c.UseContextSignal(units);
        c.EndRender();

        string? seen = null; int runs = 0;
        var bind = new Effect(rt, () => { seen = sig.Value; runs++; });   // stands in for a Prop.Of(() => ... units.Value) bind
        Assert.Equal("mi", seen);

        attached = false;                // KeepAlive parks + detaches the page
        provided.Value = "nm";           // the user switches units elsewhere
        rt.Flush();
        Assert.Equal(2, runs);
        Assert.Equal("nm", seen);        // before the fix: "km" (the Default)
        Assert.Equal("nm", sig.Peek());

        provided.Value = "ft";           // a later change must still reach the bind
        rt.Flush();
        Assert.Equal(3, runs);           // before the fix: 2 (the bind lost its subscription)
        Assert.Equal("ft", seen);

        attached = true;                 // re-activated: attached resolution is used again
        provided.Value = "yd";
        rt.Flush();
        Assert.Equal("yd", seen);
        bind.Dispose();
    }
}
