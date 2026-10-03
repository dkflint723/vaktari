using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The Linux engine's half of the folder hand-over.
///
/// **Nothing of Vaktari's can block a rename here** — Linux renames a folder
/// whatever has it open — but the tabs inside it were left on a folder that
/// had gone. The engine tells the application the same things the Windows one
/// does, through the one FolderMoves both share, so the tabs follow.
///
/// Like the other Linux operation tests, these run on any platform: the code
/// under test is path arithmetic and ordinary file I/O.
/// </summary>
public sealed class HandoverEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-handover-" + Guid.NewGuid().ToString("N")[..8]);

    public HandoverEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private sealed class Recording : IFolderHandover
    {
        public List<string> Released { get; } = [];
        public List<(string From, string To)> Moved { get; } = [];
        public List<(string From, string To)> Followed { get; } = [];
        public int Ended;

        public ValueTask<IFolderLease> ReleaseAsync(IReadOnlyList<string> folders, CancellationToken ct)
        {
            lock (Released) Released.AddRange(folders);
            return ValueTask.FromResult<IFolderLease>(new Lease(this));
        }

        void IFolderHandover.Followed(string from, string to)
        {
            lock (Followed) Followed.Add((from, to));
        }

        private sealed class Lease(Recording owner) : IFolderLease
        {
            public void Moved(string from, string to)
            {
                lock (owner.Moved) owner.Moved.Add((from, to));
            }

            public void Gone(string folder) { }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Ended);
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Renaming_a_folder_hands_it_over_and_says_where_it_went()
    {
        var folder = Directory.CreateDirectory(At("photos")).FullName;
        var handover = new Recording();

        await new LinuxFileOperations { Handover = handover }.RenameAsync(folder, "pictures", CancellationToken.None);

        Assert.Contains(handover.Released, f => PathRules.Same(f, folder));

        var moved = Assert.Single(handover.Moved);
        Assert.True(PathRules.Same(folder, moved.From));
        Assert.True(PathRules.Same(At("pictures"), moved.To));
        Assert.Equal(1, handover.Ended);
    }

    /// <summary>A file is nothing anybody is inside: it is not handed over.</summary>
    [Fact]
    public async Task Renaming_a_file_hands_nothing_over()
    {
        var file = At("a.txt");
        File.WriteAllText(file, "a");
        var handover = new Recording();

        await new LinuxFileOperations { Handover = handover }.RenameAsync(file, "b.txt", CancellationToken.None);

        Assert.Empty(handover.Released);
        Assert.Empty(handover.Moved);
    }

    /// <summary>An undone folder rename is followed back.</summary>
    [Fact]
    public async Task Undoing_a_folder_rename_is_followed_back()
    {
        var folder = Directory.CreateDirectory(At("photos")).FullName;
        var handover = new Recording();
        var ops = new LinuxFileOperations();

        await ops.RenameAsync(folder, "pictures", CancellationToken.None);

        ops.Handover = handover;
        await ops.UndoAsync(CancellationToken.None);

        Assert.True(Directory.Exists(folder));

        var moved = Assert.Single(handover.Moved);
        Assert.True(PathRules.Same(At("pictures"), moved.From));
        Assert.True(PathRules.Same(folder, moved.To));
    }

    /// <summary>
    /// **An undo refused because the old name is taken is not put back**
    /// (rename QA, note 1): it would meet the same refusal on every press and
    /// wall off the step beneath it. Only something having it open is put
    /// back, and nothing of that kind is refused on Linux.
    /// </summary>
    [Fact]
    public async Task An_undo_refused_because_the_old_name_is_taken_does_not_wedge_the_history()
    {
        var file = At("older.txt");
        File.WriteAllText(file, "x");
        var folder = Directory.CreateDirectory(At("photos")).FullName;
        var ops = new LinuxFileOperations();

        await ops.RenameAsync(file, "older2.txt", CancellationToken.None);
        var beneath = ops.UndoDescription;

        await ops.RenameAsync(folder, "pictures", CancellationToken.None);

        Directory.CreateDirectory(folder);

        await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));

        Assert.Equal(beneath, ops.UndoDescription);
    }

    /// <summary>A move is followed once it has landed, and never held.</summary>
    [Fact]
    public async Task Moving_a_folder_follows_it_without_holding_it()
    {
        var folder = Directory.CreateDirectory(At("photos")).FullName;
        File.WriteAllText(At("photos", "a.jpg"), "a");
        var into = Directory.CreateDirectory(At("archive")).FullName;
        var handover = new Recording();

        var handle = new LinuxFileOperations { Handover = handover }
            .Move([folder], into, _ => ValueTask.FromResult(ConflictResolution.Skip));
        await handle.Completion;

        Assert.Empty(handle.Problems);
        Assert.Empty(handover.Released);

        var followed = Assert.Single(handover.Followed);
        Assert.True(PathRules.Same(folder, followed.From));
        Assert.True(PathRules.Same(At("archive", "photos"), followed.To));
    }
}
