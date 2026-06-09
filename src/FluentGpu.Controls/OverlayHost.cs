using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A live overlay; call <see cref="Close"/> to dismiss it.</summary>
public sealed class OverlayHandle
{
    internal Action<OverlayCloseCause>? CloseAction;
    internal Action? ClosedAction;
    internal Action<OverlayCloseCause>? ClosedWithCauseAction;
    internal Func<OverlayCloseCause, bool>? ClosingAction;
    public bool IsOpen { get; internal set; }
    public void Close() => Close(OverlayCloseCause.Programmatic);
    internal void Close(OverlayCloseCause cause) { if (IsOpen) CloseAction?.Invoke(cause); }
}

/// <summary>
/// Opens anchored, light-dismissable flyouts (menus, ComboBox/DropDownButton/SplitButton dropdowns, ColorPicker). A
/// control captures its own node via <see cref="BoxEl.OnRealized"/> and passes a thunk for it as the anchor; the host
/// positions the popup relative to the anchor's on-screen rect (flip/nudge into the viewport) and animates it open/closed
/// with the WinUI MenuPopupThemeTransition. Resolve via <c>UseContext(Overlay.Service)</c> inside an <see cref="OverlayHost"/>.
/// </summary>
/// <summary>How a popup dismisses. <see cref="LightDismiss"/> = click-outside + Escape (the default); <see cref="Modal"/>
/// = no light dismiss (ContentDialog); <see cref="None"/> = caller-controlled only.</summary>
public enum DismissBehavior : byte { LightDismiss, Modal, None }

/// <summary>Where an overlay close request came from; controls map this to their public close reason.</summary>
public enum OverlayCloseCause : byte { Programmatic, LightDismiss, Escape }

/// <summary>Which presenter chrome the overlay host supplies around popup content.</summary>
public enum PopupChrome : byte { Flyout, Raw, Modal, TeachingTip }

/// <summary>Optional popup behavior. <see cref="FocusTrap"/> keeps Tab/Shift-Tab inside the overlay subtree (modal-style).</summary>
public readonly record struct PopupOptions(
    bool FocusTrap = false,
    DismissBehavior DismissBehavior = DismissBehavior.LightDismiss,
    PopupChrome Chrome = PopupChrome.Flyout);

public interface IOverlayService
{
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft);
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options);
    void CloseTop();
    void CloseAll();
    bool AnyOpen { get; }
}

/// <summary>Ambient overlay service, provided by the nearest <see cref="OverlayHost"/>.</summary>
public static class Overlay
{
    public static readonly Context<IOverlayService> Service = new(NullOverlayService.Instance);
    public static readonly Context<Signal<OverlayPlacementInfo>?> Placement = new(null);
}
/// <summary>Resolved popup placement in popup-local coordinates, updated by <see cref="OverlayHost"/> after layout.</summary>
public readonly record struct OverlayPlacementInfo(
    float AnchorCenterX,
    float AnchorCenterY,
    float PopupWidth,
    float PopupHeight,
    bool OpensUp);

internal sealed class NullOverlayService : IOverlayService
{
    public static readonly NullOverlayService Instance = new();
    public OverlayHandle Open(Func<NodeHandle> a, Func<Element> c, FlyoutPlacement p) => new();
    public OverlayHandle Open(Func<NodeHandle> a, Func<Element> c, FlyoutPlacement p, PopupOptions o) => new();
    public void CloseTop() { }
    public void CloseAll() { }
    public bool AnyOpen => false;
}

internal sealed class OverlayEntry
{
    public int Id;
    public required Func<NodeHandle> Anchor;
    public required Func<Element> Content;
    public FlyoutPlacement Placement;
    public required OverlayHandle Handle;
    public NodeHandle WrapperNode;    // measured + translated for placement
    public NodeHandle SurfaceNode;    // presenter surface: the SizeH clip-reveal + opacity animate this node
    public float MeasuredW;
    public float MeasuredH;
    public bool OpensUp;
    public CornerJoin CornerJoin;     // which popup corners abut the anchor (corner-squaring for ComboBox/AutoSuggestBox)
    public NodeHandle SavedFocus;     // focus captured at open time → restored when the overlay finishes closing
    public bool FocusTrap;
    public DismissBehavior DismissBehavior;
    public PopupChrome Chrome;
    public OverlayPhase Phase;
    public bool OpenSeeded;
    public bool CloseSeeded;
    public OverlayCloseCause CloseCause;
    public Signal<OverlayPlacementInfo> PlacementInfo = new(default);
}

internal enum OverlayPhase : byte { Opening, Open, Closing }

internal sealed class OverlayServiceImpl : IOverlayService
{
    const float OpacityMs = 83f;

    private readonly Signal<int> _version;     // bump → OverlayHost re-renders
    private int _nextId;
    public readonly List<OverlayEntry> Entries = new(4);

    // Host-wired (via the InputHooks ambient): read/restore the focused node for WinUI flyout focus-restoration.
    public Func<NodeHandle>? GetFocus;
    public Action<NodeHandle>? RestoreFocus;
    public SceneStore? Scene;
    public AnimEngine? Anim;
    public NodeHandle ScrimNode;

    public OverlayServiceImpl(Signal<int> version) => _version = version;

    public bool AnyOpen { get { foreach (var e in Entries) if (e.Phase != OverlayPhase.Closing) return true; return false; } }
    public bool AnyClosing { get { foreach (var e in Entries) if (e.Phase == OverlayPhase.Closing) return true; return false; } }
    public bool AnyModal { get { foreach (var e in Entries) if (e.Phase != OverlayPhase.Closing && e.DismissBehavior == DismissBehavior.Modal) return true; return false; } }
    public bool AnyModalVisual { get { foreach (var e in Entries) if (e.DismissBehavior == DismissBehavior.Modal) return true; return false; } }
    public bool TopCanLightDismiss
    {
        get
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (Entries[i].Phase != OverlayPhase.Closing) return Entries[i].DismissBehavior == DismissBehavior.LightDismiss;
            return false;
        }
    }

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
        => Open(anchor, content, placement, default);

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options)
    {
        var handle = new OverlayHandle { IsOpen = true };
        var entry = new OverlayEntry
        {
            Id = _nextId++, Anchor = anchor, Content = content, Placement = placement, Handle = handle,
            FocusTrap = options.FocusTrap,
            DismissBehavior = options.DismissBehavior,
            Chrome = options.Chrome,
            Phase = OverlayPhase.Opening,
            SavedFocus = GetFocus?.Invoke() ?? NodeHandle.Null,   // capture pre-open focus → restore on close
        };
        handle.CloseAction = cause => BeginClose(entry, cause);
        Entries.Add(entry);
        Bump();
        return handle;
    }

    public void CloseTop()
        => CloseTop(OverlayCloseCause.Programmatic);

    internal void CloseTop(OverlayCloseCause cause)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Phase != OverlayPhase.Closing) { BeginClose(Entries[i], cause); return; }
    }

    public void CloseAll()
    {
        foreach (var e in Entries) BeginClose(e, OverlayCloseCause.Programmatic);
    }

    // Start the close animation: keep the entry (so it stays on top and fades), mark it closing; the host seeds the
    // fade-out and OverlayCloseDriver removes it once the animation settles.
    private void BeginClose(OverlayEntry e, OverlayCloseCause cause)
    {
        if (e.Phase == OverlayPhase.Closing) return;
        if (e.Handle.ClosingAction is { } beforeClose && !beforeClose(cause)) return;
        e.CloseCause = cause;
        e.Phase = OverlayPhase.Closing;
        e.Handle.IsOpen = false;
        SeedCloseIfNeeded(e);
        Bump();
    }

    internal void SeedCloseIfNeeded(OverlayEntry e)
    {
        if (e.CloseSeeded) return;
        if (Scene is not { } scene || Anim is not { } anim) return;
        if (e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode)) return;

        e.CloseSeeded = true;
        ref NodePaint surfacePaint = ref scene.Paint(e.SurfaceNode);
        float currentScaleX = surfacePaint.LocalTransform.M11;
        float currentScaleY = surfacePaint.LocalTransform.M22;
        float currentOpacity = surfacePaint.Opacity;

        if (e.Chrome == PopupChrome.TeachingTip && e.MeasuredW > 0f && e.MeasuredH > 0f)
        {
            float sx = 20f / e.MeasuredW;
            float sy = 20f / e.MeasuredH;
            var ease = EasingSpec.CubicBezier(0.7f, 0.0f, 1.0f, 0.5f);
            anim.Animate(e.SurfaceNode, AnimChannel.ScaleX, currentScaleX, sx, 200f, ease);
            anim.Animate(e.SurfaceNode, AnimChannel.ScaleY, currentScaleY, sy, 200f, ease);
            anim.Animate(e.SurfaceNode, AnimChannel.Opacity, currentOpacity, 0f, OpacityMs, Easing.Linear);
        }
        else if (e.Chrome == PopupChrome.Modal)
        {
            anim.Animate(e.SurfaceNode, AnimChannel.ScaleX, currentScaleX, 1.05f, 167f, Easing.FluentPopOpen);
            anim.Animate(e.SurfaceNode, AnimChannel.ScaleY, currentScaleY, 1.05f, 167f, Easing.FluentPopOpen);
            anim.Animate(e.SurfaceNode, AnimChannel.Opacity, currentOpacity, 0f, OpacityMs, Easing.Linear);
            if (!ScrimNode.IsNull && scene.IsLive(ScrimNode))
            {
                float scrimOpacity = scene.Paint(ScrimNode).Opacity;
                anim.Animate(ScrimNode, AnimChannel.Opacity, scrimOpacity, 0f, OpacityMs, Easing.Linear);
            }
        }
        else
        {
            anim.Cancel(e.SurfaceNode, AnimChannel.SizeH);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipL);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipT);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipR);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipB);
            surfacePaint.ClipRect = RectF.Infinite;
            scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
            anim.Animate(e.SurfaceNode, AnimChannel.Opacity, currentOpacity, 0f, OpacityMs, Easing.Linear);
        }
    }

    /// <summary>Host phase 7.1: remove closing entries whose retained tracks have settled, before recording the frame.</summary>
    public void AfterAnimations()
    {
        if (Scene is not { } scene || Anim is not { } anim) return;
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Phase != OverlayPhase.Closing || !e.CloseSeeded) continue;
            bool surfaceSettled = e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode) || !anim.HasTracks(e.SurfaceNode);
            bool scrimSettled = e.Chrome != PopupChrome.Modal || ScrimNode.IsNull || !scene.IsLive(ScrimNode) || !anim.HasTracks(ScrimNode);
            if (surfaceSettled && scrimSettled) Finalize(e);
        }
    }

    public void Finalize(OverlayEntry e)
    {
        if (Scene is { } scene)
        {
            // Finalization runs after AnimEngine.Tick but before record. The OverlayHost render that removes the logical
            // layer is scheduled for the NEXT frame, so the retained nodes can still be attached for one record pass.
            // Make the whole retained wrapper inert immediately (not just Surface opacity): otherwise a stale child write
            // or late channel composition can visibly "pop" the closed dialog/flyout back for a frame.
            if (!e.WrapperNode.IsNull && scene.IsLive(e.WrapperNode))
                HideSubtreeAndCancel(scene, Anim, e.WrapperNode);
            else if (!e.SurfaceNode.IsNull && scene.IsLive(e.SurfaceNode))
                HideSubtreeAndCancel(scene, Anim, e.SurfaceNode);
        }
        if (!Entries.Remove(e)) return;
        if (Entries.Count == 0 && Scene is { } sc && !ScrimNode.IsNull && sc.IsLive(ScrimNode))
        {
            Anim?.CancelAll(ScrimNode);
            sc.Paint(ScrimNode).Opacity = 0f;
            sc.Unmark(ScrimNode, NodeFlags.Visible | NodeFlags.HitTestVisible);
            sc.Mark(ScrimNode, NodeFlags.PaintDirty);
        }
        e.Handle.ClosedAction?.Invoke();
        e.Handle.ClosedWithCauseAction?.Invoke(e.CloseCause);
        // Restore the focus captured at open time, once nothing is left open (WinUI flyout focus-restoration).
        if (!AnyOpen && !e.SavedFocus.IsNull) RestoreFocus?.Invoke(e.SavedFocus);
        Bump();
    }

    private static void HideSubtreeAndCancel(SceneStore scene, AnimEngine? anim, NodeHandle node)
    {
        if (node.IsNull || !scene.IsLive(node)) return;
        anim?.CancelAll(node);
        scene.Paint(node).Opacity = 0f;
        scene.Unmark(node, NodeFlags.Visible | NodeFlags.HitTestVisible);
        scene.Mark(node, NodeFlags.PaintDirty);
        for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            HideSubtreeAndCancel(scene, anim, c);
    }

    /// <summary>Pre-focus key preview wired into the dispatcher via the InputHooks ambient: Escape closes the top overlay.</summary>
    public bool PreviewKey(int key)
    {
        if (key == Keys.Escape && AnyOpen) { CloseTop(OverlayCloseCause.Escape); return true; }
        return false;
    }

    private void Bump() => _version.Value = _version.Peek() + 1;
}

/// <summary>
/// Wraps the app root and hosts anchored flyouts in a top-level ZStack.
/// The base <see cref="Child"/> is always the first ZStack child (so opening/closing an overlay never remounts it).
/// Open mirrors WinUI MenuPopupThemeTransition: a top-anchored SizeH clip-reveal of the presenter (content full size,
/// revealed from the anchor edge) + a fade (250ms cubic-bezier(0,0,0,1); 83ms linear opacity). Close is an 83ms fade.
/// </summary>
public sealed class OverlayHost : Component
{
    public Element Child = new BoxEl();

    // WinUI MenuPopupThemeTransition timings.
    const float OpenMs = 250f, OpacityMs = 83f, ClosedRatio = 0.5f;

    public override Element Render()
    {
        var version = UseSignal(0);
        var svcRef = UseRef<OverlayServiceImpl?>(null);
        svcRef.Value ??= new OverlayServiceImpl(version);
        var svc = svcRef.Value;
        svc.Scene = Context.Scene;
        svc.Anim = Context.Anim;
        int ver = version.Value;   // subscribe → re-render when overlays open/close

        var hooks = UseContext(InputHooks.Current);
        hooks.KeyPreview = svc.PreviewKey;
        hooks.SetAfterAnimations(svc, svc.AfterAnimations);
        svc.GetFocus = hooks.GetFocus;          // host-wired focus get/restore → flyout focus-restoration on close
        svc.RestoreFocus = hooks.RestoreFocus;
        var vp = UseContext(Viewport.Size);

        // After layout (Bounds valid): place each popup, then seed its open (clip-unfold + fade) / close (fade) animation.
        UseLayoutEffect(() =>
        {
            var scene = Context.Scene;
            var anim = Context.Anim;
            if (scene is null || anim is null) return;
            foreach (var e in svc.Entries)
            {
                if (e.WrapperNode.IsNull || !scene.IsLive(e.WrapperNode)) continue;
                if (e.Chrome != PopupChrome.Modal)
                {
                    var anchor = e.Anchor();
                    if (!anchor.IsNull && scene.IsLive(anchor))
                    {
                        var aRect = scene.AbsoluteRect(anchor);
                        var pRect = scene.AbsoluteRect(e.WrapperNode);
                        e.MeasuredW = pRect.W;
                        e.MeasuredH = pRect.H;
                        var place = FlyoutPositioner.Place(in aRect, new Size2(pRect.W, pRect.H), in vp, e.Placement);
                        e.OpensUp = place.OpensUp;
                        e.CornerJoin = place.CornerJoin;
                        e.PlacementInfo.Value = new OverlayPlacementInfo(
                            aRect.X + aRect.W * 0.5f - place.X,
                            aRect.Y + aRect.H * 0.5f - place.Y,
                            pRect.W,
                            pRect.H,
                            place.OpensUp);
                        ref NodePaint wp = ref scene.Paint(e.WrapperNode);
                        wp.LocalTransform = Affine2D.Translation(place.X, place.Y);
                        scene.Mark(e.WrapperNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                        if (!e.SurfaceNode.IsNull && scene.IsLive(e.SurfaceNode) && e.Chrome == PopupChrome.Flyout)
                        {
                            scene.Paint(e.SurfaceNode).Corners = FlyoutSurface.CornersFor(e.CornerJoin);
                            scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
                        }
                    }
                }

                if (e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode)) continue;
                if (e.Phase == OverlayPhase.Closing) { svc.SeedCloseIfNeeded(e); continue; }

                if (!e.OpenSeeded && e.Chrome == PopupChrome.TeachingTip && e.MeasuredW > 0f && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    float sx = MathF.Min(0.01f, 20f / e.MeasuredW);
                    float sy = MathF.Min(0.01f, 20f / e.MeasuredH);
                    var ease = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);
                    anim.Animate(e.SurfaceNode, AnimChannel.ScaleX, sx, 1f, 300f, ease);
                    anim.Animate(e.SurfaceNode, AnimChannel.ScaleY, sy, 1f, 300f, ease);
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, 0f, 1f, 83f, Easing.Linear);
                }
                else if (!e.OpenSeeded && e.Chrome == PopupChrome.Modal)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    if (!svc.ScrimNode.IsNull && scene.IsLive(svc.ScrimNode))
                    {
                        anim.Animate(svc.ScrimNode, AnimChannel.Opacity, 0f, 1f, OpacityMs, Easing.Linear);
                    }
                    anim.Animate(e.SurfaceNode, AnimChannel.ScaleX, 1.05f, 1f, OpenMs, Easing.FluentPopOpen);
                    anim.Animate(e.SurfaceNode, AnimChannel.ScaleY, 1.05f, 1f, OpenMs, Easing.FluentPopOpen);
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, 0f, 1f, OpacityMs, Easing.Linear);
                }
                else if (!e.OpenSeeded && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    float fullH = e.MeasuredH;
                    // WinUI MenuPopupThemeTransition: the presenter clips open from the anchor edge — content stays full size
                    // and is revealed top-down — plus a quick fade. A single top-anchored SizeH reveal on the surface drives
                    // the acrylic + border + clipped content + shadow together; no per-node translate/scale to desync or glitch.
                    if (e.OpensUp)
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipT, fullH * (1f - ClosedRatio), 0f, OpenMs, Easing.FluentPopOpen);
                    else
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipB, fullH * ClosedRatio, fullH, OpenMs, Easing.FluentPopOpen);
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, 0f, 1f, OpacityMs, Easing.Linear);
                }
            }
        }, ver, vp.Width, vp.Height);

        // Keep the layer structure index-stable so the reconciler never rebuilds a popup mid-animation: Child is always
        // index 0; while any entry exists the scrim sits at index 1 (its interactivity toggles with AnyOpen, but it stays
        // present during a close fade); popups follow; the close driver is appended last (so it never shifts a popup).
        var layers = new List<Element> { Child };
        if (svc.Entries.Count > 0)
        {
            bool modal = svc.AnyModal;
            bool modalVisual = svc.AnyModalVisual;
            bool lightDismiss = svc.TopCanLightDismiss;
            ColorF scrimFill = modalVisual ? Tok.FillSmoke : ColorF.Transparent;
            layers.Add(new BoxEl
            {
                Width = vp.Width,
                Height = vp.Height,
                Grow = 1,
                Fill = scrimFill,
                HoverFill = scrimFill,
                PressedFill = scrimFill,
                Opacity = 1f,
                OnRealized = h => svc.ScrimNode = h,
                HitTestVisible = svc.AnyOpen,
                OnClick = lightDismiss ? () => svc.CloseTop(OverlayCloseCause.LightDismiss) : (svc.AnyOpen ? () => { } : null),
                OnPointerDown = svc.AnyOpen && !lightDismiss ? _ => { } : null,
            });
            foreach (var e in svc.Entries)
                layers.Add(Positioned(e) with { Key = "overlay:" + e.Id });
        }

        // Pin the overlay stack to the viewport so a tall popup (a flyout/menu is a POPOUT by design) can't grow the stack
        // and re-flow the page underneath — MeasureZStack otherwise sizes to the tallest child, which would resize the app
        // shell and reset the nav scroll. The popup still floats/overflows freely; it just no longer affects the layout.
        return Ctx.Provide(Overlay.Service, (IOverlayService)svc,
            Ui.ZStack(layers.ToArray()) with { Grow = 1, Width = vp.Width, Height = vp.Height });
    }

    static BoxEl Positioned(OverlayEntry e) => e.Chrome == PopupChrome.Modal ? PositionedModal(e) : PositionedAnchored(e);

    static BoxEl PositionedModal(OverlayEntry e) => new BoxEl
    {
        Width = float.NaN,
        Height = float.NaN,
        Grow = 1,
        Direction = 1,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        OnRealized = h => e.WrapperNode = h,
        Children =
        [
            Embed.Comp(() => new FlyoutSurface
            {
                Body = e.Content(),
                Chrome = e.Chrome,
                OnSurface = h => e.SurfaceNode = h,
            }),
        ],
    };

    // A full-bleed positioning host so the popup lays out at its own (0,0); the layout effect translates the wrapper.
    static BoxEl PositionedAnchored(OverlayEntry e) => new BoxEl
    {
        Grow = 1,
        Direction = 1,
        AlignItems = FlexAlign.Start,
        Justify = FlexJustify.Start,
        Children =
        [
            new BoxEl
            {
                Direction = 1,
                AlignSelf = FlexAlign.Start,
                OnRealized = h => e.WrapperNode = h,
                Children =
                [
                    Embed.Comp(() => new FlyoutSurface
                    {
                        Body = Ctx.Provide(Overlay.Placement, e.PlacementInfo, e.Content()),
                        Chrome = e.Chrome,
                        OnSurface = h => e.SurfaceNode = h,
                    }),
                ],
            },
        ],
    };
}

/// <summary>WinUI MenuFlyoutPresenter surface: a transparent presenter over a frosted acrylic backdrop, a 1px flyout
/// stroke, a soft elevation shadow, and rounded corners. The OverlayHost drives the open clip-reveal (SizeH) + fade on
/// this single node; ClipToBounds clips the content to both the rounded corners and the revealing height.</summary>
internal sealed class FlyoutSurface : Component
{
    public Element Body = new BoxEl();
    public PopupChrome Chrome = PopupChrome.Flyout;
    public Action<NodeHandle>? OnSurface;

    public static CornerRadius4 CornersFor(CornerJoin join) => join switch
    {
        CornerJoin.Top => new CornerRadius4(0f, 0f, Radii.Overlay, Radii.Overlay),
        CornerJoin.Bottom => new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f),
        _ => Radii.OverlayAll,
    };

    public override Element Render()
    {
        if (Chrome == PopupChrome.Raw || Chrome == PopupChrome.Modal || Chrome == PopupChrome.TeachingTip)
        {
            bool modal = Chrome == PopupChrome.Modal;
            return new BoxEl
            {
                Direction = 1,
                AlignSelf = modal ? FlexAlign.Center : FlexAlign.Start,
                AlignItems = modal ? FlexAlign.Center : FlexAlign.Stretch,
                Fill = ColorF.Transparent,
                ClipToBounds = true,
                TransformOriginX = 0.5f,
                TransformOriginY = 0.5f,
                OnRealized = h => OnSurface?.Invoke(h),
                Children = [Body],
            };
        }

        return new BoxEl
    {
        Direction = 1,
        AlignSelf = FlexAlign.Start,
        Fill = ColorF.Transparent,
        Acrylic = AcrylicSpec.Flyout,
        BorderColor = Tok.StrokeFlyoutDefault,
        BorderWidth = 1f,
        Corners = Radii.OverlayAll,
        Shadow = Elevation.Flyout,
        ClipToBounds = true,
        Padding = new Edges4(0, 2, 0, 2),   // MenuFlyoutPresenterThemePadding
        OnRealized = h => OnSurface?.Invoke(h),
        Children = [Body],
    };
    }
}
