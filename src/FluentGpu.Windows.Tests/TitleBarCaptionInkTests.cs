using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// <see cref="TitleBarOptions.CaptionInk"/>: an app that draws its own backdrop behind the bar (a photo bleeding under the
/// chrome) paints the caption glyphs from a live thunk. <see cref="TitleBar"/> resets the caption buttons' children after
/// <c>Parts.Apply</c>, so a part modifier cannot reach the glyph; the hook is the way in. Mounted through the real reconciler
/// so the assertion is on the scene column the glyph paints from, not on the element tree. The pane toggle (the bar's other own
/// glyph) rides the same hook, and the editor inside an <see cref="AutoSuggestBox"/> takes its own pair of thunks
/// (<see cref="AutoSuggestBox.TextInk"/> / <see cref="AutoSuggestBox.PlaceholderInk"/>).
/// </summary>
public sealed class TitleBarCaptionInkTests
{
    static readonly ColorF s_light = ColorF.FromRgba(0xF0, 0xF0, 0xF0);
    static readonly ColorF s_dark = ColorF.FromRgba(0x10, 0x10, 0x10);

    static NodeHandle[] Mount(TreeReconciler recon, Func<ColorF>? ink, bool paneToggle = false)
    {
        var buttons = new NodeHandle[paneToggle ? 4 : 3];
        string[] parts = paneToggle
            ? [TitleBar.PartCaptionMin, TitleBar.PartCaptionMax, TitleBar.PartCaptionClose, TitleBar.PartPaneToggle]
            : [TitleBar.PartCaptionMin, TitleBar.PartCaptionMax, TitleBar.PartCaptionClose];
        var tp = new TemplateParts();
        for (int i = 0; i < parts.Length; i++)
        {
            int slot = i;
            tp[parts[i]] = b => b with { OnRealized = TemplateParts.Chain<NodeHandle>(n => buttons[slot] = n, b.OnRealized) };
        }
        recon.ReconcileRoot(TitleBar.Create(new TitleBarOptions { CaptionInk = ink, ShowPaneToggle = paneToggle, OnPaneToggle = static () => { }, Parts = tp }), null);
        recon.Runtime.Flush();
        return buttons;
    }

    static ColorF GlyphColor(SceneStore scene, NodeHandle button)
    {
        var glyph = scene.FirstChild(button);
        Assert.False(glyph.IsNull);
        return scene.Paint(glyph).TextColor;
    }

    /// <summary>The first descendant text node under <paramref name="root"/> that paints <paramref name="color"/>.</summary>
    static bool AnyTextPaints(SceneStore scene, NodeHandle root, ColorF color)
    {
        for (var n = scene.FirstChild(root); !n.IsNull; n = scene.NextSibling(n))
        {
            if (scene.Paint(n).TextColor == color || AnyTextPaints(scene, n, color)) return true;
        }
        return false;
    }

    [Fact]
    public void WithCaptionInk_TheCaptionGlyphsFollowTheThunk()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var mix = new Signal<float>(0f);
        var buttons = Mount(recon, () => ColorF.Lerp(s_dark, s_light, mix.Value));

        foreach (var b in buttons) Assert.Equal(s_dark, GlyphColor(scene, b));

        mix.Value = 1f;                                   // the hero shows: the ink moves with no re-render of the bar
        recon.Runtime.Flush();
        foreach (var b in buttons) Assert.Equal(s_light, GlyphColor(scene, b));
    }

    [Fact]
    public void WithoutCaptionInk_TheCaptionGlyphsKeepTheStyleTokens()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        // Tok is process-global and other suites switch theme/accent concurrently: take the expectation on both sides of the
        // mount and retry if the token moved under us, so the assertion is about the glyph, not about a racing global.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, new StringTable());
            var before = Tok.TextPrimary;
            var buttons = Mount(recon, null);
            var after = Tok.TextPrimary;
            if (before != after) continue;
            foreach (var b in buttons) Assert.Equal(before, GlyphColor(scene, b));
            return;
        }
        Assert.Fail("Tok.TextPrimary kept changing under the test");
    }

    [Fact]
    public void WithCaptionInk_ThePaneToggleGlyphFollowsTheThunkToo()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var mix = new Signal<float>(0f);
        var buttons = Mount(recon, () => ColorF.Lerp(s_dark, s_light, mix.Value), paneToggle: true);
        var pane = buttons[3];

        // The toggle is an IconButton: button -> glyph wrapper -> glyph run.
        ColorF PaneGlyph() => scene.Paint(scene.FirstChild(scene.FirstChild(pane))).TextColor;
        Assert.Equal(s_dark, PaneGlyph());

        mix.Value = 1f;
        recon.Runtime.Flush();
        Assert.Equal(s_light, PaneGlyph());
    }

    static (SceneStore Scene, TreeReconciler Recon, Signal<float> Mix) MountBox(string initial, ColorF typedDark, ColorF typedLight, ColorF hintDark, ColorF hintLight)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var mix = new Signal<float>(0f);
        var root = AutoSuggestBox.Create([], "Search", text: new Signal<string>(initial),
            textInk: () => ColorF.Lerp(typedDark, typedLight, mix.Value),
            placeholderInk: () => ColorF.Lerp(hintDark, hintLight, mix.Value));
        recon.ReconcileRoot(root, null);
        recon.Runtime.Flush();
        return (scene, recon, mix);
    }

    [Fact]
    public void WithPlaceholderInk_TheEmptyEditorsPlaceholderFollowsTheThunk()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        ColorF typedDark = ColorF.FromRgba(0x11, 0x22, 0x33), typedLight = ColorF.FromRgba(0xEE, 0xDD, 0xCC);
        ColorF hintDark = ColorF.FromRgba(0x44, 0x55, 0x66), hintLight = ColorF.FromRgba(0xBB, 0xAA, 0x99);
        var (scene, recon, mix) = MountBox("", typedDark, typedLight, hintDark, hintLight);

        // Empty: the placeholder paints, and it moves with its thunk with no re-render.
        Assert.True(AnyTextPaints(scene, scene.Root, hintDark));
        mix.Value = 1f;
        recon.Runtime.Flush();
        Assert.True(AnyTextPaints(scene, scene.Root, hintLight));
        Assert.False(AnyTextPaints(scene, scene.Root, hintDark));
    }

    [Fact]
    public void WithTextInk_TheTypedTextFollowsTheThunk()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        ColorF typedDark = ColorF.FromRgba(0x11, 0x22, 0x33), typedLight = ColorF.FromRgba(0xEE, 0xDD, 0xCC);
        ColorF hintDark = ColorF.FromRgba(0x44, 0x55, 0x66), hintLight = ColorF.FromRgba(0xBB, 0xAA, 0x99);
        var (scene, recon, mix) = MountBox("abc", typedDark, typedLight, hintDark, hintLight);

        Assert.True(AnyTextPaints(scene, scene.Root, typedDark));
        mix.Value = 1f;
        recon.Runtime.Flush();
        Assert.True(AnyTextPaints(scene, scene.Root, typedLight));
        Assert.False(AnyTextPaints(scene, scene.Root, typedDark));
    }
}
