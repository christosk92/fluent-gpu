namespace FluentGpu.Controls.Media;

/// <summary>
/// The chrome-activity seam for a host that draws its OWN on-media chrome instead of <see cref="MediaPlayerElement"/>'s
/// transport (<see cref="MediaPlayerElement.SuppressTransport"/>). The host's controls are SIBLINGS of the element in
/// the host's own tree, never descendants of it — the dispatcher's routed <c>OnPointerMoveWithin</c> walks ancestors
/// only, so hovering the host's own strip is invisible to the element and the chrome (and, on a
/// <see cref="CursorAutoHidePolicy"/>-driven surface, the cursor) fades out from under a pointer that is still
/// legitimately "on the controls". This feed lets the host tell the element's <see cref="PlayerChromeVisibility"/>
/// machine about that activity directly, on the SAME single pure machine the element's own transport would have driven
/// — never a second timer that could disagree with it.
///
/// <para>ONE instance per surface: create it once (a field, not a render-local — init props FREEZE AT MOUNT) and hand
/// it to <see cref="MediaPlayerElement.ChromeFeed"/>. The element claims it on mount and releases it on unmount; every
/// call is INERT before the element has mounted and after it has unmounted (the calls just no-op against a null
/// <see cref="Owner"/>) — a host may hold and call this instance across the element's own mount/unmount cycles (e.g. a
/// keyed remount) without ever needing to null-check it itself. No call allocates.</para>
/// </summary>
public sealed class PlayerChromeFeed
{
    /// <summary>The currently-mounted element this feed drives, or null before mount / after unmount. Set by the
    /// element's own binding effect — never by the host.</summary>
    internal MediaPlayerElement? Owner;

    /// <summary>Explicit activity on the host's own chrome (a hover, a click, a key) — reveals and restarts the dwell,
    /// exactly like a pointer move over the element's own transport would.</summary>
    public void Activity() => Owner?.FeedActivity();

    /// <summary>The pointer is resting on the HOST's own control panel (over, not merely near, the element) — a HOLD,
    /// not activity: it must never let the dwell expire out from under a resting pointer, but it does not itself
    /// reveal hidden chrome.</summary>
    public void SetPointerOverControls(bool over) => Owner?.FeedOverControls(over);

    /// <summary>The primary button is held on the host's own chrome. Re-asserting while already held is allowed and
    /// cheap: the element's own <c>HandleExit</c> → <c>PointerCovered</c> drops this hold the moment hover leaves the
    /// picture (a scrim, a capture cancel), so a host that is dragging something of its own re-asserts per sample to
    /// keep the hold alive for as long as the drag legitimately continues.</summary>
    public void SetPressed(bool pressed) => Owner?.FeedPressed(pressed);

    /// <summary>A scrub is in progress on the host's own seek UI. With <see cref="MediaPlayerElement.SuppressTransport"/>
    /// set the element never mounts its own seek bar, so its own scrub-gate effect never writes this hold itself — the
    /// host's write is the ONLY writer, and the two can never disagree.</summary>
    public void SetScrubbing(bool scrubbing) => Owner?.FeedScrubbing(scrubbing);

    /// <summary>The host observed <c>InputHooks.WindowMoveSizeBeganObserved</c> — its window entered an OS move loop
    /// (a caption-region drag). The WindowMove hold releases on <c>WindowMoveSizeEndedObserved</c> from the same hooks.</summary>
    public void WindowMoveStarted() => Owner?.FeedWindowMoveStarted();
}
