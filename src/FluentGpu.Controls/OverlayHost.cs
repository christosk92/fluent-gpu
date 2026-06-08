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
    internal Action? CloseAction;
    public bool IsOpen { get; internal set; }
    public void Close() { if (IsOpen) CloseAction?.Invoke(); }
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

/// <summary>Optional popup behavior. <see cref="FocusTrap"/> keeps Tab/Shift-Tab inside the overlay subtree (modal-style).</summary>
public readonly record struct PopupOptions(bool FocusTrap = false, DismissBehavior DismissBehavior = DismissBehavior.LightDismiss);

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
}

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
    public float MeasuredH;
    public bool OpensUp;
    public CornerJoin CornerJoin;     // which popup corners abut the anchor (corner-squaring for ComboBox/AutoSuggestBox)
    public NodeHandle SavedFocus;     // focus captured at open time → restored when the overlay finishes closing
    public bool FocusTrap;
    public bool OpenSeeded;
    public bool Closing;
    public bool CloseSeeded;
}

internal sealed class OverlayServiceImpl : IOverlayService
{
    private readonly Signal<int> _version;     // bump → OverlayHost re-renders
    private int _nextId;
    public readonly List<OverlayEntry> Entries = new(4);

    // Host-wired (via the InputHooks ambient): read/restore the focused node for WinUI flyout focus-restoration.
    public Func<NodeHandle>? GetFocus;
    public Action<NodeHandle>? RestoreFocus;

    public OverlayServiceImpl(Signal<int> version) => _version = version;

    public bool AnyOpen { get { foreach (var e in Entries) if (!e.Closing) return true; return false; } }
    public bool AnyClosing { get { foreach (var e in Entries) if (e.Closing) return true; return false; } }

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
        => Open(anchor, content, placement, default);

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options)
    {
        var handle = new OverlayHandle { IsOpen = true };
        var entry = new OverlayEntry
        {
            Id = _nextId++, Anchor = anchor, Content = content, Placement = placement, Handle = handle,
            FocusTrap = options.FocusTrap,
            SavedFocus = GetFocus?.Invoke() ?? NodeHandle.Null,   // capture pre-open focus → restore on close
        };
        handle.CloseAction = () => BeginClose(entry);
        Entries.Add(entry);
        Bump();
        return handle;
    }

    public void CloseTop()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (!Entries[i].Closing) { BeginClose(Entries[i]); return; }
    }

    public void CloseAll()
    {
        foreach (var e in Entries) BeginClose(e);
    }

    // Start the close animation: keep the entry (so it stays on top and fades), mark it closing; the host seeds the
    // fade-out and OverlayCloseDriver removes it once the animation settles.
    private void BeginClose(OverlayEntry e)
    {
        if (e.Closing) return;
        e.Closing = true;
        e.Handle.IsOpen = false;
        Bump();
    }

    public void Finalize(OverlayEntry e)
    {
        if (!Entries.Remove(e)) return;
        // Restore the focus captured at open time, once nothing is left open (WinUI flyout focus-restoration).
        if (!AnyOpen && !e.SavedFocus.IsNull) RestoreFocus?.Invoke(e.SavedFocus);
        Bump();
    }

    /// <summary>Pre-focus key preview wired into the dispatcher via the InputHooks ambient: Escape closes the top overlay.</summary>
    public bool PreviewKey(int key)
    {
        if (key == Keys.Escape && AnyOpen) { CloseTop(); return true; }
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
        int ver = version.Value;   // subscribe → re-render when overlays open/close

        var hooks = UseContext(InputHooks.Current);
        hooks.KeyPreview = svc.PreviewKey;
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
                var anchor = e.Anchor();
                if (!anchor.IsNull && scene.IsLive(anchor))
                {
                    var aRect = scene.AbsoluteRect(anchor);
                    var pRect = scene.AbsoluteRect(e.WrapperNode);
                    e.MeasuredH = pRect.H;
                    var place = FlyoutPositioner.Place(in aRect, new Size2(pRect.W, pRect.H), in vp, e.Placement);
                    e.OpensUp = place.OpensUp;
                    e.CornerJoin = place.CornerJoin;
                    ref NodePaint wp = ref scene.Paint(e.WrapperNode);
                    wp.LocalTransform = Affine2D.Translation(place.X, place.Y);
                    scene.Mark(e.WrapperNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                }

                if (e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode)) continue;

                if (!e.OpenSeeded && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    float fullH = e.MeasuredH;
                    // WinUI MenuPopupThemeTransition: the presenter clips open from the anchor edge — content stays full size
                    // and is revealed top-down — plus a quick fade. A single top-anchored SizeH reveal on the surface drives
                    // the acrylic + border + clipped content + shadow together; no per-node translate/scale to desync or glitch.
                    anim.Animate(e.SurfaceNode, AnimChannel.SizeH, fullH * ClosedRatio, fullH, OpenMs, Easing.FluentPopOpen);
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, 0f, 1f, OpacityMs, Easing.Linear);
                }
                if (e.Closing && !e.CloseSeeded)
                {
                    e.CloseSeeded = true;
                    anim.Cancel(e.SurfaceNode, AnimChannel.SizeH);   // freeze the reveal; close is a pure 83ms fade (WinUI)
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, scene.Paint(e.SurfaceNode).Opacity, 0f, OpacityMs, Easing.Linear);
                }
            }
        }, ver, vp.Width, vp.Height);

        // Keep the layer structure index-stable so the reconciler never rebuilds a popup mid-animation: Child is always
        // index 0; while any entry exists the scrim sits at index 1 (its interactivity toggles with AnyOpen, but it stays
        // present during a close fade); popups follow; the close driver is appended last (so it never shifts a popup).
        var layers = new List<Element> { Child };
        if (svc.Entries.Count > 0)
        {
            layers.Add(new BoxEl { Grow = 1, Fill = ColorF.Transparent, HitTestVisible = svc.AnyOpen, OnClick = svc.AnyOpen ? svc.CloseTop : null });
            foreach (var e in svc.Entries)
                layers.Add(Positioned(e));
            if (svc.AnyClosing)   // poll each frame to remove popups whose close animation has settled (kept on top, no orphan)
                layers.Add(Embed.Comp(() => new OverlayCloseDriver { Svc = svc }));
        }

        // Pin the overlay stack to the viewport so a tall popup (a flyout/menu is a POPOUT by design) can't grow the stack
        // and re-flow the page underneath — MeasureZStack otherwise sizes to the tallest child, which would resize the app
        // shell and reset the nav scroll. The popup still floats/overflows freely; it just no longer affects the layout.
        return Ctx.Provide(Overlay.Service, (IOverlayService)svc,
            Ui.ZStack(layers.ToArray()) with { Grow = 1, Width = vp.Width, Height = vp.Height });
    }

    // A full-bleed positioning host so the popup lays out at its own (0,0); the layout effect translates the wrapper.
    static Element Positioned(OverlayEntry e) => new BoxEl
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
                        Body = e.Content(),
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
    public Action<NodeHandle>? OnSurface;

    public override Element Render() => new BoxEl
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

/// <summary>Invisible per-frame poller, mounted only while a popup is closing: subscribes to the host frame clock and
/// removes each closing popup once its fade animation has settled. Unmounts (stopping the per-frame wake) when none remain.</summary>
internal sealed class OverlayCloseDriver : Component
{
    public required OverlayServiceImpl Svc;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (during a close)
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null) return;
            for (int i = Svc.Entries.Count - 1; i >= 0; i--)
            {
                var e = Svc.Entries[i];
                if (e.Closing && e.CloseSeeded && (e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode) || !anim.HasTracks(e.SurfaceNode)))
                    Svc.Finalize(e);
            }
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}
