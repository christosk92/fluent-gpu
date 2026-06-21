using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Connected animation / shared-element (Hero) transitions (backdrop-effects-animation.md §5.4/§5.6/§5.9). A node
/// tagged with a shared key (<see cref="FluentGpu.Dsl.Element.MorphId"/>) flies its album-art image from the rect it
/// occupied on the LEAVING route to the rect it occupies on the ARRIVING route — drawn as an UNCLIPPED overlay above
/// all page content (so a grid card flying into a narrow detail rail is never cut off by the rail's scissor).
///
/// Lifecycle: a node is registered by the reconciler (<see cref="NoteTagged"/>) and its laid-out rect + image are
/// remembered each frame (<see cref="Tick65"/>, phase 6.5, Bounds valid). A source snapshot is captured either at
/// click (<see cref="Begin"/>, forward nav) or on the source's unmount (<see cref="NoteUntagged"/>, reverse nav).
/// When a like-tagged node is live + laid out on the new route, <see cref="Tick65"/> seeds an overlay that springs
/// from the source rect to the dest rect; <see cref="Settle"/> retires it (revealing the real dest) once it arrives.
///
/// The single-slot-per-key snapshot supersedes on a second capture (releasing the prior pin); an unclaimed snapshot
/// expires after <see cref="MaxPendingAge"/> frames (fast nav-away / a dest that never mounts) — never leaking a pin.
/// Under reduced motion the whole thing degrades to the instant swap (no capture, no fly).
/// </summary>
/// <summary>How a connected-animation fly travels: a critically-damped (or springy) SPRING, or an EASED curve with any
/// <see cref="EasingSpec"/> — a named easing OR a custom cubic-bézier (<c>EasingSpec.CubicBezier(x1,y1,x2,y2)</c>) — over
/// a fixed duration. Set on <see cref="ConnectedAnimation.FlyMotion"/> (a tunable motion token).</summary>
public readonly struct ConnectedMotion
{
    public readonly bool IsSpring;
    public readonly SpringParams Spring;
    public readonly EasingSpec Easing;
    public readonly float DurationMs;

    private ConnectedMotion(bool spring, SpringParams sp, EasingSpec e, float dur) { IsSpring = spring; Spring = sp; Easing = e; DurationMs = dur; }

    /// <summary>A spring fly (velocity-continuous on interruption). Default uses a critically-damped 0.40s response.</summary>
    public static ConnectedMotion Springy(in SpringParams spring) => new(true, spring, default, 0f);
    /// <summary>An eased fly with a custom curve (named or cubic-bézier) over <paramref name="durationMs"/>.</summary>
    public static ConnectedMotion Eased(EasingSpec easing, float durationMs) => new(false, default, easing, durationMs);
    public static ConnectedMotion Default => Springy(SpringParams.FromResponse(0.32f, 1.0f));   // critically damped — smooth, no overshoot; tightened from 0.40s so a long Home→Detail fly settles into place instead of sailing
}

public sealed class ConnectedAnimation
{
    private readonly SceneStore _scene;
    private readonly AnimEngine _anim;
    private readonly ImageCache _images;

    // A live shared-element participant: its key + last-laid-out world rect/image (the reverse-capture source data).
    private struct Tag { public string Key; public RectF Rect; public bool HasRect; public int ImageId; public CornerRadius4 Corners; public byte Fit; }
    private readonly Dictionary<NodeHandle, Tag> _tagged = new();

    // Single-slot-per-key source snapshot awaiting a dest on the new route.
    private struct Snapshot { public RectF Rect; public int ImageId; public CornerRadius4 Corners; public byte Fit; public NodeHandle Source; public int Age; }
    private readonly Dictionary<string, Snapshot> _pending = new();
    private readonly HashSet<string> _freshlyCaptured = new();   // keys captured by Begin() this frame ⇒ suppress the source's own NoteUntagged reverse-capture

    // In-flight overlay flies. Keyed so every live node carrying the key stays hidden under the overlay until it lands.
    // SrcCorners→DestCorners morph the rounding over the fly (a circular artist cover squaring into a card slot); Sx0 is
    // the seed scale, so the morph progress tracks the spring (1 at the source, 0-radius-delta at the dest).
    private struct Flight { public NodeHandle Overlay; public NodeHandle Dest; public string Key; public int ImageId; public int Age; public CornerRadius4 SrcCorners, DestCorners; public float Sx0; public float LastDx, LastM11; public bool HasLast; public RectF DestRect; }
    private readonly List<Flight> _flights = new();
    private const int MaxFlightFrames = 240;   // defensive: force-retire a wedged fly (~4s) so an overlay can never get stuck

    // Reused scratch (alloc-free steady state): keys/nodes touched this frame.
    private readonly List<NodeHandle> _scratchNodes = new();
    private readonly List<string> _scratchKeys = new();

    private const int MaxPendingAge = 30;   // frames a snapshot waits for its dest before expiring (~0.5s — the detail
                                            // page's skeleton reserves a tagged cover slot immediately, so the dest mounts fast)
    private const float RevealMs = 130f;    // dest cross-fade-in once the overlay lands
    private const float FadeInMs = 110f;    // overlay materialize-in: the cover eases in instead of hard-popping at the source rect

    /// <summary>The fly motion (a tunable "motion token" the theme/app can set) — a SPRING or an EASED curve with a
    /// custom <see cref="EasingSpec"/> + duration. Defaults to the WinUI shared-element DECELERATE curve (fast launch,
    /// smooth no-overshoot landing). Set <c>FlyMotion = ConnectedMotion.Springy(...)</c> or another <c>Eased(...)</c>.</summary>
    public ConnectedMotion FlyMotion { get; set; } = ConnectedMotion.Eased(Easing.FluentDecelerate, 300f);

    /// <summary>OS / theme reduced-motion gate: when true, capture + fly are skipped (instant swap), per §5.9.</summary>
    public bool ReducedMotion { get; set; }

    private static readonly bool MorphLog = System.Environment.GetEnvironmentVariable("FG_MORPH_LOG") == "1";

    public ConnectedAnimation(SceneStore scene, AnimEngine anim, ImageCache images)
    {
        _scene = scene; _anim = anim; _images = images;
    }

    /// <summary>The host keeps painting while a fly is in flight or a snapshot is waiting for its dest.</summary>
    public bool HasActive => _flights.Count > 0 || _pending.Count > 0;

    /// <summary>Probe/diagnostic only: the first live shared-element key currently registered (preferring a <c>pl:</c>
    /// key — the preview-path detail). Lets a harness trigger a REAL Hero fly without knowing the catalog data. Null if none.</summary>
    public string? FirstTaggedKey
    {
        get
        {
            string? any = null;
            foreach (var kv in _tagged)
            {
                if (!_scene.IsLive(kv.Key)) continue;
                var k = kv.Value.Key;
                if (k.StartsWith("pl:", System.StringComparison.Ordinal)) return k;
                any ??= k;
            }
            return any;
        }
    }

    /// <summary>Probe/diagnostic only: collect the distinct live <c>pl:</c> shared-element keys (the home cards), so a
    /// harness can fly to several DIFFERENT (uncached) detail pages to measure the cold-mount-plus-fly cost.</summary>
    public void CollectTaggedKeys(System.Collections.Generic.List<string> into)
    {
        into.Clear();
        foreach (var kv in _tagged)
        {
            if (!_scene.IsLive(kv.Key)) continue;
            var k = kv.Value.Key;
            if (k.StartsWith("pl:", System.StringComparison.Ordinal) && k.Length > 3 && !into.Contains(k)) into.Add(k);
        }
    }

    // ── tagging (reconciler-driven) ──────────────────────────────────────────────────────────────
    /// <summary>Register a node as a shared-element participant for <paramref name="key"/> (its <c>MorphId</c>).</summary>
    public void NoteTagged(NodeHandle node, string key)
    {
        if (_tagged.TryGetValue(node, out var t) && t.Key == key) return;   // already tagged with this key
        _tagged[node] = new Tag { Key = key };
    }

    /// <summary>KeepAlive parked / un-parked a node. Reverse-fly capture on park is deferred to Phase 6 (capturing every
    /// parked tagged node on a forward nav floods the registry with snapshots that have no dest); for now this is a no-op.</summary>
    public void OnNodeParked(NodeHandle node, bool parked) { }

    /// <summary>The node is LEAVING the live route — freed (unmount, <paramref name="removeTag"/> = true) or parked by
    /// KeepAlive (<paramref name="removeTag"/> = false, the node stays a participant for when it un-parks). For a reverse
    /// transition (Back) this captures the node's remembered rect as a source snapshot so its art can fly back to the
    /// like-tagged dest on the route we are returning to — unless this key was just captured by <see cref="Begin"/>
    /// (the forward path owns it) or motion is reduced.</summary>
    public void CaptureOnLeave(NodeHandle node, bool removeTag)
    {
        if (removeTag) _tagged.Remove(node);   // cleanup; reverse-fly capture (Back) is Phase 6
    }

    // ── forward capture at click (app, before the nav route write) ───────────────────────────────
    /// <summary>Snapshot the live source tagged with <paramref name="key"/> just before navigation, so its art can fly
    /// to the dest on the new route. No-op under reduced motion or if no live source carries an image.</summary>
    public void Begin(string key)
    {
        if (ReducedMotion) return;
        foreach (var kv in _tagged)
        {
            if (kv.Value.Key != key || !_scene.IsLive(kv.Key)) continue;
            ref NodePaint p = ref _scene.Paint(kv.Key);
            if (p.ImageId == 0) continue;
            var sr = _scene.AbsoluteRect(kv.Key);
            if (MorphLog) System.Console.Error.WriteLine($"[morph] Begin key={key} src node={kv.Key.Raw.Index} rect=({sr.X:0},{sr.Y:0},{sr.W:0},{sr.H:0}) img={p.ImageId}");
            Register(key, new Snapshot { Rect = sr, ImageId = p.ImageId, Corners = p.Corners, Fit = p.ImageFit, Source = kv.Key });
            _freshlyCaptured.Add(key);
            return;
        }
    }

    private void Register(string key, Snapshot snap)
    {
        if (_pending.TryGetValue(key, out var old)) _images.Unpin(new ImageHandle(old.ImageId));   // supersede: release the prior pin
        _images.Pin(new ImageHandle(snap.ImageId));   // survive the source's synchronous unmount until the fly retires
        _pending[key] = snap;
    }

    // ── phase 6.5: remember rects, seed flies to arrived dests, expire stale ─────────────────────
    /// <summary>Called once per frame after layout (Bounds valid, before the anim tick): refresh every live tag's
    /// remembered rect/image, seed a fly for any pending snapshot whose dest is now live + laid out, and age out
    /// snapshots that never found a dest. Allocation-free in steady state (no tags / no pending / no flights).</summary>
    public void Tick65()
    {
        // 1. Remember the current laid-out rect + image of every live tag (the reverse-capture source data) and drop dead ones.
        if (_tagged.Count > 0)
        {
            _scratchNodes.Clear();
            foreach (var node in _tagged.Keys) _scratchNodes.Add(node);
            for (int i = 0; i < _scratchNodes.Count; i++)
            {
                var node = _scratchNodes[i];
                if (!_scene.IsLive(node)) { _tagged.Remove(node); continue; }
                if (!OnLiveTree(node)) continue;   // parked/detached: keep the last-good rect for a reverse capture
                ref NodePaint p = ref _scene.Paint(node);
                RectF r = _scene.AbsoluteRect(node);
                var t = _tagged[node];
                if (r.W > 1f && r.H > 1f) { t.Rect = r; t.HasRect = true; }
                t.ImageId = p.ImageId; t.Corners = p.Corners; t.Fit = p.ImageFit;
                _tagged[node] = t;
            }
        }

        // 2. Seed flies: for each pending snapshot, find a live dest tagged with the same key (NOT the source) and fly to it.
        if (_pending.Count > 0)
        {
            _scratchKeys.Clear();
            foreach (var key in _pending.Keys) _scratchKeys.Add(key);
            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                string key = _scratchKeys[i];
                if (!_pending.TryGetValue(key, out var snap)) continue;
                NodeHandle dest = FindDest(key, snap.Source, out RectF destRect);
                if (!dest.IsNull)
                {
                    BeginFly(key, in snap, destRect, _scene.Paint(dest).Corners, dest);
                    _pending.Remove(key);                       // claimed: the pin transfers to the flight
                }
                else if (++snap.Age > MaxPendingAge)
                {
                    if (MorphLog) System.Console.Error.WriteLine($"[morph] EXPIRE key={key} (no dest found in {MaxPendingAge} frames)");
                    _images.Unpin(new ImageHandle(snap.ImageId));   // no dest laid out in time → release the pin, drop the snapshot
                    _pending.Remove(key);
                }
                else _pending[key] = snap;                       // dest not yet laid out on the live route — wait a frame
            }
        }

        // Keep every live node tagged with an in-flight key hidden so the flying overlay is the only copy on screen —
        // re-applied each frame so a real cover mounting mid-fly (the skeleton→art swap) doesn't flash under the overlay.
        // Hide via BOTH opacity (the cross-fade origin) AND the Visible flag: the recorder CULLS the !Visible dest
        // (SceneRecorder.Walk skips it) instead of blending a full-size cover at alpha 0 every fly frame (wasted fill-rate).
        for (int i = 0; i < _flights.Count; i++) { SetTaggedOpacity(_flights[i].Key, 0f, fade: false); SetTaggedVisible(_flights[i].Key, false); }

        // Bound the overlay band to the page content region while a fly is in flight, so a flying cover stays on the page
        // (never sails over the sidebar / window chrome) — RectF.Infinite (the unbounded band) when nothing is flying.
        _scene.OverlayClip = _flights.Count > 0 ? ComputeFlyClip() : RectF.Infinite;

        _freshlyCaptured.Clear();
    }

    // The clip rect for the overlay band: the union of every in-flight dest's OUTERMOST clip ancestor (the page's
    // content card — NOT the inner rail scissor, so the cover still clears the rail) in window DIP. Infinite when no
    // dest carries a clip ancestor, degrading to the historical unbounded band. Only called while flights exist.
    private RectF ComputeFlyClip()
    {
        RectF clip = default; bool any = false;
        for (int i = 0; i < _flights.Count; i++)
        {
            if (!_scene.IsLive(_flights[i].Dest)) continue;   // dest freed mid-fly: skip (mirrors DestReady's IsLive guard)
            NodeHandle c = OutermostClipAncestor(_flights[i].Dest);
            if (c.IsNull || !_scene.IsLive(c)) continue;
            RectF r = _scene.AbsoluteRect(c);
            if (r.W <= 0f || r.H <= 0f) continue;
            clip = any ? UnionRect(clip, r) : r;
            any = true;
        }
        return any ? clip : RectF.Infinite;
    }

    // Walk dest→root and keep the HIGHEST (closest to root) ancestor that clips its children — the page content card,
    // above the rail's scroll scissor — so the fly is bounded to the page yet never cut off by the narrow rail.
    private NodeHandle OutermostClipAncestor(NodeHandle node)
    {
        NodeHandle best = default;
        for (var n = node; !n.IsNull && n != _scene.Root; n = _scene.Parent(n))
            if ((_scene.Flags(n) & NodeFlags.ClipsToBounds) != 0) best = n;
        return best;
    }

    private static RectF UnionRect(RectF a, RectF b)
    {
        float x = MathF.Min(a.X, b.X), y = MathF.Min(a.Y, b.Y);
        float r = MathF.Max(a.X + a.W, b.X + b.W), bot = MathF.Max(a.Y + a.H, b.Y + b.H);
        return new RectF(x, y, r - x, bot - y);
    }

    // The live dest for a key: a tagged node other than the (now-gone) source, that is ATTACHED TO THE LIVE TREE (not a
    // KeepAlive-parked cached page — whose detached AbsoluteRect would be ~origin) and actually LAID OUT (non-degenerate
    // rect). Prefers a node carrying an image; falls back to a bare placeholder box (the detail skeleton's reserved cover
    // slot). Returns Null (caller waits a frame) until such a dest exists — fixes "flew to (0,0)" and "first nav no-show".
    private NodeHandle FindDest(string key, NodeHandle source, out RectF rect)
    {
        rect = default;
        NodeHandle fallback = default; RectF fallbackRect = default;
        foreach (var kv in _tagged)
        {
            if (kv.Value.Key != key || kv.Key == source || !_scene.IsLive(kv.Key) || !OnLiveTree(kv.Key)) continue;
            RectF r = _scene.AbsoluteRect(kv.Key);
            if (r.W <= 1f || r.H <= 1f) continue;   // tagged but not yet arranged on the live route → wait
            if (_scene.Paint(kv.Key).ImageId != 0) { rect = r; return kv.Key; }   // prefer a real-image cover
            if (fallback.IsNull) { fallback = kv.Key; fallbackRect = r; }
        }
        rect = fallbackRect;
        return fallback;
    }

    // Is the node attached to the live scene root (vs. a KeepAlive-parked / detached subtree)? Few candidates per frame.
    private bool OnLiveTree(NodeHandle node)
    {
        for (var n = node; !n.IsNull; n = _scene.Parent(n)) if (n == _scene.Root) return true;
        return false;
    }

    // Morph the overlay's corner rounding from the source's to the dest's over the fly — a circular artist cover squares
    // into a card slot. A no-op when both are the same radius (albums). Progress tracks the scale spring (source → identity).
    private void MorphCorners(in Flight f)
    {
        if (!_scene.IsLive(f.Overlay)) return;
        ref NodePaint p = ref _scene.Paint(f.Overlay);
        float span = 1f - f.Sx0;
        float prog = MathF.Abs(span) < 0.001f ? 1f : Math.Clamp((p.LocalTransform.M11 - f.Sx0) / span, 0f, 1f);
        p.Corners = LerpCorners(f.SrcCorners, f.DestCorners, prog);
    }

    private static CornerRadius4 LerpCorners(CornerRadius4 a, CornerRadius4 b, float t) => new(
        a.TopLeft + (b.TopLeft - a.TopLeft) * t,
        a.TopRight + (b.TopRight - a.TopRight) * t,
        a.BottomRight + (b.BottomRight - a.BottomRight) * t,
        a.BottomLeft + (b.BottomLeft - a.BottomLeft) * t);

    private void BeginFly(string key, in Snapshot snap, RectF dest, CornerRadius4 destCorners, NodeHandle destNode)
    {
        if (dest.W <= 0f || dest.H <= 0f) { _images.Unpin(new ImageHandle(snap.ImageId)); return; }
        if (MorphLog) System.Console.Error.WriteLine($"[morph] Fly key={key} src=({snap.Rect.X:0},{snap.Rect.Y:0},{snap.Rect.W:0},{snap.Rect.H:0}) dest=({dest.X:0},{dest.Y:0},{dest.W:0},{dest.H:0})");

        RetireFlightsForKey(key);   // velocity-handoff lite: a rapid re-nav to the same key retires the old overlay so flies never stack

        var ov = _scene.CreateNode(8 /* ImageEl */);
        ref NodePaint p = ref _scene.Paint(ov);
        p = NodePaint.Default;
        p.VisualKind = VisualKind.Image;
        p.ImageId = snap.ImageId;
        p.ImageFit = snap.Fit;
        p.Corners = snap.Corners;
        ref RectF b = ref _scene.Bounds(ov);
        b = dest;   // the overlay's model box IS the dest rect; the seed transform places it at the source initially
        _scene.Flags(ov) = (_scene.Flags(ov) & ~NodeFlags.HitTestVisible) | NodeFlags.Visible;   // never intercept input

        // FLIP seed: translate the centre from dest→source and scale by the size ratio, so frame 0 already draws at the
        // source rect (no flash); springs drive both back to identity (== exactly covering the dest rect). Scale is about
        // the node centre (NodePaint.Origin 0.5,0.5), matching the recorder's transform-origin composition.
        float sx = snap.Rect.W / dest.W, sy = snap.Rect.H / dest.H;
        float tx = (snap.Rect.X + snap.Rect.W * 0.5f) - (dest.X + dest.W * 0.5f);
        float ty = (snap.Rect.Y + snap.Rect.H * 0.5f) - (dest.Y + dest.H * 0.5f);
        p.LocalTransform = Affine2D.Translation(tx, ty).Multiply(Affine2D.Scale(sx, sy));

        _scene.AddOverlay(ov);
        // Materialize-in: a short opacity fade so the cover eases into view rather than hard-popping at the source rect.
        // The source card has just unmounted on a forward nav (and the dest is hidden under us), so there's no double
        // image — only the overlay carries the art for this fade. Starts at a small floor (not 0) so the cover is never
        // a fully-transparent frame even if a tick were skipped, and keeps the shared element more continuous. Independent
        // of the translate/scale fly + the corner morph.
        _anim.Animate(ov, AnimChannel.Opacity, 0.12f, 1f, FadeInMs, Easing.SmoothOut);
        var motion = FlyMotion;
        if (motion.IsSpring)
        {
            _anim.Spring(ov, AnimChannel.ScaleX, 1f, motion.Spring, initial: sx);
            _anim.Spring(ov, AnimChannel.ScaleY, 1f, motion.Spring, initial: sy);
            _anim.Spring(ov, AnimChannel.TranslateX, 0f, motion.Spring, initial: tx);
            _anim.Spring(ov, AnimChannel.TranslateY, 0f, motion.Spring, initial: ty);
        }
        else   // eased — a custom EasingSpec (named or cubic-bézier) over a fixed duration
        {
            float dur = motion.DurationMs;
            _anim.Animate(ov, AnimChannel.ScaleX, sx, 1f, dur, motion.Easing);
            _anim.Animate(ov, AnimChannel.ScaleY, sy, 1f, dur, motion.Easing);
            _anim.Animate(ov, AnimChannel.TranslateX, tx, 0f, dur, motion.Easing);
            _anim.Animate(ov, AnimChannel.TranslateY, ty, 0f, dur, motion.Easing);
        }

        SetTaggedOpacity(key, 0f, fade: false); SetTaggedVisible(key, false);   // hide the real dest under the flying overlay (record-culled; Tick65 re-applies each frame)
        _flights.Add(new Flight { Overlay = ov, Dest = destNode, Key = key, ImageId = snap.ImageId, SrcCorners = snap.Corners, DestCorners = destCorners, Sx0 = sx, DestRect = dest });
    }

    private void RetireFlightsForKey(string key)
    {
        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            if (_flights[i].Key != key) continue;
            var f = _flights[i];
            _images.Unpin(new ImageHandle(f.ImageId));
            if (_scene.IsLive(f.Overlay)) { _scene.RemoveOverlay(f.Overlay); _scene.FreeSubtree(f.Overlay); }
            _flights.RemoveAt(i);
        }
        SetTaggedVisible(key, true);   // a retired fly (rapid re-nav) must never leave the dest record-culled (invisible)
    }

    // ── phase 7 (after the anim tick): retire landed flies ───────────────────────────────────────
    /// <summary>Retire any fly whose springs have settled (or whose overlay was freed): reveal the real dest with a
    /// short cross-fade, release the image pin, and free the overlay node.</summary>
    public void Settle()
    {
        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];
            f.Age++;
            // The page SCROLLED out from under an in-flight fly (its dest moved from the seeded target): the overlay lives
            // in the fixed window-space band, so it would float at the stale spot. Retire NOW and SNAP the dest in at its
            // CURRENT position — an interrupted connected-anim lands on the real target, never chases a ghost.
            if (DestMoved(in f))
            {
                SetTaggedVisible(f.Key, true); SetTaggedOpacity(f.Key, 1f, fade: false);
                _images.Unpin(new ImageHandle(f.ImageId));
                if (_scene.IsLive(f.Overlay)) { _scene.RemoveOverlay(f.Overlay); _scene.FreeSubtree(f.Overlay); }
                _flights.RemoveAt(i);
                continue;
            }
            bool overlayDead = !_scene.IsLive(f.Overlay);
            bool wedged = f.Age >= MaxFlightFrames;   // defensive backstop so an overlay can never get stuck
            // Settled = within ~0.6px / 0.6% of identity AND no longer moving frame-to-frame. The "not moving" test is what
            // lets a SPRINGY (overshoot) motion play out fully — it keeps moving through the identity crossing until it damps
            // — while still retiring at the visual rest, not the spring's tiny RestEps tail (~1s later).
            bool settled = false;
            if (!overlayDead)
            {
                var t = _scene.Paint(f.Overlay).LocalTransform;
                bool moving = !f.HasLast || MathF.Abs(t.Dx - f.LastDx) > 0.25f || MathF.Abs(t.M11 - f.LastM11) > 0.0025f;
                f.LastDx = t.Dx; f.LastM11 = t.M11; f.HasLast = true;
                bool nearId = MathF.Abs(t.Dx) < 0.6f && MathF.Abs(t.Dy) < 0.6f && MathF.Abs(t.M11 - 1f) < 0.006f && MathF.Abs(t.M22 - 1f) < 0.006f;
                settled = nearId && !moving;
            }
            bool flying = !overlayDead && _anim.HasTracks(f.Overlay) && !settled && !wedged;
            if (flying)
            {
                MorphCorners(in f);   // morph the rounding source→dest over the fly (circle→square for an artist cover)
                _flights[i] = f; continue;
            }
            // LANDED — but HOLD the overlay (which carries the already-decoded source art) over the dest until the dest's
            // OWN image has decoded + revealed, so the hand-off is seamless (no music-note placeholder flash on reveal).
            if (!overlayDead && !wedged && !DestReady(f.Dest)) { _flights[i] = f; continue; }
            // Restore visibility + SNAP the real dest to opaque (no fade): it is the SAME cover at the SAME rect as the
            // landed overlay, so an instant reveal is seamless — whereas a fade sets opacity=1 then (since _anim.Tick
            // already ran this frame, before Settle) restarts it from 0 next frame, the 1-2 frame full→dim→up flicker.
            SetTaggedVisible(f.Key, true); SetTaggedOpacity(f.Key, 1f, fade: false);
            _images.Unpin(new ImageHandle(f.ImageId));
            if (_scene.IsLive(f.Overlay)) { _scene.RemoveOverlay(f.Overlay); _scene.FreeSubtree(f.Overlay); }
            _flights.RemoveAt(i);
        }
        if (_flights.Count == 0) _scene.OverlayClip = RectF.Infinite;   // last fly retired — release the band clip
    }

    // The dest's own cover image has decoded AND its placeholder→image cross-fade has fully revealed — so revealing it
    // under the retiring overlay shows the finished art, never the music-note placeholder. A bare placeholder dest
    // (skeleton box, no image) is "ready" immediately.
    private bool DestReady(NodeHandle dest)
    {
        if (!_scene.IsLive(dest)) return true;
        int img = _scene.Paint(dest).ImageId;
        if (img == 0) return true;
        var h = new ImageHandle(img);
        return _images.StateOf(h) == ImageState.Ready && _images.CrossFadeOf(h) >= 0.99f;
    }

    // True once the dest has MOVED appreciably from the rect the overlay was seeded to chase (the page SCROLLED while the
    // fly is in flight). The overlay band is fixed window-space, so a moved dest means the overlay is heading to a ghost
    // position — the caller retires the fly and snaps the dest in where it ACTUALLY is now.
    private bool DestMoved(in Flight f)
    {
        if (!_scene.IsLive(f.Dest) || !OnLiveTree(f.Dest)) return false;
        RectF cur = _scene.AbsoluteRect(f.Dest);
        return MathF.Abs(cur.X - f.DestRect.X) > 8f || MathF.Abs(cur.Y - f.DestRect.Y) > 8f;
    }

    // Set the opacity of every live node tagged with `key` (the real cover, the skeleton slot, the parked source). Used
    // to hide the dest under the flying overlay and to cross-fade it back in on landing.
    private void SetTaggedOpacity(string key, float op, bool fade)
    {
        foreach (var kv in _tagged)
        {
            if (kv.Value.Key != key || !_scene.IsLive(kv.Key)) continue;
            _scene.Paint(kv.Key).Opacity = op;
            _scene.Mark(kv.Key, NodeFlags.PaintDirty);
            if (fade && op > 0f) _anim.Animate(kv.Key, AnimChannel.Opacity, 0f, op, RevealMs, Easing.FluentDecelerate);
        }
    }

    // Toggle the RECORD-visibility of every live node tagged with `key`: clear NodeFlags.Visible during a fly so the
    // SceneRecorder skips the hidden dest entirely (it culls !Visible at the top of Walk) — cheaper than recording +
    // blending a full-size cover at alpha 0 each frame — and restore it on reveal/retire. Layout, decode and crossfade
    // progress are unaffected (none gate on Visible), so the fly target rect + the seamless hand-off are unchanged.
    private void SetTaggedVisible(string key, bool visible)
    {
        foreach (var kv in _tagged)
        {
            if (kv.Value.Key != key || !_scene.IsLive(kv.Key)) continue;
            if (visible) _scene.Flags(kv.Key) |= NodeFlags.Visible; else _scene.Flags(kv.Key) &= ~NodeFlags.Visible;
            _scene.Mark(kv.Key, NodeFlags.PaintDirty);
        }
    }
}
