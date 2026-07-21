using System;
using System.Runtime.CompilerServices;
using FluentGpu.Media;

namespace FluentGpu.Hooks;

/// <summary>Backs <see cref="RenderContext.UseMediaPlayer"/>: a component-lifetime-owned <see cref="MediaPlayer"/> created
/// once at mount and ASYNC-disposed (fire-and-forget, engine-ordered) on unmount — no manual IAsyncDisposable race pushed
/// to the app (spec §12 guarantee 1).</summary>
internal sealed class MediaPlayerCell : HookCell, IDisposableCell
{
    public MediaPlayer Player = null!;
    public void DisposeCell()
    {
        // Disposal is always safe at any state (spec §12); fire-and-forget the async teardown — no sleep, no finalizer.
        try { _ = Player.DisposeAsync(); } catch { /* teardown never throws to the reconciler */ }
    }
}

public sealed partial class RenderContext
{
    /// <summary>A <see cref="MediaPlayer"/> whose lifetime is bound to this component; auto-disposed (async) on unmount
    /// by the reconciler (spec §4.1). <paramref name="configure"/> runs ONCE at mount against the Layer-2 builder (network,
    /// buffering, ABR, DRM relay, backend registrations). Reuses the same instance across re-renders.</summary>
    public MediaPlayer UseMediaPlayer(Action<MediaPlayerBuilder>? configure = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        MediaPlayerCell cell;
        if (idx < 0)
        {
            var builder = MediaPlayer.Build();
            configure?.Invoke(builder);
            cell = new MediaPlayerCell { Player = builder.Build() };
            RegisterCell(__k, cell, cleanupCapable: true);
        }
        else cell = (MediaPlayerCell)_cells[idx];
        return cell.Player;
    }

    /// <summary>A <see cref="MediaPlayer"/> already pointed at a source that RE-LOADS when <paramref name="source"/> (a
    /// thunk over the reactive inputs it reads) yields a different <see cref="MediaSource"/> value (spec §4.1). Auto
    /// SMTC/buffering/default-track selection; auto-disposed on unmount.</summary>
    public MediaPlayer UseVideo(Func<MediaSource> source)
    {
        var player = UseMediaPlayer();
        var last = UseRef<MediaSource?>(null);
        var next = source();
        if (!Equals(last.Value, next))   // MediaSource is a record ⇒ value equality; re-load only on a real change
        {
            last.Value = next;
            _ = player.Play(next);
        }
        return player;
    }
}
