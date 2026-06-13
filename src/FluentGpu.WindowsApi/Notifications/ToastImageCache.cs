using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Packaging;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// Resolves an <c>http(s)://</c> toast-image URL into a local source an UNPACKAGED app can actually display. Unpackaged
/// apps cannot use web images in toasts — the Shell silently drops an <c>http(s)://</c> <c>&lt;image src&gt;</c>; only
/// packaged apps with the <c>internet</c> capability may pass the URL straight through (Microsoft Learn,
/// <see href="https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/send-local-toast-other-apps">Send a local toast from other unpackaged apps</see>;
/// docs/plans/windowsapi-implementation-research.md §2.1, "the unpackaged web-image trap"). This cache downloads the
/// image once to local app data and hands back a local reference, keyed by a hash of the URL so repeated album art
/// (WAVEE's case) is fetched at most once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Packaged passthrough.</b> When <see cref="PackageIdentity.IsPackaged"/> is true the URL is returned unchanged
/// (a packaged WAVEE with <c>internetClient</c> uses <c>https://i.scdn.co/…</c> directly — the single cleanest argument
/// for choosing MSIX). Only an unpackaged process pays the download.
/// </para>
/// <para>
/// <b>Reference scheme.</b> For an unpackaged process the downloaded file is referenced as a <c>file:///</c> URI, which
/// the Shell resolves for a classic Win32 app. (The doc notes <c>ms-appdata:///local/…</c> additionally survives Action
/// Center persistence after the app exits, but <c>ms-appdata</c> only resolves for an identity-bearing process; a
/// sparse-identity build that reports <see cref="PackageIdentity.IsPackaged"/> takes the passthrough branch and never
/// reaches here.) A non-web source (already <c>file:///</c>, <c>ms-appdata:///</c>, <c>ms-appx:///</c>, or a bare local
/// path) is returned unchanged.
/// </para>
/// <para>
/// <b>Location.</b> Files land in <c>%LOCALAPPDATA%\{appFolder}\toastimg\{sha256(url)}{ext}</c>. The folder is created
/// on demand; a previously-downloaded file is reused without re-fetching. Cold path — synchronous download is fine, but
/// an async overload is provided for callers already on an async path (e.g. building a "Now Playing" toast off the audio
/// thread). Failures are non-fatal: a download error returns the original URL (the toast then simply shows no image
/// rather than failing to show).
/// </para>
/// </remarks>
public sealed class ToastImageCache
{
    private static readonly Lazy<HttpClient> s_http = new(() => new HttpClient());

    private readonly string _cacheDir;

    /// <summary>The process-wide default cache, rooted at <c>%LOCALAPPDATA%\FluentGpu\toastimg</c>.</summary>
    public static ToastImageCache Default { get; } = new("FluentGpu");

    /// <summary>Create a cache under <c>%LOCALAPPDATA%\{appFolder}\toastimg</c>.</summary>
    /// <param name="appFolder">The per-app subfolder of <c>%LOCALAPPDATA%</c> (e.g. <c>"WAVEE"</c>).</param>
    public ToastImageCache(string appFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(appFolder);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDir = System.IO.Path.Combine(localAppData, appFolder, "toastimg");
    }

    /// <summary>
    /// Resolve <paramref name="imageUrl"/> to a toast-usable source. Packaged: returns it unchanged. Unpackaged + a web
    /// URL: downloads it (or reuses the cached file) and returns a <c>file:///</c> URI. Any non-web source is returned
    /// unchanged. On a download failure the original URL is returned (the toast shows no image rather than not showing).
    /// </summary>
    public string Localize(string imageUrl)
    {
        if (!ShouldLocalize(imageUrl, out string ext))
            return imageUrl;

        try
        {
            string path = CachePathFor(imageUrl, ext);
            if (!System.IO.File.Exists(path))
            {
                EnsureDir();
                byte[] bytes = s_http.Value.GetByteArrayAsync(imageUrl).GetAwaiter().GetResult();
                System.IO.File.WriteAllBytes(path, bytes);
            }
            return ToFileUri(path);
        }
        catch
        {
            return imageUrl;   // best-effort: a failed localize degrades to "no image", never to "no toast".
        }
    }

    /// <summary>Async form of <see cref="Localize(string)"/> for callers already on an async path.</summary>
    public async Task<string> LocalizeAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        if (!ShouldLocalize(imageUrl, out string ext))
            return imageUrl;

        try
        {
            string path = CachePathFor(imageUrl, ext);
            if (!System.IO.File.Exists(path))
            {
                EnsureDir();
                byte[] bytes = await s_http.Value.GetByteArrayAsync(imageUrl, cancellationToken).ConfigureAwait(false);
                await System.IO.File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            return ToFileUri(path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return imageUrl;
        }
    }

    /// <summary>Delete all cached images (e.g. on sign-out). Best-effort; ignores a missing directory.</summary>
    public void Clear()
    {
        try
        {
            if (System.IO.Directory.Exists(_cacheDir))
                System.IO.Directory.Delete(_cacheDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Decide whether <paramref name="imageUrl"/> needs localizing: only an unpackaged process, only an
    /// absolute <c>http</c>/<c>https</c> URL. <paramref name="ext"/> is the file extension to persist with (from the URL
    /// path, defaulting to <c>.img</c>).</summary>
    private static bool ShouldLocalize(string imageUrl, out string ext)
    {
        ext = ".img";
        if (string.IsNullOrEmpty(imageUrl))
            return false;
        if (PackageIdentity.IsPackaged)
            return false;   // packaged: the Shell fetches http(s) itself (with the internet capability).
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;   // file:///, ms-appdata:///, ms-appx:///, bare paths: already local.

        string urlExt = System.IO.Path.GetExtension(uri.AbsolutePath);
        if (!string.IsNullOrEmpty(urlExt) && urlExt.Length <= 5)
            ext = urlExt;
        return true;
    }

    private string CachePathFor(string imageUrl, string ext)
    {
        // SHA-256 of the URL → a stable, collision-free, filesystem-safe file name (album art repeats; same URL reuses
        // the same file). Lowercase hex, truncated is unnecessary — full digest keeps it unambiguous.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl));
        string name = Convert.ToHexStringLower(hash);
        return System.IO.Path.Combine(_cacheDir, name + ext);
    }

    private void EnsureDir() => System.IO.Directory.CreateDirectory(_cacheDir);

    private static string ToFileUri(string path) => new Uri(path).AbsoluteUri;   // "file:///C:/.../{hash}.jpg"
}
