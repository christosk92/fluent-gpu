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
/// clip reveal 250ms with NO content translate and NO open fade; close = 167ms clip collapse + late 83ms fade.</item>
/// <item><see cref="Raw"/> — ToolTip/Slider value tip: FadeIn/FadeOutThemeAnimation (TAS_FADEIN/TAS_FADEOUT, 167ms linear).</item>
/// <item><see cref="Modal"/> — ContentDialog scale/fade; <see cref="TeachingTip"/> — muxc expand/contract scale.</item>
/// <item><see cref="Static"/> — a bare WinUI <c>Popup</c> with no transitions (the AutoSuggestBox SuggestionsPopup,
/// generic.xaml AutoSuggestBox template — no TransitionCollection and AutoSuggestBox_Partial.cpp adds none): instant.</item>
/// </list></summary>
public enum PopupChrome : byte { Flyout, Raw, Modal, TeachingTip, Popup, Dropdown, Static }

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
}

public interface IOverlayService
{
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft);
    OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options);

    /// <summary>Open anchored to an arbitrary window-DIP RECT instead of a scene node — pointer-relative placement
    /// (ToolTip PlacementMode.Mouse, context menus at the right-tap point). <paramref name="owner"/> (optional) is the
    /// logical owner node, used only to resolve the nested-overlay parent chain for cascade close.</summary>
    OverlayHandle OpenAt(Func<RectF> anchorRect, Func<Element> content, FlyoutPlacement placement, PopupOptions options = default, Func<NodeHandle>? owner = null);

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
    public float MeasuredW;
    public float MeasuredH;
    public bool OpensUp;
    public CornerJoin CornerJoin;     // which popup corners abut the anchor (corner-squaring for ComboBox/AutoSuggestBox)
    public NodeHandle SavedFocus;     // focus captured at open time → restored when the close STARTS (WinUI timing)
    public bool FocusTrap;
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
        => OpenCore(anchor, null, content, placement, options);

    public OverlayHandle OpenAt(Func<RectF> anchorRect, Func<Element> content, FlyoutPlacement placement, PopupOptions options = default, Func<NodeHandle>? owner = null)
        => OpenCore(owner ?? (static () => NodeHandle.Null), anchorRect, content, placement, options);

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
            if (e.OpensUp)
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
            scene.Mark(e.SurfaceNode, NodeFlags.PaintDirty);
            float fadeMs = e.Chrome == PopupChrome.Raw ? RawFadeMs : OpacityMs;
            anim.Animate(e.SurfaceNode, AnimChannel.Opacity, currentOpacity, 0f, fadeMs, Easing.Linear);
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
        if (key == Keys.Escape && AnyOpen) { CloseTop(OverlayCloseCause.Escape); return true; }
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
        var vp = UseContext(Viewport.Size);

        // After layout (Bounds valid): place each popup, then seed its open (clip-unfold + slide) / close (fade) animation.
        UseLayoutEffect(() =>
        {
            var scene = Context.Scene;
            var anim = Context.Anim;
            if (scene is null || anim is null) return;
            var vpRect = new RectF(0f, 0f, vp.Width, vp.Height);
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
                        bool wantWindowed = !e.ConstrainToRootBounds && svc.Hooks is { OpenPopupWindow: not null };
                        RectF container = vpRect;
                        if (wantWindowed && svc.Hooks!.GetWorkArea is { } workArea)
                            container = workArea(new Point2(aRect.X + aRect.W * 0.5f, aRect.Y + aRect.H * 0.5f));

                        var place = FlyoutPositioner.Place(in aRect, in popupSize, in container, e.Placement, isWindowed: wantWindowed);

                        if (wantWindowed)
                        {
                            // Lease a platform popup window for this subtree (host records it into its own DrawList +
                            // swapchain; the subtree is skipped from the main-window record). -1 = the platform/device
                            // can't (WinUI's DoesPlatformSupportWindowedPopup == false) → constrained fallback.
                            if (e.PopupWindowToken < 0)
                                e.PopupWindowToken = svc.Hooks!.OpenPopupWindow!(e.WrapperNode);
                            if (e.PopupWindowToken >= 0)
                                svc.Hooks!.SetPopupWindowBounds?.Invoke(e.PopupWindowToken, new RectF(place.X, place.Y, pRect.W, pRect.H));
                            else
                                place = FlyoutPositioner.Place(in aRect, in popupSize, in vpRect, e.Placement, isWindowed: false);
                        }

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
                    if (e.OpensUp)
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipT, fullH - closedH, 0f, OpenMs, Easing.FluentPopOpen);
                    else
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipB, closedH, fullH, OpenMs, Easing.FluentPopOpen);
                }
                else if (!e.OpenSeeded && e.MeasuredH > 0f)
                {
                    e.OpenSeeded = true;
                    e.Phase = OverlayPhase.Open;
                    float fullH = e.MeasuredH;
                    // WinUI MenuPopupThemeTransition load (LayoutTransition_partial.cpp:441-506): BOTH a content
                    // TranslateY (initialTranslateY = openedLength × ClosedRatio, signed by direction → 0) and a
                    // counter-translated clip animate over s_OpenDuration=250ms cubic-bezier(0,0,0,1)
                    // (cpp:443-444 easing.cp3.X=0; MenuPopupThemeTransition_Partial.h:24). The menu unfolds from the
                    // anchor edge WITH its content sliding in — and there is NO opacity keyframe on the presenter at
                    // load (only the overlay element fades, cpp:508-519): it appears at 50% height instantly.
                    // Our clip rect is node-local (it rides the surface's translate), so the counter-translation is
                    // expressed by animating the NEAR clip edge against the slide — the anchor-side edge of the
                    // visible window stays glued in parent space while the far edge expands.
                    float slide = fullH * ClosedRatio;
                    if (e.OpensUp)
                    {
                        // Opens upward (WinUI AnimationDirection_Bottom): content slides UP into place; the visible
                        // window's BOTTOM edge stays glued to the anchor while the top edge reveals upward.
                        anim.Animate(e.SurfaceNode, AnimChannel.TranslateY, slide, 0f, OpenMs, Easing.FluentPopOpen);
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipB, fullH - slide, fullH, OpenMs, Easing.FluentPopOpen);
                    }
                    else
                    {
                        // Opens downward (WinUI AnimationDirection_Top): content slides DOWN into place; the visible
                        // window's TOP edge stays glued to the anchor while the bottom edge reveals downward.
                        anim.Animate(e.SurfaceNode, AnimChannel.TranslateY, -slide, 0f, OpenMs, Easing.FluentPopOpen);
                        anim.Animate(e.SurfaceNode, AnimChannel.ClipT, slide, 0f, OpenMs, Easing.FluentPopOpen);
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
                HitTestVisible = svc.AnyOpen,
                TabStop = false,   // WinUI: the light-dismiss layer is never a tab stop (focus restore depends on it)
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
/// stroke, a soft elevation shadow, and rounded corners. The OverlayHost drives the open clip-reveal + content slide
/// and the close fade on this single node; ClipToBounds clips the content to both the rounded corners and the
/// revealing window.</summary>
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
