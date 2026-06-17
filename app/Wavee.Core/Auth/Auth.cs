namespace Wavee.Core;

public enum AuthStatus { LoggedOut, Authenticating, Authenticated, Error }

public sealed record WaveeUser(string Id, string DisplayName, string? AvatarUrl, bool IsPremium);

/// <summary>The session / auth seam. Collapses WaveeMusic's <c>ISession</c> + auth-state surface
/// down to what the first scaffold needs: a status, the current user, and connect/logout.</summary>
public interface ISpotifySession
{
    AuthStatus Status { get; }
    WaveeUser? CurrentUser { get; }
    IObservable<AuthStatus> StatusChanged { get; }
    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}
