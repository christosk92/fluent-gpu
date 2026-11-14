using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// The room a popup has where its overlay places it: the one rule every scrolling popup body (MenuFlyoutPresenter's list,
/// CommandBarFlyout's overflow, an app's own menu) caps its height by, so a menu taller than a short window scrolls inside
/// the space it can be shown in instead of running past the window's foot. WinUI sizes a flyout presenter to the popup's
/// available height the same way.
/// <para>The space is the container <see cref="FlyoutPositioner"/> places the popup in: the monitor work area while the popup
/// may still lease an OS window to leave the window (it does, when it does not fit), else the window's root bounds
/// (constrained, refused, in-window by fit, or windowed only for its material, which keeps the in-window geometry); less the
/// positioner's margin at both ends and the plate's own padding (the plate's ring is drawn, it takes no layout). What the
/// caller stacks above or beside its scrolling part (a header, a command row) is the caller's to leave room for, ideally by
/// capping its whole column with this and letting the scrolling part shrink.</para>
/// <para>Read it where the body is built and again whenever the placement changes (subscribe to the
/// <see cref="Overlay.Placement"/> signal in an effect): the first measured placement decides window or not, and a resize
/// re-places the popup.</para>
/// </summary>
public static class PopupRoom
{
    /// <summary>The height the content inside the popup's plate may take, in DIP; <see cref="float.PositiveInfinity"/> when it
    /// cannot be known (no overlay host, or the body is not inside an overlay entry). UI thread; reads, never subscribes.</summary>
    /// <param name="svc">The overlay service (<c>UseContext(Overlay.Service)</c>).</param>
    /// <param name="placement">The entry's placement signal (<c>UseContext(Overlay.Placement)</c>): it names the entry.</param>
    /// <param name="plate">The plate the body's chrome draws when the entry has not reported one yet; null = the Fluent plate.</param>
    public static float Height(IOverlayService svc, Signal<OverlayPlacementInfo>? placement, PopupPlate? plate = null)
    {
        if (svc is not OverlayServiceImpl impl || placement is null) return float.PositiveInfinity;
        OverlayEntry? entry = null;
        foreach (var e in impl.Entries) if (ReferenceEquals(e.PlacementInfo, placement)) { entry = e; break; }
        if (entry is null) return float.PositiveInfinity;
        RectF container = impl.ViewportRect;
        bool mayLeave = !entry.ConstrainToRootBounds && entry.Chrome is PopupChrome.Flyout or PopupChrome.CommandBar
                        && impl.Hooks is { OpenPopupWindow: not null } && !entry.PopupWindowRefused && !entry.InWindowByFit
                        && !entry.WindowForMaterial;
        if (mayLeave && impl.Hooks!.GetWorkArea is { } workArea)
        {
            var a = entry.LastAnchorRect.IsEmpty ? container : entry.LastAnchorRect;
            container = workArea(new Point2(a.X + a.W * 0.5f, a.Y + a.H * 0.5f));
        }
        if (container.IsEmpty || !float.IsFinite(container.H)) return float.PositiveInfinity;
        var p = entry.PlateStyle ?? plate ?? PopupPlate.Fluent();
        return MathF.Max(0f, container.H - 2f * FlyoutPositioner.FlyoutMargin - p.Padding.Top - p.Padding.Bottom);
    }
}

/// <summary>A popup body's column (a header, a strip, then a scrolling list) capped at the room its placement has
/// (<see cref="PopupRoom.Height"/>), re-read on every placement: the column keeps the parts above the list at their size and the
/// list gives way (its scroller shrinks), so the list scrolls inside the window. ContextMenu's header-and-strip shape.</summary>
internal sealed class PopupRoomColumn : Component
{
    public override Element Render()
    {
        var column = UseProps<BoxEl>();   // the column, re-pushed (Embed.Comp(column, () => new PopupRoomColumn()))
        var svc = UseContext(Overlay.Service);
        var placement = UseContext(Overlay.Placement);
        var room = UseSignal(PopupRoom.Height(svc, placement));
        UseEffect(() =>
        {
            _ = placement?.Value;
            float h = PopupRoom.Height(svc, placement), was = room.Peek();
            if (h != was && !(MathF.Abs(h - was) < 0.5f)) room.Value = h;
        });
        float now = room.Value;
        return column with { MaxHeight = float.IsFinite(now) ? now : float.NaN };
    }
}

/// <summary>Keyboard roving in a popup's scrolling list: keep the row the cursor moved to inside its scroller's viewport (the
/// minimal scroll, WinUI's default BringIntoViewOptions), so Down in a list capped by <see cref="PopupRoom"/> reaches the last
/// row and shows it. A row with no scrolling ancestor, or already in view, moves nothing. UI thread.</summary>
internal static class ScrollReveal
{
    public static void Row(FluentGpu.Scene.SceneStore? scene, NodeHandle row)
    {
        if (scene is null || row.IsNull || !scene.IsLive(row)) return;
        NodeHandle vp = scene.Parent(row);
        while (!vp.IsNull && !scene.HasScroll(vp)) vp = scene.Parent(vp);
        if (vp.IsNull) return;
        ref var sc = ref scene.ScrollRef(vp);
        if (sc.Orientation != 0 || sc.ViewportH <= 0f) return;
        var r = scene.AbsoluteRect(row);
        float top = r.Y - scene.AbsoluteRect(vp).Y;   // the row's top in the viewport (scroll translation included)
        float offset = sc.OffsetY, target;
        if (top < 0f) target = offset + top;
        else if (top + r.H > sc.ViewportH) target = offset + top + r.H - sc.ViewportH;
        else return;
        scene.ScrollHandleFor(vp)?.ScrollTo(target, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
    }
}
