using FluentGpu.Scroll;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Idle-power gate for <see cref="ScrollCommandPort"/>'s SetFrame dedup (scroll-v3-plan §4): layout posts
/// an idempotent SetFrame for EVERY arranged viewport, every frame, regardless of whether its geometry actually
/// changed. Reposting a byte-identical SetFrame for the same node must be a true no-op — nothing enqueued,
/// <see cref="ScrollCommandPort.Pending"/> unmoved — so a stable-geometry frame does not, by itself, look like
/// scroll work to a wake-reason gate reading <c>Port.Pending &gt; 0</c>.</summary>
public sealed class ScrollCommandPortSetFrameCoalesceTests
{
    private static ScrollFrameSpec Frame(float extent, float viewport)
        => new(0, extent, 300f, viewport, 300f, 1f, false, 0f, 0f, 0f, null);

    [Fact]
    public void IdenticalRepostedSetFrame_DoesNotEnqueue()
    {
        var port = new ScrollCommandPort();
        var spec = Frame(1000f, 200f);

        port.Post(ScrollInput.SetFrame(1, spec));
        Assert.Equal(1, port.Pending);

        // Layout re-arranges the same viewport with the exact same geometry — the common steady-state case.
        port.Post(ScrollInput.SetFrame(1, spec));
        port.Post(ScrollInput.SetFrame(1, spec));
        Assert.Equal(1, port.Pending);
    }

    [Fact]
    public void ChangedSetFrame_StillEnqueues()
    {
        var port = new ScrollCommandPort();
        port.Post(ScrollInput.SetFrame(1, Frame(1000f, 200f)));
        Assert.Equal(1, port.Pending);

        port.Post(ScrollInput.SetFrame(1, Frame(1000f, 250f)));  // viewport resized — a real change
        Assert.Equal(2, port.Pending);

        // Re-posting the (now current) 250f frame again is a no-op.
        port.Post(ScrollInput.SetFrame(1, Frame(1000f, 250f)));
        Assert.Equal(2, port.Pending);
    }

    [Fact]
    public void DifferentNodes_AreTrackedIndependently()
    {
        var port = new ScrollCommandPort();
        var spec = Frame(1000f, 200f);
        port.Post(ScrollInput.SetFrame(1, spec));
        port.Post(ScrollInput.SetFrame(2, spec));
        Assert.Equal(2, port.Pending);

        port.Post(ScrollInput.SetFrame(1, spec));
        port.Post(ScrollInput.SetFrame(2, spec));
        Assert.Equal(2, port.Pending);
    }

    [Fact]
    public void UnbindClearsTheCache_SoARebindAlwaysPostsItsFirstSetFrame()
    {
        var port = new ScrollCommandPort();
        var spec = Frame(1000f, 200f);

        port.Post(ScrollInput.Bind(1));
        port.Post(ScrollInput.SetFrame(1, spec));
        Assert.Equal(2, port.Pending);

        port.Post(ScrollInput.Unbind(1));
        Assert.Equal(3, port.Pending);

        // Same node index reused by a fresh Bind with IDENTICAL geometry to what was cached before Unbind — must
        // still land, since the kernel body was torn down and needs a real SetFrame to reinitialize.
        port.Post(ScrollInput.Bind(1));
        port.Post(ScrollInput.SetFrame(1, spec));
        Assert.Equal(5, port.Pending);
    }
}
