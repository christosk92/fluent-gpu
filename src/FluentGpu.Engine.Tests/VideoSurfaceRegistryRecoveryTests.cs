using System.Collections.Generic;
using System.Linq;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The registry's render-side recovery contract (L2-02, F075/F076/F130): a presenter swap (device recovery) rebuilds
/// every live surface with no new intent, a failed bind is never recorded as bound and retries with backoff, a forced
/// re-bind wraps an UNCHANGED handle again, and each slot reports its OWN bound readiness to the UI thread.
/// </summary>
public sealed class VideoSurfaceRegistryRecoveryTests
{
    private sealed class RecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public int FailBinds;       // the next N BindSurfaceHandle calls report failure
        public RectF LastPlace;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface()
        {
            var id = new VideoSurfaceId(_next++);
            Calls.Add($"Create({id.Value})");
            return id;
        }

        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
        {
            if (FailBinds > 0)
            {
                FailBinds--;
                Calls.Add($"BindFail({id.Value},0x{dcompSurfaceHandle:X})");
                return false;
            }
            Calls.Add($"Bind({id.Value},0x{dcompSurfaceHandle:X})");
            return true;
        }

        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z)
        {
            LastPlace = deviceRect;
            Calls.Add($"Place({id.Value})");
        }

        public void SetVisible(VideoSurfaceId id, bool visible) => Calls.Add($"Visible({id.Value},{visible})");
        public void Destroy(VideoSurfaceId id) => Calls.Add($"Destroy({id.Value})");
        public void Commit() { }

        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, System.StringComparison.Ordinal));
    }

    private static readonly RectF Rect = new(10f, 20f, 100f, 50f);

    [Fact]
    public void PresenterSwap_RebuildsLiveSurface_WithNoNewIntent()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Bind(token, 0x1234);
        reg.Place(token, Rect);
        var a = new RecordingPresenter();
        reg.Drain(a, 1f);
        Assert.True(reg.HasLiveSurface);
        int aCalls = a.Calls.Count;

        // Device recovery: a brand-new presenter, nothing dirty. The old early-out left the video black forever.
        var b = new RecordingPresenter();
        reg.Drain(b, 1f);

        Assert.Equal(1, b.Count("Create("));
        Assert.Contains("Bind(1,0x1234)", b.Calls);
        Assert.Equal(1, b.Count("Place("));
        Assert.Equal(Rect, b.LastPlace);
        Assert.True(reg.HasLiveSurface);
        Assert.Equal(aCalls, a.Calls.Count);   // the old presenter is never touched again
    }

    [Fact]
    public void PresenterSwap_FreesReleasePendingEntry_WithoutDestroy()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, Rect);
        var a = new RecordingPresenter();
        reg.Drain(a, 1f);

        reg.Release(token);
        var b = new RecordingPresenter();
        reg.Drain(b, 1f);

        Assert.Empty(b.Calls);                       // its surface died with the old presenter: nothing to Destroy
        Assert.False(reg.HasLiveSurface);
        Assert.Equal(token, reg.Acquire());          // and the slot is free again
    }

    [Fact]
    public void Drain_ReturnsTrue_OnlyWhenTheBoundReadinessChanged()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        var p = new RecordingPresenter();
        reg.Bind(token, 0x10);
        reg.Place(token, Rect);
        Assert.True(reg.Drain(p, 1f));               // the bind edge: the host must wake the UI loop
        Assert.False(reg.Drain(p, 1f));              // nothing changed
        reg.Place(token, new RectF(1f, 1f, 50f, 50f));
        Assert.False(reg.Drain(p, 1f));              // a re-place alone does not change readiness
    }

    [Fact]
    public void Rebind_WithUnchangedHandle_BindsAgain_AndPlainBindStaysGated()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        var p = new RecordingPresenter();
        reg.Bind(token, 0x10);
        reg.Place(token, Rect);
        reg.Drain(p, 1f);
        Assert.Equal(1, p.Count("Bind("));

        reg.Bind(token, 0x10);                       // value-gated: dropped
        reg.Drain(p, 1f);
        Assert.Equal(1, p.Count("Bind("));

        reg.Bind(token, 0x10, force: true);          // the producer re-raised the same handle for a rebuilt surface
        reg.Drain(p, 1f);
        Assert.Equal(2, p.Count("Bind("));

        reg.Rebind(token);
        reg.Drain(p, 1f);
        Assert.Equal(3, p.Count("Bind("));
    }

    [Fact]
    public void FailedBind_IsNotRecordedAsBound_AndRetriesWithBackoff()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        var p = new RecordingPresenter { FailBinds = 1 };
        reg.Bind(token, 0x77);
        reg.Place(token, Rect);

        reg.Drain(p, 1f);                            // drain 1: the bind fails
        Assert.Equal(1, p.Count("BindFail("));
        Assert.False(reg.HasLiveSurface);            // not bound ⇒ not live

        reg.Drain(p, 1f);                            // drain 2: inside the backoff window, no attempt
        Assert.Equal(1, p.Count("BindFail("));
        Assert.Equal(0, p.Count("Bind("));

        reg.Drain(p, 1f);                            // drain 3: retried, now succeeds
        Assert.Equal(1, p.Count("Bind("));
        Assert.True(reg.HasLiveSurface);
    }

    [Fact]
    public void Bound_FlipsOnlyAfterTheSlotsOwnSurfaceIsBound_ViaUiThreadSync()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        Assert.False(reg.Bound(token).Peek());

        reg.Bind(token, 0x42);
        reg.Place(token, Rect);
        Assert.False(reg.Bound(token).Peek());       // intents alone are not readiness

        var p = new RecordingPresenter();
        reg.Drain(p, 1f);
        Assert.False(reg.Bound(token).Peek());       // the render thread never writes the signal itself
        Assert.True(reg.HasPendingPumps);            // ...it publishes an edge the UI thread folds in

        reg.PumpPending(1f);
        Assert.True(reg.Bound(token).Peek());
        Assert.False(reg.HasPendingPumps);
    }

    [Fact]
    public void Bound_DropsWhenPresenterIsReplacedAndRebindFails_ThenRecovers()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        reg.Bind(token, 0x42);
        reg.Place(token, Rect);
        reg.Drain(new RecordingPresenter(), 1f);
        reg.PumpPending(1f);
        Assert.True(reg.Bound(token).Peek());

        var b = new RecordingPresenter { FailBinds = 1 };
        reg.Drain(b, 1f);                            // swap: the rebind fails, so the slot is no longer bound
        reg.PumpPending(1f);
        Assert.False(reg.Bound(token).Peek());

        reg.Drain(b, 1f);                            // backoff
        reg.Drain(b, 1f);                            // retry succeeds
        reg.PumpPending(1f);
        Assert.True(reg.Bound(token).Peek());
    }

    [Fact]
    public void Bound_IsAlwaysTrue_WithoutAPresenterToWaitFor()
    {
        var reg = new VideoSurfaceRegistry();        // RequireSlotBinding off: the headless seam
        int token = reg.Acquire();
        Assert.True(reg.Bound(token).Peek());
        Assert.True(new VideoBinding(reg, token).Bound.Peek());
        Assert.True(default(VideoBinding).Bound.Peek());
    }
}
