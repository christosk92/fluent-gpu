using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Sub-tile damage (gpu-renderer.md §13.1l, <see cref="TileDamage"/>): a tile re-rastered only over the diff's damage — the
/// damage cleared, the ops that reach it replayed under a scissor cut to it, every other pixel kept — must hold exactly the
/// bytes a whole re-raster of the new op list writes. The equivalence is checked on a CPU model of the tile replay
/// (painter-order fills with src-over, scissor clips, opacity layers) over thousands of random scene edits, plus the
/// table's snapshot lifecycle (what may and may not be partial).
/// </summary>
public sealed class TileDamageTests
{
    // ── the diff ───────────────────────────────────────────────────────────────────────────────────────────────

    private static TileOpRec Op(ulong hash, float x, float y, float w, float h, ulong scope = 0, byte flags = 0)
        => TileOpRec.Of(hash, scope, new RectF(x, y, w, h), flags, 1024, 512);

    [Fact]
    public void IdenticalLists_HaveNoDamage()
    {
        TileOpRec[] a = [Op(1, 0, 0, 10, 10), Op(2, 20, 20, 5, 5)];
        Assert.True(TileDamage.Diff(a, a, 100, 100, out PixelRect d));
        Assert.True(d.IsEmpty);
    }

    [Fact]
    public void AnOpChangedInPlace_DamagesItsOldAndNewFootprint_Only()
    {
        TileOpRec[] a = [Op(1, 0, 0, 100, 100), Op(2, 10, 10, 4, 4), Op(3, 60, 60, 10, 10)];
        TileOpRec[] b = [Op(1, 0, 0, 100, 100), Op(9, 12, 10, 4, 4), Op(3, 60, 60, 10, 10)];
        Assert.True(TileDamage.Diff(a, b, 100, 100, out PixelRect d));
        Assert.Equal(new PixelRect(10, 10, 16, 14), d);
    }

    [Fact]
    public void AnInsertedAndADeletedOp_DamageOnlyThemselves()
    {
        TileOpRec[] a = [Op(1, 0, 0, 100, 100), Op(2, 10, 10, 4, 4), Op(3, 60, 60, 10, 10), Op(4, 80, 0, 5, 5)];
        TileOpRec[] b = [Op(1, 0, 0, 100, 100), Op(3, 60, 60, 10, 10), Op(5, 30, 30, 2, 2), Op(4, 80, 0, 5, 5)];
        Assert.True(TileDamage.Diff(a, b, 100, 100, out PixelRect d));
        Assert.Equal(new PixelRect(10, 10, 32, 32), d);
    }

    [Fact]
    public void AnOpMovedIntoAScope_IsDamagedEvenWithItsBytesAndFootprintUnchanged()
    {
        TileOpRec[] a = [Op(1, 0, 0, 50, 50), Op(2, 10, 10, 4, 4, scope: 0)];
        TileOpRec[] b = [Op(1, 0, 0, 50, 50), Op(2, 10, 10, 4, 4, scope: 77)];
        Assert.True(TileDamage.Diff(a, b, 100, 100, out PixelRect d));
        Assert.Equal(new PixelRect(10, 10, 14, 14), d);
    }

    [Fact]
    public void ABlurLayerAnywhere_OrAnUnboundedChange_IsWholeTile()
    {
        TileOpRec[] a = [Op(1, 0, 0, 10, 10), Op(2, 50, 50, 10, 10, flags: TileOpRec.FlagSpread)];
        TileOpRec[] b = [Op(3, 0, 0, 10, 10), Op(2, 50, 50, 10, 10, flags: TileOpRec.FlagSpread)];
        Assert.False(TileDamage.Diff(a, b, 100, 100, out _));
        TileOpRec[] c = [Op(1, 0, 0, 10, 10), Op(4, 0, 0, 0, 0, flags: TileOpRec.FlagInfinite)];
        TileOpRec[] e = [Op(1, 0, 0, 10, 10), Op(5, 0, 0, 0, 0, flags: TileOpRec.FlagInfinite)];
        Assert.False(TileDamage.Diff(c, e, 100, 100, out _));
        Assert.True(TileDamage.Diff(c, c, 100, 100, out _));   // a MATCHED unbounded op paints the same pixels
    }

    [Fact]
    public void Round_GrowsByASlackPixel_SnapsToTheQuadGrid_AndCutsToTheSurface()
    {
        Assert.Equal(new PixelRect(8, 2, 16, 8), TileDamage.Round(new RectF(9.5f, 3.2f, 4f, 2f), 100, 100));
        Assert.Equal(new PixelRect(0, 0, 100, 50), TileDamage.Round(new RectF(-5f, -5f, 200f, 200f), 100, 50));
        Assert.True(TileDamage.Round(default(PixelRect), 100, 100).IsEmpty);
        Assert.True(TileDamage.Round(default(RectF), 100, 100).IsEmpty);
    }

    // ── pixel identity on a CPU model of the tile replay ─────────────────────────────────────────────────────────

    private const int W = 96, H = 64;

    /// <summary>A scene node: a fill, or a clip / opacity-layer scope around its children.</summary>
    private sealed class Node
    {
        public int Kind;            // 0 fill, 1 clip, 2 layer
        public RectF Rect;
        public float R, G, B, A;    // fill colour (straight alpha) / layer opacity in A
        public List<Node> Kids = new();
        public Node Clone()
        {
            var n = (Node)MemberwiseClone();
            n.Kids = new List<Node>(Kids.Count);
            foreach (var k in Kids) n.Kids.Add(k.Clone());
            return n;
        }
    }

    private enum Code : byte { Fill, PushClip, PopClip, PushLayer, PopLayer }
    private readonly record struct SimOp(Code Code, RectF Rect, float R, float G, float B, float A);

    private static void Flatten(Node n, List<SimOp> ops)
    {
        switch (n.Kind)
        {
            case 0: ops.Add(new SimOp(Code.Fill, n.Rect, n.R, n.G, n.B, n.A)); break;
            case 1:
                ops.Add(new SimOp(Code.PushClip, n.Rect, 0, 0, 0, 0));
                foreach (var k in n.Kids) Flatten(k, ops);
                ops.Add(new SimOp(Code.PopClip, default, 0, 0, 0, 0));
                break;
            default:
                ops.Add(new SimOp(Code.PushLayer, n.Rect, 0, 0, 0, n.A));
                foreach (var k in n.Kids) Flatten(k, ops);
                ops.Add(new SimOp(Code.PopLayer, default, 0, 0, 0, 0));
                break;
        }
    }

    private static ulong HashOf(in SimOp o)
    {
        ulong h = TileContentHash.Fold(TileContentHash.Empty, (ulong)o.Code + 1);
        h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.Rect.X)); h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.Rect.Y));
        h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.Rect.W)); h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.Rect.H));
        h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.R)); h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.G));
        h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.B)); h = TileContentHash.Fold(h, BitConverter.SingleToUInt32Bits(o.A));
        return h == 0 ? 1UL : h;
    }

    /// <summary>The op list the content scan and <see cref="TileContentHash.CollectTileOps"/> would hand the diff: every
    /// fill / scope push, its footprint cut by the clip open around it, the fold of the open scopes' hashes; pops and ops
    /// that miss the tile are left out.</summary>
    private static TileOpRec[] Recs(List<SimOp> ops)
    {
        var recs = new List<TileOpRec>();
        var clips = new Stack<RectF>();
        var sigs = new Stack<ulong>();
        RectF top = RectF.Infinite;
        ulong sig = TileContentHash.Empty;
        var tile = new RectF(0, 0, W, H);
        foreach (var o in ops)
        {
            ulong hash = HashOf(in o);
            switch (o.Code)
            {
                case Code.Fill:
                case Code.PushClip:
                case Code.PushLayer:
                {
                    // the scan cuts a footprint by the clip grown by a slack pixel (the scissor rounds the clip OUT)
                    RectF b = top.IsInfinite ? o.Rect : o.Rect.Intersect(Slack(top));
                    if (!b.IsEmpty && b.Overlaps(tile)) recs.Add(TileOpRec.Of(hash, sig, b, 0, W, H));
                    if (o.Code == Code.Fill) break;
                    b = top.IsInfinite ? o.Rect : o.Rect.Intersect(top);
                    sigs.Push(sig);
                    sig = TileContentHash.Fold(sig, hash);
                    if (o.Code == Code.PushClip) { clips.Push(top); top = b; }
                    else clips.Push(top);
                    break;
                }
                default:
                    sig = sigs.Pop();
                    top = clips.Pop();
                    break;
            }
        }
        return recs.ToArray();
    }

    /// <summary>The model replay: premultiplied RGBA, src-over fills covering the pixels whose CENTRE is inside the rect,
    /// a scissor of whole pixels (the clip rounded out, cut by the enclosing scissor and the <paramref name="damage"/>), an
    /// opacity layer rendered into a cleared scratch and composited back inside its rect at its opacity. A partial replay
    /// (<paramref name="partial"/>) keeps <paramref name="target"/> outside the damage, clears the damage, and skips (culls)
    /// every fill whose footprint misses it — exactly the backend's partial raster.</summary>
    private static void Replay(List<SimOp> ops, float[] target, bool partial, PixelRect damage)
    {
        var full = new PixelRect(0, 0, W, H);
        PixelRect clamp = partial ? damage : full;
        if (partial) { for (int y = clamp.Top; y < clamp.Bottom; y++) Array.Clear(target, (y * W + clamp.Left) * 4, (clamp.Right - clamp.Left) * 4); }
        else Array.Clear(target);
        var bufs = new Stack<(float[] Buf, PixelRect Scissor, RectF Rect, float Alpha)>();
        var scissors = new Stack<PixelRect>();
        float[] cur = target;
        PixelRect sc = Cut(full, clamp);
        RectF top = RectF.Infinite;
        var tops = new Stack<RectF>();
        foreach (var o in ops)
        {
            switch (o.Code)
            {
                case Code.Fill:
                {
                    RectF b = top.IsInfinite ? o.Rect : o.Rect.Intersect(Slack(top));
                    if (partial && !Overlaps(b, damage)) break;   // the decode-time cull
                    float pa = o.A;
                    for (int y = sc.Top; y < sc.Bottom; y++)
                        for (int x = sc.Left; x < sc.Right; x++)
                        {
                            float cx = x + 0.5f, cy = y + 0.5f;
                            if (cx < o.Rect.X || cx >= o.Rect.Right || cy < o.Rect.Y || cy >= o.Rect.Bottom) continue;
                            int i = (y * W + x) * 4;
                            float inv = 1f - pa;
                            cur[i] = o.R * pa + cur[i] * inv; cur[i + 1] = o.G * pa + cur[i + 1] * inv;
                            cur[i + 2] = o.B * pa + cur[i + 2] * inv; cur[i + 3] = pa + cur[i + 3] * inv;
                        }
                    break;
                }
                case Code.PushClip:
                    scissors.Push(sc); tops.Push(top);
                    top = top.IsInfinite ? o.Rect : o.Rect.Intersect(top);
                    sc = Cut(sc, RoundOut(o.Rect));
                    break;
                case Code.PopClip:
                    sc = scissors.Pop(); top = tops.Pop();
                    break;
                case Code.PushLayer:
                    bufs.Push((cur, sc, o.Rect, o.A));
                    cur = new float[W * H * 4];
                    break;
                case Code.PopLayer:
                {
                    var (parent, psc, rect, alpha) = bufs.Pop();
                    PixelRect area = Cut(psc, RoundOut(rect));
                    for (int y = area.Top; y < area.Bottom; y++)
                        for (int x = area.Left; x < area.Right; x++)
                        {
                            int i = (y * W + x) * 4;
                            float sa = cur[i + 3] * alpha, inv = 1f - sa;
                            parent[i] = cur[i] * alpha + parent[i] * inv; parent[i + 1] = cur[i + 1] * alpha + parent[i + 1] * inv;
                            parent[i + 2] = cur[i + 2] * alpha + parent[i + 2] * inv; parent[i + 3] = sa + parent[i + 3] * inv;
                        }
                    cur = parent;
                    break;
                }
            }
        }
    }

    private static RectF Slack(in RectF r) => new(r.X - 1f, r.Y - 1f, r.W + 2f, r.H + 2f);

    private static string Dump(List<SimOp> ops)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in ops) sb.Append(o.Code).Append(' ').Append(o.Rect).Append(" a=").Append(o.A).AppendLine();
        return sb.ToString();
    }

    private static bool Overlaps(in RectF r, in PixelRect p) => !r.IsEmpty && r.X < p.Right && p.Left < r.Right && r.Y < p.Bottom && p.Top < r.Bottom;
    private static PixelRect RoundOut(in RectF r)
        => new((int)MathF.Floor(r.X), (int)MathF.Floor(r.Y), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom));
    private static PixelRect Cut(in PixelRect a, in PixelRect b)
    {
        int l = Math.Max(a.Left, b.Left), t = Math.Max(a.Top, b.Top), r = Math.Min(a.Right, b.Right), btm = Math.Min(a.Bottom, b.Bottom);
        return new PixelRect(l, t, Math.Max(l, r), Math.Max(t, btm));
    }

    private static RectF RandRect(Random rng)
    {
        float x = rng.Next(-8, W) + (rng.Next(4) == 0 ? (float)rng.NextDouble() : 0f);
        float y = rng.Next(-8, H) + (rng.Next(4) == 0 ? (float)rng.NextDouble() : 0f);
        return new RectF(x, y, rng.Next(1, 40) + (float)rng.NextDouble() * (rng.Next(3) == 0 ? 1f : 0f), rng.Next(1, 30));
    }

    private static Node RandNode(Random rng, int depth)
    {
        int kind = depth < 3 && rng.Next(5) == 0 ? 1 + rng.Next(2) : 0;
        var n = new Node
        {
            Kind = kind, Rect = RandRect(rng),
            R = (float)rng.NextDouble(), G = (float)rng.NextDouble(), B = (float)rng.NextDouble(),
            A = rng.Next(3) == 0 ? 1f : (float)rng.NextDouble(),
        };
        if (kind != 0) for (int i = rng.Next(0, 4); i > 0; i--) n.Kids.Add(RandNode(rng, depth + 1));
        return n;
    }

    private static void AllContainers(Node n, List<Node> into)
    {
        if (n.Kind != 0 || into.Count == 0) into.Add(n);
        foreach (var k in n.Kids) if (k.Kind != 0) AllContainers(k, into);
    }

    /// <summary>One random edit of the scene: recolour / move / resize a node, insert or delete one, swap two siblings,
    /// move a node into another scope (or out of one), or change a clip / layer.</summary>
    private static void Edit(Node root, Random rng)
    {
        var containers = new List<Node>();
        AllContainers(root, containers);
        Node c = containers[rng.Next(containers.Count)];
        switch (rng.Next(7))
        {
            case 0 when c.Kids.Count > 0:
            {
                var k = c.Kids[rng.Next(c.Kids.Count)];
                k.R = (float)rng.NextDouble(); k.A = rng.Next(2) == 0 ? 1f : (float)rng.NextDouble();
                break;
            }
            case 1 when c.Kids.Count > 0:
            {
                var k = c.Kids[rng.Next(c.Kids.Count)];
                k.Rect = new RectF(k.Rect.X + rng.Next(-6, 7) + 0.25f * rng.Next(4), k.Rect.Y + rng.Next(-6, 7), k.Rect.W + rng.Next(-2, 3), k.Rect.H);
                break;
            }
            case 2: c.Kids.Insert(rng.Next(c.Kids.Count + 1), RandNode(rng, 2)); break;
            case 3 when c.Kids.Count > 0: c.Kids.RemoveAt(rng.Next(c.Kids.Count)); break;
            case 4 when c.Kids.Count > 1:
            {
                int i = rng.Next(c.Kids.Count - 1);
                (c.Kids[i], c.Kids[i + 1]) = (c.Kids[i + 1], c.Kids[i]);
                break;
            }
            case 5 when c.Kids.Count > 0:
            {
                // move a node into another scope (a pop moved: same bytes, now drawn under other scopes)
                var k = c.Kids[rng.Next(c.Kids.Count)];
                c.Kids.Remove(k);
                Node d = containers[rng.Next(containers.Count)];
                if (IsInside(d, k)) d = root;
                d.Kids.Insert(rng.Next(d.Kids.Count + 1), k);
                break;
            }
            default:
                if (c != root) { c.Rect = RandRect(rng); c.A = (float)rng.NextDouble(); }
                else root.Kids.Add(RandNode(rng, 1));
                break;
        }
    }

    private static bool IsInside(Node d, Node k)
    {
        if (ReferenceEquals(d, k)) return true;
        foreach (var kk in k.Kids) if (IsInside(d, kk)) return true;
        return false;
    }

    [Theory]
    [InlineData(20261005)]
    [InlineData(7)]
    [InlineData(424242)]
    public void APartialRasterOfTheDiffDamage_IsByteIdenticalToAWholeRaster_OverRandomEdits(int seed)
    {
        var rng = new Random(seed);
        int partials = 0, wholes = 0, emptyDamage = 0;
        for (int scene = 0; scene < 300; scene++)
        {
            var root = new Node { Kind = 1, Rect = new RectF(-1000, -1000, 4000, 4000) };
            for (int i = rng.Next(3, 14); i > 0; i--) root.Kids.Add(RandNode(rng, 1));
            var oldOps = new List<SimOp>();
            Flatten(root, oldOps);
            var tile = new float[W * H * 4];
            Replay(oldOps, tile, false, default);
            TileOpRec[] snapshot = Recs(oldOps);
            var prevOps = oldOps;
            for (int step = 0; step < 12; step++)
            {
                for (int e = rng.Next(1, 3); e > 0; e--) Edit(root, rng);
                var newOps = new List<SimOp>();
                Flatten(root, newOps);
                TileOpRec[] cur = Recs(newOps);
                var whole = new float[W * H * 4];
                Replay(newOps, whole, false, default);
                if (TileDamage.Diff(snapshot, cur, W, H, out PixelRect d))
                {
                    PixelRect dmg = TileDamage.Round(in d, W, H);
                    if (dmg.IsEmpty) emptyDamage++;
                    else Replay(newOps, tile, true, dmg);
                    partials++;
                    for (int i = 0; i < tile.Length; i++)
                        if (BitConverter.SingleToInt32Bits(tile[i]) != BitConverter.SingleToInt32Bits(whole[i]))
                            Assert.Fail($"scene {scene} step {step}: pixel ({i / 4 % W},{i / 4 / W}) channel {i % 4} partial {tile[i]} != whole {whole[i]} (damage {dmg}) OLD: {Dump(prevOps)} NEW: {Dump(newOps)}");
                }
                else
                {
                    wholes++;
                    Array.Copy(whole, tile, tile.Length);
                }
                snapshot = cur;
                prevOps = newOps;
            }
        }
        Assert.True(partials > 1000, $"partials={partials}");   // the property was exercised, not dodged by whole rasters
        Assert.True(emptyDamage < partials);
        _ = wholes;
    }

    // ── the table's snapshot lifecycle ──────────────────────────────────────────────────────────────────────────

    private static readonly SliceFrame Grid = new(0, 0, 0f, 0f, 1f);
    private static readonly RectF Viewport = new(0f, 0f, 1024f, 512f);

    private struct OneTile : ITileContent
    {
        public ulong KeyValue, Hash;
        public ulong Key => KeyValue;
        public ulong Want(in RectF tilePx, out int ops, out RectF paint) { ops = 1; paint = new RectF(0f, 0f, tilePx.W, tilePx.H); return Hash; }
    }

    /// <summary>One turn over a one-tile slice: request with content (key, hash), resolve, plan the raster's damage from
    /// <paramref name="ops"/> on <paramref name="grid"/>, then report it done (or not).</summary>
    private static TileRaster? Turn(SliceTable t, int frame, ulong key, ulong hash, TileOpRec[] ops, bool done = true,
        SliceFrame? grid = null, Action<SliceTable, int>? before = null, bool beyondPlan = false)
    {
        t.BeginFrame(frame);
        SliceFrame g = grid ?? Grid;
        int id = t.OpenSlice(3, 1, SliceKind.Static, in g, new RectF(0f, 0f, 1024f, 512f), mainAxisGrows: false);
        before?.Invoke(t, id);
        Span<TileKey> need = [new TileKey(id, 0, 0)];
        var content = new OneTile { KeyValue = key, Hash = hash };
        t.Request(id, in Viewport, 0.0, 512.0, false, need, default, ref content);
        var r = new TileRaster[4];
        t.Resolve(long.MaxValue, r, out int n);
        Span<TilePlacement> pl = stackalloc TilePlacement[4];
        int np = t.CollectPlacements(id, pl);
        for (int i = 0; i < np; i++) if (t.SurfaceWantKey(pl[i].Surface) != key) t.SetSurfaceWant(pl[i].Surface, key, hash, 1);
        TileRaster? planned = null;
        if (n > 0)
        {
            planned = t.PlanDamage(in r[0], in g, ops, known: true);
            if (done) t.MarkRastered(r[0].Key); else t.MarkRasterFailed(r[0].Key, beyondPlan);
        }
        t.EndFrame();
        return planned;
    }

    private static TileOpRec[] Ops(params (ulong Hash, float X)[] o)
    {
        var a = new TileOpRec[o.Length];
        for (int i = 0; i < o.Length; i++) a[i] = Op(o[i].Hash, o[i].X, 100f, 20f, 20f);
        return a;
    }

    [Fact]
    public void AContentReRaster_OfATrustedSurface_IsPartial_OverTheDiff()
    {
        var t = new SliceTable(4, 8, 8);
        Assert.False(Turn(t, 1, 7, 100, Ops((1, 10), (2, 300)))!.Value.Partial);   // first raster: NoTexture → whole
        var r = Turn(t, 2, 8, 101, Ops((1, 10), (3, 300)))!.Value;
        Assert.True(r.Partial);
        Assert.Equal(InvalidationReason.Content, r.Reason);
        Assert.Equal(TileDamage.Round(new RectF(300f, 100f, 20f, 20f), r.W, r.H), r.Damage);
        Assert.Equal(1, t.PartialRastersThisFrame);
    }

    [Fact]
    public void AnotherGrid_OrAnotherReason_RastersWhole()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, 7, 100, Ops((1, 10)));
        // the residual phase moved: every pixel may differ although no op did
        var r = Turn(t, 2, 8, 101, Ops((2, 10)), grid: new SliceFrame(0, 0, 0.5f, 0f, 1f))!.Value;
        Assert.False(r.Partial);
        Assert.Equal(InvalidationReason.SliceGeometry, r.Reason);
        var r2 = Turn(t, 3, 9, 102, Ops((3, 10)), grid: new SliceFrame(0, 0, 0.5f, 0f, 1f), before: (tb, id) => tb.InvalidateSlice(id, InvalidationReason.BackgroundOrTheme))!.Value;
        Assert.False(r2.Partial);
    }

    [Fact]
    public void AnImageCrossFadeRect_IsDamageNoOpDescribes()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, 7, 100, Ops((1, 10), (2, 300)));
        var r = Turn(t, 2, 8, 101, Ops((1, 10), (3, 300)),
            before: (tb, id) => tb.InvalidateRect(id, new RectF(600f, 200f, 10f, 10f), InvalidationReason.Content))!.Value;
        Assert.True(r.Partial);
        Assert.Equal(TileDamage.Union(TileDamage.Round(new RectF(300f, 100f, 20f, 20f), r.W, r.H),
            TileDamage.Round(new RectF(600f, 200f, 10f, 10f), r.W, r.H)), r.Damage);
    }

    [Fact]
    public void AnUnfaithfulPartialRaster_LeavesItsDamageStray_UntilAFaithfulOneCoversIt()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, 7, 100, Ops((1, 10), (2, 300)));
        var failed = Turn(t, 2, 8, 101, Ops((1, 10), (3, 300)), done: false)!.Value;   // an image still in flight
        Assert.True(failed.Partial);
        // the content moves on before the retry: the stray pixels of the failed raster must still be repainted
        var r = Turn(t, 3, 9, 102, Ops((4, 10), (2, 300)))!.Value;
        Assert.True(r.Partial);
        Assert.Equal(TileDamage.Union(TileDamage.Round(new RectF(10f, 100f, 20f, 20f), r.W, r.H), failed.Damage), r.Damage);
        // once faithful, the stray is gone
        var r2 = Turn(t, 4, 10, 103, Ops((5, 10), (2, 300)))!.Value;
        Assert.Equal(TileDamage.Round(new RectF(10f, 100f, 20f, 20f), r2.W, r2.H), r2.Damage);
    }

    [Fact]
    public void AnUnfaithfulRasterBeyondThePlan_OrAnUnfaithfulWholeOne_LeavesNoSnapshot()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, 7, 100, Ops((1, 10), (2, 300)));
        Assert.True(Turn(t, 2, 8, 101, Ops((1, 10), (3, 300)), done: false, beyondPlan: true)!.Value.Partial);
        Assert.False(Turn(t, 3, 9, 102, Ops((1, 10), (4, 300)))!.Value.Partial);   // nothing to vouch for: whole
        Assert.True(Turn(t, 4, 10, 103, Ops((1, 10), (5, 300)))!.Value.Partial);   // trusted again
    }

    [Fact]
    public void ADamageCoveringMostOfTheTile_RastersWhole()
    {
        var t = new SliceTable(4, 8, 8);
        var big = new[] { Op(1, 0f, 0f, 1024f, 400f) };
        var big2 = new[] { Op(2, 0f, 0f, 1024f, 400f) };
        Turn(t, 1, 7, 100, big);
        Assert.False(Turn(t, 2, 8, 101, big2)!.Value.Partial);
    }

    [Fact]
    public void TheSnapshotSlab_CompactsInPlace_AndKeepsEverySnapshotExact()
    {
        var t = new SliceTable(4, 8, 8);
        static TileOpRec[] Long(int changed, ulong salt)
        {
            var a = new TileOpRec[3000];
            for (int i = 0; i < a.Length; i++)
                a[i] = Op(i == changed ? salt : (ulong)i + 1, i % 1000, 10f + i / 1000 * 100f, 8f, 8f);
            return a;
        }
        Turn(t, 1, 7, 100, Long(-1, 0));
        int cap = t.DamageSlabCapacity;
        for (int turn = 2; turn < 60; turn++)
        {
            int changed = (turn * 397) % 3000;
            var r = Turn(t, turn, (ulong)turn + 7, (ulong)turn + 100, Long(changed, 0xABC0UL + (ulong)turn))!.Value;
            Assert.True(r.Partial);
            int prev = ((turn - 1) * 397) % 3000;
            PixelRect expect = TileDamage.Round(Op(1, changed % 1000, 10f + changed / 1000 * 100f, 8f, 8f) is var o
                ? new PixelRect(o.X0, o.Y0, o.X1, o.Y1) : default, r.W, r.H);
            if (turn > 2)
            {
                var p = Op(1, prev % 1000, 10f + prev / 1000 * 100f, 8f, 8f);
                expect = TileDamage.Union(in expect, TileDamage.Round(new PixelRect(p.X0, p.Y0, p.X1, p.Y1), r.W, r.H));
            }
            Assert.Equal(expect, r.Damage);
        }
        Assert.Equal(cap, t.DamageSlabCapacity);   // 3000-op lists turned over 60 times: compacted, never grown
    }

    [Fact]
    public void TheSwitch_TurnsPartialRasterOff()
    {
        bool was = TileDamage.Enabled;
        try
        {
            TileDamage.Enabled = false;
            var t = new SliceTable(4, 8, 8);
            Turn(t, 1, 7, 100, Ops((1, 10), (2, 300)));
            Assert.False(Turn(t, 2, 8, 101, Ops((1, 10), (3, 300)))!.Value.Partial);
        }
        finally { TileDamage.Enabled = was; }
    }
}
