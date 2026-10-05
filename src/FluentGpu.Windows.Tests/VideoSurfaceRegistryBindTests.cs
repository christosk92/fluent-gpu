using System.Linq;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The clear session's half of the video-slot contract (L2-02): a re-raised UNCHANGED handle after a presentation epoch
/// move is wrapped again, a failed presenter bind is retried until it lands, and the session never overrides the
/// element's visibility decision (it only expresses readiness; <c>MediaPlayerElement.PumpNow</c> writes visibility last).
/// </summary>
public sealed class VideoSurfaceRegistryBindTests
{
    private static readonly RectF Rect = new(0, 0, 320, 180);

    private static (MfMediaSession session, FakeVideoEngine engine) NewSession()
    {
        var core = new MediaPlayerCore();
        var engine = new FakeVideoEngine();
        var session = new MfMediaSession(engine, 0, new MediaOpenOptions { StartPaused = true });
        session.ConnectSignals(new MediaSignalSink(core));
        return (session, engine);
    }

    private static VideoBinding NewBinding(out VideoSurfaceRegistry registry)
    {
        registry = new VideoSurfaceRegistry();
        return new VideoBinding(registry, registry.Acquire());   // internal ctor (Engine InternalsVisibleTo the test assembly)
    }

    private static int BindCalls(FakeVideoPresenter p) => p.Calls.Count(c => c.StartsWith("Bind(", System.StringComparison.Ordinal));

    [Fact]
    public void EpochMove_WithTheSameHandleValue_RebindsThePresenter()
    {
        var (s, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xAAAA;
        var presenter = new FakeVideoPresenter();

        s.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, 1f);
        Assert.Equal(1, BindCalls(presenter));

        // RESOURCELOST: MF rebuilt its swap chain and handed back the same handle value. The registry's value gate used
        // to drop that, leaving the visual on the dead surface.
        eng.RaiseFormatChange();
        s.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, 1f);
        Assert.Equal(2, BindCalls(presenter));
    }

    [Fact]
    public void FailedPresenterBind_IsRetriedByLaterDrains_WithoutAnyNewHandle()
    {
        var (s, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xBEEF;
        var presenter = new FakeVideoPresenter { FailBinds = 1 };

        s.PumpVideo(binding, Rect, 1f);
        for (int i = 0; i < 4; i++) registry.Drain(presenter, 1f);   // the pump bound ONCE; only the drains retry

        Assert.Equal(1, presenter.Calls.Count(c => c.StartsWith("BindFail(", System.StringComparison.Ordinal)));
        Assert.Equal(1, BindCalls(presenter));
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);
        Assert.True(registry.HasLiveSurface);
    }

    [Fact]
    public void SessionPump_NeverOverridesTheElementsHide()
    {
        var (s, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xCAFE;
        var presenter = new FakeVideoPresenter();

        binding.SetVisible(false);                  // the element's decision for an inactive (parked) player
        s.PumpVideo(binding, Rect, 1f);             // ...survives the session's pump (it used to write SetVisible(true))
        registry.Drain(presenter, 1f);

        Assert.False(presenter.LastVisible);
        Assert.False(registry.HasLiveSurface);      // hidden ⇒ not live on screen
        Assert.Equal((nuint)0xCAFE, presenter.LastBoundHandle);
    }
}
