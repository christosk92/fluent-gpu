using FluentGpu.Controls;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The avatar's initials size: WinUI's 42 % of the diameter, floored at the 11 DIP type floor where that still fits in half the circle.</summary>
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
    public void Initials_follow_the_42_percent_rule_with_a_legible_floor(float size, float expected)
        => Assert.Equal(expected, PersonPicture.InitialsFontSizeFor(size), 3);

    [Fact]
    public void The_template_settings_use_the_floored_size()
        => Assert.Equal(11f, PersonPicture.PersonPictureTemplateSettings.For("AB", false, 24f).InitialsFontSize, 3);
}
