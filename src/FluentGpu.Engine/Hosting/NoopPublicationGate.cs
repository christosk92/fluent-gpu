using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Hosting;

/// <summary>Everything a scene publication carries that no store ledger describes: the target epoch it is made for, the
/// scene-level fields the snapshot copies wholesale, the image and compositor-animation inputs (each as a change detector),
/// the recording configuration and options, and the frame's size, scale and clear colour. Two equal keys, with the store's
/// own ledger quiet (<see cref="FluentGpu.Scene.SceneStore.HasUnpublishedChanges"/>), mean a capture would hand the renderer
/// exactly the scene it already holds. Every member is a value type with value equality: comparing two keys never allocates.</summary>
internal readonly record struct PublicationKey(
    long TargetEpoch,
    NodeHandle Root,
    float DeviceScale,
    RectF OverlayClip,
    RectF? SpotlightScrimClip,
    bool RevealBands,
    int PendingRemovals,
    bool RemovalOverflow,
    long ImageSerial,
    ulong AnimFingerprint,
    int RecordingConfigVersion,
    Threading.SceneRecordOptions Options,
    Size2 FrameSize,
    float Scale,
    ColorF Clear);

/// <summary>Why a publication went ahead: the first gate clause that held (<see cref="None"/> = elided). Counted per
/// window for the <c>[wake]</c> census (<c>published=</c>), so "why did this idle-looking stretch keep publishing" has an answer.</summary>
internal enum NoopPublicationBlock
{
    None, Wake, Target, Capture, Structure, Transform, Images, SceneChange, Retiring, Overlay, Device, Scroll, Key, Parity,
}

/// <summary>
/// The no-op publication skip (async render seam). Under a render thread the UI used to publish a scene on EVERY Paint, so a
/// frame woken by something that changed nothing (a FrameClock poller that wrote an equal value, a timer, an explicit wake,
/// the tail of a warm-cadence hold) still captured the scene, woke the render thread, re-adopted the compositor rows,
/// re-recorded and hashed it, only to find the stream identical. The host now publishes only when the frame produced
/// something a publication carries.
/// <para><b>Soundness.</b> A publication is skipped only when ALL of these hold: the frame's wake reasons are within
/// <see cref="SkippableWake"/> (anything else — scroll, images, drag, popups, animation, video — always publishes), the host's
/// hard gates are quiet (no reconcile, layout, transform write, image content change, crossfade, upload, overlay, orphan,
/// popup, detached fly, structural damage, video change, record-dirty or removal ledger still retiring — see
/// <c>AppHost.NoopPublicationCandidate</c>), the store has no unpublished captured change, and the <see cref="PublicationKey"/>
/// and the scroll coverage equal the ones the last publication carried. The renderer then keeps presenting the scene it
/// already holds, which is byte-for-byte the one this frame would have published: nothing is traded for the saving.</para>
/// </summary>
internal sealed class NoopPublicationGate
{
    /// <summary>The wake reasons that, by themselves, do not imply a publication: their work (a signal write, a timer or poller
    /// callback, a caret toggle, a HUD string, the warm hold) reaches the scene only through ledgered store writes, which
    /// <see cref="FluentGpu.Scene.SceneStore.HasUnpublishedChanges"/> sees. Every other bit publishes unconditionally.</summary>
    public const WakeReasons SkippableWake = WakeReasons.FrameNeeded | WakeReasons.RuntimePending | WakeReasons.DynamicText
        | WakeReasons.Caret | WakeReasons.Timer | WakeReasons.WarmCadence | WakeReasons.FrameClockPoller
        | WakeReasons.FrameClockPaceable;

    private PublicationKey _last;
    private bool _valid;
    private readonly ScrollCoverageTable _lastCoverage = new();

    /// <summary>Publications elided as no-ops (cumulative, UI thread).</summary>
    public long Elided { get; private set; }

    public static bool WakeAllowsSkip(WakeReasons wake) => (wake & ~SkippableWake) == 0;

    /// <summary>True when <paramref name="key"/> and <paramref name="coverage"/> equal what the last publication carried.
    /// False until the first <see cref="Remember"/> and after <see cref="Invalidate"/>.</summary>
    public bool Matches(in PublicationKey key, ScrollCoverageTable coverage)
        => _valid && key == _last && coverage.ContentEquals(_lastCoverage);

    /// <summary>Count one elided publication.</summary>
    public void NoteElided() => Elided++;

    private const int BlockCount = (int)NoopPublicationBlock.Parity + 1;
    private static readonly string[] s_blockNames =
        ["none", "wake", "target", "capture", "structure", "transform", "images", "sceneChange", "retiring", "overlay", "device",
         "scroll", "key", "parity"];
    private readonly long[] _blocked = new long[BlockCount];

    /// <summary>Count one publication that went ahead, by the clause that required it.</summary>
    public void NoteBlocked(NoopPublicationBlock block) => _blocked[(int)block]++;

    /// <summary>Append <c> published=cause×n,…</c> for the publications since the previous call (nothing when none), and open
    /// the next window. Report cadence only (UI thread).</summary>
    public void AppendBlockedWindow(System.Text.StringBuilder sb)
    {
        bool first = true;
        for (int i = 1; i < BlockCount; i++)
        {
            if (_blocked[i] == 0) continue;
            sb.Append(first ? " published=" : ",").Append(s_blockNames[i])
              .Append(System.Globalization.CultureInfo.InvariantCulture, $"×{_blocked[i]}");
            first = false;
            _blocked[i] = 0;
        }
    }

    /// <summary>Record what a publication just carried.</summary>
    public void Remember(in PublicationKey key, ScrollCoverageTable coverage)
    {
        _last = key;
        _lastCoverage.CopyFrom(coverage);
        _valid = true;
    }

    /// <summary>Forget the last publication: the next frame publishes whatever it holds (the DEBUG elide self-check's
    /// response to a divergence, AppHost.VerifyNoopPublication).</summary>
    public void Invalidate() => _valid = false;
}
