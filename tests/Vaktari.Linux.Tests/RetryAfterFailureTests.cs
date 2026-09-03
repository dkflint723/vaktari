using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Going again on the items that could not be done — the freedesktop twin of
/// the Windows tests, over the same recipe.
///
/// **Skip and Cancel already worked; only Retry was missing.** The batch
/// carries on past a failure and names what it left behind, and cancel has been
/// on the bar for the whole run. The verb with nothing behind it was the one
/// that needs the person to go and DO something first — which is exactly the
/// one a modal cannot express.
///
/// A file sitting where a folder must go is the blocker throughout, because it
/// fails the same way on both platforms: ENOTDIR on Unix, ERROR_PATH_NOT_FOUND
/// on Windows, and both land inside the engine's existing filter. Holding a
/// file open, which the Windows tests use, is not portable.
/// </summary>
public sealed class RetryAfterFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-retry-" + Guid.NewGuid().ToString("N")[..8]);

    public RetryAfterFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Write(string relative, string text)
    {
        var path = At(relative.Split('/'));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);

        return path;
    }

    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    /// <summary>
    /// The whole feature: the obstruction is removed, and the retry lands what
    /// the first pass could not.
    /// </summary>
    [Fact]
    public async Task Clearing_the_way_and_retrying_copies_the_folder()
    {
        Write("src/A/first.txt", "one");
        Directory.CreateDirectory(At("dst"));

        // Not a folder, so the create refuses over it.
        File.WriteAllText(At("dst", "A"), "in the way");

        var ops = new LinuxFileOperations();

        var handle = ops.Copy(
            [At("src", "A")], At("dst"), Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.NotEmpty(handle.Problems);
        Assert.NotNull(handle.Retry);

        File.Delete(At("dst", "A"));

        var second = handle.Retry!.Again();
        await second.Completion;

        Assert.Equal("one", File.ReadAllText(At("dst", "A", "first.txt")));
    }

    /// <summary>
    /// A clean run offers nothing. The button is ABSENT rather than present and
    /// doing nothing.
    /// </summary>
    [Fact]
    public async Task A_clean_run_offers_nothing_to_go_again_on()
    {
        Write("src/a.txt", "a");
        Directory.CreateDirectory(At("dst"));

        var ops = new LinuxFileOperations();
        var handle = ops.Copy([At("src")], At("dst"), Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Null(handle.Retry);
    }

    /// <summary>
    /// Keep both renames the arriving folder and leaves the one already there
    /// alone — the property a retry's carried target has to preserve.
    ///
    /// **This does not itself exercise a retry**, and is not named as if it
    /// does: nothing portable makes a file copy fail here, so the retry-target
    /// regression is pinned on the Windows side, where a file can be held open.
    /// This is the half that can be checked on both.
    /// </summary>
    [Fact]
    public async Task Keep_both_renames_the_arrival_and_leaves_the_original_alone()
    {
        Write("src/A/first.txt", "one");
        Write("dst/A/theirs.txt", "theirs");

        var ops = new LinuxFileOperations();

        var handle = ops.Copy(
            [At("src", "A")], At("dst"), Always(ConflictResolution.KeepBoth));

        await handle.Completion;

        Assert.True(Directory.Exists(At("dst", "A (1)")) || Directory.Exists(At("dst", "A (2)")),
                    "keep both did not rename the arriving folder");

        Assert.Equal(
            ["theirs.txt"],
            Directory.EnumerateFiles(At("dst", "A")).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// A delete that could not be done goes again on the same path.
    ///
    /// The blocker is a FILE standing in for a directory, deliberately: on Unix
    /// deleting a path whose directory is absent succeeds silently — ENOENT is
    /// a no-op there — so the "missing directory" case records no problem at
    /// all and would leave this with nothing to retry.
    /// </summary>
    [Fact]
    public async Task A_delete_that_failed_goes_on_the_second_try()
    {
        File.WriteAllText(At("blocker"), "not a folder");

        var ops = new LinuxFileOperations();
        var handle = ops.Delete([At("blocker", "x.txt")]);

        await handle.Completion;

        Assert.Single(handle.Problems);
        Assert.NotNull(handle.Retry);

        // The way is cleared and the file really made.
        File.Delete(At("blocker"));
        Write("blocker/x.txt", "x");

        var second = handle.Retry!.Again();
        await second.Completion;

        Assert.False(File.Exists(At("blocker", "x.txt")));
        Assert.Empty(second.Problems);
    }
}
