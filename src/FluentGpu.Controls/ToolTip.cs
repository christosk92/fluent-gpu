using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>How the tooltip bubble is positioned — WinUI <c>PlacementMode</c> (the two modes ToolTipService actually
/// drives): <see cref="Top"/> = centered above the TARGET, flipping on a collision (the WinUI default,
/// ToolTip_Partial.cpp:1119-1122 "Fall back to the default - PlacementMode_Top"), the target rect inflated by the
/// input-mode offset — 20px mouse / 12px keyboard (<see cref="ToolTip.MousePlacementOffset"/>); <see cref="Mouse"/> =
/// at the last POINTER position, offset 11px below the cursor (ToolTip_Partial.h:56
/// <c>m_mousePlacementVerticalOffset = 11</c>; ToolTip_Partial.cpp:976-984 "align ToolTip with the bottom left corner
/// of mouse bounding rectangle").</summary>
public enum ToolTipPlacementMode : byte { Top, Mouse }

/// <summary>
/// A WinUI <c>ToolTip</c> (framework <c>ToolTip</c> + <c>ToolTipService</c>): wraps a target element and surfaces a small
/// text bubble anchored above it (or at the pointer in <see cref="ToolTipPlacementMode.Mouse"/>). 1:1 with the
/// framework's automatic-tooltip behavior:
/// <list type="bullet">
/// <item>Hover trigger — pointer-over the target opens the bubble after the initial show delay
///   (<c>SPI_GETMOUSEHOVERTIME</c> 400ms × 2 = 800ms; a re-show within <see cref="BetweenShowDelayMs"/> = 200ms uses the
///   reshow delay — see <see cref="MouseReshowDelayMs"/> for why that is 400ms, not the spec'd 600ms). Pointer-leave
///   cancels a PENDING open immediately (ToolTipService_Partial.cpp:1435-1442); an OPEN bubble is kept and closed by
///   the safe-zone monitor instead — see the safe-zone item below.</item>
/// <item>Safe zone — an open bubble does NOT close the instant the pointer leaves the target: WinUI keeps it while the
///   pointer is inside owner ∪ tooltip (∪ their convex hull) and a 1s check timer closes it once outside
///   (ToolTipService_Partial.cpp:1433-1453 owner-exit only records the owner; cpp:349-381 OnSafeZoneCheck;
///   cpp:1060-1098 IsToolTipInSafeZone; .h:22 <c>s_safeZoneCheckTimerDuration</c> = 1s). Implemented against the REAL
///   pointer (<c>InputHooks.GetPointerPosition</c>): owner-exit arms the 1s <see cref="SafeZoneCheckMs"/> poll, and each
///   elapse geometry-tests the live pointer against owner ∪ bubble — inside keeps polling, one full interval outside
///   closes. The bubble itself is hit-test-INVISIBLE (a real tooltip never intercepts pointer or wheel input — the
///   hover-handler approximation this replaces made the bubble swallow wheel scrolling under it), so bubble-hover is
///   detected by geometry, never by events. The 5s dwell stays authoritative while parked on the bubble.</item>
/// <item>Keyboard trigger — KEYBOARD focus landing inside the target opens the bubble after 800ms (×2, normal AND
///   reshow: ToolTipService_Partial.cpp:1777-1779), exactly WinUI's OnOwnerGotFocus gate
///   (ToolTipService_Partial.cpp:1648-1664: only <c>FocusState::Keyboard</c> shows the tooltip — pointer focus does
///   not). Focus leaving closes it (cpp:1696-1706 OnOwnerLostFocus → OnOwnerLeaveInternal).</item>
/// <item>Press dismiss — a pointer press over the target closes an open (or pending) bubble, and it stays closed
///   until the pointer LEAVES and re-enters (classic tooltip behavior; WinUI 3's ToolTipService itself registers no
///   PointerPressed handler — it closes via safe-zone exit monitoring, ToolTipService_Partial.cpp:1437-1453 — but
///   cannot re-open a dismissed owner until a real leave + re-enter either, cpp:725-737 CancelAutomaticToolTip).</item>
/// <item>Auto-dismiss — an open bubble closes itself after <see cref="ShowDurationMs"/> (<c>SPI_GETMESSAGEDURATION</c>
///   default 5s), and the dismissal LATCHES like press dismiss: WinUI's only show trigger is PointerEntered
///   (ToolTipService_Partial.cpp:1395-1418), so in-place hover moves can never re-open a timed-out tooltip — it takes
///   a real leave + re-enter.</item>
/// <item>No click trigger — the wrapper adds NO OnClick (ToolTipService registers none,
///   ToolTipService_Partial.cpp:176-220), so a tooltipped element is never a tab stop by itself.</item>
/// </list>
/// Chrome matches <c>ToolTip_themeresources.xaml</c>: AcrylicInAppFillColorDefault fill, 1px SurfaceStrokeColorFlyout
/// stroke, ControlCornerRadius (4px) corners, the light tooltip elevation (<see cref="Elevation.Tooltip"/>), 9,6,9,8
/// padding, 12px TextFillColorPrimary text, MaxWidth 320, TextWrapping=Wrap. Open/close are the WinUI
/// FadeIn/FadeOutThemeAnimation fades (167ms — <see cref="PopupChrome.Raw"/> in the overlay host). The bubble does NOT
/// trap focus and does NOT light-dismiss on outside click (<see cref="DismissBehavior.None"/>) — dismissal is owned by
/// hover-leave/focus-leave/press + the auto-dismiss timer, exactly like <c>ToolTipService</c>.
/// </summary>
public sealed class ToolTip : Component
{
    // Template parts (see TemplateParts). The part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those). The hover/focus/press trigger wrapper around Target is NOT
    // a part — its handlers ARE the ToolTipService mechanics.
    /// <summary>The text bubble surface (the control-built chrome inside the raw overlay host — acrylic fill, flyout
    /// stroke, 4px corners, tooltip elevation, 9,6,9,8 padding). Owned: Children (the <see cref="Text"/> content),
    /// hit-test invisibility (a tooltip never intercepts input), and the realized-node capture (safe-zone geometry).</summary>
    public const string PartBubble = "Bubble";

    public Element Target = new BoxEl();
    public string Text = "";
    /// <inheritdoc cref="Wrap(Element, string, float, float)"/>
    public float Grow;
    /// <inheritdoc cref="Wrap(Element, string, float, float)"/>
    public float ShowDelayMs = float.NaN;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real tooltip after first mount
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary>WinUI <c>ToolTip.Placement</c> (Top/Mouse subset). Default <see cref="ToolTipPlacementMode.Top"/> —
    /// the WinUI default (ToolTip_Partial.cpp:1119-1122).</summary>
    public ToolTipPlacementMode Placement = ToolTipPlacementMode.Top;

    // ── ToolTipService timing constants (ToolTipService_Partial.h / .cpp). ────────────────────────────────────────────
    /// <summary><c>SPI_GETMOUSEHOVERTIME</c> default (DEFAULT_SPI_GETMOUSEHOVERTIME, ToolTipService_Partial.h:18) = 400ms.</summary>
    public const float MouseHoverTimeMs = 400f;
    /// <summary>Mouse initial show delay: hover time × 2 = 800ms (GetInitialShowDelay, Mouse, first show —
    /// ToolTipService_Partial.cpp:1774-1775).</summary>
    public const float MouseShowDelayMs = MouseHoverTimeMs * 2f;
    /// <summary>Mouse RE-show delay = 400ms (× 1), NOT the spec-comment's × 1.5 = 600ms: the shipping code is
    /// <c>ticks *= static_cast&lt;INT64&gt;(isReshow ? 1.5 : 2)</c> (ToolTipService_Partial.cpp:1775) and the C++
    /// <c>static_cast&lt;INT64&gt;(1.5)</c> TRUNCATES to 1 — behavior parity follows the compiled code, not the
    /// comment table at cpp:1737-1742.</summary>
    public const float MouseReshowDelayMs = MouseHoverTimeMs * 1f;
    /// <summary>Keyboard show delay: hover time × 2 = 800ms for BOTH normal and reshow
    /// (ToolTipService_Partial.cpp:1777-1779 — Keyboard ignores isReshow).</summary>
    public const float KeyboardShowDelayMs = MouseHoverTimeMs * 2f;
    /// <summary>BETWEEN_SHOW_DELAY_MS = 200ms (ToolTipService_Partial.h:17) — a re-open inside this window of the last
    /// close uses the reshow delay (cpp:659 <c>GetTickCount() - s_lastToolTipOpenedTime &lt; BETWEEN_SHOW_DELAY_MS</c>).</summary>
    public const float BetweenShowDelayMs = 200f;
    /// <summary>DEFAULT_SHOW_DURATION_SECONDS = 5s — auto-dismiss after this long open.</summary>
    public const float ShowDurationMs = 5000f;
    /// <summary>Pointer-mode vertical offset below the cursor — ToolTip_Partial.h:56
    /// <c>m_mousePlacementVerticalOffset = 11</c> (the brief's "14px" did not survive source verification).</summary>
    public const float MousePlacementVerticalOffset = 11f;
    /// <summary><c>DEFAULT_MOUSE_OFFSET</c> = 20 (ToolTip_Partial.h:11): target-mode placement inflates the dock rect
    /// by this on BOTH axes for a mouse-opened automatic tooltip before positioning (ToolTip_Partial.cpp:1224-1258
    /// <c>InflateRect(&amp;rcDockTo, horizontalOffset, verticalOffset)</c> + :1275).</summary>
    public const float MousePlacementOffset = 20f;
    /// <summary><c>DEFAULT_KEYBOARD_OFFSET</c> = 12 (ToolTip_Partial.h:10) — the dock-rect inflation when the tooltip
    /// was opened by keyboard focus.</summary>
    public const float KeyboardPlacementOffset = 12f;
    /// <summary><c>s_safeZoneCheckTimerDuration</c> = 1s (ToolTipService_Partial.h:22) — the safe-zone poll cadence;
    /// the bubble closes once the pointer has been outside owner ∪ bubble for one full check interval.</summary>
    public const float SafeZoneCheckMs = 1000f;

    // ── ONE bubble at a time (WinUI's s_pToolTipServiceMetadata single open tooltip) ─────────────────────────────────
    // WinUI's ToolTipService keeps ONE automatic tooltip open per thread and closes it before opening the next
    // (ToolTipService_Partial.cpp CloseAutomaticToolTip / s_tpCurrentToolTip). We had no such rule: every wrapper owned
    // an independent timer and an independent overlay entry, so a pointer sweeping a row of small targets could leave
    // several bubbles on screen at once — each one still inside its own 5s dwell or its 1s safe-zone grace while the
    // next had already opened. Two tooltips are never right (there is one pointer and one question), so this is a
    // blanket rule rather than an opt-in.
    //
    // The owner is the COMPONENT INSTANCE (stable across re-renders — props are re-pushed, the component is reused) and
    // the closer is that render's CloseNow, whose captured cells are the same UseRef/UseSignal cells whatever render
    // installed it. UI thread only, like every other field the reconciler touches.
    private static ToolTip? s_openOwner;
    private static Action? s_openCloser;

    /// <summary>Close whichever bubble is open, if any — the host's hook for a moment the pointer cannot signal. A
    /// kept-alive page (Flow.KeepAlive parks it, nothing unmounts) that navigates away under a STILL pointer leaves its
    /// owner both mounted and geometrically under the cursor: no leave edge fires, the safe-zone poll finds the owner
    /// rect still "inside", and the bubble outlives the page it described until the 5s dwell (recording 2026-09-16: a
    /// drawer's "Go to album" tip parked over the album page it opened). The app calls this on every route commit.
    /// Idempotent; UI thread only. Runs the SAME closer a competing owner would (<see cref="s_openCloser"/>), so the
    /// re-show window and the single-bubble latch stay consistent.</summary>
    public static void CloseOpen() => s_openCloser?.Invoke();

    /// <summary>LIVE target+text slots RE-PUSHED to the core (<c>Embed.Comp(slots, …)</c>; the SelectorBar/RadioButtons
    /// idiom). <see cref="Target"/> and <see cref="Text"/> are plain fields, so via a propless <c>Embed.Comp</c> they
    /// freeze at first mount — a re-rendering parent's new wrapped element or new tooltip text would be silently
    /// dropped (the toggle-tooltip staleness bug). <see cref="Wrap"/> routes them through re-pushed props instead; when
    /// present they WIN over the fields and the ToolTip re-renders reactively (props are signal-backed). Read with
    /// <c>UsePropsOrDefault</c>.</summary>
    public sealed record ToolTipSlots(Element Target, string Text, float Grow = 0f, float ShowDelayMs = float.NaN)
    {
        // The compiler-generated record equality walks the whole wrapped Element tree, field by field, EVERY time the
        // parent re-pushes props — and the delivery seam compares props on every parent render. For a shell that wraps
        // dozens of targets that deep walk is the single hottest thing in a reconcile flush, and it decides nothing:
        // Element is immutable, so a rebuilt target is a different instance whose subtree could only compare equal by
        // accident. Comparing the target by REFERENCE is therefore both far cheaper and semantics-preserving in the
        // safe direction — a rebuilt target is unequal, so it still re-renders; only a genuinely identical instance
        // (the parent handed back the same element) short-circuits.
        public bool Equals(ToolTipSlots? other)
            => other is not null && ReferenceEquals(Target, other.Target) && Text == other.Text && Grow == other.Grow
               // float.Equals, not ==: the default is NaN ("use the service delay") and NaN == NaN is false, so a plain
               // == would report every default-delay re-push as a CHANGE and re-render every tooltip in the shell.
               && ShowDelayMs.Equals(other.ShowDelayMs);

        public override int GetHashCode()
            => HashCode.Combine(RuntimeHelpers.GetHashCode(Target), Text, Grow, ShowDelayMs);
    }

    /// <summary>DEFERRED target slots: the wrapped element as a FACTORY instead of a built tree, so a parent that
    /// re-renders every frame stops re-rendering its tooltips.
    ///
    /// <para><see cref="ToolTipSlots"/> already compares the target by reference, but a parent that rebuilds its
    /// children each render hands over a NEW instance every time, so the short-circuit never fires and every wrapped
    /// target re-renders its ToolTip core — with dozens of tooltips in one shell (a sidebar rail, a command bar) that is
    /// a measurable share of an idle reconcile flush. A <b>mount-stable</b> factory delegate is reference-equal across
    /// renders, so the whole re-push collapses to one <c>ReferenceEquals</c> + a string compare.</para>
    ///
    /// <para>The factory is invoked INSIDE the ToolTip's own render, which is what makes this safe rather than stale:
    /// any signal it reads subscribes the ToolTip, so live data still reaches the target with no re-push at all
    /// (component-props-contract.md — a frozen VALUE would be the bug; a delegate re-read each render is the fix).</para></summary>
    public sealed record ToolTipStableSlots(Func<Element> Target, string Text, float Grow = 0f, float ShowDelayMs = float.NaN)
    {
        // Identity on the FACTORY, exactly like ToolTipSlots' reference compare on the built element — a delegate has no
        // meaningful value equality, and two lambdas with the same body are still different instances.
        public bool Equals(ToolTipStableSlots? other)
            => other is not null && ReferenceEquals(Target, other.Target) && Text == other.Text && Grow == other.Grow
               && ShowDelayMs.Equals(other.ShowDelayMs);   // see ToolTipSlots.Equals for why float.Equals

        public override int GetHashCode()
            => HashCode.Combine(RuntimeHelpers.GetHashCode(Target), Text, Grow, ShowDelayMs);
    }

    /// <summary>Wrap <paramref name="target"/> with the hover/focus/press tooltip mechanics.
    ///
    /// <para><paramref name="grow"/> is the FILL opt-in (the kit's usual <c>grow:</c> spelling — cf.
    /// <c>ItemsView.List</c>, <c>Responsive.Of</c>, <c>AutoSuggestBox.Create</c>): 0, the default, is exactly today's
    /// behaviour at every existing call site. Above 0 it makes the wrap layout-transparent on the MAIN axis as well as
    /// the cross one — see the wrapper's own comment in <c>Render</c> for the bug it exists to cure (a wrapped ROW in a
    /// column shrink-wrapped to its own title, so the sidebar's track / missing / unavailable rows painted narrower
    /// fill plates than their unwrapped neighbours). Use it only where the target genuinely owns its parent's width; a
    /// target that declares its own <c>Width</c> is left alone regardless.</para>
    ///
    /// <para><paramref name="showDelayMs"/> is the PER-ELEMENT initial-show override — WinUI's
    /// <c>ToolTipService.InitialShowDelay</c> attached property, which is likewise per-element and likewise defaults to
    /// the service value. <c>NaN</c> (the default) is exactly today's behaviour at every existing call site: the mouse
    /// delay ladder, <see cref="MouseShowDelayMs"/> normally and <see cref="MouseReshowDelayMs"/> inside the
    /// <see cref="BetweenShowDelayMs"/> re-show window. A number replaces BOTH mouse legs — a surface that wants an
    /// instant bubble wants it on the FIRST hover too, not only on the re-show. It does NOT touch
    /// <see cref="KeyboardShowDelayMs"/>: a focus-driven tooltip firing the instant Tab lands would strobe down a tab
    /// order, and WinUI keeps the keyboard leg on its own clock for the same reason
    /// (ToolTipService_Partial.cpp:1777-1779). 0 is honoured literally, and literally means "the next frame": the
    /// countdown is <see cref="ToolTipClock"/>, a host one-shot timer armed during the render that mounts it and
    /// drained at the top of the next frame.</summary>
    public static Element Wrap(Element target, string text, float grow = 0f, float showDelayMs = float.NaN)
        => Embed.Comp(new ToolTipSlots(target, text, grow, showDelayMs), () => new ToolTip());

    /// <summary>BOUND text form (Operation ultra-fast GPU engine, P3 — for a virtualized row: one <see cref="Prop{T}"/>
    /// read per render instead of a rebuilt call-site string). Null/empty resolves to NO TOOLTIP: <see cref="Render"/>
    /// returns <paramref name="target"/> alone that render — no hover/focus/press/safe-zone wiring mounted — and the
    /// SAME reused <c>ToolTip</c> component (re-pushed props via <c>Embed.Comp</c>, unchanged from every other
    /// <see cref="Wrap"/> overload) re-evaluates every render, so a later non-empty value lights the tooltip up with no
    /// remount either way. This does not eliminate the one <c>ToolTip</c> component instance per wrapped target (the
    /// plan's "no component per target" is aspirational for a future hover-time-lookup service — not implemented this
    /// phase); what it removes is the PER-ROW STRING rebuild and the empty-tooltip wiring cost.</summary>
    public static Element Wrap(Element target, Prop<string?> text, float grow = 0f, float showDelayMs = float.NaN)
        => Embed.Comp(new ToolTipBoundSlots(target, text, grow, showDelayMs), () => new ToolTip());

    /// <summary>Live target + BOUND text slot for <see cref="Wrap(Element, Prop{string}, float, float)"/>. Compared by
    /// target reference (see <see cref="ToolTipSlots"/>'s comment) plus the <see cref="Prop{T}"/> payload's own value
    /// equality (bind identity for a bound Text, plain value equality for a static one).</summary>
    public sealed record ToolTipBoundSlots(Element Target, Prop<string?> Text, float Grow = 0f, float ShowDelayMs = float.NaN)
    {
        public bool Equals(ToolTipBoundSlots? other)
            => other is not null && ReferenceEquals(Target, other.Target) && Text.Equals(other.Text) && Grow == other.Grow
               && ShowDelayMs.Equals(other.ShowDelayMs);

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Target), Text, Grow, ShowDelayMs);
    }

    /// <summary>Wrap a target that is built LAZILY, inside the ToolTip's render — the churn-free form of
    /// <see cref="Wrap"/> (see <see cref="ToolTipStableSlots"/>).
    ///
    /// <para><b><paramref name="target"/> must be MOUNT-STABLE</b>: a method group, a cached field, or a delegate held
    /// in a <c>UseMemo</c>/<c>UseRef</c>. A lambda allocated per render is a fresh instance every time, which makes the
    /// props compare unequal and reintroduces exactly the churn this overload exists to remove (it stays CORRECT — just
    /// pointless). The factory runs on every ToolTip render, so it must be cheap and side-effect-free.</para></summary>
    public static Element WrapStable(Func<Element> target, string text, float grow = 0f, float showDelayMs = float.NaN)
        => Embed.Comp(new ToolTipStableSlots(target, text, grow, showDelayMs), () => new ToolTip());

    // ── Per-mount state. Instance fields, not hook cells: the component instance IS the mount, so these live exactly as
    // long as the cells did, and the handlers are built once per mount instead of once per render. A handler handed to the
    // wrapper box every render was a fresh delegate each time (a dozen allocations per ToolTip render) and made the diff
    // see "changed" handlers and rewrite the node's columns.
    IOverlayService _svc = NullOverlayService.Instance;      // re-read from context each render; the handlers use the latest
    InputHooks _hooks = InputHooks.Current.Default;
    readonly Ref<NodeHandle> _anchor = new(default);
    readonly Ref<OverlayHandle?> _h = new(null);
    readonly Ref<bool> _autoOpened = new(false);
    readonly Ref<Point2> _lastPointerLocal = new(default);   // last hover position (wrapper-local) → Mouse placement
    readonly Ref<bool> _dismissedUntilLeave = new(false);    // press- or timeout-dismissed: no re-open until leave + re-enter
    readonly Ref<NodeHandle> _bubbleNode = new(default);     // the open bubble's realized node → its rect joins the safe zone
    // The text the bubble shows, written each render. The bubble's content thunk is captured by the overlay ENTRY at open
    // time and re-invoked by OverlayHost every render (OverlayHost.cs:1251). Capturing the `text` LOCAL froze the bubble at
    // the text of whichever render opened it: a recycled owner (ToolTip.Wrap re-pushes props, so the component is reused,
    // not remounted) kept showing the previous entity's tooltip. A Ref written each render is not reactive — no
    // subscription, no backwards write — and the thunk reads the CURRENT value.
    readonly Ref<string> _textRef = new("");
    // The text the OPEN bubble was opened WITH — the owner's identity, as far as a bubble can observe it. Wrap
    // re-pushes props, so a rail/list row whose identity changes IN PLACE reuses this component instead of
    // remounting it, and both live thunks then quietly re-point: the content thunk reads _textRef, the anchor thunk
    // reads _anchor.Value, so an open bubble survives onto a DIFFERENT owner and re-anchors there, describing the
    // item that used to be under the pointer. A Ref (no subscription, no per-frame allocation) records what was
    // opened so the stranded-bubble guard below can close it the moment the two disagree. Text is the only identity
    // the wrapper actually has — the target Element is a new instance on every parent render and the wrapper's own
    // node handle is reused across the swap — so a live-updating bound text on ONE owner also closes the bubble;
    // that is the safe direction (a tooltip re-opens on the next hover; a wrong-row tooltip lies for 5s).
    readonly Ref<string> _openedText = new("");
    // TRANSIENT posture — set by the same per-element override that makes the bubble open immediately (see Wrap).
    // Instant show and instant hide are one decision, not two: a bubble that appears the moment the pointer lands
    // is a DATA TIP (a sparkline bar's week, a blend slice's share) rather than a delayed reminder about a button.
    // A data tip is never travelled into, so it must not keep the safe-zone's 1s grace on the way out — with the
    // 800ms delay gone, that grace is what leaves four or five bubbles stacked behind a fast sweep. Held in a Ref
    // because the leave handler must read the CURRENT posture, not whichever render happened to install it.
    readonly Ref<bool> _transient = new(false);
    readonly Ref<long> _openedAtMs = new(0);                 // monotonic ms at open → the 5s dwell survives 2↔3 phase flips
    readonly Signal<int> _safePoll = new(0);                 // bumped per in-zone safe-zone elapse → remounts the 1s poll clock
    // Timer phase: 0 = idle, 1 = show-delay counting down (open after it), 2 = bubble open (auto-dismiss counting
    // down), 3 = bubble open + pointer outside owner ∪ bubble (the 1s safe-zone grace counting down).
    readonly Signal<int> _phase = new(0);
    // 3-state input mode of the pending/open tooltip (WinUI AutomaticToolTipInputMode): 0 = mouse, 1 = keyboard.
    readonly Ref<bool> _keyboardMode = new(false);
    // GetTickCount()-style monotonic ms at the last close → re-show detection (BETWEEN_SHOW_DELAY_MS window).
    // WinUI names this s_lastToolTipOpenedTime, but assigns it in CloseAutomaticToolTip at the close start.
    readonly Ref<long> _lastClosedAtMs = new(long.MinValue / 2);

    // The handlers, thunks and effect bodies the wrapper box and the effects hold, created once per mount.
    readonly Func<Element> _bubbleContent;
    readonly Action<NodeHandle> _onBubbleRealized, _onAnchorRealized;
    readonly Action<Point2> _onEnter;
    readonly Action _onLeave, _openOnMountFx, _strandedGuardFx, _openNow, _autoDismiss, _safeZoneCheck;
    readonly Action<PointerEventArgs> _onPressed;
    readonly Action<bool> _onFocus;
    readonly Func<Action?> _teardownFx;

    public ToolTip()
    {
        _onBubbleRealized = x => _bubbleNode.Value = x;
        _onAnchorRealized = x => _anchor.Value = x;
        _bubbleContent = () => BubbleContent(_textRef.Value, _onBubbleRealized);
        _onEnter = OnEnter;
        _onLeave = OnLeave;
        _onPressed = OnPressed;
        _onFocus = OnFocus;
        _openNow = OpenNow;
        _autoDismiss = AutoDismiss;
        _safeZoneCheck = SafeZoneCheck;
        _openOnMountFx = OpenOnMountEffect;
        _strandedGuardFx = StrandedGuardEffect;
        _teardownFx = TeardownEffect;
    }

    void OpenNow()
    {
        _phase.Value = 2;   // bubble open → arm the auto-dismiss countdown
        if (_h.Value is { IsOpen: true }) return;
        // One bubble at a time: whoever owns the screen loses it here, before this one is placed (see s_openOwner).
        if (!ReferenceEquals(s_openOwner, this)) s_openCloser?.Invoke();
        s_openOwner = this;
        s_openCloser = CloseNow;
        _openedAtMs.Value = Environment.TickCount64;   // dwell epoch (m_tpCloseTimer is armed once per open)
        _openedText.Value = _textRef.Value;             // identity epoch — see _openedText (a re-pushed owner closes it)
        // A tooltip never traps focus and never light-dismisses on outside click — it is transient and dismissal
        // is driven by hover/focus-leave + press + the auto-dismiss timer (ToolTipService owns close, not the user).
        //
        // Chrome: the fade (Raw) for a normal tooltip; NONE (Static) for a transient one. A 167ms fade-out is a
        // ghost the next bubble's fade-in overlaps, which is precisely what "instant" must not look like — a data
        // tip that re-anchors as the pointer crosses a strip has to be exactly one bubble on every frame.
        var options = new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None,
                                       Chrome: _transient.Value ? PopupChrome.Static : PopupChrome.Raw);
        if (Placement == ToolTipPlacementMode.Mouse && !_keyboardMode.Value)
        {
            // PlacementMode.Mouse: top-left at the pointer, 11px below it (ToolTip_Partial.cpp:976-977
            // "align ToolTip with the bottom left corner of mouse bounding rectangle"; .h:56 offset = 11). The
            // positioner adds FlyoutMargin (4) below a Bottom-placed _anchor, so the synthetic point-rect carries
            // the remaining 7; collisions flip it above the pointer.
            var local = _lastPointerLocal.Value;
            _h.Value = _svc.OpenAt(
                () =>
                {
                    var scene = Context.Scene;
                    var node = _anchor.Value;   // LIVE: a re-keyed/remounted target changes the node; a capture
                                               // would strand the bubble on a dead handle and clamp it to origin
                    RectF abs = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                    return new RectF(abs.X + local.X, abs.Y + local.Y + (MousePlacementVerticalOffset - FlyoutPositioner.FlyoutMargin), 0f, 0f);
                },
                _bubbleContent,
                FlyoutPlacement.BottomEdgeAlignedLeft,
                options,
                owner: () => _anchor.Value);
        }
        else
        {
            // WinUI default PlacementMode.Top: CENTERED above the target (ToolTip_Partial.cpp:1119-1122 default),
            // against the target rect INFLATED by the input-mode offset — DEFAULT_MOUSE_OFFSET 20 /
            // DEFAULT_KEYBOARD_OFFSET 12 on both axes (ToolTip_Partial.h:10-11; cpp:1224-1258
            // InflateRect(&rcDockTo, horizontalOffset, verticalOffset) feeds QueryRelativePosition at :1275).
            // The positioner adds FlyoutMargin (4) in the major direction, so the synthetic rect carries the rest;
            // FlyoutPositioner flips below on a collision.
            bool keyboard = _keyboardMode.Value;
            _h.Value = _svc.OpenAt(
                () =>
                {
                    var scene = Context.Scene;
                    var node = _anchor.Value;   // LIVE: a re-keyed/remounted target changes the node; a capture
                                               // would strand the bubble on a dead handle and clamp it to origin
                    RectF abs = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                    float inflate = (keyboard ? KeyboardPlacementOffset : MousePlacementOffset) - FlyoutPositioner.FlyoutMargin;
                    return new RectF(abs.X - inflate, abs.Y - inflate, abs.W + inflate * 2f, abs.H + inflate * 2f);
                },
                _bubbleContent,
                FlyoutPlacement.Top,
                options,
                owner: () => _anchor.Value);
        }
    }

    void CloseNow()
    {
        bool wasOpen = _phase.Peek() is 2 or 3;
        if (_h.Value is { IsOpen: true } o) o.Close();
        _h.Value = null;
        if (ReferenceEquals(s_openOwner, this)) { s_openOwner = null; s_openCloser = null; }
        _bubbleNode.Value = default;   // the bubble unmounts — its rect leaves the safe zone
        if (wasOpen) _lastClosedAtMs.Value = Environment.TickCount64;   // mark close start for the re-show window
        _keyboardMode.Value = false;
        _phase.Value = 0;
    }

    // The SPI_GETMESSAGEDURATION dwell elapsed: close AND latch until leave + re-enter — WinUI's only show trigger
    // is PointerEntered (ToolTipService_Partial.cpp:1395-1418 OnOwnerPointerEntered), so in-place hover moves can
    // never re-open a timed-out tooltip (the owner re-enters the show path via a real exit + enter only).
    void AutoDismiss() { _dismissedUntilLeave.Value = true; CloseNow(); }

    // Pointer-enter (OnHoverMove fires on any move while hovering): begin the initial-show-delay countdown if idle.
    // ToolTipService.OnOwnerEnterInternal — Mouse mode, reshow if the previous tooltip closed < 200ms ago.
    void OnEnter(Point2 local)
    {
        _lastPointerLocal.Value = local;   // tracked even while open — WinUI re-reads the current point at placement
        if (_phase.Peek() == 3) { _phase.Value = 2; return; }   // back inside the safe zone → cancel the 1s grace
        if (_dismissedUntilLeave.Value) return;   // dismissed: moves while still hovering must NOT re-arm (the WinUI
                                                 // owner stays out of m_nestedOwners until a real leave + re-enter)
        if (_phase.Peek() != 0 || (_h.Value is { IsOpen: true })) return;
        _keyboardMode.Value = false;
        _phase.Value = 1;   // show-delay counting down
    }

    // Pointer-leave: cancel a PENDING open (ToolTipService_Partial.cpp:1435-1442 — "Cancel the ToolTip if it had
    // not been opened yet"), but KEEP an open bubble: WinUI's owner-exit only records the owner and lets the
    // safe-zone monitor close it once the pointer is outside owner ∪ tooltip (cpp:1443-1453; the 1s check timer,
    // cpp:349-381 + .h:22). A leave also lifts the press/timeout dismiss latch (the next enter may show again).
    void OnLeave()
    {
        _dismissedUntilLeave.Value = false;
        // A transient (instant-show) bubble is hit-test-invisible chrome over a data surface: there is nothing in
        // it to travel INTO, which is the only thing the safe zone protects. It closes on the leave edge, full stop.
        if (_transient.Value) { CloseNow(); return; }
        if (_phase.Peek() == 2) { _phase.Value = 3; return; }   // arm the 1s safe-zone poll (the bubble rect keeps it open via geometry)
        if (_phase.Peek() != 3) CloseNow();   // pending open → cancel
    }

    // The 1s safe-zone poll elapsed (phase 3): WinUI OnSafeZoneCheck / IsToolTipInSafeZone against the GLOBAL
    // pointer — the bubble is hit-test-invisible (a real tooltip never intercepts input), so bubble-hover is
    // detected by geometry, never by events. Owner re-entry is event-driven (OnEnter flips 3→2) but the owner
    // rect is tested too (WinUI tests owner ∪ tooltip). The 5s dwell stays authoritative while parked in-zone.
    void SafeZoneCheck()
    {
        if (Environment.TickCount64 - _openedAtMs.Value >= (long)ShowDurationMs) { AutoDismiss(); return; }
        var scene = Context.Scene;
        var on = _anchor.Value;
        // The owner DIED under the bubble (its subtree was relaid out or removed while the pointer sat on it, or a
        // drag holds capture), so the leave edge that would have closed this is never coming. The rect thunk now
        // resolves to `default` and the placement pass walks the bubble to the viewport origin — a dead anchor is a
        // close, not another poll interval.
        if (scene is not null && !on.IsNull && !scene.IsLive(on)) { CloseNow(); return; }
        if (scene is not null && _hooks.GetPointerPosition?.Invoke() is { } pt)
        {
            var bn = _bubbleNode.Value;
            bool inside = (!on.IsNull && scene.IsLive(on) && InRect(scene.AbsoluteRect(on), pt))
                       || (!bn.IsNull && scene.IsLive(bn) && InRect(scene.AbsoluteRect(bn), pt));
            if (inside) { _safePoll.Value = _safePoll.Peek() + 1; return; }   // stay open — remount the poll clock (keyed by _safePoll)
        }
        CloseNow();   // outside owner ∪ bubble for one full interval (or no trustworthy pointer) → close
    }

    // Keyboard focus entering the target subtree (the dispatcher routes focus-changed to ancestors on subtree
    // boundary crossings): WinUI OnOwnerGotFocus (ToolTipService_Partial.cpp:1635-1668) — show ONLY for
    // FocusState::Keyboard (cpp:1652-1656: GetRealFocusStateForFocusedElement() == Keyboard; pointer-driven focus
    // never opens a tooltip). Keyboard delay = ×2 (800ms), reshow included (cpp:1777-1779). Focus leaving = leave.
    void OnFocus(bool got)
    {
        if (!got) { CloseNow(); return; }
        if (_phase.Peek() != 0 || _h.Value is { IsOpen: true }) return;
        var scene = Context.Scene;
        var focused = _hooks.GetFocus?.Invoke() ?? NodeHandle.Null;
        bool keyboardFocus = scene is not null && !focused.IsNull && scene.IsLive(focused)
                             && (scene.Flags(focused) & NodeFlags.FocusVisual) != 0;
        if (!keyboardFocus) return;
        _keyboardMode.Value = true;
        _phase.Value = 1;
    }

    // Pointer press over the target: dismiss an open bubble (and a pending one), latched until leave + re-enter.
    // Press-dismiss is classic Win32/WPF tooltip behavior we keep deliberately — WinUI 3's ToolTipService registers
    // no PointerPressed handler (ToolTipService_Partial.cpp:176-220; it closes via safe-zone exit, cpp:1437-1453)
    // but equally cannot re-open a dismissed owner until a real leave + re-enter (cpp:725-737). There is no
    // click-to-toggle — the wrapper adds NO OnClick, so it never becomes a tab stop or intercepts activation.
    void OnPressed(PointerEventArgs _)
    {
        if (_phase.Peek() == 0 && _h.Value is not { IsOpen: true }) return;
        _dismissedUntilLeave.Value = true;
        CloseNow();
    }


    void OpenOnMountEffect()
    {
        if (!OpenOnMount || _autoOpened.Value) return;
        _autoOpened.Value = true;
        OpenNow();
    }

    // STRANDED-BUBBLE GUARD. Two ways an open bubble outlives the thing it describes, both ending with a tip
    // anchored to the wrong item (or to the viewport origin) until the 5s dwell finally fires:
    //   • the owner's IDENTITY changed in place — Wrap re-pushes props, so a recycled rail/list row REUSES this
    //     component and the live thunks re-point the already-open bubble at the new item (see _openedText);
    //   • the anchor node DIED with no leave edge — the target was relaid out or destroyed under a still pointer, or
    //     a drag holds capture, so OnLeave never runs and the phase never leaves 2 (the safe-zone poll, which has
    //     the same test, only runs in phase 3).
    // Deps-gated on the text, so a plain re-render costs one DepKey compare; an EFFECT rather than an inline render
    // check because CloseNow writes signals (phase/bubbleNode), which a render body must not do.
    void StrandedGuardEffect()
    {
        if (_phase.Peek() is not (2 or 3) && _h.Value is not { IsOpen: true }) return;
        if (!string.Equals(_openedText.Value, _textRef.Value, StringComparison.Ordinal)) { CloseNow(); return; }
        var scene = Context.Scene;
        var on = _anchor.Value;
        if (scene is not null && !on.IsNull && !scene.IsLive(on)) CloseNow();
    }

    // Owner unmount must not orphan the bubble. The component's OverlayHandle is the ONLY thing an unmounting
    // ToolTip still owns that the host does not: the entry lives in OverlayServiceImpl.Entries and leaves only via
    // Closing → AfterAnimations → Finalize, so a wrapper that disappears while open leaves a live entry whose rect
    // thunk now resolves against a DEAD node (scene.IsLive false ⇒ abs = default ⇒ the synthetic point at the
    // viewport origin) — and the placement pass re-places rect-anchored entries on EVERY OverlayHost render
    // (OverlayHost.cs:835-839), so the bubble walks to the viewport's top-left corner and stays.
    //
    // Handle-only teardown ON PURPOSE: CloseNow() also writes phase / bubbleNode / lastClosedAtMs / keyboardMode,
    // cells being torn down in this very pass (RunAllCleanups). Those writes cannot throw (a SignalCell is not
    // disposed) but they are useless — no subscriber survives — and they re-enter a dying reactive graph.
    Action? TeardownEffect() => () =>
    {
        if (_h.Value is { IsOpen: true } o) o.Close();
        _h.Value = null;
        // Release the single-bubble latch too: a torn-down owner's closer writes into cells that no longer have
        // a subscriber, and worse, would leave the NEXT tooltip believing something else still owns the screen.
        if (ReferenceEquals(s_openOwner, this)) { s_openOwner = null; s_openCloser = null; }
    };

    public override Element Render()
    {
        // The deferred form wins when present: it is the one the parent chose, and resolving it FIRST is what puts the
        // factory's signal reads inside THIS component's render (the whole point of the overload).
        var stable = UsePropsOrDefault<ToolTipStableSlots>();
        var slots = stable is null ? UsePropsOrDefault<ToolTipSlots>() : null;
        var bound = stable is null && slots is null ? UsePropsOrDefault<ToolTipBoundSlots>() : null;
        Element target = stable is not null ? stable.Target() : (bound?.Target ?? slots?.Target ?? Target);
        // P3: the BOUND overload reads its Prop<string?> HERE, inside this render — a bound Text subscribes this
        // component exactly like the stable factory above subscribes to whatever signals it reads.
        string? boundText = bound?.Text.Current();
        string text = boundText ?? slots?.Text ?? Text;
        float grow = stable?.Grow ?? bound?.Grow ?? slots?.Grow ?? Grow;
        float showDelayOverride = stable?.ShowDelayMs ?? bound?.ShowDelayMs ?? slots?.ShowDelayMs ?? ShowDelayMs;
        if (grow > 0f) target = Fill(target, grow);
        // Null/empty bound text ⇒ NO TOOLTIP: read below (after every Use* hook below has run, in the SAME order every
        // render — hooks must never be skipped conditionally) to short-circuit the actual wiring/wrap. The component
        // stays mounted; re-pushed props re-render it, so a later non-empty value wires up with no remount.
        bool hasTooltip = !(bound is not null && string.IsNullOrEmpty(boundText));
        _svc = UseContext(Overlay.Service);
        _hooks = UseContext(InputHooks.Current);
        _textRef.Value = text;
        _transient.Value = !float.IsNaN(showDelayOverride);

        UseEffect(_openOnMountFx, OpenOnMount);
        UseEffect(_strandedGuardFx, text);
        UseEffect(_teardownFx, DepKey.Empty);

        int ph = _phase.Value;   // subscribe → re-render when the timer phase changes (mount/unmount the clock)
        int poll = _safePoll.Value;   // subscribe → an in-zone safe-zone elapse remounts a fresh 1s poll clock
        // GetInitialShowDelay: Mouse ×2 normal / ×1 reshow (truncated 1.5 — see MouseReshowDelayMs); Keyboard ×2 always.
        bool isReshow = Environment.TickCount64 - _lastClosedAtMs.Value < (long)BetweenShowDelayMs;
        // The per-element override (see Wrap) replaces both MOUSE legs and leaves the keyboard leg alone. Clamped at 0
        // rather than passed raw: the value reaches the host timer queue as a deadline offset, and a negative span is
        // not a shorter delay, it is a deadline in the past. (0 itself is safe and means "the next frame's drain".)
        float delay = _keyboardMode.Value ? KeyboardShowDelayMs
                    : !float.IsNaN(showDelayOverride) ? MathF.Max(0f, showDelayOverride)
                    : (isReshow ? MouseReshowDelayMs : MouseShowDelayMs);
        // The REMAINING show-duration dwell: WinUI's m_tpCloseTimer is armed once per open and keeps running while the
        // safe-zone monitor watches (ToolTipService_Partial.h:54; OpenAutomaticToolTip arms it, cpp:429-459), so the
        // 2↔3 phase flips must not restart the 5s — the remount re-arms with whatever dwell is left.
        float dwellLeft = MathF.Max(1f, ShowDurationMs - (Environment.TickCount64 - _openedAtMs.Value));

        // Mount the one-shot countdown ONLY while a phase is live (1 = show-delay, 2 = auto-dismiss, 3 = safe-zone
        // grace). When idle it is absent — and while it IS mounted it costs nothing per frame either: ToolTipClock arms
        // one HostTimerQueue entry at mount and never re-renders, so a pending tooltip lets the host loop idle to the
        // deadline instead of pinning it at panel rate. The clock is KEYED by _phase: the reconciler reuses a same-type
        // component without re-running its factory (constructor props are mount-time only), so a phase flip must REMOUNT a fresh clock
        // or the open bubble keeps the already-fired show-delay clock and the auto-dismiss never arms. WinUI keeps
        // these as separate DispatcherTimers — m_tpOpenTimer (show delay) vs m_tpCloseTimer (SPI_GETMESSAGEDURATION
        // dwell, ToolTipService_Partial.h:54/96-99) vs m_tpSafeZoneCheckTimer (1s poll, .h:22; cpp:384-414).
        Element? clock = ph == 0 ? null : Embed.Comp(() => new ToolTipClock
        {
            DurationMs = ph == 1 ? delay : ph == 2 ? dwellLeft : SafeZoneCheckMs,
            OnElapsed = ph == 1 ? _openNow : ph == 2 ? _autoDismiss : _safeZoneCheck,
        }) with
        { Key = ph == 1 ? "tt-open-timer" : ph == 2 ? "tt-close-timer" : "tt-safezone-timer:" + poll };

        return new BoxEl
        {
            // The service wrapper must be layout-transparent on the parent's cross axis. Pinning it to Start pulled
            // every wrapped toolbar button to the top of a centred row and every compact-rail tile to the left edge.
            // Auto preserves the alignment the target would have inherited before ToolTip.Wrap introduced this node.
            AlignSelf = FlexAlign.Auto,
            // …and the MAIN axis is the OPT-IN half (`grow:`, default 0 = untouched). This wrapper is a flex ROW
            // (BoxEl.Direction defaults to 0), so the target is main-axis sized inside it — its own content width, not
            // the wrapper's. A wrapper stretched by a COLUMN parent therefore holds a shrink-wrapped target: the same
            // class Reorderable.Item's call sites are fixed for, and why the sidebar's track / missing / unavailable
            // rows painted narrower fill plates than their unwrapped neighbours. It stays OPT-IN because the fix cannot
            // be global — every wrapped chip and icon button in a column would go full-bleed — and it grows the TARGET
            // (see Fill) rather than making this a ZStack, because a ZStack would also stretch the second child, the
            // live ToolTipClock, into a full-bleed hittable layer over the target (Reconciler.MirrorParticipation's
            // note on bare anchors is the same hazard). Growing the wrapper too is what makes `grow:` mean the same
            // thing here as everywhere else in the kit: fill the PARENT, whichever axis the parent runs on.
            Grow = grow,
            // A pointer LISTENER, not an interaction scope: the four handlers below give it PointerBit, which would
            // otherwise make it a hover-cascade boundary and hide a wrapped card FAB's reveal from the card's hover.
            HoverScopeTransparent = true,
            OnRealized = _onAnchorRealized,
            // P3 bound-text form: hasTooltip is false exactly when a bound Prop<string?> resolved null/empty THIS
            // render — no new open/dismiss/focus trigger is wired (the clock above is likewise forced absent via `ph`
            // reading `dismissedUntilLeave`/pending state that a !hasTooltip render never arms), so the wrapper is
            // functionally inert until a later render's text is non-empty again.
            OnHoverMove = hasTooltip ? _onEnter : null,         // mouse-enter trigger (makes the target hit-testable for hover)
            OnPointerExit = hasTooltip ? _onLeave : null,       // mouse-leave → cancel pending / close open
            OnPointerPressed = hasTooltip ? _onPressed : null,  // press over the target → dismiss (never survives an interaction)
            OnFocusChanged = hasTooltip ? _onFocus : null,      // keyboard focus in/out of the target subtree (a11y trigger)
            Children = clock is null || !hasTooltip ? [target] : [target, clock],
        };
    }

    /// <summary>The <c>grow:</c> opt-in, applied to the TARGET so it fills this wrapper's main axis (the wrapper itself
    /// grows into the parent — see the wrapper's comment). It sets ONLY <c>Grow</c>: nothing else about the target is
    /// this parameter's business, and a target that declares its own <c>Width</c> — statically or through a bind — has
    /// already stated its size, so it is returned untouched rather than silently overridden. A non-<c>BoxEl</c> target
    /// (a bare <c>TextEl</c>/<c>ImageEl</c>) has no flex channel to set and is likewise left alone; wrap such a target
    /// in a Box if it must fill.</summary>
    static Element Fill(Element target, float grow)
        => target is BoxEl b && !b.Width.IsBound && float.IsNaN(b.Width.Value)
            ? b with { Grow = grow }
            : target;

    // WinUI ToolTip bubble (ToolTip_themeresources.xaml DefaultToolTipStyle:42-76):
    //   Background = ToolTipBackgroundBrush = AcrylicInAppFillColorDefaultBrush (:14 dark / :40 light) —
    //     theme-aware Tok.AcrylicFlyout (dark #2C2C2C @0.15 lum 0.96 fb #2C2C2C; light #FCFCFC @0.05 lum 0.96 fb #F9F9F9
    //     — light luminosity raised from WinUI's 0.85 so the plate reads solid over the Mica-lit pale pages)
    //   BorderBrush = SurfaceStrokeColorFlyout (Tok.StrokeFlyoutDefault), BorderThickness = 1
    //   CornerRadius = ControlCornerRadius (4px), Padding = ToolTipBorderPadding 9,6,9,8
    //   FontSize = ToolTipContentThemeFontSize 12, Foreground = TextFillColorPrimary, MaxWidth = 320, TextWrapping = Wrap.
    //   Shadow = the light transient elevation class (Elevation.Tooltip) — tooltips sit on the lowest popup band.
    Element BubbleContent(string text, Action<NodeHandle> onRealized)
    {
        var bubble = new BoxEl
        {
            Fill = ColorF.Transparent,
            Acrylic = Tok.AcrylicFlyout,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Shadow = Elevation.Tooltip,
            MaxWidth = 320f,
            Padding = new Edges4(9, 6, 9, 8),
            // A tooltip never intercepts input (WinUI tooltips are hit-test-transparent): the bubble must not swallow
            // wheel/press under the pointer — scrolling continues through it. Its bounds still join the safe zone,
            // tested by GEOMETRY against the real pointer (SafeZoneCheck), not by hover events.
            HitTestVisible = false,
            OnRealized = onRealized,   // fresh mount per open → the capture cannot go stale (unlike a recycled row)
            Children =
            [
                new TextEl(text)
                {
                    Size = 12f,
                    Color = Tok.TextPrimary,
                    Wrap = TextWrap.Wrap,
                    MaxWidth = 302f,   // 320 − (9 + 9) horizontal padding
                },
            ],
        };
        // Parts: restyle the bubble chrome (acrylic, stroke, elevation, padding…); the Text content, the input
        // transparency, and the safe-zone node capture always win.
        return Parts.Apply(PartBubble, bubble) with { Children = bubble.Children, HitTestVisible = false, OnRealized = onRealized };
    }

    static bool InRect(in RectF r, Point2 p)
        => p.X >= r.X && p.Y >= r.Y && p.X <= r.X + r.W && p.Y <= r.Y + r.H;
}

/// <summary>
/// One-shot countdown, mounted by <see cref="ToolTip"/> (and by <see cref="CommandBarFlyout"/> / <see cref="MenuFlyout"/> /
/// <see cref="Slider"/>) only while a show-delay, auto-dismiss, safe-zone poll or close-fade completion is pending. It is
/// the engine analogue of ToolTipService's DispatcherTimer, and it is exactly that: <c>UseTimeout</c> on the host's
/// <c>HostTimerQueue</c> — ONE heap entry armed at mount for <see cref="DurationMs"/>, firing <see cref="OnElapsed"/>
/// once when the host drains the queue at the top of the frame the deadline falls in. Unmount cancels it (the hook cell
/// is generation-guarded, so a due-after-unmount pop is a no-op) and a re-arm is a REMOUNT via the call site's
/// <c>Key</c>.
/// <para><b>It does not wake the UI thread and it never re-renders.</b> There is no <c>FrameClock.Tick</c>
/// subscription and no animation track: the component renders one empty, non-hit-testable node at mount and is then
/// inert. A pending countdown therefore costs zero frames and zero allocations — the host loop idles and merely
/// shortens its wait to the earliest armed deadline (<c>AppHost.ClampWaitToTimers</c>). The previous mechanism seeded
/// an invisible Opacity 1→1 tween and polled <c>HasTracks</c> from a per-frame re-render, which pinned the UI thread at
/// panel rate for the whole 800 ms delay and the whole 5 s dwell; it is deleted.</para>
/// <para>The queue's clock is the host frame clock (wall clock for a real window, the deterministic accumulated frame
/// delta headless), so the 800 ms show delay and the 5 s auto-dismiss stay wall-accurate and the VerticalSlice gates
/// stay deterministic. <see cref="DurationMs"/> 0 is honoured literally and means "the next frame's drain".</para>
/// </summary>
internal sealed class ToolTipClock : Component
{
    public required float DurationMs;
    public required Action OnElapsed;

    public override Element Render()
    {
        // Mount-once: DepKey.Empty ⇒ armed on the first render and never re-armed (this component has no reactive
        // read, so there IS no second render). Cancelled by the cell's own unmount cleanup.
        UseTimeout(OnElapsed, MathF.Max(0f, DurationMs));
        return new BoxEl { HitTestVisible = false };
    }
}
