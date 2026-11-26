using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;

namespace FluentGpu.Controls;

/// <summary>
/// A target marker ("drag here"): a dashed ring with a faint core and a solid ring that pings out of it on a loop, in one
/// colour that means "the place to reach". <c>done</c> turns it into a filled success disc with a check (the ping stops). Reduced
/// motion keeps the dashed ring and drops the ping (a value: the loop is not seeded). The marker draws nothing outside its
/// <see cref="Style.Size"/> box except the ping, which grows past it (place it in a box that does not clip, or give the box room).
/// It takes no input: a target is where something is dropped, not a control.
/// </summary>
public static class Beacon
{
    /// <summary>The marker's look. Colours are read where the marker is built (a theme switch rebuilds it).</summary>
    public sealed record Style
    {
        /// <summary>The ring's colour: the hint colour (a colour no lane, song or accent uses).</summary>
        public ColorF Color { get; init; } = Tok.AccentDefault;
        /// <summary>The disc's colour once <c>done</c>.</summary>
        public ColorF Done { get; init; } = Tok.SystemFillSuccess;
        /// <summary>The check's colour on the disc.</summary>
        public ColorF OnDone { get; init; } = Tok.TextOnAccentPrimary;
        /// <summary>The marker's diameter (DIP).</summary>
        public float Size { get; init; } = 26f;
        /// <summary>The rings' stroke (DIP).</summary>
        public float Stroke { get; init; } = 2f;
        /// <summary>The core's alpha (a share of <see cref="Color"/>).</summary>
        public float CoreAlpha { get; init; } = 0.12f;
        /// <summary>How far the ping grows (× the size) before it is gone.</summary>
        public float PingScale { get; init; } = 1.9f;
        /// <summary>One ping (ms).</summary>
        public float PingMs { get; init; } = 1600f;
        /// <summary>0 = a circle (Fluent); a positive radius rounds a square (a theme that squares its controls).</summary>
        public float? Corner { get; init; }
    }

    /// <summary>A marker in <paramref name="style"/>; <paramref name="done"/>: the target was reached.</summary>
    public static Element Create(Style style, bool done = false, string? key = null)
        => Embed.Comp(new Props(style, done), static () => new BeaconCore()) with { Key = key };

    internal sealed record Props(Style Style, bool Done);

    /// <summary>A circle of <paramref name="d"/> DIP as a path (four cubics), for a dashed stroke.</summary>
    internal static PathData Circle(float d, float inset)
    {
        float r = d * 0.5f - inset, c = d * 0.5f, k = r * 0.5523f;
        var b = new PathBuilder();
        b.MoveTo(c + r, c);
        b.CubicTo(c + r, c + k, c + k, c + r, c, c + r);
        b.CubicTo(c - k, c + r, c - r, c + k, c - r, c);
        b.CubicTo(c - r, c - k, c - k, c - r, c, c - r);
        b.CubicTo(c + k, c - r, c + r, c - k, c + r, c);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    /// <summary>A rounded square of <paramref name="d"/> DIP as a path, for a dashed stroke.</summary>
    internal static PathData Square(float d, float inset, float corner)
    {
        float a = inset, z = d - inset, r = Math.Clamp(corner, 0f, (z - a) * 0.5f), k = r * 0.5523f;
        var b = new PathBuilder();
        b.MoveTo(a + r, a);
        b.LineTo(z - r, a);
        if (r > 0f) b.CubicTo(z - r + k, a, z, a + r - k, z, a + r);
        b.LineTo(z, z - r);
        if (r > 0f) b.CubicTo(z, z - r + k, z - r + k, z, z - r, z);
        b.LineTo(a + r, z);
        if (r > 0f) b.CubicTo(a + r - k, z, a, z - r + k, a, z - r);
        b.LineTo(a, a + r);
        if (r > 0f) b.CubicTo(a, a + r - k, a + r - k, a, a + r, a);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }
}

internal sealed class BeaconCore : Component
{
    PathData? _ring;
    (float Size, float Stroke, float? Corner) _ringKey = (-1f, -1f, null);

    public override Element Render()
    {
        var p = UseProps<Beacon.Props>();
        var s = p.Style;
        float d = s.Size;
        bool square = s.Corner is not null;
        float corner = square ? s.Corner!.Value : d * 0.5f;
        if (_ring is null || _ringKey != (d, s.Stroke, s.Corner))
        {
            _ringKey = (d, s.Stroke, s.Corner);
            _ring = square ? Beacon.Square(d, s.Stroke * 0.5f, corner) : Beacon.Circle(d, s.Stroke * 0.5f);
        }
        bool ping = !p.Done && !Motion.ReducedMotion;
        var pingRef = UseRef<NodeHandle>(default);
        UseLayoutEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = pingRef.Value;
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node)) return;
            if (!ping)
            {
                anim.CancelToRest(node, AnimChannel.ScaleX);
                anim.CancelToRest(node, AnimChannel.ScaleY);
                anim.CancelToRest(node, AnimChannel.Opacity);
                return;
            }
            // A ping: from inside the core out past the marker, fading as it grows (CSS "ping": scale .8 → 1.9, opacity .9 → 0).
            var grow = new Keyframe[] { new(0f, 0.8f, Easing.Linear), new(1f, s.PingScale, Easing.FluentDecelerate) };
            var fade = new Keyframe[] { new(0f, 0.9f, Easing.Linear), new(1f, 0f, Easing.FluentDecelerate) };
            anim.Keyframes(node, AnimChannel.ScaleX, grow, s.PingMs, loop: true);
            anim.Keyframes(node, AnimChannel.ScaleY, grow, s.PingMs, loop: true);
            anim.Keyframes(node, AnimChannel.Opacity, fade, s.PingMs, loop: true);
        }, DepKey.From(HashCode.Combine(ping, d, s.PingMs, s.PingScale)));

        var corners = CornerRadius4.All(corner);
        if (p.Done)
        {
            return new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, Corners = corners, Fill = s.Done, HitTestVisible = false,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Ui.Icon(Icons.Accept, MathF.Round(d * 0.5f), s.OnDone)],
            };
        }
        return new BoxEl
        {
            ZStack = true, Width = d, Height = d, Shrink = 0f, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Key = "beacon:ping", Width = d, Height = d, Corners = corners, BorderColor = s.Color, BorderWidth = s.Stroke,
                    Opacity = ping ? 0f : 0f, TransformOriginX = 0.5f, TransformOriginY = 0.5f, OnRealized = h => pingRef.Value = h,
                },
                new BoxEl { Width = d, Height = d, Corners = corners, Fill = s.Color with { A = s.Color.A * s.CoreAlpha } },
                new PathEl { Geometry = _ring, Width = d, Height = d, StrokeColor = s.Color, Stroke = new StrokeStyle(s.Stroke, LineCap.Butt, LineJoin.Round, 4f, s.Stroke * 2f, s.Stroke * 1.5f) },
            ],
        };
    }
}
