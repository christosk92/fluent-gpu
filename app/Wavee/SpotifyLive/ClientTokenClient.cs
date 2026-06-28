using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Protocol.ClientToken;

namespace Wavee.SpotifyLive;

// Obtains + caches the Spotify client-token (required on every spclient request — extended-metadata 403s without it).
// Lifted faithfully from the desktop wire: POST a CLIENT_DATA request to clienttoken.spotify.com; if the server answers
// with a hashcash CHALLENGE, solve it (HashcashSolver) and re-submit; cache the granted token until ~5 min before refresh.
public sealed class ClientTokenClient
{
    const string Endpoint = "https://clienttoken.spotify.com/v1/clienttoken";
    const int MaxChallengeRetries = 3;

    readonly string _clientId;
    readonly string _deviceId;
    readonly SemaphoreSlim _lock = new(1, 1);
    string? _cached;
    DateTimeOffset _expiresAt;

    public ClientTokenClient(string clientId, string deviceId)
    {
        _clientId = clientId;
        _deviceId = deviceId;
    }

    /// <summary>A valid client-token, fetching/refreshing as needed. Returns null on failure (the caller proceeds without).</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5)) return _cached;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5)) return _cached;
            return await FetchAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;   // best-effort — a missing client-token surfaces downstream as a 403 the caller can report
        }
        finally { _lock.Release(); }
    }

    async Task<string?> FetchAsync(CancellationToken ct)
    {
        var request = BuildInitial();
        for (int attempt = 0; attempt < MaxChallengeRetries; attempt++)
        {
            var response = await SendAsync(request, ct).ConfigureAwait(false);
            switch (response.ResponseType)
            {
                case ClientTokenResponseType.ResponseGrantedTokenResponse:
                    var g = response.GrantedToken;
                    _cached = g.Token;
                    _expiresAt = DateTimeOffset.UtcNow.AddSeconds(g.RefreshAfterSeconds > 0 ? g.RefreshAfterSeconds : 7200);
                    return _cached;
                case ClientTokenResponseType.ResponseChallengesResponse:
                    request = SolveChallenge(response.Challenges);
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    ClientTokenRequest BuildInitial() => new()
    {
        RequestType = ClientTokenRequestType.RequestClientDataRequest,
        ClientData = new ClientDataRequest
        {
            ClientVersion = SpotifyHeaders.ClientVersion,
            ClientId = _clientId,
            ConnectivitySdkData = BuildConnectivity(),
        },
    };

    ConnectivitySdkData BuildConnectivity()
    {
        var data = new ConnectivitySdkData { DeviceId = _deviceId, PlatformSpecificData = new PlatformSpecificData() };
        if (OperatingSystem.IsWindows())
            data.PlatformSpecificData.DesktopWindows = new NativeDesktopWindowsData
            {
                OsVersion = 10,
                OsBuild = Environment.OSVersion.Version.Build,
                PlatformId = 2,
                UnknownValue5 = 9,
                UnknownValue6 = 9,
                PeMachine = PeMachine(),
            };
        else if (OperatingSystem.IsLinux())
            data.PlatformSpecificData.DesktopLinux = new NativeDesktopLinuxData
            {
                SystemName = "Linux",
                SystemRelease = Environment.OSVersion.Version.ToString(),
                SystemVersion = "#1",
                Hardware = "x86_64",
            };
        else if (OperatingSystem.IsMacOS())
            data.PlatformSpecificData.DesktopMacos = new NativeDesktopMacOSData
            {
                SystemVersion = Environment.OSVersion.Version.ToString(),
                HwModel = "MacBookPro",
                CompiledCpuType = "0",
            };
        return data;
    }

    // PE machine constant for the current architecture (System.Reflection.PortableExecutable.Machine values).
    static int PeMachine() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => 332,
        Architecture.X64 => 34404,
        Architecture.Arm => 452,
        Architecture.Arm64 => 43620,
        _ => 34404,
    };

    static ClientTokenRequest SolveChallenge(ChallengesResponse challenges)
    {
        var request = new ClientTokenRequest
        {
            RequestType = ClientTokenRequestType.RequestChallengeAnswersRequest,
            ChallengeAnswers = new ChallengeAnswersRequest { State = challenges.State },
        };
        foreach (var challenge in challenges.Challenges)
        {
            if (challenge.Type == ChallengeType.ChallengeHashCash && challenge.EvaluateHashcashParameters is { } hp)
            {
                // client-token hashcash: context is empty; the answer suffix is UPPERCASE hex (login5 differs — raw bytes).
                var suffix = HashcashSolver.Solve([], Convert.FromHexString(hp.Prefix), hp.Length);
                request.ChallengeAnswers.Answers.Add(new ChallengeAnswer
                {
                    ChallengeType = ChallengeType.ChallengeHashCash,
                    HashCash = new HashCashAnswer { Suffix = Convert.ToHexString(suffix) },
                });
            }
        }
        return request;
    }

    static async Task<ClientTokenResponse> SendAsync(ClientTokenRequest request, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(request.ToByteArray()) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        using var resp = await SharedHttp.Client.SendAsync(msg, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ClientTokenResponse.Parser.ParseFrom(bytes);
    }
}
