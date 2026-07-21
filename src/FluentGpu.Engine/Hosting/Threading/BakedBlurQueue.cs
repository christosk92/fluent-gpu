using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;

namespace FluentGpu.Hosting.Threading;

/// <summary>Portable UI-to-render handoff for one-shot static image blurs. The cache owns handles and generations;
/// the backend owns GPU resources. A completion is posted only after the persistent output texture has been registered
/// under <see cref="Job.Id"/>, so the next UI pump can publish Ready without racing residency.</summary>
public sealed class BakedBlurQueue
{
    public enum Quality : byte { High = 0, Economy = 1, Minimal = 2 }

    public readonly record struct Job(int Id, int SourceId, int OutputW, int OutputH, float SigmaTexels, int Generation,
                                      bool IsUpgrade = false, Quality Quality = Quality.High);
    public readonly record struct Result(int Id, int Generation, bool Ok, int W, int H,
                                         Quality Quality = Quality.High, bool IsUpgrade = false);

    private readonly record struct QueuedJob(Job Job, long EligibleTicks);
    private readonly record struct WorkKey(int Id, int Generation, bool IsUpgrade);

    private readonly ConcurrentQueue<QueuedJob> _initialJobs = new();
    private readonly ConcurrentQueue<QueuedJob> _upgradeJobs = new();
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly ConcurrentDictionary<WorkKey, byte> _queued = new();
    private readonly ConcurrentDictionary<int, int> _latestGeneration = new();
    private Action? _completionWake;
    private int _initialJobCount, _upgradeJobCount, _resultCount;
    private long _nextJobTicks;
    private int _quality = (int)Quality.Economy;
    private int _fastGpuStreak;
    private static readonly long s_upgradeDelayTicks = (long)(0.35 * Stopwatch.Frequency);
    private static readonly bool s_diag = Environment.GetEnvironmentVariable("FG_BAKED_BLUR_DIAG") == "1";

    public volatile bool Paused;
    public int InitialJobCount => Volatile.Read(ref _initialJobCount);
    public int UpgradeJobCount => Volatile.Read(ref _upgradeJobCount);
    public int JobCount => InitialJobCount + UpgradeJobCount;
    public bool HasJobs => JobCount > 0;
    public bool HasResults => Volatile.Read(ref _resultCount) > 0;
    public bool HasPending => HasJobs || HasResults;
    public bool HasRunnableJob
    {
        get
        {
            if (Paused || Stopwatch.GetTimestamp() < Volatile.Read(ref _nextJobTicks)) return false;
            if (InitialJobCount > 0) return true;
            return _upgradeJobs.TryPeek(out var q) && Stopwatch.GetTimestamp() >= q.EligibleTicks;
        }
    }
    public Quality AdaptiveQuality => (Quality)Volatile.Read(ref _quality);

    public void SetCompletionWake(Action? wake) => Volatile.Write(ref _completionWake, wake);

    public void Enqueue(in Job job)
    {
        int id = job.Id, generation = job.Generation;
        int latest = _latestGeneration.AddOrUpdate(id, generation,
            (_, prior) => Math.Max(prior, generation));
        if (job.Generation < latest) return;
        var key = new WorkKey(job.Id, job.Generation, job.IsUpgrade);
        if (!_queued.TryAdd(key, 0)) return;

        if (job.IsUpgrade)
        {
            _upgradeJobs.Enqueue(new QueuedJob(job with { Quality = Quality.High },
                Stopwatch.GetTimestamp() + s_upgradeDelayTicks));
            Interlocked.Increment(ref _upgradeJobCount);
        }
        else
        {
            _initialJobs.Enqueue(new QueuedJob(job, 0));
            Interlocked.Increment(ref _initialJobCount);
        }
    }

    /// <summary>Invalidate queued or in-flight work for an entry whose cache generation advanced.</summary>
    public void Invalidate(int id, int generation)
        => _latestGeneration.AddOrUpdate(id, generation, (_, prior) => Math.Max(prior, generation));

    /// <summary>Raw deterministic dequeue used by headless/tests. Real GPU backends use
    /// <see cref="TryDequeueRunnableJob(out Job)"/> so cadence and adaptive sizing are enforced.</summary>
    public bool TryDequeueJob(out Job job)
    {
        if (TryTake(_initialJobs, ref _initialJobCount, ignoreEligibility: true, out job)) return true;
        return TryTake(_upgradeJobs, ref _upgradeJobCount, ignoreEligibility: true, out job);
    }

    /// <summary>Take at most one job every 33 ms. Initial jobs may use an adaptive provisional size; a deferred upgrade
    /// runs only after the initial backlog drains, and always restores the originally requested quality.</summary>
    public bool TryDequeueRunnableJob(out Job job)
    {
        job = default;
        if (!HasRunnableJob) return false;

        if (!TryTake(_initialJobs, ref _initialJobCount, ignoreEligibility: true, out var requested))
        {
            if (!TryTake(_upgradeJobs, ref _upgradeJobCount, ignoreEligibility: false, out requested)) return false;
            job = requested with { Quality = Quality.High };
            Volatile.Write(ref _nextJobTicks, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30);
            PublishJobDiag(in job, Quality.High, JobCount + 1);
            return true;
        }

        int backlog = InitialJobCount + 1;
        Quality quality = AdaptiveQuality;
        if (backlog >= 6) quality = Quality.Minimal;
        else if (backlog >= 3 && quality < Quality.Economy) quality = Quality.Economy;
        float factor = quality switch { Quality.High => 1f, Quality.Economy => 0.5f, _ => 0.25f };
        job = requested with
        {
            OutputW = Math.Max(1, (int)MathF.Ceiling(requested.OutputW * factor)),
            OutputH = Math.Max(1, (int)MathF.Ceiling(requested.OutputH * factor)),
            SigmaTexels = MathF.Max(0.25f, requested.SigmaTexels * factor),
            Quality = quality,
        };
        Volatile.Write(ref _nextJobTicks, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30);
        PublishJobDiag(in job, quality, backlog);
        return true;
    }

    private void PublishJobDiag(in Job job, Quality quality, int backlog)
    {
        Diag.Set("d3d12", "bakedBlurTier", (int)quality);
        Diag.Set("d3d12", "bakedBlurWidth", job.OutputW);
        Diag.Set("d3d12", "bakedBlurHeight", job.OutputH);
        if (s_diag) Console.Error.WriteLine($"[baked-blur] tier={quality} upgrade={job.IsUpgrade} backlog={backlog} actual={job.OutputW}x{job.OutputH}");
    }

    private bool TryTake(ConcurrentQueue<QueuedJob> queue, ref int count, bool ignoreEligibility, out Job job)
    {
        while (queue.TryPeek(out var head))
        {
            if (!ignoreEligibility && Stopwatch.GetTimestamp() < head.EligibleTicks) break;
            if (!queue.TryDequeue(out head)) continue;
            Interlocked.Decrement(ref count);
            _queued.TryRemove(new WorkKey(head.Job.Id, head.Job.Generation, head.Job.IsUpgrade), out _);
            if (_latestGeneration.TryGetValue(head.Job.Id, out int latest) && latest != head.Job.Generation)
                continue;
            job = head.Job;
            return true;
        }
        job = default;
        return false;
    }

    /// <summary>Delay until queued work can actually run, used by the host to sleep through the upgrade grace period.</summary>
    public int RecommendedWaitMs
    {
        get
        {
            if (!HasJobs) return -1;
            if (Paused) return 33;
            long due = Volatile.Read(ref _nextJobTicks);
            if (InitialJobCount == 0 && _upgradeJobs.TryPeek(out var upgrade) && upgrade.EligibleTicks > due)
                due = upgrade.EligibleTicks;
            long remaining = due - Stopwatch.GetTimestamp();
            if (remaining <= 0) return 0;
            return Math.Clamp((int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency), 1, 500);
        }
    }

    public void ReportRecordTime(double milliseconds)
    {
        Diag.Set("d3d12", "bakedBlurRecordUs", (int)Math.Round(milliseconds * 1000.0));
    }

    public void ReportGpuTime(double milliseconds)
    {
        Diag.Set("d3d12", "bakedBlurGpuUs", (int)Math.Round(milliseconds * 1000.0));
        Quality q = AdaptiveQuality;
        if (milliseconds > 2.0)
        {
            Volatile.Write(ref _quality, Math.Min((int)Quality.Minimal, (int)q + 1));
            _fastGpuStreak = 0;
        }
        else if (milliseconds < 0.75 && JobCount < 3)
        {
            if (++_fastGpuStreak >= 8)
            {
                Volatile.Write(ref _quality, Math.Max((int)Quality.High, (int)q - 1));
                _fastGpuStreak = 0;
            }
        }
        else _fastGpuStreak = 0;
        if (s_diag) Console.Error.WriteLine($"[baked-blur] gpu={milliseconds:0.000}ms next={AdaptiveQuality}");
    }

    public void Post(in Result result)
    {
        _results.Enqueue(result);
        Interlocked.Increment(ref _resultCount);
        Volatile.Read(ref _completionWake)?.Invoke();
    }

    public bool TryDequeueResult(out Result result)
    {
        if (!_results.TryDequeue(out result)) return false;
        Interlocked.Decrement(ref _resultCount);
        return true;
    }

    /// <summary>Whether a dequeued job still targets the live cache generation. Backends check immediately before
    /// replacing the resident texture, closing the common queued-stale race.</summary>
    public bool IsCurrent(in Job job)
        => !_latestGeneration.TryGetValue(job.Id, out int latest) || latest == job.Generation;
}
