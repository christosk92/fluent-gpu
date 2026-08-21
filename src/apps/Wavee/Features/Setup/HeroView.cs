using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>The setup wizard's hero-art SEAM. Each page now returns its real animated vector hero — one
/// self-contained <see cref="Component"/> per page (<c>Hero{Page}.cs</c>), authored on the engine's path lane
/// (<c>PathEl</c>/<see cref="BoxEl.Arc"/> + <c>AnimEngine.Keyframes</c>) against the approved prototype
/// (<c>docs/plans/wavee/onboarding-mica.html</c>'s <c>ob-*</c> scenes) — see <see cref="HeroMotion"/> for the shared
/// cadence/shape helpers. <see cref="SetupPageHost"/> calls only <see cref="Exists"/>/<see cref="For"/> — do not add
/// per-page hero logic anywhere else.</summary>
static class HeroView
{
    /// <summary>False ⇒ the page drops the hero column, independently of the plate-width breakpoint in
    /// <see cref="SetupPageHost"/> (which drops it for every page below 700-DIP plate width regardless of this).
    /// Every page has art today, so this is unconditionally true — kept as a real seam rather than an always-true
    /// stub, because a future text-only page (a legal/consent page, say) is then a one-line change here.</summary>
    public static bool Exists(SetupPage page) => true;

    /// <summary>The approved prototype's full-height 192-DIP art rail. The 192×192 animation stays centered inside
    /// that rail; the rail supplies the quiet card surface and token-derived accent glow. Prototype diagnostic
    /// captions are deliberately not part of the production surface.</summary>
    public static Element For(SetupPage page) => new BoxEl
    {
        Width = SetupPageHost.Width, AlignSelf = FlexAlign.Stretch, Shrink = 0f, MinHeight = 0f,
        Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(Radii.Card),
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
        [
            new GradientStop(0f, ColorF.Lerp(Tok.FillCardSecondary, Tok.AccentDefault, 0.24f)),
            new GradientStop(0.72f, Tok.FillCardSecondary),
            new GradientStop(1f, Tok.FillCardSecondary),
        ])
        {
            RadialCenter = new Point2(0.32f, 0.18f),
            RadialRadius = new Point2(0.74f, 0.28f),
        },
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [ Art(page) ],
    };

    static Element Art(SetupPage page) => page switch
    {
        SetupPage.Welcome => Embed.Comp(() => new HeroWelcome()),
        SetupPage.Terms => Embed.Comp(() => new HeroEula()),
        SetupPage.SignIn => Embed.Comp(() => new HeroConnect()),
        SetupPage.LocalPlayback => Embed.Comp(() => new HeroPatch()),
        SetupPage.Appearance => Embed.Comp(() => new HeroSettings()),
        SetupPage.Sidebar => Embed.Comp(() => new HeroSidebar()),
        SetupPage.Sound => Embed.Comp(() => new HeroSound()),
        SetupPage.Notifications => Embed.Comp(() => new HeroBell()),
        _ => Embed.Comp(() => new HeroDone()),
    };
}
