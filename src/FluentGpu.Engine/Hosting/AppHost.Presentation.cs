using FluentGpu.Pal;

namespace FluentGpu.Hosting;

public sealed partial class AppHost
{
    // The window presentation motion's mailbox (UI posts, the render thread applies it in DrainPopupRenderActions): latest wins, so
    // a starting hold followed by its entrance in one turn applies the entrance (which names its own starting pose).
    private bool _presentationPending;
    private WindowPresentationMotion _presentationMotion;
    /// <summary>How long a detached child's present-slot probe may wait out its own flip when this host has nothing of its own to present
    /// that turn (DrainChildRenderSources): the waitable signals well under a millisecond after the compositor tick the turn starts on.</summary>
    private const int ChildSlotGraceMs = 2;
    // Render thread: a posted motion has not landed yet, so the render loop keeps ticking this host's turns to pose it (HasRenderMotion).
    private bool _presentationMoving;

    /// <summary>UI thread: move this window as a whole on the compositor (<see cref="WindowPresentationMotion"/>): its UI composition
    /// root and its video layer fade and scale about an origin while the host keeps laying out and presenting at the window's real
    /// size. Applied on the render thread's next turn (woken now); a window that has not presented yet takes it with its first
    /// composed frame. Latest wins. A host without a composition tree (headless, an opaque swap chain) ignores it.</summary>
    public void AnimatePresentation(in WindowPresentationMotion motion)
    {
        // While the motion owns the window's arrival and departure, the system's own show / hide transitions stay off (its hide
        // transition drew the faded window's ground over the windows around it); Identity gives them back.
        _window.SetSystemTransitions(motion.IsIdentity);
        lock (_popupActionLock) { _presentationPending = true; _presentationMotion = motion; }
        OwningRenderThread?.WakeAsync();
    }

    /// <summary>Render thread, at the top of <see cref="DrainPopupRenderActions"/> (every turn, before the present): start a posted
    /// presentation motion, or pose the moving one for this turn.</summary>
    private void DrainPresentationMotion()
    {
        bool started = false;
        WindowPresentationMotion motion = default;
        lock (_popupActionLock)
        {
            if (_presentationPending)
            {
                _presentationPending = false;
                motion = _presentationMotion;
                started = true;
            }
        }
        if (!started && !_presentationMoving) return;
        try
        {
            if (started) { _device.AnimatePresentation(_swapchain, in motion); _presentationMoving = true; }
            else _presentationMoving = _device.TickPresentation(_swapchain);
        }
        catch (Exception) when (_device.NoteIfDeviceLost()) { _presentationMoving = false; }
    }
}
