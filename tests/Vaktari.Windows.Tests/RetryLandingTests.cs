using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A retry asks where each item will land, as the first run did.** The
/// landing rule (ReachablePath.RefuseLanding) is asked of every planned target
/// before a byte moves, "on a retry too, whose plan can reach a folder the
/// first run could not read" — but every landing test started a first run, so
/// asking it of the first run alone reddened nothing (fix-7 round-2
/// verification). A retry re-plans its folders from what is in them NOW: a
/// folder whose first attempt failed can hold a name by the time Retry is
/// pressed that a plainly spelled destination would fold onto another.
///
/// The first run copies a folder, reached through "\\?\", into a plain
/// destination where a FILE stands at the folder's name, answered Overwrite:
/// the folder cannot be made, so it is offered back for a retry. Before the
/// retry the file is taken away and the source gains "x..." beside its own
/// "x". The retry must refuse the whole of it, naming "x...", and land
/// nothing. All in a temporary folder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RetryLandingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-retrylanding").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Extended(_root), "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.Delete(Extended(_root), recursive: true);
    }

    private static string Extended(string path) => @"\\?\" + path;

    [WindowsTheory]
    [InlineData("x...", false)]
    [InlineData("x...", true)]
    [InlineData("x ", false)]
    public async Task A_retry_asks_where_each_item_will_land(string late, bool move)
    {
        var album = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(album, "a.txt"), "a");
        File.WriteAllText(Path.Combine(album, "x"), "the source's own x");
        File.WriteAllText(Path.Combine(dst, "album"), "a file standing where the folder goes");

        var ops = new WindowsFileOperations();

        var first = move
            ? ops.Move([Extended(album)], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite))
            : ops.Copy([Extended(album)], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite));
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(first.Retry is not null, $"the first run offered no retry: {first.State} {first.Error?.Message}");

        File.Delete(Path.Combine(dst, "album"));
        File.WriteAllText(Extended(Path.Combine(album, late)), "arrived before the retry");

        var again = first.Retry!.Again();
        await again.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationState.Failed, again.State);
        Assert.Equal(ReachablePath.RefuseLanding(Path.Combine(dst, "album", late)), again.Error?.Message);
        Assert.False(Directory.Exists(Extended(Path.Combine(dst, "album"))), "the retry landed something");
        Assert.Equal(3, Directory.GetFiles(Extended(album)).Length);
        Assert.Equal("the source's own x", File.ReadAllText(Path.Combine(album, "x")));
    }
}
