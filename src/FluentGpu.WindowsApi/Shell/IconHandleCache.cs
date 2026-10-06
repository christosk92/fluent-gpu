using System;
using System.Collections.Generic;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// A small, bounded cache of loaded <c>HICON</c>s keyed by (file path, pixel size), so a glyph that is applied again —
/// the taskbar overlay badge and the thumbnail-toolbar buttons on every play/pause — is read from disk once per size
/// instead of once per call. The cache OWNS every handle it returns: callers must never destroy one. Pure over its two
/// delegates (load, destroy), so the ownership and eviction rules are a unit test without Win32.
/// </summary>
/// <remarks>
/// <para><b>Size.</b> The caller passes the size the load would resolve to (for <c>LR_DEFAULTSIZE</c> that is
/// <c>SM_CXICON</c>/<c>SM_CYICON</c>). When that size changes (a DPI change that moves the system icon metric), every
/// entry of another size is destroyed, so a stale-resolution handle is never handed out and never leaks.</para>
/// <para><b>Bound.</b> At most <see cref="Capacity"/> handles are held; past that the least-recently-used one is
/// destroyed. A handle the shell already received is safe to destroy: <c>ITaskbarList3</c> copies the icon it is
/// given (the same assumption the uncached code made when it destroyed a replaced thumb icon).</para>
/// <para>Not thread-safe; <see cref="TaskbarManager"/> calls it under its own lock.</para>
/// </remarks>
internal sealed class IconHandleCache
{
    /// <summary>The most handles held at once.</summary>
    public const int Capacity = 16;

    private readonly Func<string, int, int, nint> _load;
    private readonly Action<nint> _destroy;
    private readonly List<Entry> _entries = new(Capacity);   // least-recently-used first
    private int _cx = -1, _cy = -1;

    private readonly record struct Entry(string Path, int Cx, int Cy, nint Handle);

    /// <param name="load">Loads <c>(path, cx, cy)</c> and returns the handle, or 0 on failure.</param>
    /// <param name="destroy">Destroys a handle this cache loaded.</param>
    public IconHandleCache(Func<string, int, int, nint> load, Action<nint> destroy)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
    }

    /// <summary>Handles currently held.</summary>
    public int Count => _entries.Count;

    /// <summary>Loads performed (a hit performs none).</summary>
    public int Loads { get; private set; }

    /// <summary>The cached handle for <paramref name="path"/> at <paramref name="cx"/>×<paramref name="cy"/>, loading
    /// it on a miss. Returns 0 when the load fails (nothing is cached then, so a later call retries).</summary>
    public nint Get(string path, int cx, int cy)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (cx != _cx || cy != _cy)
        {
            // The resolution moved: no handle of the old size may be returned again.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Cx == cx && _entries[i].Cy == cy) continue;
                _destroy(_entries[i].Handle);
                _entries.RemoveAt(i);
            }
            _cx = cx;
            _cy = cy;
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            if (e.Cx != cx || e.Cy != cy || !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            if (i != _entries.Count - 1)
            {
                _entries.RemoveAt(i);
                _entries.Add(e);
            }
            return e.Handle;
        }

        nint handle = _load(path, cx, cy);
        Loads++;
        if (handle == 0) return 0;
        if (_entries.Count == Capacity)
        {
            _destroy(_entries[0].Handle);
            _entries.RemoveAt(0);
        }
        _entries.Add(new Entry(path, cx, cy, handle));
        return handle;
    }

    /// <summary>Destroy every held handle (shutdown, or a caller that knows the icons changed on disk).</summary>
    public void Clear()
    {
        for (int i = 0; i < _entries.Count; i++) _destroy(_entries[i].Handle);
        _entries.Clear();
    }
}
