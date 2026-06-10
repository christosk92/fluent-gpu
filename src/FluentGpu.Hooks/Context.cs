using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Text;

namespace FluentGpu.Hooks;

/// <summary>A context channel with a default. Consumers read the nearest provider's value via <c>UseContext</c>.</summary>
public sealed class Context<T>
{
    public T Default;
    public Context(T defaultValue) => Default = defaultValue;
}

/// <summary>
/// The ambient client size (DIP), pushed by the host each frame so components can adapt to available width
/// (responsive layout / NavigationView display modes). Read with <c>UseContext(Viewport.Size)</c>.
/// </summary>
public static class Viewport
{
    public static readonly Context<Size2> Size = new(default);
}

/// <summary>
/// A per-frame tick the host bumps (only while something subscribes, so it's free when idle). A tree-level concern that
/// must poll each frame — e.g. an overlay driving a timed close animation to completion — reads
/// <c>UseContext(FrameClock.Tick)</c> to re-render every frame for as long as it's mounted.
/// </summary>
public static class FrameClock
{
    public static readonly Context<long> Tick = new(0L);
}

/// <summary>
/// A host-owned registration surface a tree-level concern can hook into without the host depending on it. The host
/// bridges <see cref="KeyPreview"/> into the input dispatcher's pre-focus key hook, and runs
/// <see cref="AfterAnimations"/> after the animation engine ticks but before recording. Read the host instance via
/// <c>UseContext(InputHooks.Current)</c>.
/// </summary>
public sealed class InputHooks
{
    /// <summary>Return true to consume the key. Set by an open overlay; cleared when it closes.</summary>
    public Func<int, bool>? KeyPreview;
    public bool Preview(int key) => KeyPreview?.Invoke(key) ?? false;

    /// <summary>The currently-focused node (host-wired to the dispatcher). An opening overlay captures it so the focus
    /// can be restored when the overlay closes (WinUI flyout focus-restoration).</summary>
    public Func<NodeHandle>? GetFocus;
    /// <summary>Restore focus to a node (host-wired to the dispatcher). Used by an overlay on close.</summary>
    public Action<NodeHandle>? RestoreFocus;

    /// <summary>Move keyboard focus to a node (host-wired to the dispatcher's SetFocus). visual=true draws the
    /// engine focus ring — keyboard-initiated moves (ItemsView arrow nav / typeahead) pass true; pointer-style
    /// moves pass false.</summary>
    public Action<NodeHandle, bool>? FocusNode;

    /// <summary>Move keyboard focus to a node WITH the focus visual (host-wired to the dispatcher's
    /// <c>SetFocus(node, visual: true)</c>) — the roving-focus seam for container controls (RadioButtons arrow
    /// navigation, RadioButtons.cpp MoveFocus), where WinUI shows the focus rect on the newly-focused item.
    /// Contrast <see cref="RestoreFocus"/>, which restores silently (overlay close).</summary>
    public Action<NodeHandle>? MoveFocusVisual;

    /// <summary>Push a dispatcher FOCUS SCOPE rooted at a node: Tab/Shift-Tab cycle inside its subtree until popped
    /// (WinUI TabFocusNavigation=Cycle - the ContentDialog/flyout focus trap). Host-wired to
    /// <c>InputDispatcher.PushFocusScope</c>.</summary>
    public Action<NodeHandle>? PushFocusScope;
    /// <summary>Remove the focus scope previously pushed for this root (order-independent - overlays can close out of
    /// stack order). Host-wired to <c>InputDispatcher.RemoveFocusScope</c>.</summary>
    public Action<NodeHandle>? PopFocusScope;
    /// <summary>First focusable node (tab order) within a subtree - the focus trap's initial-focus query.
    /// Host-wired to <c>InputDispatcher.FirstFocusableIn</c>.</summary>
    public Func<NodeHandle, NodeHandle>? FirstFocusableIn;

    /// <summary>Set by the overlay host: the window lost activation → close every light-dismiss overlay
    /// (WinUI window-deactivation dismiss). Invoked from the dispatcher's WindowBlur via the host wiring.</summary>
    public Action? WindowBlurred;
    public void NotifyWindowBlur() => WindowBlurred?.Invoke();

    // ── E4 windowed out-of-bounds popups (host-wired in the AppHost ctor; consumed by OverlayHost) ──────────────────
    /// <summary>Window-DIP point → the containing MONITOR's work area translated into window-DIP space (the container
    /// windowed popups clamp against — WinUI FlyoutBase_Partial.cpp:3382-3392 <c>useMonitorBounds</c>). The host owns
    /// the DIP↔screen conversion (<c>IPlatformWindow.ClientOriginPx</c> + <c>IPlatformApp.GetWorkArea</c>).</summary>
    public Func<Point2, RectF>? GetWorkArea;
    /// <summary>Lease a popup WINDOW for an overlay subtree root (<c>PopupOptions.ConstrainToRootBounds = false</c>):
    /// the host creates the PAL popup window + its own swapchain and re-records the subtree into its own DrawList each
    /// frame (SceneRecorder root-override). Returns a token, or -1 when windowed popups are unavailable — callers fall
    /// back to constrained placement (WinUI's <c>DoesPlatformSupportWindowedPopup</c> gate).</summary>
    public Func<NodeHandle, int>? OpenPopupWindow;
    /// <summary>Place a leased popup window: token + bounds in main-window DIP (the host converts to physical
    /// virtual-screen px and shows the window without activating it).</summary>
    public Action<int, RectF>? SetPopupWindowBounds;
    /// <summary>Release a leased popup window (hide + dispose the window and its swapchain).</summary>
    public Action<int>? ClosePopupWindow;

    // ── Text-editing seams (host-wired in the AppHost ctor; consumed by EditableText) ───────────────────────────────
    // Wiring choice: the PAL seam INTERFACES are exposed directly (Hooks → Pal is a new interface-only edge; the
    // alternative — re-declaring delegate twins of IClipboard/ITextInputSink here — would duplicate a contract the
    // ownership map says Pal owns). Caret-blink and IME-caret-rect stay DELEGATE-shaped because the host owns knowledge
    // the control must not have: the CaretBlinker instance, and the window Scale for the DIP→physical-px conversion.

    /// <summary>The system clipboard (UI-thread only; <c>IPlatformApp.Clipboard</c>).</summary>
    public IClipboard? Clipboard;
    /// <summary>The focused window's IME/text-services seam (<c>IPlatformWindow.TextInput</c>): the focused editor
    /// registers its composition sink and flips <c>SetEditable</c> here. (Candidate-window placement goes through
    /// <see cref="ImeSetCaretRect"/> instead — it needs the host's DIP→px scale.)</summary>
    public IPlatformTextInput? TextInput;
    /// <summary>The host's text seam — the SAME layout pipeline the renderer measures with, so editor hit-test/caret
    /// queries agree with drawn glyph positions exactly.</summary>
    public IFontSystem? Fonts;

    /// <summary>Launch a URI in the OS default handler (<c>IPlatformApp.OpenUri</c>) — HyperlinkButton's WinUI
    /// NavigateUri step (Click first, then launch — HyperLinkButton_Partial.cpp:149-177). Host-wired in the AppHost
    /// ctor onto BOTH the host instance and the <see cref="Current"/> channel-default instance (static control
    /// factories have no component scope → no UseContext, so they reach the seam via the default). Null in a
    /// host-less tree → element construction/clicks never launch.</summary>
    public Action<string>? OpenUri;

    /// <summary>Arm the caret blinker for a (newly focused) editor's TEXT node; float = blink half-period ms
    /// (<c>IPlatformTextInput.CaretBlinkMs</c>).</summary>
    public Action<NodeHandle, float>? CaretFocus;
    /// <summary>Stop blinking for an editor's text node (focus lost).</summary>
    public Action<NodeHandle>? CaretBlur;
    /// <summary>An edit happened: snap the caret visible and restart the blink phase.</summary>
    public Action<NodeHandle>? CaretReset;
    /// <summary>Position the IME candidate window: the caret rect in window DIP — the HOST converts to physical px
    /// (it owns the window scale) before calling <c>IPlatformTextInput.SetCaretRectPx</c>.</summary>
    public Action<RectF>? ImeSetCaretRect;

    private readonly List<(object Owner, Action Action)> _afterAnimations = new();

    /// <summary>
    /// Host phase 7 hook: after <c>AnimEngine.Tick</c>, before record/present. Tree-level systems with retained
    /// animation lifecycles (overlays) use this to finalize settled visuals without a one-frame post-present delay.
    /// Registered by owner so multiple tree systems can coexist without clobbering each other.
    /// </summary>
    public void SetAfterAnimations(object owner, Action? action)
    {
        for (int i = 0; i < _afterAnimations.Count; i++)
        {
            if (!ReferenceEquals(_afterAnimations[i].Owner, owner)) continue;
            if (action is null) _afterAnimations.RemoveAt(i);
            else _afterAnimations[i] = (owner, action);
            return;
        }
        if (action is not null) _afterAnimations.Add((owner, action));
    }

    public void RunAfterAnimations()
    {
        for (int i = 0; i < _afterAnimations.Count; i++)
            _afterAnimations[i].Action();
    }

    public static readonly Context<InputHooks> Current = new(new InputHooks());
}

/// <summary>Provides a context value to its subtree (the React <c>Context.Provider</c>). One child.</summary>
public sealed record ContextProviderEl(object Channel, object? Value, Element Child) : Element
{
    public override ushort ElementTypeId => 4;
}

/// <summary>Fluent: <c>Ctx.Provide(MyContext, value, child)</c>.</summary>
public static class Ctx
{
    public static ContextProviderEl Provide<T>(Context<T> context, T value, Element child) => new(context, value, child);
}

/// <summary>
/// Reconcile-time shadow stack of provided context values. The reconciler pushes a provider's value before
/// reconciling its subtree (incl. nested component renders) and pops after; <c>UseContext</c> reads the top.
/// (Push/pop happen on the dirty reconcile path, never the zero-alloc paint half.)
/// </summary>
public static class ContextStack
{
    [ThreadStatic] private static List<(object ctx, object? val)>? _s;

    public static void Push(object ctx, object? val) => (_s ??= new()).Add((ctx, val));
    public static void Pop() { var s = _s!; s.RemoveAt(s.Count - 1); }

    public static bool TryGet(object ctx, out object? val)
    {
        var s = _s;
        if (s is not null)
            for (int i = s.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s[i].ctx, ctx)) { val = s[i].val; return true; }
        val = null;
        return false;
    }
}
