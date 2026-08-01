namespace FluentGpu.Foundation;

/// <summary>Keyboard modifier state, captured per input event at the platform pump (Win32 GetKeyState).</summary>
[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>The physical pointer device class an event came from (WinUI PointerDeviceType) — controls touch-vs-mouse
/// behavioral splits (RatingControl hover scale, touch paddings).</summary>
public enum PointerKind : byte
{
    Mouse = 0,
    Touch = 1,
    Pen = 2,
    /// <summary>Windows precision touchpad (<c>POINTER_INPUT_TYPE.PT_TOUCHPAD</c>); kept distinct from a mouse wheel so
    /// its high-resolution packet stream can track content directly and hand measured velocity to the kinetic tail.</summary>
    Touchpad = 3,
}

/// <summary>
/// The public gesture taxonomy the <c>UseGesture</c> hook (input-a11y.md §13) declares against — the app-facing
/// projection of the internal §7A.1 arena <c>GestureKind</c> (the recognizer-member identity stays Input-private). A
/// component that declares one of these enrolls a matching gesture-arena member on its node; the dispatcher routes the
/// arena WINNER's event to the handler (the §7A.5 first-accept/last-standing resolution decides which gesture fires).
///
/// <b>Phase-3 usable surface:</b> <see cref="Tap"/> (a within-slop down→up), <see cref="Hold"/> (a long-press that
/// promotes mid-stream), and <see cref="Pan"/> (a directional drag over the node, carrying end-velocity for fling
/// hand-off). <see cref="DoubleTap"/>/<see cref="RightTap"/>/<see cref="Drag"/>/<see cref="Pinch"/> are reserved
/// names the arena already recognizes internally; their <c>UseGesture</c> wiring is Phase-4 surface (pinch-zoom, the
/// double/right-tap routing) and a declaration against them is accepted but not yet routed.
/// </summary>
public enum GestureType : byte
{
    /// <summary>A within-slop press→release (the §7A pointer-up sweep resolves a clean tap to this).</summary>
    Tap,
    /// <summary>A long-press: the recognizer promotes to an eager arena win after the hold dwell (~500ms).</summary>
    Hold,
    /// <summary>A directional drag over the node: eager-wins on the slop cross; the end event carries the fling velocity.</summary>
    Pan,
    /// <summary>Reserved (Phase-4 routing): a double press→release inside the inter-tap window.</summary>
    DoubleTap,
    /// <summary>Reserved (Phase-4 routing): a right-button / long-press context tap.</summary>
    RightTap,
    /// <summary>Reserved (Phase-4 routing): a free drag (no scroll-axis lock).</summary>
    Drag,
    /// <summary>Reserved (Phase-4 routing): a two-contact pinch/zoom manipulation.</summary>
    Pinch,
}

/// <summary>
/// The payload routed to a <c>UseGesture</c> handler when its node WINS the gesture arena (input-a11y.md §13/§7A). ONE
/// instance is reused for the whole gesture surface (0 steady-state alloc — the dispatcher fills it before each
/// invocation); a handler copies what it keeps, never holds the reference. <see cref="Kind"/> is the resolved gesture,
/// <see cref="Position"/> the window-space pointer (for <see cref="GestureType.Tap"/>/<see cref="GestureType.Hold"/>
/// the down/press point; for <see cref="GestureType.Pan"/> the latest sample), and <see cref="Velocity"/> the
/// end-of-gesture flick speed (px/s) — meaningful for the Pan-end fling, zero otherwise.
/// </summary>
public sealed class GestureEventArgs
{
    /// <summary>The resolved gesture this event reports (the arena winner's kind).</summary>
    public GestureType Kind;
    /// <summary>Pointer position in window space (tap/hold: the press point; pan: the latest sample).</summary>
    public Point2 Position;
    /// <summary>End-of-gesture flick velocity (px/s; Pan-end only — zero for tap/hold).</summary>
    public Point2 Velocity;
    /// <summary>The device class that drove the gesture (touch is the Phase-3 driver; mouse/pen route through it too).</summary>
    public PointerKind Pointer;
}

/// <summary>
/// Keyboard event passed to node handlers during tunnel/bubble routing. <see cref="Handled"/> stops propagation.
/// Carries the modifier chord and the auto-repeat flag (Win32 lParam bit 30) so editors can do Ctrl+arrow word
/// navigation and Shift+arrow selection without re-querying the keyboard.
/// </summary>
public sealed class KeyEventArgs
{
    public int KeyCode;
    public KeyModifiers Mods;
    public bool IsRepeat;
    public bool Handled;
    public KeyEventArgs(int keyCode) => KeyCode = keyCode;
    public KeyEventArgs(int keyCode, KeyModifiers mods, bool isRepeat = false)
    {
        KeyCode = keyCode; Mods = mods; IsRepeat = isRepeat;
    }

    public bool Shift => (Mods & KeyModifiers.Shift) != 0;
    public bool Ctrl => (Mods & KeyModifiers.Ctrl) != 0;
    public bool Alt => (Mods & KeyModifiers.Alt) != 0;
}

/// <summary>
/// Character (text) input passed to <c>OnCharInput</c> handlers — the layout/IME-resolved Unicode codepoint, distinct
/// from the raw virtual-key of <see cref="KeyEventArgs"/> (Win32 splits WM_KEYDOWN from WM_CHAR; we mirror that).
/// </summary>
public sealed class CharEventArgs
{
    public int Codepoint;
    public bool Handled;
    public CharEventArgs(int codepoint) => Codepoint = codepoint;
}

/// <summary>
/// Position-aware pointer-press payload for <c>OnPointerPressed</c>: local coords + the click count (1/2/3 — the
/// dispatcher tracks double/triple-click timing and slop), the modifier chord, the button (0=left 1=right 2=middle)
/// and the device kind. Allocated only on an actual press (cold user-gesture edge).
/// </summary>
public sealed class PointerEventArgs
{
    public Point2 Local;
    public byte ClickCount = 1;
    public KeyModifiers Mods;
    public byte Button;
    public PointerKind Kind;
    public bool Handled;
}

/// <summary>
/// Element-level wheel payload for <c>OnPointerWheel</c> (WinUI PointerWheelChanged): the platform wheel delta —
/// the same value the viewport scroll path consumes — plus the modifier chord. Setting <see cref="Handled"/> stops
/// the dispatcher from scrolling the enclosing viewport (NumberBox value-stepping inside a scrollable form,
/// NumberBox.cpp:578-597 OnNumberBoxScroll marks the routed event handled). Unhandled, the wheel keeps walking up
/// to the next handler / the nearest scrollable (routed-event semantics). Allocated per wheel event (cold edge).
/// </summary>
public sealed class WheelEventArgs
{
    public Point2 Local;
    public float Delta;     // vertical wheel (the value the viewport vertical-scroll path consumes)
    public float DeltaX;    // horizontal wheel (WM_POINTERHWHEEL / trackpad two-finger horizontal); 0 on a plain wheel
    public KeyModifiers Mods;
    public bool Handled;
}

/// <summary>
/// Drag-reorder lifecycle payload for <c>OnDragStarted</c>/<c>OnDragDelta</c>/<c>OnDragCompleted</c>: the pointer in
/// the dragged node's CURRENT box (<see cref="Local"/> stays ≈ the grab offset) and in window space, the accumulated
/// gesture translation since the arming press, and the smoothed pointer velocity (px/s, ~50ms EMA) for flick/settle
/// decisions. ONE instance is reused for the whole gesture (0 steady-state alloc per move) — handlers must copy
/// fields they keep, never hold the reference.
/// </summary>
public sealed class DragEventArgs
{
    /// <summary>Pointer position in the dragged node's CURRENT (moving) box.</summary>
    public Point2 Local;
    /// <summary>Pointer position in window space.</summary>
    public Point2 Absolute;
    /// <summary>Accumulated translation since the arming press — feed to <c>ReorderList.Update</c>.</summary>
    public float TotalDx, TotalDy;
    /// <summary>Smoothed pointer velocity (px/s; ~50ms exponential-moving-average horizon).</summary>
    public float VelocityX, VelocityY;
    public KeyModifiers Mods;
    public PointerKind Kind;
}

/// <summary>How a context-menu request was raised (WinUI ContextRequestedEventArgs — the moral equivalent of
/// <c>TryGetPosition</c>): a <see cref="Pointer"/>/<see cref="Hold"/> request carries a real point (open at it), a
/// <see cref="Keyboard"/> request does NOT (WinUI TryGetPosition == false) so it anchors to the element rect instead.</summary>
public enum ContextRequestTrigger : byte
{
    /// <summary>Mouse right-click release over the node.</summary>
    Pointer,
    /// <summary>Menu key (VK_APPS) / Shift+F10 while the node has focus — position is the node centre, not a pointer.</summary>
    Keyboard,
    /// <summary>Touch long-press (gesture-arena Hold win) at the contact point.</summary>
    Hold,
    /// <summary>A left-click / touch-tap ACTIVATION of a <c>BoxEl.ClickRequestsContext</c> node re-entered the
    /// context-request funnel (input-a11y.md §6.5.1). Like <see cref="Keyboard"/> it carries NO pointer point — anchor
    /// to the SOURCE node's rect (the Keyboard rule generalized) — but it is pointer-originated, so the menu does NOT
    /// focus its first item. (Space/Enter key-activation of such a node dispatches <see cref="Keyboard"/> instead, so a
    /// keyboard invocation still focuses the first item.) Appended after <see cref="Hold"/> — the wire values of the
    /// existing triggers do NOT change.</summary>
    Invoke,
}

/// <summary>
/// Context-menu request payload for <c>OnContextRequested</c> (WinUI ContextRequested): the <see cref="Position"/> in
/// node-LOCAL coords plus the <see cref="Trigger"/> that raised it (so a menu opens AT the pointer/contact but anchors
/// to the element rect for a keyboard invocation). ONE instance is reused on the dispatcher (0 steady-state alloc — it
/// is filled before each invocation); a handler copies what it keeps, never holds the reference. <see cref="Source"/>
/// records the node the request ORIGINATED at (the button, for a <see cref="ContextRequestTrigger.Invoke"/>), distinct
/// from <see cref="Node"/> (the ContextBit owner the walk stopped at) — a rect-anchored open uses <see cref="Source"/>.
/// </summary>
public sealed class ContextRequestEventArgs
{
    /// <summary>Request position in node-LOCAL coords (pointer/hold: the contact; keyboard: the node centre).</summary>
    public Point2 Position;
    /// <summary>What raised the request (pointer / keyboard / touch long-press / click-activation Invoke).</summary>
    public ContextRequestTrigger Trigger;
    /// <summary>The node whose handler is being invoked (the ContextBit owner the dispatch walk stopped at) —
    /// <see cref="Position"/> is local to THIS node. This is the anchor a context menu opens against: reading it from
    /// the event is render- and recycle-proof, unlike an OnRealized capture (which goes stale the first time the
    /// element re-renders — realization callbacks fire at mount, not per diff). Value-copy it; the args instance is reused.</summary>
    public NodeHandle Node;
    /// <summary>The node the request ORIGINATED at — the <c>ClickRequestsContext</c> button for an
    /// <see cref="ContextRequestTrigger.Invoke"/>, the right-clicked/focused node otherwise. For Pointer/Keyboard/Hold
    /// the dispatcher sets <c>Source == Node</c>; for Invoke it is the activated button (below <see cref="Node"/> in
    /// the tree). A rect-anchored open (keyboard / invoke) anchors on <see cref="Source"/> so an Invoke opens against
    /// the button, not the row. Same reused-instance contract as <see cref="Node"/>: value-copy it, never hold the reference.</summary>
    public NodeHandle Source;
}

/// <summary>A keyboard accelerator chord (WinUI KeyboardAccelerator): <see cref="Key"/> + <see cref="Mods"/> invoke the
/// owning node's click handler from anywhere (dispatched after focused routing leaves the key unhandled).</summary>
public readonly record struct KeyAccelerator(int Key, KeyModifiers Mods);

/// <summary>Well-known virtual-key codes (Win32 VK_*) used by the input router.</summary>
public static class Keys
{
    public const int Back = 8;
    public const int Tab = 9;
    public const int Enter = 13;
    public const int Shift = 16, Ctrl = 17, Alt = 18;
    public const int Pause = 19, CapsLock = 20;
    public const int Escape = 27;
    public const int Space = 32;
    public const int PageUp = 33, PageDown = 34;
    public const int End = 35, Home = 36;
    public const int Left = 37, Up = 38, Right = 39, Down = 40;
    public const int PrintScreen = 44, Insert = 45, Delete = 46;
    // 0-9 (VK '0'..'9' == ASCII)
    public const int D0 = 48, D1 = 49, D2 = 50, D3 = 51, D4 = 52, D5 = 53, D6 = 54, D7 = 55, D8 = 56, D9 = 57;
    // A-Z (VK 'A'..'Z' == ASCII)
    public const int A = 65, B = 66, C = 67, D = 68, E = 69, F = 70, G = 71, H = 72, I = 73, J = 74, K = 75, L = 76,
                     M = 77, N = 78, O = 79, P = 80, Q = 81, R = 82, S = 83, T = 84, U = 85, V = 86, W = 87, X = 88,
                     Y = 89, Z = 90;
    public const int LeftWin = 91, RightWin = 92;
    /// <summary>The dedicated context-menu key (VK_APPS) — opens the focused element's context flyout.</summary>
    public const int Apps = 93;
    public const int F1 = 112, F2 = 113, F3 = 114, F4 = 115, F5 = 116, F6 = 117, F7 = 118, F8 = 119,
                     F9 = 120, F10 = 121, F11 = 122, F12 = 123;
    // Gamepad (VK_GAMEPAD_*) — translated by the dispatcher to activation/cancel/XY-focus.
    public const int GamepadA = 195, GamepadB = 196, GamepadX = 197, GamepadY = 198;
    public const int GamepadDPadUp = 203, GamepadDPadDown = 204, GamepadDPadLeft = 205, GamepadDPadRight = 206;
    public const int GamepadLeftThumbUp = 211, GamepadLeftThumbDown = 212, GamepadLeftThumbRight = 213, GamepadLeftThumbLeft = 214;

    /// <summary>True for VK 'A'..'Z' / '0'..'9' — the access-key (Alt mnemonic) candidates.</summary>
    public static bool IsAccessKeyCandidate(int vk) => (vk >= A && vk <= Z) || (vk >= D0 && vk <= D9);
}

/// <summary>The semantic outcome of an IN-APP drop (E5-L2 — the Flutter/SwiftUI model, never OLE): advisory — the
/// engine sets Move while over an accepting target, None otherwise; targets may refine it in OnEnter/OnOver.</summary>
public enum DropEffect : byte { None = 0, Move = 1, Copy = 2, Link = 3 }

/// <summary>
/// How a promoted drag treats the SOURCE node's visual (the E5 lift mode).
/// <list type="bullet">
/// <item><see cref="Ghost"/> (default, byte-identical to the pre-chip engine) — the source row IS the drag visual: the
/// controller translates it, dims it, gives it a lifted shadow and hoists its subtree into the recorder's unclipped
/// <c>DragGhost</c> top band.</item>
/// <item><see cref="Stationary"/> — the source row STAYS in its slot and is only dimmed + made hit-test-transparent;
/// the drag visual is an independent compact chip drawn by a <c>DragPreviewLayer</c> in the
/// <see cref="FluentGpu.Scene.SceneStore.DragOverlay"/> band. No translate, no shadow, no ghost hoist — so the whole
/// ghost-band cost and its clipping/blend hazards are bypassed, and the gesture SURVIVES the source being virtualized
/// away (the chip, not the row, carries it).</item>
/// </list>
/// </summary>
public enum DragLift : byte { Ghost = 0, Stationary = 1 }

/// <summary>
/// Styling for the lifted drag GHOST (the moving visual of a dragged node) — the per-source override of the engine's
/// hardcoded defaults (opacity 0.80 + a flyout-class shadow). All knobs are optional; an object initializer is safe
/// (the parameterless ctor seeds the Fluent defaults). Carried on <see cref="DragSource.Style"/>; null there ⇒ default.
/// </summary>
public readonly record struct DragVisualStyle
{
    public DragVisualStyle() { }
    /// <summary>Ghost opacity (WinUI ListViewItemDragThemeOpacity = 0.80 default). In
    /// <see cref="DragLift.Stationary"/> this is the SOURCE row's dim (the Atlassian 0.4 "it's in the chip" cue).</summary>
    public float Opacity { get; init; } = 0.80f;
    /// <summary>Ghost drop shadow; null ⇒ the engine's default flyout-depth shadow. Ignored under
    /// <see cref="DragLift.Stationary"/> (a stationary source is never lifted).</summary>
    public ShadowSpec? Shadow { get; init; } = null;
    /// <summary>Uniform scale about the ghost's center (1 = none; e.g. 1.03 for a subtle "lift"). Ignored under
    /// <see cref="DragLift.Stationary"/>.</summary>
    public float Scale { get; init; } = 1f;
    /// <summary>Lift mode — see <see cref="DragLift"/>. Default <see cref="DragLift.Ghost"/>, so an unstyled or
    /// partially-styled source keeps the historical behavior exactly.</summary>
    public DragLift Lift { get; init; } = DragLift.Ghost;
    /// <summary>Ghost-mode only: an OPAQUE plate filled beneath the lifted subtree (inside its opacity group, using the
    /// ghost node's own corner radii). A list row with a transparent fill (plain/zebra rows) otherwise lets the content
    /// under the ghost read straight THROUGH its text — the S3 "both texts fully legible → garbage" failure. Null (the
    /// default) = no plate.</summary>
    public ColorF? Backplate { get; init; } = null;
    /// <summary>The engine default (opacity 0.80, default shadow, no scale, Ghost lift, no backplate).</summary>
    public static readonly DragVisualStyle Default = new();
}

/// <summary>
/// E5-L2 typed drag SOURCE spec (<c>BoxEl.Draggable</c> — the Flutter Draggable / react-beautiful-dnd model; user
/// ruling 2026-06-10: deliberately NOT WinUI's OLE DataPackage/DoDragDrop modal loop): <paramref name="Kind"/> is a
/// string discriminator so target accept-tests are cast-free; <paramref name="PayloadFactory"/> resolves the typed
/// payload ONCE when the L1 press promotes past the drag box (never per move). Trimming-safe: plain delegates.
/// </summary>
public sealed record DragSource(string Kind, Func<object?> PayloadFactory)
{
    /// <summary>Optional ghost styling (opacity/shadow/scale) for the lifted drag visual; null ⇒ the engine default
    /// (opacity 0.80 + flyout shadow). For a fully CUSTOM floating preview (a card/badge unrelated to the dragged
    /// node), use a <c>DragPreviewLayer</c> keyed on the live drag <see cref="DragState"/> instead.</summary>
    public DragVisualStyle? Style { get; init; }
}

/// <summary>Optional app-wide treatment for compatible destinations during a typed drag. Spotlight targets remain at
/// their authored opacity while ordinary content is deemphasized; discovery and hit testing are unchanged.</summary>
public enum DropTargetVisualPolicy : byte
{
    None,
    Spotlight,
}

/// <summary>Named drag presentation values. They live beside the drag contracts so recorder and controls share one
/// vocabulary instead of hand-authoring unrelated opacity values.</summary>
public static class DragVisualTok
{
    /// <summary>The drop-spotlight SCRIM colour — an explicit band the recorder paints over the app while a drag has
    /// compatible spotlight destinations, with a rounded cutout per destination (gpu-renderer.md §7.4). Opaque black:
    /// the scrim's strength is <see cref="ScrimOpacity"/>, applied ONCE as the band's opacity-group alpha (the colour is
    /// filled at alpha 1 inside the group so the cutouts erase cleanly).
    /// <para>Deliberately ONE constant, not a theme pair: the recorder is theme-blind (its only colour inputs are the
    /// focus/scrollbar/text-edit styles the host hands it per frame), so a light-vs-dark scrim would need a new host-
    /// plumbed colour on <c>SceneRecorder.Record</c>. Black at <see cref="ScrimOpacity"/> reads correctly on the dark
    /// theme this app ships; a light-theme softening is a known residual, not a silent approximation.</para></summary>
    public static readonly ColorF ScrimColor = ColorF.FromRgba(0x00, 0x00, 0x00, 0xFF);

    /// <summary>How dark the drop-spotlight scrim is (the band's opacity-group alpha). Replaces the old
    /// <c>SpotlightBackgroundOpacity</c> multiply/divide hack, which faded the whole app to 0.28 of its authored alpha —
    /// a comparable visual weight, but achieved by mutating every node's opacity (and un-mutating the targets' again,
    /// which double-lit any translucent target).</summary>
    public const float ScrimOpacity = 0.55f;
}

/// <summary>
/// E5-L2 drop TARGET spec (<c>BoxEl.DropTarget</c> — Flutter DragTarget / SwiftUI dropDestination): receives sessions
/// whose Kind is in <see cref="AcceptKinds"/>. Discovery is hit-test-chain based — per pointer move the engine picks
/// the NEAREST enabled accepting target under the pointer (a non-accepting target never blocks an accepting
/// ancestor). OnEnter/OnLeave fire on hover transitions, OnOver every move while inside, OnDrop on release over it
/// (BEFORE the L1 completion). The <see cref="DragSession"/> argument is THE one live reused instance — copy what
/// you keep.
/// </summary>
public sealed record DropTargetSpec(
    string[] AcceptKinds,
    Action<DragSession>? OnEnter = null,
    Action<DragSession>? OnOver = null,
    Action<DragSession>? OnLeave = null,
    Action<DragSession>? OnDrop = null)
{
    /// <summary>Optional payload/session capability gate evaluated after the cheap kind match. False makes this target
    /// transparent so discovery may continue to a compatible ancestor; it never receives Enter/Over/Drop.</summary>
    public Func<DragSession, bool>? CanAccept { get; init; }

    /// <summary>Keep the L1 drop-settle glide after OnDrop (reorder targets — the commit's FLIP retarget turns it
    /// into the glide-into-the-new-slot motion). False (default) = the drop suppresses the spring-back and the
    /// source visual snaps home: the "deposited" feel of a foreign-surface drop.</summary>
    public bool SettleOnDrop { get; init; }

    /// <summary>Opt this destination into compatible-target spotlighting for the duration of a drag.</summary>
    public DropTargetVisualPolicy VisualPolicy { get; init; }

    /// <summary>Per-SESSION spotlight policy: when set and it returns false for the live session, this target does not
    /// participate in the drag scrim even though its <see cref="VisualPolicy"/> opts in — so a same-list reorder never
    /// dims the app it is reordering inside. Null (default) = the <see cref="VisualPolicy"/> decides alone. Evaluated
    /// on the cold refresh edge (<c>SceneStore.RefreshDropSpotlight</c>), never during record; a session with no
    /// surviving spotlight destination emits no scrim band at all.</summary>
    public Func<DragSession, bool>? SpotlightWhen { get; init; }

    /// <summary>The REFUSAL cue: why this target — which matched the session's <see cref="AcceptKinds"/> — said no.
    /// A target refusing through <see cref="CanAccept"/> is deliberately transparent (discovery continues to an
    /// accepting ancestor), so it never becomes <c>OverTarget</c> and none of its handlers fire: without this seam a
    /// refusal is indistinguishable from empty space and reads as "drag &amp; drop is broken". When NOTHING on the chain
    /// accepts, the engine publishes the NEAREST kind-matched refuser as <see cref="DragSession.RefusedTarget"/> and
    /// this delegate's text as the session <see cref="DragSession.Caption"/>, which the chip renders beside its
    /// not-allowed glyph ("Clear sorting to reorder"). Null ⇒ the glyph alone. Called per move while refused — return a
    /// cached/constant string where the reason cannot change mid-gesture.</summary>
    public Func<DragSession, string?>? RefusalCaption { get; init; }

    /// <summary>SPRING-LOADING (the macOS Finder / WinUI ~500ms dwell convention): hold a compatible drag still over
    /// this surface for this many milliseconds and <see cref="OnSpringLoad"/> fires ONCE — the container opens itself so
    /// the user can keep going without dropping first (a collapsed folder expands, a tab navigates). 0 (default) = off.
    /// <para>The dwell accumulates on the NEAREST kind-matched target that configures it, whether that target accepts
    /// the payload, refuses it through <see cref="CanAccept"/>, or is a pure <see cref="SpringLoadOnly"/> waypoint — a
    /// spring-load is a NAVIGATION affordance, not a drop, so tying it to acceptance would make exactly the surfaces
    /// that need it (a folder you cannot drop INTO, a tab that takes no deposit) unable to have it. It re-arms only
    /// after the pointer leaves and re-enters; small movements inside the target do not reset it.</para></summary>
    public float SpringLoadDelayMs { get; init; }

    /// <summary>Fired once per Enter after <see cref="SpringLoadDelayMs"/> of dwell (see there). The session is THE live
    /// reused instance — copy what you keep. Null ⇒ no spring-load regardless of the delay.</summary>
    public Action<DragSession>? OnSpringLoad { get; init; }

    /// <summary>This surface is a spring-load WAYPOINT, never a drop destination: it is skipped for acceptance AND for
    /// the refusal cue, so hovering it neither accepts nor accuses — only its <see cref="OnSpringLoad"/> can fire.
    /// <para>The Finder tab-bar shape. Without it a "hold to navigate" surface has to pretend: either accept and
    /// silently no-op the drop (the failure mode this whole contract exists to kill), or refuse and wear a not-allowed
    /// glyph while the drag merely PASSES OVER it on the way somewhere real.</para></summary>
    public bool SpringLoadOnly { get; init; }

    /// <summary>Ordinal accept test over <see cref="AcceptKinds"/> (cast-free, 0-alloc).</summary>
    public bool Accepts(string kind)
    {
        var kinds = AcceptKinds;
        for (int i = 0; i < kinds.Length; i++)
            if (string.Equals(kinds[i], kind, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>
/// THE live drag session (E5-L2): ONE mutable instance owned by <c>Input.DragDropContext</c>, opened when an L1 drag
/// promotes on a chain carrying a <see cref="DragSource"/> (payload resolved once), updated per pointer move, handed
/// to every <see cref="DropTargetSpec"/> handler, and cleared (incl. the Payload GC edge) when the gesture ends.
/// Handlers copy what they keep — never hold the reference across gestures.
/// </summary>
public sealed class DragSession
{
    /// <summary>The typed payload (resolved once at promotion from <see cref="DragSource.PayloadFactory"/>).</summary>
    public object? Payload;
    /// <summary>The source's kind discriminator (accept tests are string compares, never casts).</summary>
    public string Kind = "";
    /// <summary>Pointer position, window space.</summary>
    public Point2 Position;
    /// <summary>Smoothed pointer velocity (px/s, ~50ms EMA — the L1 gesture's velocity).</summary>
    public float VelocityX, VelocityY;
    /// <summary>The node carrying the matched <see cref="DragSource"/>.</summary>
    public NodeHandle Source;
    /// <summary>The accepting target currently under the pointer (Null when over nothing that accepts).</summary>
    public NodeHandle OverTarget;
    /// <summary>The nearest target under the pointer that MATCHED this session's Kind but refused through
    /// <see cref="DropTargetSpec.CanAccept"/> — published ONLY while nothing on the chain accepts (an accepting
    /// ancestor means the drop still succeeds, so there is nothing to refuse). Null over empty space and over an
    /// accepting target: that is exactly the distinction the chip's not-allowed glyph needs, since
    /// <see cref="Effect"/> is <see cref="DropEffect.None"/> in BOTH the refusal and the over-nothing case.</summary>
    public NodeHandle RefusedTarget;
    /// <summary>Advisory effect (engine: Move while over an accepting target, None otherwise; targets may refine).</summary>
    public DropEffect Effect;
    /// <summary>Optional drop CAPTION (the WinUI <c>DragUIOverride.Caption</c> analogue — "Add 3 tracks to Chill"):
    /// targets set it in <c>OnEnter</c>/<c>OnOver</c>; the engine clears it on every target CHANGE and at session end,
    /// so a target never has to unset it. Surfaced to the preview chip through <see cref="DragState.Caption"/>.</summary>
    public string? Caption;
    public KeyModifiers Mods;
    public PointerKind Pointer;
}

/// <summary>The drop-settle window a <c>DragPreviewLayer</c> chip animates through after a
/// <see cref="DragLift.Stationary"/> gesture ends (rbd's "nothing ever teleports"): <see cref="ToTarget"/> = an
/// accepted drop (glide into the drop point and fade), <see cref="Home"/> = a refusal/cancel (glide back to the source
/// row's resting rect). <see cref="None"/> = no settle in flight.</summary>
public enum DragSettlePhase : byte { None = 0, ToTarget = 1, Home = 2 }

/// <summary>
/// A reactive, copied SNAPSHOT of the live drag (the value <c>UseDragState()</c> returns) — safe to hold across a
/// render, unlike the mutable <see cref="DragSession"/>. A component re-renders when any of these change (drag
/// begin/move/end), so a <c>DragPreviewLayer</c> can render a custom floating preview that follows the cursor. When
/// no drag is active, <see cref="Active"/> is false and the rest are default.
/// </summary>
/// <param name="Active">A typed drag is in flight.</param>
/// <param name="Kind">The drag source's kind discriminator (<see cref="DropKinds.Files"/> for an OS file drag).</param>
/// <param name="Position">The pointer in window (DIP) space.</param>
/// <param name="Payload">The drag payload (<see cref="FileDropData"/> for OS files; the source's typed payload otherwise).</param>
/// <param name="Effect">The live advisory <see cref="DropEffect"/> — <see cref="DropEffect.None"/> while over nothing
/// that accepts AND while over a target that refused, which is why the not-allowed cue reads <paramref name="Refused"/>
/// instead: only the latter is a refusal the user needs told about.</param>
/// <param name="Caption">The current target's drop caption (<see cref="DragSession.Caption"/>), or — while
/// <paramref name="Refused"/> — the refuser's <see cref="DropTargetSpec.RefusalCaption"/>. Null when neither applies.</param>
/// <param name="Settle">Non-<see cref="DragSettlePhase.None"/> only during the ~250ms post-gesture settle window a
/// Stationary-lift drag publishes; <see cref="Active"/> stays true across it so the chip can animate out.</param>
/// <param name="SettleTarget">Where the chip settles TO: the drop point (ToTarget) or the source's resting rect (Home).</param>
/// <param name="Refused">A compatible-KIND target under the pointer explicitly refused this payload
/// (<see cref="DragSession.RefusedTarget"/>) — the one state a not-allowed cue belongs in. False over empty space, so
/// hovering nothing stays silent rather than accusing every gap between targets of refusing.</param>
public readonly record struct DragState(
    bool Active, string Kind, Point2 Position, object? Payload,
    DropEffect Effect = DropEffect.None, string? Caption = null,
    DragSettlePhase Settle = DragSettlePhase.None, RectF SettleTarget = default,
    bool Refused = false);

/// <summary>
/// Well-known drag KIND discriminators for OS-originated (OLE) drags delivered through the external-drop seam
/// (the host's <c>IDropTarget</c> → <c>InputHooks.ExternalDrag*</c> → <c>InputDispatcher</c> → <c>DragDropContext</c>).
/// A <see cref="DropTargetSpec"/> that lists one of these in its <c>AcceptKinds</c> receives FOREIGN-surface drops
/// exactly like an in-app drag — the engine never special-cases OS drags past the seam: they open a normal
/// <see cref="DragSession"/> (Source = the scene root) whose <c>Payload</c> carries the typed data below.
/// </summary>
public static class DropKinds
{
    /// <summary>An OS file/folder drop (Explorer, the desktop, any OLE source offering <c>CF_HDROP</c>).
    /// <see cref="DragSession.Payload"/> is a <see cref="FileDropData"/>.</summary>
    public const string Files = "os.files";
}

/// <summary>
/// The payload of a <see cref="DropKinds.Files"/> session: the absolute paths the OS handed us (files AND/OR folders,
/// in the source's order). Read it in a target's <c>OnOver</c>/<c>OnDrop</c> via <c>(FileDropData)session.Payload</c>.
/// Allocated once per OS drag (a cold OLE edge, never per frame); folders arrive as-is — the receiver decides whether
/// to recurse.
/// </summary>
public sealed class FileDropData
{
    public FileDropData(string[] paths) => Paths = paths;
    /// <summary>The dropped absolute paths (files and folders intermixed, OLE order).</summary>
    public string[] Paths { get; }
    public int Count => Paths.Length;
    /// <summary>True when every path is an existing directory (a pure folder drop) — a cheap receiver hint.</summary>
    public bool AllFolders
    {
        get
        {
            var p = Paths;
            if (p.Length == 0) return false;
            for (int i = 0; i < p.Length; i++)
                if (!System.IO.Directory.Exists(p[i])) return false;
            return true;
        }
    }
}
