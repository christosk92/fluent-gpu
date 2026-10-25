using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
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

    /// <summary>The bubble's look, read at RENDER through the control style table (<see cref="ControlStyles.ToolTipStyle"/>).
    /// Every default is the WinUI DefaultToolTipStyle (ToolTip_themeresources.xaml:42-76).</summary>
    public sealed record Style
    {
        /// <summary>The bubble fill under the frosted layer (transparent: the acrylic is the body); with no
        /// <see cref="Acrylic"/> it is the whole, solid bubble.</summary>
        public ColorF Fill { get; init; } = ColorF.Transparent;
        /// <summary>The frosted layer (ToolTipBackgroundBrush = AcrylicInAppFillColorDefault); null = a solid <see cref="Fill"/>.</summary>
        public AcrylicSpec? Acrylic { get; init; } = Tok.AcrylicFlyout;
        /// <summary>The ring (SurfaceStrokeColorFlyout) and its width (1; 0 = none).</summary>
        public ColorF Border { get; init; } = Tok.StrokeFlyoutDefault;
        public float BorderWidth { get; init; } = 1f;
        /// <summary>The radius (ControlCornerRadius).</summary>
        public float Radius { get; init; } = Radii.Control;
        /// <summary>The elevation (the tooltip band); null = none.</summary>
        public ShadowSpec? Shadow { get; init; } = Elevation.Tooltip;
        /// <summary>The bubble's greatest width (320) and its padding (ToolTipBorderPadding 9,6,9,8).</summary>
        public float MaxWidth { get; init; } = 320f;
        public Edges4 Padding { get; init; } = new(9, 6, 9, 8);
        /// <summary>The text (ToolTipContentThemeFontSize 12, TextFillColorPrimary).</summary>
        public float TextSize { get; init; } = 12f;
        public ColorF TextColor { get; init; } = Tok.TextPrimary;
    }

    /// <summary>The bubble style now: the active theme's (<see cref="ControlStyles.ToolTipStyle"/>), else <see cref="FluentStyle"/>.</summary>
    public static Style DefaultStyle => ControlStyles.Resolve(ControlStyles.ToolTipStyle, FluentStyle);

    /// <summary>The built-in Fluent default (what a theme table without a ToolTip entry resolves to). Reads the live Tok on every call.</summary>
    public static Style FluentStyle() => new();

    public Element Target = s_noTarget;
    private static readonly BoxEl s_noTarget = new();   // shared: an element is immutable, and a Wrap form never reads it
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

    /// <summary>Close whichever bubble is open, if any — the host's hook for a moment the pointer cannot signal. A
    /// kept-alive page (Flow.KeepAlive parks it, nothing unmounts) that navigates away under a STILL pointer leaves its
    /// owner both mounted and geometrically under the cursor: no leave edge fires, the safe-zone poll finds the owner
    /// rect still "inside", and the bubble outlives the page it described until the 5s dwell (recording 2026-09-16: a
    /// drawer's "Go to album" tip parked over the album page it opened). The app calls this on every route commit.
    /// Idempotent; UI thread only. Runs the SAME closer a competing owner would (the single-bubble latch, see
    /// <see cref="ToolTipHost"/>), so the re-show window and the latch stay consistent.</summary>
    public static void CloseOpen() => ToolTipHost.CloseOpen();

    /// <summary>LIVE target+text slots RE-PUSHED to the component form (<c>Embed.Comp(slots, () => new ToolTip())</c>; the
    /// SelectorBar/RadioButtons idiom). <see cref="Target"/> and <see cref="Text"/> are plain fields, so via a propless
    /// <c>Embed.Comp</c> they freeze at first mount — a re-rendering parent's new wrapped element or new tooltip text would be
    /// silently dropped (the toggle-tooltip staleness bug). When present they WIN over the fields and the ToolTip re-renders
    /// reactively (props are signal-backed). Read with <c>UsePropsOrDefault</c>. <see cref="Wrap(Element, string, float, float)"/>
    /// needs neither: it builds the wrapper and its attachment in the caller's render.</summary>
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
    /// countdown is a host one-shot timer (<see cref="HostTimerQueue"/>) armed in the reactive flush that follows the
    /// pointer-enter and drained at the top of the next frame.</summary>
    public static Element Wrap(Element target, string text, float grow = 0f, float showDelayMs = float.NaN)
        => Embed.Comp(new ToolTipSlots(target, text, grow, showDelayMs), s_factory) with { SkeletonProxy = ProxyOf(target, grow) };

    // Every form stays a ToolTip COMPONENT boundary (PERF r3 UI). The component is thin (no hook, no state: Render returns the
    // attachment wrapper, and the tooltip's state lives in the attachment, created on the first hover or focus), but its element
    // KIND is part of how a parent's slot matches across renders: a keyed slot that holds a Wrap result in one arm and another
    // component in the other (the show toolbar's words <-> filter menu across a tier) keeps its node and swaps the component; a
    // slot holding a Wrap result and a plain box remounts. A box-only Wrap changed both, so the component stays.
    private static readonly Func<ToolTip> s_factory = static () => new ToolTip();   // ComponentType = typeof(ToolTip), as every form had

    /// <summary>BOUND text form (Operation ultra-fast GPU engine, P3 — for a virtualized row: one <see cref="Prop{T}"/>
    /// read per render instead of a rebuilt call-site string). Null/empty resolves to NO TOOLTIP: the wrapper carries no
    /// hover/focus/press/safe-zone wiring while it is, and a later non-empty value lights the tooltip up with no remount
    /// either way. The value is read inside the <see cref="ToolTip"/> component's render, so a bound Text re-evaluates it
    /// (and only it) on change.</summary>
    public static Element Wrap(Element target, Prop<string?> text, float grow = 0f, float showDelayMs = float.NaN)
        => Embed.Comp(new ToolTipBoundSlots(target, text, grow, showDelayMs), s_factory) with { SkeletonProxy = ProxyOf(target, grow) };

    /// <summary>The wrapped target's shape for a derived skeleton: a tooltip draws nothing in place, so a loading region derives the TARGET (a ring,
    /// a glyph button at its real size), never the deriver's 160-wide default bar for an opaque component, which overran the row it stood in.</summary>
    static Func<Element> ProxyOf(Element target, float grow) => () => grow > 0f ? Fill(target, grow) : target;

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
        => Embed.Comp(new ToolTipStableSlots(target, text, grow, showDelayMs), s_factory);

    /// <summary>The service wrapper every form renders: the target inside a layout-transparent box that carries the tooltip as a
    /// <see cref="NodeAttachment"/> (<see cref="ToolTipSpec"/>). The component that renders it holds no state (PERF r3 UI): the
    /// attachment's per-node state is a few handlers until the pointer or keyboard focus first lands on the box, and the state
    /// machine below (<see cref="ToolTipHost"/>) runs only then.
    /// <para>The box is layout-transparent on the parent's cross axis: pinning it to Start pulled every wrapped toolbar button to the
    /// top of a centred row and every compact-rail tile to the left edge, so AlignSelf Auto preserves the alignment the target would
    /// have inherited. The MAIN axis is the opt-in half (<c>grow:</c>, default 0 = untouched): the wrapper is a flex ROW, so the target
    /// is main-axis sized inside it; a wrapper stretched by a COLUMN parent therefore holds a shrink-wrapped target (why the
    /// sidebar's track / missing / unavailable rows painted narrower fill plates than their unwrapped neighbours). It stays OPT-IN
    /// because the fix cannot be global (every wrapped chip and icon button in a column would go full-bleed), and it grows the TARGET
    /// (see <see cref="Fill"/>) as well as the wrapper, so <c>grow:</c> means fill the PARENT, whichever axis the parent runs on.
    /// HoverScopeTransparent: a pointer LISTENER, not an interaction scope — the handlers give it PointerBit, which would otherwise
    /// make it a hover-cascade boundary and hide a wrapped card FAB's reveal from the card's hover.</para></summary>
    internal static BoxEl Wrapper(Element target, float grow, ToolTipSpec spec)
    {
        if (grow > 0f) target = Fill(target, grow);
        return new BoxEl
        {
            AlignSelf = FlexAlign.Auto,
            Grow = grow,
            HoverScopeTransparent = true,
            Attachment = spec,
            Children = [target],
        };
    }

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
        // Null/empty bound text ⇒ NO TOOLTIP: the wrapper carries no wiring; the component stays mounted, and re-pushed props
        // re-render it, so a later non-empty value wires up with no remount.
        bool hasTooltip = !(bound is not null && string.IsNullOrEmpty(boundText));
        return Wrapper(target, grow, new ToolTipSpec(text, showDelayOverride, Placement, OpenOnMount, Parts, Wired: hasTooltip));
    }

    /// <summary>The <c>grow:</c> opt-in, applied to the TARGET so it fills this wrapper's main axis (the wrapper itself
    /// grows into the parent — see <see cref="Wrapper"/>). It sets ONLY <c>Grow</c>: nothing else about the target is
    /// this parameter's business, and a target that declares its own <c>Width</c> — statically or through a bind — has
    /// already stated its size, so it is returned untouched rather than silently overridden. A non-<c>BoxEl</c> target
    /// (a bare <c>TextEl</c>/<c>ImageEl</c>) has no flex channel to set and is likewise left alone; wrap such a target
    /// in a Box if it must fill.</summary>
    static Element Fill(Element target, float grow)
        => target is BoxEl b && !b.Width.IsBound && float.IsNaN(b.Width.Value)
            ? b with { Grow = grow }
            : target;

    internal static bool InRect(in RectF r, Point2 p)
        => p.X >= r.X && p.Y >= r.Y && p.X <= r.X + r.W && p.Y <= r.Y + r.H;
}

/// <summary>A tooltip's <see cref="NodeAttachment"/> (the spec a <see cref="ToolTip.Wrapper"/> carries; immutable, rebuilt per
/// render, compared by value so an unchanged re-render rebinds nothing). <see cref="Wired"/> false is the "no tooltip" state of a
/// null/empty bound text: no handlers, and no per-node state until one is wired.</summary>
internal sealed record ToolTipSpec(string Text, float ShowDelayMs = float.NaN, ToolTipPlacementMode Placement = ToolTipPlacementMode.Top,
    bool OpenOnMount = false, TemplateParts? Parts = null, bool Wired = true) : NodeAttachment
{
    public static readonly ToolTipSpec Unwired = new("", Wired: false);

    protected override NodeAttachmentState Bind(NodeHandle node, NodeAttachmentState? state, IAttachmentHost host)
    {
        if (state is ToolTipHost existing) { existing.Update(this); return existing; }
        if (!Wired) return ToolTipHost.Inert;   // nothing wired yet: no per-node state until a text arrives
        return new ToolTipHost(node, host, this);
    }
}

/// <summary>
/// The per-node tooltip: WinUI's ToolTipService state machine for ONE owner (the wrapper box), as the <see cref="ToolTip"/> component
/// used to run it per instance. Created when the wrapper is first written with a wired spec; until the pointer or keyboard focus
/// lands on it, it holds four handlers and its spec. The phase signals, the clock effect and the overlay handle come into being on
/// the first activation. Every behaviour is the component's, line for line: the show delay ladder and the re-show window, the
/// single-bubble rule, the safe zone and its 1 s poll, press and timeout dismiss latched until leave + re-enter, keyboard focus
/// (FocusVisual only) at 800 ms, the transient (instant) posture, the stranded-bubble guard and the teardown on unmount.
/// <para>Timing is the component's too. A phase change used to re-render the component in the next reactive flush, which mounted
/// (or remounted, keyed by the phase) a <see cref="ToolTipClock"/> whose one-shot timeout was armed at the host timer clock of that
/// flush; here a phase change schedules <see cref="ClockEffect"/> in the same flush, which arms the same one-shot on the same clock,
/// keyed the same way (a different phase, or a new safe-zone poll, re-arms; an unchanged one does not).</para>
/// </summary>
internal sealed class ToolTipHost : NodeAttachmentState
{
    /// <summary>The state of a box whose spec is not wired (yet): no handlers, nothing to detach.</summary>
    internal static readonly NodeAttachmentState Inert = new InertState();
    private sealed class InertState : NodeAttachmentState { }

    // ── ONE bubble at a time (WinUI's s_pToolTipServiceMetadata single open tooltip) ─────────────────────────────────
    // WinUI's ToolTipService keeps ONE automatic tooltip open per thread and closes it before opening the next
    // (ToolTipService_Partial.cpp CloseAutomaticToolTip / s_tpCurrentToolTip). Two tooltips are never right (there is one
    // pointer and one question), so this is a blanket rule rather than an opt-in. The owner is the per-node host (stable for the
    // wrapper's life); the closer is its CloseNow. UI thread only.
    private static ToolTipHost? s_openOwner;
    private static Action? s_openCloser;

    /// <summary><see cref="ToolTip.CloseOpen"/>.</summary>
    internal static void CloseOpen() => s_openCloser?.Invoke();

    private readonly NodeHandle _node;          // the wrapper: the owner's anchor (the bubble's placement rect, the safe zone)
    private readonly IAttachmentHost _host;
    private ToolTipSpec _spec;

    // ── State (the component's former Ref cells) ──────────────────────────────────────────────────────────────────────
    private OverlayHandle? _h;
    private bool _autoOpened;
    private Point2 _lastPointerLocal;           // last hover position (wrapper-local) → Mouse placement
    private bool _dismissedUntilLeave;          // press- or timeout-dismissed: no re-open until leave + re-enter
    private NodeHandle _bubbleNode;             // the open bubble's realized node → its rect joins the safe zone
    // The text the OPEN bubble was opened WITH — the owner's identity, as far as a bubble can observe it. A rail/list row whose
    // identity changes IN PLACE rebinds this host to the new item's spec, so an open bubble would survive onto a DIFFERENT owner;
    // the stranded-bubble guard closes it the moment the two disagree. Text is the only identity the wrapper actually has, so a
    // live-updating text on ONE owner also closes the bubble; that is the safe direction (a tooltip re-opens on the next hover).
    private string _openedText = "";
    private long _openedAtMs;                   // monotonic ms at open → the 5s dwell survives 2↔3 phase flips
    private bool _keyboardMode;                 // 3-state input mode of the pending/open tooltip (WinUI AutomaticToolTipInputMode)
    // GetTickCount()-style monotonic ms at the last close → re-show detection (BETWEEN_SHOW_DELAY_MS window).
    private long _lastClosedAtMs = long.MinValue / 2;

    // Timer phase: 0 = idle, 1 = show-delay counting down (open after it), 2 = bubble open (auto-dismiss counting down),
    // 3 = bubble open + pointer outside owner ∪ bubble (the 1s safe-zone grace counting down). Signals so a change runs
    // ClockEffect in the reactive flush, exactly where the component's re-render ran; created on the first activation.
    private Signal<int>? _phase, _safePoll;
    private Effect? _clockFx;
    private long _armedKey, _gen;
    private int _armedPhase;
    private HostTimerQueue? _queue;     // the queue the armed one-shot sits in (a cancel needs no context walk from a dying node)
    private Action<long>? _fire;

    private readonly Action<Point2> _onEnter;
    private readonly Action _onLeave;
    private readonly Action<PointerEventArgs> _onPressed;
    private readonly Action<bool> _onFocus;
    private Func<Element>? _bubbleContent;
    private Action<NodeHandle>? _onBubbleRealized;
    private Action? _closeNow, _strandedGuard, _openOnMount;

    internal ToolTipHost(NodeHandle node, IAttachmentHost host, ToolTipSpec spec)
    {
        _node = node;
        _host = host;
        _spec = spec;
        _onEnter = OnEnter;
        _onLeave = OnLeave;
        _onPressed = OnPressed;
        _onFocus = OnFocus;
        Wire(spec.Wired);
        if (spec.OpenOnMount) _host.EnqueuePassive(_openOnMount ??= OpenOnMountEffect);   // UseEffect(…, OpenOnMount) at mount
    }

    /// <summary>A re-render rebound this node to <paramref name="spec"/> (the component's re-pushed props).</summary>
    internal void Update(ToolTipSpec spec)
    {
        var old = _spec;
        _spec = spec;
        if (old.Wired != spec.Wired) Wire(spec.Wired);
        // UseEffect(StrandedGuard, text): after present, when the text changed.
        if (!string.Equals(old.Text, spec.Text, StringComparison.Ordinal)) _host.EnqueuePassive(_strandedGuard ??= StrandedGuardEffect);
        if (spec.OpenOnMount && !old.OpenOnMount) _host.EnqueuePassive(_openOnMount ??= OpenOnMountEffect);
    }

    // Wired: the four triggers (mouse-enter makes the target hit-testable for hover; leave cancels pending / closes open; a press
    // over the target dismisses; keyboard focus in/out of the target subtree). Unwired: none, and no clock (the component's
    // wrapper dropped its clock child while it had no tooltip); wiring again re-arms the current phase's clock from now, as that
    // child's remount did.
    private void Wire(bool wired)
    {
        OnHoverMove = wired ? _onEnter : null;
        OnPointerExit = wired ? _onLeave : null;
        OnPointerPressed = wired ? _onPressed : null;
        OnFocusChanged = wired ? _onFocus : null;
        if (!wired) Disarm();
        else if (_phase is not null) ArmFor(_phase.Peek(), _safePoll!.Peek());
    }

    private int PhaseNow => _phase?.Peek() ?? 0;

    private void SetPhase(int value)
    {
        if (_phase is null)
        {
            if (value == 0) return;
            _safePoll = new Signal<int>(0);
            _phase = new Signal<int>(0);
            _clockFx = new Effect(_host.Runtime, ClockEffect);
        }
        _phase.Value = value;
    }

    private bool Transient => !float.IsNaN(_spec.ShowDelayMs);
    private IOverlayService Svc => _host.PeekContext(_node, Overlay.Service);
    private InputHooks Hooks => _host.PeekContext(_node, InputHooks.Current);

    // ── The clock (the component's ToolTipClock child) ────────────────────────────────────────────────────────────────
    private void ClockEffect()
    {
        int ph = _phase!.Value;      // subscribe: a phase change re-runs this in the reactive flush
        int poll = _safePoll!.Value; // subscribe: an in-zone safe-zone elapse re-arms a fresh 1s poll
        if (!_spec.Wired) { Disarm(); return; }
        ArmFor(ph, poll);
    }

    private void ArmFor(int ph, int poll)
    {
        // The component keyed its clock by phase ("tt-open-timer" / "tt-close-timer" / "tt-safezone-timer:" + poll): the same key
        // kept the armed one-shot, a new key remounted (re-armed) it, phase 0 had none.
        long key = ph == 0 ? 0 : ph == 3 ? 3L | ((long)(uint)poll << 8) : ph;
        if (key == _armedKey) return;
        Disarm();
        if (ph == 0) return;
        // GetInitialShowDelay: Mouse ×2 normal / ×1 reshow (truncated 1.5 — see MouseReshowDelayMs); Keyboard ×2 always. The
        // per-element override (see Wrap) replaces both MOUSE legs and leaves the keyboard leg alone, clamped at 0 (a negative span
        // is a deadline in the past, not a shorter delay; 0 itself means "the next frame's drain").
        bool isReshow = Environment.TickCount64 - _lastClosedAtMs < (long)ToolTip.BetweenShowDelayMs;
        float delay = _keyboardMode ? ToolTip.KeyboardShowDelayMs
                    : Transient ? MathF.Max(0f, _spec.ShowDelayMs)
                    : (isReshow ? ToolTip.MouseReshowDelayMs : ToolTip.MouseShowDelayMs);
        // The REMAINING show-duration dwell: WinUI's m_tpCloseTimer is armed once per open and keeps running while the safe-zone
        // monitor watches, so the 2↔3 phase flips must not restart the 5s — the re-arm takes whatever dwell is left.
        float dwellLeft = MathF.Max(1f, ToolTip.ShowDurationMs - (Environment.TickCount64 - _openedAtMs));
        float ms = ph == 1 ? delay : ph == 2 ? dwellLeft : ToolTip.SafeZoneCheckMs;
        _armedKey = key;
        _armedPhase = ph;
        // ToolTipClock's UseTimeout: NowMs + max(ms, 0) on the nearest host timer queue, owner named ToolTipClock in the wake census.
        var queue = _queue = _host.PeekContext(_node, HostTimers.Current);
        if (queue is null) return;
        _gen++;
        queue.Schedule(queue.NowMs + MathF.Max(ms, 0f), _gen, _fire ??= OnTimer, typeof(ToolTipClock));
    }

    private void Disarm()
    {
        if (_armedKey == 0) return;
        _armedKey = 0;
        if (_fire is not null) _queue?.Cancel(_fire);
        _queue = null;
        _gen++;
    }

    private void OnTimer(long g)
    {
        if (g != _gen) return;   // cancelled or re-armed since (the TimeoutCell's generation guard)
        switch (_armedPhase)
        {
            case 1: OpenNow(); break;
            case 2: AutoDismiss(); break;
            case 3: SafeZoneCheck(); break;
        }
    }

    // ── The ToolTipService mechanics ──────────────────────────────────────────────────────────────────────────────────
    void OpenNow()
    {
        SetPhase(2);   // bubble open → arm the auto-dismiss countdown
        if (_h is { IsOpen: true }) return;
        // One bubble at a time: whoever owns the screen loses it here, before this one is placed (see s_openOwner).
        if (!ReferenceEquals(s_openOwner, this)) s_openCloser?.Invoke();
        s_openOwner = this;
        s_openCloser = _closeNow ??= CloseNow;
        _openedAtMs = Environment.TickCount64;   // dwell epoch (m_tpCloseTimer is armed once per open)
        _openedText = _spec.Text;                // identity epoch — see _openedText (a rebound owner closes it)
        // A tooltip never traps focus and never light-dismisses on outside click — it is transient and dismissal
        // is driven by hover/focus-leave + press + the auto-dismiss timer (ToolTipService owns close, not the user).
        //
        // Chrome: the fade (Raw) for a normal tooltip; NONE (Static) for a transient one. A 167ms fade-out is a
        // ghost the next bubble's fade-in overlaps, which is precisely what "instant" must not look like — a data
        // tip that re-anchors as the pointer crosses a strip has to be exactly one bubble on every frame.
        var options = new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None,
                                       Chrome: Transient ? PopupChrome.Static : PopupChrome.Raw);
        _onBubbleRealized ??= x => _bubbleNode = x;
        _bubbleContent ??= () => BubbleContent(_spec.Text, _onBubbleRealized);
        var scene = _host.Scene;
        var node = _node;
        if (_spec.Placement == ToolTipPlacementMode.Mouse && !_keyboardMode)
        {
            // PlacementMode.Mouse: top-left at the pointer, 11px below it (ToolTip_Partial.cpp:976-977
            // "align ToolTip with the bottom left corner of mouse bounding rectangle"; .h:56 offset = 11). The
            // positioner adds FlyoutMargin (4) below a Bottom-placed anchor, so the synthetic point-rect carries
            // the remaining 7; collisions flip it above the pointer.
            var local = _lastPointerLocal;
            _h = Svc.OpenAt(
                () =>
                {
                    RectF abs = !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                    return new RectF(abs.X + local.X, abs.Y + local.Y + (ToolTip.MousePlacementVerticalOffset - FlyoutPositioner.FlyoutMargin), 0f, 0f);
                },
                _bubbleContent,
                FlyoutPlacement.BottomEdgeAlignedLeft,
                options,
                owner: () => node);
        }
        else
        {
            // WinUI default PlacementMode.Top: CENTERED above the target (ToolTip_Partial.cpp:1119-1122 default),
            // against the target rect INFLATED by the input-mode offset — DEFAULT_MOUSE_OFFSET 20 /
            // DEFAULT_KEYBOARD_OFFSET 12 on both axes (ToolTip_Partial.h:10-11; cpp:1224-1258
            // InflateRect(&rcDockTo, horizontalOffset, verticalOffset) feeds QueryRelativePosition at :1275).
            // The positioner adds FlyoutMargin (4) in the major direction, so the synthetic rect carries the rest;
            // FlyoutPositioner flips below on a collision.
            bool keyboard = _keyboardMode;
            _h = Svc.OpenAt(
                () =>
                {
                    RectF abs = !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                    float inflate = (keyboard ? ToolTip.KeyboardPlacementOffset : ToolTip.MousePlacementOffset) - FlyoutPositioner.FlyoutMargin;
                    return new RectF(abs.X - inflate, abs.Y - inflate, abs.W + inflate * 2f, abs.H + inflate * 2f);
                },
                _bubbleContent,
                FlyoutPlacement.Top,
                options,
                owner: () => node);
        }
    }

    void CloseNow()
    {
        bool wasOpen = PhaseNow is 2 or 3;
        if (_h is { IsOpen: true } o) o.Close();
        _h = null;
        if (ReferenceEquals(s_openOwner, this)) { s_openOwner = null; s_openCloser = null; }
        _bubbleNode = default;   // the bubble unmounts — its rect leaves the safe zone
        if (wasOpen) _lastClosedAtMs = Environment.TickCount64;   // mark close start for the re-show window
        _keyboardMode = false;
        SetPhase(0);
    }

    // The SPI_GETMESSAGEDURATION dwell elapsed: close AND latch until leave + re-enter — WinUI's only show trigger
    // is PointerEntered (ToolTipService_Partial.cpp:1395-1418 OnOwnerPointerEntered), so in-place hover moves can
    // never re-open a timed-out tooltip (the owner re-enters the show path via a real exit + enter only).
    void AutoDismiss() { _dismissedUntilLeave = true; CloseNow(); }

    // Pointer-enter (OnHoverMove fires on any move while hovering): begin the initial-show-delay countdown if idle.
    // ToolTipService.OnOwnerEnterInternal — Mouse mode, reshow if the previous tooltip closed < 200ms ago.
    void OnEnter(Point2 local)
    {
        _lastPointerLocal = local;   // tracked even while open — WinUI re-reads the current point at placement
        if (PhaseNow == 3) { SetPhase(2); return; }   // back inside the safe zone → cancel the 1s grace
        if (_dismissedUntilLeave) return;   // dismissed: moves while still hovering must NOT re-arm (the WinUI
                                            // owner stays out of m_nestedOwners until a real leave + re-enter)
        if (PhaseNow != 0 || (_h is { IsOpen: true })) return;
        _keyboardMode = false;
        SetPhase(1);   // show-delay counting down
    }

    // Pointer-leave: cancel a PENDING open (ToolTipService_Partial.cpp:1435-1442 — "Cancel the ToolTip if it had
    // not been opened yet"), but KEEP an open bubble: WinUI's owner-exit only records the owner and lets the
    // safe-zone monitor close it once the pointer is outside owner ∪ tooltip (cpp:1443-1453; the 1s check timer,
    // cpp:349-381 + .h:22). A leave also lifts the press/timeout dismiss latch (the next enter may show again).
    void OnLeave()
    {
        _dismissedUntilLeave = false;
        // A transient (instant-show) bubble is hit-test-invisible chrome over a data surface: there is nothing in
        // it to travel INTO, which is the only thing the safe zone protects. It closes on the leave edge, full stop.
        if (Transient) { CloseNow(); return; }
        if (PhaseNow == 2) { SetPhase(3); return; }   // arm the 1s safe-zone poll (the bubble rect keeps it open via geometry)
        if (PhaseNow != 3) CloseNow();   // pending open → cancel
    }

    // The 1s safe-zone poll elapsed (phase 3): WinUI OnSafeZoneCheck / IsToolTipInSafeZone against the GLOBAL
    // pointer — the bubble is hit-test-invisible (a real tooltip never intercepts input), so bubble-hover is
    // detected by geometry, never by events. Owner re-entry is event-driven (OnEnter flips 3→2) but the owner
    // rect is tested too (WinUI tests owner ∪ tooltip). The 5s dwell stays authoritative while parked in-zone.
    void SafeZoneCheck()
    {
        if (Environment.TickCount64 - _openedAtMs >= (long)ToolTip.ShowDurationMs) { AutoDismiss(); return; }
        var scene = _host.Scene;
        var on = _node;
        // The owner DIED under the bubble (its subtree was relaid out or removed while the pointer sat on it, or a
        // drag holds capture), so the leave edge that would have closed this is never coming. A dead anchor is a
        // close, not another poll interval.
        if (!on.IsNull && !scene.IsLive(on)) { CloseNow(); return; }
        if (Hooks.GetPointerPosition?.Invoke() is { } pt)
        {
            var bn = _bubbleNode;
            bool inside = (!on.IsNull && scene.IsLive(on) && ToolTip.InRect(scene.AbsoluteRect(on), pt))
                       || (!bn.IsNull && scene.IsLive(bn) && ToolTip.InRect(scene.AbsoluteRect(bn), pt));
            if (inside) { _safePoll!.Value = _safePoll.Peek() + 1; return; }   // stay open — re-arm a fresh poll
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
        if (PhaseNow != 0 || _h is { IsOpen: true }) return;
        var scene = _host.Scene;
        var focused = Hooks.GetFocus?.Invoke() ?? NodeHandle.Null;
        bool keyboardFocus = !focused.IsNull && scene.IsLive(focused) && (scene.Flags(focused) & NodeFlags.FocusVisual) != 0;
        if (!keyboardFocus) return;
        _keyboardMode = true;
        SetPhase(1);
    }

    // Pointer press over the target: dismiss an open bubble (and a pending one), latched until leave + re-enter.
    // Press-dismiss is classic Win32/WPF tooltip behavior we keep deliberately — WinUI 3's ToolTipService registers
    // no PointerPressed handler (ToolTipService_Partial.cpp:176-220; it closes via safe-zone exit, cpp:1437-1453)
    // but equally cannot re-open a dismissed owner until a real leave + re-enter (cpp:725-737). There is no
    // click-to-toggle — the wrapper adds NO OnClick, so it never becomes a tab stop or intercepts activation.
    void OnPressed(PointerEventArgs _)
    {
        if (PhaseNow == 0 && _h is not { IsOpen: true }) return;
        _dismissedUntilLeave = true;
        CloseNow();
    }

    void OpenOnMountEffect()
    {
        if (!_spec.OpenOnMount || _autoOpened) return;
        _autoOpened = true;
        OpenNow();
    }

    // STRANDED-BUBBLE GUARD. Two ways an open bubble outlives the thing it describes, both ending with a tip anchored to the
    // wrong item (or to the viewport origin) until the 5s dwell finally fires: the owner's IDENTITY changed in place (a recycled
    // rail/list row rebinds this host to the new item's spec), or the anchor node DIED with no leave edge (relaid out or
    // destroyed under a still pointer, or a drag holds capture). Runs with the passive effects after a text change (the
    // component's UseEffect(…, text)); CloseNow writes signals, which a reconcile must not.
    void StrandedGuardEffect()
    {
        if (PhaseNow is not (2 or 3) && _h is not { IsOpen: true }) return;
        if (!string.Equals(_openedText, _spec.Text, StringComparison.Ordinal)) { CloseNow(); return; }
        if (!_node.IsNull && !_host.Scene.IsLive(_node)) CloseNow();
    }

    // Owner unmount must not orphan the bubble. The OverlayHandle is the ONLY thing an unmounting owner still owns that the host
    // does not: the entry lives in OverlayServiceImpl.Entries and leaves only via Closing → AfterAnimations → Finalize, so a
    // wrapper that disappears while open leaves a live entry whose rect thunk now resolves against a DEAD node (the bubble walks to
    // the viewport's top-left corner and stays). Handle-only teardown ON PURPOSE (the component's teardown effect): no phase
    // writes into a dying owner. The clock is cancelled and the clock effect disposed with it.
    protected override void Detach()
    {
        if (_h is { IsOpen: true } o) o.Close();
        _h = null;
        // Release the single-bubble latch too: a torn-down owner's closer would leave the NEXT tooltip believing something else
        // still owns the screen.
        if (ReferenceEquals(s_openOwner, this)) { s_openOwner = null; s_openCloser = null; }
        Disarm();
        _clockFx?.Dispose();
        _clockFx = null;
    }

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
        // The bubble's look, read now (the bubble is built in the overlay's render).
        var st = ToolTip.DefaultStyle;
        var bubble = new BoxEl
        {
            Fill = st.Fill,
            Acrylic = st.Acrylic,
            BorderColor = st.Border,
            BorderWidth = st.BorderWidth,
            Corners = CornerRadius4.All(st.Radius),
            Shadow = st.Shadow,
            MaxWidth = st.MaxWidth,
            Padding = st.Padding,
            // A tooltip never intercepts input (WinUI tooltips are hit-test-transparent): the bubble must not swallow
            // wheel/press under the pointer — scrolling continues through it. Its bounds still join the safe zone,
            // tested by GEOMETRY against the real pointer (SafeZoneCheck), not by hover events.
            HitTestVisible = false,
            OnRealized = onRealized,   // fresh mount per open → the capture cannot go stale (unlike a recycled row)
            Children =
            [
                new TextEl(text)
                {
                    Size = st.TextSize,
                    Color = st.TextColor,
                    Wrap = TextWrap.Wrap,
                    MaxWidth = st.MaxWidth - st.Padding.Left - st.Padding.Right,   // 320 − (9 + 9) horizontal padding
                },

            ],
        };
        // Parts: restyle the bubble chrome (acrylic, stroke, elevation, padding…); the Text content, the input
        // transparency, and the safe-zone node capture always win.
        return _spec.Parts.Apply(ToolTip.PartBubble, bubble) with { Children = bubble.Children, HitTestVisible = false, OnRealized = onRealized };
    }
}

/// <summary>
/// One-shot countdown, mounted by <see cref="CommandBarFlyout"/> / <see cref="MenuFlyout"/> / <see cref="Slider"/> only while a
/// close-fade completion or a similar one-shot is pending (a tooltip arms the same host one-shot directly from its per-node
/// <see cref="ToolTipHost"/>, under this type's name in the wake census). It is
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
