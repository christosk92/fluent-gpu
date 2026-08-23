using System;
using System.Threading.Tasks;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The bounded native transport-acknowledgement waits for <see cref="DesktopProtectedVideoPlayer"/>.
/// <para><b>Why this is a separate file.</b> The main part of the type is declared <c>unsafe</c> (it owns the native
/// licence callback and the blittable open descriptor), and C# forbids <c>await</c> anywhere in an unsafe context.
/// That is why these waits used to be <c>Task.Run</c> + <c>Thread.Sleep</c> polls, parking a THREAD-POOL thread for
/// up to ten seconds per seek or quality switch — a video→video skip could hold several at once. A partial part that
/// deliberately OMITS the <c>unsafe</c> modifier is a safe context, so the same polls become real async waits that
/// occupy no thread between ticks.</para>
/// <para><b>Why nothing here throws.</b> Callers discard these tasks (<c>_ = ...</c>): the transport verb is accepted
/// synchronously and the pump realizes the resulting state from the native snapshot. A supersession (the user seeks
/// again), a native error, or a blown budget is therefore NOT a user-visible failure — it is a stale acknowledgement
/// nobody is waiting on, and throwing produced unobserved task exceptions instead of information. Each of those
/// outcomes is a logged no-op on the always-on diagnostic timeline instead.</para>
/// </summary>
public sealed partial class DesktopProtectedVideoPlayer
{
    // These MUST match FgPlayReadySeekEx in ops/tools/playready-native/PlayReadyNative.cpp, where the default-arity
    // FgPlayReadySeek forwards with mode 0 — so 0 is EXACT, not approximate. Inverting them is silent: every scrub
    // preview would pay a full decode and every commit would land on a keyframe.
    /// <summary>Native seek mode: snap to the nearest keyframe (fast scrub, no preroll decode).</summary>
    private const int NativeSeekApproximate = 1;
    /// <summary>Native seek mode: decode to the exact requested PTS (the commit on release).</summary>
    private const int NativeSeekExact = 0;

    private const int TransportAckBudgetMs = 5_000;
    private const int TransportAckPollMs = 10;
    private const int RepresentationAckBudgetMs = 10_000;
    private const int RepresentationAckPollMs = 20;

    private async Task AwaitTransportAckAsync(ulong sequence, TransportAck kind)
    {
        long deadline = Environment.TickCount64 + TransportAckBudgetMs;
        while (!_disposed && !_shutdown.IsSet && Environment.TickCount64 < deadline)
        {
            if (TryGetSnapshot(out var snapshot))
            {
                ulong applied = kind == TransportAck.PlayPause ? snapshot.PlayAppliedSeq : snapshot.SeekAppliedSeq;
                if (applied >= sequence) return;
                if (snapshot.State == 5)
                {
                    LogVideo($"transport {kind} seq={sequence} abandoned — native is in error " +
                             $"0x{unchecked((uint)snapshot.ErrorHr):X8} (the pump surfaces the typed failure)");
                    return;
                }
            }
            await Task.Delay(TransportAckPollMs).ConfigureAwait(false);
        }
        if (!_disposed && !_shutdown.IsSet)
            LogVideo($"transport {kind} seq={sequence} was not acknowledged within {TransportAckBudgetMs}ms — " +
                     "treating it as superseded (the pump reconciles from the native snapshot)");
    }

    private async Task AwaitRepresentationAckAsync(ulong sequence, int index, string representationId)
    {
        long deadline = Environment.TickCount64 + RepresentationAckBudgetMs;
        while (!_disposed && !_shutdown.IsSet && Environment.TickCount64 < deadline)
        {
            if (TryGetSnapshot(out var snapshot) && snapshot.RepresentationAppliedSeq >= sequence)
            {
                if (snapshot.ActiveVideoRepresentation == index)
                {
                    _activeVideoRepresentationId = representationId;
                    LogVideo($"representation '{representationId}' applied at index {index}");
                }
                else
                {
                    LogVideo($"representation '{representationId}' (index {index}) was superseded at the segment " +
                             $"boundary — native is on index {snapshot.ActiveVideoRepresentation}");
                }
                return;
            }
            await Task.Delay(RepresentationAckPollMs).ConfigureAwait(false);
        }
        if (!_disposed && !_shutdown.IsSet)
            LogVideo($"representation '{representationId}' was not applied within {RepresentationAckBudgetMs}ms — " +
                     "treating it as superseded");
    }
}
