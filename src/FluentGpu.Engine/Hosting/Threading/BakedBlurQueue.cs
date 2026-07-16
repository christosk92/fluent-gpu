using System.Collections.Concurrent;

namespace FluentGpu.Hosting.Threading;

/// <summary>Portable UI-to-render handoff for one-shot static image blurs. The cache owns handles and generations;
/// the backend owns GPU resources. A completion is posted only after the persistent output texture has been registered
/// under <see cref="Job.Id"/>, so the next UI pump can publish Ready without racing residency.</summary>
public sealed class BakedBlurQueue
{
    public readonly record struct Job(int Id, int SourceId, int OutputW, int OutputH, float SigmaTexels, int Generation);
    public readonly record struct Result(int Id, int Generation, bool Ok, int W, int H);

    private readonly ConcurrentQueue<Job> _jobs = new();
    private readonly ConcurrentQueue<Result> _results = new();

    public volatile bool Paused;
    public bool HasPending => !_jobs.IsEmpty || !_results.IsEmpty;
    public void Enqueue(in Job job) => _jobs.Enqueue(job);
    public bool TryDequeueJob(out Job job) => _jobs.TryDequeue(out job);
    public void Post(in Result result) => _results.Enqueue(result);
    public bool TryDequeueResult(out Result result) => _results.TryDequeue(out result);
}
