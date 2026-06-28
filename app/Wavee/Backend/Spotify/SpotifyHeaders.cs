namespace Wavee.Backend.Spotify;

// Desktop client-identity constants Spotify expects on spclient requests. Lifted from the genuine desktop wire — the
// version strings (esp. the client-token client_version's git-hash suffix) are part of the server's allowlist signature,
// so stale/no-hash values get tagged untrusted. Centralized so login5 / client-token / spclient all agree.
public static class SpotifyHeaders
{
    public const string ClientId = "65b708073fc0480ea92a077233ca87bd";     // Spotify's public desktop client id
    public const string ClientVersion = "1.2.88.483.g8aa8628e";            // client-token client_version
    public const string AppPlatform = "Win32_x86_64";
    public const string AppVersion = "128800483";
    public const string UserAgent = "Spotify/128800483 Win32_x86_64/0 (PC desktop)";
}
