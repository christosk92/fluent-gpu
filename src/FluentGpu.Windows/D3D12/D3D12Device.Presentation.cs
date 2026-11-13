using System.Diagnostics;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FluentGpu.Rhi.D3D12;

// ── WINDOW PRESENTATION MOTION (IGpuDevice.AnimatePresentation / TickPresentation) ─────────────────────────────────
//
// A whole window fades and scales on the compositor: an effect group (opacity) and a scale transform on the target's DComp ROOT
// visual (the UI child and anything beneath it in the tree), and the same pose on the window's video layer
// (CompositionVideoPresenter, Windows.UI.Composition). The render thread poses both once per turn of the window, from the motion's
// curve at that moment, and commits: nothing relayouts, nothing moves the window. Posed per turn rather than handed to the two
// compositors as their own animations, so the UI root and the video layer (DirectComposition and Windows.UI.Composition: two clocks,
// two time bases) are always in the same pose. Identity removes the effect and the transform.
public sealed unsafe partial class D3D12Device
{
    /// <summary><see cref="IGpuDevice.AnimatePresentation"/>.</summary>
    public void AnimatePresentation(ISwapchain target, in WindowPresentationMotion motion)
    {
        AssertSubmitThread();
        if (target is not D3D12Swapchain { Composited: true, Disposed: false } sc) return;
        sc.Presentation = motion;
        sc.PresentationStartQpc = Stopwatch.GetTimestamp();
        sc.PresentationMoving = true;
        FluentGpu.Foundation.Diag.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[window.presentation] target={sc.Ordinal} opacity={motion.FromOpacity:0.##}->{motion.ToOpacity:0.##}/{motion.OpacityMs:0}ms scale={motion.FromScale:0.###}->{motion.ToScale:0.###}/{motion.ScaleMs:0}ms origin={motion.OriginX:0.##},{motion.OriginY:0.##} delay={motion.DelayMs:0}ms bound={(sc.DcompRoot != null ? 1 : 0)}"));
        // Not bound yet (the window has not presented): BindDComp poses it with the tree, before the tree's first commit.
        if (sc.DcompRoot == null || _dcomp == null) return;
        PosePresentation(sc);
        Check(_dcomp->Commit(), "DComp.Commit(presentation)");
        CommitVideoCompositor();
    }

    /// <summary><see cref="IGpuDevice.TickPresentation"/>: one turn of a moving presentation.</summary>
    public bool TickPresentation(ISwapchain target)
    {
        AssertSubmitThread();
        if (target is not D3D12Swapchain { PresentationMoving: true, Disposed: false } sc || sc.DcompRoot == null || _dcomp == null) return false;
        PosePresentation(sc);
        Check(_dcomp->Commit(), "DComp.Commit(presentation)");
        CommitVideoCompositor();
        return sc.PresentationMoving;
    }

    /// <summary>Render thread, inside <see cref="BindDComp"/> (before its commit): the pose asked for before the tree existed.</summary>
    private void PosePendingPresentation(D3D12Swapchain sc)
    {
        if (sc.PresentationMoving) PosePresentation(sc);
    }

    /// <summary>The motion's pose now (motion time: <see cref="EngineSwitches.AnimScale"/> stretches it as every engine animation), on
    /// the UI root and the video layer; the motion ends (<see cref="D3D12Swapchain.PresentationMoving"/> false) once it has landed.</summary>
    private void PosePresentation(D3D12Swapchain sc)
    {
        var m = sc.Presentation;
        float scaleClock = EngineSwitches.AnimScale > 0f ? EngineSwitches.AnimScale : 1f;
        float ms = (float)((Stopwatch.GetTimestamp() - sc.PresentationStartQpc) * 1000.0 / Stopwatch.Frequency) * scaleClock;
        if (ms >= m.EndMs) sc.PresentationMoving = false;
        var originPx = new FluentGpu.Foundation.Point2(m.OriginX * sc.W, m.OriginY * sc.H);
        if (m.IsIdentity)
        {
            Check(sc.DcompRoot->SetEffect((IDCompositionEffect*)null), "Root.SetEffect(null)");
            Check(sc.DcompRoot->SetTransform((IDCompositionTransform*)null), "Root.SetTransform(null)");
            sc.PresentationAttached = false;
            sc.VideoPresenter?.PosePresentation(1f, 1f, originPx, identity: true);
            return;
        }
        float opacity = WindowPresentationMotion.Sample(m.FromOpacity, m.ToOpacity, m.OpacityMs, m.OpacityEase, m.DelayMs, ms);
        float scale = WindowPresentationMotion.Sample(m.FromScale, m.ToScale, m.ScaleMs, m.ScaleEase, m.DelayMs, ms);
        if (sc.DcompEffect == null)
        {
            IDCompositionEffectGroup* group;
            Check(_dcomp->CreateEffectGroup(&group), "CreateEffectGroup");
            sc.DcompEffect = group;
        }
        if (sc.DcompScale == null)
        {
            IDCompositionScaleTransform* st;
            Check(_dcomp->CreateScaleTransform(&st), "CreateScaleTransform");
            sc.DcompScale = st;
        }
        // The effect and the transform go on the root once (and again after an Identity dropped them).
        if (!sc.PresentationAttached)
        {
            Check(sc.DcompRoot->SetEffect((IDCompositionEffect*)sc.DcompEffect), "Root.SetEffect");
            Check(sc.DcompRoot->SetTransform((IDCompositionTransform*)sc.DcompScale), "Root.SetTransform");
        }
        sc.PresentationAttached = true;
        Check(sc.DcompEffect->SetOpacity(opacity), "EffectGroup.SetOpacity");
        Check(sc.DcompScale->SetCenterX(originPx.X), "Scale.SetCenterX");
        Check(sc.DcompScale->SetCenterY(originPx.Y), "Scale.SetCenterY");
        Check(sc.DcompScale->SetScaleX(scale), "Scale.SetScaleX");
        Check(sc.DcompScale->SetScaleY(scale), "Scale.SetScaleY");
        sc.VideoPresenter?.PosePresentation(opacity, scale, originPx, identity: false);
    }
}
