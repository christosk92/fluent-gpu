using System.IO;

namespace FluentGpu.Localization;

/// <summary>
/// Dev-only hot-reload: a <see cref="FileSystemWatcher"/> over the localization JSON directory that reloads the folder
/// and bumps the culture epoch whenever a translator saves a file — so on-screen text updates LIVE as the JSON is edited
/// (the same reactive path a <see cref="Localization.SetCulture"/> takes: no re-render, just the bound text nodes
/// re-resolve). This is a debugging/translation-authoring aid, gated behind an explicit opt-in (it should never run in a
/// shipping build — a release product loads strings once).
///
/// <para><b>Threading.</b> <see cref="FileSystemWatcher"/> raises events on a thread-pool thread; writing the
/// reactive culture epoch off the UI thread is illegal. So the watcher does NOT touch the reactive core directly —
/// it marshals the reload through the host's UI-thread poster (the <see cref="System.Action{T}"/> the host wires from
/// <c>HostDispatch.Post</c>), exactly as off-thread OS callbacks do elsewhere in the engine. When no poster is provided
/// (headless/test), the reload runs inline. A short debounce coalesces the multiple change events an editor save emits.</para>
/// </summary>
public sealed class LocalizationWatcher : System.IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _dir;
    private readonly System.Action<System.Action> _post;
    private readonly object _gate = new();
    private System.Threading.Timer? _debounce;
    private bool _disposed;

    /// <summary>Watch <paramref name="jsonDir"/> for <c>*.json</c> changes and reload on save. <paramref name="post"/> is
    /// the host UI-thread poster (<c>HostDispatch.Post</c>); pass <see langword="null"/> in a headless context to reload
    /// inline on the watcher thread.</summary>
    public LocalizationWatcher(string jsonDir, System.Action<System.Action>? post = null)
    {
        _dir = jsonDir;
        _post = post ?? (a => a());
        _watcher = new FileSystemWatcher(jsonDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = false,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    /// <summary>Begin watching (call once the host poster is available).</summary>
    public void Start() => _watcher.EnableRaisingEvents = true;

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: an editor save fires several Changed events in a burst; reload once ~150 ms after the last.
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new System.Threading.Timer(_ => Reload(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debounce.Change(150, System.Threading.Timeout.Infinite);
        }
    }

    private void Reload()
    {
        // Marshal onto the UI thread (or inline when host-less) — Localization.LoadFolder bumps the reactive epoch,
        // which must happen on the UI thread.
        _post(() => Localization.LoadFolder(_dir));
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _debounce?.Dispose(); }
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
