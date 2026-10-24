namespace FluentGpu.Theming;

/// <summary>
/// The typed key of one entry of a <see cref="ControlStyleTable"/>. <typeparamref name="TProvider"/> is what the control asks the
/// table for: normally a <c>Func&lt;TStyle&gt;</c> that BUILDS the control's default style now (so it can read the live
/// <c>Tok</c> and the active definition's scales), or, for a control whose style depends on its arguments, a delegate over them
/// (<c>Button</c>: appearance and size). Keys are declared once, <c>static readonly</c>, by the control that owns the style
/// (the engine does not know the controls: <c>FluentGpu.Controls.ControlStyles</c> lists them), and identified by
/// <see cref="Id"/> (case-sensitive, ordinal).
/// </summary>
public readonly record struct ControlStyleKey<TProvider>(string Id) where TProvider : class;

/// <summary>
/// The per-control style table carried by <see cref="ThemeDefinition.Controls"/>: control kind to the provider that builds that
/// control's default style. It REPLACES the single global <c>Button.StyleHook</c> with one entry per control that has a style
/// record, so a theme swap (one <c>Tok.Use</c>) restyles every mounted control on the next frame.
/// <para><b>An absent entry means "the control's built-in Fluent look".</b> The built-in defaults stay with the controls (they are
/// the WinUI-derived ladders in <c>FluentGpu.Controls</c>, which the engine assembly cannot reference), so
/// <see cref="FluentDefault"/> is the empty table and a definition that does not restyle a control costs nothing and renders
/// exactly what the control rendered before tables existed. A theme restyles a control with
/// <c>table.With(ControlStyles.SliderStyle, static () =&gt; Slider.FluentStyle() with { ... })</c>.</para>
/// <para>Entries are read at RENDER (<c>Tok.Definition.Controls</c>), never captured at type load. The table is immutable and
/// value-equal when every entry has the same key and an equal provider (delegates compare by target and method), which is what
/// <see cref="ThemeDefinition.DiffersOnlyInColours"/> uses to treat a changed table as a non-colour change. Lookup is a linear
/// scan of a tiny array and allocates nothing.</para>
/// </summary>
public sealed class ControlStyleTable : IEquatable<ControlStyleTable>
{
    private readonly (string Id, object Provider)[] _entries;   // sorted by Id (ordinal): a canonical order for equality

    private ControlStyleTable((string Id, object Provider)[] entries) => _entries = entries;

    /// <summary>The table with no entries: every control renders its built-in Fluent default.</summary>
    public static ControlStyleTable Empty { get; } = new([]);

    /// <summary>The engine default table: the same instance as <see cref="Empty"/> (the built-in defaults live with the controls).</summary>
    public static ControlStyleTable FluentDefault => Empty;

    /// <summary>The number of controls this table restyles.</summary>
    public int Count => _entries.Length;

    /// <summary>The ids of the restyled controls, in canonical order.</summary>
    public IEnumerable<string> Ids => _entries.Select(static e => e.Id);

    /// <summary>The provider registered for <paramref name="key"/>; false when the table has none (the control falls back to its
    /// built-in default). Allocation-free.</summary>
    public bool TryGet<TProvider>(ControlStyleKey<TProvider> key, out TProvider provider) where TProvider : class
    {
        var entries = _entries;
        for (int i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Id, key.Id, StringComparison.Ordinal) && entries[i].Provider is TProvider typed)
            {
                provider = typed;
                return true;
            }
        }
        provider = null!;
        return false;
    }

    /// <summary>True when the table has an entry for <paramref name="key"/>.</summary>
    public bool Contains<TProvider>(ControlStyleKey<TProvider> key) where TProvider : class => TryGet(key, out _);

    /// <summary>A copy of this table with <paramref name="provider"/> registered for <paramref name="key"/> (replacing any entry).</summary>
    public ControlStyleTable With<TProvider>(ControlStyleKey<TProvider> key, TProvider provider) where TProvider : class
    {
        ArgumentNullException.ThrowIfNull(key.Id);
        ArgumentNullException.ThrowIfNull(provider);
        var list = new List<(string Id, object Provider)>(_entries.Length + 1);
        bool placed = false;
        foreach (var e in _entries)
        {
            if (string.Equals(e.Id, key.Id, StringComparison.Ordinal)) continue;
            if (!placed && string.CompareOrdinal(key.Id, e.Id) < 0) { list.Add((key.Id, provider)); placed = true; }
            list.Add(e);
        }
        if (!placed) list.Add((key.Id, provider));
        return new ControlStyleTable(list.ToArray());
    }

    /// <summary>A copy of this table without the entry for <paramref name="key"/> (the control falls back to its built-in default).</summary>
    public ControlStyleTable Without<TProvider>(ControlStyleKey<TProvider> key) where TProvider : class
    {
        if (!Contains(key)) return this;
        return new ControlStyleTable(_entries.Where(e => !string.Equals(e.Id, key.Id, StringComparison.Ordinal)).ToArray());
    }

    public bool Equals(ControlStyleTable? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other._entries.Length != _entries.Length) return false;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!string.Equals(_entries[i].Id, other._entries[i].Id, StringComparison.Ordinal)) return false;
            if (!_entries[i].Provider.Equals(other._entries[i].Provider)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ControlStyleTable t && Equals(t);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var e in _entries) { h.Add(e.Id, StringComparer.Ordinal); h.Add(e.Provider); }
        return h.ToHashCode();
    }

    public override string ToString() => _entries.Length == 0 ? "ControlStyleTable { }" : $"ControlStyleTable {{ {string.Join(", ", Ids)} }}";
}
