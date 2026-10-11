namespace FluentGpu.Pal;

/// <summary>
/// The system backdrop behind a top-level window (EP9). A material is independent of whether the window's swapchain is
/// composited: a composited swapchain without a backdrop is allowed (<see cref="None"/>: an opaque ground, no DWM backdrop - the
/// swapchain stays composited so video keeps working).
/// </summary>
public enum WindowMaterial : byte
{
    /// <summary>No DWM backdrop (<c>DWMSBT_NONE</c>). The window paints an OPAQUE ground (the theme's window background): a
    /// composited window with no backdrop shows the desktop through transparent pixels, so the clear is never transparent.</summary>
    None = 0,
    /// <summary>Mica Base (<c>DWMSBT_MAINWINDOW</c>), the neutral "main window" backdrop.</summary>
    Mica = 1,
    /// <summary>Mica BaseAlt (<c>DWMSBT_TABBEDWINDOW</c>), the stronger wallpaper tint.</summary>
    MicaAlt = 2,
    /// <summary>Acrylic (<c>DWMSBT_TRANSIENTWINDOW</c>).</summary>
    Acrylic = 3,
}

/// <summary>What a (<see cref="WindowMaterial"/>, composited, custom-frame) combination means for the window. Pure data: the
/// platform backend turns it into DWM calls, the host into the frame's clear colour.</summary>
/// <param name="Material">The requested backdrop (written to <c>DWMWA_SYSTEMBACKDROP_TYPE</c> as-is, so <see cref="WindowMaterial.None"/>
/// really clears a previous one).</param>
/// <param name="Composited">The swapchain is a DirectComposition surface bound to the window (no redirection bitmap).</param>
/// <param name="TransparentGround">The frame clears TRANSPARENT so the backdrop shows through: only a composited window with a
/// backdrop. Everything else clears to the opaque theme ground.</param>
/// <param name="ExtendGlass">Extend the DWM frame over the whole client (sheet of glass) so the backdrop fills the window:
/// transparent ground and the standard frame (a custom frame owns zero margins - the Windows Terminal Mica rule).</param>
/// <param name="VideoCapable">Video needs a composited swapchain (the DComp video visuals hang off it), whatever the material.</param>
public readonly record struct ResolvedWindowMaterial(
    WindowMaterial Material, bool Composited, bool CustomFrame, bool TransparentGround, bool ExtendGlass, bool VideoCapable);

/// <summary>The single place the material / composited / frame combinations are decided.</summary>
public static class WindowMaterialPolicy
{
    public static ResolvedWindowMaterial Resolve(WindowMaterial material, bool composited, bool customFrame)
    {
        bool transparent = composited && material != WindowMaterial.None;
        return new ResolvedWindowMaterial(material, composited, customFrame,
            TransparentGround: transparent, ExtendGlass: transparent && !customFrame, VideoCapable: composited);
    }

    /// <summary>The material the legacy <c>Mica</c> / <c>MicaAlt</c> options mean: Mica (or MicaAlt) when on, none when off.</summary>
    public static WindowMaterial FromLegacy(bool mica, bool micaAlt)
        => mica ? (micaAlt ? WindowMaterial.MicaAlt : WindowMaterial.Mica) : WindowMaterial.None;
}
