using FluentGpu.Dsl;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// E14 (EP15): <see cref="ControlStyleTable"/> is an immutable, value-equal map from a typed key to the provider that builds a
/// control's default style. An absent entry is the control's built-in look, so the empty table is <c>FluentDefault</c>; a changed
/// table is a non-colour change of the definition (<c>StyleEpoch</c> moves). The per-control behaviour (every control reads the
/// table at render, the sample renders identically under FluentDefault) is gated in the VerticalSlice
/// <c>control-style-table</c> suite, which can reference the controls. Serial: it writes Tok's process-wide definition.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ControlStyleTableTests
{
    private sealed record FakeStyle(int Level);

    private static readonly ControlStyleKey<Func<FakeStyle>> A = new("fake.a");
    private static readonly ControlStyleKey<Func<FakeStyle>> B = new("fake.b");
    private static readonly ControlStyleKey<Func<string>> OtherType = new("fake.a");   // same id, another provider type

    private static FakeStyle Level1() => new(1);
    private static FakeStyle Level2() => new(2);

    [Fact]
    public void FluentDefault_IsTheEmptyTable()
    {
        Assert.Same(ControlStyleTable.Empty, ControlStyleTable.FluentDefault);
        Assert.Equal(0, ControlStyleTable.FluentDefault.Count);
        Assert.False(ControlStyleTable.Empty.TryGet(A, out _));
    }

    [Fact]
    public void With_AddsOrReplacesOneEntry_AndNeverMutatesTheOriginal()
    {
        var one = ControlStyleTable.Empty.With(A, Level1);
        var replaced = one.With(A, Level2);

        Assert.Equal(0, ControlStyleTable.Empty.Count);
        Assert.Equal(1, one.Count);
        Assert.Equal(1, replaced.Count);
        Assert.True(one.TryGet(A, out var p1));
        Assert.Equal(1, p1().Level);
        Assert.True(replaced.TryGet(A, out var p2));
        Assert.Equal(2, p2().Level);
        Assert.False(one.TryGet(B, out _));
    }

    [Fact]
    public void Without_RemovesTheEntry_OrReturnsTheSameTable()
    {
        var both = ControlStyleTable.Empty.With(A, Level1).With(B, Level2);
        var noA = both.Without(A);
        Assert.False(noA.Contains(A));
        Assert.True(noA.Contains(B));
        Assert.Same(noA, noA.Without(A));
        Assert.Equal(new[] { "fake.a", "fake.b" }, both.Ids.ToArray());
    }

    [Fact]
    public void AKeyOfAnotherProviderType_DoesNotMatchTheSameId()
    {
        var table = ControlStyleTable.Empty.With(A, Level1);
        Assert.False(table.TryGet(OtherType, out _));
    }

    [Fact]
    public void Equality_IsByEntries_NotByInsertionOrderOrInstance()
    {
        var ab = ControlStyleTable.Empty.With(A, Level1).With(B, Level2);
        var ba = ControlStyleTable.Empty.With(B, Level2).With(A, Level1);
        Assert.Equal(ab, ba);
        Assert.Equal(ab.GetHashCode(), ba.GetHashCode());
        Assert.NotEqual(ab, ControlStyleTable.Empty.With(A, Level1));
        Assert.NotEqual(ab, ControlStyleTable.Empty.With(B, Level2).With(A, Level2));   // another provider for A
        Assert.NotEqual(ControlStyleTable.Empty, ab);
    }

    [Fact]
    public void ANewKeyDeclaredAnywhere_NeedsNoEngineChange()
    {
        // The table is keyed by typed id, so a control (or an app) declares its own key; nothing in the engine lists the controls.
        var custom = new ControlStyleKey<Func<FakeStyle>>("app.custom");
        Assert.True(ControlStyleTable.Empty.With(custom, Level1).Contains(custom));
    }

    [Fact]
    public void ADifferentTable_IsANonColourChange_AnEqualOneIsNot()
    {
        var def = ThemeDefinition.FluentDefault;
        var withTable = def with { Controls = ControlStyleTable.Empty.With(A, Level1) };
        Assert.False(def.DiffersOnlyInColours(withTable));
        Assert.True(withTable.DiffersOnlyInColours(def with { Id = "other", Controls = ControlStyleTable.Empty.With(A, Level1) }));
        Assert.True(def.DiffersOnlyInColours(def with { Controls = ControlStyleTable.FluentDefault }));
    }

    [Fact]
    public void Use_BumpsStyleEpoch_WhenTheControlTableChanges()
    {
        var saved = Tok.Definition; var kind = Tok.Theme;
        Tok.Use(ThemeDefinition.FluentDefault, kind);
        try
        {
            int epoch = Tok.Epoch, style = Tok.StyleEpoch;
            Tok.Use(ThemeDefinition.FluentDefault with { Id = "styled", Controls = ControlStyleTable.Empty.With(A, Level1) }, kind);
            Assert.Equal((epoch + 1, style + 1), (Tok.Epoch, Tok.StyleEpoch));
            Assert.True(Tok.Definition.Controls.Contains(A));
            Tok.Use(ThemeDefinition.FluentDefault, kind);
            Assert.Equal((epoch + 2, style + 2), (Tok.Epoch, Tok.StyleEpoch));
            Assert.False(Tok.Definition.Controls.Contains(A));
        }
        finally { Tok.Use(saved, kind); }
    }

    [Fact]
    public void LookupAllocatesNothing()
    {
        var table = ControlStyleTable.Empty.With(A, Level1).With(B, Level2);
        for (int i = 0; i < 64; i++) table.TryGet(B, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = true;
        for (int i = 0; i < 4096; i++) ok &= table.TryGet(B, out _) & !ControlStyleTable.Empty.TryGet(A, out _);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(ok);
    }
}
