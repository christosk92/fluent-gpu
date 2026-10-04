using System.Collections.Generic;
using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The refcount / adapter-LUID policy behind the process-wide D3D11 video device (<c>MfVideoDevice</c>): one device per
/// adapter shared by every clear engine, kept briefly after the last lease, replaced only on an adapter change or device
/// removal, destroyed only once nothing decodes on it. The real owner needs a GPU; the policy runs here over a fake device.</summary>
public sealed class MfVideoDeviceTests
{
    private sealed class FakeDevice
    {
        internal readonly long Key;
        internal bool Removed;
        internal bool Destroyed;
        internal FakeDevice(long key) { Key = key; }
    }

    private sealed class Rig
    {
        internal long Now;
        internal int Created;
        internal bool FailCreate;
        internal readonly List<FakeDevice> Devices = new();
        internal readonly List<int> SweepRequests = new();
        internal readonly SharedDeviceCache<FakeDevice> Cache;

        internal Rig(int lingerMs = 1000)
        {
            Cache = new SharedDeviceCache<FakeDevice>(
                create: key =>
                {
                    if (FailCreate) return null;
                    Created++;
                    var d = new FakeDevice(key);
                    Devices.Add(d);
                    return d;
                },
                destroy: d => { Assert.False(d.Destroyed, "device destroyed twice"); d.Destroyed = true; },
                isRemoved: d => d.Removed,
                clock: () => Now,
                lingerMs: lingerMs,
                scheduleSweep: ms => SweepRequests.Add(ms));
        }
    }

    [Fact]
    public void Leases_ForTheSameAdapter_ShareOneDevice()
    {
        var rig = new Rig();
        using var a = rig.Cache.Acquire(7)!;
        using var b = rig.Cache.Acquire(7)!;

        Assert.Same(a.Device, b.Device);
        Assert.Equal(1, rig.Created);
        Assert.Equal(2, rig.Cache.LeaseCount(7));
        Assert.Equal(1, rig.Cache.AliveDevices);
    }

    [Fact]
    public void LastRelease_KeepsTheDevice_ForTheNextEngine_ThenSweepDestroysIt()
    {
        var rig = new Rig(lingerMs: 1000);
        var first = rig.Cache.Acquire(7)!;
        FakeDevice device = first.Device;
        first.Dispose();

        Assert.False(device.Destroyed);
        Assert.Equal(new[] { 1000 }, rig.SweepRequests);

        // The warm-engine miss: a new engine reuses the lingering device instead of creating another.
        var second = rig.Cache.Acquire(7)!;
        Assert.Same(device, second.Device);
        Assert.Equal(1, rig.Created);
        second.Dispose();

        rig.Now = 999;
        rig.Cache.Sweep();
        Assert.False(device.Destroyed);
        Assert.Equal(1, rig.Cache.AliveDevices);

        rig.Now = 1000;
        rig.Cache.Sweep();
        Assert.True(device.Destroyed);
        Assert.Equal(0, rig.Cache.AliveDevices);
    }

    [Fact]
    public void Sweep_NeverDestroysALeasedDevice_AndRearmsForTheRemainingIdleOnes()
    {
        var rig = new Rig(lingerMs: 1000);
        using var held = rig.Cache.Acquire(1)!;
        rig.Cache.Acquire(2)!.Dispose();
        rig.Now = 400;
        rig.Cache.Acquire(3)!.Dispose();   // adapter 2 is idle and unleased, so asking for 3 retires it (see the adapter-change test)
        rig.SweepRequests.Clear();

        rig.Now = 1200;
        rig.Cache.Sweep();

        Assert.False(held.Device.Destroyed);
        Assert.Equal(1, rig.Cache.LeaseCount(1));
        // Adapter 3 went idle at t=400: 200 ms of its linger are left.
        Assert.Equal(new[] { 200 }, rig.SweepRequests);
    }

    [Fact]
    public void AdapterChange_DestroysUnleasedDevicesOfOtherAdapters_ButNotLeasedOnes()
    {
        var rig = new Rig();
        rig.Cache.Acquire(1)!.Dispose();            // idle on adapter 1
        using var busy = rig.Cache.Acquire(2)!;     // leased on adapter 2

        using var moved = rig.Cache.Acquire(3)!;    // the renderer moved to adapter 3

        Assert.True(rig.Devices[0].Destroyed);       // idle adapter-1 device: gone
        Assert.False(busy.Device.Destroyed);         // an engine still decodes on adapter 2: untouched
        Assert.NotSame(busy.Device, moved.Device);
        Assert.Equal(2, rig.Cache.AliveDevices);
    }

    [Fact]
    public void DeviceRemoved_ReportedByALease_IsReplacedOnTheNextAcquire_AndDiesWithItsLastLease()
    {
        var rig = new Rig();
        var a = rig.Cache.Acquire(7)!;
        var b = rig.Cache.Acquire(7)!;
        FakeDevice dead = a.Device;

        a.MarkRemoved();
        var c = rig.Cache.Acquire(7)!;

        Assert.NotSame(dead, c.Device);              // fresh device for the new engine
        Assert.Equal(2, rig.Created);
        Assert.False(dead.Destroyed);                // b still holds it
        a.Dispose();
        Assert.False(dead.Destroyed);
        b.Dispose();
        Assert.True(dead.Destroyed);                 // last lease gone: destroyed now, not before
        Assert.False(c.Device.Destroyed);
        c.Dispose();
    }

    [Fact]
    public void DeviceRemoved_FoundByTheNextAcquire_WithoutAnyLeaseReportingIt()
    {
        var rig = new Rig();
        var a = rig.Cache.Acquire(7)!;
        a.Device.Removed = true;                     // a TDR the holder has not polled yet

        var b = rig.Cache.Acquire(7)!;

        Assert.NotSame(a.Device, b.Device);
        Assert.Equal(2, rig.Created);
        a.Dispose();
        Assert.True(rig.Devices[0].Destroyed);
        b.Dispose();
    }

    [Fact]
    public void ReportingRemoval_OnAnIdleLingeringDevice_DoesNotDoubleDestroy()
    {
        var rig = new Rig();
        var a = rig.Cache.Acquire(7)!;
        a.MarkRemoved();
        a.Dispose();
        a.MarkRemoved();                             // after dispose: ignored
        a.Dispose();                                 // idempotent

        Assert.True(rig.Devices[0].Destroyed);
        rig.Now = 10_000;
        rig.Cache.Sweep();
        Assert.Equal(0, rig.Cache.AliveDevices);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndCountsOneLease()
    {
        var rig = new Rig();
        var a = rig.Cache.Acquire(7)!;
        using var b = rig.Cache.Acquire(7)!;

        a.Dispose();
        a.Dispose();

        Assert.Equal(1, rig.Cache.LeaseCount(7));
        Assert.False(b.Device.Destroyed);
    }

    [Fact]
    public void CreateFailure_YieldsNoLease_AndLeavesNoState()
    {
        var rig = new Rig { FailCreate = true };

        Assert.Null(rig.Cache.Acquire(7));
        Assert.Equal(0, rig.Cache.AliveDevices);
        Assert.Equal(0, rig.Cache.LeaseCount(7));

        rig.FailCreate = false;
        using var ok = rig.Cache.Acquire(7)!;
        Assert.Equal(1, rig.Created);
    }

    [Fact]
    public void LuidKey_PacksBothHalves_AndNeverCollidesWithDefaultAdapterKey()
    {
        Assert.Equal(0x0000_0001_0000_0002L, MfVideoDevice.LuidKey(1, 2u));
        Assert.NotEqual(MfVideoDevice.LuidKey(1, 2u), MfVideoDevice.LuidKey(2, 1u));
        Assert.Equal(unchecked((long)0xFFFF_FFFF_8000_0000UL), MfVideoDevice.LuidKey(-1, 0x8000_0000u));
        Assert.NotEqual(0L, MfVideoDevice.LuidKey(0, 1u));
    }
}
