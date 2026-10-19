using FluentGpu.Controls;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The avatar's initials size: WinUI's 42 % of the diameter, floored at a theme's <see cref="PersonPicture.Style.InitialsFloor"/> where that still fits
/// in half the circle; no floor (the built-in style) is the 42 % rule alone.</summary>
public sealed class PersonPictureInitialsTests
{
    [Theory]
    [InlineData(96f, 40.32f)]   // the default avatar: 42 %, untouched by the floor
    [InlineData(32f, 13.44f)]
    [InlineData(28f, 11.76f)]
    [InlineData(24f, 11f)]      // the attribution portrait: 10.08 lifts to the floor
    [InlineData(22f, 11f)]      // still fits: half of 22 is 11
    [InlineData(16f, 8f)]       // a tiny avatar: capped at half its diameter, so the letters stay inside
    [InlineData(1f, 1f)]        // never under 1
    public void An_eleven_floor_lifts_small_initials_within_half_the_circle(float size, float expected)
        => Assert.Equal(expected, PersonPicture.InitialsFontSizeFor(size, 11f), 3);

    [Theory]
    [InlineData(96f, 40.32f)]
    [InlineData(24f, 10.08f)]   // no floor: exactly what the control drew before themes could set one
    [InlineData(1f, 1f)]
    public void Without_a_floor_the_42_percent_rule_stands(float size, float expected)
        => Assert.Equal(expected, PersonPicture.InitialsFontSizeFor(size), 3);

    [Fact]
    public void The_template_settings_carry_the_floor()
    {
        Assert.Equal(11f, PersonPicture.PersonPictureTemplateSettings.For("AB", false, 24f, 11f).InitialsFontSize, 3);
        Assert.Equal(10.08f, PersonPicture.PersonPictureTemplateSettings.For("AB", false, 24f).InitialsFontSize, 3);
    }
}
