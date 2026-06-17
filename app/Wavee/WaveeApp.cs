using System;
using FluentGpu;          // FluentApp (OS theme facade + SystemColorsChanged relay)
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

// The app root. Owns Services, provides the Services + PlaybackBridge contexts, wires the Core→Signal bridge on mount
// (and starts a fake session + playback so the shell is live), then renders the shell. The whole app blur-rises in.
sealed class WaveeApp : Component
{
    readonly Services _services;

    // The composition root passes the settings store created early (so the theme is seeded before the first frame);
    // null in tests falls back to the store Services creates itself.
    public WaveeApp(IAppSettings? settings = null) => _services = Services.CreateFake(settings);

    public override Element Render()
    {
        var bridge = _services.Playback;

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
            _ = _services.Session.ConnectAsync();
            _ = _services.Player.ResumeAsync();
            _services.Log.Info("app", "Shell online; playback started");
        });

        this.UseSoftReveal(); // app entrance (compositor-only, reduced-motion-aware)

        return Ctx.Provide(Services.Slot, _services,
            Ctx.Provide(PlaybackBridge.Slot, bridge,
                Embed.Comp(() => new WaveeShell(_services.Settings))));
    }
}