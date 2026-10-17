using FluentGpu.Dsl;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// <see cref="ViewRegistryBuilder.AddMissing"/>: a theme that is still being built registers its own views first and fills every other key
/// from another theme's registry, key by key; its own registrations always win.
/// </summary>
public sealed class ViewRegistryAddMissingTests
{
    private sealed record P(string Title);
    private sealed record M(int Value);

    private static readonly ViewKey<P> A = new("test.a@1");
    private static readonly ViewKey<P> B = new("test.b@1");
    private static readonly StyleKey<M> S = new("test.s");
    private static readonly StyleKey<M> T = new("test.t");

    [Fact]
    public void Own_views_and_styles_win_and_the_rest_come_from_the_fallback()
    {
        var fallback = new ViewRegistryBuilder()
            .Add(A, static (P p, in ViewContext ctx) => new BoxEl(), "fallback.a")
            .Add(B, static (P p, in ViewContext ctx) => new BoxEl(), "fallback.b")
            .AddStyle(S, new M(1))
            .AddStyle(T, new M(2))
            .Build();

        var merged = new ViewRegistryBuilder()
            .Add(A, static (P p, in ViewContext ctx) => new BoxEl(), "own.a")
            .AddStyle(S, new M(10))
            .AddMissing(fallback)
            .Build();

        Assert.Equal(2, merged.ViewCount);
        Assert.Equal("own.a", merged.TemplateIdOf(A.Id));
        Assert.Equal("fallback.b", merged.TemplateIdOf(B.Id));
        Assert.True(merged.TryGetStyle(S, out var s) && s.Value == 10);
        Assert.True(merged.TryGetStyle(T, out var t) && t.Value == 2);
    }

    [Fact]
    public void A_key_added_after_the_fallback_still_throws_as_a_duplicate()
    {
        var fallback = new ViewRegistryBuilder().Add(A, static (P p, in ViewContext ctx) => new BoxEl(), "fallback.a").Build();
        var builder = new ViewRegistryBuilder().AddMissing(fallback);
        Assert.Throws<System.InvalidOperationException>(() => builder.Add(A, static (P p, in ViewContext ctx) => new BoxEl(), "own.a"));
    }
}
