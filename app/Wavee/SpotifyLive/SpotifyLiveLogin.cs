using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// End-to-end LIVE login with persisted credentials.
// Logging discipline: plain ASCII only; the access token and the reusable-credential BYTES are NEVER logged (only the byte
// count); the account username is REDACTED. The device user-code is surfaced only for the interactive CLI prompt (it is
// transient + single-use and the user must enter it). In-app, the challenge surfaces via AuthState in the UI, not the log.
public static class SpotifyLiveLogin
{
    const string ClientId = "65b708073fc0480ea92a077233ca87bd";   // Spotify's public desktop client id
    static readonly string[] Scopes =
    [
        "streaming", "app-remote-control", "user-read-playback-state", "user-modify-playback-state",
        "user-read-currently-playing", "playlist-read-private", "user-library-read", "user-read-email", "user-read-private",
    ];

    /// <summary>The shared result of a live login — enough to continue to login5 / spclient.</summary>
    public sealed record LoginResult(SpotifyWelcome Welcome, string DeviceId, LocalCredentialStore CredStore, string Scheme,
        ApConnection? Channel = null);

    public static async Task<int> RunAsync(Action<string> log, CancellationToken ct)
    {
        var login = await LoginAsync(log, ct).ConfigureAwait(false);
        if (login is null) return 1;
        var w = login.Welcome;

        // Step 2 - the REAL tier from ProductInfo drives the premium gate. Only a CONFIRMED premium product passes;
        // null/unknown (the trailing 0x50 missing) is treated as Free and refused, so a missing packet can't slip a
        // non-premium account through. (The 0x50 reliably arrives within the post-welcome window; absence is anomalous.)
        var tier = w.Product?.IsPremium == true ? Tier.Premium : Tier.Free;
        if (!SessionGate.IsAllowed(tier))
        {
            log(SessionGate.WarningTitle + " - the signed-in account is not Premium. Refusing (no launch).");
            return 2;
        }
        log("Logged in [" + login.Scheme + "]; next launch skips device-code.");
        return 0;
    }

    /// <summary>The credential chain (stored reusable creds → device-code) → AP handshake/login (with AP failover) → APWelcome,
    /// persisting the fresh reusable credentials. Returns null on any failure (already logged). Reused by RunAsync (the
    /// premium gate) and by the metadata probe (which continues to login5 + spclient).</summary>
    public static async Task<LoginResult?> LoginAsync(Action<string> log, CancellationToken ct, bool retainChannel = false)
    {
        // Step 1 - PORTABLE credential store. DPAPI on Windows, Keychain/libsecret on macOS/Linux, else NoOp - same seam.
        ICredentialProtector protector = new NoOpProtector();
        if (OperatingSystem.IsWindows()) protector = new DpapiProtector();
        else if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && KeyringProtector.IsAvailable()) protector = new KeyringProtector();
        var localStore = FileLocalStore.ForApp("Wavee");
        var credStore = new LocalCredentialStore(localStore, protector);
        var deviceId = GetOrCreateDeviceId(localStore);   // persisted → stable across launches (don't churn the device list)

        try
        {
            var device = new DeviceCodeProvider(new HttpClientPost(), ClientId, Scopes);
            var flow = new AuthFlow([new StoredCredentialProvider(() => credStore.Load()), device]);
            using var challengeSub = flow.State.Subscribe(new ChallengeLogger(log));
            log("Authenticating (stored credentials first, else device-code)...");
            var cred = await flow.AcquireAsync(ct).ConfigureAwait(false);
            if (cred is null) { log("No credential obtained."); return null; }
            bool usedStored = cred.Kind == CredentialKind.ReusableBlob;
            log(usedStored ? "Using stored credentials (no re-auth)." : "Authorized via device-code.");

            log("Resolving Spotify access points...");
            var json = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=accesspoint", ct).ConfigureAwait(false);
            var aps = ApResolver.ParseAccessPoints(json);
            if (aps.Count == 0) { log("No access points returned."); return null; }
            // :4070 endpoints first, then the rest — FAIL OVER to the next AP on a connection error / timeout / TryAnotherAP.
            var ordered = aps.Where(a => a.EndsWith(":4070")).Concat(aps.Where(a => !a.EndsWith(":4070"))).ToList();

            SpotifyWelcome? welcome = null;
            ApConnection? channel = null;
            foreach (var apHostPort in ordered)
            {
                var parts = apHostPort.Split(':');
                string host = parts[0];
                int port = int.Parse(parts[1]);
                log("Connecting to " + host + ":" + port + "...");
                TcpDuplexStream? tcp = null;
                bool keep = false;
                try
                {
                    // retainChannel: keep THIS socket alive as the one persistent AP channel (login + audio-key share it —
                    // no second handshake). Otherwise the socket is disposed after login (the original probe/gate behavior).
                    tcp = await TcpDuplexStream.ConnectAsync(host, port, ct, retainChannel ? ApConnection.IdleReadTimeout : null).ConfigureAwait(false);
                    if (retainChannel)
                    {
                        var (w, codec) = await SpotifyConnection.HandshakeRetainAsync(tcp, cred, deviceId, ct).ConfigureAwait(false);
                        welcome = w;
                        channel = ApConnection.Adopt(tcp, codec, log);   // the login socket becomes the persistent AP channel
                        keep = true;
                    }
                    else
                    {
                        welcome = await SpotifyConnection.HandshakeAndLoginAsync(tcp, cred, deviceId, ct).ConfigureAwait(false);
                    }
                    break;   // logged in
                }
                catch (SpotifyAuthRejectedException ex)
                {
                    // A genuine credential rejection is FINAL (don't try other APs). Clear stored creds → a re-run re-auths.
                    if (usedStored) { credStore.Clear(); log("Stored credentials were rejected - cleared them. Re-run to authorize fresh."); }
                    else log("Login rejected by Spotify: " + ex.Message);
                    return null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // user cancel → stop
                catch (Exception ex) { log("  " + host + " failed (" + ex.Message + ") - trying the next access point."); }   // TryAnotherAP / connection / timeout
                finally { if (!keep) tcp?.Dispose(); }   // dispose on failure / non-retain (matches the old `using`)
            }
            if (welcome is null) { log("All access points failed."); return null; }

            log("Logged in to Spotify (user " + Redact(welcome.Username) + ", product " + (welcome.Product?.Type ?? "unknown") + ", country " + (welcome.Country ?? "?") + ").");

            // Persist the fresh reusable credentials (bytes never logged, only the count) — login5 + the next launch use them.
            credStore.Save(new Credential(CredentialKind.ReusableBlob, welcome.Username, Convert.ToBase64String(welcome.ReusableCredentials)));
            log("Saved " + welcome.ReusableCredentials.Length + "-byte reusable credentials [" + protector.Scheme + "].");
            return new LoginResult(welcome, deviceId, credStore, protector.Scheme, channel);
        }
        catch (OperationCanceledException) { log("Live login timed out / cancelled."); return null; }
        catch (Exception ex) { log("Live login failed: " + ex.Message); return null; }
    }

    static string GetOrCreateDeviceId(ILocalStore store)
    {
        var id = store.Get("device.id");
        if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N"); store.Set("device.id", id); }
        return id;
    }

    // Redact an account identifier before it reaches a log (keep a short hint, hide the rest).
    static string Redact(string s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 6 ? "***" : s[..3] + "***" + s[^2..];

    sealed class ChallengeLogger(Action<string> log) : IObserver<AuthState>
    {
        bool _shown;
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(AuthState s)
        {
            if (s.Phase == AuthPhase.ChallengeExpired) { log("The device code expired before approval. Re-run to get a fresh code."); return; }
            if (_shown || s.Phase != AuthPhase.AwaitingUser || s.Challenge is not { } c) return;
            _shown = true;
            log("Authorize Wavee on Spotify (Premium account):");
            log("  Open: " + (c.VerificationUriComplete ?? c.VerificationUri));
            log("  or visit " + c.VerificationUri + " and enter code: " + c.UserCode);
            log("Waiting for approval...");
        }
    }
}
