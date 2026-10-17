using FluentGpu.SourceGen.Analyzers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace FluentGpu.SourceGen.Tests;

/// <summary>FGTH004: a theme token (Spacing / Radii / Elevation / the type ramp / Tok / Theme) frozen into a <c>const</c>
/// or <c>static readonly</c> field. Positive and negative coverage, resolved against the real engine.</summary>
public sealed class FrozenThemeTokenAnalyzerTests
{
    private const string Usings =
        "using FluentGpu.Dsl; using FluentGpu.Foundation; using FluentGpu.Signals; using System;\n";

    private static int Fgth004(string source)
        => Harness.Count(Harness.Analyze(new FrozenThemeTokenAnalyzer(), Usings + source), "FGTH004");

    [Fact]
    public void Fires_On_Static_Readonly_Spacing()
    {
        var diags = Harness.Analyze(new FrozenThemeTokenAnalyzer(), Usings + """
            static class Gaps { static readonly float Gap = Spacing.M; }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGTH004"));
        Assert.Equal("Spacing.M", Harness.SpanText(diags[0]));
        Assert.Equal(DiagnosticSeverity.Warning, diags[0].Severity);
    }

    [Fact]
    public void Fires_On_Static_Readonly_Radii_Corners()
    {
        Assert.Equal(1, Fgth004("""
            static class Chrome { static readonly CornerRadius4 Corners = new(0f, Radii.Overlay, Radii.Overlay, 0f); }
            """));
    }

    [Fact]
    public void Fires_On_Static_Readonly_Radii_Composite()
    {
        Assert.Equal(1, Fgth004("""
            static class Chrome { static readonly CornerRadius4 Control = Radii.ControlAll; }
            """));
    }

    [Fact]
    public void Fires_On_Static_Readonly_Tok_Colour()
    {
        Assert.Equal(1, Fgth004("""
            static class Ink { static readonly ColorF Text = Tok.TextPrimary; }
            """));
    }

    [Fact]
    public void Fires_On_Static_Readonly_Tok_Definition()
    {
        Assert.Equal(1, Fgth004("""
            static class Fonts { static readonly string Icons = Tok.Definition.Fonts.Icon; }
            """));
    }

    [Fact]
    public void Fires_On_Static_Readonly_Theme_Getter()
    {
        Assert.Equal(1, Fgth004("""
            static class Ink { static readonly ColorF Window = Theme.WindowText; }
            """));
    }

    [Fact]
    public void Fires_On_Static_Readonly_Elevation()
    {
        Assert.Equal(1, Fgth004("""
            static class Cards { static readonly ShadowSpec Shadow = Elevation.Card; }
            """));
    }

    [Fact]
    public void Fires_On_Static_Readonly_Type_Ramp_Factory()
    {
        Assert.Equal(1, Fgth004("""
            static class Labels { static readonly TextEl Heading = Ui.Title("x"); }
            """));
    }

    [Fact]
    public void Fires_On_Const_Initialised_From_A_Token()
    {
        // The compiler also rejects this (a property is not a constant expression); the analyzer fires regardless.
        Assert.Equal(1, Fgth004("""
            static class Gaps { const float Gap = Spacing.M; }
            """));
    }

    [Fact]
    public void Fires_Once_Per_Offending_Variable()
    {
        Assert.Equal(2, Fgth004("""
            static class Gaps { static readonly float A = Spacing.S, B = Spacing.L + 1f, C = 4f; }
            """));
    }

    [Fact]
    public void Silent_On_Token_Read_Inside_A_Lambda_Thunk()
    {
        Assert.Equal(0, Fgth004("""
            static class Brushes
            {
                static readonly Prop<ColorF> Primary = Prop.Of(static () => Tok.TextPrimary);
                static readonly Func<float> Gap = () => Spacing.M;
            }
            """));
    }

    [Fact]
    public void Silent_On_Instance_Fields_Properties_And_Locals()
    {
        Assert.Equal(0, Fgth004("""
            sealed class Card
            {
                readonly float _gap = Spacing.M;
                float Pad { get; } = Spacing.L;
                static float Gap => Spacing.M;
                static float Compute() { float local = Spacing.S; return local; }
            }
            """));
    }

    [Fact]
    public void Silent_On_Static_Non_Readonly_Field()
    {
        Assert.Equal(0, Fgth004("""
            static class Cache { static float _gap = Spacing.M; }
            """));
    }

    [Fact]
    public void Silent_On_Theme_Invariant_Members()
    {
        Assert.Equal(0, Fgth004("""
            static class Fixed
            {
                static readonly ColorF OnMedia = Tok.OnMediaPrimary;
                const float Focus = Tok.FocusThickness;
                static readonly CornerRadius4 Dot = Radii.Circle(6f);
                static readonly ShadowSpec None = Elevation.None;
            }
            """));
    }

    [Fact]
    public void Silent_On_Nameof_And_Typeof()
    {
        Assert.Equal(0, Fgth004("""
            static class Names
            {
                const string Epoch = nameof(Tok.Epoch);
                static readonly Type Kind = typeof(Tok);
            }
            """));
    }

    [Fact]
    public void Silent_On_Unrelated_Static_Readonly()
    {
        Assert.Equal(0, Fgth004("""
            static class Plain
            {
                static readonly float Gap = 12f;
                static readonly ColorF Red = ColorF.FromRgba(0xFF, 0, 0);
                static readonly ThemeKind Kind = ThemeKind.Dark;
            }
            """));
    }
}
