using System.Collections.Generic;
using FluentGpu.WindowsApi.Shell;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The taskbar's icon cache (<see cref="IconHandleCache"/>): a glyph applied on every play/pause is loaded once per size,
/// the cache owns and eventually destroys every handle it loaded exactly once, and it never grows past its bound. Fake
/// load/destroy delegates; no Win32.
/// </summary>
public sealed class IconHandleCacheTests
{
    private sealed class Fake
    {
        public readonly List<(string Path, int Cx, int Cy)> Loaded = new();
        public readonly List<nint> Destroyed = new();
        private nint _next = 100;
        public bool Fail;

        public IconHandleCache Cache() => new(
            (p, cx, cy) => { Loaded.Add((p, cx, cy)); return Fail ? 0 : _next++; },
            h => Destroyed.Add(h));
    }

    [Fact]
    public void The_same_icon_at_the_same_size_is_loaded_once()
    {
        var f = new Fake();
        var cache = f.Cache();
        nint a = cache.Get(@"C:\app\assets\taskbar\pause.ico", 32, 32);
        for (int i = 0; i < 20; i++)
            Assert.Equal(a, cache.Get(@"C:\app\assets\taskbar\pause.ico", 32, 32));
        Assert.Single(f.Loaded);
        Assert.Empty(f.Destroyed);                 // the cache owns it; nothing is freed while it is in use
    }

    [Fact]
    public void Paths_compare_case_insensitively_like_the_file_system()
    {
        var f = new Fake();
        var cache = f.Cache();
        Assert.Equal(cache.Get(@"C:\A\play.ico", 32, 32), cache.Get(@"c:\a\PLAY.ico", 32, 32));
        Assert.Single(f.Loaded);
    }

    [Fact]
    public void A_size_change_destroys_every_handle_of_the_old_size_and_reloads()
    {
        var f = new Fake();
        var cache = f.Cache();
        nint play = cache.Get("play.ico", 32, 32);
        nint pause = cache.Get("pause.ico", 32, 32);

        nint play48 = cache.Get("play.ico", 48, 48);   // the system icon metric moved (a DPI change)
        Assert.NotEqual(play, play48);
        Assert.Equal(new[] { pause, play }, f.Destroyed.ToArray());   // both old-size handles, each once
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void The_cache_is_bounded_and_evicts_the_least_recently_used_handle()
    {
        var f = new Fake();
        var cache = f.Cache();
        nint first = cache.Get("icon0.ico", 32, 32);
        nint second = cache.Get("icon1.ico", 32, 32);
        for (int i = 2; i < IconHandleCache.Capacity; i++) cache.Get("icon" + i + ".ico", 32, 32);
        cache.Get("icon0.ico", 32, 32);              // touch: icon1 is now the least recently used

        cache.Get("one-more.ico", 32, 32);
        Assert.Equal(IconHandleCache.Capacity, cache.Count);
        Assert.Equal(new[] { second }, f.Destroyed.ToArray());
        Assert.Equal(first, cache.Get("icon0.ico", 32, 32));   // still cached, no reload
        Assert.Equal(IconHandleCache.Capacity + 1, f.Loaded.Count);
    }

    [Fact]
    public void A_failed_load_returns_zero_and_is_not_cached_so_a_later_call_retries()
    {
        var f = new Fake { Fail = true };
        var cache = f.Cache();
        Assert.Equal(0, cache.Get("missing.ico", 32, 32));
        Assert.Equal(0, cache.Count);
        f.Fail = false;
        Assert.NotEqual(0, cache.Get("missing.ico", 32, 32));
        Assert.Equal(2, f.Loaded.Count);
    }

    [Fact]
    public void Clear_destroys_every_held_handle_exactly_once()
    {
        var f = new Fake();
        var cache = f.Cache();
        nint a = cache.Get("a.ico", 32, 32), b = cache.Get("b.ico", 32, 32);
        cache.Clear();
        Assert.Equal(new[] { a, b }, f.Destroyed.ToArray());
        Assert.Equal(0, cache.Count);
        cache.Clear();
        Assert.Equal(2, f.Destroyed.Count);
        Assert.NotEqual(a, cache.Get("a.ico", 32, 32));   // reloads after a clear
    }
}
