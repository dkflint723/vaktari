using BenchmarkDotNet.Attributes;
using Vaktari.Core.FileSystem;

namespace Vaktari.Benchmarks;

/// <summary>
/// The tree walk behind folder sizes, recursive permissions and the Windows
/// search, over a tree built for the purpose: a hundred folders of a hundred
/// files, made once, deleted at the end.
///
/// Cold and warm are not separated here — the first iteration warms the
/// directory cache and BenchmarkDotNet's warm-up discards it — so this is the
/// warm figure, which is the one the walk pays on the second look at a folder.
/// </summary>
[MemoryDiagnoser]
public class WalkBenchmarks
{
    private string _root = "";

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "vaktari-bench-walk-" + Guid.NewGuid().ToString("N")[..8]);

        for (var folder = 0; folder < 100; folder++)
        {
            var path = Path.Combine(_root, $"folder{folder:D3}");
            Directory.CreateDirectory(path);

            for (var file = 0; file < 100; file++)
                File.WriteAllText(Path.Combine(path, $"file{file:D3}.txt"), "x");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Benchmark]
    public int Descend() => SafeWalk.Descend(_root).Count();
}
