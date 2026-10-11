using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// E3 (EP2): <see cref="ThemeDefinition"/>. The engine default must be exactly what the engine shipped before definitions
/// existed (the literals below were captured from the constants and switch that this PR replaced), a definition swap must
/// change geometry in one publication, <see cref="ThemeDefinition.DiffersOnlyInColours"/> must separate colour changes from
/// everything else, and re-installing the active definition must not allocate.
/// </summary>
[Collection(SerialTestCollection.Name)]   // writes Tok's process-wide definition and theme kind
public sealed class ThemeDefinitionTests
{
    // ── FluentDefault == today's values ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FluentDefault_spacing_is_the_old_const_scale()
    {
        var s = ThemeDefinition.FluentDefault.Spacing;
        Assert.Equal(2f, s.XXS);
        Assert.Equal(4f, s.XS);
        Assert.Equal(8f, s.S);
        Assert.Equal(12f, s.M);
        Assert.Equal(16f, s.L);
        Assert.Equal(20f, s.XL);
        Assert.Equal(24f, s.XXL);
        Assert.Equal(32f, s.XXXL);
        Assert.Equal(24f, s.Gutter);
        Assert.Equal(36f, s.PageWide);
        Assert.Equal(16f, s.PageNarrow);
        Assert.Equal(12f, s.Card);
        Assert.Equal(16f, s.Inner);
        Assert.Equal(4f, s.StackS);
        Assert.Equal(8f, s.StackM);
        Assert.Equal(12f, s.StackL);
    }

    [Fact]
    public void FluentDefault_radii_are_the_old_const_ramp()
    {
        var r = ThemeDefinition.FluentDefault.Radii;
        Assert.Equal(0f, r.None);
        Assert.Equal(4f, r.Control);
        Assert.Equal(8f, r.Card);
        Assert.Equal(8f, r.Overlay);
        Assert.Equal(16f, r.Pill);
        Assert.Equal(999f, r.Full);
    }

    [Fact]
    public void FluentDefault_type_ramp_is_the_old_Ui_factory_values()
    {
        var t = ThemeDefinition.FluentDefault.Type;
        Assert.Equal(new TypeStyle(12f, 16f, 0), t.Caption);
        Assert.Equal(new TypeStyle(14f, 20f, 0), t.Body);
        Assert.Equal(new TypeStyle(14f, 20f, 600), t.BodyStrong);
        Assert.Equal(new TypeStyle(18f, 24f, 0), t.BodyLarge);
        Assert.Equal(new TypeStyle(20f, 28f, 600), t.Subtitle);
        Assert.Equal(new TypeStyle(28f, 36f, 600), t.Title);
        Assert.Equal(new TypeStyle(40f, 52f, 600), t.TitleLarge);
        Assert.Equal(new TypeStyle(68f, 92f, 600), t.Display);
    }

    [Fact]
    public void FluentDefault_elevation_is_the_old_shadow_presets()
    {
        static ShadowSpec S(float blur, float y, byte a) => new(blur, y, 0f, ColorF.FromRgba(0, 0, 0, a));
        var e = ThemeDefinition.FluentDefault.Elevation;
        Assert.Equal(S(8f, 2f, 0x33), e.Dark.Card);
        Assert.Equal(S(16f, 4f, 0x40), e.Dark.CardHover);
        Assert.Equal(S(16f, 4f, 0x40), e.Dark.Tooltip);
        Assert.Equal(S(16f, 8f, 0x42), e.Dark.Flyout);
        Assert.Equal(S(12f, -2f, 0x28), e.Dark.DockTop);
        Assert.Equal(S(64f, 16f, 0x66), e.Dark.Dialog);
        Assert.Equal(S(4f, 2f, 0x1A), e.Light.Card);
        Assert.Equal(S(12f, 4f, 0x24), e.Light.CardHover);
        Assert.Equal(S(8f, 4f, 0x24), e.Light.Tooltip);
        Assert.Equal(S(16f, 8f, 0x24), e.Light.Flyout);
        Assert.Equal(S(6f, -1f, 0x14), e.Light.DockTop);
        Assert.Equal(S(48f, 12f, 0x30), e.Light.Dialog);
    }

    [Fact]
    public void FluentDefault_motion_is_the_old_MotionTok_switch()
    {
        var m = ThemeDefinition.FluentDefault.Motion;
        static MotionTokenDef Eased(float ms, Easing e, ReducedMotionPolicy p = ReducedMotionPolicy.SnapEnd) => MotionTokenDef.Eased(ms, e, p);
        static MotionTokenDef Spring(float response, float damping) => MotionTokenDef.SpringOf(SpringParams.FromResponse(response, damping));
        Assert.Equal(Eased(83f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.ControlFaster));
        Assert.Equal(Eased(150f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.ControlFast));
        Assert.Equal(Eased(250f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.ControlNormal));
        Assert.Equal(Eased(300f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.StandardEnter));
        Assert.Equal(Eased(200f, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.StandardExit));
        Assert.Equal(Eased(500f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.EmphasizedEnter));
        Assert.Equal(Eased(350f, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.EmphasizedExit));
        Assert.Equal(Spring(0.35f, 0.85f), m.Get(MotionTokenId.StandardSpring));
        Assert.Equal(Spring(0.50f, 0.60f), m.Get(MotionTokenId.ExpressiveSpring));
        Assert.Equal(Spring(0.45f, 1.0f), m.Get(MotionTokenId.ConnectedFly));
        Assert.Equal(Spring(0.40f, 0.90f), m.Get(MotionTokenId.ContentResize));
        Assert.Equal(Spring(0.40f, 0.85f), m.Get(MotionTokenId.ItemPlacement));
        Assert.Equal(Eased(150f, Easing.Linear, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.ScrollFade));
        Assert.Equal(Eased(150f, Easing.FluentDecelerate), m.Get(MotionTokenId.MediaChromeReveal));
        Assert.Equal(Eased(400f, Easing.EaseOut), m.Get(MotionTokenId.MediaChromeConceal));
        Assert.Equal(Eased(200f, Easing.FluentPane, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.PaneOpen));
        Assert.Equal(Eased(100f, Easing.FluentPane, ReducedMotionPolicy.KeepFade), m.Get(MotionTokenId.PaneClose));
        Assert.Equal(Spring(0.26f, 1f), m.Get(MotionTokenId.Reveal));
        Assert.Equal(MotionTokenDef.SpringOf(SpringParams.Default), m.Get((MotionTokenId)999));   // the old fall-through
    }

    [Fact]
    public void FluentDefault_fonts_are_the_old_Theme_statics()
    {
        var f = ThemeDefinition.FluentDefault.Fonts;
        Assert.Equal("Segoe UI", f.Body);
        Assert.Equal("Segoe UI", f.BodyPrimary);
        Assert.Equal("Segoe Fluent Icons", f.Icon);
        Assert.Equal("Segoe UI Variable Text", new FontSet("Segoe UI Variable Text, Segoe UI", "Icons").BodyPrimary);
    }

    [Fact]
    public void FluentDefault_token_sets_are_the_old_Warm_palette_values()
    {
        var d = ThemeDefinition.FluentDefault;
        // Spot checks against literals captured from the palette builder (dark, then light).
        Assert.Equal(ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F), d.Dark.FillControlDefault);
        Assert.Equal(ColorF.FromRgba(0xFF, 0xFF, 0xFF), d.Dark.TextPrimary);
        Assert.Equal(ColorF.FromRgba(0x60, 0xCD, 0xFF), d.Dark.AccentDefault);
        Assert.Equal(ColorF.FromRgba(0xA6, 0xD8, 0xFF), d.Dark.AccentTextPrimary);
        Assert.Equal(ColorF.FromRgba(0xC4, 0x2B, 0x1C), d.Dark.CaptionCloseHover);
        Assert.Equal(ColorF.FromRgba(0x1F, 0x1E, 0x1B), d.Light.TextPrimary);
        Assert.Equal(ColorF.FromRgba(0x00, 0x5F, 0xB8), d.Light.AccentDefault);
        Assert.Equal(ColorF.FromRgba(0xEF, 0xEE, 0xEB), d.Light.FillSolidBase);
        Assert.Equal(ColorF.FromRgba(0xC4, 0x2B, 0x1C, 0xE6), d.Light.CaptionClosePressed);
        // The default definition IS the Warm preset the engine started in, and the presets share its non-colour tables.
        Assert.Same(d, Tok.WarmPalette);
        Assert.Equal("warm", d.Id);
        Assert.True(Tok.SlatePalette.DiffersOnlyInColours(d));
        Assert.True(Tok.NeutralPalette.DiffersOnlyInColours(d));
        Assert.Equal(AccentRamp.FluentDefault, d.Accent);
    }

    [Fact]
    public void Spacing_Radii_Elevation_Motion_and_Fonts_statics_read_the_default_definition()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(ThemeDefinition.FluentDefault, kind);
        try
        {
            Assert.Equal(12f, Spacing.M);
            Assert.Equal(36f, Spacing.PageWide);
            Assert.Equal(new Edges4(36f, 24f, 36f, 36f), Spacing.PagePadWide);
            Assert.Equal(Edges4.All(16f), Spacing.PagePadNarrow);
            Assert.Equal(Edges4.All(16f), Spacing.CardPad);
            Assert.Equal(4f, Radii.Control);
            Assert.Equal(CornerRadius4.All(4f), Radii.ControlAll);
            Assert.Equal(CornerRadius4.All(999f), Radii.FullAll);
            Assert.Equal(new CornerRadius4(8f, 8f, 0f, 0f), Radii.OverlayTop);
            Assert.Equal(new CornerRadius4(0f, 0f, 8f, 8f), Radii.OverlayBottom);
            Assert.Equal(CornerRadius4.All(3f), Radii.Circle(6f));
            Assert.Equal(150f, MotionTok.ControlFast.DurationMs);
            Assert.Equal("Segoe Fluent Icons", Tok.Definition.Fonts.Icon);
            var body = Ui.Body("x");
            Assert.Equal(14f, body.Size);
            Assert.Equal(20f, body.LineHeight);
            Assert.Equal((ushort)0, body.Weight);
            Assert.Equal((ushort)600, Ui.Title("x").Weight);
            var elevation = ThemeDefinition.FluentDefault.Elevation;
            Assert.Equal(kind == ThemeKind.Dark ? elevation.Dark.Card : elevation.Light.Card, Elevation.Card);
            Assert.Equal(kind == ThemeKind.Dark ? elevation.Dark.Dialog : elevation.Light.Dialog, Elevation.Dialog);
        }
        finally { Tok.Use(saved, kind); }
    }

    // ── DiffersOnlyInColours ────────────────────────────────────────────────────────────────────────────────────────

    static ThemeDefinition Baseline => ThemeDefinition.FluentDefault;

    [Fact]
    public void DiffersOnlyInColours_is_true_for_colour_only_differences()
    {
        var b = Baseline;
        Assert.True(b.DiffersOnlyInColours(b));
        Assert.True(b.DiffersOnlyInColours(b with { }));
        Assert.True(b.DiffersOnlyInColours(b with { Id = "other" }));
        Assert.True(b.DiffersOnlyInColours(b with { Light = b.Light with { TextPrimary = ColorF.FromRgba(1, 2, 3) } }));
        Assert.True(b.DiffersOnlyInColours(b with { Dark = b.Dark with { FillCardDefault = ColorF.FromRgba(4, 5, 6) } }));
        Assert.True(b.DiffersOnlyInColours(b with { LightShell = b.DarkShell, DarkShell = b.LightShell }));
        Assert.True(b.DiffersOnlyInColours(b with { Accent = AccentRamp.Derive(ColorF.FromRgba(0xE0, 0x40, 0x40)) }));
        Assert.True(b.DiffersOnlyInColours(b with { HighContrast = b.Dark }));
        Assert.True(b.DiffersOnlyInColours(Tok.NeutralPalette));
    }

    [Fact]
    public void DiffersOnlyInColours_is_false_when_any_non_colour_table_differs()
    {
        var b = Baseline;
        Assert.False(b.DiffersOnlyInColours(b with { Spacing = b.Spacing with { M = 14f } }));
        Assert.False(b.DiffersOnlyInColours(b with { Radii = b.Radii with { Control = 6f } }));
        Assert.False(b.DiffersOnlyInColours(b with { Type = b.Type with { Body = new TypeStyle(15f, 21f) } }));
        Assert.False(b.DiffersOnlyInColours(b with { Fonts = new FontSet("Cascadia Code", "Segoe Fluent Icons") }));
        Assert.False(b.DiffersOnlyInColours(b with { Motion = b.Motion.With(MotionTokenId.ControlFast, MotionTokenDef.Eased(1f, Easing.Linear)) }));
        Assert.False(b.DiffersOnlyInColours(b with { Elevation = b.Elevation with { Dark = b.Elevation.Dark with { Card = default } } }));
        Assert.False(b.DiffersOnlyInColours(b with { AppRoles = "roles" }));
        // A colour change that rides along with a geometry change is still a geometry change.
        Assert.False(b.DiffersOnlyInColours(b with { Light = b.Light with { TextPrimary = ColorF.FromRgba(1, 2, 3) }, Spacing = b.Spacing with { S = 9f } }));
    }

    // ── Tok.Use(definition, kind) ───────────────────────────────────────────────────────────────────────────────────

    static ThemeDefinition Roomy() => ThemeDefinition.FluentDefault with
    {
        Id = "roomy",
        Spacing = SpacingScale.FluentDefault with { M = 20f },
        Radii = RadiiScale.FluentDefault with { Control = 9f },
    };

    [Fact]
    public void Use_bumps_Epoch_always_and_StyleEpoch_only_for_non_colour_changes()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, kind);
        try
        {
            int epoch = Tok.Epoch, style = Tok.StyleEpoch;

            Tok.Use(Baseline, kind);   // nothing changed
            Assert.Equal((epoch, style), (Tok.Epoch, Tok.StyleEpoch));

            Tok.Use(Tok.SlatePalette, kind);   // colour-only
            Assert.Equal((epoch + 1, style), (Tok.Epoch, Tok.StyleEpoch));

            Tok.Use(Tok.SlatePalette, kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);   // light/dark flip
            Assert.Equal((epoch + 2, style), (Tok.Epoch, Tok.StyleEpoch));

            Tok.Use(Roomy(), kind);   // spacing + radii
            Assert.Equal((epoch + 3, style + 1), (Tok.Epoch, Tok.StyleEpoch));
            Assert.Equal("roomy", Tok.Definition.Id);

            Tok.Use(ThemeKind.Dark);   // the kind switch keeps the current definition
            Assert.Equal("roomy", Tok.Definition.Id);
            Assert.Equal(ThemeKind.Dark, Tok.Theme);
        }
        finally { Tok.Use(saved, kind); }
    }

    [Fact]
    public void Use_with_an_equal_valued_definition_is_a_no_op()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, kind);
        try
        {
            int epoch = Tok.Epoch, style = Tok.StyleEpoch;
            var twin = Baseline with { };
            Assert.NotSame(Baseline, twin);
            Tok.Use(twin, kind);
            Assert.Equal((epoch, style), (Tok.Epoch, Tok.StyleEpoch));
            Assert.Same(twin, Tok.Definition);   // the reference is adopted
        }
        finally { Tok.Use(saved, kind); }
    }

    [Fact]
    public void Statics_follow_the_installed_definition_in_one_swap()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, kind);
        try
        {
            var def = ThemeDefinition.FluentDefault with
            {
                Id = "all-tables",
                Spacing = SpacingScale.FluentDefault with { M = 20f, Inner = 22f },
                Radii = RadiiScale.FluentDefault with { Control = 9f, Overlay = 11f },
                Type = TypeRamp.FluentDefault with { Body = new TypeStyle(15f, 21f, 500) },
                Motion = MotionTable.FluentDefault.With(MotionTokenId.ControlFast, MotionTokenDef.Eased(321f, Easing.Linear)),
                Fonts = new FontSet("Cascadia Code, Segoe UI", "Custom Icons"),
            };
            Tok.Use(def, kind);

            Assert.Equal(20f, Spacing.M);
            Assert.Equal(Edges4.All(22f), Spacing.CardPad);
            Assert.Equal(9f, Radii.Control);
            Assert.Equal(CornerRadius4.All(11f), Radii.OverlayAll);
            Assert.Equal(new CornerRadius4(11f, 11f, 0f, 0f), Radii.OverlayTop);
            Assert.Equal(321f, MotionTok.ControlFast.DurationMs);
            Assert.Equal(321f, MotionTok.Get(MotionTokenId.ControlFast).DurationMs);
            Assert.Equal("Custom Icons", Tok.Definition.Fonts.Icon);
            Assert.Equal("Cascadia Code", Tok.Definition.Fonts.BodyPrimary);
            Assert.Equal("Custom Icons", Ui.Icon("x").FontFamily);
            var body = Ui.Body("x");
            Assert.Equal((15f, 21f, (ushort)500), (body.Size, body.LineHeight, body.Weight));
        }
        finally { Tok.Use(saved, kind); }

        // Restored: the swap is symmetric.
        Assert.Equal(12f, Spacing.M);
        Assert.Equal(4f, Radii.Control);
        Assert.Equal(150f, MotionTok.ControlFast.DurationMs);
    }

    [Fact]
    public void A_definition_accent_drives_the_accent_tokens_and_SetAccent_wins_over_it()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, ThemeKind.Dark);
        try
        {
            ColorF baked = Tok.AccentDefault;
            Assert.Equal(ColorF.FromRgba(0x60, 0xCD, 0xFF), baked);   // the engine default: baked tokens

            var red = AccentRamp.Derive(ColorF.FromRgba(0xE0, 0x40, 0x40));
            Tok.Use(Baseline with { Accent = red }, ThemeKind.Dark);
            Assert.Equal(red.Light2, Tok.AccentDefault);              // dark kind: Light2

            var green = AccentRamp.Derive(ColorF.FromRgba(0x10, 0x90, 0x30));
            Tok.SetAccent(in green);                                  // the explicit override wins
            Assert.Equal(green.Light2, Tok.AccentDefault);
            Tok.SetAccent(null);                                      // clearing reverts to the DEFINITION's accent
            Assert.Equal(red.Light2, Tok.AccentDefault);

            Tok.Use(Baseline, ThemeKind.Dark);                        // marker ramp again: baked
            Assert.Equal(baked, Tok.AccentDefault);
        }
        finally { Tok.SetAccent(null); Tok.Use(saved, kind); }
    }

    // ── geometry in one publication ─────────────────────────────────────────────────────────────────────────────────

    private sealed class GeometryRoot : Component
    {
        public NodeHandle Outer, Inner;
        public int Renders;

        public override Element Render()
        {
            Renders++;
            return new BoxEl
            {
                Direction = 1, Width = 400f, Height = 300f, AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Width = 300f, Padding = Edges4.All(Spacing.M), Corners = Radii.ControlAll,
                        OnRealized = h => Outer = h,
                        Children = [new BoxEl { Height = 20f, OnRealized = h => Inner = h }],
                    },
                ],
            };
        }
    }

    [Fact]
    public void A_definition_with_different_spacing_and_radii_changes_geometry_in_one_publication()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, kind);
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("theme-definition-geometry", new Size2(400, 300), 1f));
        window.Show();
        var root = new GeometryRoot();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            Assert.Equal(12f, scene.Bounds(root.Inner).X);
            Assert.Equal(12f, scene.Bounds(root.Inner).Y);
            Assert.Equal(44f, scene.Bounds(root.Outer).H);   // 12 + 20 + 12
            Assert.Equal(4f, scene.Paint(root.Outer).Corners.TopLeft);

            Tok.Use(Roomy(), kind);
            host.RunFrame();   // ONE frame after the swap

            Assert.Equal(20f, scene.Bounds(root.Inner).X);
            Assert.Equal(20f, scene.Bounds(root.Inner).Y);
            Assert.Equal(60f, scene.Bounds(root.Outer).H);   // 20 + 20 + 20
            Assert.Equal(9f, scene.Paint(root.Outer).Corners.TopLeft);

            Tok.Use(Baseline, kind);
            host.RunFrame();
            Assert.Equal(12f, scene.Bounds(root.Inner).X);
            Assert.Equal(44f, scene.Bounds(root.Outer).H);
            Assert.Equal(4f, scene.Paint(root.Outer).Corners.TopLeft);
        }
        finally
        {
            Tok.Use(saved, kind);
            host.Dispose();
            app.Dispose();
        }
    }

    // ── allocation ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reinstalling_the_active_definition_allocates_nothing()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, kind);
        try
        {
            var current = Tok.Definition;
            for (int i = 0; i < 100; i++) Tok.Use(current, kind);   // warm the path

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) Tok.Use(current, kind);
            long same = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, same);

            // Equal-valued but distinct instances: the deep compare must not allocate either.
            var twins = new ThemeDefinition[64];
            for (int i = 0; i < twins.Length; i++) twins[i] = current with { };
            Tok.Use(twins[0], kind);   // warm the compare
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i < twins.Length; i++) Tok.Use(twins[i], kind);
            long adopt = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, adopt);
        }
        finally { Tok.Use(saved, kind); }
    }

    [Fact]
    public void Reading_the_token_statics_allocates_nothing()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(Baseline, kind);
        try
        {
            float sink = 0f;
            for (int i = 0; i < 100; i++) sink += Read();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) sink += Read();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, bytes);
            Assert.True(sink > 0f);
        }
        finally { Tok.Use(saved, kind); }

        static float Read()
        {
            var pad = Spacing.PagePadWide;
            var corners = Radii.OverlayTop;
            var shadow = Elevation.Flyout;
            var motion = MotionTok.Get(MotionTokenId.ControlFast);
            return Spacing.M + Radii.Control + pad.Left + corners.TopLeft + shadow.Blur + motion.DurationMs + Tok.Definition.Fonts.Icon.Length;
        }
    }
}
