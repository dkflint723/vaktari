using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A folder moved between folders is followed, not held (review finding 11).
///
/// **The move goes item by item**, recreating the tree and moving each file,
/// so nothing of Vaktari's can block it (rename notes, plan §0.3) — and a move
/// can run for hours, so holding every tab inside the source detached for the
/// length of it would be the wrong trade. The tabs inside only need to know
/// where it went, once it has landed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MoveFollowTests
{
    private sealed class Recording : IFolderHandover
    {
        public int Released;
        public List<(string From, string To)> Followed { get; } = [];

        public ValueTask<IFolderLease> ReleaseAsync(IReadOnlyList<string> folders, CancellationToken ct)
        {
            Interlocked.Increment(ref Released);
            throw new InvalidOperationException("a move must not hold anything");
        }

        void IFolderHandover.Followed(string from, string to)
        {
            lock (Followed) Followed.Add((from, to));
        }
    }

    [WindowsFact]
    public async Task Moving_a_folder_follows_it_without_holding_it()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("photos");
        tree.Write(Path.Combine("photos", "a.jpg"));
        var into = tree.Dir("archive");

        var handover = new Recording();
        var ops = new WindowsFileOperations { Handover = handover };

        var handle = ops.Move([folder], into, _ => ValueTask.FromResult(ConflictResolution.Skip));
        await handle.Completion;

        Assert.Empty(handle.Problems);
        Assert.True(File.Exists(tree.At("archive", "photos", "a.jpg")));
        Assert.Equal(0, handover.Released);

        var followed = Assert.Single(handover.Followed);
        Assert.True(PathRules.Same(folder, followed.From));
        Assert.True(PathRules.Same(tree.At("archive", "photos"), followed.To));
    }

    /// <summary>A file moved is not a folder anybody is inside.</summary>
    [WindowsFact]
    public async Task Moving_a_file_follows_nothing()
    {
        using var tree = new TempTree();
        var file = tree.Write("a.txt");
        var into = tree.Dir("archive");

        var handover = new Recording();
        var handle = new WindowsFileOperations { Handover = handover }
            .Move([file], into, _ => ValueTask.FromResult(ConflictResolution.Skip));
        await handle.Completion;

        Assert.Empty(handover.Followed);
    }
}
