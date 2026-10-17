using FluentGpu.Dsl;

namespace FluentGpu.Theming;

/// <summary>
/// The contract an application's role table implements to live on <see cref="ThemeDefinition.AppRoles"/>. The roles of an
/// application are a mix of colours (a brand wash, a status colour) and non-colour data (a row height, a corner choice, a
/// font weight), so reference or value equality cannot say whether two role tables differ only in colours. The table
/// itself can: <see cref="DiffersOnlyInColours"/> is the engine's question to it, asked while it decides between the live
/// cross-fade path (colours only) and an atomic application (anything else).
/// </summary>
public interface IAppRoles
{
    /// <summary>
    /// True when <paramref name="other"/> differs from this table only in colour roles: every non-colour role is equal.
    /// Equal tables return true. Called on the UI thread by <c>Tok.Use</c> (once per definition swap); it must not allocate on
    /// the equal path and must treat an <paramref name="other"/> of an unknown implementation as different (false).
    /// </summary>
    bool DiffersOnlyInColours(IAppRoles other);
}

/// <summary>
/// RESERVED for the theme-package loader: the typed key of one extension role (<c>"wavee.hero.scrim@1"</c>) in an
/// <see cref="ExtRoleTable"/>. Nothing in the engine reads it yet; it exists so a loader and the application can agree on the
/// key shape before the loader lands.
/// </summary>
public readonly record struct RoleKey<T>(string Id) where T : class;

/// <summary>
/// RESERVED for the theme-package loader: an immutable table of extension roles keyed by <see cref="RoleKey{T}"/> id, for
/// roles a loaded theme declares that the application's typed <see cref="IAppRoles"/> object does not know at compile time.
/// Nothing in the engine reads it yet and <see cref="ThemeDefinition"/> does not carry one; it is deliberately minimal
/// (empty, add, typed lookup) so the loader can grow it without changing a published shape.
/// </summary>
public sealed class ExtRoleTable
{
    private readonly Dictionary<string, object> _roles;

    private ExtRoleTable(Dictionary<string, object> roles) => _roles = roles;

    /// <summary>The table with no roles.</summary>
    public static ExtRoleTable Empty { get; } = new(new Dictionary<string, object>(0));

    /// <summary>The number of roles.</summary>
    public int Count => _roles.Count;

    /// <summary>A copy of this table with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    public ExtRoleTable With<T>(RoleKey<T> key, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var copy = new Dictionary<string, object>(_roles) { [key.Id] = value };
        return new ExtRoleTable(copy);
    }

    /// <summary>The role for <paramref name="key"/>; false when it is absent or holds another type.</summary>
    public bool TryGet<T>(RoleKey<T> key, out T value) where T : class
    {
        if (_roles.TryGetValue(key.Id, out object? o) && o is T typed) { value = typed; return true; }
        value = null!;
        return false;
    }
}

/// <summary>
/// The per-<c>Tok.Epoch</c> cache behind <see cref="ViewContext.App{T}"/>: the active definition's <c>AppRoles</c> checked as a
/// <typeparamref name="T"/> once per epoch instead of once per template call. UI-thread only (a template runs in a render).
/// The cached value is also validated against the live <c>AppRoles</c> reference, so an epoch that did not move (an
/// equal-valued definition adopted by reference) can never serve a stale object.
/// </summary>
internal static class AppRolesCache<T> where T : class
{
    private static int s_epoch = -1;
    private static object? s_source;
    private static T? s_value;

    public static T? Get()
    {
        object? source = Tok.Definition.AppRoles;
        if (s_epoch == Tok.Epoch && ReferenceEquals(s_source, source)) return s_value;
        s_epoch = Tok.Epoch;
        s_source = source;
        s_value = source as T;
        return s_value;
    }
}
