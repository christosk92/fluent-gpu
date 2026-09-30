using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

/// <summary>
/// Operation ultra-fast GPU engine, P3 ("Typed bound-item authoring API + engine-owned gating, caches, recycle
/// rules, bound controls") — registered as the <c>bound</c> suite. Exercises <c>BoundItemScope&lt;T&gt;</c>'s
/// helpers (<c>Text/Number/Color/Opacity/Show/Image/Spans/Duration/Text&lt;TKey&gt;/Value/Signal/Invoke/InvokeSpan/
/// ShowWhen</c>), the equality-gated <c>BoundItemsSource&lt;T&gt;.BindItem</c> overload every
/// <see cref="ItemsView.CreateBound{T}"/> now uses, the six/seven newly equality-gated static channels
/// (HoverFill/PressedFill/BorderColor/Corners/RadialGradientCenter/TextEl.Color/ImageEl.Placeholder), the
/// <see cref="TreeReconciler.SuppressBoundTransitions"/> recycle-snap rule, <see cref="FormatCache{TKey}"/>'s
/// bound cap, and <see cref="PersonPicture.Bound"/>'s shape stability.
/// </summary>
static class BoundTemplateSuite
{
    public static void Run(StringTable strings)
    {
        TypedTemplateZeroAllocChecks(strings);
        EqualRepublishFiresNothingChecks(strings);
        ChangedItemFiresOnlyItsSlotChecks(strings);
        TransitionSnapsOnRecycleChecks(strings);
        FormatCacheBoundedChecks();
        HandlersResolveAtInvocationChecks(strings);
        PersonPictureToolTipShapeStableChecks(strings);
    }

    // ── the shared row item + probe used by most of these gates ──────────────────────────────────────────────────

    public readonly record struct BoundItem(int Number, string Title, ColorF Tint, bool Flag, string ImageUrl, long DurationMs, bool HasBadge)
    {
        public static BoundItem Empty => default;
        public static BoundItem For(int i) => new(i, "Track " + i, ColorF.FromRgba((byte)(i % 200 + 20), 80, 160, 255),
            i % 2 == 0, i % 3 == 0 ? "" : "img://" + i, 1000L * (i % 300 + 1), i % 5 == 0);
    }

    static List<BoundItem> BuildItems(int n)
    {
        var l = new List<BoundItem>(n);
        for (int i = 0; i < n; i++) l.Add(BoundItem.For(i));
        return l;
    }

    static readonly FormatCache<int> s_dateCache = FormatCache.Create<int>();

    /// <summary>~15 distinct bound channels per row (Text, Number, Color ×2, Opacity, Show ×2, Image, Spans,
    /// Duration, Text&lt;TKey&gt;, Invoke (click), Invoke (pointer), InvokeSpan, ShowWhen+Signal, Cells) — the plan's
    /// "≈30 channels/row" is an order-of-magnitude target for the zero-alloc gate, not a literal count; this probe
    /// covers every helper in <c>BoundItemScopeExtensions</c> at least once.</summary>
    sealed class BoundTemplateRowsProbe : Component
    {
        public readonly Signal<IReadOnlyList<BoundItem>> Snapshot = new(BuildItems(1000));
        public int Builds;
        public (int Number, string Kind)? LastInvoke;
        public (int Number, int Span)? LastSpanInvoke;

        public override Element Render()
        {
            var source = BoundItems.From(Snapshot, BoundItem.Empty);
            var list = ItemsView.CreateBound(source, item =>
            {
                Builds++;
                return new BoxEl
                {
                    Width = 340f,
                    Height = 36f,
                    Direction = 0,
                    Gap = 4f,
                    OnClick = item.Invoke(t => LastInvoke = (t.Number, "click")),
                    OnPointerPressed = item.Invoke((t, _) => LastInvoke = (t.Number, "pointer")),
                    Children =
                    [
                        new TextEl(item.Number(t => t.Number)) { Size = 12f, Color = item.Color(t => t.Tint) },
                        new TextEl(item.Text(t => t.Title)),
                        new BoxEl
                        {
                            Width = 10f, Height = 10f, Fill = item.Color(t => t.Tint), Visible = item.Show(t => t.Flag),
                            Opacity = item.Opacity(t => t.Flag ? 1f : 0.6f),
                        },
                        new ImageEl { Source = item.Image(t => t.ImageUrl), Width = 20f, Height = 20f, Visible = item.Show(t => !t.Flag) },
                        new SpanTextEl(item.Spans((t, b) => { b.Add(new TextSpan(t.Title, IsLink: true)); }))
                        {
                            Size = 11f,
                            OnSpanClick = item.InvokeSpan((t, i) => LastSpanInvoke = (t.Number, i)),
                        },
                        new TextEl(item.Duration(t => t.DurationMs)) { Size = 11f },
                        new TextEl(item.Text(t => t.Number, s_dateCache, static n => "d" + n)) { Size = 11f },
                        item.ShowWhen(t => t.HasBadge, () => new TextEl(Prop.Bind(item.Signal(t => t.Title))) { Size = 10f }),
                        // A flat ListRowEl cell strip bound through the same slot item (the two-tier row seam).
                        new ListRowEl(item.Cells((t, b) =>
                        {
                            b.Add(new RowCell { Kind = RowCellKind.Rect, Rect = new RectF(0f, 2f, 6f, 12f), Color = t.Tint });
                            b.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(8f, 0f, 40f, 16f), Text = t.Title, Color = ColorF.FromRgba(255, 255, 255), FontSize = 10f });
                        })) { Width = 48f, Height = 16f },
                    ],
                };
            }, RepeatLayout.Stack(40f), new ListOptions<BoundItem> { Grow = 1f });
            return new BoxEl { Width = 360f, Height = 240f, Children = [list] };
        }
    }

    static (AppHost host, BoundTemplateRowsProbe probe, NodeHandle viewport, HeadlessWindow window) MountProbe(StringTable strings, HeadlessPlatformApp app)
    {
        var fonts = new HeadlessFontSystem(strings);
        var window = new HeadlessWindow(new WindowDesc("bound-template", new Size2(360, 240), 1f));
        window.Show();
        var probe = new BoundTemplateRowsProbe();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = ViewportWithItemCount(host.Scene, host.Scene.Root, 1000);
        return (host, probe, vp, window);
    }

    // ── gate.bound.typed-template-zero-alloc ──────────────────────────────────────────────────────────────────────
    static void TypedTemplateZeroAllocChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, probe, vp, _) = MountProbe(strings, app);
        int buildsAtMount = probe.Builds;
        host.Scene.TryGetScroll(vp, out var sc0);
        int slotsAtMount = sc0.LastRealized - sc0.FirstRealized;

        // A 5-row shift (small — recycles a handful of slots without a structural template rebuild) then settle.
        host.TryGetScrollHandle(vp)?.ScrollTo(40f * 5f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        var shiftFrame = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc1);
        int reboundRows = Math.Max(1, sc1.LastRealized - sc1.FirstRealized);
        for (int k = 0; k < 3; k++) host.RunFrame();
        var steady = host.RunFrame();

        bool buildsUnchanged = probe.Builds == buildsAtMount;   // recycle never rebuilds the template
        bool zeroHot = steady.HotPhaseAllocBytes == 0;
        bool flushBudgetOk = shiftFrame.RebindFlushAllocBytes <= 2048L;

        Check("gate.bound.typed-template-zero-alloc a 1000-row typed bound template (~15 channels/row across every BoundItemScope helper); a 5-row shift then settle ⇒ 0 hot-phase alloc, <=2KB rebind-flush alloc, template builds unchanged",
            buildsUnchanged && zeroHot && flushBudgetOk,
            $"slotsAtMount={slotsAtMount} builds={buildsAtMount}->{probe.Builds} hotAlloc={steady.HotPhaseAllocBytes}B reboundFlush={shiftFrame.RebindFlushAllocBytes}B/{reboundRows}rows");
        host.Dispose();
    }

    // ── gate.bound.equal-republish-fires-nothing ──────────────────────────────────────────────────────────────────
    static void EqualRepublishFiresNothingChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, probe, _, _) = MountProbe(strings, app);

        // Republish an EQUAL snapshot: a new List<BoundItem> instance whose per-index VALUES are identical to the
        // live one (a plausible "activity-only" upstream republish). The list reference changes (Signal notifies),
        // every realized slot's item Memo recomputes, but the gated overload must resolve every recompute EQUAL and
        // therefore fire NO downstream channel effect at all.
        // NodeBindingFireCount/WriteCount are PER-FRAME (reset at BeginRenderCensus, Paint start) — read the fired
        // frame's OWN FrameStats.BindingFires rather than a cross-frame delta.
        var clone = new List<BoundItem>(probe.Snapshot.Peek());
        probe.Snapshot.Value = clone;
        var frame = host.RunFrame();

        Check("gate.bound.equal-republish-fires-nothing an equal republish of the bound source (new list instance, identical per-index values) fires ZERO downstream channel effects (this frame's FrameStats.BindingFires == 0)",
            frame.BindingFires == 0, $"bindingFires={frame.BindingFires}");
        host.Dispose();
    }

    // ── gate.bound.changed-item-fires-only-its-slot ───────────────────────────────────────────────────────────────
    static void ChangedItemFiresOnlyItsSlotChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, probe, vp, _) = MountProbe(strings, app);
        host.Scene.TryGetScroll(vp, out var sc);
        int first = sc.FirstRealized, last = sc.LastRealized;
        Check("gate.bound.changed-item-fires-only-its-slot: precondition — more than one row realized",
            last - first > 2, $"first={first} last={last}");

        var list = new List<BoundItem>(probe.Snapshot.Peek());
        int changedIndex = first + 1;
        list[changedIndex] = list[changedIndex] with { Title = "CHANGED-" + changedIndex, Tint = ColorF.FromRgba(255, 0, 0, 255) };

        probe.Snapshot.Value = list;
        var frame = host.RunFrame();   // NodeBindingFireCount/WriteCount are PER-FRAME — read THIS frame's totals directly

        // Every realized slot's item Memo recomputes (fires), but only the CHANGED slot's channels WRITE (a changed
        // Number/Text/Color/etc. actually mutates NodePaint) — every other slot's recompute is silent (equal).
        int realizedRows = Math.Max(1, last - first);
        bool wrote = frame.BindingWrites > 0;
        // Loose upper bound: the changed row owns a handful of channels (Number/Title/Tint/Flag/Image/Spans/Duration/
        // dateCache — ~8); everyone else must write nothing, so the total write count must stay near ONE row's worth,
        // far below "every realized row wrote" (realizedRows * 4).
        bool onlyOneRowWrote = frame.BindingWrites < realizedRows * 4;

        Check("gate.bound.changed-item-fires-only-its-slot changing ONE item's fields writes channels on only that row's slot (this frame's BindingWrites stays far below every-realized-row-wrote)",
            wrote && onlyOneRowWrote,
            $"bindingFires={frame.BindingFires} bindingWrites={frame.BindingWrites} realizedRows={realizedRows}");
        host.Dispose();
    }

    // ── gate.bound.transition-snaps-on-recycle ────────────────────────────────────────────────────────────────────
    sealed class TransitionRowItem
    {
        public bool Badge;
    }

    sealed class TransitionRowsProbe : Component
    {
        public readonly Signal<IReadOnlyList<TransitionRowItem>> Snapshot;
        public TransitionRowsProbe(int n)
        {
            var l = new List<TransitionRowItem>(n);
            for (int i = 0; i < n; i++) l.Add(new TransitionRowItem { Badge = false });
            Snapshot = new Signal<IReadOnlyList<TransitionRowItem>>(l);
        }

        public override Element Render()
        {
            var source = BoundItems.From(Snapshot, new TransitionRowItem());
            var list = ItemsView.CreateBound(source, item => new BoxEl
            {
                Width = 340f,
                Height = 36f,
                Children =
                [
                    new BoxEl
                    {
                        Width = 16f, Height = 16f, Fill = ColorF.FromRgba(255, 200, 0, 255),
                        Visible = item.Show(t => t.Badge),
                        Enter = new EnterExit(Sx: 0.5f, Sy: 0.5f, Opacity: 0f, Active: true),
                    },
                ],
            }, RepeatLayout.Stack(40f), new ListOptions<TransitionRowItem> { Grow = 1f });
            return new BoxEl { Width = 360f, Height = 240f, Children = [list] };
        }
    }

    static void TransitionSnapsOnRecycleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var fonts = new HeadlessFontSystem(strings);
        var window = new HeadlessWindow(new WindowDesc("bound-transition-snap", new Size2(360, 240), 1f));
        window.Show();
        var probe = new TransitionRowsProbe(1000);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = ViewportWithItemCount(host.Scene, host.Scene.Root, 1000);

        // Track a SPECIFIC node's animation state (not the global HasActive, which a scroll jump can also perturb via
        // unrelated tracks — e.g. a scrollbar fade) via TryGetTrackValue on the badge's Opacity channel: a seeded Enter
        // starts the badge at Opacity=0 and animates toward 1, so a track exists immediately after the seeding frame;
        // a snap writes the final value directly with no track at all.
        host.Scene.TryGetScroll(vp, out var scLive);
        int liveIndex = scLive.FirstRealized;
        NodeHandle FindSlotRoot(int ordinalFromFirst)
        {
            host.Scene.TryGetScroll(vp, out var sc2);
            var contentNode = sc2.ContentNode;
            int i = 0;
            for (var c = host.Scene.FirstChild(contentNode); !c.IsNull; c = host.Scene.NextSibling(c), i++)
                if (i == ordinalFromFirst) return c;
            return NodeHandle.Null;
        }
        NodeHandle liveSlotRoot = FindSlotRoot(0);   // FirstRealized is always DOM-first for this simple (non-extended) path
        NodeHandle liveBadge = liveSlotRoot.IsNull ? NodeHandle.Null : host.Scene.FirstChild(liveSlotRoot);

        // Live toggle (no scroll in flight, outside any RealizeBoundWindow pass): a false→true Visible edge on an
        // ALREADY-REALIZED row's badge — the P1 Enter-seed path DOES fire here (SuppressBoundTransitions == 0).
        var list1 = new List<TransitionRowItem>(probe.Snapshot.Peek());
        list1[liveIndex] = new TransitionRowItem { Badge = true };
        probe.Snapshot.Value = list1;
        host.RunFrame();
        bool liveSeeded = !liveBadge.IsNull && host.Animation.TryGetTrackValue(liveBadge, AnimChannel.Opacity, out _);
        // Drain any seeded track before the recycle probe below so it can't leak into that reading.
        for (int i = 0; i < 60 && host.Animation.HasActive; i++) host.RunFrame();

        // Recycle: mutate ONLY a far-away range (never realized yet — currently-visible rows are untouched, so this
        // republish alone triggers no live flip) to Badge=true, THEN scroll there so every slot in view RECYCLES onto
        // one of those items. The SAME persistent slot node (liveSlotRoot/liveBadge — bound slots never remount, only
        // rebind) now shows a DIFFERENT item whose Badge is true: the false→true edge is reached ONLY through
        // RebindBoundSlot → FlushRebindsToQuiescence, both under SuppressBoundTransitions — never through the
        // ordinary (unwrapped) reactive flush this live case used above.
        var list2 = new List<TransitionRowItem>(probe.Snapshot.Peek());
        for (int i = 700; i < 760; i++) list2[i] = new TransitionRowItem { Badge = true };
        probe.Snapshot.Value = list2;
        host.RunFrame();   // absorb the republish while nothing realized in [700,760) — must seed nothing (off-screen)
        host.TryGetScrollHandle(vp)?.ScrollTo(700f * 40f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        host.RunFrame();
        bool recycleSeeded = !liveBadge.IsNull && host.Scene.IsLive(liveBadge)
            && host.Animation.TryGetTrackValue(liveBadge, AnimChannel.Opacity, out _);

        Check("gate.bound.transition-snaps-on-recycle a live (non-recycle) Visible false->true edge seeds an Enter track on that node, but the SAME node reached through a bound-window recycle (RealizeBoundWindow/FlushRebindsToQuiescence, SuppressBoundTransitions>0) seeds NOTHING on it — it snaps",
            !liveBadge.IsNull && liveSeeded && !recycleSeeded,
            $"liveBadge={!liveBadge.IsNull} liveSeeded={liveSeeded} recycleSeeded={recycleSeeded}");
    }

    // ── gate.bound.format-cache-bounded ───────────────────────────────────────────────────────────────────────────
    static void FormatCacheBoundedChecks()
    {
        var cache = FormatCache.Create<int>();
        for (int i = 0; i < FormatCache<int>.Capacity + 500; i++)
            cache.Get(i, static n => "v" + n);

        bool neverExceeded = cache.Count <= FormatCache<int>.Capacity;
        // The dense int cache never clears — probe a large-ish value and confirm it stays intern-stable (same ref).
        string a = FormatCache.Int(123456);
        string b = FormatCache.Int(123456);
        bool denseStable = ReferenceEquals(a, b);

        Check("gate.bound.format-cache-bounded a keyed FormatCache<TKey> stays at or under its capacity across an overflow (clears rather than growing unbounded); the dense int cache never clears and returns the SAME string instance for a repeated key",
            neverExceeded && denseStable,
            $"count={cache.Count} cap={FormatCache<int>.Capacity} denseStable={denseStable}");
    }

    // ── gate.bound.handlers-resolve-at-invocation ─────────────────────────────────────────────────────────────────
    static void HandlersResolveAtInvocationChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var (host, probe, vp, window) = MountProbe(strings, app);
        host.Scene.TryGetScroll(vp, out var sc);
        int idx = sc.FirstRealized;

        // Find the realized slot's root and click it (OnClick = item.Invoke(...)).
        NodeHandle slotRoot = NodeHandle.Null;
        for (var c = host.Scene.FirstChild(sc.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
        { slotRoot = c; break; }
        Check("gate.bound.handlers-resolve-at-invocation: precondition — a slot is realized", !slotRoot.IsNull, "no realized slot");

        var rect = host.Scene.AbsoluteRect(slotRoot);
        var p = new Point2(rect.X + rect.W * 0.5f, rect.Y + rect.H * 0.5f);
        ClickHere(p);
        int numberAtFirstClick = probe.LastInvoke?.Number ?? -1;

        // Now recycle this SAME slot (scroll far) to a DIFFERENT logical item, then click the SAME screen position
        // again: the handler must resolve the CURRENT item (Peek at invocation time), not whatever item occupied the
        // slot when the template captured `item` at build time.
        host.TryGetScrollHandle(vp)?.ScrollTo(25000f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out sc);
        NodeHandle slotRoot2 = NodeHandle.Null;
        for (var c = host.Scene.FirstChild(sc.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
        { var r = host.Scene.AbsoluteRect(c); if (r.Y >= 0f && r.Y + r.H <= 240f) { slotRoot2 = c; break; } }
        Check("gate.bound.handlers-resolve-at-invocation: precondition — a slot is realized after recycle", !slotRoot2.IsNull, "no realized slot after recycle");
        var rect2 = host.Scene.AbsoluteRect(slotRoot2);
        var p2 = new Point2(rect2.X + rect2.W * 0.5f, rect2.Y + rect2.H * 0.5f);
        ClickHere(p2);
        int numberAtSecondClick = probe.LastInvoke?.Number ?? -1;

        Check("gate.bound.handlers-resolve-at-invocation a per-item handler (item.Invoke) resolves the CURRENT item at click time, on a slot recycled between mount and click, not the item the template captured when it built the closure",
            numberAtFirstClick >= 0 && numberAtSecondClick >= 0 && numberAtSecondClick != numberAtFirstClick,
            $"first={numberAtFirstClick} second={numberAtSecondClick}");
        host.Dispose();

        void ClickHere(Point2 pt)
        {
            window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0));
            host.RunFrame();
        }
    }

    // ── gate.bound.personpicture-tooltip-shape-stable ─────────────────────────────────────────────────────────────
    sealed class PersonPictureProbe : Component
    {
        public readonly Signal<string> Name = new("Ada Lovelace");
        public readonly Signal<string> ImageUrl = new("");
        public readonly Signal<string?> TipText = new("A tip");

        public override Element Render()
        {
            var pic = PersonPicture.Bound(Prop.Bind(Name), Prop.Bind(ImageUrl), 48f);
            var wrapped = ToolTip.Wrap(pic, Prop.Bind(TipText));
            return new BoxEl { Width = 200f, Height = 100f, Children = [wrapped] };
        }
    }

    static void PersonPictureToolTipShapeStableChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var fonts = new HeadlessFontSystem(strings);
        var window = new HeadlessWindow(new WindowDesc("pp-tooltip-shape-stable", new Size2(200, 100), 1f));
        window.Show();
        var probe = new PersonPictureProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        int liveAtMount = host.Scene.LiveCount;

        // Flip image url empty -> non-empty -> empty, and tooltip text non-empty -> empty -> non-empty: none of these
        // are SHAPE changes (PersonPicture.Bound always mounts both layers; ToolTip.Wrap's bound overload renders the
        // SAME target either way) — SceneStore.LiveCount must stay exactly what it was at mount throughout.
        probe.ImageUrl.Value = "img://ada";
        host.RunFrame();
        int liveWithImage = host.Scene.LiveCount;

        probe.TipText.Value = null;
        host.RunFrame();
        int liveNoTip = host.Scene.LiveCount;

        probe.ImageUrl.Value = "";
        probe.TipText.Value = "back";
        host.RunFrame();
        int liveBack = host.Scene.LiveCount;

        Check("gate.bound.personpicture-tooltip-shape-stable PersonPicture.Bound + ToolTip.Wrap's bound text overload never change SceneStore.LiveCount across image/tooltip-text flips (shape-stable — no remount)",
            liveAtMount == liveWithImage && liveWithImage == liveNoTip && liveNoTip == liveBack,
            $"live mount={liveAtMount} withImage={liveWithImage} noTip={liveNoTip} back={liveBack}");
    }
}
