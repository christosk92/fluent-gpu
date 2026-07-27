using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>The ONE video-source resolution point behind <c>PlaybackBridge.ResolveVideoSource</c>: a tiered walk that
/// answers "what video, if any, should this playable show?".
/// <list type="number">
/// <item>the user's attached local file — it ALWAYS wins, including over a source's own official video;</item>
/// <item>the SOURCE's own video resolver (Spotify's manifest → a playable <see cref="PopOutVideoSource"/>);</item>
/// <item>null → the controller's existing audio fallback, which is tier 3 and lives there.</item>
/// </list>
/// The whole of tier 1 is the pure, engine-free <see cref="VideoOverrideService.Decide"/> — this class is only the shell
/// that maps a decision onto a <see cref="PopOutVideoSource"/> and does the branch's logging/notification. With no
/// override service attached the walk is exactly the single source tier it replaced (the feature's kill switch).</summary>
public sealed class CompositeVideoResolver
{
    readonly Func<string, CancellationToken, Task<PopOutVideoSource?>> _sourceTier;
    readonly VideoOverrideService? _overrides;

    public CompositeVideoResolver(
        Func<string, CancellationToken, Task<PopOutVideoSource?>> sourceTier,
        VideoOverrideService? overrides = null)
    {
        _sourceTier = sourceTier ?? throw new ArgumentNullException(nameof(sourceTier));
        _overrides = overrides;
    }

    /// <summary>A resolver with NO source tier — every playable's video comes from the user's own attachments. This is the
    /// pre-login / fake bootstrap shape: overrides must work without Spotify, and there is simply nothing behind them.</summary>
    public static CompositeVideoResolver OverridesOnly(VideoOverrideService? overrides)
        => new((_, _) => Task.FromResult<PopOutVideoSource?>(null), overrides);

    public Task<PopOutVideoSource?> ResolveAsync(string playableUri, CancellationToken ct = default)
    {
        if (_overrides is { } svc)
        {
            var decision = svc.Decide(playableUri);
            switch (decision.Tier)
            {
                case VideoOverrideTier.UseOverride:
                    svc.NoteResolved(playableUri, decision.Override);
                    return Task.FromResult<PopOutVideoSource?>(PopOutVideoSource.LocalFile(decision.Override));
                case VideoOverrideTier.Broken:
                    // The file moved / the drive is offline. Keep the link (it is repairable), warn once, and fall through
                    // to the original — a bad attachment must never block the music. The File.Exists probe inside Decide
                    // IS the fallback gate; there is no second existence check anywhere downstream.
                    svc.NoteBroken(playableUri, decision.Override);
                    break;
                case VideoOverrideTier.Quarantined:
                    break;   // already failed to open this session — skip tier 1 silently (the anti-loop latch)
            }
        }
        return _sourceTier(playableUri, ct);
    }
}
