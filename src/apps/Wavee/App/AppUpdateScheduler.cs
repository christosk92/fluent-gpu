using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The background cadence behind <see cref="IAppUpdateService"/>: one check 30 s after launch (late enough that it
/// never competes with the cold-start frame budget or the session bootstrap), then one every 24 h for as long as the
/// process lives. Idempotent — a second <see cref="Start"/> is ignored, so a re-activated bridge cannot stack timers.
/// <para>
/// Nothing here decides anything: it only calls <see cref="IAppUpdateService.CheckAsync"/>, which owns the state
/// machine and swallows its own failures. Any escape is logged and the loop keeps its cadence — a laptop that is
/// offline all week must still check on the day it comes back.
/// </para>
/// </summary>
static class AppUpdateScheduler
{
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    /// <summary>A restart within this window of the last successful check skips the launch check — restarting Wavee
    /// five times in a row must not mean five feed hits.</summary>
    const long LaunchCheckCooldownMs = 60 * 60 * 1000;
    static int s_started;

    public static void Start(IAppUpdateService svc, IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(settings);
        if (Interlocked.Exchange(ref s_started, 1) != 0) return;
        _ = Task.Run(() => RunAsync(svc, settings));
    }

    static async Task RunAsync(IAppUpdateService svc, IAppSettings settings)
    {
        var log = WaveeLog.Instance;
        try { await Task.Delay(FirstDelay).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        long lastChecked = settings.Get(WaveeSettings.UpdateLastCheckedMs);
        long sinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastChecked;
        if (lastChecked <= 0 || sinceMs < 0 || sinceMs >= LaunchCheckCooldownMs)
        {
            try { await svc.CheckAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.Warn("update", "initial update check failed", ex); }
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try { await svc.CheckAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { log.Warn("update", "periodic update check failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
