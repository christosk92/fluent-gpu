using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A live overlay; call <see cref="Close"/> to dismiss it.</summary>
public sealed class OverlayHandle
{
    internal Action<OverlayCloseCause>? CloseAction;
    public Action? ClosedAction;
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
/// <summary>How a popup dismisses. <see cref="LightDismiss"/> = click-outside + Escape + window deactivation (the
/// default); <see cref="Modal"/> = no light dismiss (ContentDialog — survives window deactivation); <see cref="None"/>
/// = caller-controlled only (ToolTip).</summary>
public enum DismissBehavior : byte { LightDismiss, Modal, None }

/// <summary>Where an overlay close request came from; controls map this to their public close reason.</summary>
public enum OverlayCloseCause : byte { Programmatic, LightDismiss, Escape }

/// <summary>Which presenter chrome + open/close MOTION the overlay host supplies around popup content. Each kind maps
/// 1:1 to the WinUI transition family that owns that surface:
/// <list type="bullet">
/// <item><see cref="Flyout"/> — menus (MenuFlyout/DropDownButton/SplitButton/MenuBar/context): MenuPopupThemeTransition
/// (clip-reveal + content translate 250ms cubic-bezier(0,0,0,1); close = 83ms linear fade) — the default.</item>
/// <item><see cref="Popup"/> — the WinUI <c>Flyout</c>/FlyoutPresenter (FlyoutBase attaches PopupThemeTransition,
/// FlyoutBase_Partial.cpp:1968–1975): OS-PVL TAS_SHOWPOPUP/TAS_HIDEPOPUP (slide ±50 + delayed fade; close 83ms fade).</item>
/// <item><see cref="Dropdown"/> — the ComboBox dropdown: SplitOpen/SplitCloseThemeAnimation (generic.xaml:9047/9056) —
/// clip reveal 250ms with NO content translate and NO open fade; close = 167ms clip collapse + late 83ms fade. With
/// <see cref="PopupOptions.SeamOffsetY"/> set, the band is centred on the SEAM (selected row) and grows/shrinks both
/// ways (WinUI OffsetFromCenter); otherwise it reveals from the anchor edge.</item>
/// <item><see cref="Raw"/> — ToolTip/Slider value tip: FadeIn/FadeOutThemeAnimation (TAS_FADEIN/TAS_FADEOUT, 167ms linear).</item>
/// <item><see cref="Modal"/> — ContentDialog scale/fade; <see cref="TeachingTip"/> — muxc expand/contract scale.</item>
/// <item><see cref="Static"/> — a bare WinUI <c>Popup</c> with no transitions (the AutoSuggestBox SuggestionsPopup,
/// generic.xaml AutoSuggestBox template — no TransitionCollection and AutoSuggestBox_Partial.cpp adds none): instant.</item>
/// </list></summary>
public enum PopupChrome : byte { Flyout, Raw, Modal, TeachingTip, Popup, Dropdown, Static, CommandBar }

/// <summary>Optional popup behavior. <see cref="FocusTrap"/> keeps Tab/Shift-Tab inside the overlay subtree (modal-style).</summary>
public readonly record struct PopupOptions(
    bool FocusTrap = false,
    DismissBehavior DismissBehavior = DismissBehavior.LightDismiss,
    PopupChrome Chrome = PopupChrome.Flyout)
{
    private readonly bool _unconstrained;

    /// <summary>
    /// WinUI <c>Popup.ShouldConstrainToRootBounds</c> (Popup_Partial.cpp:951-970: read-only false for WINDOWED popups;
    /// FlyoutBase_Partial.cpp:3181-3205 <c>SetIsWindowedPopup</c>). Default TRUE = the popup is clamped inside the
    /// window (today's behavior). Set FALSE where WinUI windows its popups (menus, ComboBox tall dropdowns,
    /// CommandBarFlyout): the host then asks the platform for a top-level popup window, places against the MONITOR
    /// work area, and renders the subtree into the popup window's own swapchain. Falls back to constrained placement
    /// when the platform/host cannot create popup windows (WinUI's <c>DoesPlatformSupportWindowedPopup</c> gate).
    /// Stored inverted so <c>default(PopupOptions)</c> stays constrained.
    /// </summary>
    public bool ConstrainToRootBounds
    {
        get => !_unconstrained;
        init => _unconstrained = !value;
    }

    /// <summary>Dropdown (SplitOpen/SplitClose) seam: (selected-row centre Y) − (popup centre Y) in POPUP-LOCAL px —
    /// WinUI <c>SplitOpenThemeAnimation.OffsetFromCenter</c> (the ComboBox carousel centres the reveal on the selected
    /// row; ThemeAnimations.cpp:682 registers ClipTranslateY = OffsetFromCenter immediately). Null (the default) keeps
    /// the anchor-edge reveal. Only meaningful with <see cref="PopupChrome.Dropdown"/>.</summary>
    public float? SeamOffsetY { get; init; }

    /// <summary>WinUI <c>FlyoutBase.OverlayInputPassThroughElement</c> (FlyoutBase_Partial.cpp:3922-3938): pointer
    /// input over THIS node's rendered bounds bypasses the light-dismiss scrim while the popup is open — the MenuBar
    /// keeps hover-switching titles with a menu down (MenuBarItem.cpp:64-70 sets the bar as the pass-through).
    /// (The full FlyoutShowMode surface — Transient / TransientWithDismissOnPointerMoveAway 80px — remains open;
    /// tracked as engine item 31 in docs/plans/winui-parity-sweep.md.)</summary>
    public Func<NodeHandle>? PassThrough { get; init; }

    /// <summary>Post-placement horizontal nudge in px, MIRRORED when the positioner flips the popup to the anchor's
    /// other side: the cascading-menu 4px overlap onto its owner (CascadingMenuHelper.h:121
    /// <c>m_subMenuOverlapPixels = 4</c>; cpp:678 subMenuPosition.X += subItemWidth − 4 → pass −4 with
    /// <see cref="FlyoutPlacement.RightEdgeAlignedTop"/>).</summary>
    public float AnchorOffsetX { get; init; }
}

public interface IOverlayService
{
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft);
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options);

    /// <summary>Open anchored to an arbitrary window-DIP RECT instead of a scene node — pointer-relative placement
    /// (ToolTip PlacementMode.Mouse, context menus at the right-tap point). <paramref name="owner"/> (optional) is the
    /// logical owner node, used only to resolve the nested-overlay parent chain for cascade close.</summary>
    OverlayHandle OpenAt(Func<RectF> anchorRect, Func<Element> content, FlyoutPlacement placement, PopupOptions options = default, Func<NodeHandle>? owner = null);

    /// <summary>Open at a node-LOCAL point (a right-tap / context-menu point): builds the window-DIP anchor rect from
    /// the <paramref name="owner"/> node's absolute rect + <paramref name="local"/>, minus the positioner's
    /// <c>FlyoutMargin</c> so the presenter top-left lands ON the point (the ToolTip PlacementMode.Mouse precedent).
    /// <paramref name="owner"/> also feeds the nested-overlay parent chain (a context menu ON a flyout cascade-closes
    /// with it).</summary>
    OverlayHandle OpenAtLocal(Func<NodeHandle> owner, Point2 local, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomEdgeAlignedLeft, PopupOptions options = default);

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
    public OverlayHandle OpenAt(Func<RectF> r, Func<Element> c, FlyoutPlacement p, PopupOptions o = default, Func<NodeHandle>? owner = null) => new();
    public OverlayHandle OpenAtLocal(Func<NodeHandle> owner, Point2 local, Func<Element> c, FlyoutPlacement p = FlyoutPlacement.BottomEdgeAlignedLeft, PopupOptions o = default) => new();
    public void CloseTop() { }
    public void CloseAll() { }
    public bool AnyOpen => false;
}

internal sealed class OverlayEntry
{
    public int Id;
    public required Func<NodeHandle> Anchor;
    public Func<RectF>? AnchorRect;   // rect-anchored open (pointer placement) — wins over Anchor when set
    public required Func<Element> Content;
    public FlyoutPlacement Placement;
    public required OverlayHandle Handle;
    public NodeHandle WrapperNode;    // measured + translated for placement
    public NodeHandle SurfaceNode;    // presenter surface: the clip-reveal + translate + opacity animate this node
    public NodeHandle PlateNode;      // menu chrome plate (acrylic+stroke+shadow): the WinUI MenuFlyoutPresenterBorder ScaleY stretch animates this node
    public float? SeamOffsetY;        // Dropdown SplitOpen/SplitClose seam (PopupOptions.SeamOffsetY)
    public Func<NodeHandle>? PassThrough;   // WinUI OverlayInputPassThroughElement (PopupOptions.PassThrough)
    public float AnchorOffsetX;       // post-placement nudge, mirrored on flip (cascade 4px overlap)
    public float MeasuredW;
    public float MeasuredH;
    public RectF LastAnchorRect;      // anchor rect at last placement — the live-anchor follow re-places on drift
    public bool OpensUp;
    public CornerJoin CornerJoin;     // which popup corners abut the anchor (corner-squaring for ComboBox/AutoSuggestBox)
    public NodeHandle SavedFocus;     // focus captured at open time → restored when the close STARTS (WinUI timing)
    public bool FocusTrap;
    public bool ScopePushed;          // a dispatcher focus scope is live for this entry (popped at close start)
    public DismissBehavior DismissBehavior;
    public PopupChrome Chrome;
    public bool ConstrainToRootBounds = true;
    public int ParentId = -1;         // nested flyout chain: the entry whose subtree contains this entry's anchor
    public int PopupWindowToken = -1; // host popup-window lease for an out-of-bounds popup (-1 = none/in-window)
    public OverlayPhase Phase;
    public bool OpenSeeded;
    public bool CloseSeeded;
    public OverlayCloseCause CloseCause;
    public Signal<OverlayPlacementInfo> PlacementInfo = new(default);
}

internal enum OverlayPhase : byte { Opening, Open, Closing }

internal sealed class OverlayServiceImpl : IOverlayService
{
    // WinUI MenuPopupThemeTransition unload opacity: 83ms linear (MenuPopupThemeTransition_Partial.h:23
    // s_OpacityChangeDuration; LayoutTransition_partial.cpp:530-531 unload keyframes).
    const float OpacityMs = 83f;
    // WinUI ToolTip FadeIn/FadeOutThemeAnimation: the OS-PVL opacity fade (WinTheme.cpp:169/182 TAS_FADEIN/TAS_FADEOUT),
    // 167ms on stock Windows themes — used for PopupChrome.Raw (ToolTip) instead of the menu's 83ms.
    internal const float RawFadeMs = 167f;

    private readonly Signal<int> _version;     // bump → OverlayHost re-renders
    private int _nextId;
    public readonly List<OverlayEntry> Entries = new(4);

    // Host-wired (via the InputHooks ambient): read/restore the focused node for WinUI flyout focus-restoration.
    public Func<NodeHandle>? GetFocus;
    public Action<NodeHandle>? RestoreFocus;
    public SceneStore? Scene;
    public AnimEngine? Anim;
    public InputHooks? Hooks;   // popup-window + work-area host seams (E4 windowed popups)
    public NodeHandle ScrimNode;
    public RectF ViewportRect;  // last rendered viewport (the live-anchor follow places against it between renders)

    public OverlayServiceImpl(Signal<int> version) => _version = version;

    public bool AnyOpen { get { foreach (var e in Entries) if (e.Phase != OverlayPhase.Closing) return true; return false; } }
    public bool AnyInputBlocking { get { foreach (var e in Entries) if (e.Phase != OverlayPhase.Closing && e.DismissBehavior != DismissBehavior.None) return true; return false; } }
    public bool AnyClosing { get { foreach (var e in Entries) if (e.Phase == OverlayPhase.Closing) return true; return false; } }
    public bool AnyModal { get { foreach (var e in Entries) if (e.Phase != OverlayPhase.Closing && e.DismissBehavior == DismissBehavior.Modal) return true; return false; } }
    public bool AnyModalVisual { get { foreach (var e in Entries) if (e.DismissBehavior == DismissBehavior.Modal) return true; return false; } }
    public bool TopCanLightDismiss
    {
        get
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (Entries[i].Phase != OverlayPhase.Closing && Entries[i].DismissBehavior != DismissBehavior.None)
                    return Entries[i].DismissBehavior == DismissBehavior.LightDismiss;
            return false;
        }
    }

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
        => Open(anchor, content, placement, default);

    public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options)
        => OpenCore(anchor, null, content, placement, options);

    public OverlayHandle OpenAt(Func<RectF> anchorRect, Func<Element> content, FlyoutPlacement placement, PopupOptions options = default, Func<NodeHandle>? owner = null)
        => OpenCore(owner ?? (static () => NodeHandle.Null), anchorRect, content, placement, options);

    public OverlayHandle OpenAtLocal(Func<NodeHandle> owner, Point2 local, Func<Element> content,
        FlyoutPlacement placement = FlyoutPlacement.BottomEdgeAlignedLeft, PopupOptions options = default)
        => OpenAt(
            () =>
            {
                var scene = Scene;
                var o = owner();
                RectF abs = scene is not null && !o.IsNull && scene.IsLive(o) ? scene.AbsoluteRect(o) : default;
                // −FlyoutMargin compensates the positioner's below-anchor gap so the presenter top-left lands ON the
                // point (the ToolTip.cs:171 PlacementMode.Mouse precedent). Zero-size rect ⇒ pure point placement.
                return new RectF(abs.X + local.X, abs.Y + local.Y - FlyoutPositioner.FlyoutMargin, 0f, 0f);
            },
            content, placement, options, owner);

    private OverlayHandle OpenCore(Func<NodeHandle> anchor, Func<RectF>? anchorRect, Func<Element> content, FlyoutPlacement placement, PopupOptions options)
    {
        var handle = new OverlayHandle { IsOpen = true };
        var entry = new OverlayEntry
        {
            Id = _nextId++, Anchor = anchor, AnchorRect = anchorRect, Content = content, Placement = placement, Handle = handle,
            FocusTrap = options.FocusTrap,
            DismissBehavior = options.DismissBehavior,
            Chrome = options.Chrome,
            ConstrainToRootBounds = options.ConstrainToRootBounds,
            SeamOffsetY = options.SeamOffsetY,
            PassThrough = options.PassThrough,
            AnchorOffsetX = options.AnchorOffsetX,
            Phase = OverlayPhase.Opening,
            SavedFocus = GetFocus?.Invoke() ?? NodeHandle.Null,   // capture pre-open focus → restore on close
            ParentId = ResolveParentId(anchor),
        };
        handle.CloseAction = cause => BeginClose(entry, cause);
        Entries.Add(entry);
        Bump();
        return handle;
    }

    /// <summary>Nested-flyout chain: the parent is the open entry whose wrapper subtree contains this entry's anchor
    /// (a MenuFlyoutSubItem anchored to an item INSIDE another popup). Closing a parent cascade-closes its children.</summary>
    private int ResolveParentId(Func<NodeHandle> anchor)
    {
        if (Scene is not { } scene) return -1;
        var a = anchor();
        if (a.IsNull || !scene.IsLive(a)) return -1;
        for (var n = a; !n.IsNull; n = scene.Parent(n))
            foreach (var e in Entries)
                if (e.Phase != OverlayPhase.Closing && !e.WrapperNode.IsNull && e.WrapperNode == n)
                    return e.Id;
        return -1;
    }

    public void CloseTop()
        => CloseTop(OverlayCloseCause.Programmatic);

    internal void CloseTop(OverlayCloseCause cause)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Phase != OverlayPhase.Closing) { BeginClose(Entries[i], cause); return; }
    }

    internal void CloseTopInputBlocking(OverlayCloseCause cause)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Phase != OverlayPhase.Closing && Entries[i].DismissBehavior != DismissBehavior.None)
            { BeginClose(Entries[i], cause); return; }
    }

    public void CloseAll()
    {
        foreach (var e in Entries) BeginClose(e, OverlayCloseCause.Programmatic);
    }

    /// <summary>Window lost activation → close every light-dismiss overlay; modal (ContentDialog) and caller-owned
    /// (ToolTip) overlays stay. WinUI: <c>DismissalTriggerFlags::WindowDeactivated</c> is part of the default
    /// light-dismiss trigger set (Popup_Partial.h:38); ContentDialog's modal popup excludes it.</summary>
    public void OnWindowBlur()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Phase != OverlayPhase.Closing && e.DismissBehavior == DismissBehavior.LightDismiss)
                BeginClose(e, OverlayCloseCause.LightDismiss);
        }
    }

    // Start the close: cascade-close child flyouts first, restore focus NOW (WinUI restores the saved focus when
    // Hide() begins — Popup_Partial.h:63-64 SavedFocusState; FlyoutBase returns focus to the invoker synchronously on
    // Hide, not after the close animation), then keep the entry (so it stays on top and fades) and mark it closing;
    // the host seeds the fade-out and AfterAnimations removes it once the animation settles.
    private void BeginClose(OverlayEntry e, OverlayCloseCause cause)
    {
        if (e.Phase == OverlayPhase.Closing) return;
        if (e.Handle.ClosingAction is { } beforeClose && !beforeClose(cause)) return;

        // Cascade: children close first (their focus restores into THIS popup's content, which then chains to the
        // original invoker when this entry's own restore runs below) — WinUI nested MenuFlyoutSubItem close order.
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var c = Entries[i];
            if (c.ParentId == e.Id && c.Phase != OverlayPhase.Closing) BeginClose(c, cause);
        }

        e.CloseCause = cause;
        e.Phase = OverlayPhase.Closing;
        e.Handle.IsOpen = false;

        // The focus trap lifts when the close STARTS (the WinUI DialogShowing → DialogHidden Tab-cycle teardown:
        // ContentDialog_themeresources.xaml:123 sets BackgroundElement.TabFocusNavigation=Cycle only while showing),
        // so the focus restore below can land OUTSIDE the dying overlay.
        if (e.ScopePushed)
        {
            e.ScopePushed = false;
            Hooks?.PopFocusScope?.Invoke(e.WrapperNode);
        }

        // Per-level focus restore at close START. Restore only when focus still lives inside this overlay's subtree
        // (or points at a dead/null node) — a popup that never took focus must not yank it from elsewhere
        // (CPopup::Close restores its saved focus only while it owns focus).
        if (!e.SavedFocus.IsNull && RestoreFocus is not null && Scene is { } sc)
        {
            var cur = GetFocus?.Invoke() ?? NodeHandle.Null;
            bool inside = cur.IsNull || !sc.IsLive(cur);
            if (!inside && !e.WrapperNode.IsNull)
                for (var n = cur; !n.IsNull; n = sc.Parent(n))
                    if (n == e.WrapperNode) { inside = true; break; }
            if (inside && sc.IsLive(e.SavedFocus)) RestoreFocus(e.SavedFocus);
        }

        // Dismiss-and-reopen seam (context menus): once no non-closing entry remains, the scrim must stop hit-testing
        // SYNCHRONOUSLY so a right-click redispatch (InputHooks.RedispatchContextAt, fired right after CloseTop on the
        // scrim's own OnContextRequested) hit-tests THROUGH the dying scrim to the node beneath in the same gesture.
        // The next OverlayHost render re-derives HitTestVisible from AnyOpen (now false) — this applies that value a
        // beat early; it never contradicts it (the scrim stays present + visible while the last popup fades).
        if (!AnyInputBlocking && Scene is { } scrimScene && !ScrimNode.IsNull && scrimScene.IsLive(ScrimNode))
        {
            scrimScene.Unmark(ScrimNode, NodeFlags.HitTestVisible);
            scrimScene.Mark(ScrimNode, NodeFlags.PaintDirty);
        }

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
            // WinUI TeachingTip contract: scale 1 → 20/Width over 200ms cubic-bezier(0.7,0,1,0.5)
            // (TeachingTip.cpp:1695–1712 contractAnimation keyframes; TeachingTip.h:235 m_contractAnimationDuration,
            // :306–307 contract control points). The contract storyboard has NO opacity keyframes — the tip shrinks
            // at full opacity, then the popup closes.
            float sx = 20f / e.MeasuredW;
            float sy = 20f / e.MeasuredH;
            var ease = EasingSpec.CubicBezier(0.7f, 0.0f, 1.0f, 0.5f);
            anim.Animate(e.SurfaceNode, AnimChannel.ScaleX, currentScaleX, sx, 200f, ease);
            anim.Animate(e.SurfaceNode, AnimChannel.ScaleY, currentScaleY, sy, 200f, ease);
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
        else if (e.Chrome == PopupChrome.Dropdown && e.MeasuredH > 0f)
        {
            // WinUI ComboBox dropdown close = SplitCloseThemeAnimation (generic.xaml:9056; ThemeAnimations.cpp:733+):
            // the clip COLLAPSES toward the anchor edge to closedRatio 0.15 of the opened length over
            // s_CloseDuration=167ms cubic-bezier(0,0,0,1) (ThemeAnimations.cpp:741 closedRatio; cpp:746-747 easing;
            // SplitCloseThemeAnimation_Partial.h:16), and opacity fades 1→0 over the LAST 83ms — begin time
            // s_OpacityChangeBeginTime = 167−83 = 84ms (Partial.h:17-18; cpp:826-828 background opacity keyframes).
            float fullH = e.MeasuredH;
            float closedH = fullH * 0.15f;
            float curT = surfacePaint.ClipRect.IsInfinite ? 0f : surfacePaint.ClipRect.Y;
            float curB = surfacePaint.ClipRect.IsInfinite ? fullH : surfacePaint.ClipRect.Bottom;
            if (e.SeamOffsetY is float seam)
            {
                // Seam form (the non-editable ComboBox carousel): the clip band collapses BOTH ways toward the seam
                // (selected-row centre = H/2 + OffsetFromCenter; ClipTranslateY = offset immediately, cpp:823; clip
                // origin {0,0.5}, cpp:740). Final ClipScaleY = closedRatio 0.15 — or the off-edge compensation
                // pixelsOff/H·2 + 0.15 when |offset| > H·(1−0.15)/2 (cpp:798-811) ⇒ half-height
                // max(0.075·H, |offset| − 0.35·H). Content TranslateY stays 0 (no ContentTranslationOffset).
                float c = fullH * 0.5f + seam;
                float halfF = MathF.Max(0.075f * fullH, MathF.Abs(seam) - 0.35f * fullH);
                anim.Animate(e.SurfaceNode, AnimChannel.ClipT, curT, c - halfF, 167f, Easing.FluentPopOpen);
                anim.Animate(e.SurfaceNode, AnimChannel.ClipB, curB, c + halfF, 167f, Easing.FluentPopOpen);
            }
            else if (e.OpensUp)
                anim.Animate(e.SurfaceNode, AnimChannel.ClipT, curT, fullH - closedH, 167f, Easing.FluentPopOpen);
            else
                anim.Animate(e.SurfaceNode, AnimChannel.ClipB, curB, closedH, 167f, Easing.FluentPopOpen);
            anim.Animate(e.SurfaceNode, AnimChannel.Opacity, currentOpacity, 0f, OpacityMs, Easing.Linear, delayMs: 167f - OpacityMs);
        }
        else if (e.Chrome == PopupChrome.Static)
        {
            // A bare WinUI Popup has no close transition (no TransitionCollection on the AutoSuggestBox
            // SuggestionsPopup): hide instantly; AfterAnimations finalizes on the next pass (no live tracks).
            scene.Paint(e.SurfaceNode).Opacity = 0f;
            scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
        }
        else
        {
            // Menus: WinUI MenuPopupThemeTransition unload (LayoutTransition_partial.cpp:525-544): an 83ms linear
            // fade; an interrupted load FREEZES the clip/translate at the interrupt offset (constant keyframes,
            // cpp:538-543) rather than snapping open — cancel the load tracks and leave the last composed
            // clip/transform in place. Generic flyouts (PopupChrome.Popup): TAS_HIDEPOPUP = the same 83ms linear
            // fade, no translate (OS PVL: OPACITY start=0 dur=83 linear is the only transform). ToolTip
            // (PopupChrome.Raw): FadeOutThemeAnimation = TAS_FADEOUT, 167ms linear (OS PVL dump; the ToolTip
            // template's Closed state, ToolTip_themeresources.xaml:59-63).
            anim.Cancel(e.SurfaceNode, AnimChannel.SizeH);
            anim.Cancel(e.SurfaceNode, AnimChannel.TranslateY);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipL);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipT);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipR);
            anim.Cancel(e.SurfaceNode, AnimChannel.ClipB);
            // The presenter-plate stretch freezes WITH the clip (an interrupted load keeps the LTE's interrupt state):
            // cancel (don't settle) the plate ScaleY so the chrome stays framing the frozen band through the fade.
            if (!e.PlateNode.IsNull && scene.IsLive(e.PlateNode))
                anim.Cancel(e.PlateNode, AnimChannel.ScaleY);
            scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
            // Windowed (OS-backed) menus: ALSO fade the composition CHROME (acrylic + shadow) over the same 83ms via the
            // host. The engine SurfaceNode fade below covers the swapchain CONTENT (border + items), and the chrome lives
            // on separate composition visuals, so content + acrylic each fade exactly once (no double-fade). The popup
            // window is torn down at finalize, after the fade settles — so the acrylic fades out instead of vanishing.
            if (e.PopupWindowToken >= 0) Hooks?.AnimatePopupClose?.Invoke(e.PopupWindowToken);
            float fadeMs = e.Chrome == PopupChrome.Raw ? RawFadeMs : OpacityMs;
            anim.Animate(e.SurfaceNode, AnimChannel.Opacity, currentOpacity, 0f, fadeMs, Easing.Linear);
        }
    }

    /// <summary>Host phase 7.1: remove closing entries whose retained tracks have settled, before recording the frame —
    /// and follow LIVE anchors: an open popup whose anchor node moved since its last placement re-places against the
    /// cached measure (the WinUI slider thumb tooltip tracks the scrubbing thumb; an anchored flyout rides a reflow).
    /// No OverlayHost re-render, no open-animation reseed; silent (zero writes) while the anchor is still.</summary>
    public void AfterAnimations()
    {
        if (Scene is not { } scene || Anim is not { } anim) return;
        // Keep the viewport rect live without re-rendering OverlayHost on resize (the host now reads root bounds, not
        // UseContext(Viewport.Size)): refresh from the laid-out root each frame so anchor-follow placement clamps correctly.
        if (!scene.Root.IsNull) { var rb = scene.Bounds(scene.Root); ViewportRect = new RectF(0f, 0f, rb.W, rb.H); }
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Phase != OverlayPhase.Closing || !e.CloseSeeded) continue;
            bool surfaceSettled = e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode) || !anim.HasTracks(e.SurfaceNode);
            bool scrimSettled = e.Chrome != PopupChrome.Modal || ScrimNode.IsNull || !scene.IsLive(ScrimNode) || !anim.HasTracks(ScrimNode);
            if (surfaceSettled && scrimSettled) Finalize(e);
        }

        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            // Node-anchored, open, in-tree entries only: rect-thunk anchors are pointer placements (no live target),
            // Modal centers on the viewport, a Closing entry keeps its last placement while it fades.
            if (e.Phase == OverlayPhase.Closing || e.Chrome == PopupChrome.Modal || e.AnchorRect is not null) continue;
            if (e.WrapperNode.IsNull || !scene.IsLive(e.WrapperNode) || e.MeasuredW <= 0f) continue;
            var anchor = e.Anchor();
            if (anchor.IsNull || !scene.IsLive(anchor)) continue;
            var aRect = scene.AbsoluteRect(anchor);
            if (MathF.Abs(aRect.X - e.LastAnchorRect.X) < 0.5f && MathF.Abs(aRect.Y - e.LastAnchorRect.Y) < 0.5f
                && MathF.Abs(aRect.W - e.LastAnchorRect.W) < 0.5f && MathF.Abs(aRect.H - e.LastAnchorRect.H) < 0.5f)
                continue;

            e.LastAnchorRect = aRect;
            var popupSize = new Size2(e.MeasuredW, e.MeasuredH);
            bool windowed = e.PopupWindowToken >= 0;
            RectF container = ViewportRect;
            if (windowed && Hooks is { GetWorkArea: { } workArea })
                container = workArea(new Point2(aRect.X + aRect.W * 0.5f, aRect.Y + aRect.H * 0.5f));

            var place = OverlayHost.ApplyAnchorOffsetX(
                FlyoutPositioner.Place(in aRect, in popupSize, in container, e.Placement, isWindowed: windowed),
                e.AnchorOffsetX, in aRect);
            if (windowed)
                Hooks!.SetPopupWindowBounds?.Invoke(e.PopupWindowToken, new RectF(place.X, place.Y, e.MeasuredW, e.MeasuredH), place.OpensUp,
                    e.ParentId >= 0 ? 0.67f : 0.5f);

            e.OpensUp = place.OpensUp;
            e.CornerJoin = place.CornerJoin;
            e.PlacementInfo.Value = new OverlayPlacementInfo(
                aRect.X + aRect.W * 0.5f - place.X,
                aRect.Y + aRect.H * 0.5f - place.Y,
                e.MeasuredW, e.MeasuredH, place.OpensUp);
            ref NodePaint wp = ref scene.Paint(e.WrapperNode);
            wp.LocalTransform = Affine2D.Translation(place.X, place.Y);
            scene.Mark(e.WrapperNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            if (!e.SurfaceNode.IsNull && scene.IsLive(e.SurfaceNode) && e.Chrome is PopupChrome.Flyout or PopupChrome.Dropdown)
            {
                scene.Paint(e.SurfaceNode).Corners = FlyoutSurface.CornersFor(e.CornerJoin);
                scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
            }
            OverlayHost.SyncWindowedMenuBackdrop(scene, e);
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
        // Release the out-of-bounds popup window (hide + dispose + drop the second swapchain) with the entry.
        if (e.PopupWindowToken >= 0)
        {
            Hooks?.ClosePopupWindow?.Invoke(e.PopupWindowToken);
            e.PopupWindowToken = -1;
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
        // NOTE: focus restoration happens at close START in BeginClose (WinUI Hide() timing), not here.
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
        if (key == Keys.Escape && AnyInputBlocking) { CloseTopInputBlocking(OverlayCloseCause.Escape); return true; }
        return false;
    }

    private void Bump() => _version.Value = _version.Peek() + 1;
}

/// <summary>
/// Wraps the app root and hosts anchored flyouts in a top-level ZStack.
/// The base <see cref="Child"/> is always the first ZStack child (so opening/closing an overlay never remounts it).
/// Open mirrors WinUI MenuPopupThemeTransition (LayoutTransition_partial.cpp:441-506): a 250ms cubic-bezier(0,0,0,1)
/// clip-reveal from the anchor edge whose CONTENT slides in with the reveal (TranslateY ±H·0.5 → 0 against a
/// counter-translated clip); the presenter does NOT fade at load. Close is the 83ms linear unload fade.
/// </summary>
public sealed class OverlayHost : Component
{
    public Element Child = new BoxEl();

    // WinUI MenuPopupThemeTransition timings: s_OpenDuration=250 / s_OpacityChangeDuration=83
    // (MenuPopupThemeTransition_Partial.h:23-24); ClosedRatio=0.5 for root menus (MenuFlyout_Partial.cpp:253
    // closedRatioConstant; cascaded MenuFlyoutSubItem popups use 0.67, MenuFlyoutSubItem_Partial.cpp:741 — wire that
    // through when sub-menus land in Wave 3).
    const float OpenMs = 250f, OpacityMs = 83f, ClosedRatio = 0.5f;

    // WinUI MenuFlyout renders into its OWN windowed popup over DesktopAcrylicBackdrop (it samples the desktop). The
    // Win32 analogue is a transparent composited popup HWND carrying DWM's DWMSBT_TRANSIENTWINDOW acrylic + rounded
    // corners + system shadow (native Windows 11 menu). Only PopupChrome.Flyout opts into that today; other chromes
    // stay in-window with the engine acrylic compositor (no regression).
    private static PopupWindowMaterial WindowMaterialFor(OverlayEntry e)
        => e.Chrome is PopupChrome.Flyout or PopupChrome.CommandBar ? PopupWindowMaterial.TransientAcrylic : PopupWindowMaterial.None;

    /// <summary>Reconcile the menu presenter plate to its placement: an OS-backed windowed flyout (a popup HWND was
    /// leased) renders TRANSPARENT over DWM acrylic — clear the engine acrylic AND the engine drop shadow (the
    /// transparent plate would otherwise reveal the shadow primitive drawn behind it, and DWM draws the window shadow),
    /// keeping just the 1px border + content. A constrained / in-window flyout keeps the engine acrylic + drop shadow.
    /// Idempotent: only mutates + marks paint-dirty when the state actually changes (no per-frame repaint churn).</summary>
    internal static void SyncWindowedMenuBackdrop(SceneStore scene, OverlayEntry e)
    {
        // Menu chrome carries the visuals on its stretch PLATE; the CommandBar chrome is a single-card surface
        // (FlyoutSurface non-menu branch), so its acrylic/shadow live on the SURFACE node itself.
        var target = e.Chrome switch
        {
            PopupChrome.Flyout => e.PlateNode,
            PopupChrome.CommandBar => e.SurfaceNode,
            _ => NodeHandle.Null,
        };
        if (target.IsNull || !scene.IsLive(target)) return;
        bool osBacked = e.PopupWindowToken >= 0;
        bool hasAcrylic = scene.TryGetAcrylic(target, out _);
        bool hasShadow = scene.TryGetShadow(target, out _);
        bool changed = false;
        ColorF desiredFill;
        if (osBacked)
        {
            if (hasAcrylic) { scene.ClearAcrylic(target); changed = true; }
            if (hasShadow) { scene.ClearShadow(target); changed = true; }
            // DWM supplies the BLUR (DWMSBT_TRANSIENTWINDOW), but its system tint alone is far too weak — in light
            // theme the app content bleeds straight through the menu. Paint the WinUI acrylic recipe's TINT over the
            // DWM blur as a single flat fill: the tint COLOR at the coverage the in-app compositor's luminosity+tint
            // layers apply over the blurred backdrop = 1 − (1−LuminosityOpacity)·(1−TintOpacity) (the fraction of the
            // result that is NOT see-through backdrop). Light FlyoutLight (Lum .85 / Tint 0) ⇒ #FCFCFC @ .85 (Explorer
            // near-opaque, kills the bleed-through); dark InAppDefault (Lum .96 / Tint .15) ⇒ #2C2C2C @ ~.97 (stays a
            // dark, faintly-translucent card — close to the DWM-only look it replaces). Matches the in-window acrylic,
            // so a windowed and a constrained (fallback) menu read the same.
            var a = Tok.AcrylicFlyout;
            float coverage = 1f - (1f - a.LuminosityOpacity) * (1f - a.TintOpacity);
            desiredFill = a.Tint with { A = coverage };
        }
        else
        {
            if (!hasAcrylic) { scene.SetAcrylic(target, Tok.AcrylicFlyout); changed = true; }
            if (!hasShadow) { scene.SetShadow(target, Elevation.Flyout); changed = true; }
            desiredFill = ColorF.Transparent;   // bg comes from the engine acrylic
        }
        // Idempotent: only write + mark dirty when the acrylic/shadow state flipped OR the fill actually differs
        // (the method runs per placement frame — no per-frame repaint churn).
        ref NodePaint tp = ref scene.Paint(target);
        if (changed || !tp.Fill.Equals(desiredFill))
        {
            tp.Fill = desiredFill;
            scene.Mark(target, NodeFlags.PaintDirty);
        }
    }

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
        svc.Hooks = hooks;                      // popup-window + work-area seams (E4 out-of-bounds popups)
        hooks.WindowBlurred = svc.OnWindowBlur; // window deactivation → light-dismiss overlays close (modal stays)
        // Viewport size read from the laid-out ROOT bounds, NOT UseContext(Viewport.Size): subscribing re-rendered this
        // (app-root) overlay host on EVERY window-resize tick (~12KB/resize → GC stutter felt as a hiccup mid-drag).
        // `version` (a popup opening/closing) is now the only re-render trigger. Open popups still track a live resize:
        // their anchor nodes move with layout and AfterAnimations re-places them every frame against the live root size.
        var rootSz = Context.Scene is { } sc0 && !sc0.Root.IsNull ? sc0.Bounds(sc0.Root) : default;
        var vp = new Size2(rootSz.W, rootSz.H);
        svc.ViewportRect = new RectF(0f, 0f, vp.Width, vp.Height);   // seed; AfterAnimations refreshes it post-layout

        // After layout (Bounds valid): place each popup, then seed its open (clip-unfold + slide) / close (fade) animation.
        UseLayoutEffect(() =>
        {
            var scene = Context.Scene;
            var anim = Context.Anim;
            if (scene is null || anim is null) return;
            var rb = !scene.Root.IsNull ? scene.Bounds(scene.Root) : default;   // live (post-layout) root size
            var vpRect = new RectF(0f, 0f, rb.W, rb.H);

            // WinUI OverlayInputPassThroughElement: register the first open entry's pass-through subtree on the
            // scrim — hover/press over its rendered bounds bypass the light-dismiss layer (the MenuBar keeps
            // hover-switching titles with a menu down, FlyoutBase_Partial.cpp:3922-3938). Cleared when none.
            if (!svc.ScrimNode.IsNull && scene.IsLive(svc.ScrimNode))
            {
                var pass = NodeHandle.Null;
                foreach (var e in svc.Entries)
                    if (e.Phase != OverlayPhase.Closing && e.PassThrough is { } pt) { pass = pt(); break; }
                scene.SetHitTestPassThrough(svc.ScrimNode, pass);
            }

            foreach (var e in svc.Entries)
            {
                if (e.WrapperNode.IsNull || !scene.IsLive(e.WrapperNode)) continue;
                // A CLOSING entry keeps its last placement (and its popup window, when windowed) while it fades —
                // re-placing it could yank it back into the viewport mid-fade.
                if (e.Chrome != PopupChrome.Modal && e.Phase != OverlayPhase.Closing)
                {
                    RectF aRect = default;
                    bool haveAnchor = false;
                    if (e.AnchorRect is { } rectThunk)
                    {
                        aRect = rectThunk();
                        haveAnchor = true;
                    }
                    else
                    {
                        var anchor = e.Anchor();
                        if (!anchor.IsNull && scene.IsLive(anchor))
                        {
                            aRect = scene.AbsoluteRect(anchor);
                            haveAnchor = true;
                        }
                    }
                    if (haveAnchor)
                    {
                        var pRect = scene.AbsoluteRect(e.WrapperNode);
                        e.MeasuredW = pRect.W;
                        e.MeasuredH = pRect.H;
                        var popupSize = new Size2(pRect.W, pRect.H);

                        // Out-of-bounds (windowed) popups place against the MONITOR work area, in window-DIP space
                        // (WinUI FlyoutBase_Partial.cpp:3382-3392 useMonitorBounds = IsWindowedPopup()); constrained
                        // popups place against the viewport. The work-area seam is host-wired (AppHost → IPlatformApp
                        // GetWorkArea via MonitorFromPoint/GetMonitorInfo).
                        // Windowed only when we have an OS material to back the transparent popup HWND (Flyout today).
                        // A non-backed chrome with ConstrainToRootBounds=false stays in-window (engine acrylic) — a
                        // transparent popup window with no DWM backdrop would render content on bare desktop.
                        bool wantWindowed = !e.ConstrainToRootBounds && svc.Hooks is { OpenPopupWindow: not null }
                                            && WindowMaterialFor(e) != PopupWindowMaterial.None;
                        RectF container = vpRect;
                        if (wantWindowed && svc.Hooks!.GetWorkArea is { } workArea)
                            container = workArea(new Point2(aRect.X + aRect.W * 0.5f, aRect.Y + aRect.H * 0.5f));

                        var place = ApplyAnchorOffsetX(
                            FlyoutPositioner.Place(in aRect, in popupSize, in container, e.Placement, isWindowed: wantWindowed),
                            e.AnchorOffsetX, in aRect);

                        if (wantWindowed)
                        {
                            // Lease a platform popup window for this subtree (host records it into its own DrawList +
                            // swapchain; the subtree is skipped from the main-window record). -1 = the platform/device
                            // can't (WinUI's DoesPlatformSupportWindowedPopup == false) → constrained fallback.
                            if (e.PopupWindowToken < 0)
                                e.PopupWindowToken = svc.Hooks!.OpenPopupWindow!(e.WrapperNode, WindowMaterialFor(e));
                            if (e.PopupWindowToken >= 0)
                                // closedRatio drives the composition open-slide: menus 0.5, cascaded sub-menus 0.67
                                // (MenuFlyout_Partial.cpp:253 / MenuFlyoutSubItem_Partial.cpp:741); the CommandBar
                                // chrome passes 0 = NO slide (WinUI CommandBarFlyout opens with its own 83ms body
                                // fade only — OpeningOpacityStoryboard, CommandBarFlyout_themeresources.xaml:655-658).
                                svc.Hooks!.SetPopupWindowBounds?.Invoke(e.PopupWindowToken, new RectF(place.X, place.Y, pRect.W, pRect.H), place.OpensUp,
                                    e.Chrome == PopupChrome.CommandBar ? 0f : (e.ParentId >= 0 ? 0.67f : 0.5f));
                            else
                                place = ApplyAnchorOffsetX(
                                    FlyoutPositioner.Place(in aRect, in popupSize, in vpRect, e.Placement, isWindowed: false),
                                    e.AnchorOffsetX, in aRect);
                            SyncWindowedMenuBackdrop(scene, e);
                        }

                        e.OpensUp = place.OpensUp;
                        e.CornerJoin = place.CornerJoin;
                        e.LastAnchorRect = aRect;   // baseline for the live-anchor follow (AfterAnimations)
                        e.PlacementInfo.Value = new OverlayPlacementInfo(
                            aRect.X + aRect.W * 0.5f - place.X,
                            aRect.Y + aRect.H * 0.5f - place.Y,
                            pRect.W,
                            pRect.H,
                            place.OpensUp);
                        ref NodePaint wp = ref scene.Paint(e.WrapperNode);
                        wp.LocalTransform = Affine2D.Translation(place.X, place.Y);
                        scene.Mark(e.WrapperNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                        // Corner squaring where the popup abuts the anchor (ComboBox/dropdown attachment). The
                        // AutoSuggestBox SuggestionsContainer keeps OverlayCornerRadius on ALL corners
                        // (AutoSuggestBox_themeresources.xaml:283) — Static is excluded.
                        if (!e.SurfaceNode.IsNull && scene.IsLive(e.SurfaceNode)
                            && e.Chrome is PopupChrome.Flyout or PopupChrome.Dropdown)
                        {
                            scene.Paint(e.SurfaceNode).Corners = FlyoutSurface.CornersFor(e.CornerJoin);
                            scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
                        }
                    }
                }

                // REAL focus trap (PopupOptions.FocusTrap): push a dispatcher focus scope rooted at the popup subtree
                // so Tab/Shift+Tab CYCLE inside it (WinUI ContentDialog DialogShowing sets
                // BackgroundElement.TabFocusNavigation=Cycle, ContentDialog_themeresources.xaml:123), and move focus
                // to the first tab-stop inside (tab order — ContentDialog ranks its DefaultButton first via TabIndex).
                // Pushed once per entry after the subtree realizes; popped when the close starts (BeginClose).
                if (e.FocusTrap && !e.ScopePushed && e.Phase != OverlayPhase.Closing)
                {
                    e.ScopePushed = true;
                    hooks.PushFocusScope?.Invoke(e.WrapperNode);
                    var firstStop = hooks.FirstFocusableIn?.Invoke(e.WrapperNode) ?? NodeHandle.Null;
                    if (!firstStop.IsNull) hooks.FocusNode?.Invoke(firstStop, false);
                }

                if (e.SurfaceNode.IsNull || !scene.IsLive(e.SurfaceNode)) continue;
                if (e.Phase == OverlayPhase.Closing) { svc.SeedCloseIfNeeded(e); continue; }

                if (!e.OpenSeeded && e.Chrome == PopupChrome.TeachingTip && e.MeasuredW > 0f && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    // WinUI TeachingTip expand: scale Min(0.01, 20/Width) → 1 over 300ms cubic-bezier(0.1,0.9,0.2,1)
                    // (TeachingTip.cpp:1660-1664 expandAnimation keyframes; TeachingTip.h:234 m_expandAnimationDuration,
                    // :304-305 expand control points). The expand storyboard has NO opacity keyframes — the tip grows
                    // from a point at full opacity.
                    float sx = MathF.Min(0.01f, 20f / e.MeasuredW);
                    float sy = MathF.Min(0.01f, 20f / e.MeasuredH);
                    var ease = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);
                    anim.Animate(e.SurfaceNode, AnimChannel.ScaleX, sx, 1f, 300f, ease);
                    anim.Animate(e.SurfaceNode, AnimChannel.ScaleY, sy, 1f, 300f, ease);
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
                else if (!e.OpenSeeded && e.Chrome == PopupChrome.Raw)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    // WinUI ToolTip: FadeInThemeAnimation only (the template's Opened state,
                    // ToolTip_themeresources.xaml:64-68) = TAS_FADEIN (WinTheme.cpp:165-169). OS PVL ground truth
                    // (uxtheme "Animations" dump, stock Windows 11): OPACITY 0→1, start=0, dur=167ms, linear.
                    // No clip reveal, no translate, no scale.
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, 0f, 1f, OverlayServiceImpl.RawFadeMs, Easing.Linear);
                }
                else if (!e.OpenSeeded && e.Chrome == PopupChrome.Static)
                {
                    // A bare WinUI Popup (AutoSuggestBox SuggestionsPopup): no open transition — appears instantly.
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                }
                else if (!e.OpenSeeded && e.Chrome == PopupChrome.CommandBar)
                {
                    // WinUI CommandBarFlyout: the flyout kills the default popup transition
                    // (AreOpenCloseAnimationsEnabled(false), CommandBarFlyout.cpp:43-44) and the BODY plays its own
                    // OpeningOpacityStoryboard — LayoutRoot+OverflowPopup Opacity 0→1 over 83ms
                    // (CommandBarFlyout_themeresources.xaml:655-658; kind probe CommandBarFlyoutCommandBar.cpp:208-224).
                    // No slide, no clip reveal on open. The host seeds nothing; the composition chrome gets
                    // closedRatio 0 (no open slide) and the CommandBarFlyoutBody drives the 83ms fade itself.
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                }
                else if (!e.OpenSeeded && e.Chrome == PopupChrome.Popup && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    // WinUI Flyout/FlyoutPresenter open = PopupThemeTransition (FlyoutBase_Partial.cpp:1968-1975)
                    // = TAS_SHOWPOPUP. OS PVL ground truth (uxtheme "Animations" dump, stock Windows 11):
                    //   TRANSLATE_2D: offset → 0, start=0, dur=367ms, cubic-bezier(0.1, 0.9, 0.2, 1.0)
                    //   OPACITY:     0 → 1, start=83ms, dur=83ms, linear (holds 0 for the first 83ms)
                    // The offset is FlyoutBase::g_entranceThemeOffset = 50 px in the major direction
                    // (FlyoutBase_Partial.cpp:68 + SetTransitionParameters cpp:2024-2059): a below-anchor flyout
                    // starts 50px ABOVE its resting spot (FromVerticalOffset −50) and slides down; an opens-up
                    // flyout starts 50px BELOW (+50) and slides up — it emerges out of the anchor.
                    float fromY = e.OpensUp ? 50f : -50f;
                    var decel = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);
                    anim.Animate(e.SurfaceNode, AnimChannel.TranslateY, fromY, 0f, 367f, decel);
                    scene.Paint(e.SurfaceNode).Opacity = 0f;
                    scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
                    anim.Animate(e.SurfaceNode, AnimChannel.Opacity, 0f, 1f, 83f, Easing.Linear, delayMs: 83f);
                }
                else if (!e.OpenSeeded && e.Chrome == PopupChrome.Dropdown && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    // WinUI ComboBox dropdown open = SplitOpenThemeAnimation (generic.xaml:9047-9051): the clip
                    // grows from closedRatio 0.50 of the opened length (ThemeAnimations.cpp:599 closedRatio;
                    // cpp:680-681 ClipScaleY keyframes) over s_OpenDuration=250ms cubic-bezier(0,0,0,1)
                    // (SplitOpenThemeAnimation_Partial.h:16-17; cpp:602-603 easing). The popup itself does NOT fade
                    // (opacity pinned to 1, cpp:684) and the CONTENT does not translate — the ComboBox template sets
                    // only OffsetFromCenter/OpenedLength, so ContentTranslationOffset stays 0 (cpp:692-711 would
                    // animate TranslateY ±0 → 0). The visible window expands from the anchor-joined edge.
                    float fullH = e.MeasuredH;
                    float closedH = fullH * 0.50f;
                    if (e.SeamOffsetY is float seam)
                    {
                        // Seam form (the non-editable ComboBox carousel): the band is CENTRED ON THE SEAM (clip origin
                        // {0,0.5}, cpp:601; ClipTranslateY = OffsetFromCenter immediately, cpp:682) and grows BOTH
                        // ways. Initial ClipScaleY = closedRatio 0.50 — or the off-edge compensation
                        // pixelsOff/H·2 + 0.5 when |offset| > H·(1−0.5)/2 (cpp:655-668) ⇒ initial half-height
                        // max(0.25·H, |offset|); final ClipScaleY = (0.5 + |offset/H|)·2 (cpp:674) ⇒ final
                        // half-height H/2 + |offset| (the unclamped band always covers [0,H] at rest — the engine's
                        // node-local clip intersects the box, so over-shoot edges are free). Content TranslateY
                        // stays 0 and there is still no fade.
                        float c = fullH * 0.5f + seam;
                        float half0 = MathF.Max(0.25f * fullH, MathF.Abs(seam));
                        float halfF = fullH * 0.5f + MathF.Abs(seam);
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipT, c - half0, c - halfF, OpenMs, Easing.FluentPopOpen);
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipB, c + half0, c + halfF, OpenMs, Easing.FluentPopOpen);
                    }
                    else if (e.OpensUp)
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipT, fullH - closedH, 0f, OpenMs, Easing.FluentPopOpen);
                    else
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipB, closedH, fullH, OpenMs, Easing.FluentPopOpen);
                }
                else if (!e.OpenSeeded && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    float fullH = e.MeasuredH;
                    // Root menus unfold from 50% (MenuFlyout_Partial.cpp:253 closedRatioConstant 0.5); CASCADED
                    // MenuFlyoutSubItem popups (a parent entry in the chain) unfold from 67%
                    // (MenuFlyoutSubItem_Partial.cpp:741 closedRatioConstant 0.67).
                    float closedRatio = e.ParentId >= 0 ? 0.67f : ClosedRatio;
                    float slide = fullH * closedRatio;

                    // WINDOWED (OS-backed desktop-acrylic) popups: the whole presenter — acrylic + content + border —
                    // slides as ONE composition animation root (CompositionBackdrop.AnimateOpen — WinUI's windowed-popup
                    // model, popup-system-backdrop.md), and the popup HWND clips the overflow. So the engine must NOT also
                    // clip/translate the SurfaceNode here, or the content double-animates against the composition slide.
                    // IN-WINDOW popups have no composition root, so they DO drive the engine clip/translate (WinUI
                    // MenuPopupThemeTransition load, LayoutTransition_partial.cpp:441-506: a content TranslateY
                    // initialTranslateY = openedLength × ClosedRatio signed by direction → 0, plus a counter-translated
                    // node-local clip edge, over s_OpenDuration=250ms cubic-bezier(0,0,0,1), no presenter opacity fade).
                    if (e.PopupWindowToken < 0)
                    {
                        if (e.OpensUp)
                        {
                            // placement Top -> AnimationDirection_Bottom: TranslateY=+slide, ClipTranslateY=-slide → 0.
                            anim.Animate(e.SurfaceNode, AnimChannel.TranslateY, slide, 0f, OpenMs, Easing.FluentPopOpen);
                            anim.Animate(e.SurfaceNode, AnimChannel.ClipB, fullH - slide, fullH, OpenMs, Easing.FluentPopOpen);
                        }
                        else
                        {
                            // placement Bottom -> AnimationDirection_Top: TranslateY=-slide, ClipTranslateY=+slide → 0.
                            anim.Animate(e.SurfaceNode, AnimChannel.TranslateY, -slide, 0f, OpenMs, Easing.FluentPopOpen);
                            anim.Animate(e.SurfaceNode, AnimChannel.ClipT, slide, 0f, OpenMs, Easing.FluentPopOpen);
                        }
                    }

                    // Presenter-plate stretch — the second half of MenuPopupThemeTransition's load storyboard
                    // (LayoutTransition_partial.cpp:497-503): "MenuFlyoutPresenterBorder" animates ScaleY (1 − ClosedRatio)
                    // → 1 over s_OpenDuration=250ms cubic-bezier(0,0,0,1), pivoted at CenterY = openedLength for
                    // AnimationDirection_Top (the BOTTOM edge for a DOWNWARD-opening menu), top (CenterY 0) for upward.
                    // This runs for BOTH paths: the plate is engine-drawn into the popup swapchain, so for a windowed
                    // popup its ScaleY rides the same composition slide — the border ring stretches in like WinUI.
                    if (!e.PlateNode.IsNull && scene.IsLive(e.PlateNode))
                    {
                        ref NodePaint platePaint = ref scene.Paint(e.PlateNode);
                        platePaint.OriginY = e.OpensUp ? 0f : 1f;
                        scene.Mark(e.PlateNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                        anim.Animate(e.PlateNode, AnimChannel.ScaleY, 1f - closedRatio, 1f, OpenMs, Easing.FluentPopOpen);
                    }
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
                HitTestVisible = svc.AnyInputBlocking,
                TabStop = false,   // WinUI: the light-dismiss layer is never a tab stop (focus restore depends on it)
                // Outside-press dismiss lives ENTIRELY on this scrim node (a Controls/OverlayHost concern reached through the
                // dispatcher's hit-test → click/press delegates — there is no in-dispatcher overlay registry; input-a11y §4).
                // The full-bleed scrim is the topmost hit-test target while a popup is open, so the press lands on IT, never
                // the content beneath. WinUI closes on the POINTER DOWN, not the click: ShouldEventCloseFlyout returns true
                // solely for XCP_POINTERDOWN (PointerInputProcessor.cpp:1028-1033) — so a light-dismiss scrim closes the top
                // overlay in OnPointerDown (mouse press and touch down both route here), and the eventual release/click is
                // consumed by the no-op OnClick (no click-through — WinUI CPopupRoot::OnPointerPressed sets
                // Handled = didCloseAPopup, popup.cpp:5206). A modal scrim's OnPointerDown is a no-op that simply eats the
                // press (modal blocks the gesture). Right-button presses never reach OnPointerDown (the dispatcher tracks
                // them for context only), so the outside-right-click path below stays release-timed.
                OnClick = svc.AnyInputBlocking ? () => { } : null,
                OnPointerDown = svc.AnyInputBlocking
                    ? (lightDismiss ? _ => svc.CloseTopInputBlocking(OverlayCloseCause.LightDismiss) : _ => { })
                    : null,
                // Right-click on the light-dismiss scrim = WinUI outside-right-click: close the top overlay AND re-fire
                // the context request at the same point so the node underneath opens its own menu in ONE gesture. The
                // scrim is full-bleed at origin, so args.Position (scrim-local) IS the window-DIP point. CloseTop first
                // (BeginClose synchronously unmarks the scrim's HitTestVisible when no non-closing entry remains) so
                // RedispatchContextAt hit-tests THROUGH the dying scrim. A modal scrim has no context handler (it eats
                // the press via OnPointerDown). Empty area under the point ⇒ dismiss only (no ContextBit found there).
                OnContextRequested = lightDismiss
                    ? args => { svc.CloseTopInputBlocking(OverlayCloseCause.LightDismiss); hooks.RedispatchContextAt?.Invoke(args.Position); }
                    : null,
            });
            foreach (var e in svc.Entries)
                layers.Add(Positioned(e) with { Key = "overlay:" + e.Id });
        }

        // The overlay stack FILLS the window via Grow + the parent's cross-stretch cascade. Do NOT pin Width/Height to a
        // vp literal here: this ZStack's size is mirrored up to the scene root (MirrorParticipation, Reconciler.cs:385-393)
        // at MOUNT, through the app shell (WaveeShell) which builds its frame ONCE and never re-renders on resize. A pinned
        // vp literal is therefore captured at the launch size and goes STALE on every later resize — FlexLayout.Run(root,
        // window) then reads the stale root li.Height (FlexLayout.cs:39-40) and lays the whole app out at the PRE-resize
        // size (player bar off-screen, sidebar/content clipped). Leaving the size indefinite (NaN) keeps the root tracking
        // the live window each frame. A tall popup still overflows freely: every node from the root down is sized by
        // Run(root, window) + cross-stretch, so the stack stays window-sized regardless of a tall child (no page reflow).
        // Reproduced + guarded by the real-Win32 resize harness; do not re-add the pin.
        return Ctx.Provide(Overlay.Service, (IOverlayService)svc,
            Ui.ZStack(layers.ToArray()) with { Grow = 1 });
    }

    /// <summary>Cascading-menu overlap (CascadingMenuHelper.cpp:678 — sub-menu lands at owner edge − 4): nudge the
    /// placed popup on X, MIRRORED when the positioner flipped it to the anchor's LEFT side so the overlap still
    /// points back onto the owner.</summary>
    internal static PopupPlacementResult ApplyAnchorOffsetX(PopupPlacementResult place, float offsetX, in RectF anchor)
    {
        if (offsetX == 0f) return place;
        bool leftOfAnchor = place.X + place.MeasuredW <= anchor.X + 0.5f;
        return place with { X = place.X + (leftOfAnchor ? -offsetX : offsetX) };
    }

    static BoxEl Positioned(OverlayEntry e) => e.Chrome == PopupChrome.Modal ? PositionedModal(e) : PositionedAnchored(e);

    // BOTH positioning hosts are full-bleed layers above the scrim and the page: they must gate their SUBTREE with
    // HitTestVisible (a closing popup goes inert) but must never take a hit THEMSELVES (HitTestPassThrough = self
    // yields, children keep). A handler-less full-bleed wrapper is invisible to the interaction-gated Hit walk, but
    // HitTestAny (the wheel's scroll-target resolve) is background-gated — without the pass-through, ANY open
    // popup/tooltip made the wrapper the window-wide deepest hit, whose ancestor chain has no scroller: wheel
    // scrolling died everywhere while a mere tooltip was showing, and outside-input probing above the scrim landed
    // on the wrapper instead of the surface beneath. A tooltip must NEVER block input (its bubble is already
    // hit-test-invisible); this keeps the promise at the host layer too.
    static BoxEl PositionedModal(OverlayEntry e) => new BoxEl
    {
        Width = float.NaN,
        Height = float.NaN,
        Grow = 1,
        HitTestVisible = e.Phase != OverlayPhase.Closing,
        HitTestPassThrough = true,
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
        HitTestVisible = e.Phase != OverlayPhase.Closing,
        HitTestPassThrough = true,
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
                // The translated positioning box sits exactly over the popup rect; it too must yield self (its
                // CONTENT — the FlyoutSurface plate — is the real hit surface) so a hit-test-invisible tooltip
                // bubble lets wheel/clicks fall through to the page beneath it.
                HitTestPassThrough = true,
                Children =
                [
                    Embed.Comp(() => new FlyoutSurface
                    {
                        Body = Ctx.Provide(Overlay.Placement, e.PlacementInfo, e.Content()),
                        Chrome = e.Chrome,
                        OnSurface = h => e.SurfaceNode = h,
                        OnPlate = h => e.PlateNode = h,
                    }),
                ],
            },
        ],
    };
}

/// <summary>WinUI MenuFlyoutPresenter surface. MENU chrome (<see cref="PopupChrome.Flyout"/>) is a two-layer split —
/// a transparent ZStack SURFACE that owns the open/close channels (translate + the authored rounded clip + the close
/// fade) over a stretch PLATE (acrylic + 1px flyout stroke + elevation shadow = the WinUI MenuFlyoutPresenterBorder
/// the open ScaleY-stretches) and an items layer. Non-menu chromes stay a single frosted card whose ClipToBounds
/// clips the content to both the rounded corners and the revealing window.</summary>
internal sealed class FlyoutSurface : Component
{
    public Element Body = new BoxEl();
    public PopupChrome Chrome = PopupChrome.Flyout;
    public Action<NodeHandle>? OnSurface;
    /// <summary>Menu chrome only: the stretch PLATE (acrylic + 1px flyout stroke + elevation shadow) — the engine's
    /// WinUI <c>MenuFlyoutPresenterBorder</c>, whose ScaleY the host animates during the open unfold.</summary>
    public Action<NodeHandle>? OnPlate;

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
                // A chrome-less surface is just a transform/clip host — the BODY decides what hit-tests. Without
                // this, HitTestAny (wheel scroll-target resolve) treated this transparent box as the deepest hit
                // over the popup rect, so a hit-test-invisible tooltip bubble still swallowed wheel under it.
                // Menu/Flyout chrome (below) keeps its opaque plate as a real hit surface — WinUI parity.
                HitTestPassThrough = true,
                Children = [Body],
            };
        }

        // The frosted popup card. Every WinUI transient surface carries the ONE default acrylic recipe
        // (Tok.AcrylicFlyout — AcrylicInAppFillColorDefaultBrush / the DesktopAcrylic default backdrop,
        // AcrylicBrush_themeresources.xaml; see Tokens.cs for the per-theme values):
        //   MenuFlyoutPresenter — transparent background + SystemBackdrop = AcrylicBackgroundFillColorDefaultBackdrop
        //     (MenuFlyout_themeresources.xaml:40/202 background + :264+271 backdrop), padding 0,2,0,2
        //     (MenuFlyoutPresenterThemePadding).
        //   ComboBox PopupBorder — Background = ComboBoxDropDownBackground = AcrylicInAppFillColorDefaultBrush
        //     (ComboBox_themeresources.xaml:63 dark / :273 light), border SystemControlTransientBorderBrush,
        //     PopupBorder padding 0 (ComboBoxDropdownBorderPadding, generic.xaml:126) — the 0,4 content inset is
        //     2px here + 2px in the ComboBox column (see ComboBox.cs).
        //   AutoSuggestBox SuggestionsContainer — Background = AcrylicBackgroundFillColorDefaultBrush
        //     (AutoSuggestBox_themeresources.xaml:5 dark / :17 light), Padding = AutoSuggestListMargin 0,2,0,2
        //     (generic.xaml:119), CornerRadius = OverlayCornerRadius on ALL corners (:283).
        //   FlyoutPresenter — Background = FlyoutPresenterBackground = AcrylicInAppFillColorDefaultBrush
        //     (FlyoutPresenter_themeresources.xaml:5 dark / :15 light); NO presenter padding here — the
        //     FlyoutContentPadding 16,15,16,17 card is supplied by the flyout content (GenericFlyout.cs).

        if (Chrome == PopupChrome.Flyout)
        {
            // MENU presenter (WinUI MenuFlyoutPresenter, generic.xaml:23796-23812): the root Grid carries the
            // background and translates+clips WITH the items, while a CHILDLESS sibling ring
            // ("MenuFlyoutPresenterBorder", generic.xaml:23810) is the part MenuPopupThemeTransition stretches
            // (ScaleY (1−ClosedRatio)→1, LayoutTransition_partial.cpp:497-503). Engine mapping: the SURFACE is a
            // transparent ZStack that owns the open/close channels (TranslateY + the authored rounded ClipRect +
            // the close fade; Corners = the rounded-clip radius — no ClipToBounds at rest, so the shadow halo
            // survives), and the PLATE under the items carries the visible chrome (acrylic + 1px flyout stroke +
            // elevation shadow) and receives the plate ScaleY — the frosted card, ring AND shadow always frame
            // exactly the revealed band. This also keeps the un-scissored acrylic composite and the pre-clip shadow
            // (SceneRecorder draws shadows BEFORE the node's own clip) INSIDE the reveal: the old single-card
            // surface painted both over the anchor for the first 250ms of the unfold (the SplitButton/DropDownButton
            // "menu overlaps the button" mid-open artifact).
            return new BoxEl
            {
                ZStack = true,
                AlignSelf = FlexAlign.Start,
                Fill = ColorF.Transparent,
                Corners = Radii.OverlayAll,
                OnRealized = h => OnSurface?.Invoke(h),
                Children =
                [
                    new BoxEl   // the plate — fills the stack (ZStack child without explicit size)
                    {
                        Fill = ColorF.Transparent,
                        Acrylic = Tok.AcrylicFlyout,
                        BorderColor = Tok.StrokeFlyoutDefault,
                        BorderWidth = 1f,
                        Corners = Radii.OverlayAll,
                        Shadow = Elevation.Flyout,
                        OnRealized = h => OnPlate?.Invoke(h),
                    },
                    new BoxEl   // the items layer — sizes the stack; rides the surface translate
                    {
                        Direction = 1,
                        Padding = new Edges4(0, 2, 0, 2),   // MenuFlyoutPresenterThemePadding
                        Children = [Body],
                    },
                ],
            };
        }

        bool menuPad = Chrome != PopupChrome.Popup;
        return new BoxEl
    {
        Direction = 1,
        AlignSelf = FlexAlign.Start,
        Fill = ColorF.Transparent,
        Acrylic = Tok.AcrylicFlyout,
        BorderColor = Tok.StrokeFlyoutDefault,
        BorderWidth = 1f,
        Corners = Radii.OverlayAll,
        Shadow = Elevation.Flyout,
        ClipToBounds = true,
        Padding = menuPad ? new Edges4(0, 2, 0, 2) : default,
        OnRealized = h => OnSurface?.Invoke(h),
        Children = [Body],
    };
    }
}
