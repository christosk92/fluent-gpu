using System.Collections.Concurrent;
using FluentGpu.Scene;

namespace FluentGpu.Hosting.Threading;

/// <summary>Producer→consumer handoff for image GPU work under the ASYNC render thread (render-thread-seam landing plan
/// §9, Step 1). Every threaded host uses it, including force-sync because independent compositor turns can run between
/// UI drains. Single-thread hosts retain direct device sinks. The UI thread (<see cref="ImageCache.Pump"/>) PRODUCES upload/evict jobs — an upload
/// transfers an owned <c>ArrayPool&lt;byte&gt;</c> buffer (the pump copies the transient decode pixels into it) — and the
/// render thread CONSUMES them inside its submit, immediately before <c>FlushUploads</c> opens the frame's command list.
/// The transferred buffer is rented from the host's bounded <see cref="BufferPool"/> (a <c>PixelBufferPool</c>); the
/// render thread returns it through <see cref="ReturnUploadBuffer"/> after <c>Stage</c> copies it (ArrayPool.Shared
/// fallback when no host pool is set).
/// That makes <c>ImageTextureStore</c>'s Stage/Free/FlushUploads have exactly ONE toucher (the render thread) → safe by
/// confinement, no lock. Upload-before-referencing-submit ordering is preserved (drain + FlushUploads precede any draw).
///
/// (Historically that buffer was a bare <c>ArrayPool&lt;byte&gt;.Shared</c> rental; it is now a <see cref="BufferPool"/>
/// rental with Shared fallback — the transfer/ownership contract is unchanged.)
///
/// Admission is <b>+1-frame async</b> (matching the existing +1-frame decode-latency contract): an upload is optimistically
/// admitted <c>Ready</c> on the UI thread; the render thread stages it and, ONLY on rejection (atlas/pool exhaustion),
/// posts the result back through <see cref="TryDequeueResult"/>, which the next <c>Pump</c> folds into the
/// Failed/GpuResourceExhausted transition + byte-accounting undo. An accepted upload posts nothing (the optimistic Ready
/// stands), so the reject ring is near-always empty. SPSC by construction (one UI producer, one render consumer);
/// <see cref="ConcurrentQueue{T}"/> is adequate and near-zero-alloc at the bounded per-frame apply rate.</summary>
public sealed class ImageUploadQueue
{
    /// <summary>One unit of render-thread image work. <see cref="Evict"/> ⇒ free id (Buffer null); else stage
    /// <c>Buffer[0..ByteLen]</c> into the resident texture for <see cref="Id"/> at <see cref="W"/>×<see cref="H"/>.</summary>
    public struct Job { public int Id; public byte[]? Buffer; public int W, H, ByteLen; public bool Evict; }

    private readonly ConcurrentQueue<Job> _jobs = new();                                    // UI → render
    // Render-owned. Each registered consumer retains its last adopted image snapshot until replacement.
    // A queued eviction cannot invalidate a texture still referenced by that consumer's scene.
    private readonly Dictionary<object, ImageRecordingSnapshot> _sceneReaders = new();
    private readonly HashSet<int> _deferredEvictions = new();
    private readonly List<int> _evictionScratch = new();
    private readonly Queue<Job> _releasedEvictions = new();

    internal void SetSceneReader(object owner, ImageRecordingSnapshot snapshot)
    {
        _sceneReaders[owner] = snapshot;
        ReleaseUnreferencedEvictions();
    }

    internal void RemoveSceneReader(object owner)
    {
        _sceneReaders.Remove(owner);
        ReleaseUnreferencedEvictions();
    }

    private bool RetainedByScene(int id)
    {
        foreach (var reader in _sceneReaders.Values)
            if (reader.Retains(id)) return true;
        return false;
    }

    private void ReleaseUnreferencedEvictions()
    {
        _evictionScratch.Clear();
        foreach (int id in _deferredEvictions)
            if (!RetainedByScene(id)) _evictionScratch.Add(id);
        foreach (int id in _evictionScratch)
        {
            _deferredEvictions.Remove(id);
            _releasedEvictions.Enqueue(new Job { Id = id, Evict = true });
        }
    }
    private readonly ConcurrentQueue<(int Id, ImageUploadResult Result)> _rejects = new();  // render → UI (rejections only)

    /// <summary>The host's bounded CPU pixel pool that upload buffers are rented from (by the UI sink) and returned to
    /// (by the render thread via <see cref="ReturnUploadBuffer"/>). Null ⇒ fall back to <c>ArrayPool&lt;byte&gt;.Shared</c>.
    /// The <c>AppHost.PixelPool</c> setter re-points this so decode + upload share ONE retained-bytes budget.</summary>
    public FluentGpu.Media.PixelBufferPool? BufferPool { get; set; }

    /// <summary>UI thread: hand a decoded upload to the render thread, transferring ownership of <paramref name="buffer"/>
    /// (rented from <see cref="BufferPool"/>, or <c>ArrayPool&lt;byte&gt;.Shared</c> when none is set); the render thread
    /// returns it via <see cref="ReturnUploadBuffer"/> after <c>Stage</c> copies it.</summary>
    public void EnqueueUpload(int id, byte[] buffer, int w, int h, int byteLen)
        => _jobs.Enqueue(new Job { Id = id, Buffer = buffer, W = w, H = h, ByteLen = byteLen, Evict = false });

    /// <summary>Render thread: give a drained job's buffer back after <c>Stage</c> copied it. Routes to the host's bounded
    /// pool — the device leaf never knows the pool type. Null pool falls back to ArrayPool.Shared.</summary>
    public void ReturnUploadBuffer(byte[] buffer)
    {
        var pool = BufferPool;
        if (pool is not null) pool.Return(buffer);
        else System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>UI thread: hand an eviction to the render thread (frees the GPU texture there, fence-deferred).</summary>
    public void EnqueueEvict(int id) => _jobs.Enqueue(new Job { Id = id, Evict = true });

    /// <summary>Render thread: drain one queued job (returns false when empty).</summary>
    public bool TryDequeueJob(out Job job)
    {
        while (_releasedEvictions.TryDequeue(out job))
        {
            // Another target may have adopted the image after its prior reader released it.
            if (!RetainedByScene(job.Id)) return true;
            _deferredEvictions.Add(job.Id);
        }
        while (_jobs.TryDequeue(out job))
        {
            if (job.Evict && RetainedByScene(job.Id))
            {
                _deferredEvictions.Add(job.Id);
                continue;
            }
            if (!job.Evict) _deferredEvictions.Remove(job.Id); // a newer upload supersedes an older queued eviction
            return true;
        }
        return false;
    }

    /// <summary>Render thread: report an upload REJECTION back to the UI (accepted uploads post nothing).</summary>
    public void PostReject(int id, ImageUploadResult result) => _rejects.Enqueue((id, result));

    /// <summary>UI thread: drain one posted rejection (returns false when empty).</summary>
    public bool TryDequeueResult(out (int Id, ImageUploadResult Result) reject) => _rejects.TryDequeue(out reject);
}
