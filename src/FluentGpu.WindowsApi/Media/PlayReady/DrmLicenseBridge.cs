using System;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>The outcome of one license acquisition: EITHER a license blob OR a typed <see cref="MediaError"/> (never
/// both, never neither). A DRM shortfall is <see cref="MediaErrorCategory.Drm"/> — the engine never silently proceeds
/// without a key.</summary>
public sealed class LicenseOutcome
{
    private LicenseOutcome(byte[]? license, MediaError? error) { License = license; Error = error; }

    /// <summary>The license bytes to hand the CDM's <c>Update()</c> (null on failure).</summary>
    public byte[]? License { get; }
    /// <summary>The typed error on failure (null on success).</summary>
    public MediaError? Error { get; }
    /// <summary>True IFF a non-empty license was produced.</summary>
    public bool Success => License is { Length: > 0 } && Error is null;

    /// <summary>A success carrying <paramref name="license"/>.</summary>
    public static LicenseOutcome Ok(byte[] license) => new(license, null);
    /// <summary>A failure carrying <paramref name="error"/>.</summary>
    public static LicenseOutcome Fail(MediaError error) => new(null, error);
}

/// <summary>
/// The managed side of the M5 DRM relay (spec §9.2), extracted so it is testable WITHOUT a native CDM / P/Invoke. The
/// native CDM raises a KeyMessage (a challenge) on an MF thread; <see cref="Resolve"/> runs the app's async license relay
/// on a worker and blocks ONLY that native thread for a bounded <see cref="TimeSpan"/>, then returns the license bytes.
/// A relay that throws, times out, or returns an empty license becomes a <see cref="MediaErrorCategory.Drm"/> error with
/// <see cref="MediaRecovery.NeedsLicense"/> — never a silent success. The engine never sees a key here.
/// </summary>
public sealed class DrmLicenseBridge
{
    private readonly Func<LicenseRequest, ValueTask<LicenseResponse>>? _relay;
    private readonly DrmSystem _system;
    private readonly TimeSpan _timeout;
    private readonly MediaLocus _locus;
    private MediaError? _lastError;

    /// <summary>Create a bridge over <paramref name="relay"/> for <paramref name="system"/>, bounded by
    /// <paramref name="timeout"/>. A null relay always fails (there is no license source).</summary>
    public DrmLicenseBridge(Func<LicenseRequest, ValueTask<LicenseResponse>>? relay, DrmSystem system,
                            TimeSpan timeout, MediaLocus locus = default)
    {
        _relay = relay;
        _system = system;
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : timeout;
        _locus = locus;
    }

    /// <summary>The last error recorded by a failed <see cref="Resolve"/> (for the session to surface as the typed error).</summary>
    public MediaError? LastError => _lastError;

    /// <summary>Resolve a license for <paramref name="challenge"/> (a managed COPY of the native challenge bytes). Blocks
    /// the caller up to the configured timeout while the async relay runs on a worker; the app relay never blocks the CDM
    /// thread indefinitely. Pure managed logic — unit-testable with a fake relay, no CDM required.</summary>
    public LicenseOutcome Resolve(ReadOnlyMemory<byte> challenge, string? keyId)
    {
        if (_relay is null)
            return Record(new MediaError(MediaErrorCategory.Drm,
                "No DRM license relay is configured for the protected source (call WithDrm).", null, _locus, MediaRecovery.NeedsLicense));

        var request = new LicenseRequest(_system, challenge, keyId, _locus);
        try
        {
            // Run the app relay on a worker; block THIS (native CDM) thread only, with a hard bound.
            var task = Task.Run(() => _relay(request).AsTask());
            if (!task.Wait(_timeout))
                return Record(new MediaError(MediaErrorCategory.Drm,
                    $"The DRM license relay timed out after {_timeout.TotalSeconds:0.#}s.", null, _locus, MediaRecovery.NeedsLicense));

            LicenseResponse response = task.GetAwaiter().GetResult();   // rethrows a faulted relay synchronously
            byte[] bytes = response.License.ToArray();
            if (bytes.Length == 0)
                return Record(new MediaError(MediaErrorCategory.Drm,
                    "The DRM license relay returned an empty license.", null, _locus, MediaRecovery.NeedsLicense));

            _lastError = null;
            return LicenseOutcome.Ok(bytes);
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException agg && agg.InnerException is { } i ? i : ex;
            return Record(new MediaError(MediaErrorCategory.Drm,
                "The DRM license relay failed: " + inner.Message, null, _locus, MediaRecovery.NeedsLicense));
        }
    }

    private LicenseOutcome Record(MediaError error) { _lastError = error; return LicenseOutcome.Fail(error); }
}
