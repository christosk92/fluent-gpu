using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Controls;

/// <summary>Where a step stands: not reached, the one open now, finished, or passed without being finished (its node dashed: "set for
/// you").</summary>
public enum StepState : byte { Upcoming, Current, Done, Skipped }

/// <summary>One step of a <see cref="Stepper"/>.</summary>
/// <param name="Key">The step's stable key (its row is keyed by it).</param>
/// <param name="Title">The step's name.</param>
/// <param name="Subtitle">One line under the name ("" for none).</param>
/// <param name="State">Where the step stands.</param>
/// <param name="Marker">What the node shows while the step is not done: its number ("3"), or a glyph when <paramref name="MarkerIsGlyph"/>.</param>
/// <param name="MarkerIsGlyph">The marker is an icon-font glyph (a flag on the last step).</param>
/// <param name="Body">The open step's content (null: closed). Opening and closing reveal it; a body that grows reveals the new height.</param>
/// <param name="Passed">The connector from this step down to the next is drawn as travelled (the steps before the current one).</param>
public sealed record StepperItem(string Key, string Title, string Subtitle, StepState State, string Marker, bool MarkerIsGlyph = false,
    Element? Body = null, bool Passed = false);

/// <summary>
/// A vertical stepper: numbered nodes on one connector line, each step's name (and a line under it) beside its node, the open step's
/// body under its name. Every step's head is a button (<c>select</c> gets its index); the open one is not. Done steps show a check on
/// an accent disc, a skipped one a dashed ring, the current one an accent ring; the line down from each step before the current one
/// is drawn in the accent. Bodies open and close with the disclosure motion (the presented height springs, what follows rides it),
/// so a step change never jumps. The look (node shape, sizes, colours, the theme's type) is <see cref="Style"/>.
/// </summary>
public static class Stepper
{
    /// <summary>A stepper's look: node shape and size, line weights, colours, and the theme's text for a step's name and its line.</summary>
    public sealed record Style
    {
        /// <summary>The node's diameter (or side, when <see cref="NodeCorner"/> is set).</summary>
        public float NodeSize { get; init; } = 28f;
        /// <summary>Null: round nodes. A value: square nodes with this corner radius.</summary>
        public float? NodeCorner { get; init; }
        /// <summary>The node's top inset in its row (centres it on the name's first line).</summary>
        public float NodeTop { get; init; } = 4f;
        /// <summary>The connector's width, and its width where travelled.</summary>
        public float Line { get; init; } = 1f;
        public float PassedLine { get; init; } = 2f;
        /// <summary>The room between the node column and the text.</summary>
        public float Gap { get; init; } = 12f;
        public ColorF Accent { get; init; } = Tok.AccentDefault;
        public ColorF LineColor { get; init; } = Tok.TextDisabled;
        /// <summary>The node's own fill (the ground it sits on, so the connector does not run through it).</summary>
        public ColorF NodeFill { get; init; } = Tok.FillSolidBase;
        public ColorF MarkerInk { get; init; } = Tok.TextTertiary;
        public ColorF DoneInk { get; init; } = Tok.TextOnAccentPrimary;
        public float MarkerSize { get; init; } = 12f;
        public float GlyphSize { get; init; } = 14f;
        /// <summary>The step's name: (text, current) → the theme's text. Null: the body-strong ramp, the subtitle ramp when current.</summary>
        public Func<string, bool, TextEl>? Title { get; init; }
        /// <summary>The line under the name: (text, current) → the theme's text. Null: caption, secondary.</summary>
        public Func<string, bool, TextEl>? Subtitle { get; init; }
        /// <summary>The head button's padding (its text sits this far in; the button's hover plate is pulled back by the start inset so the
        /// words line up with the body).</summary>
        public Edges4 HeadPadding { get; init; } = new(10f, 8f, 10f, 14f);
        public float HeadCorner { get; init; } = 4f;
        public ColorF HeadHover { get; init; } = Tok.FillSubtleSecondary;
        public ColorF HeadPressed { get; init; } = Tok.FillSubtleTertiary;
        /// <summary>The room under an open body before the next step.</summary>
        public float BodyBottom { get; init; } = 28f;
        /// <summary>The check glyph on a done node.</summary>
        public string DoneGlyph { get; init; } = Icons.Accept;
    }

    /// <summary>The steps of <paramref name="items"/>; <paramref name="select"/> hears a head's click with the step's index. The open step is
    /// brought into view in the nearest scroller (its head near the viewport's top): at once when the stepper mounts on it, gliding when it
    /// changes.</summary>
    public static Element Create(IReadOnlyList<StepperItem> items, Action<int> select, Style? style = null, string? key = null)
        => Embed.Comp(new Props(items, select, style ?? new Style()), static () => new StepperCore()) with { Key = key ?? "stepper" };

    internal sealed record Props(IReadOnlyList<StepperItem> Items, Action<int> Select, Style Style);

    /// <summary>The rows (the component's render).</summary>
    internal static Element[] Rows(IReadOnlyList<StepperItem> items, Action<int> select, Style s, Action<int, NodeHandle> realized)
    {
        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            int at = i;
            rows[i] = Row(items[i], i, i == items.Count - 1, select, s) with { OnRealized = h => realized(at, h) };
        }
        return rows;
    }

    /// <summary>How far above the open step's head the scroller stops (DIP).</summary>
    public const float BringMargin = 12f;

    static LayoutTransition Reveal => new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.FlowReveal,
        Anchor: SizeAnchor.Leading);

    static BoxEl Row(StepperItem it, int index, bool last, Action<int> select, Style s)
    {
        bool current = it.State == StepState.Current;
        var line = new BoxEl
        {
            Width = it.Passed ? s.PassedLine : s.Line, Grow = 1f, AlignSelf = FlexAlign.Center, Fill = it.Passed ? s.Accent : s.LineColor,
            Margin = new Edges4(0f, 4f, 0f, 4f), Visible = !last, BrushTransitionMs = 167f,
        };
        var nodeColumn = new BoxEl
        {
            Direction = 1, Width = s.NodeSize, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, s.NodeTop, 0f, 0f),
            Children = [Node(it, s), line],
        };
        var head = new List<Element>(2) { (s.Title?.Invoke(it.Title, current) ?? DefaultTitle(it.Title, current)) with { MaxLines = 2, Wrap = TextWrap.Wrap } };
        if (it.Subtitle.Length > 0)
            head.Add((s.Subtitle?.Invoke(it.Subtitle, current) ?? DefaultSubtitle(it.Subtitle, current)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
        int at = index;
        var headButton = new BoxEl
        {
            Key = "stepper:head:" + it.Key, Direction = 1, MinWidth = 0f, Padding = s.HeadPadding,
            Margin = new Edges4(-s.HeadPadding.Left, 0f, 0f, 0f), Corners = CornerRadius4.All(s.HeadCorner),
            Fill = ColorF.Transparent, HoverFill = current ? ColorF.Transparent : s.HeadHover, PressedFill = current ? ColorF.Transparent : s.HeadPressed,
            Role = AutomationRole.Button, Focusable = !current, Cursor = current ? null : CursorId.Hand,
            OnClick = current ? null : () => select(at), Children = head.ToArray(),
        };
        var body = new BoxEl
        {
            Key = "stepper:body:" + it.Key, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Reveal,
            Height = it.Body is null ? 0f : float.NaN,
            Children = it.Body is { } b ? [new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, s.BodyBottom), Children = [b] }] : [],
        };
        return new BoxEl
        {
            Key = "stepper:step:" + it.Key, Direction = 0, MinWidth = 0f,
            Children =
            [
                nodeColumn,
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Margin = new Edges4(s.Gap, 0f, 0f, 0f), Children = [headButton, body] },
            ],
        };
    }

    static Element Node(StepperItem it, Style s)
    {
        float d = s.NodeSize;
        bool square = s.NodeCorner is not null;
        var corners = CornerRadius4.All(square ? s.NodeCorner!.Value : d * 0.5f);
        TextEl Mark(ColorF ink) => it.MarkerIsGlyph
            ? Ui.Icon(it.Marker, s.GlyphSize, ink)
            : new TextEl(it.Marker) { Size = s.MarkerSize, Weight = 600, Color = ink, MaxLines = 1 };
        Element inner;
        ColorF fill = s.NodeFill, border = s.LineColor;
        float bw = 1f;
        switch (it.State)
        {
            case StepState.Done:
                fill = s.Accent; border = s.Accent;
                inner = Ui.Icon(s.DoneGlyph, s.GlyphSize, s.DoneInk);
                break;
            case StepState.Current:
                border = s.Accent; bw = s.PassedLine;
                inner = Mark(s.Accent);
                break;
            case StepState.Skipped:
                border = ColorF.Transparent;
                inner = Mark(s.Accent);
                break;
            default:
                inner = Mark(s.MarkerInk);
                break;
        }
        var kids = new List<Element>(2);
        if (it.State == StepState.Skipped)
            kids.Add(new PathEl
            {
                Geometry = square ? Beacon.Square(d, 0.5f, s.NodeCorner!.Value) : Beacon.Circle(d, 0.5f), Width = d, Height = d,
                StrokeColor = s.Accent, Stroke = new StrokeStyle(1f, LineCap.Butt, LineJoin.Round, 4f, 3f, 2.5f),
            });
        kids.Add(new BoxEl { Width = d, Height = d, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [inner] });
        return new BoxEl
        {
            ZStack = true, Width = d, Height = d, Shrink = 0f, Corners = corners, Fill = fill, BorderColor = border, BorderWidth = bw,
            BrushTransitionMs = 167f, HitTestVisible = false, Children = kids.ToArray(),
        };
    }

    static TextEl DefaultTitle(string text, bool current)
        => current ? Ui.Subtitle(text) : Ui.BodyStrong(text);

    static TextEl DefaultSubtitle(string text, bool current)
        => Ui.Caption(text) with { Color = current ? Tok.TextSecondary : Tok.TextTertiary };
}

internal sealed class StepperCore : Component
{
    readonly List<NodeHandle> _rows = new();

    public override Element Render()
    {
        var p = UseProps<Stepper.Props>();
        int open = -1;
        for (int i = 0; i < p.Items.Count; i++) if (p.Items[i].State == StepState.Current) { open = i; break; }
        while (_rows.Count < p.Items.Count) _rows.Add(NodeHandle.Null);
        var first = UseRef(true);
        // The open step is brought into view: at once when the stepper mounts on it (a page reopened on step 4, a layout that swapped), with a
        // glide when it changes.
        UseLayoutEffect(() =>
        {
            bool initial = first.Value;
            first.Value = false;
            if (open < 0 || open >= _rows.Count) return;
            var scene = Context.Scene;
            var node = _rows[open];
            if (scene is null || node.IsNull || !scene.IsLive(node)) return;
            scene.BringIntoView(node, align: 0f, move: initial || Motion.ReducedMotion ? ScrollMove.Immediate : ScrollMove.Glide, margin: Stepper.BringMargin);
        }, DepKey.From(open));
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = Stepper.Rows(p.Items, p.Select, p.Style, (i, h) => { if (i < _rows.Count) _rows[i] = h; }) };
    }
}
