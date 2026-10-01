using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// A fact that needs the thread pool to be able to give it a worker: one
/// driven by a System.Threading.Timer, whose callback runs on a pool worker,
/// or one that hands work to the pool on purpose, as a provider's event can.
///
/// **Skipped, and shown as skipped, where the pool can never have more than
/// three workers** (batch-0.11.2e QA, round 8, with
/// DOTNET_ThreadPool_ForceMaxWorkerThreads=3). With Core's classes running in
/// parallel and holding workers, a timer's callback there may never get one
/// at all, and every such test failed on "the retry timer never ticked" —
/// even run alone. That is a property of the pool, not of the code under
/// test: the product's own timers are as dependent on the pool, and a real
/// pool has no such ceiling. Everything these tests drive themselves runs on
/// threads of its own (OwnThread), and they pass with the pool capped at six
/// workers and the processor count at four.
/// </summary>
public sealed class PoolWorkersFactAttribute : FactAttribute
{
    public const int Fewest = 4;

    public PoolWorkersFactAttribute()
    {
        ThreadPool.GetMaxThreads(out var workers, out _);

        if (workers < Fewest)
            Skip = $"The thread pool is capped at {workers} workers, fewer than {Fewest}: a timer's callback may never run.";
    }
}
