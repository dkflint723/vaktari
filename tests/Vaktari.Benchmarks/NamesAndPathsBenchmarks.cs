using BenchmarkDotNet.Attributes;
using Vaktari.Core.FileSystem;

namespace Vaktari.Benchmarks;

/// <summary>
/// The rules a name and a path go through on their way to the screen: the
/// name check every rename and every new file pays, and the path comparison
/// every watcher event and every selection match pays.
/// </summary>
[MemoryDiagnoser]
public class NamesAndPathsBenchmarks
{
    private string[] _names = [];
    private (string, string)[] _pairs = [];

    [GlobalSetup]
    public void Setup()
    {
        var realistic = Names.Realistic(10_000);

        // One in ten carries a fault the rules refuse: a trailing dot, a
        // reserved device name, a slash — so the refusal path is measured too.
        _names = realistic
            .Select((n, i) => (i % 10) switch
            {
                0 => n + ".",
                5 => "con." + n,
                7 => n.Replace('_', '/'),
                _ => n,
            })
            .ToArray();

        var root = Path.Combine(Path.GetTempPath(), "vaktari-bench");

        _pairs = realistic
            .Take(5_000)
            .Select(n => (Path.Combine(root, n), Path.Combine(root, n.ToUpperInvariant())))
            .ToArray();
    }

    [Benchmark]
    public int Refusals()
    {
        var refused = 0;

        foreach (var name in _names)
            if (FileNames.Refuse(name) is not null) refused++;

        return refused;
    }

    [Benchmark]
    public int SamePath()
    {
        var same = 0;

        foreach (var (a, b) in _pairs)
            if (PathRules.Same(a, b)) same++;

        return same;
    }

    [Benchmark]
    public int Normalised()
    {
        var length = 0;

        foreach (var (a, _) in _pairs)
            length += PathRules.Normalise(a).Length;

        return length;
    }
}
