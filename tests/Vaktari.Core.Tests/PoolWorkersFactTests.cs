using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// <see cref="PoolWorkersFactAttribute"/> skips its tests only where the pool
/// is capped (batch-0.11.2f QA, round 9). A skip is shown, but nothing fails
/// on it: making the attribute skip everywhere left all twelve of its tests
/// skipped and every suite green.
/// </summary>
public sealed class PoolWorkersFactTests
{
    /// <summary>
    /// **With the pool as the runtime sets it, the attribute skips nothing.**
    /// Only a run that caps the pool on purpose — the one setting that does is
    /// DOTNET_ThreadPool_ForceMaxWorkerThreads — may see these tests skipped.
    /// </summary>
    [Fact]
    public void An_uncapped_pool_runs_the_pool_workers_facts()
    {
        // A run that caps the pool on purpose (xunit 2 has no runtime skip).
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ThreadPool_ForceMaxWorkerThreads"))) return;

        ThreadPool.GetMaxThreads(out var workers, out _);

        Assert.True(workers >= PoolWorkersFactAttribute.Fewest, $"the pool is capped at {workers} workers with nothing asking for it");
        Assert.Null(new PoolWorkersFactAttribute().Skip);
    }
}
