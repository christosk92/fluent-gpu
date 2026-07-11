namespace FluentGpu.Controls;

/// <summary>
/// Name → <see cref="IconDef"/> table for <see cref="ThemedIcon"/>. Seeded once (on first touch) from the harvested +
/// hand-authored <c>ThemedIconData</c> set; apps register their own domain icons with <see cref="Register"/> (e.g. the
/// music-domain PlayNext/AddToQueue). AOT-safe: plain static data, no reflection scan. UI-thread only.
/// </summary>
public static class ThemedIconRegistry
{
    private static readonly Dictionary<string, IconDef> Defs = new(StringComparer.Ordinal);

    static ThemedIconRegistry() => ThemedIconData.RegisterAll();

    /// <summary>Register (or replace) an icon by name. Idempotent; last write wins.</summary>
    public static void Register(string name, IconDef def) => Defs[name] = def;

    /// <summary>Look up an icon by name.</summary>
    public static bool TryGet(string name, out IconDef? def) => Defs.TryGetValue(name, out def);

    /// <summary>True if an icon with this name is registered.</summary>
    public static bool Has(string name) => Defs.ContainsKey(name);

    /// <summary>Registered icon count (diagnostics / gates).</summary>
    public static int Count => Defs.Count;
}
