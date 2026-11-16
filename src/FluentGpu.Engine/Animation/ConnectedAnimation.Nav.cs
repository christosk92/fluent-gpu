﻿using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>How a structure's connected navigation flight moves (<see cref="ConnectedAnimation.BeginNav"/>): a critically damped
/// spring whose response grows with the distance the picture travels, <c>clamp(ResponseMin + ResponsePerDip * d, ResponseMin,
/// ResponseMax)</c> seconds for a centre-to-centre distance <c>d</c> in DIP (90 % of the way at 0.62 x the response, 99 % at
/// 1.06 x), times <paramref name="BackScale"/> for Back. A source or a destination under <paramref name="MinVisible"/> of its
/// area on screen does not fly. <paramref name="PendingMs"/> is how long the flyer may hold at the source for its destination
/// to mount (0: it must mount on the click frame, or nothing flies). An abandoned flyer fades out over
/// <paramref name="AbandonFadeMs"/> where it is. A landed flyer whose destination's picture is not drawable yet stands in for it
/// for up to <paramref name="HandOffWaitMs"/>, then fades out over <paramref name="AbandonFadeMs"/>. <paramref name="HandOffFadeMs"/>
/// 0: the hand-off is a cut (the real node shows and the flyer goes in one frame); more: the real node shows and the flyer fades
/// out over it (a header whose copy lies on the picture). A source picture whose shorter edge is under
/// <paramref name="MinSourceEdge"/> DIP flies nothing.</summary>
public readonly record struct ConnectedNavSpec(
    float ResponseMin, float ResponseMax, float ResponsePerDip, float BackScale,
    float MinVisible, float PendingMs, float AbandonFadeMs, float HandOffWaitMs,
    float HandOffFadeMs = 0f, float MinSourceEdge = 0f)
{
    /// <summary>The spring response (seconds) for a flight of <paramref name="distanceDip"/> centre to centre.</summary>
    public float ResponseFor(float distanceDip, bool back)
    {
        float r = Math.Clamp(ResponseMin + ResponsePerDip * distanceDip, ResponseMin, ResponseMax);
        return back ? r * BackScale : r;
    }
}

/// <summary>Which picture a forward flight takes off from (<see cref="ConnectedNavRequest.Source"/>).</summary>
public enum ConnectedNavSource : byte
{
    /// <summary>The end tagged with the key inside the element the user activated.</summary>
    Activated,
    /// <summary>The largest picture inside the activated element, tagged or not: a bound row template cannot carry a per-item tag.
    /// The caller asserts that picture is the entity's own.</summary>
    ActivatedAnyPicture,
    /// <summary>The node a forward flight to this key last left from (mouse Forward after a Back), while it is live, on screen and
    /// still shows the same picture.</summary>
    Remembered,
}

/// <summary>One route commit, told to the connected navigation flight before the route is written. <paramref name="Key"/> is the
/// shared-element key of the page being opened (forward) or left (Back), empty when the route has none: every commit calls,
/// so a running flight to another page is abandoned.</summary>
public readonly record struct ConnectedNavRequest(string Key, bool Back, ConnectedNavSpec Spec,
    ConnectedNavSource Source = ConnectedNavSource.Activated);

/// <summary>Where a connected navigation flight stands (the probe, <see cref="ConnectedAnimation.TryGetNavFlight"/>).</summary>
public enum ConnectedNavPhase : byte { Pending, Flying, Landed, HandingOff, Abandoned }

/// <summary>A read-only view of one connected navigation flight, for the inspector's trace. Rects are window DIP: the source's rect
/// at take-off, the destination's rest rect (the flyer's model box), the flyer's presented rect this frame, and the rect the first
/// frame that drew the flyer posed it at (<c>TakeOff</c>: equal to <c>Source</c> when nothing jumped on the click frame).
/// <c>Radius</c> is the flyer's corner radius this frame and <c>DestRadius</c> the destination's; <c>Mix</c> the destination picture's
/// share when the two ends show different pictures (-1: one picture); <c>StandingIn</c> a landed flyer that stands in for a
/// destination picture not drawable yet; <c>Clip</c> the flyer's visible part; <c>Refinds</c> how often the destination was found
/// again (a skeleton's plate replaced by the loaded header).</summary>
public readonly record struct ConnectedNavFlightInfo(
    string Key, ConnectedNavPhase Phase, bool Back, RectF Source, RectF Dest, RectF Presented, float ShadowOpacity, int ImageId,
    bool SourceHidden, bool DestHidden, RectF TakeOff, float Radius, float DestRadius, float Mix, bool StandingIn, RectF Clip,
    int Refinds);

// ── THE NAVIGATION FLIGHT ────────────────────────────────────────────────────────────────────────────────────────────────
//
// A picture flies into the page it opens (and back). Beside the legacy key-wide flight above, which the Stage uses, and built
// for navigation:
//   · the source is the tagged end INSIDE the element the user activated (InputDispatcher.ActivatingNode), never "any node
//     with the key": a key repeats down a page;
//   · an END is a tagged node: an image (its slot is the image and the same-sized boxes round it), or a box (the box IS its
//     slot: its rect and corners, and the fit and crop of the largest picture inside it; a box with no picture is a landing
//     PLATE, a destination that gives a rect and stands in until the loaded node replaces it), or a WHOLE picture (a box tagged
//     through ConnectedAnimation.WholeKey: a generated cover, a gradient and a glyph or a mosaic, drawn as a whole by mirroring
//     its subtree into the flyer);
//   · the flyer is posed by its RECT, not a scale: its model box is the destination's rest rect, TranslateX/Y and SizeW/H
//     springs (presented size) carry it from the source's rect, the recorder cover-fits each picture into the presented rect
//     every frame, and the corner radius rides the presented size (SceneStore.SetCornerMorph), so a circle opens into a wide
//     header exactly on whichever thread draws the frame;
//   · two pictures: the second layer shows the destination's picture and dissolves in on the flight's spring when the two
//     ends differ (an avatar becoming a header photograph); the same picture at another decode size is a cut instead;
//   · a critically damped spring whose response scales with the distance: a turn-around (Back mid-flight) retargets the same
//     springs and keeps their velocity;
//   · the slot roots of both ends are hidden while it flies, so the flyer is the only copy;
//   · the hand-off: the flyer is exactly its model rect, the destination is at rest there, opaque enough, unblurred, and shows
//     the flyer's picture. Then a cut (the real node shows and the flyer goes in one publication) or a fade (the real node
//     shows and the flyer fades out over it). A landed flyer whose destination's picture is not drawable stands in for it.
// Per frame (UI thread) while a flight is up: hide flags, one rest-rect walk, one image-state poll. Nothing when none is.

public sealed partial class ConnectedAnimation
{
    private enum EndKind : byte { Picture, Plate, Whole }

    private sealed class NavFlight
    {
        public string Key = "";
        public bool Back, Turning;
        public ConnectedNavSpec Spec;
        public ConnectedNavPhase Phase;
        public NodeHandle Source, SourceSlot, Dest, DestSlot;
        public EndKind DestKind;
        public bool SourceHid, DestHid;
        // A turn-around's first source (the card a Back flies home to): still hidden from the forward leg until the dest is found.
        public NodeHandle PrevSlot;
        public bool PrevHid;
        public RectF TakeOff;
        public bool TookOff;
        public RectF SourceRect, Model;
        public float SourceRadius, DestRadius;               // effective radii (a circle is half its edge)
        // The layers: Image (the source's picture: an image, or a mirror of a whole picture), Image2 (the dest's picture, set once
        // it is known and differs; its opacity is the mix), Shadow (the hero's elevation, fading in or out).
        public NodeHandle Image, Image2, Shadow;
        public int ImageId, ImageId2;                       // 0: the layer mirrors a whole picture (or Image2 shows nothing yet)
        public bool Image2Set;
        public float MixTarget;
        public bool Clipped;                                // the image layers carry ClipL/T/R/B rows (a partly visible end)
        public SpringParams Spring;
        public float ShadowTarget;                          // the shadow's opacity at the dest (1: the hero's, 0: a card's)
        public double LandedAtMs, AbandonEndMs, PendingSinceMs;
        public bool Presented;                              // a frame has drawn the flyer (an abandon fades; else it frees at once)
        public bool Rescan;                                 // a node with this key was tagged: look for a better dest
        public int Refinds;
        // The source's ancestor chain at take-off (nearest first): the dest is the candidate whose chain meets it nearest the root
        // (another page, not a sibling card on the source's own page), and the overlay band clips to the first clipping box of the
        // dest's chain that is on it (the page box: the flyer never crosses the nav pane, the title bar or the player bar).
        public readonly NodeHandle[] Chain = new NodeHandle[MaxChain];
        public int ChainCount;
        public NodeHandle ClipNode;
    }

    /// <summary>The node a forward flight left from, per key, and the picture source it showed then: a Back flies to it (and a mouse
    /// Forward from it) while it is live and still shows that picture (a recycled row now shows another item).</summary>
    private readonly record struct NavReturn(NodeHandle Node, string? Picture);

    private const int MaxChain = 96;
    private const int SlotClimb = 3;                            // image → the same-sized boxes round it, at most this many levels
    private const float SlotSameSize = 0.5f;
    private const float RetargetEps = 0.5f;                     // a rest rect that moved by more than this re-aims the springs
    private const float HandOffEps = 0.25f;
    private const float RadiusEps = 0.25f;                      // radii closer than this need no corner morph
    private const float CutOpacity = 0.99f, FadeOpacity = 0.9f; // the dest's opacity chain a cut, a fading hand-off needs
    private const float BlurEps = 0.1f;
    private const int PictureScanNodes = 32, PictureScanDepth = 4;

    /// <summary>The first character of a key that marks its node as a WHOLE picture (<see cref="WholeKey"/>).</summary>
    public const char WholeMark = '\u0001';

    /// <summary>The tag for a node whose whole subtree is the picture (a generated cover: a gradient and a glyph, a mosaic): the flyer
    /// draws it as one picture, scaled, instead of the largest image inside it. Matches <paramref name="key"/> everywhere else.</summary>
    public static string WholeKey(string key) => WholeMark + key;

    private readonly List<NavFlight> _nav = new();
    private readonly Stack<NavFlight> _navPool = new();
    private readonly Dictionary<string, NavReturn> _navReturn = new(StringComparer.Ordinal);
    private readonly HashSet<NodeHandle> _whole = new();
    private readonly List<string> _deadReturns = new();

    /// <summary>The element the user just activated (the host hands the input dispatcher's <c>ActivatingNode</c>, else the focused
    /// node): a forward flight's source is the tagged picture inside it.</summary>
    internal Func<NodeHandle>? NavOrigin { get; set; }

    /// <summary>Live navigation flights (pending, flying, landed, handing off, or fading out after an abandon).</summary>
    public int NavFlightCount => _nav.Count;

    /// <summary>The probe: a read-only view of navigation flight <paramref name="i"/> (allocation-free).</summary>
    public bool TryGetNavFlight(int i, out ConnectedNavFlightInfo info)
    {
        if ((uint)i >= (uint)_nav.Count) { info = default; return false; }
        NavFlight f = _nav[i];
        RectF pres = f.SourceRect;
        float radius = f.SourceRadius;
        RectF clip = pres;
        if (_scene.IsLive(f.Image))
        {
            pres = FlyerRect(f.Image);
            radius = FlyerRadius(f.Image, in pres);
            ref NodePaint ip = ref _scene.Paint(f.Image);
            clip = ip.ClipRect.IsInfinite ? pres : new RectF(pres.X + ip.ClipRect.X, pres.Y + ip.ClipRect.Y, ip.ClipRect.W, ip.ClipRect.H).Intersect(pres);
        }
        float shadow = _scene.IsLive(f.Shadow) ? _scene.Paint(f.Shadow).Opacity : 0f;
        float mix = f.Image2Set && _scene.IsLive(f.Image2) ? _scene.Paint(f.Image2).Opacity : -1f;
        bool standingIn = f.Phase == ConnectedNavPhase.Landed && !ReadyToHandOff(f);
        info = new ConnectedNavFlightInfo(f.Key, f.Phase, f.Back, f.SourceRect, f.Model, pres, shadow, f.Image2Set && f.MixTarget > 0.5f ? f.ImageId2 : f.ImageId,
            f.SourceHid, f.DestHid, f.TookOff ? f.TakeOff : pres, radius, f.DestRadius, mix, standingIn, clip, f.Refinds);
        return true;
    }

    // ── the entry ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A route commit, before the route is written (every one: an empty key abandons a running flight). Abandons every
    /// navigation flight to another key; turns a flight with this key around (Back mid-flight: the same flyer, its springs keep
    /// their velocity); else captures the source (forward: per <see cref="ConnectedNavRequest.Source"/>; Back: the tagged picture
    /// with the largest visible area, the header of the page being left), puts the flyer up at its rect and hides the source's
    /// slot. True when a flyer is up: the shell then plays the quiet page swap under it.</summary>
    public bool BeginNav(in ConnectedNavRequest r)
    {
        string key = r.Key ?? "";
        for (int i = _nav.Count - 1; i >= 0; i--)
        {
            NavFlight g = _nav[i];
            if (g.Phase == ConnectedNavPhase.HandingOff) { FreeNav(g); continue; }   // the real node already shows
            if (g.Phase != ConnectedNavPhase.Abandoned && !string.Equals(g.Key, key, StringComparison.Ordinal)) AbandonNav(g);
        }
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

        NodeHandle source = PickSource(in r, key);
        if (source.IsNull) return false;
        bool whole = _whole.Contains(source);
        NodeHandle picture = whole ? NodeHandle.Null : PictureOf(source);
        if (!whole && picture.IsNull) return false;

        RectF rect = _scene.AbsoluteRect(source);
        if (MathF.Min(rect.W, rect.H) < r.Spec.MinSourceEdge) return false;
        float visibleFraction;
        RectF visible = EffectiveVisibleRect(source);
        VisibleArea(source, out visibleFraction);
        if (visibleFraction < r.Spec.MinVisible) return false;

        NavFlight n = _navPool.Count > 0 ? _navPool.Pop() : new NavFlight();
        ResetNav(n);
        n.Key = key; n.Back = r.Back; n.Spec = r.Spec; n.Phase = ConnectedNavPhase.Pending;
        n.Source = source;
        n.SourceSlot = SlotOf(source);
        n.SourceRect = rect;
        n.PendingSinceMs = _anim.ClockNowMs;
        CaptureChain(n, source);
        CornerRadius4 corners = EndCorners(source, n.SourceSlot);
        n.SourceRadius = Effective(corners.TopLeft, in rect);

        if (SlotShadow(n.SourceSlot, source, out ShadowSpec shadow)) n.Shadow = CreateShadowNode(in shadow, corners, rect, 1f);
        if (whole) n.Image = CreateMirrorLayer(n.SourceSlot, corners, rect, 1f);
        else
        {
            n.ImageId = TakeOffTexture(picture);
            _images.Pin(new ImageHandle(n.ImageId));
            n.Image = CreateImageLayer(n.ImageId, picture, corners, rect, 1f);
        }
        // A source a scroller's edge cuts takes off as its visible part (the band clips the rest), and opens on the flight's spring.
        if (!visible.IsInfinite && RectDiffers(in visible, in rect, RetargetEps))
        {
            ref NodePaint ip = ref _scene.Paint(n.Image);
            ip.ClipRect = new RectF(visible.X - rect.X, visible.Y - rect.Y, visible.W, visible.H);
            _scene.Mark(n.Image, NodeFlags.PaintDirty);
            n.Clipped = true;
        }
        n.SourceHid = Hide(n.SourceSlot);
        _nav.Add(n);
        if (MorphLog) Console.Error.WriteLine($"[morph] nav begin key={key} back={r.Back} whole={whole} src=({rect.X:0},{rect.Y:0},{rect.W:0},{rect.H:0})");
        return true;
    }

    // The source of a new flight, or null: forward per the request's source rule, Back the largest visible tagged end with a picture.
    private NodeHandle PickSource(in ConnectedNavRequest r, string key)
    {
        if (!r.Back && r.Source == ConnectedNavSource.Remembered)
        {
            if (!_navReturn.TryGetValue(key, out NavReturn ret) || !ReturnStillShows(in ret)) return NodeHandle.Null;
            return ret.Node;
        }
        NodeHandle origin = r.Back ? NodeHandle.Null : NavOrigin?.Invoke() ?? NodeHandle.Null;
        if (!r.Back && (origin.IsNull || !_scene.IsLive(origin))) return NodeHandle.Null;
        if (!r.Back && r.Source == ConnectedNavSource.ActivatedAnyPicture)
            return LargestPicture(origin, int.MaxValue, 256);

        NodeHandle source = NodeHandle.Null;
        float bestArea = -1f;
        foreach (var kv in _tagged)
        {
            if (!string.Equals(kv.Value, key, StringComparison.Ordinal) || !_scene.IsLive(kv.Key) || !OnLiveTree(kv.Key)) continue;
            if (!_whole.Contains(kv.Key) && PictureOf(kv.Key).IsNull) continue;   // a plate is never a source
            if (!r.Back && !IsSelfOrDescendant(kv.Key, origin)) continue;
            float area = VisibleArea(kv.Key, out _);
            if (area > bestArea) { bestArea = area; source = kv.Key; }
        }
        return source;
    }

    // The node a return entry names, when it is live, on the live tree and still shows the picture it showed.
    private bool ReturnStillShows(in NavReturn ret)
    {
        if (!_scene.IsLive(ret.Node) || !OnLiveTree(ret.Node)) return false;
        if (ret.Picture is null) return true;   // a whole picture: no source string to compare
        NodeHandle pic = PictureOf(ret.Node);
        return !pic.IsNull && string.Equals(_images.SourceOf(new ImageHandle(_scene.Paint(pic).ImageId)), ret.Picture, StringComparison.Ordinal);
    }

    // The texture the flyer takes off with: the largest resident rendition of the source's picture when it beats the source's own
    // (a 64 px row decode, a 256 card): the same picture and crop, so the first frame matches the source and later frames are sharp.
    private int TakeOffTexture(NodeHandle picture)
    {
        int own = _scene.Paint(picture).ImageId;
        var h = new ImageHandle(own);
        ImageHandle best = _images.ResidentRenditionOf(h);
        if (best.IsNull || _images.StateOf(best) != ImageState.Ready) return own;
        var (bw, bh) = _images.SizeOf(best);
        var (ow, oh) = _images.SizeOf(h);
        return _images.StateOf(h) != ImageState.Ready || (long)bw * bh > (long)ow * oh ? best.Id : own;
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
        f.SourceRadius = _scene.IsLive(f.Image) ? FlyerRadius(f.Image, in f.SourceRect) : f.DestRadius;
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
            if (f.Phase is ConnectedNavPhase.Abandoned or ConnectedNavPhase.HandingOff) continue;
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
                // The dest died, left the live tree, or a node with the key was just tagged (the loaded header replacing the
                // skeleton's plate): find it again. No dest: abandon.
                if (f.Rescan || !_scene.IsLive(f.Dest) || !OnLiveTree(f.Dest))
                {
                    f.Rescan = false;
                    NodeHandle better = FindNavDest(f);
                    if (better.IsNull) { AbandonNav(f); continue; }
                    if (better != f.Dest) Refind(f, better);
                }
                RectF rest = RestRect(f.Dest, out _, out _);
                if (RectDiffers(in rest, in f.Model, RetargetEps))
                {
                    // Flying: the springs follow the dest's new rest rect. Standing in (landed): it never chases a move, it hands off.
                    if (f.Phase == ConnectedNavPhase.Landed) { HandOffFade(f, f.Spec.AbandonFadeMs); continue; }
                    Reaim(f, rest, keepResponse: true);
                }
                SyncDestPicture(f);
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
        SetDest(f, dest);
        f.ClipNode = SharedClip(f, dest);
        RectF rest = RestRect(dest, out _, out _);
        bool destShadow = SlotShadow(f.DestSlot, dest, out ShadowSpec dShadow);
        if (destShadow && f.Shadow.IsNull)
        {
            // The shadow draws under the pictures: the band draws in insertion order, so they go back on top.
            f.Shadow = CreateShadowNode(in dShadow, _scene.Paint(f.Image).Corners, FlyerRect(f.Image), 0f);
            _scene.RemoveOverlay(f.Image);
            _scene.AddOverlay(f.Image);
            if (_scene.IsLive(f.Image2)) { _scene.RemoveOverlay(f.Image2); _scene.AddOverlay(f.Image2); }
        }
        else if (destShadow) _scene.SetShadow(f.Shadow, in dShadow);
        f.ShadowTarget = destShadow ? 1f : 0f;
        if (f.Turning) Reaim(f, rest, keepResponse: false);
        else Seed(f, rest);
        if (f.PrevHid)
        {
            if (f.PrevSlot == f.DestSlot) f.DestHid = true;   // the card the flight left from: hidden since take-off, shown at the hand-off
            else if (_scene.IsLive(f.PrevSlot)) Show(f.PrevSlot);
            f.PrevSlot = NodeHandle.Null; f.PrevHid = false;
        }
        if (!f.Back && !f.Source.IsNull) Remember(f.Key, f.Source);
        f.Phase = ConnectedNavPhase.Flying;
        f.Turning = false;
        SyncDestPicture(f);
        if (MorphLog) Console.Error.WriteLine($"[morph] nav fly key={f.Key} back={f.Back} kind={f.DestKind} dest=({rest.X:0},{rest.Y:0},{rest.W:0},{rest.H:0})");
    }

    // Take a dest over: its kind, slot, radius; its slot hidden.
    private void SetDest(NavFlight f, NodeHandle dest)
    {
        f.Dest = dest;
        f.DestKind = _whole.Contains(dest) ? EndKind.Whole : PictureOf(dest).IsNull ? EndKind.Plate : EndKind.Picture;
        f.DestSlot = SlotOf(dest);
        RectF r = _scene.AbsoluteRect(dest);
        f.DestRadius = Effective(EndCorners(dest, f.DestSlot).TopLeft, in r);
        f.DestHid = Hide(f.DestSlot);
    }

    // A better dest than the one the flight has (the loaded header replacing the skeleton's plate; a dest that died): show the old
    // slot, hide the new one, and glide to its rect if it differs.
    private void Refind(NavFlight f, NodeHandle better)
    {
        if (f.DestHid && _scene.IsLive(f.DestSlot) && f.DestSlot != f.SourceSlot) Show(f.DestSlot);
        f.DestHid = false;
        SetDest(f, better);
        f.Refinds++;
        RectF rest = RestRect(better, out _, out _);
        if (RectDiffers(in rest, in f.Model, RetargetEps)) Reaim(f, rest, keepResponse: true);
        else UpdateCornerMorph(f, rest);
        if (MorphLog) Console.Error.WriteLine($"[morph] nav refind key={f.Key} kind={f.DestKind}");
    }

    // First seed: the model box is the dest's rest rect; the rows start at the source's rect and rest at the model box. The rows
    // are pending-start, so the mount frame shows t = 0 at exactly the source's rect and the clock starts when it is presented. The
    // second picture's layer is created here, empty and transparent, so its rows are the image layer's from the first frame.
    private void Seed(NavFlight f, RectF rest)
    {
        RectF from = f.SourceRect;
        f.Model = rest;
        f.Spring = SpringParams.FromResponse(f.Spec.ResponseFor(CentreDistance(in from, in rest), f.Back), 1f);
        if (f.Image2.IsNull)
        {
            f.Image2 = CreateImageLayer(0, NodeHandle.Null, _scene.Paint(f.Image).Corners, from, 0f);
            if (f.Clipped) { _scene.Paint(f.Image2).ClipRect = _scene.Paint(f.Image).ClipRect; _scene.Mark(f.Image2, NodeFlags.PaintDirty); }
        }
        SeedNode(f.Image, in from, in rest, f.Spring);
        SeedNode(f.Image2, in from, in rest, f.Spring);
        if (f.Clipped) { SeedClip(f, f.Image, rest); SeedClip(f, f.Image2, rest); }
        if (!f.Shadow.IsNull)
        {
            SeedNode(f.Shadow, in from, in rest, f.Spring);
            float o = _scene.Paint(f.Shadow).Opacity;
            if (MathF.Abs(o - f.ShadowTarget) > 0.001f) _anim.Spring(f.Shadow, AnimChannel.Opacity, f.ShadowTarget, f.Spring, initial: o);
            _anim.HoldStartUntilPresented(f.Shadow);
        }
        UpdateCornerMorph(f, rest);
        _anim.HoldStartUntilPresented(f.Image);
        _anim.HoldStartUntilPresented(f.Image2);
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

    // The clip rows of a partly visible end: from where the clip stands to the dest's visible part (the whole rect when all of it
    // shows), local to the presented rect, on the flight's spring.
    private void SeedClip(NavFlight f, NodeHandle node, in RectF rest)
    {
        RectF target = DestLocalClip(f, in rest);
        ref NodePaint p = ref _scene.Paint(node);
        RectF at = p.ClipRect.IsInfinite ? new RectF(0f, 0f, rest.W, rest.H) : p.ClipRect;
        p.ClipRect = at;
        _scene.Mark(node, NodeFlags.PaintDirty);
        _anim.Spring(node, AnimChannel.ClipL, target.X, f.Spring, initial: at.X);
        _anim.Spring(node, AnimChannel.ClipT, target.Y, f.Spring, initial: at.Y);
        _anim.Spring(node, AnimChannel.ClipR, target.Right, f.Spring, initial: at.Right);
        _anim.Spring(node, AnimChannel.ClipB, target.Bottom, f.Spring, initial: at.Bottom);
    }

    // The dest's visible part, local to its rest rect (the whole rect when it all shows).
    private RectF DestLocalClip(NavFlight f, in RectF rest)
    {
        RectF full = new(0f, 0f, rest.W, rest.H);
        if (!_scene.IsLive(f.Dest)) return full;
        RectF v = EffectiveVisibleRect(f.Dest);
        if (v.IsInfinite || v.IsEmpty || !RectDiffers(in v, in rest, RetargetEps)) return full;
        return new RectF(v.X - rest.X, v.Y - rest.Y, v.W, v.H);
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
        UpdateCornerMorph(f, rest);
        ReaimNode(f.Image, rest, f.Spring);
        if (_scene.IsLive(f.Image2)) ReaimNode(f.Image2, rest, f.Spring);
        if (f.Clipped || !DestLocalClip(f, in rest).Equals(new RectF(0f, 0f, rest.W, rest.H)))
        {
            f.Clipped = true;
            SeedClip(f, f.Image, rest);
            if (_scene.IsLive(f.Image2)) SeedClip(f, f.Image2, rest);
        }
        if (!f.Shadow.IsNull)
        {
            ReaimNode(f.Shadow, rest, f.Spring);
            _anim.Spring(f.Shadow, AnimChannel.Opacity, f.ShadowTarget, f.Spring);
        }
        if (f.Image2Set && _scene.IsLive(f.Image2) && f.Turning)
        {
            // A turn flies back to the picture it came from: the mix runs the other way on the turn's spring.
            f.MixTarget = f.MixTarget > 0.5f ? 0f : 1f;
            _anim.Spring(f.Image2, AnimChannel.Opacity, f.MixTarget, f.Spring);
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

    // The radius travels with the size: from the flyer's radius at its presented extent now to the dest's radius at its extent,
    // along the axis that changes more, evaluated by the recorder per drawn frame (SceneStore.SetCornerMorph). Radii that already
    // agree need none (the corners stay, a cut at the hand-off is invisible). Mirror layers keep their own content's corners.
    private void UpdateCornerMorph(NavFlight f, in RectF rest)
    {
        if (!_scene.IsLive(f.Image)) return;
        RectF at = FlyerRect(f.Image);
        float from = FlyerRadius(f.Image, in at);
        if (MathF.Abs(from - f.DestRadius) < RadiusEps)
        {
            ClearMorph(f.Image); ClearMorph(f.Image2); ClearMorph(f.Shadow);
            CornerRadius4 c = CornerRadius4.All(f.DestRadius);
            SetCorners(f.Image, c); SetCorners(f.Image2, c); SetCorners(f.Shadow, c);
            return;
        }
        bool alongH = MathF.Abs(rest.H - at.H) > MathF.Abs(rest.W - at.W);
        var m = alongH
            ? new CornerMorph(from, f.DestRadius, at.H, rest.H, true)
            : new CornerMorph(from, f.DestRadius, at.W, rest.W, false);
        if (_scene.IsLive(f.Image) && f.ImageId != 0) _scene.SetCornerMorph(f.Image, in m);
        if (_scene.IsLive(f.Image2) && (!f.Image2Set || f.ImageId2 != 0)) _scene.SetCornerMorph(f.Image2, in m);
        if (_scene.IsLive(f.Shadow)) _scene.SetCornerMorph(f.Shadow, in m);
    }

    private void ClearMorph(NodeHandle node) { if (_scene.IsLive(node)) _scene.ClearCornerMorph(node); }

    private void SetCorners(NodeHandle node, CornerRadius4 c)
    {
        if (!_scene.IsLive(node) || _scene.Paint(node).Corners.Equals(c)) return;
        _scene.Paint(node).Corners = c;
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    // The flyer shows the dest's picture once that is drawable: the same picture at another decode size (one url, the same aspect,
    // fit and crop) is a cut on the image layer; anything else (an avatar becoming a header photograph, a card's 16:9 art and the
    // square cover, a whole generated cover) fills the second layer and dissolves in on the flight's spring.
    private void SyncDestPicture(NavFlight f)
    {
        if (f.DestKind == EndKind.Plate || !_scene.IsLive(f.Dest)) return;
        if (f.DestKind == EndKind.Whole)
        {
            if (f.Image2Set) return;
            BecomeMirror(f.Image2, f.DestSlot);
            f.ImageId2 = 0;
            StartMix(f);
            return;
        }
        NodeHandle dp = PictureOf(f.Dest);
        if (dp.IsNull) return;
        int id = _scene.Paint(dp).ImageId;
        if (id == 0 || _images.StateOf(new ImageHandle(id)) != ImageState.Ready) return;
        if (f.Image2Set)
        {
            if (f.MixTarget < 0.5f || f.ImageId2 == id || f.ImageId2 == 0) return;
            // The dest's picture moved to another rendition of itself (a larger decode): a cut on the second layer.
            if (!_images.SameSource(new ImageHandle(id), new ImageHandle(f.ImageId2))) return;
            SwapLayerImage(f.Image2, ref f.ImageId2, id);
            return;
        }
        if (id == f.ImageId) return;
        if (f.ImageId != 0 && SameFraming(f, dp, id))
        {
            SwapLayerImage(f.Image, ref f.ImageId, id);
            return;
        }
        _images.Pin(new ImageHandle(id));
        f.ImageId2 = id;
        BecomeImage(f.Image2, id, dp);
        StartMix(f);
    }

    private void StartMix(NavFlight f)
    {
        f.Image2Set = true;
        f.MixTarget = 1f;
        float o = _scene.Paint(f.Image2).Opacity;
        _anim.Spring(f.Image2, AnimChannel.Opacity, 1f, f.Spring, initial: o);
        UpdateCornerMorphLayer(f);
    }

    // The second layer just took a picture: an image follows the image layer's corner morph, a mirror keeps its content's corners.
    private void UpdateCornerMorphLayer(NavFlight f)
    {
        if (f.ImageId2 == 0) { ClearMorph(f.Image2); return; }
        if (_scene.IsLive(f.Image) && _scene.TryGetCornerMorph(f.Image, out CornerMorph m)) _scene.SetCornerMorph(f.Image2, in m);
        else if (_scene.IsLive(f.Shadow) && _scene.TryGetCornerMorph(f.Shadow, out CornerMorph sm)) _scene.SetCornerMorph(f.Image2, in sm);
        else SetCorners(f.Image2, _scene.Paint(f.Image).Corners);
    }

    // One picture, one framing: the same source url, the two ends' aspects within 1 %, the same fit and crop focus.
    private bool SameFraming(NavFlight f, NodeHandle destPicture, int destId)
    {
        if (!_images.SameSource(new ImageHandle(f.ImageId), new ImageHandle(destId))) return false;
        float a0 = f.SourceRect.H > 0f ? f.SourceRect.W / f.SourceRect.H : 1f, a1 = f.Model.H > 0f ? f.Model.W / f.Model.H : 1f;
        if (MathF.Abs(a0 - a1) > 0.01f * MathF.Max(a0, a1)) return false;
        ref NodePaint lp = ref _scene.Paint(f.Image);
        if (lp.ImageFit != _scene.Paint(destPicture).ImageFit) return false;
        ref readonly NodePaintCold lc = ref _scene.PaintCold(f.Image);
        ref readonly NodePaintCold dc = ref _scene.PaintCold(destPicture);
        return MathF.Abs(lc.ImageFocusX - dc.ImageFocusX) < 0.001f && MathF.Abs(lc.ImageFocusY - dc.ImageFocusY) < 0.001f
               && MathF.Abs(lc.ImageFrameX - dc.ImageFrameX) < 0.001f && MathF.Abs(lc.ImageFrameY - dc.ImageFrameY) < 0.001f;
    }

    private void SwapLayerImage(NodeHandle layer, ref int layerId, int id)
    {
        _images.Pin(new ImageHandle(id));
        if (layerId != 0) _images.Unpin(new ImageHandle(layerId));
        layerId = id;
        _scene.Paint(layer).ImageId = id;
        _scene.Mark(layer, NodeFlags.PaintDirty);
    }

    // ── phase 7 (after the anim tick): land, hand off, stand in, retire ──────────────────────────────────────────────────

    private void SettleNav()
    {
        if (_nav.Count == 0) return;
        double now = _anim.ClockNowMs;
        for (int i = _nav.Count - 1; i >= 0; i--)
        {
            NavFlight f = _nav[i];
            f.Presented = true;   // this frame records it
            if (!f.TookOff && f.Phase == ConnectedNavPhase.Flying && _scene.IsLive(f.Image)) { f.TakeOff = FlyerRect(f.Image); f.TookOff = true; }
            if (f.Clipped && f.Phase is ConnectedNavPhase.Flying or ConnectedNavPhase.Landed) SettleClip(f);
            switch (f.Phase)
            {
                case ConnectedNavPhase.Abandoned:
                case ConnectedNavPhase.HandingOff:
                    if (now >= f.AbandonEndMs) FreeNav(f);
                    break;
                case ConnectedNavPhase.Flying:
                    if (GeometryLive(f.Image) || _scene.IsLive(f.Shadow) && GeometryLive(f.Shadow)) break;
                    f.Phase = ConnectedNavPhase.Landed;
                    f.LandedAtMs = now;
                    goto case ConnectedNavPhase.Landed;
                case ConnectedNavPhase.Landed:
                    if (ReadyToHandOff(f))
                    {
                        if (MorphLog) Console.Error.WriteLine($"[morph] nav hand-off key={f.Key} fade={f.Spec.HandOffFadeMs} after={now - f.LandedAtMs:0}ms");
                        if (f.Spec.HandOffFadeMs > 0f) HandOffFade(f, f.Spec.HandOffFadeMs);
                        else FreeNav(f);
                    }
                    else if (now - f.LandedAtMs >= f.Spec.HandOffWaitMs)
                    {
                        // Stood in as long as it may: the page's own picture (or its placeholder) takes over under a fade.
                        if (MorphLog) Console.Error.WriteLine($"[morph] nav stand-in over key={f.Key}");
                        HandOffFade(f, f.Spec.AbandonFadeMs);
                    }
                    break;
            }
        }
    }

    private bool GeometryLive(NodeHandle node)
        => _anim.HasLiveRow(node, AnimChannel.TranslateX) || _anim.HasLiveRow(node, AnimChannel.TranslateY)
           || _anim.HasLiveRow(node, AnimChannel.SizeW) || _anim.HasLiveRow(node, AnimChannel.SizeH);

    private bool ClipLive(NodeHandle node)
        => _anim.HasLiveRow(node, AnimChannel.ClipL) || _anim.HasLiveRow(node, AnimChannel.ClipT)
           || _anim.HasLiveRow(node, AnimChannel.ClipR) || _anim.HasLiveRow(node, AnimChannel.ClipB);

    // A clip that opened all the way is dropped (the layers draw unclipped, their shadow-free rect is the whole picture).
    private void SettleClip(NavFlight f)
    {
        if (ClipLive(f.Image) || _scene.IsLive(f.Image2) && ClipLive(f.Image2)) return;
        ref NodePaint p = ref _scene.Paint(f.Image);
        ref RectF b = ref _scene.Bounds(f.Image);
        if (!p.ClipRect.IsInfinite && !RectDiffers(in p.ClipRect, new RectF(0f, 0f, b.W, b.H), HandOffEps))
        {
            p.ClipRect = RectF.Infinite;
            _scene.Mark(f.Image, NodeFlags.PaintDirty);
            if (_scene.IsLive(f.Image2)) { _scene.Paint(f.Image2).ClipRect = RectF.Infinite; _scene.Mark(f.Image2, NodeFlags.PaintDirty); }
            f.Clipped = false;
        }
    }

    // The hand-off rule: the flyer is exactly its model rect (its rows are done), the dest is at rest at that rect (what its own and
    // its ancestors' translate rows have left to travel is within a quarter DIP, and no scale row is live), its opacity chain is
    // opaque enough (a fading hand-off may start a little earlier: the same picture is under it), it is not blurred, the flyer's
    // mix and clip are settled, and it shows the dest's picture, drawable. A plate is never ready: the flyer stands in for it.
    private bool ReadyToHandOff(NavFlight f)
    {
        if (!_scene.IsLive(f.Dest) || f.DestKind == EndKind.Plate) return false;
        RectF rest = RestRect(f.Dest, out float travel, out bool scaling);
        if (scaling || travel > HandOffEps) return false;
        ref RectF b = ref _scene.Bounds(f.Image);
        if (RectDiffers(in rest, in b, HandOffEps)) return false;
        if (GeometryLive(f.Image) || ClipLive(f.Image)) return false;
        float opacity = 1f, blur = 0f;
        for (NodeHandle n = f.Dest; !n.IsNull; n = _scene.Parent(n))
        {
            ref NodePaint np = ref _scene.Paint(n);
            blur += np.BlurSigma;
            if (n == f.DestSlot) continue;   // hidden by us: its own opacity is the page's
            opacity *= np.Opacity;
        }
        if (opacity < (f.Spec.HandOffFadeMs > 0f ? FadeOpacity : CutOpacity) || blur >= BlurEps) return false;
        if (f.Image2Set && _scene.IsLive(f.Image2) && _anim.HasLiveRow(f.Image2, AnimChannel.Opacity)) return false;
        if (f.DestKind == EndKind.Whole)
            return f.Image2Set && f.MixTarget > 0.5f || f.ImageId == 0 && _scene.TryGetMirror(f.Image, out NodeHandle mt) && mt == f.DestSlot;
        NodeHandle dp = PictureOf(f.Dest);
        if (dp.IsNull) return false;
        int id = _scene.Paint(dp).ImageId;
        if (id == 0) return false;
        var h = new ImageHandle(id);
        if (_images.StateOf(h) != ImageState.Ready || _images.CrossFadeOf(h) < 0.99f) return false;
        // The flyer shows what the dest draws: its second layer holds it, or the image layer does (one picture, or a turn home).
        int showing = f.Image2Set && f.MixTarget > 0.5f ? f.ImageId2 : f.ImageId;
        return showing == id || _images.SameSource(new ImageHandle(showing), h);
    }

    // The real node shows and the flyer fades out over it where it stands (geometry held, the shadow gone with the reveal).
    private void HandOffFade(NavFlight f, float ms)
    {
        if (ms <= 0f || !f.Presented) { FreeNav(f); return; }
        Reveal(f);
        FreeOverlayNode(f.Shadow);
        f.Shadow = NodeHandle.Null;
        FadeLayer(f.Image, ms);
        FadeLayer(f.Image2, ms);
        f.Phase = ConnectedNavPhase.HandingOff;
        f.AbandonEndMs = _anim.ClockNowMs + ms;
    }

    private void FadeLayer(NodeHandle node, float ms)
    {
        if (!_scene.IsLive(node)) return;
        HoldGeometry(node);
        _anim.SeedEased(node, AnimChannel.Opacity, _scene.Paint(node).Opacity, 0f, ms, Easing.Linear);
    }

    // An abandon reveals both ends at once. A flyer a frame has drawn fades out where it stands (its geometry rows held: the
    // pixel on screen, on the render thread too); one no frame drew is freed now.
    private void AbandonNav(NavFlight f, bool fade = true)
    {
        Reveal(f);
        if (f.Phase == ConnectedNavPhase.HandingOff || !fade || !f.Presented || !_scene.IsLive(f.Image) || f.Spec.AbandonFadeMs <= 0f) { FreeNav(f); return; }
        FadeLayer(f.Image, f.Spec.AbandonFadeMs);
        FadeLayer(f.Image2, f.Spec.AbandonFadeMs);
        FadeLayer(f.Shadow, f.Spec.AbandonFadeMs);
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
        _anim.SetHeld(node, AnimChannel.ClipL, true);
        _anim.SetHeld(node, AnimChannel.ClipT, true);
        _anim.SetHeld(node, AnimChannel.ClipR, true);
        _anim.SetHeld(node, AnimChannel.ClipB, true);
    }

    // Every exit: both ends shown, the pins released, the layers freed, the flight back in the pool.
    private void FreeNav(NavFlight f)
    {
        Reveal(f);
        if (f.ImageId != 0) _images.Unpin(new ImageHandle(f.ImageId));
        if (f.ImageId2 != 0) _images.Unpin(new ImageHandle(f.ImageId2));
        FreeOverlayNode(f.Image);
        FreeOverlayNode(f.Image2);
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
        f.Source = f.SourceSlot = f.Dest = f.DestSlot = f.Image = f.Image2 = f.Shadow = f.ClipNode = NodeHandle.Null;
        f.DestKind = EndKind.Picture;
        f.SourceHid = f.DestHid = f.PrevHid = f.TookOff = false; f.PrevSlot = NodeHandle.Null; f.SourceRect = f.Model = f.TakeOff = default;
        f.ImageId = f.ImageId2 = 0; f.Image2Set = false; f.MixTarget = 0f; f.Clipped = false; f.Spring = default;
        f.SourceRadius = f.DestRadius = 0f;
        f.LandedAtMs = double.NaN; f.AbandonEndMs = 0d; f.PendingSinceMs = 0d; f.Presented = false; f.ShadowTarget = 0f;
        f.Rescan = false; f.Refinds = 0;
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

    // A node was tagged with a key a flight is landing on: look for a better dest on the next tick (no per-frame scan).
    private void NoteNavTagged(string key)
    {
        for (int i = 0; i < _nav.Count; i++)
            if (_nav[i].Phase is ConnectedNavPhase.Flying or ConnectedNavPhase.Landed && string.Equals(_nav[i].Key, key, StringComparison.Ordinal))
                _nav[i].Rescan = true;
    }

    private void Remember(string key, NodeHandle node)
    {
        // Drop the entries whose node died while the dictionary is touched anyway (once per flight).
        _deadReturns.Clear();
        foreach (var kv in _navReturn) if (!_scene.IsLive(kv.Value.Node)) _deadReturns.Add(kv.Key);
        for (int i = 0; i < _deadReturns.Count; i++) _navReturn.Remove(_deadReturns[i]);
        string? picture = null;
        if (!_whole.Contains(node))
        {
            NodeHandle pic = PictureOf(node);
            if (!pic.IsNull) picture = _images.SourceOf(new ImageHandle(_scene.Paint(pic).ImageId));
        }
        _navReturn[key] = new NavReturn(node, picture);
    }

    // ── ends, layers, geometry and tree helpers ──────────────────────────────────────────────────────────────────────────

    // The dest: a tagged node with the flight's key (or the node a forward flight left from, tagged or not, on a Back), live, on
    // the live tree, laid out, not the source. In order: not inside an exit orphan (a page fading out), whose ancestor chain meets
    // the source's nearest the root (the other page: a second card of the key on the source's own page never wins), the node the
    // forward flight left from (Back), a picture over a plate, the larger visible area.
    private NodeHandle FindNavDest(NavFlight f)
    {
        NodeHandle best = NodeHandle.Null;
        int bestScore = int.MinValue;
        float bestArea = -1f;
        NodeHandle ret = NodeHandle.Null;
        if (f.Back && _navReturn.TryGetValue(f.Key, out NavReturn rn) && ReturnStillShows(in rn)) ret = rn.Node;
        foreach (var kv in _tagged)
        {
            if (!string.Equals(kv.Value, f.Key, StringComparison.Ordinal)) continue;
            Consider(f, kv.Key, ret, ref best, ref bestScore, ref bestArea);
        }
        if (!ret.IsNull && !_tagged.ContainsKey(ret)) Consider(f, ret, ret, ref best, ref bestScore, ref bestArea);
        return best;
    }

    private void Consider(NavFlight f, NodeHandle n, NodeHandle ret, ref NodeHandle best, ref int bestScore, ref float bestArea)
    {
        if (n == f.Source || !_scene.IsLive(n) || !OnLiveTree(n)) return;
        ref RectF b = ref _scene.Bounds(n);
        if (b.W <= 1f || b.H <= 1f) return;
        int meet = MeetIndex(f, n);
        // Lexicographic: orphan (no = better) > meet > return > picture over plate.
        int score = (UnderOrphan(n) ? 0 : 1 << 24) + ((meet + 1) << 4) + (n == ret ? 1 << 2 : 0)
                    + (_whole.Contains(n) || !PictureOf(n).IsNull ? 1 : 0);
        float area = VisibleArea(n, out _);
        if (score < bestScore || score == bestScore && area <= bestArea) return;
        best = n; bestScore = score; bestArea = area;
    }

    // Is the node inside an exit orphan (a page fading out)? The top of its parent chain is one.
    private bool UnderOrphan(NodeHandle node)
    {
        NodeHandle top = node;
        for (NodeHandle n = node; !n.IsNull; n = _scene.Parent(n)) top = n;
        return top != _scene.Root && _scene.IsOrphan(top);
    }

    // Where the node's ancestor chain first meets the source's chain, as an index into it (higher = nearer the root); -1 never.
    private int MeetIndex(NavFlight f, NodeHandle node)
    {
        for (NodeHandle n = _scene.Parent(node); !n.IsNull; n = _scene.Parent(n))
            for (int i = 0; i < f.ChainCount; i++)
                if (f.Chain[i] == n) return i;
        return -1;
    }

    // The band's clip: the first clipping box above the dest that is also above the source (the page box for a page source; the
    // window's content for a pane source).
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

    private bool IsPictureNode(NodeHandle n)
    {
        ref NodePaint p = ref _scene.Paint(n);
        return p.VisualKind == VisualKind.Image && p.ImageId != 0;
    }

    // An end's picture: the node itself when it is an image with a picture; else (a box end) its largest image with a picture,
    // at most PictureScanDepth levels down and PictureScanNodes nodes in (allocation-free). Null: a plate (or a whole picture).
    private NodeHandle PictureOf(NodeHandle end)
    {
        if (!_scene.IsLive(end)) return NodeHandle.Null;
        if (IsPictureNode(end)) return end;
        if (_scene.Paint(end).VisualKind == VisualKind.Image) return NodeHandle.Null;
        return LargestPicture(end, PictureScanDepth, PictureScanNodes);
    }

    // The largest-area shown image with a picture under root (pre-order, depth- and count-bounded; no recursion, no allocation).
    private NodeHandle LargestPicture(NodeHandle root, int maxDepth, int maxNodes)
    {
        if (IsPictureNode(root)) return root;
        NodeHandle best = NodeHandle.Null;
        float bestArea = 0f;
        int visited = 0, depth = 1;
        NodeHandle n = _scene.FirstChild(root);
        while (!n.IsNull && visited++ < maxNodes)
        {
            bool shown = (_scene.Flags(n) & NodeFlags.Visible) != 0;
            if (shown && IsPictureNode(n))
            {
                ref RectF b = ref _scene.Bounds(n);
                float area = b.W * b.H;
                if (area > bestArea) { bestArea = area; best = n; }
            }
            NodeHandle child = shown && depth < maxDepth ? _scene.FirstChild(n) : NodeHandle.Null;
            if (!child.IsNull) { n = child; depth++; continue; }
            // next in pre-order: a sibling, else climb towards root
            while (true)
            {
                NodeHandle sib = _scene.NextSibling(n);
                if (!sib.IsNull) { n = sib; break; }
                n = _scene.Parent(n);
                depth--;
                if (n.IsNull || n == root || depth <= 0) { n = NodeHandle.Null; break; }
            }
        }
        return best;
    }

    // An end's slot: an image climbs to the same-sized boxes round it (the card's cover stack with its play button, the hero's
    // artwork box and its shadowed cover box), at most SlotClimb levels; a box end is its own slot.
    private NodeHandle SlotOf(NodeHandle end)
    {
        if (_scene.Paint(end).VisualKind != VisualKind.Image) return end;
        NodeHandle slot = end;
        ref RectF ib = ref _scene.Bounds(end);
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

    // An end's corners: its own, else its slot's (a circular card clips its cover stack, not the image).
    private CornerRadius4 EndCorners(NodeHandle end, NodeHandle slot)
    {
        CornerRadius4 c = _scene.Paint(end).Corners;
        if (c.TopLeft > 0f || slot.IsNull || !_scene.IsLive(slot)) return c;
        for (NodeHandle n = _scene.Parent(end); !n.IsNull; n = _scene.Parent(n))
        {
            CornerRadius4 sc = _scene.Paint(n).Corners;
            if (sc.TopLeft > 0f) return sc;
            if (n == slot) break;
        }
        return c;
    }

    private static float Effective(float authored, in RectF rect) => MathF.Max(0f, MathF.Min(authored, MathF.Min(rect.W, rect.H) * 0.5f));

    // The shadow an end's slot draws (the hero's cover box carries Elevation.Card; a card carries none).
    private bool SlotShadow(NodeHandle slot, NodeHandle end, out ShadowSpec shadow)
    {
        for (NodeHandle n = end; !n.IsNull; n = _scene.Parent(n))
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

    // An image layer: the picture's id, fit and crop focus, the end's corners.
    private NodeHandle CreateImageLayer(int imageId, NodeHandle picture, CornerRadius4 corners, RectF at, float opacity)
    {
        var img = _scene.CreateNode(8 /* ImageEl */);
        ref NodePaint p = ref _scene.Paint(img);
        p = NodePaint.Default;
        p.VisualKind = VisualKind.Image;
        p.Corners = corners;
        p.Opacity = opacity;
        _scene.Bounds(img) = at;
        _scene.SetFlagsRaw(img, (_scene.Flags(img) & ~NodeFlags.HitTestVisible) | NodeFlags.Visible);
        _scene.AddOverlay(img);
        if (imageId != 0) BecomeImage(img, imageId, picture);
        return img;
    }

    private void BecomeImage(NodeHandle layer, int imageId, NodeHandle picture)
    {
        ref NodePaint p = ref _scene.Paint(layer);
        p.VisualKind = VisualKind.Image;
        p.ImageId = imageId;
        if (!picture.IsNull && _scene.IsLive(picture))
        {
            p.ImageFit = _scene.Paint(picture).ImageFit;
            ref readonly NodePaintCold pc = ref _scene.PaintCold(picture);
            NodePaintCold c = _scene.PaintCold(layer);
            c.ImageFocusX = pc.ImageFocusX; c.ImageFocusY = pc.ImageFocusY;
            c.ImageFrameX = pc.ImageFrameX; c.ImageFrameY = pc.ImageFrameY;
            _scene.SetPaintCold(layer, in c);
        }
        _scene.Mark(layer, NodeFlags.PaintDirty);
    }

    // A mirror layer: a fill-less box that draws a whole picture's subtree (SceneStore.SetMirror) scaled into its presented rect.
    private NodeHandle CreateMirrorLayer(NodeHandle target, CornerRadius4 corners, RectF at, float opacity)
    {
        var node = _scene.CreateNode(1 /* BoxEl */);
        ref NodePaint p = ref _scene.Paint(node);
        p = NodePaint.Default;
        p.Opacity = opacity;
        p.Corners = corners;
        _scene.Bounds(node) = at;
        _scene.SetFlagsRaw(node, (_scene.Flags(node) & ~NodeFlags.HitTestVisible) | NodeFlags.Visible);
        _scene.AddOverlay(node);
        BecomeMirror(node, target);
        return node;
    }

    private void BecomeMirror(NodeHandle layer, NodeHandle target)
    {
        ref NodePaint p = ref _scene.Paint(layer);
        p.VisualKind = VisualKind.None;
        p.ImageId = 0;
        _scene.SetMirror(layer, target);
        _scene.ClearCornerMorph(layer);
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
    // page's entrance slide, a body's rise, a hover lift). `travel` is the largest of the summed offsets, `scaling` whether a
    // scale row is live up the chain (its rect is not its rest one yet, and this cannot say where that is).
    private RectF RestRect(NodeHandle node, out float travel, out bool scaling)
    {
        RectF a = _scene.AbsoluteRect(node);
        float dx = 0f, dy = 0f;
        scaling = false;
        for (NodeHandle n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if (!_anim.RestOffset(n, out float ox, out float oy, out bool sc)) continue;
            dx += ox; dy += oy; scaling |= sc;
        }
        travel = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
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

    // The flyer's corner radius at a presented rect: its morph's, else its corners'.
    private float FlyerRadius(NodeHandle node, in RectF at)
        => _scene.TryGetCornerMorph(node, out CornerMorph m) ? m.At(at.W, at.H) : Effective(_scene.Paint(node).Corners.TopLeft, in at);

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
