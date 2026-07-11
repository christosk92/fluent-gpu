using System;
using System.Numerics;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.WinRT.WinRT;
using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// True desktop-sampling acrylic for a windowed popup, built with OS-level <c>Windows.UI.Composition</c> (no Windows App
/// SDK, no CsWinRT — hand-vtable WinRT interop via TerraFX). This mirrors WinUI's windowed-popup architecture
/// (microsoft-ui-xaml popup-system-backdrop.md): the popup HWND is just a transparent VIEWPORT, and every visible chrome
/// layer lives under ONE animation root so they move together:
/// <code>
///   root → animationRoot{ backdropGroup{ host-backdrop sprite, tint sprite }  (rounded-clipped to the content rect)
///                         content sprite (the popup's D3D12 swapchain: transparent plate + 1px border + items) }
/// </code>
/// Open = <c>animationRoot.Offset.Y</c> slides from the anchor edge to 0 over 250ms cubic-bezier(0,0,0,1); the popup
/// window clips the overflow (WinUI MenuPopupThemeTransition — no inset clip needed). Close = <c>animationRoot.Opacity</c>
/// 1→0 over 83ms, so the ACRYLIC fades out too (not just the engine content). The composition KeyFrameAnimations run
/// autonomously on the compositor once committed (the thread's DispatcherQueue commits on the next message pump — the host
/// keeps pumping briefly via <see cref="IsAnimating"/>). Hosted via <c>ICompositorDesktopInterop.CreateDesktopWindowTarget</c>;
/// single-threaded (created/updated on the UI thread). DWM draws NO chrome (the HWND is DWMWCP_DONOTROUND).
/// </summary>
internal sealed unsafe class CompositionBackdrop : IDisposable
{
    // ── shared per-process composition device (UI thread) ───────────────────────────────────────────────────────────
    private static bool s_roInit;
    private static IDispatcherQueueController* s_dq;
    private static ICompositor* s_comp;
    private static ICompositor2* s_comp2;   // CreateDropShadow
    private static ICompositor3* s_comp3;
    private static ICompositor5* s_comp5;   // CreateRoundedRectangleGeometry
    private static ICompositor6* s_comp6;   // CreateGeometricClipWithGeometry
    private static ICompositorWithVisualSurface* s_compVS;   // CreateVisualSurface (rounded-shadow mask)
    private static ICompositorDesktopInterop* s_deskInterop;
    private static ICompositorInterop* s_interop;

    private static void EnsureCompositor()
    {
        if (s_comp != null) return;
        if (!s_roInit) { RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED); s_roInit = true; }

        // A Compositor needs a DispatcherQueue on the thread. Create one for the current (UI) thread; harmless if one exists.
        DispatcherQueueOptions opts = default;
        opts.dwSize = (uint)sizeof(DispatcherQueueOptions);
        opts.threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT;
        opts.apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE;
        IDispatcherQueueController* dq;
        CreateDispatcherQueueController(opts, &dq);
        s_dq = dq;

        IInspectable* insp = null;
        const string clsName = "Windows.UI.Composition.Compositor";
        fixed (char* p = clsName)
        {
            HSTRING cls;
            Check(WindowsCreateString(p, (uint)clsName.Length, &cls), "WindowsCreateString");
            Check(RoActivateInstance(cls, &insp), "RoActivateInstance(Compositor)");
            WindowsDeleteString(cls);
        }
        ICompositor* comp;
        Check(insp->QueryInterface(__uuidof<ICompositor>(), (void**)&comp), "QI ICompositor");
        s_comp = comp;
        ICompositor2* c2; Check(comp->QueryInterface(__uuidof<ICompositor2>(), (void**)&c2), "QI ICompositor2"); s_comp2 = c2;
        ICompositor3* c3; Check(comp->QueryInterface(__uuidof<ICompositor3>(), (void**)&c3), "QI ICompositor3"); s_comp3 = c3;
        ICompositor5* c5; Check(comp->QueryInterface(__uuidof<ICompositor5>(), (void**)&c5), "QI ICompositor5"); s_comp5 = c5;
        ICompositor6* c6; Check(comp->QueryInterface(__uuidof<ICompositor6>(), (void**)&c6), "QI ICompositor6"); s_comp6 = c6;
        ICompositorWithVisualSurface* cvs; Check(comp->QueryInterface(__uuidof<ICompositorWithVisualSurface>(), (void**)&cvs), "QI ICompositorWithVisualSurface"); s_compVS = cvs;
        ICompositorDesktopInterop* di; Check(comp->QueryInterface(__uuidof<ICompositorDesktopInterop>(), (void**)&di), "QI ICompositorDesktopInterop"); s_deskInterop = di;
        ICompositorInterop* ci; Check(comp->QueryInterface(__uuidof<ICompositorInterop>(), (void**)&ci), "QI ICompositorInterop"); s_interop = ci;
        insp->Release();
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private static HSTRING H(string s)
    {
        fixed (char* p = s) { HSTRING h; Check(WindowsCreateString(p, (uint)s.Length, &h), "WindowsCreateString"); return h; }
    }

    // ── per-popup state ─────────────────────────────────────────────────────────────────────────────────────────────
    private IDesktopWindowTarget* _target;
    private ICompositionTarget* _ctarget;
    private IContainerVisual* _root;
    private IVisual* _rootVisual;
    private IContainerVisual* _animRoot;        // slides on open (Offset.Y)
    private IVisual* _animRootVisual;
    private IContainerVisual* _chrome;          // shadow + acrylic group; fades together on close (Opacity). NOT clipped — the shadow extends past the plate
    private IVisual* _chromeVisual;
    private ISpriteVisual* _shadow;             // transparent body; its DropShadow (masked to the rounded acrylic shape) IS the menu drop shadow
    private IDropShadow* _shadowDrop;
    private ICompositionVisualSurface* _maskSurface;   // captures the rounded acrylic group's alpha → the shadow's rounded shape
    private IContainerVisual* _group;           // backdrop + tint, rounded-clipped to the content rect
    private IVisual* _groupVisual;
    private ISpriteVisual* _backdrop;
    private ISpriteVisual* _tint;
    private ISpriteVisual* _content;
    private ICompositionSurface* _surface;
    private ICompositionGeometricClip* _roundClip;
    private ICompositionRoundedRectangleGeometry* _roundGeo;
    private float _cornerRadiusPx;
    private float _w, _h;

    // ── motion ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private const long OpenMs = 250, CloseMs = 83;
    private bool _opensUp;
    private float _closedRatio = 0.5f;          // WinUI MenuPopupThemeTransition: 0.5 root menu, 0.67 cascaded submenu
    private float _contentHPx;
    private long _motionStartTick;
    private bool _opened, _closing;

    /// <summary>Build the composition tree on the popup HWND, with the popup's swapchain as the content surface.</summary>
    public CompositionBackdrop(HWND hwnd, IUnknown* swapChain, ColorF tint, float cornerRadiusPx)
    {
        _cornerRadiusPx = cornerRadiusPx;
        EnsureCompositor();

        IDesktopWindowTarget* tgt;
        Check(s_deskInterop->CreateDesktopWindowTarget(hwnd, BOOL.FALSE, &tgt), "CreateDesktopWindowTarget");
        _target = tgt;
        ICompositionTarget* ct; Check(tgt->QueryInterface(__uuidof<ICompositionTarget>(), (void**)&ct), "QI ICompositionTarget"); _ctarget = ct;

        IContainerVisual* root; Check(s_comp->CreateContainerVisual(&root), "CreateContainerVisual(root)"); _root = root;
        _rootVisual = AsVisual((IUnknown*)root);
        Check(_ctarget->put_Root(_rootVisual), "put_Root");

        // animation root — open slides its Offset.Y (the popup HWND clips the overflow, WinUI's windowed-popup viewport).
        IContainerVisual* ar; Check(s_comp->CreateContainerVisual(&ar), "CreateContainerVisual(animRoot)"); _animRoot = ar;
        _animRootVisual = AsVisual((IUnknown*)ar);
        AddChild(_root, _animRootVisual);

        // chrome — holds the drop shadow + the acrylic group and fades them TOGETHER on close (Opacity). Unclipped, so the
        // shadow extends past the plate into the window's shadow-inset margins.
        IContainerVisual* chr; Check(s_comp->CreateContainerVisual(&chr), "CreateContainerVisual(chrome)"); _chrome = chr;
        _chromeVisual = AsVisual((IUnknown*)chr);
        AddChild(_animRoot, _chromeVisual);

        // acrylic group (rounded-clipped to the content rect). Created now so the shadow mask can capture it; added to chrome
        // AFTER the shadow so it sits ON TOP and (being opaque) occludes the shadow's center, leaving only the outer ring.
        IContainerVisual* grp; Check(s_comp->CreateContainerVisual(&grp), "CreateContainerVisual(group)"); _group = grp;
        _groupVisual = AsVisual((IUnknown*)grp);
        ICompositionRoundedRectangleGeometry* rg; Check(s_comp5->CreateRoundedRectangleGeometry(&rg), "CreateRoundedRectangleGeometry"); _roundGeo = rg;
        ICompositionGeometry* rgGeom; Check(rg->QueryInterface(__uuidof<ICompositionGeometry>(), (void**)&rgGeom), "QI ICompositionGeometry");
        ICompositionGeometricClip* rc; Check(s_comp6->CreateGeometricClipWithGeometry(rgGeom, &rc), "CreateGeometricClipWithGeometry"); _roundClip = rc;
        rgGeom->Release();
        ICompositionClip* rcc; Check(rc->QueryInterface(__uuidof<ICompositionClip>(), (void**)&rcc), "QI ICompositionClip(round)");
        Check(_groupVisual->put_Clip(rcc), "put_Clip(group round)"); rcc->Release();

        // drop shadow — a transparent sprite whose DropShadow is MASKED by the acrylic group's rendered alpha (captured via a
        // VisualSurface), so the shadow takes the rounded plate's exact shape. Blur/offset/opacity are set in ConfigureChrome.
        _shadow = MakeSprite();
        ICompositionVisualSurface* msv; Check(s_compVS->CreateVisualSurface(&msv), "CreateVisualSurface"); _maskSurface = msv;
        Check(msv->put_SourceVisual(_groupVisual), "put_SourceVisual");
        ICompositionSurface* msurf; Check(msv->QueryInterface(__uuidof<ICompositionSurface>(), (void**)&msurf), "QI ICompositionSurface(mask)");
        ICompositionSurfaceBrush* msb; Check(s_comp->CreateSurfaceBrushWithSurface(msurf, &msb), "CreateSurfaceBrushWithSurface(mask)"); msurf->Release();
        ICompositionBrush* maskBrush; Check(msb->QueryInterface(__uuidof<ICompositionBrush>(), (void**)&maskBrush), "QI ICompositionBrush(mask)"); msb->Release();
        IDropShadow* ds; Check(s_comp2->CreateDropShadow(&ds), "CreateDropShadow"); _shadowDrop = ds;
        Check(ds->put_Color(new Color { A = 255, R = 0, G = 0, B = 0 }), "shadow put_Color");
        Check(ds->put_Mask(maskBrush), "shadow put_Mask"); maskBrush->Release();
        ICompositionShadow* dsBase; Check(ds->QueryInterface(__uuidof<ICompositionShadow>(), (void**)&dsBase), "QI ICompositionShadow");
        ISpriteVisual2* sh2; Check(_shadow->QueryInterface(__uuidof<ISpriteVisual2>(), (void**)&sh2), "QI ISpriteVisual2");
        Check(sh2->put_Shadow(dsBase), "put_Shadow"); sh2->Release(); dsBase->Release();
        AddChild(_chrome, AsVisual((IUnknown*)_shadow));   // shadow at the BOTTOM of chrome
        AddChild(_chrome, _groupVisual);                    // acrylic group ON TOP of the shadow

        // 1) host-backdrop (desktop-sampling, pre-blurred) sprite
        _backdrop = MakeSprite();
        ICompositionBackdropBrush* host; Check(s_comp3->CreateHostBackdropBrush(&host), "CreateHostBackdropBrush");
        SetBrush(_backdrop, (IUnknown*)host); host->Release();
        AddChild(_group, AsVisual((IUnknown*)_backdrop));

        // 2) flat tint sprite over the backdrop (straight color; alpha = acrylic tint opacity)
        _tint = MakeSprite();
        Color tc = new() { A = (byte)(tint.A * 255f), R = (byte)(tint.R * 255f), G = (byte)(tint.G * 255f), B = (byte)(tint.B * 255f) };
        ICompositionColorBrush* cb; Check(s_comp->CreateColorBrushWithColor(tc, &cb), "CreateColorBrushWithColor");
        SetBrush(_tint, (IUnknown*)cb); cb->Release();
        AddChild(_group, AsVisual((IUnknown*)_tint));

        // 3) the popup's D3D12 swapchain as the content surface (transparent plate + border + text), over the acrylic
        _content = MakeSprite();
        ICompositionSurface* surf; Check(s_interop->CreateCompositionSurfaceForSwapChain(swapChain, &surf), "CreateCompositionSurfaceForSwapChain");
        _surface = surf;
        ICompositionSurfaceBrush* sb; Check(s_comp->CreateSurfaceBrushWithSurface(surf, &sb), "CreateSurfaceBrushWithSurface");
        SetBrush(_content, (IUnknown*)sb); sb->Release();
        AddChild(_animRoot, AsVisual((IUnknown*)_content));
    }

    private static IVisual* AsVisual(IUnknown* o)
    {
        IVisual* v; Check(o->QueryInterface(__uuidof<IVisual>(), (void**)&v), "QI IVisual");
        return v;
    }

    private ISpriteVisual* MakeSprite()
    {
        ISpriteVisual* s; Check(s_comp->CreateSpriteVisual(&s), "CreateSpriteVisual");
        return s;
    }

    private static void SetBrush(ISpriteVisual* sprite, IUnknown* brush)
    {
        ICompositionBrush* cb; Check(brush->QueryInterface(__uuidof<ICompositionBrush>(), (void**)&cb), "QI ICompositionBrush");
        Check(sprite->put_Brush(cb), "put_Brush");
        cb->Release();
    }

    private static void AddChild(IContainerVisual* parent, IVisual* child)
    {
        IVisualCollection* kids; Check(parent->get_Children(&kids), "get_Children");
        Check(kids->InsertAtTop(child), "InsertAtTop");
        kids->Release();
    }

    /// <summary>Size the window-filling visuals to the popup's client size (physical px). The rounded-acrylic geometry is
    /// NOT sized here — it's set to the inset content rect in <see cref="ConfigureChrome"/>.</summary>
    public void SetBounds(float w, float h)
    {
        if (w == _w && h == _h) return;
        _w = w; _h = h;
        var size = new Vector2(w, h);
        Check(_rootVisual->put_Size(size), "put_Size(root)");
        Check(_animRootVisual->put_Size(size), "put_Size(animRoot)");
        Check(_chromeVisual->put_Size(size), "put_Size(chrome)");
        Check(_groupVisual->put_Size(size), "put_Size(group)");
        PutSpriteSize(_backdrop, size);
        PutSpriteSize(_tint, size);
        PutSpriteSize(_content, size);
        PutSpriteSize(_shadow, size);
        Check(_maskSurface->put_SourceSize(size), "mask put_SourceSize");   // capture the whole group (rounded acrylic + transparent margins)
        if (_contentHPx <= 0f)   // not configured yet — round to the whole window as a sane default
        {
            Check(_roundGeo->put_Size(size), "round put_Size");
            Check(_roundGeo->put_CornerRadius(new Vector2(_cornerRadiusPx, _cornerRadiusPx)), "round put_CornerRadius");
        }
    }

    private static void PutSpriteSize(ISpriteVisual* s, Vector2 size)
    {
        IVisual* v = AsVisual((IUnknown*)s);
        Check(v->put_Size(size), "put_Size");
        v->Release();
    }

    /// <summary>Configure the chrome for the current placement: round the host-acrylic to the menu plate rect (inside the
    /// shadow margins) and stash the open-motion parameters. Called on each placement, before <see cref="AnimateOpen"/>.</summary>
    public void ConfigureChrome(RectF contentPx, bool opensUp, float closedRatio, float cornerRadiusPx)
    {
        _opensUp = opensUp;
        // closedRatio 0 is a CONTRACT value: no open slide at all (the CommandBar chrome — WinUI CommandBarFlyout
        // suppresses the popup transition and fades its own body over 83ms, CommandBarFlyout.cpp:43-44 +
        // CommandBarFlyout_themeresources.xaml:655-658). Negative = unset → the menu default 0.5.
        _closedRatio = closedRatio >= 0f ? closedRatio : 0.5f;
        _contentHPx = contentPx.H;
        _cornerRadiusPx = cornerRadiusPx;
        Check(_roundGeo->put_Offset(new Vector2(contentPx.X, contentPx.Y)), "round put_Offset");
        Check(_roundGeo->put_Size(new Vector2(contentPx.W, contentPx.H)), "round put_Size");
        Check(_roundGeo->put_CornerRadius(new Vector2(cornerRadiusPx, cornerRadiusPx)), "round put_CornerRadius");

        // Drop-shadow recipe to fit the WinUI medium-popup insets (L10 T2 R10 B18 DIP): blur ≈ side inset (10), offset down
        // ≈ (B−T)/2 (8) — at scale, blur extends ~10 sideways, ~2 up, ~18 down. cornerRadiusPx = OverlayCornerRadius(8 DIP)·scale.
        float scale = cornerRadiusPx > 0f ? cornerRadiusPx / 8f : 1f;
        Check(_shadowDrop->put_BlurRadius(10f * scale), "shadow put_BlurRadius");
        Check(_shadowDrop->put_Offset(new Vector3(0f, 8f * scale, 0f)), "shadow put_Offset");
        Check(_shadowDrop->put_Opacity(0.26f), "shadow put_Opacity");
    }

    /// <summary>True for a short window while an open/close motion commits + settles — the host keeps pumping the
    /// DispatcherQueue (WakeReasons.PopupAnim) so the StartAnimation commits, and defers disposal until a close fade ends.
    /// The animation itself then runs autonomously on the compositor.</summary>
    public bool IsAnimating
    {
        get
        {
            long e = Environment.TickCount64 - _motionStartTick;
            if (_closing) return e < CloseMs + 80;
            return _opened && e < OpenMs + 100;
        }
    }

    /// <summary>Open motion (WinUI MenuPopupThemeTransition): seed <c>animationRoot.Offset.Y</c> to the anchor-side slide
    /// (+slide opensUp / −slide downward; slide = contentHeight·closedRatio), then animate it → 0 over 250ms
    /// cubic-bezier(0,0,0,1), no opacity fade. The window clips the overflow. Runs once.</summary>
    public void AnimateOpen()
    {
        if (_opened || _contentHPx <= 0f) return;
        _opened = true;
        if (_closedRatio <= 0f) return;   // no-slide chrome (CommandBar): appear in place, the body fades itself
        _motionStartTick = Environment.TickCount64;

        float slide = _contentHPx * _closedRatio;
        float initial = _opensUp ? slide : -slide;
        Check(_animRootVisual->put_Offset(new Vector3(0f, initial, 0f)), "put_Offset(seed)");

        IScalarKeyFrameAnimation* anim; Check(s_comp->CreateScalarKeyFrameAnimation(&anim), "CreateScalarKeyFrameAnimation");
        ICubicBezierEasingFunction* ease; Check(s_comp->CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f), &ease), "CreateCubicBezierEasingFunction");
        ICompositionEasingFunction* ef; Check(ease->QueryInterface(__uuidof<ICompositionEasingFunction>(), (void**)&ef), "QI ICompositionEasingFunction");
        Check(anim->InsertKeyFrameWithEasingFunction(1f, 0f, ef), "InsertKeyFrame");
        IKeyFrameAnimation* kf; Check(anim->QueryInterface(__uuidof<IKeyFrameAnimation>(), (void**)&kf), "QI IKeyFrameAnimation");
        Check(kf->put_Duration(TimeSpan.FromMilliseconds(OpenMs)), "put_Duration");

        ICompositionObject* obj; Check(_animRootVisual->QueryInterface(__uuidof<ICompositionObject>(), (void**)&obj), "QI ICompositionObject(animRoot)");
        ICompositionAnimation* ca; Check(anim->QueryInterface(__uuidof<ICompositionAnimation>(), (void**)&ca), "QI ICompositionAnimation");
        HSTRING prop = H("Offset.Y");
        Check(obj->StartAnimation(prop, ca), "StartAnimation(Offset.Y)");
        WindowsDeleteString(prop);

        ca->Release(); obj->Release(); kf->Release(); ef->Release(); ease->Release(); anim->Release();
    }

    /// <summary>Close motion: fade the CHROME (acrylic + shadow) opacity 1→0 over 83ms linear. The engine fades the content
    /// (swapchain) over the same 83ms, so content + acrylic each fade exactly once (no double-fade). The host keeps the
    /// window alive until <see cref="IsAnimating"/> clears, then disposes — so the acrylic fades out instead of vanishing.</summary>
    public void AnimateClose()
    {
        if (_closing) return;
        _closing = true;
        _motionStartTick = Environment.TickCount64;

        IScalarKeyFrameAnimation* anim; Check(s_comp->CreateScalarKeyFrameAnimation(&anim), "CreateScalarKeyFrameAnimation(close)");
        Check(anim->InsertKeyFrame(1f, 0f), "InsertKeyFrame(close)");   // linear fade to 0
        IKeyFrameAnimation* kf; Check(anim->QueryInterface(__uuidof<IKeyFrameAnimation>(), (void**)&kf), "QI IKeyFrameAnimation(close)");
        Check(kf->put_Duration(TimeSpan.FromMilliseconds(CloseMs)), "put_Duration(close)");

        ICompositionObject* obj; Check(_chromeVisual->QueryInterface(__uuidof<ICompositionObject>(), (void**)&obj), "QI ICompositionObject(close)");
        ICompositionAnimation* ca; Check(anim->QueryInterface(__uuidof<ICompositionAnimation>(), (void**)&ca), "QI ICompositionAnimation(close)");
        HSTRING prop = H("Opacity");
        Check(obj->StartAnimation(prop, ca), "StartAnimation(Opacity)");
        WindowsDeleteString(prop);

        ca->Release(); obj->Release(); kf->Release(); anim->Release();
    }

    public void Dispose()
    {
        if (_content != null) { _content->Release(); _content = null; }
        if (_tint != null) { _tint->Release(); _tint = null; }
        if (_backdrop != null) { _backdrop->Release(); _backdrop = null; }
        if (_surface != null) { _surface->Release(); _surface = null; }
        if (_shadowDrop != null) { _shadowDrop->Release(); _shadowDrop = null; }
        if (_maskSurface != null) { _maskSurface->Release(); _maskSurface = null; }
        if (_shadow != null) { _shadow->Release(); _shadow = null; }
        if (_roundClip != null) { _roundClip->Release(); _roundClip = null; }
        if (_roundGeo != null) { _roundGeo->Release(); _roundGeo = null; }
        if (_groupVisual != null) { _groupVisual->Release(); _groupVisual = null; }
        if (_group != null) { _group->Release(); _group = null; }
        if (_chromeVisual != null) { _chromeVisual->Release(); _chromeVisual = null; }
        if (_chrome != null) { _chrome->Release(); _chrome = null; }
        if (_animRootVisual != null) { _animRootVisual->Release(); _animRootVisual = null; }
        if (_animRoot != null) { _animRoot->Release(); _animRoot = null; }
        if (_rootVisual != null) { _rootVisual->Release(); _rootVisual = null; }
        if (_root != null) { _root->Release(); _root = null; }
        if (_ctarget != null) { _ctarget->Release(); _ctarget = null; }
        if (_target != null) { _target->Release(); _target = null; }
    }
}
