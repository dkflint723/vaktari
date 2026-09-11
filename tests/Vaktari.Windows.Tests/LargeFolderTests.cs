using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The large-folder budget through the Windows provider — the Windows half
/// of what the Linux tests of the same name pin: the first batch of a listing
/// on screen long before the folder is read, and the whole of it within a
/// generous bound. Nightly, and only when <c>VAKTARI_LARGE_FIXTURE</c> names
/// a size.
/// </summary>
[SupportedOSPlatform("windows")]
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

        var provider = new WindowsFileSystemProvider();
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
