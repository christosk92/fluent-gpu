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
/// positions the popup relative to the anchor's on-screen rect (flip/nudge into the viewport). Resolve via
/// <c>UseContext(Overlay.Service)</c> inside an <see cref="OverlayHost"/>.
/// </summary>
public interface IOverlayService
{
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft);
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
    public void CloseTop() { }
    public void CloseAll() { }
    public bool AnyOpen => false;
}

internal sealed class OverlayEntry
{
    public required Func<NodeHandle> Anchor;
    public required Func<Element> Content;
    public FlyoutPlacement Placement;
    public required OverlayHandle Handle;
    public NodeHandle PopupNode;   // captured at realize → measured for placement
}

internal sealed class OverlayServiceImpl : IOverlayService
{
    private readonly Signal<int> _version;     // bump → OverlayHost re-renders
    public readonly List<OverlayEntry> Entries = new(4);

    public OverlayServiceImpl(Signal<int> version) => _version = version;

    public bool AnyOpen => Entries.Count > 0;

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
    {
        var handle = new OverlayHandle { IsOpen = true };
        var entry = new OverlayEntry { Anchor = anchor, Content = content, Placement = placement, Handle = handle };
        handle.CloseAction = () => Remove(entry);
        Entries.Add(entry);
        Bump();
        return handle;
    }

    public void CloseTop()
    {
        if (Entries.Count == 0) return;
        Remove(Entries[^1]);
    }

    public void CloseAll()
    {
        if (Entries.Count == 0) return;
        foreach (var e in Entries) e.Handle.IsOpen = false;
        Entries.Clear();
        Bump();
    }

    private void Remove(OverlayEntry e)
    {
        int i = Entries.IndexOf(e);
        if (i < 0) return;
        e.Handle.IsOpen = false;
        Entries.RemoveAt(i);
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
/// Wraps the app root and hosts anchored flyouts in a top-level ZStack: <c>[Child, light-dismiss scrim, popups…]</c>.
/// The base <see cref="Child"/> is always the first ZStack child (so opening/closing an overlay never remounts it);
/// popups append as later siblings, so the deepest-first hit-test dismisses on an outside click and routes inside
/// clicks to the popup. Placement (flip/nudge) is applied in a layout effect once the popup is measured.
/// </summary>
public sealed class OverlayHost : Component
{
    public Element Child = new BoxEl();

    public override Element Render()
    {
        var version = UseSignal(0);
        var svcRef = UseRef<OverlayServiceImpl?>(null);
        svcRef.Value ??= new OverlayServiceImpl(version);
        var svc = svcRef.Value;
        int ver = version.Value;   // subscribe → re-render when overlays open/close

        // Register Escape interception while mounted (idempotent; the host bridges this to the input dispatcher).
        UseContext(InputHooks.Current).KeyPreview = svc.PreviewKey;

        var vp = UseContext(Viewport.Size);

        // Position every open popup after layout (Bounds valid): measure the popup, place relative to the anchor.
        UseLayoutEffect(() =>
        {
            var scene = Context.Scene;
            if (scene is null) return;
            foreach (var e in svc.Entries)
            {
                if (e.PopupNode.IsNull || !scene.IsLive(e.PopupNode)) continue;
                var anchor = e.Anchor();
                if (anchor.IsNull || !scene.IsLive(anchor)) continue;
                var aRect = scene.AbsoluteRect(anchor);
                var pRect = scene.AbsoluteRect(e.PopupNode);
                var (x, y) = FlyoutPositioner.Place(in aRect, new Size2(pRect.W, pRect.H), in vp, e.Placement);
                ref NodePaint p = ref scene.Paint(e.PopupNode);
                p.LocalTransform = Affine2D.Translation(x, y);
                scene.Mark(e.PopupNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            }
        }, ver, vp.Width, vp.Height);

        var layers = new List<Element> { Child };
        if (svc.AnyOpen)
        {
            // Transparent light-dismiss scrim: any click not on a popup hits this and closes the top overlay.
            layers.Add(new BoxEl { Grow = 1, Fill = ColorF.Transparent, OnClick = svc.CloseTop });
            foreach (var e in svc.Entries)
                layers.Add(Positioned(e));
        }

        return Ctx.Provide(Overlay.Service, (IOverlayService)svc, Ui.ZStack(layers.ToArray()) with { Grow = 1 });
    }

    // A full-bleed positioning host so the popup lays out at its own (0,0); the layout effect then translates it. The
    // host has no handler, so clicks outside the popup fall through to the scrim.
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
                Fill = Tok.FillLayerDefault,
                BorderColor = Tok.StrokeSurfaceDefault,
                BorderWidth = 1f,
                Corners = Radii.OverlayAll,
                Shadow = Elevation.Flyout,
                ClipToBounds = true,
                OnRealized = h => e.PopupNode = h,
                Children = [e.Content()],
            },
        ],
    };
}
