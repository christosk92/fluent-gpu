using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Media.Windows;
using TerraFX.Interop.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The hand-rolled <c>IMFMediaEngineNotify</c> CCW's lifetime (com-interop.md §4.3): a real COM refcount whose
/// LAST release frees the struct and the owner's <see cref="GCHandle"/> (Media Foundation's final release can land on
/// one of its workers after the engine's own Shutdown), and a shutdown gate that stops <c>EventNotify</c> reaching a
/// disposing engine. Everything is driven through the vtable exactly as MF would call it; the owner is a real
/// <see cref="VideoMediaEngine"/> that is never started (no thread, no COM), whose command-queue wake is the observable
/// proof that an event reached it.</summary>
public sealed unsafe class MediaEngineNotifyCcwTests
{
    private static uint AddRef(MediaEngineNotifyCcw* p)
        => ((delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint>)p->Vtbl[1])(p);

    private static uint Release(MediaEngineNotifyCcw* p)
        => ((delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint>)p->Vtbl[2])(p);

    private static int QueryInterface(MediaEngineNotifyCcw* p, Guid* riid, void** ppv)
        => ((delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, Guid*, void**, int>)p->Vtbl[0])(p, riid, ppv);

    private static int EventNotify(MediaEngineNotifyCcw* p, MF_MEDIA_ENGINE_EVENT ev)
        => ((delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint, nuint, uint, int>)p->Vtbl[3])(p, (uint)ev, 0, 0);

    private static MediaEngineNotifyCcw* NewCcw(VideoMediaEngine owner)
        => MediaEngineNotifyCcw.Create(GCHandle.ToIntPtr(GCHandle.Alloc(owner)));

    [Fact]
    public void RefCount_FollowsComRules_AndTheLastReleaseFreesTheStruct()
    {
        int before = MediaEngineNotifyCcw.LiveInstances;
        var owner = new VideoMediaEngine();
        MediaEngineNotifyCcw* ccw = NewCcw(owner);
        Assert.Equal(before + 1, MediaEngineNotifyCcw.LiveInstances);

        // MF takes its own reference (SetUnknown / CreateInstance), and QueryInterface hands out another.
        Assert.Equal(2u, AddRef(ccw));
        Guid iunknown = new("00000000-0000-0000-C000-000000000046");
        void* unk = null;
        Assert.Equal(0, QueryInterface(ccw, &iunknown, &unk));
        Assert.True(unk == ccw);
        Guid other = new("12345678-0000-0000-C000-000000000046");
        void* none = null;
        Assert.NotEqual(0, QueryInterface(ccw, &other, &none));
        Assert.True(none == null);

        // The engine's own reference is dropped first (DisposeCom): nothing is freed while MF still holds the callback.
        Assert.Equal(2u, MediaEngineNotifyCcw.ReleaseRef(ccw));
        Assert.Equal(before + 1, MediaEngineNotifyCcw.LiveInstances);
        Assert.Equal(1u, Release(ccw));
        Assert.Equal(before + 1, MediaEngineNotifyCcw.LiveInstances);

        // MF's final release — through the vtable, as it calls it — is the one that frees.
        Assert.Equal(0u, Release(ccw));
        Assert.Equal(before, MediaEngineNotifyCcw.LiveInstances);
        owner.Dispose();
    }

    [Fact]
    public void LastRelease_FreesTheOwnersGcHandle_NotBefore()
    {
        (nint ccwAddress, WeakReference owner) = NewCcwOverTemporaryOwner();
        var ccw = (MediaEngineNotifyCcw*)ccwAddress;
        AddRef(ccw);   // MF's reference

        MediaEngineNotifyCcw.ReleaseRef(ccw);   // the engine let go; MF still holds the callback
        CollectGarbage();
        Assert.True(owner.IsAlive);             // the CCW's GCHandle still roots the owner

        Release(ccw);                           // MF's final release
        CollectGarbage();
        Assert.False(owner.IsAlive);            // the handle went with the struct
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (nint, WeakReference) NewCcwOverTemporaryOwner()
    {
        var owner = new object();
        MediaEngineNotifyCcw* ccw = MediaEngineNotifyCcw.Create(GCHandle.ToIntPtr(GCHandle.Alloc(owner)));
        return ((nint)ccw, new WeakReference(owner));
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void EventNotify_ReachesTheOwner_UntilShutdown_AndStaysSafeAfterIt()
    {
        var owner = new VideoMediaEngine();
        int wakes = 0;
        owner.Commands.Wake = () => wakes++;
        MediaEngineNotifyCcw* ccw = NewCcw(owner);
        AddRef(ccw);   // MF's reference

        Assert.Equal(0, EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA));
        Assert.Equal(1, wakes);

        MediaEngineNotifyCcw.Shutdown(ccw);     // DisposeCom: stop delivering BEFORE the engine goes away
        owner.Commands.BeginDrain();            // re-open the coalescing gate so a second delivery would be visible
        Assert.Equal(0, EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_CANPLAY));
        Assert.Equal(1, wakes);                 // dropped at the gate: the owner was never called

        // The engine drops its reference, and a late event from an MF worker still lands on live memory (MF holds a reference).
        MediaEngineNotifyCcw.ReleaseRef(ccw);
        Assert.Equal(0, EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PLAYING));
        Assert.Equal(1, wakes);
        Assert.Equal(0u, Release(ccw));
        owner.Dispose();
    }

    [Fact]
    public void StallEvents_AreIgnoredUntilTheSourceHasHadEnoughData()
    {
        var owner = new VideoMediaEngine();
        int wakes = 0;
        owner.Commands.Wake = () => wakes++;
        MediaEngineNotifyCcw* ccw = NewCcw(owner);

        // WAITING / STALLED while the source is still loading are the normal opening path: no refresh is requested for them.
        EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_WAITING);
        owner.Commands.BeginDrain();
        EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_STALLED);
        owner.Commands.BeginDrain();
        Assert.Equal(0, wakes);

        // CANPLAY: enough data once. From here a WAITING is a real starvation and asks for an out-of-cadence publish.
        EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_CANPLAY);
        owner.Commands.BeginDrain();
        Assert.Equal(1, wakes);
        EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_WAITING);
        owner.Commands.BeginDrain();
        Assert.Equal(2, wakes);
        EventNotify(ccw, MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_STALLED);
        Assert.Equal(3, wakes);

        Assert.Equal(0u, MediaEngineNotifyCcw.ReleaseRef(ccw));
        owner.Dispose();
    }
}
