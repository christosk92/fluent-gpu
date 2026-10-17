using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>An app role table with one colour role and one non-colour role, the shape the contract exists for.</summary>
internal sealed record AppRolesFixture(ColorF Brand, int RowHeight) : IAppRoles
{
    public bool DiffersOnlyInColours(IAppRoles other) => other is AppRolesFixture o && o.RowHeight == RowHeight;
}

/// <summary>
/// E16 (EP5): <see cref="ThemeDefinition.AppRoles"/> is the source of <see cref="ViewContext.App{T}"/>, cached per
/// <c>Tok.Epoch</c>, and takes part in <see cref="ThemeDefinition.DiffersOnlyInColours"/> through
/// <see cref="IAppRoles.DiffersOnlyInColours"/>. Serial: it writes Tok's process-wide definition and constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class AppRolesTests
{
    private static ThemeDefinition WithRoles(IAppRoles? roles, string id = "roles")
        => ThemeDefinition.FluentDefault with { Id = id, AppRoles = roles };

    private static readonly AppRolesFixture Warm = new(ColorF.FromRgba(200, 120, 40), 44);

    // ── DiffersOnlyInColours ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AColourOnlyRoleChange_IsColourOnly()
    {
        var a = WithRoles(Warm);
        Assert.True(a.DiffersOnlyInColours(a));
        Assert.True(a.DiffersOnlyInColours(WithRoles(Warm with { })));                                          // equal value, other instance
        Assert.True(a.DiffersOnlyInColours(WithRoles(Warm with { Brand = ColorF.FromRgba(10, 20, 30) })));      // a colour role moved
    }

    [Fact]
    public void ANonColourRoleChange_IsNotColourOnly()
    {
        var a = WithRoles(Warm);
        Assert.False(a.DiffersOnlyInColours(WithRoles(Warm with { RowHeight = 52 })));
        // A colour change riding along with a non-colour one is still a non-colour change.
        Assert.False(a.DiffersOnlyInColours(WithRoles(Warm with { Brand = ColorF.FromRgba(10, 20, 30), RowHeight = 52 })));
    }

    [Fact]
    public void AMissingTable_AgainstAPresentOne_IsADifference()
    {
        Assert.False(WithRoles(null).DiffersOnlyInColours(WithRoles(Warm)));
        Assert.False(WithRoles(Warm).DiffersOnlyInColours(WithRoles(null)));
        Assert.True(WithRoles(null).DiffersOnlyInColours(WithRoles(null)));
    }

    [Fact]
    public void ADifferentImplementation_IsTreatedAsADifferenceByTheTableItself()
    {
        Assert.False(WithRoles(Warm).DiffersOnlyInColours(WithRoles(new OtherRoles())));
    }

    private sealed class OtherRoles : IAppRoles
    {
        public bool DiffersOnlyInColours(IAppRoles other) => false;
    }

    [Fact]
    public void Use_BumpsStyleEpoch_OnlyWhenTheRolesChangeBeyondColour()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(WithRoles(Warm), kind);
        try
        {
            int epoch = Tok.Epoch, style = Tok.StyleEpoch;

            Tok.Use(WithRoles(Warm with { Brand = ColorF.FromRgba(1, 2, 3) }, "recoloured"), kind);
            Assert.Equal((epoch + 1, style), (Tok.Epoch, Tok.StyleEpoch));          // colour roles: the live cross-fade path

            Tok.Use(WithRoles(Warm with { RowHeight = 56 }, "taller"), kind);
            Assert.Equal((epoch + 2, style + 1), (Tok.Epoch, Tok.StyleEpoch));      // a non-colour role: applied as a unit
        }
        finally { Tok.Use(saved, kind); }
    }

    // ── RoleKey / ExtRoleTable (reserved) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReservedExtRoleTable_RoundTripsTypedRoles()
    {
        var key = new RoleKey<string>("wavee.hero.scrim@1");
        var other = new RoleKey<AppRolesFixture>("wavee.hero.scrim@1");
        var table = ExtRoleTable.Empty.With(key, "dark");

        Assert.Equal(0, ExtRoleTable.Empty.Count);
        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet(key, out string v));
        Assert.Equal("dark", v);
        Assert.False(table.TryGet(other, out _));                                   // same id, other type
        Assert.False(table.TryGet(new RoleKey<string>("missing"), out _));
        Assert.Equal(0, ExtRoleTable.Empty.Count);                                  // immutable
    }

    // ── ViewContext.App<T>() ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void App_ReadsTheActiveDefinitionsRoles_AndFollowsAThemeSwap()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        var recorded = new List<int>();
        Tok.Use(WithRoles(Warm), kind);
        try
        {
            var registry = new ViewRegistryBuilder()
                .Add(Views.Bar, (BarProps p, in ViewContext ctx) =>
                {
                    recorded.Add(ctx.App<AppRolesFixture>().RowHeight);
                    return new BoxEl { Height = 10f };
                }, "roles.t")
                .Build();
            var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
            using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), null, page));
            h.Pump();
            Assert.Equal(new[] { 44 }, recorded.ToArray());

            Tok.Use(WithRoles(Warm with { RowHeight = 60 }, "taller"), kind);       // a theme swap carries new roles
            h.Pump();
            Assert.Equal(60, recorded[^1]);                                         // the template re-rendered with the new roles
        }
        finally { Tok.Use(saved, kind); }
    }

    [Fact]
    public void App_IsCachedPerEpoch_AndRevalidatedAgainstTheLiveReference()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        try
        {
            Tok.Use(WithRoles(Warm), kind);
            var first = AppRolesCache<AppRolesFixture>.Get();
            Assert.Same(Warm, first);
            Assert.Same(first, AppRolesCache<AppRolesFixture>.Get());               // steady: the cached object

            var next = Warm with { RowHeight = 70 };
            Tok.Use(WithRoles(next, "next"), kind);
            Assert.Same(next, AppRolesCache<AppRolesFixture>.Get());                // a new epoch re-reads

            Assert.Null(AppRolesCache<OtherRoles2>.Get());                          // another type: not the roles object
            Tok.Use(WithRoles(null, "none"), kind);
            Assert.Null(AppRolesCache<AppRolesFixture>.Get());
        }
        finally { Tok.Use(saved, kind); }
    }

    private sealed class OtherRoles2 { }

    [Fact]
    public void App_Throws_WhenNoRolesAreSet_OrTheyAreAnotherType()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(WithRoles(null), kind);
        try
        {
            Exception? seen = null;
            var registry = new ViewRegistryBuilder()
                .Add(Views.Bar, (BarProps p, in ViewContext ctx) =>
                {
                    try { ctx.App<AppRolesFixture>(); } catch (InvalidOperationException e) { seen = e; }
                    return new BoxEl { Height = 10f };
                }, "roles.missing")
                .Build();
            var page = new SlotPage(() => Slots.View(Views.Bar, new BarProps("a")));
            using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), null, page));
            h.Pump();
            Assert.NotNull(seen);
            Assert.Contains("no AppRoles", seen!.Message);
        }
        finally { Tok.Use(saved, kind); }
    }

    [Fact]
    public void TheContext_StillOverridesTheDefinitionForASubtree()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(WithRoles(Warm), kind);
        try
        {
            int row = 0;
            var overrideRoles = Warm with { RowHeight = 99 };
            var registry = new ViewRegistryBuilder()
                .Add(Views.Bar, (BarProps p, in ViewContext ctx) => { row = ctx.App<AppRolesFixture>().RowHeight; return new BoxEl { Height = 10f }; }, "roles.override")
                .Build();
            var page = new SlotPage(() => Ctx.Provide(ViewContext.AppRolesContext, overrideRoles, Slots.View(Views.Bar, new BarProps("a"))));
            using var h = new SlotHarness(new SlotRoot(new Signal<ViewRegistry?>(registry), null, page));
            h.Pump();
            Assert.Equal(99, row);
        }
        finally { Tok.Use(saved, kind); }
    }
}
