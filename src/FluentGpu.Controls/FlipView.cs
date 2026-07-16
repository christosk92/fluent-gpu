using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A WinUI FlipView: a clipped frame showing exactly one item at a time, with thin acrylic navigation bars
/// hugging the frame edges (no dot/pip indicator — WinUI FlipView has none). Selection CLAMPS at the ends (MoveNext /
/// MovePrevious, FlipView_Partial.cpp:131-139/:165-171 — no wrap-around) and the end bar is not rendered
/// (nothingPrevious/nothingNext, :1242-1278). The bars are hidden until pointer movement / keyboard focus shows them,
/// then fade out after FLIP_VIEW_BUTTONS_SHOW_DURATION_MS = 3000ms (FlipView_Partial.h:13). Adjacent selection changes
/// slide (UseTouchAnimationsForAllNavigation, default TRUE — FlipView_Partial.cpp:1639-1645); non-adjacent changes jump
/// (SetOffsetToSelectedIndex, :1719). Wheel flips one item per distinct event (200ms throttle, FlipView_Partial.h:10),
/// arrow keys navigate (Selector::HandleNavigationKey, Selector_Partial.cpp:3694-3773), and the content pans with a
/// drag, snapping MandatorySingle on release (ScrollingHost snap points, FlipView_themeresources.xaml:297). All items
/// are realized (no virtualization — WinUI's VirtualizingStackPanel is out of scope for this control surface).</summary>
public static class FlipView
{
    /// <summary>String-item convenience: each entry renders centered at 20px primary text.</summary>
    public static Element Create(IReadOnlyList<string> items, float width = 400f, float height = 240f,
                                 int selectedIndex = 0, Action<int>? onSelectionChanged = null,
                                 bool useTouchAnimationsForAllNavigation = true, bool vertical = false)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
            cells[i] = new BoxEl
            {
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(items[i]) { Size = 20f, Color = Tok.TextPrimary }],
            };
        return Create(cells, width, height, selectedIndex, onSelectionChanged, useTouchAnimationsForAllNavigation, vertical);
    }

    /// <summary>Arbitrary per-item content (the WinUI FlipViewItem surface — FlipViewItem_themeresources.xaml:23-25).
    /// Each item fills its page cell (FlipViewItem Horizontal/VerticalContentAlignment = Stretch, :17-18).
    /// <paramref name="selectedIndex"/> is live-controlled: a changed value navigates the view (the Selector
    /// SelectedIndex DP, FlipView_Partial.cpp:1607-1613); internal navigation reports through
    /// <paramref name="onSelectionChanged"/> (SelectionChanged).</summary>
    public static Element Create(IReadOnlyList<Element> items, float width = 400f, float height = 240f,
                                 int selectedIndex = 0, Action<int>? onSelectionChanged = null,
                                 bool useTouchAnimationsForAllNavigation = true, bool vertical = false)
        => Ctx.Provide(Props.Channel,
                       new Props(items, width, height, selectedIndex, onSelectionChanged,
                                 useTouchAnimationsForAllNavigation, vertical),
                       Embed.Comp(() => new FlipViewCore()));

    /// <summary>Controlled props via context — a reused ComponentEl never re-runs its factory, so props must flow
    /// through a provider (the SelectorBar/PipsPager convention).</summary>
    internal sealed record Props(IReadOnlyList<Element> Items, float Width, float Height, int SelectedIndex,
                                 Action<int>? OnSelectionChanged, bool UseTouchAnimations, bool Vertical)
    {
        internal static readonly Context<Props?> Channel = new(null);
    }
}

/// <summary>The stateful core: the clamped Selector index, the sliding item strip, the 3s-fading navigation bars,
/// the 200ms-throttled wheel flips, arrow-key navigation, and drag panning with MandatorySingle snap.</summary>
internal sealed class FlipViewCore : Component
{
    const string HPrevGlyph = "";  // ChevronLeftSmall — HorizontalPreviousTemplate (FlipView_themeresources.xaml:193)
    const string HNextGlyph = "";  // ChevronRightSmall — HorizontalNextTemplate (:145)
    const string VPrevGlyph = "";  // ChevronUpSmall — VerticalPreviousTemplate (:289)
    const string VNextGlyph = "";  // ChevronDownSmall — VerticalNextTemplate (:241)

    const float ButtonFontSize = 8f;          // FlipViewButtonFontSize (FlipView_themeresources.xaml:74)
    const float ButtonScalePressed = 0.875f;  // FlipViewButtonScalePressed (:75)
    const float BarMain = 16f;                // nav button 16x38 horizontal / 38x16 vertical (:300-303)
    const float BarCross = 38f;
    const float BarMargin = 1f;               // Margin="1" on all four nav buttons (:300-303)
    const float ButtonsShowDurationMs = 3000f;// FLIP_VIEW_BUTTONS_SHOW_DURATION_MS (FlipView_Partial.h:13)
    const float WheelDelayMs = 200f;          // FLIP_VIEW_DISTINCT_SCROLL_WHEEL_DELAY_MS (FlipView_Partial.h:10)
    const float PanSlopPx = 4f;               // the engine drag-box convention (ListViewBaseItem_Partial.cpp:1864-1878)
    // Hi-res packet stream (precision-touchpad two-finger swipe, arriving via the dispatcher's no-scroller wheel fallback
    // — InputDispatcher §A). These packets are sub-notch, so they accumulate until a flip's worth of travel is reached.
    const float SwipeFlipDip = 80f;           // accumulated hi-res travel per flip
    const float SwipeNotchDip = 48f;          // |axis| at/above this = a discrete detent (mouse notch ≈ 60 DIP)
    const float SwipeCooldownMs = 350f;       // post-flip refractory for the packet stream (inertia keeps delivering)
    // Fling-distance projection divisor for the MandatorySingle commit. A release of speed v (px/s) coasts an extra
    // v / FlickProjectK px in the snap-settle window before resting; the projected offset is then snapped to the nearest
    // index. Derived from the engine scroller decay (ScrollIntegrator.FlingDecayPerS = 0.05/s survival) over the ControlNormal settle
    // window T: coast = v·(1−decay^T)/−ln(decay), so the divisor is −ln(decay)/(1−decay^T). A BOUNDED window (not the full
    // infinite scroll coast) is the right model for a page snap — a slow under-50% drag springs back, a flick navigates. ≈ 4.0.
    static readonly float FlickProjectK = ProjectDivisor(ScrollIntegrator.FlingDecayPerS, Motion.ControlNormal / 1000f);
    static float ProjectDivisor(float decayPerS, float windowS)
    {
        float k = -MathF.Log(decayPerS);                 // the per-second decay rate (−ln survival)
        float frac = 1f - MathF.Exp(-k * windowS);       // fraction of the full coast reached within the window
        return frac > 1e-4f ? k / frac : k;              // divisor: projectedExtra = v / divisor = v·frac/k
    }

    public override Element Render()
    {
        // Hooks — stable order, unconditionally.
        var props = UseContext(FlipView.Props.Channel);
        var hooks = UseContext(InputHooks.Current);   // PointerVelocity: real flick speed for the touch commit
        var (idx, setIdx) = UseState(Math.Max(0, props?.SelectedIndex ?? 0));
        var stripRef = UseRef<NodeHandle>(default);
        var stripSeeded = UseRef(false);
        var lastIdx = UseRef(Math.Max(0, props?.SelectedIndex ?? 0));
        var lastExternal = UseRef(props?.SelectedIndex ?? 0);
        var panKeys = UseRef(new Keyframe[2]);   // reused pan-follow keyframes (0-alloc per pointer move)
        var panStart = UseRef(0f);
        var panBase = UseRef(0f);
        var panning = UseRef(false);
        var panOffset = UseRef(0f);
        var showButtons = UseSignal(false);      // m_showNavigationButtons — hidden by default
        var keepButtonsVisible = UseRef(false);  // m_keepNavigationButtonsVisible (pointer over a nav button)
        var fadeDeadline = UseRef(0L);
        var fadePending = UseSignal(false);
        var lastWheelTime = UseRef(0L);          // m_lastScrollWheelTime
        var lastWheelDelta = UseRef(0f);         // m_lastScrollWheelDelta
        var swipeAccum = UseRef(0f);             // accumulated hi-res axis travel toward the next flip
        var swipeLastMs = UseRef(0L);            // last hi-res packet time (a gap resets the accumulator)
        var swipeCooldownUntil = UseRef(0L);     // post-flip refractory deadline (inertia tail can't re-flip)

        var p = props;
        int count = p?.Items.Count ?? 0;
        float w = p?.Width ?? 400f, h = p?.Height ?? 240f;
        bool vertical = p?.Vertical ?? false;
        bool animations = p?.UseTouchAnimations ?? true;
        float extent = vertical ? h : w;
        var ch = vertical ? AnimChannel.TranslateY : AnimChannel.TranslateX;
        int cur = count == 0 ? 0 : Math.Clamp(idx, 0, count - 1);

        // A changed external SelectedIndex wins (the Selector SelectedIndex DP set path — FlipView_Partial.cpp
        // OnSelectedIndexChanged :1624-1721 — which also raises SelectionChanged, :1607-1613).
        int external = p?.SelectedIndex ?? 0;
        UseEffect(() =>
        {
            if (external == lastExternal.Value) return;
            lastExternal.Value = external;
            int clamped = Math.Clamp(external, 0, Math.Max(0, count - 1));
            if (clamped == cur) return;
            setIdx(clamped);
            p?.OnSelectionChanged?.Invoke(clamped);
        }, external);

        // Strip offset follows the selection. Adjacent ±1 changes glide (WinUI ScrollViewer.BringIntoViewport DManip
        // inertia when UseTouchAnimationsForAllNavigation, FlipView_Partial.cpp:1643-1699 — substituted with the
        // engine 250ms FluentPopOpen tween, the sanctioned spring/eased substitution); non-adjacent changes jump
        // (SetOffsetToSelectedIndex, :1719). A mid-flight retarget starts from the LIVE offset (no snap).
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || stripRef.Value.IsNull || !scene.IsLive(stripRef.Value)) return;
            float to = -cur * extent;
            int prev = lastIdx.Value;
            lastIdx.Value = cur;
            if (!stripSeeded.Value)
            {
                stripSeeded.Value = true;
                anim.Animate(stripRef.Value, ch, to, to, 1f, Easing.Linear);   // seed the resting offset, no motion
                return;
            }
            bool adjacent = Math.Abs(cur - prev) == 1;
            if (adjacent && animations && !Motion.ReducedMotion)
            {
                float from = anim.TryGetTrackValue(stripRef.Value, ch, out float live) ? live : -prev * extent;
                anim.Animate(stripRef.Value, ch, from, to, Motion.ControlNormal, Easing.FluentPopOpen);
            }
            else
            {
                anim.Animate(stripRef.Value, ch, to, to, 1f, Easing.Linear);   // jump
            }
        }, cur, count, vertical, w, h, animations);

        // ── Selection (clamped — MoveNext stops at nCount-1, MovePrevious at 0; FlipView_Partial.cpp:131-139/:165-171)
        void Select(int target)
        {
            target = Math.Clamp(target, 0, count - 1);
            if (target == cur) return;
            setIdx(target);
            p?.OnSelectionChanged?.Invoke(target);
        }
        bool MoveNext()
        {
            if (count == 0 || cur >= count - 1) return false;
            Select(cur + 1);
            return true;
        }
        bool MovePrevious()
        {
            if (count == 0 || cur <= 0) return false;
            Select(cur - 1);
            return true;
        }

        // ── Nav-button show/fade (ResetButtonsFadeOutTimer / HideButtonsImmediately, FlipView_Partial.cpp:1867-1927)
        void ShowButtonsAndArmFade()
        {
            showButtons.Value = true;
            fadeDeadline.Value = Environment.TickCount64 + (long)ButtonsShowDurationMs;
            fadePending.Value = true;
        }
        void HideButtonsImmediately()
        {
            fadePending.Value = false;
            if (!keepButtonsVisible.Value) showButtons.Value = false;   // pointer over a button keeps them (:1904-1908)
        }

        // ── Wheel: one flip per distinct event — direction change, or 200ms since the last delta
        //    (FlipView::OnPointerWheelChanged, FlipView_Partial.cpp:650-749). Generalized to the DOMINANT axis so a
        //    horizontal wheel / two-finger swipe (e.DeltaX) pages too, plus a hi-res accumulation path for the sub-notch
        //    packet stream that the dispatcher's no-scroller wheel fallback (InputDispatcher §A) now delivers.
        void OnWheel(WheelEventArgs e)
        {
            if (e.Handled || count == 0) return;
            if ((e.Mods & KeyModifiers.Ctrl) != 0) return;   // Ctrl+wheel ignored (:666-668)

            bool horizPacket = MathF.Abs(e.DeltaX) > MathF.Abs(e.Delta);   // dominant axis owns this packet
            float axis = horizPacket ? e.DeltaX : e.Delta;
            if (axis == 0f) return;
            long now = Environment.TickCount64;

            if (MathF.Abs(axis) >= SwipeNotchDip)
            {
                // Detented wheel — the existing one-flip-per-distinct-event behavior, generalized to the dominant axis.
                bool directionChange = (axis < 0f && lastWheelDelta.Value >= 0f) ||
                                       (axis > 0f && lastWheelDelta.Value <= 0f);   // (:687-692)
                bool canFlip = directionChange || now - lastWheelTime.Value > (long)WheelDelayMs;
                // Recorded whether a flip happened or not, so a touchpad flick (deltas spread over seconds) can't
                // re-trigger every 200ms (:708-716).
                lastWheelTime.Value = now;
                if (canFlip)
                {
                    bool moved = axis < 0f ? MoveNext() : MovePrevious();   // axis<0 = next (:719-726)
                    if (moved)
                    {
                        lastWheelDelta.Value = axis;
                        e.Handled = true;   // a flip consumes the wheel (:728-733)
                    }
                    // NOT handled at the first/last item so the parent viewport scroll-chains (:736).
                }
                else
                {
                    e.Handled = true;       // throttled: still consumed so ancestors don't scroll (:738-744)
                }
                return;
            }

            // Hi-res packet stream (precision touchpad, via the dispatcher's no-scroller wheel fallback). Sub-notch
            // packets accumulate until a flip's worth of travel; a gap resets the segment, and a post-flip cooldown
            // keeps the inertia tail from cascading extra flips.
            if (now - swipeLastMs.Value > (long)WheelDelayMs) swipeAccum.Value = 0f;   // new segment
            swipeLastMs.Value = now;
            if (now < swipeCooldownUntil.Value) { e.Handled = true; return; }
            swipeAccum.Value += axis;
            if (MathF.Abs(swipeAccum.Value) >= SwipeFlipDip)
            {
                bool moved = swipeAccum.Value < 0f ? MoveNext() : MovePrevious();
                swipeAccum.Value = 0f;
                swipeCooldownUntil.Value = now + (long)SwipeCooldownMs;
                if (moved) e.Handled = true;
                // At the ends: leave unhandled so the gesture can chain outward, matching the notch path (:736).
            }
            else
            {
                e.Handled = true;   // building toward a flip — swallow partials
            }
        }

        // ── Keyboard: Left/Up = previous, Right/Down = next, Home/End = first/last
        //    (Selector::HandleNavigationKey, Selector_Partial.cpp:3694-3773); keyboard shows the buttons (:1471-1473).
        void OnKey(KeyEventArgs a)
        {
            if (a.Handled || count == 0) return;
            bool moved;
            switch (a.KeyCode)
            {
                case Keys.Left or Keys.Up: moved = MovePrevious(); break;
                case Keys.Right or Keys.Down: moved = MoveNext(); break;
                case Keys.Home: moved = cur != 0; Select(0); break;
                case Keys.End: moved = cur != count - 1; Select(count - 1); break;
                default: return;
            }
            if (moved)
            {
                a.Handled = true;
                ShowButtonsAndArmFade();
            }
        }

        // ── Drag panning with MandatorySingle snap (ScrollingHost Horizontal/VerticalSnapPointsType=MandatorySingle,
        //    FlipView_themeresources.xaml:297; release rounds offset/viewport to the nearest index,
        //    FlipView_Partial.cpp:441-536). The gesture rails to one viewport either side of its start (the
        //    MandatorySingle DManip restriction), so the commit is always the adjacent item.
        void PanDown(Point2 lp)
        {
            if (count == 0) return;
            panStart.Value = vertical ? lp.Y : lp.X;
            var anim = Context.Anim;
            panBase.Value = anim is not null && !stripRef.Value.IsNull &&
                            anim.TryGetTrackValue(stripRef.Value, ch, out float live)
                ? live
                : -cur * extent;
            panOffset.Value = panBase.Value;
            panning.Value = false;
        }
        void PanMove(Point2 lp)
        {
            if (count == 0 || stripRef.Value.IsNull || extent <= 0f) return;
            float delta = (vertical ? lp.Y : lp.X) - panStart.Value;
            if (!panning.Value && MathF.Abs(delta) < PanSlopPx) return;
            panning.Value = true;
            float off = Math.Clamp(panBase.Value + delta, panBase.Value - extent, panBase.Value + extent);
            off = Math.Clamp(off, -(count - 1) * extent, 0f);
            var keys = panKeys.Value;
            keys[0] = new Keyframe(0f, off, Easing.Linear);
            keys[1] = new Keyframe(1f, off, Easing.Linear);
            Context.Anim?.Keyframes(stripRef.Value, ch, keys, 1f);   // follow the pointer (seed-style, 0-alloc)
            panOffset.Value = off;
        }
        void CommitPan()   // the OnDrag gesture's release/commit edge (implicit pointer capture)
        {
            if (!panning.Value || count == 0 || extent <= 0f) return;
            panning.Value = false;
            float live = panOffset.Value;
            // REAL release velocity from the arena VelocitySampler (px/s along the pan axis), projected to the inertia
            // resting offset the WinUI ScrollViewer DManip would settle at, then snapped MandatorySingle (rail-bounded to
            // ONE viewport either side of the start — FlipView_themeresources.xaml:297, FlipView_Partial.cpp:1643-1699).
            // With UseTouchAnimationsForAllNavigation a touch FLICK navigates even short of 50% because the projected
            // resting offset carries past the halfway snap; a slow drag commits only once it physically passes 50%.
            float vAxis = animations ? (vertical ? (hooks.PointerVelocity?.Invoke().Y ?? 0f)
                                                 : (hooks.PointerVelocity?.Invoke().X ?? 0f))
                                     : 0f;
            float projected = live + vAxis / FlickProjectK;   // + velocity ⇒ toward index 0 (offset rises toward 0)
            // Rail to ±1 viewport of the start index (MandatorySingle never skips more than one per gesture).
            float lo = -(cur + 1) * extent, hi = -(cur - 1) * extent;
            projected = Math.Clamp(projected, lo, hi);
            int target = Math.Clamp((int)MathF.Round(-projected / extent), 0, count - 1);
            target = Math.Clamp(target, cur - 1, cur + 1);   // adjacent only (the rail)
            if (target != cur)
            {
                setIdx(target);   // the selection effect glides from the live pan offset
                p?.OnSelectionChanged?.Invoke(target);
            }
            else if (!stripRef.Value.IsNull)
            {
                // Same index: spring back to rest from the live (un-projected) offset.
                Context.Anim?.Animate(stripRef.Value, ch, live, -cur * extent, Motion.ControlFast, Easing.FluentPopOpen);
            }
        }

        // ── Pointer presence: non-touch shows + re-arms the fade, touch hides immediately
        //    (OnPointerEntered/OnPointerMoved, FlipView_Partial.cpp:755-803).
        void OnPressed(PointerEventArgs pe)
        {
            if (pe.Kind == PointerKind.Touch) HideButtonsImmediately();
            else ShowButtonsAndArmFade();
        }

        // ── Nav bar: 16x38 (38x16 vertical) acrylic plate, the arrow recolors and scales on press — the plate doesn't.
        BoxEl NavBar(bool next)
        {
            string glyph = vertical ? (next ? VNextGlyph : VPrevGlyph) : (next ? HNextGlyph : HPrevGlyph);
            float ox = vertical ? (w - BarCross) / 2f : (next ? w - BarMain - BarMargin : BarMargin);
            float oy = vertical ? (next ? h - BarMain - BarMargin : BarMargin) : (h - BarCross) / 2f;
            return new BoxEl
            {
                Width = vertical ? BarCross : BarMain,
                Height = vertical ? BarMain : BarCross,
                Corners = Radii.ControlAll,        // CornerRadius="{TemplateBinding CornerRadius}" = ControlCornerRadius 4 (FlipView_themeresources.xaml:98, :300-303)
                // FlipViewNextPreviousButtonBackground[/PointerOver/Pressed] = AcrylicInAppFillColorDefaultBrush —
                // one in-app acrylic across ALL states (:7-9); no border (FlipViewButtonBorderThemeThickness = 0, :5).
                Fill = ColorF.Transparent,
                Acrylic = Tok.AcrylicFlyout,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                AlignSelf = FlexAlign.Start,
                OffsetX = ox, OffsetY = oy,
                Role = AutomationRole.Button,
                TabStop = false,                   // IsTabStop=False on the nav buttons (:300-303)
                OnClick = () =>
                {
                    if (next) MoveNext(); else MovePrevious();
                    ShowButtonsAndArmFade();       // OnNextButtonPartClick → ResetButtonsFadeOutTimer (FlipView_Partial.cpp:1155-1163)
                },
                // Pointer over a nav button keeps the buttons visible; leaving re-arms the 3s fade
                // (OnPointerEntered/ExitedNavigationButtons, FlipView_Partial.cpp:1168-1188).
                OnHoverMove = _ => { keepButtonsVisible.Value = true; showButtons.Value = true; },
                OnPointerExit = () => { keepButtonsVisible.Value = false; ShowButtonsAndArmFade(); },
                Children =
                [
                    // Pressed scales the Arrow FontIcon about its centre to FlipViewButtonScalePressed = 0.875
                    // (RenderTransformOrigin 0.5,0.5 — FlipView_themeresources.xaml:133-140); the bar never scales.
                    new BoxEl
                    {
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        PressScale = ButtonScalePressed,
                        Children =
                        [
                            new TextEl(glyph)
                            {
                                Size = ButtonFontSize, FontFamily = Theme.IconFont,
                                Color = Tok.FillControlStrong,    // FlipViewNextPreviousArrowForeground (:10)
                                HoverColor = Tok.TextSecondary,   // ...PointerOver (:11)
                                PressedColor = Tok.TextSecondary, // ...Pressed (:12)
                            },
                        ],
                    },
                ],
            };
        }

        // ── Assembly: ZStack — item strip, then the conditional nav bars, then the invisible fade ticker.
        bool buttonsVisible = showButtons.Value;
        bool fadeTicking = fadePending.Value;

        var items = p?.Items;
        var cells = new Element[count];
        for (int i = 0; i < count; i++)
            // Each page cell stretches its content (FlipViewItem Horizontal/VerticalContentAlignment = Stretch,
            // FlipViewItem_themeresources.xaml:17-18) — a ZStack cell fills an unsized child.
            cells[i] = new BoxEl { ZStack = true, Width = w, Height = h, Children = [items![i]] };

        // Keyed children: the bars mount/unmount, and the strip must survive those diffs (it carries the live
        // translate track) — the keyed reconciler matches by key, never by position.
        var children = new List<Element>(4)
        {
            new BoxEl
            {
                Key = "fv-strip",
                Direction = vertical ? (byte)1 : (byte)0,
                Width = vertical ? w : w * count,
                Height = vertical ? h * count : h,
                OnRealized = nh => stripRef.Value = nh,
                Children = cells,
            },
        };
        if (buttonsVisible && count > 1)
        {
            // nothingPrevious collapses the previous bar at index 0, nothingNext the next bar at the last index;
            // both collapse when count <= 1 (ChangeVisualState, FlipView_Partial.cpp:1242-1278).
            if (cur > 0) children.Add(NavBar(next: false) with { Key = "fv-prev" });
            if (cur < count - 1) children.Add(NavBar(next: true) with { Key = "fv-next" });
        }
        if (fadeTicking)
            // The 3000ms fade-out timer (m_tpButtonsFadeOutTimer), as the engine-idiomatic deadline ticker.
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = fadeDeadline,
                Pending = fadePending,
                Fire = HideButtonsImmediately,
            }) with { Key = "fv-buttons-fade" });

        return new BoxEl
        {
            ZStack = true,
            // A ZStack ignores Direction for layout (children overlay), so this is layout-neutral — it tags the swipe AXIS
            // for the dispatcher's DragYieldsToPan inference: a vertical FlipView drags along Y, a horizontal one along X.
            Direction = vertical ? (byte)1 : (byte)0,
            Width = w, Height = h,
            Corners = Radii.ControlAll,    // CornerRadius = ControlCornerRadius (FlipView_themeresources.xaml:98)
            Fill = Tok.FillSolidBase,      // FlipViewBackground = SolidBackgroundFillColorBaseBrush (:6)
            ClipToBounds = true,
            Focusable = true,              // the frame is the keyboard-navigation focus proxy (TabNavigation=Once, :80)
            OnKeyDown = OnKey,
            OnPointerWheel = OnWheel,
            OnHoverMove = _ => ShowButtonsAndArmFade(),   // non-touch movement shows the buttons (FlipView_Partial.cpp:780-803)
            OnPointerPressed = OnPressed,
            OnPointerDown = PanDown,
            OnDrag = PanMove,
            // Cross-axis page drag (§7A): inside a scroller the FlipView's along-axis drag flips pages; a cross-axis drag
            // yields to the list pan. (FlipView is rarely nested in a same-axis scroller, but the opt-in makes the race
            // deterministic and unifies the touch commit on the arena's velocity.)
            DragYieldsToPan = true,
            OnClick = CommitPan,
            OnFocusChanged = focused => { if (focused) ShowButtonsAndArmFade(); },   // keyboard focus shows buttons (:1471-1473)
            Children = children.ToArray(),
        };
    }
}
