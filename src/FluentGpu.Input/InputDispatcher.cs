using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Input;

/// <summary>Directional focus movement for roving/XY keyboard navigation (arrow keys in lists, grids, menus).</summary>
public enum FocusDirection : byte { Left, Right, Up, Down }

/// <summary>
/// Phase 2 (input dispatch): hit-tests the committed scene and routes pointer + keyboard. Pointer down→up over the
/// same node fires the click handler and focuses the nearest focusable self-or-ancestor (a WinUI IsTabStop=False part
/// never takes pointer focus; nothing focusable in the chain → focus unchanged); keyboard routes to the focused node and bubbles up ancestors
/// (Handled stops it); Tab moves focus through focusable nodes; Enter/Space activates a focused clickable (the
/// "one declaration, three modalities" contract). The full engine adds tunnel(Preview), gesture arena, XY-focus.
/// </summary>
public sealed class InputDispatcher
{
    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _focusables = new();
    private readonly List<NodeHandle> _scoped = new();   // reused buffer for scoped (within-a-root) focus collection
    private NodeHandle _down;
    private NodeHandle _focused;
    private NodeHandle _hovered;
    private NodeHandle _pressed;
    private NodeHandle _dragTarget;
    private NodeHandle _scrollHovered;
    private NodeHandle _scrollDragNode;
    private float _scrollDragGrab;
    private NodeHandle _contextDown;   // right-button press target — context menu fires on release over the same chain
    private NodeHandle _middleDown;    // middle-button press target — delivered on release over the same node (TabView close)
    private NodeHandle _keyArmed;      // focused clickable held via Space/Enter — pressed while held, activates on key-UP
    private int _keyArmedKey;          // which key armed it (Space or Enter) — any OTHER key-down cancels without firing
    private bool _accessKeyMode;       // Alt tapped → the next letter invokes a matching AccessKey mnemonic
    private bool _altPending;          // Alt is down with no intervening key (candidate for access-key-mode toggle)
    private readonly List<NodeHandle> _focusScopes = new();
    // Double/triple-click tracking (platform timestamps; slop + window per Win32 defaults, capped at 3).
    private uint _lastDownMs;
    private Point2 _lastDownPos;
    private int _lastDownButton = -1;
    private byte _clickCount = 1;
    private CursorId _lastCursor = CursorId.Arrow;
    // Read-only text selection (rtb-02 — WinUI TextSelectionManager, default-on for RichTextBlock,
    // RichTextBlock.cpp:1730): the SelectableTextBit node owning the current selection + the drag anchor index.
    private NodeHandle _selText;
    private int _selAnchor;
    private bool _selDragging;

    public const float ClickSlopPx = 4f;
    public const uint DoubleClickMs = 500;

    private const float ScrollbarSize = 12f;
    private const float ScrollbarMinExpandedThumb = 30f;
    private const float ScrollbarMinCollapsedThumb = 32f;
    private const float ScrollbarSmallChange = 48f;

    public InputDispatcher(SceneStore scene)
    {
        _scene = scene;
        Drag = new DragController(scene, () => RequestRerender());
        DragDrop = new DragDropContext(scene, () => RequestRerender()) { ScrollBy = AutoScrollBy };
    }

    public NodeHandle Focused => _focused;

    /// <summary>The drag-reorder gesture engine (E5-L1): armed by a press on a <c>CanDrag</c> chain, promoted past the
    /// 4px drag box, owning the pointer until release/Escape. Constructed with the dispatcher so every host gets
    /// item-drag without wiring; the host hooks <see cref="DragController.OnSettle"/> for the FLIP drop-glide.</summary>
    public DragController Drag { get; }

    /// <summary>The typed drag-drop context (E5-L2, the Flutter Draggable/DragTarget + rbd model): a promoted L1 drag
    /// whose chain carries a <c>BoxEl.Draggable</c> opens THE <see cref="DragSession"/> here; per move the nearest
    /// accepting <c>BoxEl.DropTarget</c> under the pointer gets Enter/Over/Leave, release over it gets OnDrop, and
    /// the engine edge auto-scroll arms when the pointer drags near an overflowing viewport's edge (host-ticked).</summary>
    public DragDropContext DragDrop { get; }

    /// <summary>Edge auto-scroll write for <see cref="DragDropContext"/>: immediate clamped offset move on a viewport
    /// (the SetScrollOffset path — content transform + virtual re-realize + scrollbar reveal). False at the boundary.</summary>
    private bool AutoScrollBy(NodeHandle n, float delta)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return false;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        float old = sc.Orientation == 1 ? sc.OffsetX : sc.OffsetY;
        return SetScrollOffset(n, old + delta);
    }

    /// <summary>Set by the host: a virtual list crossing an item boundary on scroll requests the next render.</summary>
    public Action RequestRerender { get; set; } = static () => { };

    /// <summary>Set by the host: notified when a node gains/loses hover or press, so the interaction animator can ease
    /// the brush transition (kept as delegates to keep Input decoupled from the Animation assembly).</summary>
    public Action<NodeHandle, bool>? OnHoverChanged;
    public Action<NodeHandle, bool>? OnPressChanged;

    /// <summary>When true, a wheel sets the scroll TARGET and the ScrollAnimator eases the offset (momentum/inertia +
    /// auto-hiding scrollbars). When false, the offset jumps immediately (the deterministic default for tests).</summary>
    public bool SmoothScroll;
    /// <summary>Set by the host: arm a viewport in the ScrollAnimator after a smooth-scroll wheel (decouples Input from Animation).</summary>
    public Action<NodeHandle>? OnScrollArmed;
    /// <summary>Set by the host: pointer is over a scrollable viewport → reveal its auto-hiding scrollbar.</summary>
    public Action<NodeHandle, bool>? OnScrollHover;
    public Action<NodeHandle>? OnScrollLeave;

    /// <summary>Set by the host: a RepeatButton was pressed (held) / released — drives the RepeatTicker auto-repeat.</summary>
    public Action<NodeHandle>? OnRepeatArmed;
    public Action<NodeHandle>? OnRepeatReleased;
    /// <summary>Set by the host: the held pointer left / re-entered the armed repeat node — the ticker pauses off-node
    /// and resumes with a FRESH initial delay on re-entry, never an immediate re-fire (RepeatButton_Partial.cpp:530-574).
    /// Both are idempotent per move.</summary>
    public Action<NodeHandle>? OnRepeatPaused;
    public Action<NodeHandle>? OnRepeatResumed;

    /// <summary>Set by the host: a global key preview run before focus routing (returns true = consumed). Lets a tree-level
    /// concern (an open overlay/flyout) intercept Escape regardless of where focus is, without stealing focus.</summary>
    public Func<int, bool>? OnKeyPreview;

    /// <summary>Raised when the window loses activation: pressed/hover/drag state has been cleared; the host closes
    /// light-dismiss overlays here (WinUI window-deactivation dismiss).</summary>
    public Action? OnWindowBlur;

    /// <summary>Raised on window activation/placement changes (WindowFocus, WindowBlur, WindowStateChanged): the host
    /// bumps the titlebar-chrome epoch signal here so a custom TitleBar re-renders (dimming / max↔restore glyph).</summary>
    public Action? OnWindowActivationChanged;

    /// <summary>Raised when the resolved hover cursor changes — the host wires this to <c>IPlatformWindow.SetCursor</c>.</summary>
    public Action<CursorId>? OnCursorChanged;

    /// <summary>Text seam for the read-only selection/hyperlink gestures (point↔index hit-testing + selection rects —
    /// the SAME editor queries EditableText drives). Null falls back to <see cref="TextSeam.Default"/> (set by the
    /// font-system constructors), so hosts that predate this seam still get selection without wiring.</summary>
    public IFontSystem? Fonts { get; set; }

    /// <summary>Clipboard seam for Ctrl+C over a read-only selection (WinUI TextSelectionManager::CopySelectionToClipboard,
    /// TextSelectionManager.cpp:30-41). Set by the host; null = copy is a no-op.</summary>
    public IClipboard? Clipboard { get; set; }

    // ── focus scopes (modal focus trap: ContentDialog / flyout) ───────────────────────────────────
    /// <summary>Push a focus scope: Tab/Shift+Tab and arrow focus stay within <paramref name="root"/>'s subtree until popped.</summary>
    public void PushFocusScope(NodeHandle root) => _focusScopes.Add(root);
    public void PopFocusScope() { if (_focusScopes.Count > 0) _focusScopes.RemoveAt(_focusScopes.Count - 1); }
    /// <summary>Remove the focus scope for <paramref name="root"/> wherever it sits in the stack (overlays can close
    /// out of stack order - popping blindly could drop another live trap).</summary>
    public void RemoveFocusScope(NodeHandle root)
    {
        for (int i = _focusScopes.Count - 1; i >= 0; i--)
            if (_focusScopes[i] == root) { _focusScopes.RemoveAt(i); return; }
    }
    private NodeHandle ScopeRoot
    {
        get
        {
            for (int i = _focusScopes.Count - 1; i >= 0; i--)
                if (_scene.IsLive(_focusScopes[i])) return _focusScopes[i];
            return _scene.Root;
        }
    }

    public int Dispatch(ReadOnlySpan<InputEvent> events)
    {
        // Drop transient state that pointed at a freed node.
        if (!_focused.IsNull && !_scene.IsLive(_focused)) _focused = NodeHandle.Null;
        if (!_hovered.IsNull && !_scene.IsLive(_hovered)) _hovered = NodeHandle.Null;
        if (!_pressed.IsNull && !_scene.IsLive(_pressed)) _pressed = NodeHandle.Null;
        if (!_keyArmed.IsNull && !_scene.IsLive(_keyArmed)) { _keyArmed = NodeHandle.Null; _keyArmedKey = 0; }
        if (!_dragTarget.IsNull && !_scene.IsLive(_dragTarget)) _dragTarget = NodeHandle.Null;
        if (!_scrollHovered.IsNull && !_scene.IsLive(_scrollHovered)) _scrollHovered = NodeHandle.Null;
        if (!_scrollDragNode.IsNull && !_scene.IsLive(_scrollDragNode)) _scrollDragNode = NodeHandle.Null;
        if (!_selText.IsNull && !_scene.IsLive(_selText)) { _selText = NodeHandle.Null; _selDragging = false; }
        Drag.PruneDead();      // an armed/active drag node freed by a reconcile is abandoned (its columns are dead)
        DragDrop.PruneDead();  // a session whose source/target/viewport died: end / drop the dead reference

        int handled = 0;
        foreach (ref readonly var e in events)
        {
            switch (e.Kind)
            {
                case InputKind.PointerMove:
                    if (Drag.IsActive)   // an active item-drag owns the pointer (capture): no hover/scroll/slider routing
                    {
                        Drag.Move(e.PositionPx, e.Mods, e.TimestampMs);
                        // L2: target Enter/Over/Leave transitions + edge auto-scroll on the chain under the pointer
                        // (the lifted subtree is hit-test-transparent, so the chain sees THROUGH the moving visual).
                        // Gated on a live session — a plain CanDrag reorder never pays the extra hit-test walk.
                        if (DragDrop.IsActive)
                            DragDrop.Move(HitTestAny(e.PositionPx), e.PositionPx, Drag.VelocityX, Drag.VelocityY, e.Mods);
                        handled++;
                        break;
                    }
                    if (Drag.IsArmed && Drag.Move(e.PositionPx, e.Mods, e.TimestampMs))
                    {
                        // Promoted on this move: the gesture is a drag now — kill the click candidate, the transient
                        // press/hover visuals and any pending auto-repeat (WinUI: crossing the drag box cancels them).
                        if (!_down.IsNull && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                            OnRepeatReleased?.Invoke(_down);
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
                        _dragTarget = NodeHandle.Null;
                        // L2: a chain carrying a BoxEl.Draggable opens THE session (payload resolved once at this
                        // promotion edge) and immediately evaluates the target under the pointer.
                        if (DragDrop.TryBegin(Drag.ActiveNode, e.PositionPx, e.Mods, e.Pointer))
                            DragDrop.Move(HitTestAny(e.PositionPx), e.PositionPx, Drag.VelocityX, Drag.VelocityY, e.Mods);
                        handled++;
                        break;
                    }
                    SetState(ref _hovered, HitTest(e.PositionPx), NodeFlags.Hovered);
                    // Hyperlink spans re-resolve the cursor on EVERY move over the same text node — the span boundary
                    // crossings happen inside one node, which the on-hover-change walk alone can't see (WinUI flips to
                    // the hand per pointer-move over an inline Hyperlink, RichTextBlock.cpp:2995 / TextBlock.cpp:3488).
                    if (!_hovered.IsNull && _scene.IsLive(_hovered)
                        && (_scene.Interaction(_hovered).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
                        UpdateSpanCursor(_hovered, e.PositionPx);
                    // While a press is held WITHOUT a capture-drag (no OnDrag target / scrollbar drag), the pressed
                    // visual tracks whether the pointer is still over the pressed node — drag-off un-presses,
                    // drag-back re-presses (ButtonBase_Partial.cpp:629-638 IsPressed = IsValidPointerPosition). A
                    // continuous OnDrag gesture (slider scrub) keeps its pressed state — WinUI's captured thumb does.
                    if (!_down.IsNull && _dragTarget.IsNull && _scrollDragNode.IsNull && _scene.IsLive(_down))
                    {
                        var dl = PointToLocal(_down, e.PositionPx);   // scale-aware (a button inside a Viewbox)
                        ref RectF db = ref _scene.Bounds(_down);
                        bool overDown = dl.X >= 0f && dl.X < db.W && dl.Y >= 0f && dl.Y < db.H;
                        SetState(ref _pressed, overDown ? _down : NodeHandle.Null, NodeFlags.Pressed);
                        // Auto-repeat pauses off-node and resumes with a FRESH delay on re-entry — no immediate
                        // re-fire (RepeatButton_Partial.cpp:530-548, :565-574). Idempotent per move.
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                        {
                            if (overDown) OnRepeatResumed?.Invoke(_down);
                            else OnRepeatPaused?.Invoke(_down);
                        }
                    }
                    // Read-only selection drag (rtb-02): the press anchored on a selectable text node — every move
                    // extends the selection at pointer rate (the seam clamps out-of-bounds points, so the drag keeps
                    // tracking past the box exactly like the editor's drag-select).
                    if (_selDragging && !_selText.IsNull && _down == _selText && _scene.IsLive(_selText))
                    {
                        ExtendTextSelection(PointToLocal(_selText, e.PositionPx));
                        handled++;
                        break;
                    }
                    UpdateScrollHover(e.PositionPx);
                    if (DragScrollbar(e.PositionPx))
                    {
                        handled++;
                        break;
                    }
                    if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget))   // drag updates while held (slider/scrollbar)
                    {
                        _scene.GetDrag(_dragTarget)?.Invoke(LocalPos(_dragTarget, e.PositionPx));
                        handled++;
                    }
                    else if (!_hovered.IsNull && _scene.GetHoverMove(_hovered) is { } hm)   // bare-hover preview (RatingControl)
                    {
                        hm(LocalPos(_hovered, e.PositionPx));
                        handled++;
                    }
                    break;

                case InputKind.PointerDown:
                    if (e.Button == 1)   // right button: context-menu tracking only — never presses/activates
                    {
                        _contextDown = HitTestAny(e.PositionPx);
                        break;
                    }
                    if (e.Button == 2)   // middle button: tracked for release-over-same delivery (TabView middle-click
                    {                    // close commits on RELEASE over the tab, TabViewItem.cpp:418-462) — no press
                        _middleDown = HitTestAny(e.PositionPx);   // visual, no focus move, no click
                        break;
                    }
                    if (e.Button != 0) break;

                    if (TryScrollbarPointerDown(e.PositionPx))
                    {
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        _down = NodeHandle.Null;
                        handled++;
                        break;
                    }

                    TrackClickCount(in e);
                    _down = HitTest(e.PositionPx);
                    SetState(ref _pressed, _down, NodeFlags.Pressed);
                    // A press anywhere OUTSIDE the selection's node dismisses it (WinUI: pointer-down resets the
                    // text selection unless it lands back in the selectable control).
                    if (!_selText.IsNull && _selText != _down) ClearTextSelection();
                    if (!_down.IsNull)
                    {
                        // Pointer focus moves on the PRESS edge (WinUI Focus(FocusState_Pointer) + CapturePointer in
                        // OnPointerPressed, ButtonBase_Partial.cpp:700-709), never on release. Nearest focusable
                        // self-or-ancestor, without the focus ring; an AllowFocusOnInteraction=False node blocks the
                        // move entirely (focus stays put — AppBarButton_themeresources.xaml:136). IsTabStop=False
                        // parts (PasswordBox reveal / TextBox delete) still fall through to the field root.
                        var focusTarget = NearestFocusable(_down);
                        if (!focusTarget.IsNull &&
                            (_scene.Interaction(focusTarget).HandlerMask & InteractionInfo.NoPointerFocusBit) == 0)
                            SetFocus(focusTarget, visual: false);

                        var local = LocalPos(_down, e.PositionPx);
                        _scene.GetPointerDown(_down)?.Invoke(local);                 // press-to-set
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.PressedBit) != 0)
                            _scene.GetPointerPressed(_down)?.Invoke(new PointerEventArgs
                            {
                                Local = local, ClickCount = _clickCount, Mods = e.Mods, Button = 0, Kind = e.Pointer,
                            });
                        if (_scene.GetDrag(_down) is not null) _dragTarget = _down;  // begin a drag gesture
                        else
                            // Arm a drag-reorder candidate on the nearest CanDrag ancestor (a press on a child of a
                            // draggable row arms the row). A continuous OnDrag press (slider) keeps its semantics.
                            Drag.TryArm(_down, e.PositionPx, e.Pointer, e.Mods, e.TimestampMs);
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                            OnRepeatArmed?.Invoke(_down);   // RepeatButton: fire click now, then repeat while held
                        // Read-only selection press (rtb-02): anchor / word-select / select-all per click count
                        // (single = caret anchor for the drag; double = word; triple = all — the RichEdit/WinUI shape).
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.SelectableTextBit) != 0)
                            BeginTextSelection(_down, PointToLocal(_down, e.PositionPx), _clickCount);
                        if ((_scene.Interaction(_down).HandlerMask & (InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.RepeatBit | InteractionInfo.DragBit | InteractionInfo.SelectableTextBit | InteractionInfo.SpanLinksBit)) != 0) handled++;
                    }
                    break;

                case InputKind.PointerUp:
                    if (e.Button == 1)   // right button release → context request on the nearest handler in the chain
                    {
                        var ctxHit = HitTestAny(e.PositionPx);
                        if (!ctxHit.IsNull && ctxHit == _contextDown && DispatchContextRequest(ctxHit, e.PositionPx)) handled++;
                        _contextDown = NodeHandle.Null;
                        break;
                    }
                    if (e.Button == 2)   // middle release over the same node → typed pointer args with Button=2 on the
                    {                    // nearest OnPointerPressed in the chain (WinUI TabViewItem middle-click close
                        var midHit = HitTestAny(e.PositionPx);   // commits on PointerReleased, TabViewItem.cpp:418-462)
                        if (!midHit.IsNull && midHit == _middleDown && DispatchMiddleRelease(midHit, in e)) handled++;
                        _middleDown = NodeHandle.Null;
                        break;
                    }
                    if (e.Button != 0) break;

                    if (Drag.IsActive)
                    {
                        // Release after an active item-drag: L2 drop FIRST (OnDrop reads the live session while the
                        // visuals are still lifted), then the L1 completion; the click is SUPPRESSED (WinUI — a
                        // finished drag never raises the item's click/Tapped). A drop on a non-reorder target
                        // suppresses the spring-back glide (the payload was deposited there); a reorder target
                        // (DropTargetSpec.SettleOnDrop) keeps the FLIP drop-glide into the new slot.
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        bool dropped = DragDrop.TryDrop(e.PositionPx, e.Mods, out bool settleGlide);
                        Drag.Complete(e.PositionPx, e.Mods, e.TimestampMs, suppressSettle: dropped && !settleGlide);
                        _down = NodeHandle.Null;
                        _dragTarget = NodeHandle.Null;
                        handled++;
                        break;
                    }
                    Drag.Disarm();   // armed but never promoted → a plain click; fall through to normal release

                    if (!_scrollDragNode.IsNull)
                    {
                        _scrollDragNode = NodeHandle.Null;
                        handled++;
                        break;
                    }

                    var up = HitTest(e.PositionPx);
                    bool wasRepeat = !_down.IsNull && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0;
                    if (wasRepeat) OnRepeatReleased?.Invoke(_down);   // stop the auto-repeat
                    SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);   // release
                    _selDragging = false;   // the selection (if any) stays; the drag gesture ends with the press
                    if (!up.IsNull && up == _down)
                    {
                        // Click on release-over-same (ClickMode.Release). Pointer FOCUS already moved on the press
                        // edge (WinUI ButtonBase_Partial.cpp:700-709) — the release only fires the click.
                        if (!wasRepeat) _scene.GetClickHandler(up)?.Invoke();   // repeat nodes already fired via the ticker
                        // Hyperlink span click: release over the span's laid rect fires ITS action (WinUI inline
                        // Hyperlink commits on the release over the pressed hyperlink, RichTextBlock.cpp:2996-3001).
                        if ((_scene.Interaction(up).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
                        {
                            int si = HitLinkSpan(up, PointToLocal(up, e.PositionPx));
                            if (si >= 0 && _scene.TryGetSpanText(up, out var linkSpans) && (uint)si < (uint)linkSpans.Length)
                                linkSpans[si].OnClick?.Invoke();
                        }
                        handled++;
                    }
                    else if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget) &&
                             (_scene.Flags(_dragTarget) & NodeFlags.Disabled) == 0)
                    {
                        // Implicit pointer capture for continuous OnDrag gestures (WinUI CapturePointer): a press that
                        // began an OnDrag gesture (slider scrub, RatingControl sweep, ToggleSwitch knob drag) delivers
                        // its RELEASE to that node even when the pointer ends outside it — the node's click handler is
                        // its release/commit edge (RatingControl.cpp:875-906 capture → commit-on-release incl. the
                        // drag-off-left clear; Slider_Partial.cpp:478-543/580-623 CapturePointer → PerformPointerUpAction).
                        // PointerCancel still skips this (capture loss is not a commit), matching WinUI's cancel path.
                        // (Focus moved on the press edge, like every pointer gesture.)
                        _scene.GetClickHandler(_dragTarget)?.Invoke();
                        handled++;
                    }
                    // Touch has no resting hover: lifting the finger clears the hover state a contact-move set
                    // (mouse/pen keep hovering the node under the cursor).
                    if (e.Pointer == PointerKind.Touch) SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
                    _down = NodeHandle.Null;
                    _dragTarget = NodeHandle.Null;
                    break;

                case InputKind.Key:
                    OnKey(in e);
                    break;

                case InputKind.KeyUp:
                    OnKeyUp(in e);
                    break;

                case InputKind.Char:
                    if (OnChar(e.KeyCode)) handled++;
                    break;

                case InputKind.Wheel:
                    // Element-level wheel handlers (WinUI PointerWheelChanged) see the wheel BEFORE the viewport:
                    // a Handled NumberBox consumes the step instead of scrolling the form (NumberBox.cpp:578-597).
                    if (DispatchWheel(in e)) { handled++; break; }
                    if (ScrollAt(e.PositionPx, e.ScrollDelta)) handled++;
                    break;

                case InputKind.PointerCancel:
                    CancelPointer();
                    break;

                case InputKind.WindowBlur:
                    CancelPointer();
                    CancelKeyArm(fire: false);
                    SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
                    _accessKeyMode = false; _altPending = false;
                    OnWindowBlur?.Invoke();
                    OnWindowActivationChanged?.Invoke();   // custom titlebar dims (TextTertiary / disabled glyphs)
                    break;

                case InputKind.WindowFocus:
                    OnWindowActivationChanged?.Invoke();   // custom titlebar un-dims
                    break;

                case InputKind.WindowStateChanged:
                    OnWindowActivationChanged?.Invoke();   // custom titlebar re-glyphs max↔restore
                    break;
            }
        }
        return handled;
    }

    /// <summary>Clear all in-flight pointer interaction (capture lost / window deactivated).</summary>
    private void CancelPointer()
    {
        DragDrop.Cancel();   // L2 first: OnLeave fires on a live target while the session still exists
        Drag.Cancel();   // an in-flight item-drag aborts on capture loss (restores visuals, fires OnDragCanceled)
        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        if (!_down.IsNull && (_scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0))
            OnRepeatReleased?.Invoke(_down);
        // The captured OnDrag gesture owner learns its gesture died (WinUI PointerCaptureLost): controls reset the
        // scrub/hover preview in OnPointerExit — a RatingControl alt-tabbed mid-sweep must not keep its downRef.
        if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget)) _scene.GetPointerExit(_dragTarget)?.Invoke();
        _down = NodeHandle.Null;
        _dragTarget = NodeHandle.Null;
        _scrollDragNode = NodeHandle.Null;
        _contextDown = NodeHandle.Null;
        _middleDown = NodeHandle.Null;
        _selDragging = false;   // capture lost mid-drag-select: keep the selection, end the gesture
    }

    /// <summary>Promote consecutive same-button presses inside the slop window into double/triple clicks (capped at 3).</summary>
    private void TrackClickCount(in InputEvent e)
    {
        bool chained = _lastDownButton == e.Button
                       && e.TimestampMs - _lastDownMs <= DoubleClickMs
                       && MathF.Abs(e.PositionPx.X - _lastDownPos.X) <= ClickSlopPx
                       && MathF.Abs(e.PositionPx.Y - _lastDownPos.Y) <= ClickSlopPx;
        _clickCount = chained ? (byte)Math.Min(_clickCount + 1, 3) : (byte)1;
        _lastDownMs = e.TimestampMs;
        _lastDownPos = e.PositionPx;
        _lastDownButton = e.Button;
    }

    /// <summary>Walk up from <paramref name="node"/> for the first enabled ContextBit handler and invoke it (local coords).</summary>
    private bool DispatchContextRequest(NodeHandle node, Point2 abs)
    {
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.ContextBit) == 0) continue;
            _scene.GetContextRequested(n)?.Invoke(LocalPos(n, abs));
            return true;
        }
        return false;
    }

    /// <summary>Middle-button release over the press target: typed pointer args (Button=2) on the nearest enabled
    /// <c>OnPointerPressed</c> in the chain — the WinUI commit-on-release middle-click (TabViewItem.cpp:418-462).</summary>
    private bool DispatchMiddleRelease(NodeHandle node, in InputEvent e)
    {
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.PressedBit) == 0) continue;
            _scene.GetPointerPressed(n)?.Invoke(new PointerEventArgs
            {
                Local = LocalPos(n, e.PositionPx), ClickCount = 1, Mods = e.Mods, Button = 2, Kind = e.Pointer,
            });
            return true;
        }
        return false;
    }

    /// <summary>Element-level wheel routing (WinUI PointerWheelChanged bubbling): every enabled WheelBit handler up the
    /// chain sees the event until one sets Handled — which also stops the enclosing viewport from scrolling.</summary>
    private bool DispatchWheel(in InputEvent e)
    {
        WheelEventArgs? args = null;
        for (var n = HitTestAny(e.PositionPx); !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.WheelBit) == 0) continue;
            args ??= new WheelEventArgs { Delta = e.ScrollDelta, Mods = e.Mods };
            args.Local = LocalPos(n, e.PositionPx);
            _scene.GetPointerWheel(n)?.Invoke(args);
            if (args.Handled) return true;
        }
        return false;
    }

    // ── scrolling (layout-free: write the content's -ScrollOffset transform; never relayout) ──

    /// <summary>Scroll the nearest scrollable ancestor under the pointer; bubbles to an outer scroller at the edge.</summary>
    /// <summary>The nearest scrollable viewport under the pointer (for revealing its scrollbar on hover).</summary>
    private NodeHandle ScrollableUnder(Point2 p)
    {
        for (var n = HitTestAny(p); !n.IsNull; n = _scene.Parent(n))
            if ((_scene.Flags(n) & NodeFlags.Scrollable) != 0) return n;
        return NodeHandle.Null;
    }

    private void UpdateScrollHover(Point2 p)
    {
        if (OnScrollHover is null && OnScrollLeave is null) return;

        var next = ScrollableUnder(p);
        if (next != _scrollHovered)
        {
            if (!_scrollHovered.IsNull && _scene.IsLive(_scrollHovered))
                OnScrollLeave?.Invoke(_scrollHovered);
            _scrollHovered = next;
        }

        if (!next.IsNull)
            OnScrollHover?.Invoke(next, PointerInScrollbarLane(next, p));
    }

    private bool PointerInScrollbarLane(NodeHandle n, Point2 p)
    {
        if (!TryGetScrollbarMetrics(n, out var m)) return false;
        var local = new Point2(p.X - m.Bounds.X, p.Y - m.Bounds.Y);
        return InScrollbarLane(local, in m);
    }

    private bool ScrollAt(Point2 p, float delta)
    {
        var node = HitTestAny(p);
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0) continue;
            if (TryScrollNode(n, delta)) return true;   // consumed; else (at the edge) climb to an outer scroller
        }
        return false;
    }

    private bool TryScrollNode(NodeHandle n, float delta)
    {
        return ScrollBy(n, delta, SmoothScroll);
    }

    private bool ScrollBy(NodeHandle n, float delta, bool smooth)
    {
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float max = horizontal ? MathF.Max(0f, sc.ContentW - sc.ViewportW) : MathF.Max(0f, sc.ContentH - sc.ViewportH);

        if (smooth)
        {
            // Set the target; the ScrollAnimator eases the live offset toward it (+ virtualization re-realize + fade).
            float curTarget = horizontal ? sc.TargetX : sc.TargetY;
            float nextTarget = Math.Clamp(curTarget + delta, 0f, max);
            if (nextTarget == curTarget) return false;   // at the edge → bubble to an outer scroller
            if (horizontal) sc.TargetX = nextTarget; else sc.TargetY = nextTarget;
            sc.IdleMs = 0f;
            OnScrollArmed?.Invoke(n);
            return true;
        }

        float old = horizontal ? sc.OffsetX : sc.OffsetY;
        return SetScrollOffset(n, old + delta);
    }

    private bool SetScrollOffset(NodeHandle n, float offset)
    {
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float max = horizontal ? MathF.Max(0f, sc.ContentW - sc.ViewportW) : MathF.Max(0f, sc.ContentH - sc.ViewportH);
        float old = horizontal ? sc.OffsetX : sc.OffsetY;
        float next = Math.Clamp(offset, 0f, max);
        float target = horizontal ? sc.TargetX : sc.TargetY;
        if (next == old && target == next) return false;
        if (horizontal) { sc.OffsetX = next; sc.TargetX = next; }
        else { sc.OffsetY = next; sc.TargetY = next; }
        sc.IdleMs = 0f;
        ApplyScrollPosition(n, ref sc, horizontal, next);
        OnScrollArmed?.Invoke(n);
        return true;
    }

    private void ApplyScrollPosition(NodeHandle n, ref ScrollState sc, bool horizontal, float next)
    {
        // Layout-free scroll: the -ScrollOffset is the content child's LocalTransform (TransformDirty only).
        var content = sc.ContentNode;
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            cp.LocalTransform = Affine2D.Translation(horizontal ? -next : 0f, horizontal ? 0f : -next);
            _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        // Virtualization: keep transform-only scroll while the visible band remains inside the realized guard band.
        if (sc.ItemCount > 0)
        {
            int visibleFirst, visibleLast;
            float vp = horizontal ? sc.ViewportW : sc.ViewportH;
            if (sc.Layout is not null)   // fixed-geometry (stack/grid/custom)
            {
                float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                sc.Layout.Window(sc.ItemCount, cross, vp, next, 0, out visibleFirst, out visibleLast);
            }
            else if (_scene.TryGetExtents(n, out var t) && t is not null)   // variable (extent table)
            {
                visibleFirst = t.IndexAt(next);
                visibleLast = Math.Min(sc.ItemCount, t.IndexAt(next + vp) + 1);
            }
            else { visibleFirst = visibleLast = 0; }
            if (VirtualWindowing.NeedsRealize(in sc, visibleFirst, visibleLast)) { _scene.Mark(n, NodeFlags.VirtualRangeDirty); RequestRerender(); }
        }
    }

    private bool TryScrollbarPointerDown(Point2 p)
    {
        var n = ScrollableUnder(p);
        if (n.IsNull || !TryGetScrollbarMetrics(n, out var m)) return false;

        var local = new Point2(p.X - m.Bounds.X, p.Y - m.Bounds.Y);
        if (!InScrollbarLane(local, in m)) return false;

        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.PointerOver = true;
        sc.PointerOverScrollbar = true;
        sc.IdleMs = 0f;
        if (sc.FadeT < 0.2f) sc.FadeT = 0.2f;
        OnScrollArmed?.Invoke(n);

        float axis = AxisPos(local, in m);
        if (axis >= m.ThumbStart && axis <= m.ThumbStart + m.ThumbLen)
        {
            _scrollDragNode = n;
            _scrollDragGrab = Math.Clamp(axis - m.ThumbStart, 0f, m.ThumbLen);
            return true;
        }

        float delta;
        if (m.Button > 1f && axis < m.Button) delta = -ScrollbarSmallChange;
        else if (m.Button > 1f && axis >= m.Axis - m.Button) delta = ScrollbarSmallChange;
        else
        {
            float page = MathF.Max(ScrollbarSmallChange, m.Viewport * 0.875f);
            delta = axis < m.ThumbStart ? -page : page;
        }
        ScrollBy(n, delta, SmoothScroll);
        return true;
    }

    private bool DragScrollbar(Point2 p)
    {
        if (_scrollDragNode.IsNull) return false;
        if (!TryGetScrollbarMetrics(_scrollDragNode, out var m))
        {
            _scrollDragNode = NodeHandle.Null;
            return false;
        }

        var local = new Point2(p.X - m.Bounds.X, p.Y - m.Bounds.Y);
        float axis = AxisPos(local, in m);
        float thumbStart = Math.Clamp(axis - _scrollDragGrab, m.TrackStart, m.TrackStart + m.Travel);
        float fraction = Math.Clamp((thumbStart - m.TrackStart) / MathF.Max(1f, m.Travel), 0f, 1f);
        SetScrollOffset(_scrollDragNode, fraction * m.Max);

        ref ScrollState sc = ref _scene.ScrollRef(_scrollDragNode);
        sc.PointerOver = true;
        sc.PointerOverScrollbar = true;
        sc.IdleMs = 0f;
        return true;
    }

    private bool TryGetScrollbarMetrics(NodeHandle n, out ScrollbarMetrics m)
    {
        m = default;
        if (n.IsNull || !_scene.HasScroll(n)) return false;

        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        float max = MathF.Max(0f, content - viewport);
        if (max <= 0.5f) return false;

        var bounds = _scene.AbsoluteRect(n);
        float axis = horizontal ? bounds.W : bounds.H;
        float cross = horizontal ? bounds.H : bounds.W;
        if (axis <= 1f || cross <= 1f) return false;

        float expand = Math.Clamp(sc.ExpandT, 0f, 1f);
        float button = ScrollbarSize * expand;
        float trackStart = button;
        float trackLen = MathF.Max(1f, axis - 2f * button);
        float fraction = Math.Clamp(viewport / content, 0.08f, 1f);
        float minThumb = ScrollbarMinCollapsedThumb + (ScrollbarMinExpandedThumb - ScrollbarMinCollapsedThumb) * expand;
        float thumbLen = MathF.Min(trackLen, MathF.Max(minThumb, fraction * trackLen));
        float travel = MathF.Max(1f, trackLen - thumbLen);
        float off = horizontal ? sc.OffsetX : sc.OffsetY;
        float thumbStart = trackStart + Math.Clamp(off / MathF.Max(max, 1f), 0f, 1f) * travel;

        m = new ScrollbarMetrics
        {
            Bounds = bounds,
            Horizontal = horizontal,
            Axis = axis,
            Cross = cross,
            Viewport = viewport,
            Max = max,
            Button = button,
            TrackStart = trackStart,
            ThumbStart = thumbStart,
            ThumbLen = thumbLen,
            Travel = travel,
        };
        return true;
    }

    private static bool InScrollbarLane(Point2 local, in ScrollbarMetrics m)
    {
        float cross = m.Horizontal ? local.Y : local.X;
        float laneStart = m.Cross - ScrollbarSize;
        return cross >= laneStart && cross < m.Cross;
    }

    private static float AxisPos(Point2 local, in ScrollbarMetrics m) => m.Horizontal ? local.X : local.Y;

    private struct ScrollbarMetrics
    {
        public RectF Bounds;
        public bool Horizontal;
        public float Axis, Cross, Viewport, Max;
        public float Button, TrackStart, ThumbStart, ThumbLen, Travel;
    }

    /// <summary>Move a single-node interaction flag (hover/pressed) from the old node to <paramref name="next"/>.</summary>
    private void SetState(ref NodeHandle slot, NodeHandle next, NodeFlags flag)
    {
        if (slot == next) return;
        NodeHandle prev = slot;
        if (!prev.IsNull && _scene.IsLive(prev)) _scene.Flags(prev) &= ~flag;
        slot = next;
        if (!next.IsNull) _scene.Flags(next) |= flag;
        Notify(flag, prev, on: false);
        Notify(flag, next, on: true);
    }

    private void Notify(NodeFlags flag, NodeHandle node, bool on)
    {
        if (flag == NodeFlags.Hovered && on) UpdateCursor(node);   // resolve even for null (back to arrow)
        if (node.IsNull) return;
        if (flag == NodeFlags.Hovered)
        {
            OnHoverChanged?.Invoke(node, on);
            if (!on && _scene.IsLive(node)) _scene.GetPointerExit(node)?.Invoke();   // pointer left → reset hover preview
        }
        else if (flag == NodeFlags.Pressed) OnPressChanged?.Invoke(node, on);
    }

    /// <summary>Resolve the cursor for the hover chain — the nearest enabled node with an EXPLICIT <c>Cursor</c>
    /// (CursorBit) wins and stops the walk, so a child's Arrow masks an ancestor I-beam/hand (WinUI's forced
    /// SetCursor(MouseCursorArrow) on TextBox's delete button, TextBox_Partial.cpp:884). No explicit cursor anywhere
    /// in the chain ⇒ system arrow — clickability does NOT imply the hand (WinUI: only HyperlinkButton shows it).</summary>
    private void UpdateCursor(NodeHandle hover) => PublishCursor(ResolveCursorWalk(hover));

    private CursorId ResolveCursorWalk(NodeHandle hover)
    {
        for (var n = hover; !n.IsNull; n = _scene.Parent(n))
        {
            if (!_scene.IsLive(n)) break;
            ref InteractionInfo ii = ref _scene.Interaction(n);
            if ((ii.HandlerMask & InteractionInfo.CursorBit) != 0 && (_scene.Flags(n) & NodeFlags.Disabled) == 0) return ii.Cursor;
        }
        return CursorId.Arrow;
    }

    private void PublishCursor(CursorId resolved)
    {
        if (resolved == _lastCursor) return;
        _lastCursor = resolved;
        OnCursorChanged?.Invoke(resolved);
    }

    // ── read-only text selection + hyperlink spans (rtb-01/rtb-02) ───────────────────────────────
    // The dispatcher OWNS the gestures for SelectableTextBit / SpanLinksBit text leaves: it queries the text seam
    // (the SAME editor queries EditableText drives — point↔index, range rects) and publishes the visuals through the
    // EXISTING TextEditState/SetTextEditRects machinery, so the recorder paints read-only selection with the editor's
    // brushes unchanged. WinUI equivalent: TextSelectionManager on CRichTextBlock/CTextBlock (RichTextBlock.cpp:1730).

    private IFontSystem? ResolveFonts() => Fonts ?? TextSeam.Default;

    private string SelectableTextOf(NodeHandle node) => _scene.Strings?.Resolve(_scene.Paint(node).Text) ?? "";

    /// <summary>The seam wrap width for a text leaf's queries — the node's laid width when the style wraps, else
    /// unbounded (the EditableText.QueryMaxWidth convention; matches the recorder's DrawGlyphRun Bounds.W).</summary>
    private float QueryMaxWidth(NodeHandle node)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        return li.TextStyle.Wrap != TextWrap.NoWrap ? MathF.Max(1f, _scene.Bounds(node).W) : float.PositiveInfinity;
    }

    /// <summary>Press on a selectable text leaf: single click anchors the drag (no selection yet — unless it lands on
    /// a hyperlink span, which keeps the press a clean link click); double-click selects the word under the press;
    /// triple selects all (the RichEdit/WinUI TextSelectionManager click ladder).</summary>
    private void BeginTextSelection(NodeHandle node, Point2 local, int clickCount)
    {
        if (ResolveFonts() is not { } fonts) return;
        if (!_selText.IsNull && _selText != node) ClearTextSelection();
        string text = SelectableTextOf(node);
        if (text.Length == 0) return;
        if (clickCount == 1 && HitLinkSpan(node, local) >= 0) return;   // a link press never starts a selection
        int idx = fonts.HitTestText(text, _scene.Layout(node).TextStyle, QueryMaxWidth(node), local, out _);
        _selText = node;
        switch (clickCount)
        {
            case 1:
                _selAnchor = idx;
                _selDragging = true;
                ApplyTextSelection(node, idx, idx);   // empty until the drag extends it
                break;
            case 2:
            {
                int ws = idx, we = idx;
                while (ws > 0 && !char.IsWhiteSpace(text[ws - 1])) ws--;
                while (we < text.Length && !char.IsWhiteSpace(text[we])) we++;
                _selAnchor = ws;
                _selDragging = true;   // WinUI keeps extending by drag after a double-click
                ApplyTextSelection(node, ws, we);
                break;
            }
            default:
                _selAnchor = 0;
                _selDragging = false;
                ApplyTextSelection(node, 0, text.Length);
                break;
        }
    }

    private void ExtendTextSelection(Point2 local)
    {
        if (ResolveFonts() is not { } fonts) return;
        var node = _selText;
        string text = SelectableTextOf(node);
        if (text.Length == 0) return;
        int idx = fonts.HitTestText(text, _scene.Layout(node).TextStyle, QueryMaxWidth(node), local, out _);
        ApplyTextSelection(node, Math.Min(_selAnchor, idx), Math.Max(_selAnchor, idx));
    }

    /// <summary>Commit a selection range: store it (Ctrl+C reads it back), flag the node's text-edit row
    /// SelectionActive, and publish the seam's range rects through the pooled slab the recorder already draws
    /// (selection highlight under the run + on-accent recolor — SceneRecorder's editor path, reused verbatim).</summary>
    private void ApplyTextSelection(NodeHandle node, int start, int end)
    {
        _scene.SetTextSelection(node, start, end);
        ref TextEditState tes = ref _scene.TextEditRef(node);
        if (end > start) tes.Flags |= TextEditState.SelectionActive;
        else tes.Flags &= unchecked((byte)~TextEditState.SelectionActive);

        Span<RectF> rects = stackalloc RectF[32];
        int n = 0;
        if (end > start && ResolveFonts() is { } fonts)
        {
            string text = SelectableTextOf(node);
            n = fonts.GetRangeRects(text, _scene.Layout(node).TextStyle, QueryMaxWidth(node), start, end, rects);
        }
        _scene.SetTextEditRects(node, rects[..n], default);
        _scene.Mark(node, NodeFlags.PaintDirty);
        RequestRerender();
    }

    private void ClearTextSelection()
    {
        var node = _selText;
        _selText = NodeHandle.Null;
        _selDragging = false;
        if (node.IsNull || !_scene.IsLive(node)) return;
        _scene.ClearTextSelection(node);
        ref TextEditState tes = ref _scene.TextEditRef(node);
        tes.Flags &= unchecked((byte)~TextEditState.SelectionActive);
        _scene.SetTextEditRects(node, default, default);
        _scene.Mark(node, NodeFlags.PaintDirty);
        RequestRerender();
    }

    /// <summary>The hyperlink span index under a node-local point, or −1. Hit-tests the seam-published span LINK
    /// rects (SpanRunRects on the node's span run — laid at measure, no font-seam touch here), then verifies the
    /// span actually carries an action.</summary>
    private int HitLinkSpan(NodeHandle node, Point2 local)
    {
        int runId = _scene.Layout(node).TextStyle.SpanRunId;
        if (runId == 0 || SpanRunTable.Shared.Resolve(runId) is not { } run || run.Rects is not { } rects) return -1;
        if (!_scene.TryGetSpanText(node, out var spans)) return -1;
        var arts = rects.Rects;
        for (int i = 0; i < arts.Length; i++)
        {
            if (arts[i].Kind != SpanStyle.LinkBit) continue;
            var rr = arts[i].Rect;
            if (local.X >= rr.X && local.X < rr.X + rr.W && local.Y >= rr.Y && local.Y < rr.Y + rr.H)
            {
                int si = arts[i].Span;
                if ((uint)si < (uint)spans.Length && spans[si].OnClick is not null) return si;
            }
        }
        return -1;
    }

    /// <summary>Per-move cursor over a span-text node: Hand over a hyperlink span's laid rect (WinUI
    /// RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)), else whatever the normal explicit-cursor walk resolves
    /// (I-beam when selectable, arrow otherwise).</summary>
    private void UpdateSpanCursor(NodeHandle node, Point2 abs)
    {
        var local = PointToLocal(node, abs);
        PublishCursor(HitLinkSpan(node, local) >= 0 ? CursorId.Hand : ResolveCursorWalk(node));
    }

    private void OnKey(in InputEvent e)
    {
        int key = e.KeyCode;

        // Gamepad translation (WinUI XYFocus): DPad/left-stick → directional focus, A → activate, B → cancel/Escape.
        switch (key)
        {
            case Keys.GamepadDPadLeft or Keys.GamepadLeftThumbLeft: MoveFocusArrow(FocusDirection.Left); return;
            case Keys.GamepadDPadRight or Keys.GamepadLeftThumbRight: MoveFocusArrow(FocusDirection.Right); return;
            case Keys.GamepadDPadUp or Keys.GamepadLeftThumbUp: MoveFocusArrow(FocusDirection.Up); return;
            case Keys.GamepadDPadDown or Keys.GamepadLeftThumbDown: MoveFocusArrow(FocusDirection.Down); return;
            case Keys.GamepadA: key = Keys.Enter; break;
            case Keys.GamepadB: key = Keys.Escape; break;
        }

        // An active item-drag is the most-modal gesture: Escape cancels it before any other routing (WinUI drag
        // cancel). The pointer is still down — kill the click candidate so the eventual release does NOT click.
        if (key == Keys.Escape && Drag.IsActive)
        {
            DragDrop.Cancel();   // L2 first: OnLeave fires on a live target, no drop
            Drag.Cancel();
            SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            _down = NodeHandle.Null;
            return;
        }

        // Alt access-key bookkeeping: a bare Alt tap (down with nothing in between, then up) toggles access-key mode;
        // a letter while Alt is held invokes the mnemonic directly (the WM_SYSKEYDOWN chord path).
        if (key == Keys.Alt) { _altPending = !e.IsRepeat; return; }
        _altPending = false;

        if ((e.Mods & KeyModifiers.Alt) != 0 && Keys.IsAccessKeyCandidate(key))
        {
            if (InvokeAccessKey((char)key)) return;
        }
        if (_accessKeyMode)
        {
            _accessKeyMode = false;
            if (Keys.IsAccessKeyCandidate(key) && InvokeAccessKey((char)key)) return;
            if (key == Keys.Escape) return;   // Escape only exits access-key mode
        }

        if (OnKeyPreview is not null && OnKeyPreview(key)) return;   // an open overlay can swallow Escape here

        // ANY other key while a Space/Enter press is held cancels it without firing, and the new key then routes
        // normally (ButtonBaseKeyProcess.h:64-70). Escape is handled below as the explicit, CONSUMED cancel.
        if (!_keyArmed.IsNull && key != _keyArmedKey && key != Keys.Escape) CancelKeyArm(fire: false);

        if (key == Keys.Tab)
        {
            MoveFocus(forward: (e.Mods & KeyModifiers.Shift) == 0);
            return;
        }

        // Escape cancels a held Space/Enter-activation without firing (WinUI button semantics).
        if (key == Keys.Escape && !_keyArmed.IsNull) { CancelKeyArm(fire: false); return; }

        // Context-menu key (VK_APPS) / Shift+F10 → context request on the focused node (keyboard passes its centre).
        if ((key == Keys.Apps || (key == Keys.F10 && (e.Mods & KeyModifiers.Shift) != 0)) && !_focused.IsNull)
        {
            var r = _scene.AbsoluteRect(_focused);
            if (DispatchContextRequest(_focused, new Point2(r.X + r.W / 2f, r.Y + r.H / 2f))) return;
        }

        if (!_focused.IsNull)
        {
            bool clickable = (_scene.Flags(_focused) & NodeFlags.Disabled) == 0 &&
                             (_scene.Interaction(_focused).HandlerMask & InteractionInfo.ClickBit) != 0;

            // Modality 2 — keyboard activation with WinUI ButtonBase semantics (ButtonBaseKeyProcess.h): the FIRST
            // Space/Enter key-down arms the press (pressed visual); held-key auto-repeats are ignored, so a held key
            // yields exactly ONE activation; the click fires on key-UP (ClickMode.Release reentrancy rule,
            // ButtonBase_Partial.cpp:475-483). A RepeatButton is ClickMode.Press (RepeatButton_Partial.cpp:29): its
            // click fires on the DOWN edge — Space additionally arms the engine repeat timer (never the OS key-repeat
            // rate), Enter never repeats (RepeatButton_Partial.cpp:212-217). Space-only controls (CheckBox/
            // RadioButton/ToggleSwitch — NoEnterActivateBit) let Enter fall through to normal key routing.
            if ((key == Keys.Space || key == Keys.Enter) && clickable &&
                (key == Keys.Space || (_scene.Interaction(_focused).HandlerMask & InteractionInfo.NoEnterActivateBit) == 0))
            {
                if (e.IsRepeat || !_keyArmed.IsNull) return;   // held key / second activation key: one press only
                _keyArmed = _focused;
                _keyArmedKey = key;
                _scene.Flags(_focused) |= NodeFlags.Pressed;
                OnPressChanged?.Invoke(_focused, true);
                if ((_scene.Interaction(_focused).HandlerMask & InteractionInfo.RepeatBit) != 0)
                {
                    if (key == Keys.Space) OnRepeatArmed?.Invoke(_focused);   // fires once now + repeats while held
                    else _scene.GetClickHandler(_focused)?.Invoke();          // Enter: exactly one click, down edge
                }
                return;
            }

            // Route to the focused node and bubble up ancestors until Handled (disabled nodes don't receive keys).
            var args = new KeyEventArgs(key, e.Mods, e.IsRepeat);
            for (var n = _focused; !n.IsNull; n = _scene.Parent(n))
            {
                if ((_scene.Flags(n) & NodeFlags.Disabled) == 0) _scene.GetKeyHandler(n)?.Invoke(args);
                if (args.Handled) return;
            }
        }

        // Ctrl+C over a read-only selection (rtb-02): copy the focused selectable node's selected text through the
        // clipboard seam (WinUI TextSelectionManager::CopySelectionToClipboard — TextSelectionManager.cpp:30-41).
        // After focused routing: an editor's own Ctrl+C (EditableText) already consumed the chord above.
        if (key == 'C' && (e.Mods & KeyModifiers.Ctrl) != 0 && !_focused.IsNull && _scene.IsLive(_focused)
            && (_scene.Interaction(_focused).HandlerMask & InteractionInfo.SelectableTextBit) != 0
            && _scene.TryGetTextSelection(_focused, out int selS, out int selE) && selE > selS)
        {
            string selDoc = SelectableTextOf(_focused);
            if (selE <= selDoc.Length) Clipboard?.SetText(selDoc.Substring(selS, selE - selS));
            return;
        }

        // Keyboard accelerators (WinUI ProcessKeyboardAccelerators order: after focused routing leaves it unhandled).
        if ((e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0 || (key >= Keys.F1 && key <= Keys.F12))
        {
            var owner = _scene.FindAccelerator(key, e.Mods);
            if (!owner.IsNull) _scene.GetClickHandler(owner)?.Invoke();
        }
    }

    private void OnKeyUp(in InputEvent e)
    {
        int key = e.KeyCode;
        // The gamepad A/B translation must mirror OnKey's, or a GamepadA-armed press would never release.
        if (key == Keys.GamepadA) key = Keys.Enter;
        else if (key == Keys.GamepadB) key = Keys.Escape;

        if (key == Keys.Alt)
        {
            if (_altPending) _accessKeyMode = !_accessKeyMode;   // bare Alt tap toggles access-key mode
            _altPending = false;
            return;
        }
        if ((key == Keys.Space || key == Keys.Enter) && !_keyArmed.IsNull && key == _keyArmedKey)
        {
            // Released over the armed node → activate (ClickMode.Release). Repeat nodes already fired on the down
            // edge (Enter) or through the ticker (Space) — their release only clears the press, never re-fires.
            bool repeats = _scene.IsLive(_keyArmed) &&
                           (_scene.Interaction(_keyArmed).HandlerMask & InteractionInfo.RepeatBit) != 0;
            CancelKeyArm(fire: !repeats);
        }
    }

    /// <summary>Release a held Space/Enter-activation: clear the pressed visual, stop a keyboard-armed repeat ticker;
    /// <paramref name="fire"/> = invoke the click (key-up over the armed node).</summary>
    private void CancelKeyArm(bool fire)
    {
        var node = _keyArmed;
        int key = _keyArmedKey;
        _keyArmed = NodeHandle.Null;
        _keyArmedKey = 0;
        if (node.IsNull || !_scene.IsLive(node)) return;
        if (key == Keys.Space && (_scene.Interaction(node).HandlerMask & InteractionInfo.RepeatBit) != 0)
            OnRepeatReleased?.Invoke(node);
        _scene.Flags(node) &= ~NodeFlags.Pressed;
        OnPressChanged?.Invoke(node, false);
        if (fire && node == _focused && (_scene.Flags(node) & NodeFlags.Disabled) == 0)
            _scene.GetClickHandler(node)?.Invoke();
    }

    private bool InvokeAccessKey(char key)
    {
        var owner = _scene.FindAccessKey(key);
        if (owner.IsNull) return false;
        _accessKeyMode = false;
        _scene.GetClickHandler(owner)?.Invoke();
        return true;
    }

    /// <summary>Route a text (character) codepoint to the focused node, bubbling up ancestors until Handled.</summary>
    private bool OnChar(int codepoint)
    {
        if (_focused.IsNull) return false;
        var args = new CharEventArgs(codepoint);   // cold path: only allocates while the user is typing
        for (var n = _focused; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) == 0 &&
                (_scene.Interaction(n).HandlerMask & InteractionInfo.CharBit) != 0)
            {
                _scene.GetCharHandler(n)?.Invoke(args);
                if (args.Handled) return true;
            }
        }
        return false;
    }

    /// <summary>Move focus to the next/previous focusable node in tab order (TabIndex, then document order; cycles).
    /// Constrained to the active focus scope when one is pushed (dialog/flyout focus trap).</summary>
    public void MoveFocus(bool forward)
    {
        _focusables.Clear();
        Collect(ScopeRoot, _focusables);
        StableSortByTabIndex(_focusables);
        if (_focusables.Count == 0) { SetFocus(NodeHandle.Null); return; }

        int idx = _focusables.IndexOf(_focused);
        int n = _focusables.Count;
        int next = idx < 0 ? (forward ? 0 : n - 1) : (forward ? (idx + 1) % n : (idx - 1 + n) % n);
        SetFocus(_focusables[next], visual: true);   // keyboard focus → show the focus ring
    }

    /// <summary>Directional (arrow/XY) focus movement: from the focused node, pick the nearest focusable in
    /// <paramref name="dir"/> (primary-axis distance dominates; cross-axis breaks ties). For roving lists/grids.</summary>
    public void MoveFocusArrow(FocusDirection dir)
    {
        if (_focused.IsNull) { MoveFocus(forward: true); return; }
        _focusables.Clear();
        Collect(ScopeRoot, _focusables);
        if (_focusables.Count == 0) return;

        var cur = _scene.AbsoluteRect(_focused);
        float cx = cur.X + cur.W * 0.5f, cy = cur.Y + cur.H * 0.5f;
        NodeHandle best = NodeHandle.Null;
        float bestScore = float.MaxValue;
        bool horizontal = dir is FocusDirection.Left or FocusDirection.Right;
        foreach (var n in _focusables)
        {
            if (n == _focused) continue;
            var r = _scene.AbsoluteRect(n);
            float dx = (r.X + r.W * 0.5f) - cx, dy = (r.Y + r.H * 0.5f) - cy;
            bool inDir = dir switch
            {
                FocusDirection.Left => dx < -1f,
                FocusDirection.Right => dx > 1f,
                FocusDirection.Up => dy < -1f,
                FocusDirection.Down => dy > 1f,
                _ => false,
            };
            if (!inDir) continue;
            float primary = horizontal ? MathF.Abs(dx) : MathF.Abs(dy);
            float cross = horizontal ? MathF.Abs(dy) : MathF.Abs(dx);
            float score = primary + cross * 2f;   // bias toward staying on the same row/column
            if (score < bestScore) { bestScore = score; best = n; }
        }
        if (!best.IsNull) SetFocus(best, visual: true);
    }

    /// <summary>First focusable within <paramref name="root"/>'s subtree (tab order) — for focus-trap entry / menus.</summary>
    public NodeHandle FirstFocusableIn(NodeHandle root)
    {
        _scoped.Clear();
        Collect(root, _scoped);
        StableSortByTabIndex(_scoped);
        return _scoped.Count > 0 ? _scoped[0] : NodeHandle.Null;
    }

    /// <summary>Last focusable within <paramref name="root"/>'s subtree (tab order) — for Shift-Tab focus-trap wrap.</summary>
    public NodeHandle LastFocusableIn(NodeHandle root)
    {
        _scoped.Clear();
        Collect(root, _scoped);
        StableSortByTabIndex(_scoped);
        return _scoped.Count > 0 ? _scoped[^1] : NodeHandle.Null;
    }

    /// <summary>Next/previous focusable within <paramref name="root"/>, cycling — roving-tabindex within a list/menu/overlay.</summary>
    public NodeHandle NextFocusableIn(NodeHandle root, NodeHandle current, bool forward = true)
    {
        _scoped.Clear();
        Collect(root, _scoped);
        StableSortByTabIndex(_scoped);
        if (_scoped.Count == 0) return NodeHandle.Null;
        int idx = _scoped.IndexOf(current);
        int n = _scoped.Count;
        int next = idx < 0 ? (forward ? 0 : n - 1) : (forward ? (idx + 1) % n : (idx - 1 + n) % n);
        return _scoped[next];
    }

    // Stable insertion sort by effective TabIndex (explicit positive indices first ascending; default 0/unset keep
    // document order at the end). Stable + in-place + alloc-free; the focusable count is small (cold Tab/arrow path).
    private void StableSortByTabIndex(List<NodeHandle> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            var node = list[i];
            int key = TabKey(node);
            int j = i - 1;
            while (j >= 0 && TabKey(list[j]) > key) { list[j + 1] = list[j]; j--; }
            list[j + 1] = node;
        }
    }

    private int TabKey(NodeHandle n) { int t = _scene.Interaction(n).TabIndex; return t > 0 ? t : int.MaxValue; }

    /// <summary>Move focus. <paramref name="visual"/> = show the focus ring (keyboard/Tab); pointer focus passes false.
    /// Focus-visual transitions mark the affected nodes PaintDirty and request a frame — the ring is paint state.
    /// When focus actually MOVES, the old node's <c>OnFocusChanged</c> handler fires with false and the new node's with
    /// true (WinUI LostFocus → GotFocus order), AFTER all flag updates — so a GotFocus handler can read
    /// <see cref="NodeFlags.FocusVisual"/> to distinguish keyboard focus (select-all) from pointer focus.</summary>
    public void SetFocus(NodeHandle node, bool visual = false)
    {
        if (!node.IsNull && (_scene.Flags(node) & NodeFlags.Disabled) != 0) return;   // can't focus a disabled node — keep current focus
        if (!_keyArmed.IsNull && node != _keyArmed) CancelKeyArm(fire: false);  // focus moved while Space/Enter held → no activation
        var prev = _focused;
        bool repaint = false;
        if (!_focused.IsNull && _scene.IsLive(_focused))
        {
            if ((_scene.Flags(_focused) & NodeFlags.FocusVisual) != 0)
            {
                _scene.Mark(_focused, NodeFlags.PaintDirty);   // the old ring must disappear
                repaint = true;
            }
            _scene.Flags(_focused) &= ~(NodeFlags.Focused | NodeFlags.FocusVisual);
        }
        _focused = node;
        if (!node.IsNull)
        {
            _scene.Flags(node) |= NodeFlags.Focused;
            if (visual)
            {
                _scene.Flags(node) |= NodeFlags.FocusVisual;
                _scene.Mark(node, NodeFlags.PaintDirty);
                repaint = true;
            }
            else _scene.Flags(node) &= ~NodeFlags.FocusVisual;
        }
        if (prev != node)
        {
            // WinUI GotFocus/LostFocus are ROUTED (bubbling) events: an ancestor with an OnFocusChanged handler hears
            // focus ENTERING/LEAVING its SUBTREE, fired only on boundary crossings. The focused node itself keeps the
            // exact pre-existing self semantics. ToolTipService's keyboard-focus trigger hangs off this
            // (microsoft-ui-xaml ToolTipService_Partial.cpp:1635 OnOwnerGotFocus on the OWNER element).
            if (!prev.IsNull && _scene.IsLive(prev))
            {
                if ((_scene.Interaction(prev).HandlerMask & InteractionInfo.FocusBit) != 0)
                    _scene.GetFocusChanged(prev)?.Invoke(false);
                for (var n = _scene.Parent(prev); !n.IsNull; n = _scene.Parent(n))
                    if ((_scene.Interaction(n).HandlerMask & InteractionInfo.FocusBit) != 0 && !IsSelfOrAncestorOf(n, node))
                        _scene.GetFocusChanged(n)?.Invoke(false);
            }
            if (!node.IsNull && _scene.IsLive(node) && node == _focused)   // a LostFocus handler may have re-moved focus
            {
                if ((_scene.Interaction(node).HandlerMask & InteractionInfo.FocusBit) != 0)
                    _scene.GetFocusChanged(node)?.Invoke(true);
                for (var n = _scene.Parent(node); !n.IsNull && node == _focused; n = _scene.Parent(n))
                    if ((_scene.Interaction(n).HandlerMask & InteractionInfo.FocusBit) != 0 && !IsSelfOrAncestorOf(n, prev))
                        _scene.GetFocusChanged(n)?.Invoke(true);
            }
        }
        if (repaint) RequestRerender();
    }

    /// <summary>Resolve a pointer-activation focus target: the nearest self-or-ancestor carrying
    /// <see cref="NodeFlags.Focusable"/> (the Reconciler keeps that flag mirrored from <c>InteractionInfo.Focusable</c> =
    /// <c>TabStop ?? (Focusable || OnClick != null)</c>). WinUI <c>Control.IsTabStop=False</c> parts cannot receive
    /// focus at all — pointer included — so a click on them lands focus on the focusable control root above (the
    /// PasswordBox RevealButton / TextBox DeleteButton keep the FIELD focused; PasswordBox_themeresources.xaml:193 +
    /// TextBox_themeresources.xaml:339, both IsTabStop=False). Null = nothing focusable in the chain — the caller must
    /// leave focus unchanged. Disabled nodes are skipped (they cannot take focus); the chain is already visible
    /// (hit-testing requires Visible on every ancestor).</summary>
    private NodeHandle NearestFocusable(NodeHandle node)
    {
        for (var n = node; !n.IsNull && _scene.IsLive(n); n = _scene.Parent(n))
            if ((_scene.Flags(n) & (NodeFlags.Focusable | NodeFlags.Disabled)) == NodeFlags.Focusable) return n;
        return NodeHandle.Null;
    }

    /// <summary>True if <paramref name="root"/> is <paramref name="node"/> or one of its ancestors — i.e. focus stayed
    /// inside <paramref name="root"/>'s subtree, so no enter/leave boundary was crossed for it.</summary>
    private bool IsSelfOrAncestorOf(NodeHandle root, NodeHandle node)
    {
        if (node.IsNull || !_scene.IsLive(node)) return false;
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
            if (n == root) return true;
        return false;
    }

    private void Collect(NodeHandle node, List<NodeHandle> into)
    {
        if (node.IsNull) return;
        ref InteractionInfo ii = ref _scene.Interaction(node);
        if (ii.Focusable && (_scene.Flags(node) & (NodeFlags.Visible | NodeFlags.Disabled)) == NodeFlags.Visible) into.Add(node);
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) Collect(c, into);
    }

    /// <summary>Event position (window space) → the node's MODEL-LOCAL coords, clamped to its box (slider/scrollbar
    /// drag). Inverse-maps through every ancestor transform (scale-aware: a slider inside a Viewbox scrubs true).</summary>
    private Point2 LocalPos(NodeHandle node, Point2 abs)
    {
        var l = PointToLocal(node, abs);
        return new Point2(Math.Clamp(l.X, 0f, _scene.Bounds(node).W), Math.Clamp(l.Y, 0f, _scene.Bounds(node).H));
    }

    private readonly List<NodeHandle> _chain = new();   // reused root→node buffer for PointToLocal (0 steady alloc)

    /// <summary>Window-space point → <paramref name="node"/>'s model-local space, undoing every ancestor's frame the
    /// way the recorder composes it (translate(bounds) ∘ origin-conjugated LocalTransform, counter-scale about the
    /// centre) — the inverse of SceneRecorder.Walk's world transform.</summary>
    private Point2 PointToLocal(NodeHandle node, Point2 abs)
    {
        _chain.Clear();
        for (var n = node; !n.IsNull; n = _scene.Parent(n)) _chain.Add(n);
        var q = abs;
        float sx = 1f, sy = 1f;
        for (int i = _chain.Count - 1; i >= 0; i--)
            if (!StepIntoNode(_chain[i], ref q, ref sx, ref sy)) return q;   // degenerate scale: best-effort
        return q;
    }

    /// <summary>Undo ONE node's frame: parent-content point → this node's model-local point, mirroring the recorder's
    /// composition (translate(b) ∘ [T(o)∘L∘T(−o)] then the counter-scale block). Updates the running net scale the
    /// children inherit. Interaction hover/press grow is deliberately NOT mirrored — the hit target keeps its model
    /// box while a thumb visually grows (matches the previous engine behavior and WinUI's layout-driven hit testing).</summary>
    private bool StepIntoNode(NodeHandle node, ref Point2 q, ref float netSx, ref float netSy)
    {
        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);
        float lx = q.X - b.X, ly = q.Y - b.Y;
        if (!np.LocalTransform.IsIdentity)
        {
            float ox = b.W * np.OriginX, oy = b.H * np.OriginY;
            // forward: l' = L(l − o) + o  ⇒  l = L⁻¹(l' − o) + o
            if (!np.LocalTransform.TryInverseTransform(new Point2(lx - ox, ly - oy), out var inv))
            {
                q = new Point2(float.MinValue, float.MinValue);   // zero-scale renders nothing: miss everything
                return false;
            }
            lx = inv.X + ox; ly = inv.Y + oy;
        }
        // Counter-scaled node (drag-lift label …): the recorder un-scales it about its centre by the PARENT net scale
        // (SceneRecorder counter block) — the inverse re-applies that scale to the point.
        if ((_scene.Flags(node) & NodeFlags.CounterScaled) != 0 && (netSx != 1f || netSy != 1f))
        {
            float cx = b.W * 0.5f, cy = b.H * 0.5f;
            lx = (lx - cx) * netSx + cx;
            ly = (ly - cy) * netSy + cy;
            netSx = 1f; netSy = 1f;
        }
        netSx *= np.LocalTransform.M11;
        netSy *= np.LocalTransform.M22;
        q = new Point2(lx, ly);
        return true;
    }

    private Point2 _hitAbs;   // the window-space point of the in-flight hit walk (pass-through rect checks)

    public NodeHandle HitTest(Point2 p)
    {
        if (_scene.Root.IsNull) return NodeHandle.Null;
        _hitAbs = p;
        return Hit(_scene.Root, p, 1f, 1f);
    }

    /// <summary>Deepest visible node containing the point, regardless of click handler (used to find a scroll target).</summary>
    private NodeHandle HitTestAny(Point2 p)
    {
        if (_scene.Root.IsNull) return NodeHandle.Null;
        _hitAbs = p;
        return HitAny(_scene.Root, p, 1f, 1f);
    }

    /// <summary>WinUI <c>OverlayInputPassThroughElement</c>: a light-dismiss scrim yields the hit when the pointer is
    /// over its registered pass-through subtree — input falls through to the content beneath (the MenuBar keeps
    /// hover-switching titles with a menu open, FlyoutBase_Partial.cpp:3922-3938).</summary>
    private bool YieldsToPassThrough(NodeHandle node)
    {
        if (!_scene.TryGetHitTestPassThrough(node, out var pass) || pass.IsNull || !_scene.IsLive(pass)) return false;
        var pr = _scene.AbsoluteRect(pass);
        return _hitAbs.X >= pr.X && _hitAbs.X < pr.X + pr.W && _hitAbs.Y >= pr.Y && _hitAbs.Y < pr.Y + pr.H;
    }

    // Both walks descend the POINT through each node's inverse transform (scale-aware — WinUI hit-tests the rendered
    // geometry, so a button inside a 2× Viewbox is clickable across its whole rendered extent), mirroring the
    // recorder's world composition exactly. q is the point in the node's PARENT-content space.

    private NodeHandle HitAny(NodeHandle node, Point2 q, float netSx, float netSy)
    {
        var flags = _scene.Flags(node);
        if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible))
            return NodeHandle.Null;

        ref RectF b = ref _scene.Bounds(node);
        var local = q;
        if (!StepIntoNode(node, ref local, ref netSx, ref netSy)) return NodeHandle.Null;
        bool inside = local.X >= 0f && local.X < b.W && local.Y >= 0f && local.Y < b.H;
        if ((flags & NodeFlags.ClipsToBounds) != 0 && !inside) return NodeHandle.Null;

        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = HitAny(c, local, netSx, netSy);
            if (!r.IsNull) result = r;
        }
        if (result.IsNull && inside && !YieldsToPassThrough(node))
            result = node;
        return result;
    }

    private NodeHandle Hit(NodeHandle node, Point2 q, float netSx, float netSy)
    {
        var flags = _scene.Flags(node);
        if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible))
            return NodeHandle.Null;

        ref RectF b = ref _scene.Bounds(node);
        var local = q;
        if (!StepIntoNode(node, ref local, ref netSx, ref netSy)) return NodeHandle.Null;
        bool inside = local.X >= 0f && local.X < b.W && local.Y >= 0f && local.Y < b.H;
        if ((flags & NodeFlags.ClipsToBounds) != 0 && !inside) return NodeHandle.Null;

        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = Hit(c, local, netSx, netSy);
            if (!r.IsNull) result = r;
        }

        if (result.IsNull)
        {
            ref InteractionInfo ii = ref _scene.Interaction(node);
            // CursorBit makes a node hover-resolvable in its own right (WinUI: SetCursor applies on direct hover of
            // any hit-testable element — XAML hit-testing is background-gated, not handler-gated), so an editing
            // surface's own padding/gaps still show its I-beam. Harmless for clicks: no handler ⇒ nothing fires.
            // SelectableText/SpanLinks text leaves hit-test too (drag-select anchoring; hyperlink hover/click —
            // WinUI's selectable/hyperlink text is hit-testable, RichTextBlock.cpp:2988-3001).
            if ((flags & NodeFlags.Disabled) == 0 &&   // disabled nodes don't hit-test (gates click/pointer/drag/repeat)
                (ii.HandlerMask & (InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.DragBit | InteractionInfo.CursorBit | InteractionInfo.SelectableTextBit | InteractionInfo.SpanLinksBit)) != 0 &&
                inside && !YieldsToPassThrough(node))
            {
                result = node;
            }
        }
        return result;
    }
}
