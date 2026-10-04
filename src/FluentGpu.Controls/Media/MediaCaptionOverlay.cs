using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace FluentGpu.Controls.Media;

/// <summary>
/// The caption layer of <see cref="MediaPlayerElement"/> as its OWN, permanently mounted leaf component. It is the only
/// subscriber of <see cref="IMediaPlayer.ActiveCue"/> (and of the chrome-height lift), so a cue start or clear
/// re-renders this small subtree and never the player element: the session publishes a new cue from inside its pump on
/// every real change, and each one used to re-run the element's whole Render (children arrays, a dozen closures, the
/// context-menu wrapper, a reconcile of stage, chrome and transport). Media3 routes cues straight to its SubtitleView
/// the same way (<c>onCues -> subtitleView.setCues</c>); the player view's own layout is never invalidated by a caption.
///
/// <para>The shape is FIXED: the leaf always renders one full-slot, non-hit-testable ZStack, and the caption box is its
/// only (optional) child. The slot fills the element's video area exactly as the old direct child did, so the caption
/// is arranged by the same ZStack rule against the same area; only WHO re-renders on a cue changed.</para>
/// </summary>
internal sealed class MediaCaptionOverlay : Component
{
    /// <summary>Caption baseline inset from the bottom of the video area with the chrome DOWN.</summary>
    internal const float BottomMargin = 28f;

    /// <summary>The caption lift. Transform-only (a FLIP): captions MOVE out of the transport's way, the controls never
    /// move out of the captions' way. Its own 200 ms, deliberately NOT the chrome's conceal duration: the conceal is a
    /// 400 ms unattended fade, and a caption that crawls down over 400 ms reads as lag, not as motion.</summary>
    private static readonly LayoutTransition CaptionMotion = new(
        TransitionChannels.Position,
        TransitionDynamics.Tween(200f, Easing.FluentStandard));

    /// <summary>The player whose active cue is shown.</summary>
    public required IMediaPlayer Player { get; init; }
    /// <summary>The element's chrome-visible signal (the same one its transport fades on).</summary>
    public required IReadSignal<bool> ChromeVisible { get; init; }
    /// <summary>The transport's measured height (DIP), written by the transport's own bounds callback.</summary>
    public required IReadSignal<float> ChromeHeight { get; init; }
    /// <summary>The element mounts its own transport (enabled and not suppressed). Without one there is nothing for a
    /// caption to lift out of the way of. Frozen at mount, like the element's own init props.</summary>
    public bool TransportPresent { get; init; }

    public override Element Render()
    {
        // The readiness is a memo so a state tick that leaves it unchanged (Playing -> Paused, say) never re-renders here.
        var ready = UseComputed(() => MediaPlayerElement.IsVideoReady(Player.NaturalSize.Value, Player.State.Value));
        // Read only while captions can show: before the first frame (and for audio-only) a cue is not drawn, so its
        // changes must not wake this leaf either. The lift is read only while a cue exists, for the same reason.
        TimedCue? cue = ready.Value ? Player.ActiveCue.Value : null;
        var slot = new BoxEl { ZStack = true, Grow = 1f, HitTestVisible = false };
        if (cue is not { } c) return slot;
        float lift = TransportPresent && ChromeVisible.Value ? ChromeHeight.Value : 0f;
        return slot with { Children = [Caption(c, BottomMargin + lift)] };
    }

    // Captions MOVE, controls do not: the caption baseline lifts by the chrome's measured height while the chrome
    // is up and settles back when it hides, animated on the same clock (a transform-only FLIP, no relayout churn).
    private static Element Caption(TimedCue cue, float bottomMargin) => new BoxEl
    {
        Key = "media-caption",
        AlignSelf = FlexAlign.Center,
        MaxWidth = 880f,
        Animate = CaptionMotion,
        Margin = new Edges4(24f, 0f, 24f, bottomMargin),
        Padding = new Edges4(10f, 5f, 10f, 6f),
        Corners = Radii.ControlAll,
        Fill = Tok.MediaScrim with { A = 0.72f },
        Children =
        [
            new TextEl(cue.Text)
            {
                Size = Math.Clamp(18f * cue.Style.FontScale, 12f, 40f),
                Color = cue.Style.ArgbColor == 0 ? Tok.OnMediaPrimary : FromArgb(cue.Style.ArgbColor),
                Wrap = TextWrap.Wrap,
            },
        ],
    };

    // Convert a cue-supplied 0xAARRGGBB color (dynamic subtitle data) to a ColorF via the float ctor — a runtime
    // conversion, not a hardcoded color constant, so the media element carries no baked color literals.
    private static ColorF FromArgb(uint argb)
        => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);
}
