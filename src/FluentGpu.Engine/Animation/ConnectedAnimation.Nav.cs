using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>How a structure's connected navigation flight moves (<see cref="ConnectedAnimation.BeginNav"/>): a critically damped
/// spring whose response grows with the distance the cover travels, <c>clamp(ResponseMin + ResponsePerDip * d, ResponseMin,
/// ResponseMax)</c> seconds for a centre-to-centre distance <c>d</c> in DIP (90 % of the way at 0.62 x the response, 99 % at
/// 1.06 x), times <paramref name="BackScale"/> for Back. A source or a destination under <paramref name="MinVisible"/> of its
/// area on screen does not fly. <paramref name="PendingMs"/> is how long the flyer may hold at the source for its destination
/// to mount (0: it must mount on the click frame, or nothing flies). An abandoned flyer fades out over
/// <paramref name="AbandonFadeMs"/> where it is; a landed flyer whose destination is not ready hands off anyway after
/// <paramref name="HandOffWaitMs"/>.</summary>
public readonly record struct ConnectedNavSpec(
    float ResponseMin, float ResponseMax, float ResponsePerDip, float BackScale,
    float MinVisible, float PendingMs, float AbandonFadeMs, float HandOffWaitMs)
{
    /// <summary>The spring response (seconds) for a flight of <paramref name="distanceDip"/> centre to centre.</summary>
    public float ResponseFor(float distanceDip, bool back)
    {
        float r = Math.Clamp(ResponseMin + ResponsePerDip * distanceDip, ResponseMin, ResponseMax);
        return back ? r * BackScale : r;
    }
}

/// <summary>One route commit, told to the connected navigation flight before the route is written. <paramref name="Key"/> is the
/// shared-element key of the page being opened (forward) or left (Back), empty when the route has none: every commit calls,
/// so a running flight to another page is abandoned.</summary>
public readonly record struct ConnectedNavRequest(string Key, bool Back, ConnectedNavSpec Spec);

/// <summary>Where a connected navigation flight stands (the probe, <see cref="ConnectedAnimation.TryGetNavFlight"/>).</summary>
public enum ConnectedNavPhase : byte { Pending, Flying, Landed, Abandoned }

/// <summary>A read-only view of one connected navigation flight, for the inspector's trace. Rects are window DIP: the source's rect
/// at take-off, the destination's rest rect (the flyer's model box), the flyer's presented rect this frame, and the rect the first
/// frame that drew the flyer posed it at (<c>TakeOff</c>: equal to <c>Source</c> when nothing jumped on the click frame).</summary>
public readonly record struct ConnectedNavFlightInfo(
    string Key, ConnectedNavPhase Phase, bool Back, RectF Source, RectF Dest, RectF Presented, float ShadowOpacity, int ImageId,
    bool SourceHidden, bool DestHidden, RectF TakeOff);

// ── THE NAVIGATION FLIGHT ────────────────────────────────────────────────────────────────────────────────────────────────
//
// A card's cover flies into the page it opens (and back). Beside the legacy key-wide flight above, which the Stage uses, and
// built for navigation:
//   · the source is the tagged image INSIDE the element the user activated (InputDispatcher.ActivatingNode), never "any node
//     with the key": a key repeats down a page;
//   · the flyer is posed by its RECT, not a scale: its model box is the destination's rest rect, TranslateX/Y and SizeW/H
//     springs (presented size) carry it from the source's rect, and the recorder cover-fits the image into the presented rect
//     every frame, so the corner radius and the shadow stay true DIP for the whole flight;
//   · a critically damped spring whose response scales with the distance: a turn-around (Back mid-flight) retargets the same
//     springs and keeps their velocity;
//   · the slot roots of both ends (the image and the same-sized boxes round it: the card's cover stack with its play button,
//     the hero's shadowed cover box) are hidden while it flies, so the flyer is the only copy;
//   · the hand-off is one frame: the flyer is exactly its model rect, the destination is at rest at that rect, opaque, and its
//     image is drawable. Then the real node shows and the flyer is freed in the same publication.
// Per frame (UI thread) while a flight is up: two hide flags, one rest-rect walk, one image-state poll. Nothing when none is.

public sealed partial class ConnectedAnimation
{
    private sealed class NavFlight
    {
        public string Key = "";
        public bool Back, Turning;
        public ConnectedNavSpec Spec;
        public ConnectedNavPhase Phase;
        public NodeHandle Source, SourceSlot, Dest, DestSlot;
        public bool SourceHid, DestHid;
        // A turn-around's first source (the card a Back flies home to): still hidden from the forward leg until the dest is found.
        public NodeHandle PrevSlot;
        public bool PrevHid;
        public RectF TakeOff;
        public bool TookOff;
        public RectF SourceRect, Model;
        public NodeHandle Image, Shadow;
        public int ImageId;
        public SpringParams Spring;
        public float ShadowTarget;                              // the shadow's opacity at the dest (1: the hero's, 0: a card's)
        public double LandedAtMs, AbandonEndMs, PendingSinceMs;
        public bool Presented;                                  // a frame has drawn the flyer (an abandon fades; else it frees at once)
        // The source's ancestor chain at take-off (nearest first): the dest is the candidate whose chain meets it nearest the root
        // (another page, not a sibling card on the source's own page), and the overlay band clips to the first clipping box of the
        // dest's chain that is on it (the page box: the flyer never crosses the nav pane, the title bar or the player bar).
        public readonly NodeHandle[] Chain = new NodeHandle[MaxChain];
        public int ChainCount;
        public NodeHandle ClipNode;
    }

    private const int MaxChain = 96;
    private const int SlotClimb = 3;                            // image → the same-sized boxes round it, at most this many levels
    private const float SlotSameSize = 0.5f;
    private const float RetargetEps = 0.5f;                     // a rest rect that moved by more than this re-aims the springs
    private const float HandOffEps = 0.25f;

    private readonly List<NavFlight> _nav = new();
    private readonly Stack<NavFlight> _navPool = new();
    // The card a forward flight left from, per key: Back flies to it when it is still the live node under that key.
    private readonly Dictionary<string, NodeHandle> _navReturn = new(StringComparer.Ordinal);

    /// <summary>The element the user just activated (the host hands the input dispatcher's <c>ActivatingNode</c>, else the focused
    /// node): a forward flight's source is the tagged image inside it.</summary>
    internal Func<NodeHandle>? NavOrigin { get; set; }

    /// <summary>Live navigation flights (pending, flying, landed or fading out after an abandon).</summary>
    public int NavFlightCount => _nav.Count;

    /// <summary>The probe: a read-only view of navigation flight <paramref name="i"/> (allocation-free).</summary>
    public bool TryGetNavFlight(int i, out ConnectedNavFlightInfo info)
    {
        if ((uint)i >= (uint)_nav.Count) { info = default; return false; }
        NavFlight f = _nav[i];
        RectF pres = f.SourceRect;
        if (_scene.IsLive(f.Image)) pres = FlyerRect(f.Image);
        float shadow = _scene.IsLive(f.Shadow) ? _scene.Paint(f.Shadow).Opacity : 0f;
        info = new ConnectedNavFlightInfo(f.Key, f.Phase, f.Back, f.SourceRect, f.Model, pres, shadow, f.ImageId, f.SourceHid, f.DestHid,
            f.TookOff ? f.TakeOff : pres);
        return true;
    }

    // ── the entry ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A route commit, before the route is written (every one: an empty key abandons a running flight). Abandons every
    /// navigation flight to another key; turns a flight with this key around (Back mid-flight: the same flyer, its springs keep
    /// their velocity); else captures the source (forward: the tagged image inside the activated element; Back: the tagged image
    /// with the largest visible area, the hero of the page being left), puts the flyer up at its rect and hides the source's
    /// slot. True when a flyer is up: the shell then plays the quiet page swap under it.</summary>
    public bool BeginNav(in ConnectedNavRequest r)
    {
        string key = r.Key ?? "";
        for (int i = _nav.Count - 1; i >= 0; i--)
            if (_nav[i].Phase != ConnectedNavPhase.Abandoned && !string.Equals(_nav[i].Key, key, StringComparison.Ordinal))
                AbandonNav(_nav[i]);
        if (ReducedMotion || key.Length == 0) return false;

        for (int i = 0; i < _nav.Count; i++)
        {
            NavFlight f = _nav[i];
            if (f.Phase is ConnectedNavPhase.Flying or ConnectedNavPhase.Landed && string.Equals(f.Key, key, StringComparison.Ordinal))
            {
                TurnNav(f, in r);
                return true;
            }
            if (f.Phase == ConnectedNavPhase.Pending && string.Equals(f.Key, key, StringComparison.Ordinal)) AbandonNav(f);
        }

        NodeHandle origin = r.Back ? NodeHandle.Null : NavOrigin?.Invoke() ?? NodeHandle.Null;
        if (!r.Back && (origin.IsNull || !_scene.IsLive(origin))) return false;
        NodeHandle source = NodeHandle.Null;
        float bestArea = -1f, bestFraction = 0f;
        foreach (var kv in _tagged)
        {
            if (!string.Equals(kv.Value, key, StringComparison.Ordinal) || !_scene.IsLive(kv.Key)) continue;
            if (_scene.Paint(kv.Key).ImageId == 0 || !OnLiveTree(kv.Key)) continue;
            if (!r.Back && !IsSelfOrDescendant(kv.Key, origin)) continue;
            float area = VisibleArea(kv.Key, out float fraction);
            if (area > bestArea) { bestArea = area; bestFraction = fraction; source = kv.Key; }
        }
        if (source.IsNull || bestFraction < r.Spec.MinVisible) return false;

        NavFlight n = _navPool.Count > 0 ? _navPool.Pop() : new NavFlight();
        ResetNav(n);
        n.Key = key; n.Back = r.Back; n.Spec = r.Spec; n.Phase = ConnectedNavPhase.Pending;
        n.Source = source;
        n.SourceSlot = SlotRoot(source);
        n.SourceRect = _scene.AbsoluteRect(source);
        n.PendingSinceMs = _anim.ClockNowMs;
        CaptureChain(n, source);
        ref NodePaint sp = ref _scene.Paint(source);
        n.ImageId = sp.ImageId;
        _images.Pin(new ImageHandle(n.ImageId));

        if (SlotShadow(source, out ShadowSpec shadow)) n.Shadow = CreateShadowNode(in shadow, sp.Corners, n.SourceRect, 1f);
        var img = _scene.CreateNode(8 /* ImageEl */);
        ref NodePaint p = ref _scene.Paint(img);
        p = NodePaint.Default;
        p.VisualKind = VisualKind.Image;
        p.ImageId = n.ImageId;
        p.ImageFit = sp.ImageFit;
        p.Corners = sp.Corners;
        _scene.Bounds(img) = n.SourceRect;
        _scene.SetFlagsRaw(img, (_scene.Flags(img) & ~NodeFlags.HitTestVisible) | NodeFlags.Visible);
        _scene.AddOverlay(img);
        n.Image = img;
        n.SourceHid = Hide(n.SourceSlot);
        _nav.Add(n);
        if (MorphLog) Console.Error.WriteLine($"[morph] nav begin key={key} back={r.Back} src=({n.SourceRect.X:0},{n.SourceRect.Y:0},{n.SourceRect.W:0},{n.SourceRect.H:0})");
        return true;
    }

    // Back (or Forward) to where a flight came from: the same flyer, re-aimed once the page it returns to is laid out.
    private void TurnNav(NavFlight f, in ConnectedNavRequest r)
    {
        // The end it flew to becomes the source: it stays hidden (the flyer is still the only copy). The end it came from stays hidden
        // too, until the dest is found: it is that end, normally (ArriveNav takes its hide over), else it is shown then.
        if (f.PrevHid && _scene.IsLive(f.PrevSlot) && f.PrevSlot != f.SourceSlot) Show(f.PrevSlot);
        f.PrevSlot = f.SourceSlot; f.PrevHid = f.SourceHid;
        f.Source = f.Dest; f.SourceSlot = f.DestSlot; f.SourceHid = f.DestHid;
        f.Dest = NodeHandle.Null; f.DestSlot = NodeHandle.Null; f.DestHid = false;
        f.SourceRect = _scene.IsLive(f.Image) ? FlyerRect(f.Image) : f.Model;
        f.Back = r.Back; f.Spec = r.Spec; f.Turning = true;
        f.TakeOff = f.SourceRect; f.TookOff = true;   // a turn takes off from where the flyer stands (its springs keep their velocity)
        f.Phase = ConnectedNavPhase.Pending; f.PendingSinceMs = _anim.ClockNowMs; f.LandedAtMs = double.NaN;
        if (_scene.IsLive(f.Source)) CaptureChain(f, f.Source);
    }

    // ── phase 6.5 (after layout): find the dest, seed or re-aim the springs, keep both slots hidden, clip the band ──────────

    private void TickNav()
    {
        if (_nav.Count == 0) return;
        double now = _anim.ClockNowMs;
        RectF clip = default;
        bool anyClip = false;
        for (int i = _nav.Count - 1; i >= 0; i--)
        {
            NavFlight f = _nav[i];
            if (f.Phase == ConnectedNavPhase.Abandoned) continue;
            if (ReducedMotion || !_scene.IsLive(f.Image)) { AbandonNav(f, fade: false); continue; }
            if (f.Phase == ConnectedNavPhase.Pending)
            {
                NodeHandle dest = FindNavDest(f);
                if (!dest.IsNull && VisibleArea(dest, out float fraction) > 0f && fraction >= f.Spec.MinVisible)
                    ArriveNav(f, dest);
                else if (f.Turning || !f.Presented && f.Spec.PendingMs <= 0f || now - f.PendingSinceMs >= f.Spec.PendingMs)
                {
                    AbandonNav(f);
                    continue;
                }
            }
            else
            {
                if (!_scene.IsLive(f.Dest) || !OnLiveTree(f.Dest)) { AbandonNav(f); continue; }
                RectF rest = RestRect(f.Dest, out _);
                if (RectDiffers(in rest, in f.Model, RetargetEps)) Reaim(f, rest, keepResponse: true);
                SwapToDestImage(f);
            }
            // Re-applied every frame: a re-render of either end (a bound Visible, a hover) must never show it under the flyer.
            if (_scene.IsLive(f.SourceSlot)) f.SourceHid |= Hide(f.SourceSlot);
            if (_scene.IsLive(f.DestSlot)) f.DestHid |= Hide(f.DestSlot);
            if (!f.ClipNode.IsNull && _scene.IsLive(f.ClipNode))
            {
                RectF c = _scene.AbsoluteRect(f.ClipNode);
                clip = anyClip ? UnionRect(clip, c) : c;
                anyClip = true;
            }
        }
        if (anyClip) _scene.OverlayClip = _flights.Count > 0 && !_scene.OverlayClip.IsInfinite ? UnionRect(_scene.OverlayClip, clip) : clip;
    }

    // The dest found (or found again after a turn): aim the springs at its rest rect, hide its slot, clip the band to the page.
    private void ArriveNav(NavFlight f, NodeHandle dest)
    {
        f.Dest = dest;
        f.DestSlot = SlotRoot(dest);
        f.ClipNode = SharedClip(f, dest);
        RectF rest = RestRect(dest, out _);
        bool destShadow = SlotShadow(dest, out ShadowSpec dShadow);
        if (destShadow && f.Shadow.IsNull)
        {
            // The shadow draws under the image: the band draws in insertion order, so the image goes back on top.
            f.Shadow = CreateShadowNode(in dShadow, _scene.Paint(f.Image).Corners, FlyerRect(f.Image), 0f);
            _scene.RemoveOverlay(f.Image);
            _scene.AddOverlay(f.Image);
        }
        else if (destShadow) _scene.SetShadow(f.Shadow, in dShadow);
        f.ShadowTarget = destShadow ? 1f : 0f;
        if (f.Turning) Reaim(f, rest, keepResponse: false);
        else Seed(f, rest);
        f.DestHid = Hide(f.DestSlot);
        if (f.PrevHid)
        {
            if (f.PrevSlot == f.DestSlot) f.DestHid = true;   // the card the flight left from: hidden since take-off, shown at the hand-off
            else if (_scene.IsLive(f.PrevSlot)) Show(f.PrevSlot);
            f.PrevSlot = NodeHandle.Null; f.PrevHid = false;
        }
        if (!f.Back && !f.Source.IsNull) _navReturn[f.Key] = f.Source;
        f.Phase = ConnectedNavPhase.Flying;
        f.Turning = false;
        if (MorphLog) Console.Error.WriteLine($"[morph] nav fly key={f.Key} back={f.Back} dest=({rest.X:0},{rest.Y:0},{rest.W:0},{rest.H:0})");
    }

    // First seed: the model box is the dest's rest rect; the rows start at the source's rect and rest at the model box. The rows
    // are pending-start, so the mount frame shows t = 0 at exactly the source's rect and the clock starts when it is presented.
    private void Seed(NavFlight f, RectF rest)
    {
        RectF from = f.SourceRect;
        f.Model = rest;
        f.Spring = SpringParams.FromResponse(f.Spec.ResponseFor(CentreDistance(in from, in rest), f.Back), 1f);
        SeedNode(f.Image, in from, in rest, f.Spring);
        if (!f.Shadow.IsNull)
        {
            SeedNode(f.Shadow, in from, in rest, f.Spring);
            float o = _scene.Paint(f.Shadow).Opacity;
            if (MathF.Abs(o - f.ShadowTarget) > 0.001f) _anim.Spring(f.Shadow, AnimChannel.Opacity, f.ShadowTarget, f.Spring, initial: o);
            _anim.HoldStartUntilPresented(f.Shadow);
        }
        _anim.HoldStartUntilPresented(f.Image);
    }

    private void SeedNode(NodeHandle node, in RectF from, in RectF rest, in SpringParams spring)
    {
        _scene.Bounds(node) = rest;
        float tx = from.X - rest.X, ty = from.Y - rest.Y;
        ref NodePaint p = ref _scene.Paint(node);
        p.LocalTransform = Affine2D.Translation(tx, ty);
        p.PresentedW = from.W; p.PresentedH = from.H;
        _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        _anim.Spring(node, AnimChannel.TranslateX, 0f, spring, initial: tx);
        _anim.Spring(node, AnimChannel.TranslateY, 0f, spring, initial: ty);
        _anim.Spring(node, AnimChannel.SizeW, rest.W, spring, initial: from.W);
        _anim.Spring(node, AnimChannel.SizeH, rest.H, spring, initial: from.H);
    }

    // Re-aim the flyer at a new model rect where it stands: the box moves to the new rect, the translate rows are re-based so
    // this frame's pixels do not move (their velocity carries), the size rows are absolute and just retarget. A turn takes
    // the response for the distance left; a dest that moved under the flight keeps the flight's own.
    private void Reaim(NavFlight f, RectF rest, bool keepResponse)
    {
        if (!keepResponse)
        {
            RectF at = FlyerRect(f.Image);
            f.Spring = SpringParams.FromResponse(f.Spec.ResponseFor(CentreDistance(in at, in rest), f.Back), 1f);
        }
        ReaimNode(f.Image, rest, f.Spring);
        if (!f.Shadow.IsNull)
        {
            ReaimNode(f.Shadow, rest, f.Spring);
            _anim.Spring(f.Shadow, AnimChannel.Opacity, f.ShadowTarget, f.Spring);
        }
        f.Model = rest;
        f.LandedAtMs = double.NaN;
        if (f.Phase == ConnectedNavPhase.Landed) f.Phase = ConnectedNavPhase.Flying;
    }

    private void ReaimNode(NodeHandle node, RectF rest, in SpringParams spring)
    {
        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint p = ref _scene.Paint(node);
        float shiftX = b.X - rest.X, shiftY = b.Y - rest.Y;
        float tx = p.LocalTransform.Dx + shiftX, ty = p.LocalTransform.Dy + shiftY;
        float w = float.IsNaN(p.PresentedW) ? b.W : p.PresentedW, h = float.IsNaN(p.PresentedH) ? b.H : p.PresentedH;
        b = rest;
        p.LocalTransform = Affine2D.Translation(tx, ty);
        p.PresentedW = w; p.PresentedH = h;
        _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        _anim.RebaseSpring(node, AnimChannel.TranslateX, 0f, spring, tx);
        _anim.RebaseSpring(node, AnimChannel.TranslateY, 0f, spring, ty);
        // Absolute extents: a live row retargets from where it is (velocity kept), a landed one seeds from the presented size.
        _anim.Spring(node, AnimChannel.SizeW, rest.W, spring);
        _anim.Spring(node, AnimChannel.SizeH, rest.H, spring);
    }

    // The flyer starts on the texture the source draws now (the card's 256 decode); once the dest's own rendition is ready (the
    // hero's 512) and differs, it cuts to it: the same picture, the same crop, sharper.
    private void SwapToDestImage(NavFlight f)
    {
        int id = _scene.Paint(f.Dest).ImageId;
        if (id == 0 || id == f.ImageId || _images.StateOf(new ImageHandle(id)) != ImageState.Ready) return;
        _images.Pin(new ImageHandle(id));
        _images.Unpin(new ImageHandle(f.ImageId));
        f.ImageId = id;
        _scene.Paint(f.Image).ImageId = id;
        _scene.Mark(f.Image, NodeFlags.PaintDirty);
    }

    // ── phase 7 (after the anim tick): land, hand off, back-stop, retire ─────────────────────────────────────────────────

    private void SettleNav()
    {
        if (_nav.Count == 0) return;
        double now = _anim.ClockNowMs;
        for (int i = _nav.Count - 1; i >= 0; i--)
        {
            NavFlight f = _nav[i];
            f.Presented = true;   // this frame records it
            if (!f.TookOff && f.Phase == ConnectedNavPhase.Flying && _scene.IsLive(f.Image)) { f.TakeOff = FlyerRect(f.Image); f.TookOff = true; }
            switch (f.Phase)
            {
                case ConnectedNavPhase.Abandoned:
                    if (now >= f.AbandonEndMs) FreeNav(f);
                    break;
                case ConnectedNavPhase.Flying:
                    if (_anim.HasTracks(f.Image) || !f.Shadow.IsNull && _anim.HasTracks(f.Shadow)) break;
                    f.Phase = ConnectedNavPhase.Landed;
                    f.LandedAtMs = now;
                    goto case ConnectedNavPhase.Landed;
                case ConnectedNavPhase.Landed:
                    if (ReadyToHandOff(f) || now - f.LandedAtMs >= f.Spec.HandOffWaitMs)
                    {
                        if (MorphLog) Console.Error.WriteLine($"[morph] nav hand-off key={f.Key} forced={!ReadyToHandOff(f)} after={now - f.LandedAtMs:0}ms");
                        FreeNav(f);
                    }
                    break;
            }
        }
    }

    // The hand-off rule: the flyer is exactly its model rect (its rows are done), the dest is at rest at that rect (no live
    // translate or scale above it, within a quarter DIP), its opacity chain is opaque, and its image is drawable.
    private bool ReadyToHandOff(NavFlight f)
    {
        if (!_scene.IsLive(f.Dest)) return true;
        RectF rest = RestRect(f.Dest, out bool moving);
        if (moving) return false;
        ref RectF b = ref _scene.Bounds(f.Image);
        if (RectDiffers(in rest, in b, HandOffEps)) return false;
        float opacity = 1f;
        for (NodeHandle n = f.Dest; !n.IsNull; n = _scene.Parent(n))
        {
            if (n == f.DestSlot) continue;   // hidden by us: its own opacity is the page's
            opacity *= _scene.Paint(n).Opacity;
        }
        if (opacity < 0.99f) return false;
        int id = _scene.Paint(f.Dest).ImageId;
        if (id == 0) return true;
        var h = new ImageHandle(id);
        return _images.StateOf(h) == ImageState.Ready && _images.CrossFadeOf(h) >= 0.99f;
    }

    // An abandon reveals both ends at once. A flyer a frame has drawn fades out where it stands (its geometry rows held: the
    // pixel on screen, on the render thread too); one no frame drew is freed now.
    private void AbandonNav(NavFlight f, bool fade = true)
    {
        Reveal(f);
        if (!fade || !f.Presented || !_scene.IsLive(f.Image) || f.Spec.AbandonFadeMs <= 0f) { FreeNav(f); return; }
        HoldGeometry(f.Image);
        float o = _scene.Paint(f.Image).Opacity;
        _anim.SeedEased(f.Image, AnimChannel.Opacity, o, 0f, f.Spec.AbandonFadeMs, Easing.Linear);
        if (_scene.IsLive(f.Shadow))
        {
            HoldGeometry(f.Shadow);
            _anim.SeedEased(f.Shadow, AnimChannel.Opacity, _scene.Paint(f.Shadow).Opacity, 0f, f.Spec.AbandonFadeMs, Easing.Linear);
        }
        f.Phase = ConnectedNavPhase.Abandoned;
        f.AbandonEndMs = _anim.ClockNowMs + f.Spec.AbandonFadeMs;
        if (MorphLog) Console.Error.WriteLine($"[morph] nav abandon key={f.Key}");
    }

    private void HoldGeometry(NodeHandle node)
    {
        _anim.SetHeld(node, AnimChannel.TranslateX, true);
        _anim.SetHeld(node, AnimChannel.TranslateY, true);
        _anim.SetHeld(node, AnimChannel.SizeW, true);
        _anim.SetHeld(node, AnimChannel.SizeH, true);
    }

    // Every exit: both ends shown, the pin released, both nodes freed, the flight back in the pool.
    private void FreeNav(NavFlight f)
    {
        Reveal(f);
        if (f.ImageId != 0) _images.Unpin(new ImageHandle(f.ImageId));
        FreeOverlayNode(f.Image);
        FreeOverlayNode(f.Shadow);
        _nav.Remove(f);
        ResetNav(f);
        _navPool.Push(f);
        if (_nav.Count == 0 && _flights.Count == 0) _scene.OverlayClip = RectF.Infinite;
    }

    private void FreeOverlayNode(NodeHandle node)
    {
        if (!_scene.IsLive(node)) return;
        _anim.CancelAll(node);
        _scene.RemoveOverlay(node);
        _scene.FreeSubtree(node);
    }

    private void Reveal(NavFlight f)
    {
        if (f.SourceHid && _scene.IsLive(f.SourceSlot)) Show(f.SourceSlot);
        if (f.DestHid && _scene.IsLive(f.DestSlot)) Show(f.DestSlot);
        if (f.PrevHid && _scene.IsLive(f.PrevSlot)) Show(f.PrevSlot);
        f.SourceHid = false; f.DestHid = false; f.PrevHid = false;
    }

    private static void ResetNav(NavFlight f)
    {
        f.Key = ""; f.Back = false; f.Turning = false; f.Spec = default; f.Phase = ConnectedNavPhase.Pending;
        f.Source = f.SourceSlot = f.Dest = f.DestSlot = f.Image = f.Shadow = f.ClipNode = NodeHandle.Null;
        f.SourceHid = f.DestHid = f.PrevHid = f.TookOff = false; f.PrevSlot = NodeHandle.Null; f.SourceRect = f.Model = f.TakeOff = default; f.ImageId = 0; f.Spring = default;
        f.LandedAtMs = double.NaN; f.AbandonEndMs = 0d; f.PendingSinceMs = 0d; f.Presented = false; f.ShadowTarget = 0f;
        f.ChainCount = 0;
    }

    /// <summary>The slot roots of the navigation flights' two ends: they flip <see cref="NodeFlags.Visible"/>, so their ancestor
    /// chains must not reuse recorded spans while a flight is up.</summary>
    private void CollectNavReuseBlockRoots(List<NodeHandle> into)
    {
        for (int i = 0; i < _nav.Count; i++)
        {
            NavFlight f = _nav[i];
            if (_scene.IsLive(f.SourceSlot)) into.Add(f.SourceSlot);
            if (_scene.IsLive(f.DestSlot)) into.Add(f.DestSlot);
        }
    }

    // ── geometry and tree helpers ────────────────────────────────────────────────────────────────────────────────────────

    // The dest: a tagged node with the flight's key, live, on the live tree, laid out, not the source, whose ancestor chain
    // meets the source's nearest the root (the other page: a second card of the key on the source's own page never wins).
    // Among those, the card a forward flight left from (Back), else the largest visible area.
    private NodeHandle FindNavDest(NavFlight f)
    {
        NodeHandle best = NodeHandle.Null;
        int bestMeet = -1;
        float bestArea = -1f;
        bool bestReturn = false;
        NodeHandle ret = f.Back && _navReturn.TryGetValue(f.Key, out NodeHandle rn) ? rn : NodeHandle.Null;
        foreach (var kv in _tagged)
        {
            NodeHandle n = kv.Key;
            if (!string.Equals(kv.Value, f.Key, StringComparison.Ordinal) || n == f.Source || !_scene.IsLive(n) || !OnLiveTree(n)) continue;
            ref RectF b = ref _scene.Bounds(n);
            if (b.W <= 1f || b.H <= 1f) continue;
            int meet = MeetIndex(f, n);
            bool isReturn = n == ret;
            float area = VisibleArea(n, out _);
            bool better = meet > bestMeet
                || meet == bestMeet && (isReturn && !bestReturn || isReturn == bestReturn && area > bestArea);
            if (!better) continue;
            best = n; bestMeet = meet; bestArea = area; bestReturn = isReturn;
        }
        return best;
    }

    // Where the node's ancestor chain first meets the source's chain, as an index into it (higher = nearer the root); -1 never.
    private int MeetIndex(NavFlight f, NodeHandle node)
    {
        for (NodeHandle n = _scene.Parent(node); !n.IsNull; n = _scene.Parent(n))
            for (int i = 0; i < f.ChainCount; i++)
                if (f.Chain[i] == n) return i;
        return -1;
    }

    // The band's clip: the first clipping box above the dest that is also above the source (the page box).
    private NodeHandle SharedClip(NavFlight f, NodeHandle dest)
    {
        for (NodeHandle n = _scene.Parent(dest); !n.IsNull && n != _scene.Root; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.ClipsToBounds) == 0) continue;
            for (int i = 0; i < f.ChainCount; i++)
                if (f.Chain[i] == n) return n;
        }
        return NodeHandle.Null;
    }

    private void CaptureChain(NavFlight f, NodeHandle node)
    {
        f.ChainCount = 0;
        for (NodeHandle n = _scene.Parent(node); !n.IsNull && f.ChainCount < MaxChain; n = _scene.Parent(n))
            f.Chain[f.ChainCount++] = n;
    }

    private bool IsSelfOrDescendant(NodeHandle node, NodeHandle ancestor)
    {
        for (NodeHandle n = node; !n.IsNull; n = _scene.Parent(n))
            if (n == ancestor) return true;
        return false;
    }

    // The slot root: from the tagged image, up while the parent has the same size (the boxes round the image: the card's cover
    // stack with its play button, the hero's artwork box and its shadowed cover box), at most SlotClimb levels.
    private NodeHandle SlotRoot(NodeHandle image)
    {
        NodeHandle slot = image;
        ref RectF ib = ref _scene.Bounds(image);
        for (int k = 0; k < SlotClimb; k++)
        {
            NodeHandle up = _scene.Parent(slot);
            if (up.IsNull || up == _scene.Root) break;
            ref RectF ub = ref _scene.Bounds(up);
            if (MathF.Abs(ub.W - ib.W) > SlotSameSize || MathF.Abs(ub.H - ib.H) > SlotSameSize) break;
            slot = up;
        }
        return slot;
    }

    // The shadow the slot draws (the hero's cover box carries Elevation.Card; a card carries none).
    private bool SlotShadow(NodeHandle image, out ShadowSpec shadow)
    {
        NodeHandle slot = SlotRoot(image);
        for (NodeHandle n = image; !n.IsNull; n = _scene.Parent(n))
        {
            if (_scene.TryGetShadow(n, out shadow) && !shadow.IsNone) return true;
            if (n == slot) break;
        }
        shadow = default;
        return false;
    }

    private NodeHandle CreateShadowNode(in ShadowSpec shadow, CornerRadius4 corners, RectF at, float opacity)
    {
        var node = _scene.CreateNode(1 /* BoxEl */);
        ref NodePaint p = ref _scene.Paint(node);
        p = NodePaint.Default;
        p.VisualKind = VisualKind.Box;
        p.Corners = corners;
        p.Opacity = opacity;
        _scene.Bounds(node) = at;
        _scene.SetFlagsRaw(node, (_scene.Flags(node) & ~NodeFlags.HitTestVisible) | NodeFlags.Visible);
        _scene.SetShadow(node, in shadow);
        _scene.AddOverlay(node);
        return node;
    }

    // Hide a slot root for the flight (record-culled, hit-test off): true when this call hid it.
    private bool Hide(NodeHandle slot)
    {
        if (slot.IsNull || (_scene.Flags(slot) & NodeFlags.Visible) == 0) return false;
        _scene.ClearFlagBits(slot, NodeFlags.Visible);
        _scene.Mark(slot, NodeFlags.PaintDirty);
        return true;
    }

    private void Show(NodeHandle slot)
    {
        if ((_scene.Flags(slot) & NodeFlags.Visible) != 0) return;
        _scene.SetFlagBits(slot, NodeFlags.Visible);
        _scene.Mark(slot, NodeFlags.PaintDirty);
    }

    // A node's rest rect: its window rect minus what its own and its ancestors' live translate rows still have to travel (a
    // page's entrance slide, a hover lift). `moving` is true while any of them has a live translate or scale row.
    private RectF RestRect(NodeHandle node, out bool moving)
    {
        RectF a = _scene.AbsoluteRect(node);
        float dx = 0f, dy = 0f;
        moving = false;
        for (NodeHandle n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if (!_anim.RestOffset(n, out float ox, out float oy)) continue;
            moving = true; dx += ox; dy += oy;
        }
        return new RectF(a.X - dx, a.Y - dy, a.W, a.H);
    }

    // The flyer's presented rect: its box moved by its translate, at its presented size.
    private RectF FlyerRect(NodeHandle node)
    {
        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint p = ref _scene.Paint(node);
        float w = float.IsNaN(p.PresentedW) ? b.W : p.PresentedW, h = float.IsNaN(p.PresentedH) ? b.H : p.PresentedH;
        return new RectF(b.X + p.LocalTransform.Dx, b.Y + p.LocalTransform.Dy, w, h);
    }

    // The area of the node on screen after every clipping ancestor, and its fraction of the node's own box.
    private float VisibleArea(NodeHandle node, out float fraction)
    {
        RectF v = EffectiveVisibleRect(node);
        ref RectF b = ref _scene.Bounds(node);
        float area = v.IsEmpty || v.IsInfinite ? 0f : v.W * v.H;
        float own = b.W * b.H;
        fraction = own > 0f ? Math.Clamp(area / own, 0f, 1f) : 0f;
        return area;
    }

    private static float CentreDistance(in RectF a, in RectF b)
    {
        float dx = a.X + a.W * 0.5f - (b.X + b.W * 0.5f), dy = a.Y + a.H * 0.5f - (b.Y + b.H * 0.5f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
