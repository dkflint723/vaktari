using BenchmarkDotNet.Attributes;
using Vaktari.Core;

namespace Vaktari.Benchmarks;

/// <summary>
/// The natural sort against the ordinal one, over a folder's worth of names.
///
/// This is the comparison every listing pays once per row per resort, and the
/// one the pane re-runs on every sort, group and filter change over the whole
/// list — so the 200,000 case is the "large folder" budget the nightly fixture
/// asserts against, measured here rather than asserted.
/// </summary>
[MemoryDiagnoser]
public class SortingBenchmarks
{
    [Params(10_000, 200_000)]
    public int Count { get; set; }

    private string[] _names = [];

    [GlobalSetup]
    public void Setup() => _names = Names.Realistic(Count);

    [Benchmark(Baseline = true)]
    public int Ordinal()
    {
        var copy = (string[])_names.Clone();
        Array.Sort(copy, StringComparer.Ordinal);
        return copy.Length;
    }

    [Benchmark]
    public int Natural()
    {
        var copy = (string[])_names.Clone();
        Array.Sort(copy, static (a, b) => NaturalOrder.Compare(a, b));
        return copy.Length;
    }
}
