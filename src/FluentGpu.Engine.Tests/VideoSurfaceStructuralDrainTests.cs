using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The registry's early (structural) drain and its deferred-commit form (F208 / F080): the render thread creates and binds a
/// surface BEFORE its present-slot wait, while placement moves and destroys stay coupled to the UI frame's present (the
/// two-clock tear lock), and a render turn's drains apply without committing so the host makes ONE device commit.
/// </summary>
public sealed class VideoSurfaceStructuralDrainTests
{
    private sealed class RecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public int Commits, Applies;
        public bool CanAttach = true;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface()
        {
            var id = new VideoSurfaceId(_next++);
            Calls.Add($"Create({id.Value})");
            return id;
        }

        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
        {
            Calls.Add($"Bind({id.Value},0x{dcompSurfaceHandle:X})");
            return true;
        }

        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add($"Place({id.Value})");
        public void SetVisible(VideoSurfaceId id, bool visible) => Calls.Add($"Visible({id.Value},{visible})");
        public void Destroy(VideoSurfaceId id) => Calls.Add($"Destroy({id.Value})");
        public void ApplyPending() => Applies++;
        public bool CanAttachSurfaces => CanAttach;
        public void Commit() => Commits++;

        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Any call is a failure: proves an early-out never reaches the presenter.</summary>
    private sealed class UntouchablePresenter : IVideoPresenter
    {
        public VideoSurfaceId CreateSurface() => throw new InvalidOperationException("CreateSurface");
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle) => throw new InvalidOperationException("Bind");
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => throw new InvalidOperationException("Place");
        public void SetVisible(VideoSurfaceId id, bool visible) => throw new InvalidOperationException("SetVisible");
        public void Destroy(VideoSurfaceId id) => throw new InvalidOperationException("Destroy");
        public void ApplyPending() => throw new InvalidOperationException("ApplyPending");
        public bool CanAttachSurfaces => throw new InvalidOperationException("CanAttachSurfaces");
        public void Commit() => throw new InvalidOperationException("Commit");
    }

    private static readonly RectF RectA = new(10f, 20f, 100f, 50f);
    private static readonly RectF RectB = new(30f, 40f, 120f, 60f);

    [Fact]
    public void DrainStructural_CreatesBindsAndPlacesANewSurface_ThenLeavesItDirtyForTheCoupledDrain()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, RectA);
        Assert.True(reg.HasStructuralWork);

        var p = new RecordingPresenter();
        reg.DrainStructural(p, 1f);
        Assert.Equal(1, p.Count("Create("));
        Assert.Equal(1, p.Count("Bind("));
        Assert.Equal(1, p.Count("Place("));      // a new surface is placed the instant it exists
        Assert.Equal(1, p.Commits);              // not deferred: committed at once
        Assert.False(reg.HasStructuralWork);     // nothing left to create or bind

        // The authoritative placement is still the coupled drain's (with the presented frame's own scale).
        reg.Drain(p, 1f);
        Assert.Equal(2, p.Count("Place("));
        Assert.Equal(1, p.Count("Create("));
        Assert.Equal(1, p.Count("Bind("));
    }

    [Fact]
    public void DrainStructural_NeverAppliesAReleasePending()
    {
        var reg = new VideoSurfaceRegistry();
        int first = reg.Acquire();
        reg.Bind(first, 0x10);
        reg.Place(first, RectA);
        var p = new RecordingPresenter();
        reg.Drain(p, 1f);

        reg.Release(first);                      // the old placement's visual must stay until the UI frame's present...
        int second = reg.Acquire();
        reg.Bind(second, 0x20);                  // ...even while a NEW surface needs its early create/bind
        reg.Place(second, RectB);
        reg.DrainStructural(p, 1f);

        Assert.Equal(0, p.Count("Destroy("));
        Assert.Equal(2, p.Count("Create("));

        reg.Drain(p, 1f);                        // the coupled drain owns the destroy
        Assert.Equal(1, p.Count("Destroy(1)"));
    }

    [Fact]
    public void DrainStructural_LeavesPlacementMovesAndAnExistingSurfacesRebindPlacementToTheCoupledDrain()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, RectA);
        var p = new RecordingPresenter();
        reg.Drain(p, 1f);
        int places = p.Count("Place(");

        reg.Place(token, RectB);                 // a Place-class move: nothing structural
        Assert.False(reg.HasStructuralWork);
        Assert.False(reg.DrainStructural(p, 1f));
        Assert.Equal(places, p.Count("Place("));

        reg.Bind(token, 0x20);                   // a new handle on the existing surface: bound early, not re-placed early
        Assert.True(reg.HasStructuralWork);
        reg.DrainStructural(p, 1f);
        Assert.Contains("Bind(1,0x20)", p.Calls);
        Assert.Equal(places, p.Count("Place("));
        Assert.Equal(1, p.Count("Create("));

        reg.Drain(p, 1f);                        // the coupled drain applies the pending move
        Assert.Equal(places + 1, p.Count("Place("));
    }

    [Fact]
    public void DrainStructural_IsOneBooleanRead_WhenNothingNeedsCreatingOrBinding()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Place(token, RectA);                 // no handle yet: nothing to create
        Assert.False(reg.HasStructuralWork);
        Assert.False(reg.DrainStructural(new UntouchablePresenter(), 1f));   // would throw on any presenter access
    }

    [Fact]
    public void DrainStructural_WaitsUntilThePresenterCanAttach()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, RectA);
        var p = new RecordingPresenter { CanAttach = false };   // the target's first Present has not bound its composition graph

        Assert.False(reg.DrainStructural(p, 1f));
        Assert.Empty(p.Calls);
        Assert.True(reg.HasStructuralWork);

        p.CanAttach = true;
        reg.DrainStructural(p, 1f);
        Assert.Equal(1, p.Count("Create("));
        Assert.False(reg.HasStructuralWork);
    }

    [Fact]
    public void DeferredDrain_AppliesWithoutCommitting_AndReadinessPublishesOnlyAfterPublishCommitted()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, RectA);
        var p = new RecordingPresenter();

        Assert.False(reg.Drain(p, 1f, deferCommit: true));   // no readiness edge yet: the surface is not composed
        Assert.Equal(1, p.Applies);
        Assert.Equal(0, p.Commits);
        reg.PumpPending(1f);
        Assert.False(reg.Bound(token).Peek());

        Assert.True(reg.PublishCommitted());                 // after the host's one device commit: the bind edge
        reg.PumpPending(1f);
        Assert.True(reg.Bound(token).Peek());
        Assert.False(reg.PublishCommitted());                // nothing applied since
    }

    [Fact]
    public void DeferredStructuralDrain_AlsoWaitsForPublishCommitted()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, RectA);
        var p = new RecordingPresenter();

        reg.DrainStructural(p, 1f, deferCommit: true);
        Assert.Equal(1, p.Applies);
        Assert.Equal(0, p.Commits);
        reg.PumpPending(1f);
        Assert.False(reg.Bound(token).Peek());
        Assert.True(reg.PublishCommitted());
        reg.PumpPending(1f);
        Assert.True(reg.Bound(token).Peek());
    }

    [Fact]
    public void ADrainWhoseWindowStoodDown_AppliesOnlyReleases_AndKeepsEveryPlacementDirty()
    {
        var reg = new VideoSurfaceRegistry();
        int kept = reg.Acquire();
        reg.Bind(kept, 0x10);
        reg.Place(kept, RectA);
        int gone = reg.Acquire();
        reg.Bind(gone, 0x20);
        reg.Place(gone, RectA);
        var p = new RecordingPresenter();
        reg.Drain(p, 1f);
        int places = p.Count("Place(");

        reg.Place(kept, RectB);                              // a move for a hole that will never reach the glass
        reg.Release(gone);                                   // a hidden window must still free its slot
        reg.Drain(p, 1f, placement: false);

        Assert.Equal(1, p.Count("Destroy("));
        Assert.Equal(places, p.Count("Place("));
        Assert.Equal(gone, reg.Acquire());                   // the released slot is free again

        reg.Drain(p, 1f);                                    // the next real present applies the held move
        Assert.Equal(places + 1, p.Count("Place("));
    }

    [Fact]
    public void Bind_RaisesTheStructuralWake_OnlyForANewNonZeroHandle()
    {
        var reg = new VideoSurfaceRegistry();
        int wakes = 0;
        reg.StructuralWake = () => wakes++;
        int token = reg.Acquire();

        reg.Bind(token, 0x10);
        Assert.Equal(1, wakes);
        reg.Bind(token, 0x10);                               // value-gated: nothing changed
        Assert.Equal(1, wakes);
        reg.Bind(token, 0x20);
        Assert.Equal(2, wakes);
        reg.Bind(token, 0);                                  // clearing a handle creates nothing
        Assert.Equal(2, wakes);
        reg.Bind(token, 0x30);
        Assert.Equal(3, wakes);
        reg.Rebind(token);                                   // a producer re-raising an unchanged handle
        Assert.Equal(4, wakes);
        reg.Place(token, RectA);                             // placement is not structural
        Assert.Equal(4, wakes);
    }

    [Fact]
    public void APresenterSwap_RaisesTheStructuralHint_SoTheEarlyDrainRebuildsTheSurface()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, RectA);
        var a = new RecordingPresenter();
        reg.Drain(a, 1f);
        Assert.False(reg.HasStructuralWork);

        var b = new RecordingPresenter();                    // device recovery: a brand-new presenter, no new intent
        reg.Drain(b, 1f, placement: false);                  // any drain notices the swap
        Assert.True(reg.HasStructuralWork);
        reg.DrainStructural(b, 1f);
        Assert.Equal(1, b.Count("Create("));
        Assert.Contains("Bind(1,0x10)", b.Calls);
        Assert.False(reg.HasStructuralWork);
    }
}
