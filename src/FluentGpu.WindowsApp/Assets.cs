using System;
using System.IO;

namespace FluentGpu;

/// <summary>
/// Output-safe asset path resolution: bundled files live under <c>assets/</c> next to the exe (copied by the csproj) and
/// are addressed by relative path. The returned absolute path feeds straight into <c>Ui.Image(...)</c> — the image
/// pipeline's fetcher streams a local file path directly (no HTTP). See <c>DefaultImageFetcher</c>.
/// </summary>
internal static class Assets
{
    public static string Path(string relative)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "assets", relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>The bundled WinUI-Gallery tile image for a control (by its asset file name, e.g. "Button.png").</summary>
    public static string ControlImage(string fileName) => Path("ControlImages/" + fileName);

    public static string Header => Path("GalleryHeaderImage.png");
}
