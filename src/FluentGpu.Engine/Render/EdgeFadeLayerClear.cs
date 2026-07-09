namespace FluentGpu.Render;

/// <summary>Pure geometry for edge-fade layer RT clears — TerraFX-free so headless gates can call it.</summary>
public static class EdgeFadeLayerClear
{
    /// <summary>Decide the clear rect for a leased edge-fade group RT. Blur-carrying layers need a full-canvas clear
    /// (BlurInPlace reads a halo past CompositeClip); pure fades clear only the composite-clip box.</summary>
    public static void Compute(in PushLayerCmd layer, float scale, int canvasW, int canvasH,
        out int left, out int top, out int right, out int bottom, out bool fullCanvas)
    {
        if (layer.BlurSigma > 0f)
        {
            left = top = 0;
            right = canvasW;
            bottom = canvasH;
            fullCanvas = true;
            return;
        }

        var cr = layer.CompositeClip;
        left = Math.Clamp((int)MathF.Floor(cr.X * scale), 0, canvasW);
        top = Math.Clamp((int)MathF.Floor(cr.Y * scale), 0, canvasH);
        right = Math.Clamp((int)MathF.Ceiling((cr.X + cr.W) * scale), left, canvasW);
        bottom = Math.Clamp((int)MathF.Ceiling((cr.Y + cr.H) * scale), top, canvasH);
        fullCanvas = false;
    }

    public static int ClearAreaPx(int left, int top, int right, int bottom) => Math.Max(0, right - left) * Math.Max(0, bottom - top);
}
