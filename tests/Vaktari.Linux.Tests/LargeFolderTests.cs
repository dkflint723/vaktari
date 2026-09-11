using System.Diagnostics;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The large-folder budget through the Linux provider: the first batch of a
/// listing on screen, and the whole of it read.
///
/// **No measurement existed.** The listing streams in batches so that the
/// first screenful appears before the folder is read, and nothing asserted
/// that it does — a provider that quietly materialised the folder first would
/// have passed every test and shown a blank pane for seconds on a real
/// folder of this size. Nightly, and only when <c>VAKTARI_LARGE_FIXTURE</c>
/// names a size; see the Core twin for why a stopwatch is not a merge gate.
/// </summary>
public sealed class LargeFolderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-large-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [LargeFixtureFact]
    public async Task The_first_batch_arrives_long_before_the_folder_is_read()
    {
        var count = LargeFixture.Count;

        Directory.CreateDirectory(_root);

        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(_root, $"file{i:D6}.dat"), []);

        var provider = new LinuxFileSystemProvider();
        var watch = Stopwatch.StartNew();
        TimeSpan? first = null;
        var seen = 0;

        await foreach (var batch in provider.EnumerateAsync(_root, new ListingOptions(), CancellationToken.None))
        {
            first ??= watch.Elapsed;
            seen += batch.Count;
        }

        watch.Stop();

        Assert.Equal(count, seen);
        Assert.NotNull(first);
        Assert.True(first < TimeSpan.FromMilliseconds(300),
            $"the first batch of {count:N0} entries took {first.Value.TotalMilliseconds:N0} ms; the budget is 300 ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"reading {count:N0} entries took {watch.Elapsed.TotalSeconds:N1} s; the budget is 10 s");
    }
}
