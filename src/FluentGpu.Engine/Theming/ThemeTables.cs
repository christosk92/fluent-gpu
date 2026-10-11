using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace FluentGpu.Theming;

/// <summary>The six drop-shadow bands of one theme kind (the Fluent elevation classes: card, card-hover, ToolTip,
/// Flyout/Menu, player dock, Dialog). Read through <see cref="FluentGpu.Dsl.Elevation"/>.</summary>
public sealed record ElevationRamp
{
    public required ShadowSpec Card { get; init; }
    public required ShadowSpec CardHover { get; init; }
    public required ShadowSpec Tooltip { get; init; }
    public required ShadowSpec Flyout { get; init; }
    public required ShadowSpec DockTop { get; init; }
    public required ShadowSpec Dialog { get; init; }
}

/// <summary>
/// The elevation presets of a <see cref="ThemeDefinition"/>, one <see cref="ElevationRamp"/> per theme kind.
/// <see cref="FluentDefault"/> holds the values the engine shipped in <c>Elevation</c> before E3 (WinUI ThemeShadow Z
/// classes rendered as analytic drop shadows).
/// </summary>
public sealed record ElevationSet
{
    public required ElevationRamp Light { get; init; }
    public required ElevationRamp Dark { get; init; }

    private static ShadowSpec Shadow(float blur, float offsetY, byte alpha)
        => new(Blur: blur, OffsetY: offsetY, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, alpha));

    /// <summary>The Fluent default set (today's engine values).</summary>
    public static ElevationSet FluentDefault { get; } = new()
    {
        Light = new ElevationRamp
        {
            Card = Shadow(4f, 2f, 0x1A),
            CardHover = Shadow(12f, 4f, 0x24),
            Tooltip = Shadow(8f, 4f, 0x24),
            Flyout = Shadow(16f, 8f, 0x24),
            DockTop = Shadow(6f, -1f, 0x14),
            Dialog = Shadow(48f, 12f, 0x30),
        },
        Dark = new ElevationRamp
        {
            Card = Shadow(8f, 2f, 0x33),
            CardHover = Shadow(16f, 4f, 0x40),
            Tooltip = Shadow(16f, 4f, 0x40),
            Flyout = Shadow(16f, 8f, 0x42),
            DockTop = Shadow(12f, -2f, 0x28),
            Dialog = Shadow(64f, 16f, 0x66),
        },
    };
}

/// <summary>
/// The per-theme motion-token table: one <see cref="MotionTokenDef"/> per <see cref="MotionTokenId"/>, read by
/// <see cref="MotionTok.Get"/>. Immutable; <see cref="With"/> returns a modified copy. An id outside the table resolves
/// to the engine's default spring (the old switch's fall-through). Value-equal when every entry is equal, which is what
/// <see cref="ThemeDefinition.DiffersOnlyInColours"/> compares.
/// </summary>
public sealed class MotionTable : IEquatable<MotionTable>
{
    // Declared before Fluent: static initialisers run in textual order.
    private static readonly MotionTokenDef Fallback = MotionTokenDef.SpringOf(SpringParams.Default);

    private readonly MotionTokenDef[] _defs;

    private MotionTable(MotionTokenDef[] defs) => _defs = defs;

    /// <summary>The recipe for <paramref name="id"/> (an array read; no allocation).</summary>
    public MotionTokenDef Get(MotionTokenId id)
    {
        var defs = _defs;
        uint index = (uint)id;
        return index < (uint)defs.Length ? defs[index] : Fallback;
    }

    /// <summary>A copy of this table with <paramref name="id"/> replaced by <paramref name="def"/>.</summary>
    public MotionTable With(MotionTokenId id, MotionTokenDef def)
    {
        int index = (int)id;
        var copy = new MotionTokenDef[Math.Max(_defs.Length, index + 1)];
        for (int i = 0; i < copy.Length; i++) copy[i] = i < _defs.Length ? _defs[i] : Fallback;
        copy[index] = def;
        return new MotionTable(copy);
    }

    public bool Equals(MotionTable? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other._defs.Length != _defs.Length) return false;
        for (int i = 0; i < _defs.Length; i++)
            if (!_defs[i].Equals(other._defs[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is MotionTable other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (int i = 0; i < _defs.Length; i++) hash.Add(_defs[i]);
        return hash.ToHashCode();
    }

    /// <summary>The Fluent default table: exactly the recipes the engine's hard switch carried before E3.</summary>
    public static MotionTable FluentDefault { get; } = BuildFluentDefault();

    private static MotionTable BuildFluentDefault()
    {
        var ids = Enum.GetValues<MotionTokenId>();
        int max = 0;
        for (int i = 0; i < ids.Length; i++) max = Math.Max(max, (int)ids[i]);
        var defs = new MotionTokenDef[max + 1];
        for (int i = 0; i < defs.Length; i++) defs[i] = Fallback;
        for (int i = 0; i < ids.Length; i++) defs[(int)ids[i]] = FluentRecipe(ids[i]);
        return new MotionTable(defs);
    }

    private static MotionTokenDef FluentRecipe(MotionTokenId id) => id switch
    {
        // Control interaction - fades keep their cross-fade under reduced motion.
        MotionTokenId.ControlFaster => MotionTokenDef.Eased(83f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
        MotionTokenId.ControlFast => MotionTokenDef.Eased(150f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
        MotionTokenId.ControlNormal => MotionTokenDef.Eased(250f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
        // Structural enter/exit - Fluent decelerate in, accelerate out.
        MotionTokenId.StandardEnter => MotionTokenDef.Eased(300f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
        MotionTokenId.StandardExit => MotionTokenDef.Eased(200f, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade),
        MotionTokenId.EmphasizedEnter => MotionTokenDef.Eased(500f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
        MotionTokenId.EmphasizedExit => MotionTokenDef.Eased(350f, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade),
        // Springs - Standard is critically-ish damped; Expressive is bouncier.
        MotionTokenId.StandardSpring => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.35f, 0.85f)),
        MotionTokenId.ExpressiveSpring => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.50f, 0.60f)),
        // Feature motions - ConnectedFly is critically damped (the user prefers a smooth, no-overshoot hero fly).
        MotionTokenId.ConnectedFly => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.45f, 1.0f)),
        MotionTokenId.ContentResize => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.40f, 0.90f)),
        MotionTokenId.ItemPlacement => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.40f, 0.85f)),
        MotionTokenId.ScrollFade => MotionTokenDef.Eased(150f, Easing.Linear, ReducedMotionPolicy.KeepFade),
        // Media chrome - SnapEnd (not KeepFade): under reduced motion the transport must appear/disappear instantly.
        MotionTokenId.MediaChromeReveal => MotionTokenDef.Eased(MotionTok.MediaChromeFadeInMs, Easing.FluentDecelerate),
        MotionTokenId.MediaChromeConceal => MotionTokenDef.Eased(MotionTok.MediaChromeFadeOutMs, Easing.EaseOut),
        MotionTokenId.PaneOpen => MotionTokenDef.Eased(200f, Easing.FluentPane, ReducedMotionPolicy.KeepFade),
        MotionTokenId.PaneClose => MotionTokenDef.Eased(100f, Easing.FluentPane, ReducedMotionPolicy.KeepFade),
        // The smooth disclosure - one critically damped spring both ways (no overshoot, the same curve open and close).
        MotionTokenId.Reveal => MotionTokenDef.SpringOf(SpringParams.FromResponse(MotionTok.RevealResponseSec, 1f)),
        _ => MotionTokenDef.SpringOf(SpringParams.Default),
    };
}

/// <summary>
/// The font families of a <see cref="ThemeDefinition"/>. <see cref="Body"/> is the default text family CHAIN (a
/// comma-separated family list, primary first, e.g. <c>"Segoe UI Variable Text, Segoe UI"</c>); the font registry
/// (<see cref="FluentGpu.Text.FontRegistry"/>, E9) walks it entry by entry and uses the first that exists, so a missing
/// primary falls back along the chain and an exhausted chain is an error. <see cref="BodyPrimary"/> is the first entry
/// (a single-name consumer's preference). <see cref="Icon"/>
/// is the symbol face behind <c>Ui.Icon</c> / IconButton / NavigationView. A family is a system name or
/// <c>"path/to.ttf#Family"</c>. Read as <c>Tok.Definition.Fonts</c>, at use, never captured at construction.
/// </summary>
public sealed record FontSet
{
    public FontSet(string body, string icon)
    {
        Body = body;
        Icon = icon;
        BodyPrimary = FirstFamily(body);
    }

    /// <summary>The default text family chain, primary first.</summary>
    public string Body { get; }
    /// <summary>The first family of <see cref="Body"/> (computed once at construction; reading it never allocates).</summary>
    public string BodyPrimary { get; }
    /// <summary>The icon font family.</summary>
    public string Icon { get; }

    private static string FirstFamily(string chain)
    {
        int comma = chain.IndexOf(',');
        return comma < 0 ? chain : chain.Substring(0, comma).Trim();
    }

    /// <summary>The Fluent default set: Segoe UI body, Segoe Fluent Icons symbols (today's engine values).</summary>
    public static FontSet FluentDefault { get; } = new("Segoe UI", "Segoe Fluent Icons");
}

/// <summary>Per-theme material policy (window material, opaque canvas, acrylic recipes). Reserved: the members arrive
/// with the window-material work (EP9); today the host-set <see cref="FluentGpu.Dsl.Materials"/> statics remain the
/// source and every definition carries the empty policy.</summary>
public sealed record MaterialPolicy
{
    public static MaterialPolicy FluentDefault { get; } = new();
}

/// <summary>Per-theme icon overrides merged into the themed-icon registry under a theme scope. Reserved: members arrive
/// with the icon-set work; every definition carries the empty set today.</summary>
public sealed record IconSet
{
    public static IconSet FluentDefault { get; } = new();
}

/// <summary>Theme-defined extra colour tokens (name to colour per theme kind, interned to ids). Reserved: members arrive
/// with the extension-token work; every definition carries <see cref="Empty"/> today.</summary>
public sealed record ExtTokenTable
{
    public static ExtTokenTable Empty { get; } = new();
}
