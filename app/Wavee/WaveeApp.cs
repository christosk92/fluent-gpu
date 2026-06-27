using System;
using FluentGpu;          // FluentApp (OS theme facade + SystemColorsChanged relay)
using FluentGpu.Dsl;
using FluentGpu.Foundation;   // Diag.CompiledIn (debug-build gate for the FPS HUD)
using FluentGpu.Hooks;

namespace Wavee;

// The app root. Owns Services, provides the Services + PlaybackBridge contexts, wires the Core→Signal bridge on mount
// (and starts a fake session + playback so the shell is live), then renders the shell. The whole app blur-rises in.
sealed class WaveeApp : Component
{
    readonly Services _services;

    // The composition root passes the settings store created early (so the theme is seeded before the first frame);
    // null in tests falls back to the store Services creates itself.
    public WaveeApp(IAppSettings? settings = null) => _services = Services.UseRealBackend ? Services.CreateReal(settings) : Services.CreateFake(settings);

    public override Element Render()
    {
        var bridge = _services.Playback;
        var libBridge = _services.LibraryBridge;
        var store = _services.LibraryStore;

        // Follow the OS dark-mode / accent live WHILE the user hasn't pinned an explicit theme (mode == System). The host
        // relays WM_SETTINGCHANGE on the UI thread; we re-read the OS state, apply it, and animate the in-place re-theme.
        var requestTheme = UseContext(ThemeControl.Request);
        Context.UseEffect(() =>
        {
            FluentApp.SystemColorsChanged += () =>
            {
                if (_services.Settings.Get(WaveeSettings.ThemeMode) != 0) return;   // explicit Light/Dark pinned → ignore OS
                Theme.Dark = !FluentApp.SystemUsesLightTheme();
                if (FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
                requestTheme?.Invoke(250f);
            };
        });

        var post = Context.UsePost();
        Context.UseEffect(() =>
        {
            bridge.Activate(post);
            libBridge.Activate(post);
            store.Activate(post);
            _ = _services.Session.ConnectAsync();
            _ = _services.Player.ResumeAsync();
            _services.Log.Info("app", "Shell online; playback started");
        });

        this.UseSoftReveal(); // app entrance (compositor-only, reduced-motion-aware)

        var root = Ctx.Provide(Services.Slot, _services,
            Ctx.Provide(PlaybackBridge.Slot, bridge,
            Ctx.Provide(LibraryBridge.Slot, libBridge,
            Ctx.Provide(LibraryStore.Slot, store,
                Embed.Comp(() => new WaveeShell(_services.Settings))))));

        // Debug-build FPS HUD on top (const-folds out of Release; subscribes to the host's per-frame stats). The HUD pill is
        // pinned top-right by a full-bleed PASS-THROUGH positioner (a PLAIN BoxEl — its HitTestPassThrough IS honoured, unlike
        // a component wrapper's mirrored-but-not-passthrough node, which would swallow every hit and silently kill scrolling).
        // ZStack carries Grow=1 to fill the window + stretch the shell exactly like the OverlayHost stack.
        if (!Diag.CompiledIn || Diag.EnvFlag("WAVEE_NO_FPS")) return root;
        var hud = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.End,
            Padding = new Edges4(0f, 104f, 14f, 0f),   // clear the title bar + toolbar; pinned top-right of the content
            Children = [ Embed.Comp(() => new FpsOverlay()) ],
        };
        return Ui.ZStack(root, hud) with { Grow = 1f };
    }
}