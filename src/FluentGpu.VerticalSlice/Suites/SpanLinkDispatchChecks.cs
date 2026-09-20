using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// Inline-hyperlink dispatch ownership (the WinUI rule, RichTextBlock.cpp:2996-3001): a click/tap that lands on a
// SpanTextEl hyperlink span is HANDLED BY THE LINK — the ancestor behind it sees neither the release nor the click.
// Both channels matter, which is what this file exists to pin: SpanLinksBit is neither PressedBit nor ClickBit, so
// NearestGestureOwner and NearestClickOwner both walk straight past the text leaf to the plate behind it, and a list
// row raises its ItemContainer tap from OnPointerReleased (SelectorVisualsBound), NOT from OnClick. Suppressing only
// the activation therefore still let "click the artist link on a track row" navigate AND select/play the row.
//
// The probe is that shape reduced to its skeleton: one plate carrying BOTH OnPointerReleased and OnClick, holding one
// span paragraph with two hyperlink spans (one index-resolved through the node's OnSpanClick, one carrying its own
// per-span closure) and two plain runs. Registered under the `text` tag (SuiteRegistry: "span-links") — it is
// SpanTextEl behaviour, and the dispatcher it exercises is shared by every other suite.
static class SpanLinkDispatchChecks
{
    const float PlateW = 400f;
    const float PlateH = 120f;
    const int NodeLinkSpan = 1;   // "Artist" — IsLink with no closure: resolves through SpanTextEl.OnSpanClick(i)
    const int OwnLinkSpan = 3;    // "Album"  — carries its own TextSpan.OnClick closure

    /// <summary>A track-row skeleton: a plate that both listens for the raw release (how a list row raises its
    /// selection/invoke tap) and declares a click, wrapping one rich-text paragraph whose links are the artist/album
    /// affordances. Every counter is a behaviour the dispatcher either fired or did not.</summary>
    sealed class LinkPlateProbe : Component
    {
        public int PlateReleased, PlateClicked, NodeSpanClicks, OwnSpanClicks;
        public int LastSpanIndex = -1;

        public void Reset() { PlateReleased = PlateClicked = NodeSpanClicks = OwnSpanClicks = 0; LastSpanIndex = -1; }

        public override Element Render() => new BoxEl
        {
            Width = PlateW, Height = PlateH, Direction = 1,
            Fill = ColorF.FromRgba(24, 24, 28),
            OnPointerReleased = _ => PlateReleased++,
            OnClick = () => PlateClicked++,
            Children =
            [
                new SpanTextEl(
                [
                    new TextSpan("Song by "),                                       // [0] plain leading run
                    new TextSpan("Artist", IsLink: true),                           // [1] index-resolved link
                    new TextSpan(" from "),                                         // [2] plain
                    new TextSpan("Album", OnClick: () => OwnSpanClicks++),          // [3] link with its own closure
                ])
                {
                    Size = 12f,
                    Wrap = TextWrap.NoWrap,
                    OnSpanClick = i => { NodeSpanClicks++; LastSpanIndex = i; },
                },
            ],
        };
    }

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("span-links", new Size2((int)PlateW, (int)PlateH + 40), 1f));
        window.Show();
        var probe = new LinkPlateProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();

        var scene = host.Scene;
        var plate = scene.Root;
        var text = TextVisualNode(scene, plate);
        var plateRect = scene.AbsoluteRect(plate);

        if (text.IsNull || !TryLinkPoint(scene, text, NodeLinkSpan, out var onNodeLink)
            || !TryLinkPoint(scene, text, OwnLinkSpan, out var onOwnLink)
            || !TryPlainSpanPoint(scene, text, out var onPlainSpan))
        {
            Check("gate.spans.link-owns-the-gesture the span paragraph publishes laid hyperlink rects the dispatcher can be aimed at",
                false, $"textNode={(text.IsNull ? "none" : "found")} — no SpanRunRects to hit-test against; the checks below could not run");
            return;
        }
        // Plate pixels that belong to nobody else: below the one text line, inside the plate.
        var textRect = scene.AbsoluteRect(text);
        var onBarePlate = new Point2(plateRect.X + plateRect.W * 0.5f, textRect.Y + textRect.H + (plateRect.H - textRect.H) * 0.5f);

        // ── 1. A click on a link span fires the link and NOTHING else ───────────────────────────────────────────────
        probe.Reset();
        ClickAt(host, window, onNodeLink);
        Check("gate.spans.link-click-owns-the-gesture clicking an inline hyperlink navigates ONLY: the span's index-resolved action fires once, and the row behind it receives neither its release (the channel a list row selects/plays from) nor its click",
            probe.NodeSpanClicks == 1 && probe.LastSpanIndex == NodeLinkSpan && probe.OwnSpanClicks == 0
            && probe.PlateReleased == 0 && probe.PlateClicked == 0,
            $"spanClicks={probe.NodeSpanClicks}@{probe.LastSpanIndex} ownSpanClicks={probe.OwnSpanClicks} plateReleased={probe.PlateReleased} plateClicked={probe.PlateClicked}");

        // ── 2. A per-span closure outranks the node-level handler, and still silences the plate ─────────────────────
        probe.Reset();
        ClickAt(host, window, onOwnLink);
        Check("gate.spans.own-closure-wins a hyperlink carrying its OWN action runs that action instead of the paragraph's index-resolved handler, and the row behind it still stays silent",
            probe.OwnSpanClicks == 1 && probe.NodeSpanClicks == 0
            && probe.PlateReleased == 0 && probe.PlateClicked == 0,
            $"ownSpanClicks={probe.OwnSpanClicks} spanClicks={probe.NodeSpanClicks} plateReleased={probe.PlateReleased} plateClicked={probe.PlateClicked}");

        // ── 3. The regression guard: a plate click away from every link is a plain, whole click ─────────────────────
        probe.Reset();
        ClickAt(host, window, onBarePlate);
        Check("gate.spans.plate-still-clicks clicking the row itself, clear of every link, still both releases and clicks on the row — suppressing links did not make the surface behind them inert",
            probe.PlateReleased == 1 && probe.PlateClicked == 1
            && probe.NodeSpanClicks == 0 && probe.OwnSpanClicks == 0,
            $"plateReleased={probe.PlateReleased} plateClicked={probe.PlateClicked} spanClicks={probe.NodeSpanClicks}/{probe.OwnSpanClicks} at=({onBarePlate.X:0.#},{onBarePlate.Y:0.#})");

        // ── 4. Plain text inside the SAME paragraph is row surface, not link surface ────────────────────────────────
        probe.Reset();
        ClickAt(host, window, onPlainSpan);
        Check("gate.spans.plain-run-is-row-surface clicking the non-link words of the same paragraph selects/clicks the ROW, exactly like clicking bare row surface — only the link glyphs belong to the link",
            probe.PlateReleased == 1 && probe.PlateClicked == 1
            && probe.NodeSpanClicks == 0 && probe.OwnSpanClicks == 0,
            $"plateReleased={probe.PlateReleased} plateClicked={probe.PlateClicked} spanClicks={probe.NodeSpanClicks}/{probe.OwnSpanClicks} at=({onPlainSpan.X:0.#},{onPlainSpan.Y:0.#})");

        // ── 5. A link click leaves no press latched: the very next row click behaves normally ───────────────────────
        //     The suppression is an `else` around the release dispatch, NOT an early `break` — the release case still
        //     falls through to its `_down`/`_dragTarget` teardown on the link path. The observable form of that is
        //     here: click a link, then click the row, and the row must fire exactly once with nothing left pressed.
        probe.Reset();
        ClickAt(host, window, onOwnLink);
        bool nothingLatched = AnyPressed(scene, scene.Root).IsNull;
        int spanFires = probe.OwnSpanClicks;
        probe.Reset();
        ClickAt(host, window, onBarePlate);
        Check("gate.spans.link-click-leaves-no-latch a link click ends the gesture cleanly — nothing stays visually pressed and the very next click on the row fires the row once, as if the link click had never happened",
            spanFires == 1 && nothingLatched && probe.PlateReleased == 1 && probe.PlateClicked == 1
            && probe.NodeSpanClicks == 0 && probe.OwnSpanClicks == 0,
            $"linkFired={spanFires} pressedLatched={!nothingLatched} thenPlateReleased={probe.PlateReleased} thenPlateClicked={probe.PlateClicked}");

        // ── 6. The same rule on the TOUCH path ─────────────────────────────────────────────────────────────────────
        probe.Reset();
        TapAt(host, window, onNodeLink, pointerId: 1);
        Check("gate.spans.tap-link-owns-the-gesture tapping an inline hyperlink navigates ONLY — the touch path suppresses the row's release and click exactly as the mouse path does",
            probe.NodeSpanClicks == 1 && probe.LastSpanIndex == NodeLinkSpan
            && probe.PlateReleased == 0 && probe.PlateClicked == 0,
            $"spanClicks={probe.NodeSpanClicks}@{probe.LastSpanIndex} plateReleased={probe.PlateReleased} plateClicked={probe.PlateClicked}");

        // ── 7. A press that began on the row and ended on a link is the ROW's gesture, not the link's ───────────────
        //     A span action is the LEAF's (the `sameNode` gate): the widened owner-equality that lets a plate absorb a
        //     press which wandered across its inert children must never let a press elsewhere fire a link. So the row
        //     takes its ordinary release+click and the link stays silent — the mirror of check 1.
        probe.Reset();
        window.QueueInput(new InputEvent(InputKind.PointerDown, onBarePlate, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, onNodeLink, 0, 0));
        host.RunFrame();
        Check("gate.spans.press-elsewhere-never-fires-a-link a press that starts on the row and lifts over a hyperlink does NOT navigate — the link only acts on a gesture that both began and ended on it; the row takes its ordinary release and click",
            probe.NodeSpanClicks == 0 && probe.OwnSpanClicks == 0
            && probe.PlateReleased == 1 && probe.PlateClicked == 1,
            $"spanClicks={probe.NodeSpanClicks}/{probe.OwnSpanClicks} plateReleased={probe.PlateReleased} plateClicked={probe.PlateClicked}");
    }

    /// <summary>The absolute centre of the seam-published hit rect of span <paramref name="spanIndex"/> — the very
    /// artifact the dispatcher hit-tests against, so the checks aim at the link the user sees rather than at a font
    /// metric reconstructed here.</summary>
    static bool TryLinkPoint(SceneStore scene, NodeHandle text, int spanIndex, out Point2 p)
    {
        p = default;
        if (!TryLinkRect(scene, text, spanIndex, out var r)) return false;
        var abs = scene.AbsoluteRect(text);
        p = new Point2(abs.X + r.X + r.W * 0.5f, abs.Y + r.Y + r.H * 0.5f);
        return true;
    }

    static bool TryLinkRect(SceneStore scene, NodeHandle text, int spanIndex, out RectF rect)
    {
        rect = default;
        int runId = scene.Layout(text).TextStyle.SpanRunId;
        if (SpanRunTable.Shared.Resolve(runId)?.Rects is not { } rects) return false;
        foreach (var art in rects.Rects)
            if (art.Span == spanIndex && art.Kind == SpanStyle.LinkBit) { rect = art.Rect; return true; }
        return false;
    }

    /// <summary>A point on the paragraph's LEADING plain run ("Song by "), taken as the midpoint of the gap before the
    /// first link rect and verified to sit outside every link rect.</summary>
    static bool TryPlainSpanPoint(SceneStore scene, NodeHandle text, out Point2 p)
    {
        p = default;
        if (!TryLinkRect(scene, text, NodeLinkSpan, out var first) || first.X < 2f) return false;
        var abs = scene.AbsoluteRect(text);
        p = new Point2(abs.X + first.X * 0.5f, abs.Y + first.Y + first.H * 0.5f);
        return !TryLinkRect(scene, text, OwnLinkSpan, out var other)
            || !(p.X >= abs.X + other.X && p.X < abs.X + other.X + other.W);
    }
}
