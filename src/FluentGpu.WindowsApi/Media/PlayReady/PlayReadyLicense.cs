using System;
using System.Net.Http;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Builds a generic PlayReady license-acquisition relay (the spec §9.2 <c>WithDrm</c> callback): POST the CDM challenge
/// to an entered license server, with an optional custom auth header (e.g. Axinom's <c>X-AxDRM-Message</c>), and return
/// the license blob. The engine never sees a key; a shortfall (non-2xx, network error) surfaces as a typed
/// <see cref="MediaError"/> upstream (the CDM leaves the key unusable → a <see cref="MediaErrorCategory.Drm"/> error),
/// never a silent success. This is the app-side generalization of the previously hardcoded Axinom relay.
/// </summary>
public static class PlayReadyLicense
{
    private static readonly HttpClient s_http = new();
    private const string AcquireLicenseSoapAction =
        "\"http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense\"";

    /// <summary>Create a relay that POSTs the challenge to <paramref name="licenseServerUrl"/> with an optional custom
    /// header. Uses a shared <see cref="HttpClient"/>.</summary>
    public static Func<LicenseRequest, ValueTask<LicenseResponse>> HttpRelay(
        string licenseServerUrl, string? headerName = null, string? headerValue = null)
        => HttpRelay(licenseServerUrl, headerName, headerValue, s_http);

    /// <summary>Create a relay over an explicit <see cref="HttpClient"/> (testable).</summary>
    public static Func<LicenseRequest, ValueTask<LicenseResponse>> HttpRelay(
        string licenseServerUrl, string? headerName, string? headerValue, HttpClient http)
    {
        if (string.IsNullOrWhiteSpace(licenseServerUrl))
            throw new ArgumentException("A license server URL is required.", nameof(licenseServerUrl));

        return async request =>
        {
            using var content = new ByteArrayContent(request.Challenge.ToArray());
            content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
            using var msg = new HttpRequestMessage(HttpMethod.Post, licenseServerUrl) { Content = content };
            msg.Headers.TryAddWithoutValidation("SOAPAction", AcquireLicenseSoapAction);
            if (!string.IsNullOrWhiteSpace(headerName) && headerValue is not null)
                msg.Headers.TryAddWithoutValidation(headerName, headerValue);

            using var resp = await http.SendAsync(msg).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();   // non-2xx → throws → DrmLicenseBridge maps to a typed DRM error
            byte[] license = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return new LicenseResponse(license);
        };
    }
}
