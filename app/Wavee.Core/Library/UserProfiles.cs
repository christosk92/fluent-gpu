namespace Wavee.Core;

/// <summary>Session-scoped user profile lookup for Spotify user ids surfaced by playlist owner / added-by fields.</summary>
public interface IUserProfileService
{
    /// <summary>Returns the cached profile for a bare id or <c>spotify:user:</c> uri, or null when unresolved/missing.</summary>
    Owner? Get(string userUriOrId);

    /// <summary>Starts best-effort resolution for any ids not already cached. Must never throw into callers.</summary>
    void Prefetch(IEnumerable<string> userUriOrIds);

    /// <summary>Emits the canonical <c>spotify:user:{id}</c> uri when a profile cache entry changes.</summary>
    IObservable<string> Changed { get; }
}

public static class UserProfileIds
{
    public const string Prefix = "spotify:user:";

    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var id = trimmed[Prefix.Length..];
            return IsBareId(id) ? trimmed : null;
        }

        return IsBareId(trimmed) ? Prefix + trimmed : null;
    }

    public static string BareId(string userUriOrId)
        => userUriOrId.StartsWith(Prefix, StringComparison.Ordinal)
            ? userUriOrId[Prefix.Length..]
            : userUriOrId;

    static bool IsBareId(string value)
    {
        if (value.Length == 0) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch) || ch == ':') return false;
        }
        return true;
    }
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding consumers.</summary>
public sealed class SwitchableUserProfileService : IUserProfileService, IDisposable
{
    readonly SimpleEvent<string> _changed = new();
    readonly object _gate = new();
    IUserProfileService _inner;
    IDisposable? _sub;

    public SwitchableUserProfileService(IUserProfileService inner)
    {
        _inner = inner;
        Wire(inner);
    }

    public void SetInner(IUserProfileService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        lock (_gate)
        {
            _sub?.Dispose();
            _inner = inner;
            Wire(inner);
        }
    }

    public Owner? Get(string userUriOrId) => Current.Get(userUriOrId);
    public void Prefetch(IEnumerable<string> userUriOrIds) => Current.Prefetch(userUriOrIds);
    public IObservable<string> Changed => _changed;

    IUserProfileService Current => System.Threading.Volatile.Read(ref _inner);

    void Wire(IUserProfileService inner)
        => _sub = inner.Changed.Subscribe(new ProfileObserver(uri => _changed.OnNext(uri)));

    public void Dispose()
    {
        lock (_gate)
        {
            _sub?.Dispose();
            _sub = null;
        }
    }

    sealed class ProfileObserver(Action<string> onNext) : IObserver<string>
    {
        public void OnNext(string value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Offline/fake fallback: no live transport, so lookups are empty and prefetches are ignored.</summary>
public sealed class NullUserProfileService : IUserProfileService
{
    readonly SimpleEvent<string> _changed = new();
    public Owner? Get(string userUriOrId) => null;
    public void Prefetch(IEnumerable<string> userUriOrIds) { }
    public IObservable<string> Changed => _changed;
}
