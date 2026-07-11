using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Per-layer semantic color role of a <see cref="IconDef"/> layer (the Files ThemedIcon model): the neutral
/// <see cref="Base"/>/<see cref="Alt"/> body, the <see cref="Accent"/> highlight (the "blue disc"), and the
/// <see cref="AccentContrast"/> symbol drawn on top of the accent.</summary>
public enum IconRole : byte { Base, Alt, Accent, AccentContrast }

/// <summary>Status recolor of the ACCENT layers (Base/Alt stay neutral): the WinUI severity palette + accent variants.</summary>
public enum IconColorType : byte { Normal, Critical, Caution, Success, Neutral, Accent, Custom }

/// <summary>How a layered icon renders: full <see cref="Layered"/> (all role layers), or a monochrome fallback from the
/// icon's single <see cref="Outline"/>/<see cref="Filled"/> path (an explicit app/control switch — HighContrast auto is
/// deferred until the theme subsystem lands a HighContrast <c>ThemeKind</c>).</summary>
public enum IconMode : byte { Layered, Outline, Filled }

/// <summary>One layer of a themed icon: a role + its SVG path string (+ an authoring opacity multiplier).</summary>
public readonly record struct IconLayerDef(IconRole Role, string PathData, float Opacity = 1f);

/// <summary>An icon's geometry: the ordered role <see cref="Layers"/> plus the single-path <see cref="Outline"/>/
/// <see cref="Filled"/> monochrome fallbacks, all in a square <see cref="ViewBox"/> coordinate space (Files icons = 16).</summary>
public sealed record IconDef(string? Outline, string? Filled, IconLayerDef[] Layers, float ViewBox = 16f, bool EvenOdd = false);

/// <summary>
/// Layered vector icon control (design: currently-missing-across-the-vast-owl-icons-design.md). <see cref="Create"/>
/// returns a fixed-size <c>ZStack</c> of <see cref="IconLayerEl"/> leaves — one per role layer — each carrying a BOUND
/// tint thunk (<see cref="ResolveRole"/> reading <c>Tok</c>), so a theme/accent swap live-recolors every layer with NO
/// mask re-raster (the masks are colorless; the tint rides the <c>DrawIconMask</c> command). Never a frozen ctor color.
/// </summary>
public static class ThemedIcon
{
    /// <summary>Build a themed icon. <paramref name="name"/> resolves through <see cref="ThemedIconRegistry"/>;
    /// <paramref name="color"/> restatuses the accent layers; <paramref name="mode"/> selects layered vs a monochrome
    /// fallback; <paramref name="enabled"/> (a thunk reading a signal — null = always enabled) dims to the disabled
    /// token; <paramref name="onAccent"/> flips Base→on-accent for a toggled-on-accent surface. Unknown name ⇒ an empty
    /// same-size spacer (graceful; keeps the slot).</summary>
    public static Element Create(string name, float size = 16f, IconColorType color = IconColorType.Normal,
        IconMode mode = IconMode.Layered, Func<bool>? enabled = null, bool onAccent = false)
    {
        if (!ThemedIconRegistry.TryGet(name, out var def) || def is null)
            return new BoxEl { Width = size, Height = size };

        if (mode == IconMode.Outline && def.Outline is { Length: > 0 } o)
            return Single(o, def, size, color, enabled, onAccent);
        if (mode == IconMode.Filled && def.Filled is { Length: > 0 } f)
            return Single(f, def, size, color, enabled, onAccent);

        var layers = def.Layers;
        if (layers.Length == 0)
        {
            string? single = def.Outline ?? def.Filled;
            return single is { Length: > 0 } s ? Single(s, def, size, color, enabled, onAccent)
                                               : new BoxEl { Width = size, Height = size };
        }

        var children = new Element[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            var ld = layers[i];
            int pathId = IconGeometryTable.Shared.Register(ld.PathData, def.ViewBox, def.ViewBox, def.EvenOdd);
            IconRole role = ld.Role;
            float lop = ld.Opacity;
            children[i] = new IconLayerEl
            {
                PathId = pathId,
                Size = size,
                Tint = Prop.Of(() => ResolveRole(role, color, enabled is null || enabled(), onAccent, lop)),
            };
        }
        return new BoxEl { ZStack = true, Width = size, Height = size, Children = children };
    }

    private static Element Single(string path, IconDef def, float size, IconColorType color, Func<bool>? enabled, bool onAccent)
    {
        int pathId = IconGeometryTable.Shared.Register(path, def.ViewBox, def.ViewBox, def.EvenOdd);
        return new IconLayerEl
        {
            PathId = pathId,
            Size = size,
            // A monochrome fallback paints in the Base role (a plain foreground icon).
            Tint = Prop.Of(() => ResolveRole(IconRole.Base, color, enabled is null || enabled(), onAccent, 1f)),
        };
    }

    /// <summary>Resolve a layer's tint for the current theme/accent (read at RethemeAll). Disabled wins as a step;
    /// otherwise Base/Alt are the neutral icon tokens, Accent takes the status/accent fill, AccentContrast the symbol
    /// color on the accent. <paramref name="layerOpacity"/> folds the authoring opacity into the alpha.</summary>
    public static ColorF ResolveRole(IconRole role, IconColorType color, bool enabled, bool onAccent, float layerOpacity)
    {
        ColorF c;
        if (!enabled)
        {
            c = Tok.TextDisabled;
        }
        else
        {
            c = role switch
            {
                IconRole.Base => onAccent ? Tok.TextOnAccentPrimary : Tok.IconBase,
                IconRole.Alt => Tok.IconAlt,
                IconRole.Accent => AccentLayer(color, onAccent),
                IconRole.AccentContrast => onAccent ? Tok.AccentDefault : Tok.TextOnAccentPrimary,
                _ => Tok.IconBase,
            };
        }
        return layerOpacity < 1f ? c with { A = c.A * layerOpacity } : c;
    }

    // The accent (highlight) layer's color: the WinUI severity fills for a status recolor, else the system accent.
    private static ColorF AccentLayer(IconColorType color, bool onAccent) => color switch
    {
        IconColorType.Critical => Tok.SystemFillCritical,
        IconColorType.Caution => Tok.SystemFillCaution,
        IconColorType.Success => Tok.SystemFillSuccess,
        IconColorType.Neutral => onAccent ? Tok.TextOnAccentPrimary : Tok.IconBase,
        _ => Tok.AccentDefault,   // Normal / Accent / Custom: the signature accent highlight ("blue")
    };
}
