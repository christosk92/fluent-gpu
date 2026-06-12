namespace FluentGpu.Foundation;

/// <summary>Main-axis distribution of free space (flexbox justify-content).</summary>
public enum FlexJustify : byte { Start = 0, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }

/// <summary>Cross-axis alignment (flexbox align-items / align-self). <see cref="Auto"/> on a child = inherit the container's align-items.</summary>
public enum FlexAlign : byte { Auto = 0, Start, Center, End, Stretch }

/// <summary>How an image's pixels map into its (possibly differently-shaped) layout box — the CSS <c>object-fit</c> model,
/// cleaner than WinUI's <c>Stretch</c> because box-sizing is a separate concern (see <c>AspectRatio</c>). Default
/// <see cref="Cover"/> (what album art / thumbnails want): never distorts, never letterboxes.</summary>
public enum ImageFit : byte
{
    /// <summary>Scale to fill the box, preserve aspect, crop the overflow (CSS <c>object-fit: cover</c>). The default.</summary>
    Cover = 0,
    /// <summary>Scale to fit inside the box, preserve aspect, letterbox the remainder (CSS <c>object-fit: contain</c>).</summary>
    Contain = 1,
    /// <summary>Stretch the whole texture to the box, ignoring aspect — may distort (WinUI <c>Fill</c>). The escape hatch.</summary>
    Fill = 2,
    /// <summary>Draw at the source's native pixel size (as DIPs), centered, cropped to the box (WinUI <c>None</c>).</summary>
    None = 3,
}
