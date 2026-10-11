using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// EP7 / E9 (<c>FontRegistry</c>): one cache of font faces for every text consumer, family CHAINS that fall back along the
/// declared list and treat falling off it as an error, <c>"file#Family"</c> selecting the face by family name, a preload that
/// leaves the UI thread nothing to create, and a body-font change that evicts the measure caches. Everything runs on the
/// headless <see cref="HeadlessFontFaceSource"/> (no DirectWrite, TerraFX-free) through the same registry the Windows
/// engines use.
/// </summary>
static class FontRegistryChecks
{
    public static void Run(StringTable strings)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        ChainAndFileChecks();
        OffChainChecks();
        OneCacheChecks();
        PreloadChecks();
        BodyChangeChecks(strings);
    }

    static HeadlessFontFaceSource NewSource() => new HeadlessFontFaceSource("Fake UI", "Fake UI Display", "Fake Icons")
        .AddFile("fonts/inter.ttc", "Inter", "Inter Display")
        .AddFile("fonts/solo.ttf", "Solo Sans");

    // ══ chains and #Family ══════════════════════════════════════════════════════════════════════════════════════════════

    static void ChainAndFileChecks()
    {
        var src = NewSource();
        var errors = new List<string>();
        var reg = new FontRegistry(src, () => "Fake UI") { ErrorSink = errors.Add };

        nint chain = reg.ResolveFace("Missing One, Fake UI Display, Fake UI", 400, out _, out string fam);
        bool walks = chain != 0 && fam == "Fake UI Display" && src.Describe(chain) == "system:Fake UI Display@400" && reg.ChainViolations == 0;
        Check("gate.fonts.chain-walk a family chain \"Missing One, Fake UI Display, Fake UI\" resolves to the FIRST entry that exists (Fake UI Display), with no violation: falling back along the declared chain is the style's choice",
            walks, $"family=\"{fam}\" face={src.Describe(chain)} violations={reg.ChainViolations}");

        nint display = reg.ResolveFace("fonts/inter.ttc#Inter Display", 400, out _);
        nint plain = reg.ResolveFace("fonts/inter.ttc#Inter", 400, out _);
        nint folded = reg.ResolveFace("fonts/inter.ttc#inter display", 400, out _);
        nint bold = reg.ResolveFace("fonts/inter.ttc#Inter Display", 700, out _);
        nint noName = reg.ResolveFace("fonts/inter.ttc", 400, out _);
        nint solo = reg.ResolveFace("fonts/solo.ttf#Wrong Name", 400, out _);
        bool selects = src.Describe(display) == "file:fonts/inter.ttc[1]@400"
                    && src.Describe(plain) == "file:fonts/inter.ttc[0]@400"
                    && folded == display                                   // the name match is case-insensitive and the face cached once
                    && src.Describe(bold) == "file:fonts/inter.ttc[1]@700" && bold != display   // a weight is its own face
                    && src.Describe(noName) == "file:fonts/inter.ttc[0]@400"                    // no #Family: the first face
                    && src.Describe(solo) == "file:fonts/solo.ttf[0]@400"                       // a single-face file is unambiguous
                    && errors.Exists(e => e.Contains("Solo Sans", StringComparison.Ordinal))    // ... but the wrong name is logged
                    && reg.ChainViolations == 0;
        Check("gate.fonts.family-selects-face \"path.ttf#Family\" selects the face of the file whose family name matches (a .ttc holding Inter and Inter Display yields each by name, case-insensitively), a weight is its own face, and a single-face file with a wrong name still resolves its only face with a logged warning",
            selects, $"display={src.Describe(display)} plain={src.Describe(plain)} folded==display:{folded == display} bold={src.Describe(bold)} noName={src.Describe(noName)} solo={src.Describe(solo)}");
    }

    // ══ falling off the chain ═══════════════════════════════════════════════════════════════════════════════════════════

    static void OffChainChecks()
    {
        var src = NewSource();
        var errors = new List<string>();
        var reg = new FontRegistry(src, () => "Fake UI") { ErrorSink = errors.Add };

        nint face = reg.ResolveFace("Nope, Nada", 400, out _, out string fam);
        bool counted = reg.ChainViolations == 1
                    && reg.LastViolation is { } last && last.Contains("Nope, Nada", StringComparison.Ordinal)
                    && errors.Count == 1;                                  // logged (Release too: the sink is the log)
        bool stillText = face != 0 && fam == "Fake UI";                    // never no text: the body chain stands in
        reg.ResolveFace("Nope, Nada", 400, out _);
        bool onceOnly = reg.ChainViolations == 1 && errors.Count == 1;     // the cached result does not re-report every frame

        reg.ThrowOnViolation = true;
        bool threw = false;
        try { reg.ResolveFace("Nowhere", 400, out _); }
        catch (FontChainException) { threw = true; }
        reg.ThrowOnViolation = false;

        Check("gate.fonts.off-chain falling OFF a chain is an error: counted, logged once, thrown when ThrowOnViolation is set (the DEBUG mode), and the run still gets the body chain's face instead of no text",
            counted && stillText && onceOnly && threw && reg.ChainViolations == 2,
            $"violations={reg.ChainViolations} logged={errors.Count} fallbackFamily=\"{fam}\" onceOnly={onceOnly} threw={threw}");
    }

    // ══ one cache ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static void OneCacheChecks()
    {
        var src = NewSource();
        var reg = new FontRegistry(src, () => "Fake UI Display, Fake UI");

        // The measure engine and the glyph renderer both resolve through ONE registry: the second consumer creates nothing.
        nint measureFace = reg.ResolveFace("Fake UI", 400, out var m1);
        int created = src.Created;
        nint rasterFace = reg.ResolveFace("Fake UI", 400, out var m2);
        nint bodyA = reg.ResolveFace("", 400, out _);                      // empty = the body chain
        nint bodyB = reg.ResolveFace("Fake UI Display, Fake UI", 400, out _);
        bool shared = measureFace == rasterFace && m1.Em == m2.Em && src.Created == created + 1 && bodyA == bodyB && bodyA != measureFace;

        // A fallback face (the shaper's system font-fallback map) is cached by the same registry, once.
        int released = 0;
        nint fb1 = reg.AddFallbackFace("Fallback Family", 700, 0xBEEF, _ => released++);
        nint fb2 = reg.AddFallbackFace("Fallback Family", 700, 0xCAFE, _ => released++);
        bool fallbackOnce = fb1 == 0xBEEF && fb2 == 0xBEEF && released == 1 && reg.TryGetFallbackFace("Fallback Family", 700) == 0xBEEF;

        // A steady-state hit allocates nothing (the render thread resolves per run).
        for (int i = 0; i < 32; i++) reg.ResolveFace("Fake UI", 400, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) reg.ResolveFace("Fake UI", 400, out _);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Check("gate.fonts.one-cache the measure engine and the glyph renderer share ONE face cache: the second consumer of a (family, weight) creates nothing and gets the identical face and metrics; the empty family is the body chain; a fallback face is cached once; a cache hit allocates 0 bytes",
            shared && fallbackOnce && bytes == 0,
            $"shared={shared} (created {created}->{src.Created}) fallbackOnce={fallbackOnce} hitBytes={bytes}");
    }

    // ══ preload ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    static void PreloadChecks()
    {
        var set = new FontSet("Fake UI Display, Fake UI", "Fake Icons");

        // Control: with no preload, the first resolution happens ON the UI thread.
        var coldSrc = NewSource();
        var cold = new FontRegistry(coldSrc, () => set.Body);
        foreach (int w in FontRegistry.PreloadWeights) cold.ResolveFace(set.Body, w, out _);
        cold.ResolveFace(set.Icon, 400, out _);
        bool controlOnUi = cold.FacesCreatedOnUiThread > 0 && cold.FacesCreatedOnUiThread == cold.FacesCreated;

        var src = NewSource();
        var reg = new FontRegistry(src, () => set.Body);
        bool notReady = !reg.IsReady(set);
        reg.Preload(set).Wait();
        bool ready = reg.IsReady(set);
        long createdByPreload = reg.FacesCreated;
        bool offUi = createdByPreload > 0 && reg.FacesCreatedOnUiThread == 0;

        foreach (int w in FontRegistry.PreloadWeights) reg.ResolveFace(set.Body, w, out _);   // what the first frame asks for
        reg.ResolveFace(set.Icon, 400, out _);
        reg.ResolveFace("", 600, out _);
        bool uiCreatedNothing = reg.FacesCreatedOnUiThread == 0 && reg.FacesCreated == createdByPreload && src.Created == (int)createdByPreload;

        // The static facade (Fonts.Preload / Fonts.IsReady) drives the shared registry through the same code.
        var shared = FontRegistry.Shared;
        var priorSource = shared.Source;
        var facadeSrc = NewSource();
        bool facade;
        try
        {
            shared.Source = facadeSrc;
            bool before = Fonts.IsReady(set);
            Fonts.Preload(set).Wait();
            facade = !before && Fonts.IsReady(set);
        }
        finally { shared.Source = priorSource; }

        Check("gate.fonts.preload Fonts.Preload(FontSet) resolves the body chain (400/600/700) and the icon family on a worker: IsReady flips false to true, and the UI thread then creates 0 faces (FacesCreatedOnUiThread stays 0 while a cold registry created them all on the UI thread)",
            controlOnUi && notReady && ready && offUi && uiCreatedNothing && facade,
            $"controlOnUi={controlOnUi} notReady={notReady} ready={ready} createdByPreload={createdByPreload} uiCreated={reg.FacesCreatedOnUiThread} afterFirstFrame={reg.FacesCreated} facade={facade}");
    }

    // ══ a body-font change evicts the measure caches ════════════════════════════════════════════════════════════════════

    static void BodyChangeChecks(StringTable strings)
    {
        // The registry follows its body provider and bumps BodyVersion on a real change only.
        string body = "Fake UI";
        var src = NewSource();
        var reg = new FontRegistry(src, () => body);
        reg.SyncBody();
        int v0 = reg.BodyVersion;
        bool stable = !reg.SyncBody() && reg.BodyVersion == v0;
        nint before = reg.ResolveFace("", 400, out _, out string famBefore);
        body = "Fake UI Display";
        bool changed = reg.SyncBody() && reg.BodyVersion == v0 + 1;
        nint after = reg.ResolveFace("", 400, out _, out string famAfter);
        bool follows = famBefore == "Fake UI" && famAfter == "Fake UI Display" && before != after;

        // End to end: a host whose theme changes its body font re-measures its text (the measure entry carries the new epoch).
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("fonts-body", new Size2(320, 120), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new BodyTextRoot());
        for (int i = 0; i < 3; i++) host.Paint(0);
        NodeHandle text = FindText(host.Scene, host.Scene.Root);
        int epochBefore = EpochOf(host.Scene, text);
        int versionBefore = FontRegistry.Shared.BodyVersion;

        var original = Tok.Definition;
        var kind = Tok.Theme;
        bool measured;
        string md = "";
        try
        {
            int se0 = Tok.StyleEpoch;
            Tok.Use(original with { Fonts = new FontSet("Another Body, Segoe UI", original.Fonts.Icon) }, kind);
            int se1 = Tok.StyleEpoch;
            for (int i = 0; i < 3; i++) host.Paint(0);
            int epochAfter = EpochOf(host.Scene, text);
            measured = FontRegistry.Shared.BodyVersion == versionBefore + 1 && epochAfter == FontRegistry.Shared.BodyVersion && epochAfter != epochBefore;
            md = $" (styleEpoch {se0}->{se1}, version {versionBefore}->{FontRegistry.Shared.BodyVersion}, epoch {epochBefore}->{epochAfter})";
        }
        finally
        {
            Tok.Use(original, kind);
            for (int i = 0; i < 2; i++) host.Paint(0);
        }

        Check("gate.fonts.body-change the registry follows the theme's body chain (BodyVersion moves only on a real change and an empty family resolves the new body), and a host whose definition changes Fonts.Body re-measures its text (the measure entry's FontEpoch moves to the new BodyVersion)",
            stable && changed && follows && measured, $"stable={stable} changed={changed} follows={follows} measured={measured}{md}");
    }

    sealed class BodyTextRoot : Component
    {
        public override Element Render() => new TextEl("Body text") { Size = 14f };
    }

    static NodeHandle FindText(SceneStore scene, NodeHandle n)
    {
        if (n.IsNull) return n;
        if (scene.Paint(n).VisualKind == VisualKind.Text) return n;
        for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c))
        {
            var r = FindText(scene, c);
            if (!r.IsNull) return r;
        }
        return default;
    }

    static int EpochOf(SceneStore scene, NodeHandle text)
        => !text.IsNull && scene.TryGetMeasureCache(text, out var cache) ? cache.ResolveForWidth(float.PositiveInfinity).FontEpoch : -1;
}
