using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>SwipeBehaviorOnInvoked</c> (SwipeControl.idl:11-16). Auto and Close both close the control after
/// the invoke (SwipeItem::InvokeSwipe closes on Close OR Auto — SwipeItem.cpp:40-45); RemainOpen keeps it revealed.</summary>
public enum SwipeBehaviorOnInvoked : byte { Auto = 0, Close = 1, RemainOpen = 2 }

/// <summary>WinUI <c>SwipeMode</c> (SwipeControl.idl:20-24): Reveal = resting tappable action buttons; Execute = ONE
/// item stretched to the control width that auto-invokes when the swipe opens past the threshold.</summary>
public enum SwipeMode : byte { Reveal = 0, Execute = 1 }

/// <summary>One swipe action (WinUI SwipeItem — SwipeControl.idl:69-91): an icon glyph, a label, an optional custom
/// background <paramref name="Color"/> (SwipeItem.Background — flips the content to on-accent text), the
/// <see cref="OnInvoked"/> callback (the Invoked event / Command) and <see cref="BehaviorOnInvoked"/>.</summary>
public sealed record SwipeAction(string Glyph, string Label, ColorF? Color = null)
{
    /// <summary>WinUI SwipeItem.Invoked (SwipeControl.idl:84): raised on tap (OnItemTapped, SwipeItem.cpp:110-125)
    /// or, for an Execute side, when the opened swipe commits (IdleStateEntered, SwipeControl.cpp:209-218).</summary>
    public Action? OnInvoked { get; init; }
    /// <summary>Default Auto (SwipeControl.idl:81-82) — closes after the invoke unless RemainOpen.</summary>
    public SwipeBehaviorOnInvoked BehaviorOnInvoked { get; init; } = SwipeBehaviorOnInvoked.Auto;
}

/// <summary>A WinUI SwipeControl (controls\dev\SwipeControl): drag the content sideways to reveal action items
/// underneath. The content pans with the pointer; releasing past min(items width, 100px) rests it open
/// (c_ThresholdValue — SwipeControl.cpp:21, ConfigurePositionInertiaRestingValues :937-1005), otherwise it springs
/// closed; an already-open side closes on ANY inward movement (UpdateThresholdReached, :1626-1645). Items are
/// invokable (Invoked + BehaviorOnInvoked); Execute mode stretches one item across the control and auto-invokes it on
/// open. Esc and a tap on the content dismiss an open swipe (AttachDismissingHandlers, SwipeControl.h:74-76).
/// Horizontal (Left/Right) items only — WinUI's vertical Top/Bottom axis is not offered on this surface, so its
/// "both axes populated" rejection (ThrowIfHasVerticalAndHorizontalContent, SwipeControl.cpp:1648-1676) cannot arise.
/// Deliberate superset: WinUI only swipes on touch/pen (VisualInteractionSource); ours pans with the mouse too.</summary>
public static class SwipeControl
{
    /// <summary>String-content convenience (the gallery surface): a card-filled text cell with TRAILING (right)
    /// actions. The cell is given a 320px MinWidth so the collapsed control leaves a swipeable surface.</summary>
    public static Element Create(string content, IReadOnlyList<SwipeAction> actions, SwipeMode mode = SwipeMode.Reveal)
        => Create(new BoxEl
        {
            MinWidth = 320f,   // gallery affordance only — WinUI sizes purely to content
            Padding = new Edges4(16, 14, 16, 14),
            Fill = Tok.FillCardDefault,
            AlignItems = FlexAlign.Center,
            Children = [new TextEl(content) { Size = 14f, Color = Tok.TextPrimary }],
        }, rightActions: actions, rightMode: mode);

    /// <summary>Arbitrary content with leading (left) and/or trailing (right) action lists
    /// (WinUI LeftItems/RightItems — SwipeControl.idl:48-51); the drag direction picks the side.</summary>
    public static Element Create(Element content,
                                 IReadOnlyList<SwipeAction>? leftActions = null,
                                 IReadOnlyList<SwipeAction>? rightActions = null,
                                 SwipeMode leftMode = SwipeMode.Reveal,
                                 SwipeMode rightMode = SwipeMode.Reveal)
        => Ctx.Provide(Props.Channel,
                       new Props(content, leftActions, rightActions, leftMode, rightMode),
                       Embed.Comp(() => new SwipeControlCore()));

    /// <summary>Controlled props via context — a reused ComponentEl never re-runs its factory, so props must flow
    /// through a provider (the SelectorBar/PipsPager convention).</summary>
    internal sealed record Props(Element Content, IReadOnlyList<SwipeAction>? LeftActions,
                                 IReadOnlyList<SwipeAction>? RightActions, SwipeMode LeftMode, SwipeMode RightMode)
    {
        internal static readonly Context<Props?> Channel = new(null);
    }
}

/// <summary>The stateful core: the pan gesture, the open/close thresholds, the Execute color flip + auto-invoke,
/// item invocation, and the Esc/tap dismissal.</summary>
internal sealed class SwipeControlCore : Component
{
    const float ThresholdPx = 100f;   // c_ThresholdValue (SwipeControl.cpp:21)
    const float CloseVelocityPxPerS = 31f;   // c_MinimumCloseVelocity (SwipeControl.cpp:22) — the floor speed an inward
                                             // release must carry to count as a close fling (else it springs back open)
    const float ItemMinWidth = 68f;   // SwipeItemStyle MinWidth (SwipeControl_themeresources.xaml:39)
    const float ItemMinHeight = 40f;  // SwipeItemStyle MinHeight (:41); Width=Auto (:40) — grows with the label
    const float PanSlopPx = 4f;       // the engine drag-box convention (ListViewBaseItem_Partial.cpp:1864-1878)
    // Fling-distance projection divisor for the open/close SNAP decision. A release of speed v (px/s) coasts an extra
    // v / FlingProjectK px in the snap-settle window before resting — the resting position the WinUI InteractionTracker
    // settles a swipe to. Derived from the engine scroller decay (ScrollIntegrator.FlingDecayPerS = 0.95/s survival) over
    // the ControlNormal settle window T: coast = v·(1−decay^T)/−ln(decay), so the divisor is −ln(decay)/(1−decay^T).
    // A BOUNDED window (not the full infinite-decay scroll coast) is the right model for a page/threshold snap — a slow
    // drag projects only a little (springs back under the threshold), a fast flick projects past it (opens). ≈ 4.0.
    static readonly float FlingProjectK = ProjectDivisor(ScrollIntegrator.FlingDecayPerS, Motion.ControlNormal / 1000f);
    static float ProjectDivisor(float decayPerS, float windowS)
    {
        float k = -MathF.Log(decayPerS);                 // the per-second decay rate (−ln survival)
        float frac = 1f - MathF.Exp(-k * windowS);       // fraction of the full coast reached within the window
        return frac > 1e-4f ? k / frac : k;              // divisor: projectedExtra = v / divisor = v·frac/k
    }

    public override Element Render()
    {
        // Hooks — stable order, unconditionally.
        var props = UseContext(SwipeControl.Props.Channel);
        var hooks = UseContext(InputHooks.Current);
        var contentRef = UseRef<NodeHandle>(default);
        var stackRef = UseRef<NodeHandle>(default);      // the items stack (m_swipeContentStackPanel) — threshold measure
        var panKeys = UseRef(new Keyframe[2]);           // reused pan-follow keyframes (0-alloc per pointer move)
        var panStart = UseRef(0f);
        var panBase = UseRef(0f);
        var panning = UseRef(false);
        var offset = UseRef(0f);                         // current content translation (px; + = left items revealed)
        var side = UseSignal(0);                         // +1 = left items revealed, -1 = right, 0 = none mounted
        var isOpen = UseSignal(false);
        var thresholdReached = UseSignal(false);         // m_thresholdReached — drives the Execute color flip
        var closeDeadline = UseRef(0L);
        var closePending = UseSignal(false);             // unmounts the reveal layer once the close settle lands
        var execDeadline = UseRef(0L);
        var execPending = UseSignal(false);              // Execute auto-close after the open settle
        var escPreview = UseRef<Func<int, bool>?>(null);
        var savedPreview = UseRef<Func<int, bool>?>(null);
        var escInstalled = UseRef(false);
        var closeAction = UseRef<Action?>(null);

        var p = props;

        float LiveOffset()
            => Context.Anim is { } a && !contentRef.Value.IsNull &&
               a.TryGetTrackValue(contentRef.Value, AnimChannel.TranslateX, out float v)
                ? v
                : offset.Value;

        void AnimateContentTo(float to, float durationMs)
        {
            if (contentRef.Value.IsNull) return;
            Context.Anim?.Animate(contentRef.Value, AnimChannel.TranslateX, LiveOffset(), to, durationMs, Easing.FluentPopOpen);
            offset.Value = to;
        }

        // The items-stack width (m_swipeContentStackPanel.ActualWidth — the open resting extent and the threshold
        // base, SwipeControl.cpp:944-1005). Falls back to MinWidth*count until the stack is laid out.
        float StackWidth(IReadOnlyList<SwipeAction> acts, SwipeMode mode)
        {
            var scene = Context.Scene;
            if (scene is not null && !stackRef.Value.IsNull && scene.IsLive(stackRef.Value))
            {
                float bw = scene.Bounds(stackRef.Value).W;
                if (bw > 0f) return bw;
            }
            return ItemMinWidth * (mode == SwipeMode.Execute ? 1 : acts.Count);
        }

        void Close()
        {
            if (!isOpen.Peek() && side.Peek() == 0) return;
            // WinUI Close() flings home with c_MinimumCloseVelocity = 31 (SwipeControl.cpp:67-119). The close DECISION now
            // reads the real release velocity against that 31px/s floor (in ReleaseOrTap); the close MOTION is the engine's
            // FluentPopOpen glide (the eased substitution for the InteractionTracker close fling's on-screen travel).
            isOpen.Value = false;
            thresholdReached.Value = false;
            AnimateContentTo(0f, Motion.ControlFast);
            // Unmount the reveal layer once the settle lands (WinUI clears SwipeContentRoot/StackPanel when the
            // tracker idles closed — IdleStateEntered, SwipeControl.cpp:219-238).
            closeDeadline.Value = Environment.TickCount64 + (long)Motion.ControlFast;
            closePending.Value = true;
        }
        closeAction.Value = Close;

        void InvokeItem(SwipeAction a)
        {
            a.OnInvoked?.Invoke();                                            // Invoked (SwipeItem.cpp:24-31)
            if (a.BehaviorOnInvoked != SwipeBehaviorOnInvoked.RemainOpen)     // Close on Close OR Auto (:40-45)
                Close();
        }

        // ── The pan gesture (root-anchored: the root never moves, so its local coords are gesture-stable) ─────────
        void PanDown(Point2 lp)
        {
            panStart.Value = lp.X;
            panBase.Value = LiveOffset();
            panning.Value = false;
        }
        void PanMove(Point2 lp)
        {
            if (p is null || contentRef.Value.IsNull) return;
            float delta = lp.X - panStart.Value;
            if (!panning.Value && MathF.Abs(delta) < PanSlopPx) return;
            panning.Value = true;
            bool hasLeft = p.LeftActions is { Count: > 0 };
            bool hasRight = p.RightActions is { Count: > 0 };
            float maxLeft = hasLeft ? StackWidth(p.LeftActions!, p.LeftMode) : 0f;
            float maxRight = hasRight ? StackWidth(p.RightActions!, p.RightMode) : 0f;
            float off = Math.Clamp(panBase.Value + delta, -maxRight, maxLeft);
            // The revealed side follows the drag direction (axis content inferred from the populated sides,
            // SwipeControl.cpp:1648-1676; CreateContent picks left/right off the position sign).
            int s = off > 0.5f ? 1 : off < -0.5f ? -1 : side.Peek();
            if (s != side.Peek() && (s != 1 || hasLeft) && (s != -1 || hasRight)) side.Value = s;
            var keys = panKeys.Value;
            keys[0] = new Keyframe(0f, off, Easing.Linear);
            keys[1] = new Keyframe(1f, off, Easing.Linear);
            Context.Anim?.Keyframes(contentRef.Value, AnimChannel.TranslateX, keys, 1f);   // follow the pointer (0-alloc)
            offset.Value = off;
            // m_thresholdReached = "releasing now commits the pending action" (UpdateThresholdReached,
            // SwipeControl.cpp:1626-1645): opening compares against min(stackWidth-1, 100); an already-open side
            // flips on ANY inward movement (abs(value) < effectiveStackPanelSize — the release will close).
            int active = s;
            if (active != 0)
            {
                var acts = active > 0 ? p.LeftActions : p.RightActions;
                var mode = active > 0 ? p.LeftMode : p.RightMode;
                if (acts is { Count: > 0 })
                {
                    float effective = StackWidth(acts, mode) - 1f;
                    bool reached = !isOpen.Peek()
                        ? MathF.Abs(off) > MathF.Min(effective, ThresholdPx)
                        : MathF.Abs(off) < effective;
                    thresholdReached.Value = reached;   // re-renders only the Execute coloring (subscribed below)
                }
            }
        }
        void ReleaseOrTap()   // the OnDrag gesture's release/commit edge; a plain tap lands here with panning=false
        {
            if (p is null) return;
            if (!panning.Value)
            {
                // Tap on the content of an open swipe dismisses it (the dismissing handlers' pointer path —
                // SwipeControl.h:74-76). True app-wide outside-press dismissal needs a global pointer hook the
                // engine does not expose; Esc (below) and content-tap cover the in-control paths.
                if (isOpen.Peek()) Close();
                return;
            }
            panning.Value = false;
            int s = side.Peek();
            if (s == 0) return;
            var acts = s > 0 ? p.LeftActions : p.RightActions;
            var mode = s > 0 ? p.LeftMode : p.RightMode;
            if (acts is not { Count: > 0 }) { Close(); return; }
            float stackW = StackWidth(acts, mode);
            // REAL release velocity from the arena VelocitySampler (px/s along the swipe axis), projected to the inertia
            // NaturalRestingPosition the WinUI InteractionTracker would settle at: |offset| + the friction-decay fling
            // distance the engine's own scroller uses (v / -ln(decay), ScrollIntegrator.FlingDecayPerS = 0.95/s). This is
            // the velocity snap WinUI does on c_ThresholdValue (SwipeControl.cpp:944-961, :1634): a fast flick rests open
            // even short of 100px, and a release toward home springs closed. Velocity that opposes the open direction
            // (sign != side) projects nothing — only outward speed counts toward resting open.
            float vAxis = hooks.PointerVelocity?.Invoke().X ?? 0f;   // horizontal swipe ⇒ X is the swipe axis
            float dist = MathF.Abs(offset.Value);
            float outward = s > 0 ? vAxis : -vAxis;                  // speed in the reveal direction (left side reveals on +X)
            float projected = dist + (outward > 0f ? outward / FlingProjectK : 0f);
            // Opening: rests open when the projected resting position >= min(stack width, 100) (the inertia resting
            // condition, SwipeControl.cpp:944-961). Already open: any inward movement closes (:1636-1639), AND an inward
            // flick at or past c_MinimumCloseVelocity = 31px/s closes even from the resting extent (the WinUI Close()
            // adds that velocity to drive the close fling home — SwipeControl.cpp:99-119).
            bool restOpen;
            if (!isOpen.Peek())
                restOpen = projected >= MathF.Min(stackW - 1f, ThresholdPx);
            else
            {
                bool inwardFlick = outward <= -CloseVelocityPxPerS;   // releasing toward home at/above the close-velocity floor
                restOpen = dist >= stackW - 1f && !inwardFlick;
            }
            if (!restOpen) { Close(); return; }
            isOpen.Value = true;
            thresholdReached.Value = true;
            AnimateContentTo(s * stackW, Motion.ControlFast);
            if (mode == SwipeMode.Execute)
            {
                // Execute auto-invokes item 0 when the opened swipe commits (IdleStateEntered, SwipeControl.cpp:209-218;
                // invoked at the release commit here, with the close sequenced after the open settle).
                var item = acts[0];
                item.OnInvoked?.Invoke();
                if (item.BehaviorOnInvoked != SwipeBehaviorOnInvoked.RemainOpen)
                {
                    execDeadline.Value = Environment.TickCount64 + (long)Motion.ControlFast;
                    execPending.Value = true;
                }
            }
        }

        // ── Esc dismisses while open (AttachDismissingHandlers — Escape/GamepadB key dismiss, SwipeControl.h:74-76),
        //    via the dispatcher's pre-focus key preview so it works without focus in the control. Chained + restored
        //    by reference so an overlay's own preview survives us.
        bool open = isOpen.Value;
        escPreview.Value ??= k =>
        {
            if ((k == Keys.Escape || k == Keys.GamepadB) && isOpen.Peek())
            {
                closeAction.Value?.Invoke();
                return true;
            }
            return savedPreview.Value?.Invoke(k) ?? false;
        };
        UseEffect(() =>
        {
            if (open && !escInstalled.Value)
            {
                escInstalled.Value = true;
                savedPreview.Value = hooks.KeyPreview;
                hooks.KeyPreview = escPreview.Value;
            }
            else if (!open && escInstalled.Value)
            {
                escInstalled.Value = false;
                if (ReferenceEquals(hooks.KeyPreview, escPreview.Value)) hooks.KeyPreview = savedPreview.Value;
                savedPreview.Value = null;
            }
        }, open);

        if (p is null) return new BoxEl();

        // ── One swipe item (WinUI SwipeItemStyle, an AppBarButton — SwipeControl_themeresources.xaml:35-77) ────────
        bool reached = thresholdReached.Value;
        Element ItemCell(SwipeAction a, bool execute)
        {
            ColorF fill, fg;
            if (execute)
            {
                // Pre-threshold SwipeItemPreThresholdExecuteForeground/Background = ControlStrongFillColorDefault on
                // ControlFillColorTertiary → post-threshold TextOnAccentFillColorPrimary on AccentFillColorDefault
                // (SwipeControl_themeresources.xaml:8-11); a custom item Background/Foreground wins
                // (UpdateExecuteBackgroundColor/ForegroundColor, SwipeControl.cpp:1325-1387).
                if (a.Color is { } custom) { fill = custom; fg = Tok.TextOnAccentPrimary; }
                else if (reached) { fill = Tok.AccentDefault; fg = Tok.TextOnAccentPrimary; }
                else { fill = Tok.FillControlTertiary; fg = Tok.FillControlStrong; }
            }
            else
            {
                // SwipeItemBackground = ControlFillColorTertiaryBrush / SwipeItemForeground = TextFillColorPrimaryBrush
                // (SwipeControl_themeresources.xaml:5-6); a custom bold fill flips the content to on-accent text.
                fill = a.Color ?? Tok.FillControlTertiary;
                fg = a.Color is not null ? Tok.TextOnAccentPrimary : Tok.TextPrimary;
            }
            return new BoxEl
            {
                MinWidth = ItemMinWidth,    // MinWidth=68, Width=Auto — grows with the label (:39-40)
                MinHeight = ItemMinHeight,  // MinHeight=40 (:41); stretches to the control height (SwipeControl.cpp:1286-1288)
                Grow = execute ? 1f : 0f,   // the Execute item stretches to the control width (SwipeControl.cpp:1289-1292)
                Direction = 1,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Gap = 4f,
                Padding = new Edges4(4, 4, 4, 2),   // ContentRoot Margin="4,4,4,2" (SwipeControl_themeresources.xaml:61)
                Fill = fill,
                // Pressed swaps the item plate to SwipeItemBackgroundPressed = ControlAltFillColorQuarternaryBrush —
                // the template overrides Root.Background for ANY item, custom fills included (:7, template :53-57).
                PressedFill = Tok.FillControlAltQuaternary,
                Role = AutomationRole.Button,
                OnClick = () => InvokeItem(a),      // tap invokes (OnItemTapped, SwipeItem.cpp:110-125)
                Children =
                [
                    new TextEl(a.Glyph) { Size = 16f, Color = fg, FontFamily = Theme.IconFont },
                    new TextEl(a.Label) { Size = 12f, Color = fg },
                ],
            };
        }

        // ── Assembly: ZStack — the reveal layer (mounted only while a side is engaged) under the panning content ──
        int sideNow = side.Value;
        bool closeTicking = closePending.Value;
        bool execTicking = execPending.Value;

        var children = new List<Element>(4);
        var sideActs = sideNow > 0 ? p.LeftActions : sideNow < 0 ? p.RightActions : null;
        if (sideNow != 0 && sideActs is { Count: > 0 })
        {
            var mode = sideNow > 0 ? p.LeftMode : p.RightMode;
            bool execute = mode == SwipeMode.Execute;
            int n = execute ? 1 : sideActs.Count;   // Execute renders item 0 only (SwipeControl.cpp:1316-1323)
            var cells = new Element[n];
            for (int i = 0; i < n; i++) cells[i] = ItemCell(sideActs[i], execute);
            // Full-bleed reveal plate: SwipeContentRoot gets SwipeItemBackground — or the OUTERMOST custom item
            // Background (Left → last item, Right → first) — painted across the whole control under the items
            // (UpdateStyles/the root-grid background swap, SwipeControl.cpp:1389-1439).
            ColorF plate = Tok.FillControlTertiary;
            var outer = sideNow > 0 ? sideActs[sideActs.Count - 1] : sideActs[0];
            if (outer.Color is { } oc) plate = oc;
            children.Add(new BoxEl
            {
                // Keyed: the reveal layer mounts/unmounts under the content — the keyed reconciler must not
                // positionally re-pair it with the content node (which carries the live pan translation).
                Key = "swipe-reveal",
                Fill = plate,
                Direction = 0,
                AlignItems = FlexAlign.Stretch,     // items take the control height (SwipeControl.cpp:1286-1288)
                Justify = sideNow > 0 ? FlexJustify.Start : FlexJustify.End,
                Children =
                [
                    // The items stack (m_swipeContentStackPanel) — its measured width is the open resting extent.
                    new BoxEl
                    {
                        Direction = 0,
                        AlignItems = FlexAlign.Stretch,
                        Grow = execute ? 1f : 0f,
                        OnRealized = nh => stackRef.Value = nh,
                        Children = cells,
                    },
                ],
            });
        }
        // The content layer: pans on its TranslateX; carries no handlers itself so presses fall through to the root.
        children.Add(new BoxEl
        {
            Key = "swipe-content",
            ZStack = true,   // an unsized app content child fills the cell
            OnRealized = nh => contentRef.Value = nh,
            Children = [p.Content],
        });
        if (closeTicking)
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = closeDeadline,
                Pending = closePending,
                Fire = () => { side.Value = 0; thresholdReached.Value = false; },   // clear the reveal (IdleStateEntered, SwipeControl.cpp:219-238)
            }) with { Key = "swipe-close-settle" });
        if (execTicking)
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = execDeadline,
                Pending = execPending,
                Fire = () => closeAction.Value?.Invoke(),   // Execute Auto/Close: close after the open settle
            }) with { Key = "swipe-exec-close" });

        // WinUI's RootGrid is bare — no border, fill or corner rounding (SwipeControl.xaml:11-19); the clip bounds
        // the panning content and the reveal underneath.
        return new BoxEl
        {
            ZStack = true,
            ClipToBounds = true,
            OnPointerDown = PanDown,
            OnDrag = PanMove,
            // Cross-axis swipe (§7A): inside a vertical list this drag competes axis-locked with the list's Pan — a
            // horizontal swipe along the row wins (reveals the actions), a vertical drag yields (the list scrolls). The
            // root box is a row (Direction default 0) ⇒ the dispatcher locks the swipe to the X axis.
            DragYieldsToPan = true,
            OnClick = ReleaseOrTap,
            // Esc also routes here when focus is inside the control (a revealed item is focusable).
            OnKeyDown = a =>
            {
                if (a.Handled || a.KeyCode != Keys.Escape || !isOpen.Peek()) return;
                a.Handled = true;
                Close();
            },
            Children = children.ToArray(),
        };
    }
}
