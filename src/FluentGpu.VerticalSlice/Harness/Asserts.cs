using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;


namespace FluentGpu.VerticalSlice.Harness;

/// <summary>Scene / draw-list / input helpers shared by suite modules.</summary>
public static class Asserts
{
    public static NodeHandle Child(SceneStore s, NodeHandle parent, int index)
    {
        var c = s.FirstChild(parent);
        for (int i = 0; i < index && !c.IsNull; i++) c = s.NextSibling(c);
        return c;
    }
    public static bool HasGlyph(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) return true;
        return false;
    }
    public static ColorF GlyphColor(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) return g.Color;
        return default;
    }
    public static int CountGlyph(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        int n = 0;
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) n++;
        return n;
    }
    public static ColorF FirstGradientC0(HeadlessGpuDevice dev) => dev.LastGradients.Count > 0 ? dev.LastGradients[0].C0 : default;
    public static bool ColorClose(ColorF a, ColorF b, float tol)
        => MathF.Abs(a.R - b.R) < tol && MathF.Abs(a.G - b.G) < tol && MathF.Abs(a.B - b.B) < tol && MathF.Abs(a.A - b.A) < tol;
    public static bool Near(float a, float b) => MathF.Abs(a - b) < 0.5f;
    public static bool Near(float a, float b, float tol) => MathF.Abs(a - b) < tol;
    public static NodeHandle FindRole(SceneStore s, NodeHandle n, AutomationRole role)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Interaction(n).Role == role) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindRole(s, c, role);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static NodeHandle TextVisualNode(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).VisualKind == VisualKind.Text) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = TextVisualNode(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static Point2 CenterOf(SceneStore s, NodeHandle n)
    {
        var r = s.AbsoluteRect(n);
        return new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
    }
    public static float MaxAbsTrackX(AppHost host, NodeHandle n)
    {
        float best = 0f;
        if (n.IsNull) return best;
        if (host.Animation.TryGetTrackValue(n, AnimChannel.TranslateX, out float v)) best = MathF.Abs(v);
        for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
        {
            float child = MaxAbsTrackX(host, c);
            if (child > best) best = child;
        }
        return best;
    }
    public static NodeHandle FindScrollable(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if ((s.Flags(n) & NodeFlags.Scrollable) != 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollable(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static NodeHandle FindFillNode(SceneStore s, NodeHandle n, ColorF fill, float tol = 0.006f)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (ColorClose(s.Paint(n).Fill, fill, tol)) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindFillNode(s, c, fill, tol);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static NodeHandle FocusedNode(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if ((s.Flags(n) & NodeFlags.Focused) != 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FocusedNode(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static void CollectRole(SceneStore s, NodeHandle n, AutomationRole role, List<NodeHandle> outList)
    {
        if (n.IsNull) return;
        if (s.Interaction(n).Role == role) outList.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) CollectRole(s, c, role, outList);
    }
    public static List<NodeHandle> Roles(SceneStore s, AutomationRole role)
    {
        var list = new List<NodeHandle>();
        CollectRole(s, s.Root, role, list);
        return list;
    }
    public static NodeHandle FindTextNode(SceneStore s, StringTable strings, NodeHandle n, string text)
    {
        if (n.IsNull) return NodeHandle.Null;
        ref var p = ref s.Paint(n);
        if (p.VisualKind == VisualKind.Text && strings.Resolve(p.Text) == text) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindTextNode(s, strings, c, text);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static bool ChildHasAcrylic(SceneStore s, NodeHandle n)
    {
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            if (s.TryGetAcrylic(c, out _)) return true;
            if (s.FirstChild(c).IsNull && s.Paint(c).BorderWidth > 0.5f) return true;
        }
        return false;
    }
    public static NodeHandle FindPolylineStrokeNode(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).VisualKind == VisualKind.PolylineStroke) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindPolylineStrokeNode(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static int DrawPayloadSize(DrawOp op) => op switch
    {
        DrawOp.FillRoundRect => Unsafe.SizeOf<FillRoundRectCmd>(),
        DrawOp.DrawGlyphRun => Unsafe.SizeOf<DrawGlyphRunCmd>(),
        DrawOp.PushClip => Unsafe.SizeOf<ClipCmd>(),
        DrawOp.PopClip => 0,
        DrawOp.DrawImage => Unsafe.SizeOf<DrawImageCmd>(),
        DrawOp.DrawRoundRectStroke => Unsafe.SizeOf<DrawRoundRectStrokeCmd>(),
        DrawOp.DrawShadow => Unsafe.SizeOf<DrawShadowCmd>(),
        DrawOp.DrawGradientRect => Unsafe.SizeOf<DrawGradientRectCmd>(),
        DrawOp.PushLayer => Unsafe.SizeOf<PushLayerCmd>(),
        DrawOp.PopLayer => Unsafe.SizeOf<PopLayerCmd>(),
        DrawOp.DrawGradientStroke => Unsafe.SizeOf<DrawGradientStrokeCmd>(),
        DrawOp.DrawArc => Unsafe.SizeOf<DrawArcCmd>(),
        DrawOp.DrawPolylineStroke => Unsafe.SizeOf<DrawPolylineStrokeCmd>(),
        DrawOp.DrawTabShape => Unsafe.SizeOf<DrawTabShapeCmd>(),
        _ => 0,
    };
    public static void ClickNode(AppHost host, HeadlessWindow window, NodeHandle n)
    {
        var c = CenterOf(host.Scene, n);
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        host.RunFrame();
    }
    public static uint s_touchClockMs = 1000;
    public static InputEvent Touch(InputKind kind, Point2 p, uint timestampMs, uint pointerId)
        => new(kind, p, 0, 0, 0f, KeyModifiers.None, PointerKind.Touch, false, timestampMs, pointerId, 1f);
    public static Point2 Lerp(Point2 a, Point2 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    public static long TouchGesture(HeadlessWindow window, AppHost host, Point2 from, Point2 to, int steps, uint pointerId, float msPerStep)
    {
        long worst = 0;
        float acc = s_touchClockMs;
        window.QueueInput(Touch(InputKind.PointerDown, from, (uint)acc, pointerId));
        host.RunFrame();   // deliver the down on its own frame (records the pan anchor + seeds the sampler)
        for (int i = 1; i <= steps; i++)
        {
            acc += msPerStep;
            var p = Lerp(from, to, (float)i / steps);
            window.QueueInput(Touch(InputKind.PointerMove, p, (uint)acc, pointerId));
            var f = host.RunFrame();
            if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes;
        }
        acc += msPerStep;
        window.QueueInput(Touch(InputKind.PointerUp, to, (uint)acc, pointerId));
        var fu = host.RunFrame();
        if (fu.HotPhaseAllocBytes > worst) worst = fu.HotPhaseAllocBytes;
        s_touchClockMs = (uint)acc + 1000;   // advance the shared clock so the next gesture never reuses a stamp
        return worst;
    }
    public static long TouchFlick(HeadlessWindow window, AppHost host, Point2 from, Point2 to, int steps, uint pointerId, float msPerStep, int decayFrames)
    {
        long worst = TouchGesture(window, host, from, to, steps, pointerId, msPerStep);
        for (int i = 0; i < decayFrames; i++)
        {
            var f = host.RunFrame();
            if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes;
        }
        return worst;
    }
    public static long PinchGesture(HeadlessWindow window, AppHost host, Point2 a0, Point2 b0, Point2 aEnd, Point2 bEnd,
                             int steps, uint idA, uint idB, float msPerStep)
    {
        long worst = 0;
        float acc = s_touchClockMs;
        window.QueueInput(Touch(InputKind.PointerDown, a0, (uint)acc, idA));   // first contact: a pan candidate over the zoom viewport
        host.RunFrame();
        acc += msPerStep;
        window.QueueInput(Touch(InputKind.PointerDown, b0, (uint)acc, idB));   // second contact over the SAME viewport → opens the pinch
        host.RunFrame();
        for (int i = 1; i <= steps; i++)
        {
            acc += msPerStep;
            var pa = Lerp(a0, aEnd, (float)i / steps);
            var pb = Lerp(b0, bEnd, (float)i / steps);
            window.QueueInput(Touch(InputKind.PointerMove, pa, (uint)acc, idA));
            window.QueueInput(Touch(InputKind.PointerMove, pb, (uint)acc, idB));
            var f = host.RunFrame();
            if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes;
        }
        s_touchClockMs = (uint)acc + 1000;
        return worst;
    }
    public static NodeHandle AnyHovered(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if ((s.Flags(n) & NodeFlags.Hovered) != 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = AnyHovered(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static NodeHandle AnyPressed(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if ((s.Flags(n) & NodeFlags.Pressed) != 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = AnyPressed(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static NodeHandle ViewportWithItemCount(SceneStore s, NodeHandle n, int itemCount)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.TryGetScroll(n, out var sc) && sc.ItemCount == itemCount) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = ViewportWithItemCount(s, c, itemCount);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static SceneStore LayoutTree(StringTable strings, Element tree)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        return scene;
    }
    public const float ShellTitleH = 40f, ShellToolbarH = 48f, ShellPlayerH = 56f;
    public const int ShellSidebarRows = 30;
    public const float ShellRowH = 44f, ShellSidebarW = 240f;
    public static Element ShellColumnTree(float w, float h, bool withShrink, bool tallMiddle = false)
    {
        var rows = new Element[ShellSidebarRows];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl { Height = ShellRowH, Fill = ColorF.FromRgba(40, 40, 40) };
        var tallContent = new BoxEl { Direction = 1, Children = rows };   // ~30 rows × 44px = 1320px, taller than any viewport here

        var sidebarPane = new BoxEl
        {
            Direction = 1, Shrink = 0f, Width = ShellSidebarW,
            Children = [ Ui.ScrollView(tallContent) with { Grow = 1f } ],   // ScrollView factory already sets Grow=1f
        };
        var contentCard = new BoxEl { Direction = 1, Grow = 1f };
        var mainRow = new BoxEl { Direction = 0, Grow = 1f, Children = [sidebarPane, contentCard] };

        // The middle ZStack's layers: the main row, plus (optionally) a tall intrinsic-height layer. A ZStack sizes to
        // its tallest child, so the tall layer makes the middle's MEASURED (base) height ~1320px — far above the window's
        // free space — without going through a viewport that would zero it.
        Element middle = tallMiddle
            ? Ui.ZStack(mainRow, new BoxEl { Height = ShellSidebarRows * ShellRowH }) with { Grow = 1f }
            : Ui.ZStack(mainRow) with { Grow = 1f };
        if (withShrink) middle = ((BoxEl)middle) with { Shrink = 1f };

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children =
            [
                new BoxEl { Height = ShellTitleH },        // titlebar
                new BoxEl { Height = ShellToolbarH },      // toolbar
                middle,                                    // fill
                new BoxEl { Height = ShellPlayerH },       // playerbar
            ],
        };
        return Ui.ZStack(column) with { Grow = 1f, Width = w, Height = h };
    }
    public static NodeHandle PlainViewport(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.TryGetScroll(n, out var sc) && sc.ItemCount == 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = PlainViewport(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static void PumpEffects(RenderContext c)
    {
        var q = c.PendingEffects;
        while (q.Count > 0) { var batch = q.ToArray(); q.Clear(); foreach (var a in batch) a(); }
    }
    public static int CountText(SceneStore s, NodeHandle n)
    {
        int c = s.Paint(n).VisualKind == VisualKind.Text ? 1 : 0;
        for (var ch = s.FirstChild(n); !ch.IsNull; ch = s.NextSibling(ch)) c += CountText(s, ch);
        return c;
    }
    public static int CountNodes(SceneStore s, NodeHandle n)
    {
        int c = 1;
        for (var ch = s.FirstChild(n); !ch.IsNull; ch = s.NextSibling(ch)) c += CountNodes(s, ch);
        return c;
    }
    public static NodeHandle FindScrollNode(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n)) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollNode(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
    public static int BoundSlotCount(SceneStore scene, NodeHandle root)
    {
        var vp = FindScrollNode(scene, root);
        if (vp.IsNull || !scene.TryGetScroll(vp, out var sc)) return 0;
        int n = 0;
        for (var c = scene.FirstChild(sc.ContentNode); !c.IsNull; c = scene.NextSibling(c)) n++;
        return n;
    }
    public static void CollectScrollNodes(SceneStore s, NodeHandle n, List<NodeHandle> into)
    {
        if (n.IsNull) return;
        if (s.HasScroll(n)) into.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) CollectScrollNodes(s, c, into);
    }
    public static int DirectChildCount(SceneStore s, NodeHandle n)
    {
        int c = 0;
        for (var ch = s.FirstChild(n); !ch.IsNull; ch = s.NextSibling(ch)) c++;
        return c;
    }
    public static bool PumpUntil(AppHost host, Func<bool> cond, int maxFrames = 200)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (cond()) return true;
            host.RunFrame();
        }
        return cond();
    }
    public static int CountFill(SceneStore s, NodeHandle n, ColorF target)
    {
        if (n.IsNull) return 0;
        int c = ColorApprox(s.Paint(n).Fill, target) ? 1 : 0;
        for (var ch = s.FirstChild(n); !ch.IsNull; ch = s.NextSibling(ch)) c += CountFill(s, ch, target);
        return c;
    }
    public static bool ColorApprox(ColorF a, ColorF b)
        => MathF.Abs(a.R - b.R) < 1e-3f && MathF.Abs(a.G - b.G) < 1e-3f && MathF.Abs(a.B - b.B) < 1e-3f && MathF.Abs(a.A - b.A) < 1e-3f;
    public static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
    public static string? ReadRepoFile(string relative, [CallerFilePath] string here = "")
    {
        try
        {
            // Walk up from the caller (Suites/, Harness/, …) until we find the repo root.
            string? dir = System.IO.Path.GetDirectoryName(here);
            string? repo = null;
            for (string? d = dir; d is not null; d = System.IO.Path.GetDirectoryName(d))
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(d, "src", "FluentGpu.slnx"))
                    || System.IO.File.Exists(System.IO.Path.Combine(d, "Wavee.slnx"))
                    || System.IO.Directory.Exists(System.IO.Path.Combine(d, ".git")))
                {
                    repo = d;
                    break;
                }
            }
            if (repo is null) return null;
            string path = System.IO.Path.Combine(repo, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    public static (int Order, int ClipDepth) FindFillCommandNear(DrawList dl, ColorF fill, float tol = 0.006f)
    {
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0, order = 0, clipDepth = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            if (op == DrawOp.FillRoundRect)
            {
                var cmd = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos, Unsafe.SizeOf<FillRoundRectCmd>()));
                if (MathF.Abs(cmd.Fill.R - fill.R) <= tol && MathF.Abs(cmd.Fill.G - fill.G) <= tol
                    && MathF.Abs(cmd.Fill.B - fill.B) <= tol && MathF.Abs(cmd.Fill.A - fill.A) <= tol)
                    return (order, clipDepth);
            }
            if (op == DrawOp.PushClip) clipDepth++;
            else if (op == DrawOp.PopClip) clipDepth--;
            pos += DrawPayloadSize(op);
            order++;
        }
        return (-1, -1);
    }

    public static (int Order, int ClipDepth) FindFillCommand(DrawList dl, ColorF fill)
    {
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0, order = 0, clipDepth = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            if (op == DrawOp.FillRoundRect)
            {
                var cmd = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos, Unsafe.SizeOf<FillRoundRectCmd>()));
                if (cmd.Fill.Equals(fill)) return (order, clipDepth);
            }
            if (op == DrawOp.PushClip) clipDepth++;
            else if (op == DrawOp.PopClip) clipDepth--;
            pos += DrawPayloadSize(op);
            order++;
        }
        return (-1, -1);
    }

    public static float ActiveStrokeTrimEnd(SceneStore s, NodeHandle n)
    {
        float best = float.NaN;
        float t = s.Paint(n).StrokeTrimEnd;
        if (!float.IsNaN(t)) best = t;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            float ct = ActiveStrokeTrimEnd(s, c);
            if (!float.IsNaN(ct) && (float.IsNaN(best) || ct < best)) best = ct;
        }
        return best;
    }

    /// <summary>Tiny filled box scene for AnimEngine unit checks.</summary>
    public static SceneStore Single(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(200, 0, 0) }, null);
        return scene;
    }
}
