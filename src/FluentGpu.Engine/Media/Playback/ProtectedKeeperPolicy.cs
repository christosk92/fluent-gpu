using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>The <c>--fg protected-keeper=</c> arm (<see cref="ProtectedKeeperPolicy.Switch"/>).</summary>
public enum ProtectedKeeperSwitch : byte
{
    /// <summary>The default lifetime: armed at launch on an adapter a previous run negotiated hardware PlayReady (SL3000) on
    /// (<see cref="ProtectedKeeperPolicy.RememberedAdapter"/>), else armed at the first SL3000 negotiation of this run.</summary>
    Auto,
    /// <summary>No keeper, ever (every protected arrival and departure pays DWM's protected-composition switch, as before).</summary>
    Off,
    /// <summary>Armed with the main window's first present, whatever was remembered.</summary>
    Launch,
    /// <summary>Armed only by this run's first SL3000 negotiation, whatever was remembered.</summary>
    Runtime,
}

/// <summary>
/// When the protected composition keeper is wanted. The keeper itself (<c>ProtectedCompositionKeeper</c> and its owner in
/// <c>D3D12Device.VideoCompositor.cs</c>, FluentGpu.Windows) is one tiny hardware-protected swap chain kept in the main window's
/// video tree for the life of the process, so Windows' desktop compositor enters hardware-protected composition ONCE instead of at
/// every protected video's arrival and departure: that switch rebuilds DWM's own swap-chain buffers and froze the whole desktop for
/// 200-680 ms each time (measured 2026-11-12, with and without DRM).
/// <para><b>Who writes what.</b> The command line sets <see cref="Switch"/>; the app sets <see cref="RememberedAdapter"/> from its
/// settings before the window exists and persists what <see cref="Remember"/> hands it; the protected runtime calls
/// <see cref="NoteHardwareDrm"/> when the CDM negotiated SL3000; the primary host installs <see cref="Wake"/>. The render thread reads
/// <see cref="Wanted"/> once per turn: two volatile reads, no allocation.</para>
/// </summary>
public static class ProtectedKeeperPolicy
{
    /// <summary>The A/B arm (<c>--fg protected-keeper=off|launch|runtime</c>). Set before the window exists.</summary>
    public static ProtectedKeeperSwitch Switch;

    private static long s_remembered;
    private static int s_hardwareDrm;

    /// <summary>The adapter (<see cref="AdapterKey"/>) a previous run negotiated SL3000 on, 0 = none. The app sets it from its settings
    /// before the window exists: in the default lifetime it arms the keeper at launch on that adapter.</summary>
    public static ulong RememberedAdapter
    {
        get => (ulong)Volatile.Read(ref s_remembered);
        set => Volatile.Write(ref s_remembered, (long)value);
    }

    /// <summary>Engine to app, any thread (the render thread): this run negotiated SL3000 on the adapter with this key, persist it so
    /// the next launch arms the keeper at once. Raised at most once per run, and only when the key differs from
    /// <see cref="RememberedAdapter"/>. The handler must not block (it runs inside a render turn).</summary>
    public static Action<ulong>? Remember;

    /// <summary>Wakes the main window's render loop (the primary host installs it): the keeper attaches on a render turn, and an arming
    /// that comes from another thread (the CDM's negotiation, a keeper built on the pool) must not wait for the next UI frame.</summary>
    public static Action? Wake;

    /// <summary>A PlayReady CDM negotiated hardware security (SL3000) in this run. Any thread (the native notifier).</summary>
    public static bool HardwareDrmSeen => Volatile.Read(ref s_hardwareDrm) != 0;

    /// <summary>The protected runtime's CDM came up at SL3000 (<c>[pr.phase] cdm.create.end sl=3000</c>): arm the keeper now, before
    /// the first protected frame. Any thread; only the first call does anything.</summary>
    public static void NoteHardwareDrm()
    {
        if (Interlocked.Exchange(ref s_hardwareDrm, 1) != 0) return;
        try { Wake?.Invoke(); } catch { /* a wake that failed only delays the keeper to the next render turn */ }
    }

    /// <summary>The persisted identity of an adapter: PCI vendor id in the high half, device id in the low half (a LUID changes at every
    /// boot; these do not).</summary>
    public static ulong AdapterKey(uint vendorId, uint deviceId) => ((ulong)vendorId << 32) | deviceId;

    /// <summary>Is the keeper wanted now on the adapter <paramref name="adapterKey"/> (0 = not known yet)? Render thread, every turn.</summary>
    public static bool Wanted(ulong adapterKey) => Switch switch
    {
        ProtectedKeeperSwitch.Off => false,
        ProtectedKeeperSwitch.Launch => true,
        ProtectedKeeperSwitch.Runtime => HardwareDrmSeen,
        _ => HardwareDrmSeen || (adapterKey != 0 && adapterKey == RememberedAdapter),
    };

    /// <summary>Why the keeper is wanted, for its <c>keeper.create</c> stamp.</summary>
    public static string WhyWanted(ulong adapterKey) => Switch switch
    {
        ProtectedKeeperSwitch.Launch => "switch=launch",
        ProtectedKeeperSwitch.Runtime => "switch=runtime sl3000",
        _ => HardwareDrmSeen ? "sl3000" : "remembered",
    };
}
