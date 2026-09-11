using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What a copy leaves behind when it does not finish — the Linux half of the
/// promise the Windows tests of the same name pin.
///
/// The copy wrote straight into the target, opened Create, and deleted it on
/// failure; an Overwrite truncated the original before a byte of the
/// replacement had been read. It lands under a staging name beside the target
/// now and is renamed over it once whole, which rename(2) makes atomic on one
/// filesystem.
///
/// Pinned from INSIDE the copy: the test waits for the first progress report,
/// reads the original while the copy is still running, and only then cancels.
/// Cancelling before the copy began was measured proving nothing — the
/// operation loop bails out before it opens a file, old code and new alike.
/// </summary>
public sealed class AtomicCopyTests : IDisposable
{
    private const int BigEnough = 64 << 20;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-atomiccopy-" + Guid.NewGuid().ToString("N"));

    private readonly string _from;
    private readonly string _into;

    public AtomicCopyTests()
    {
        _from = Path.Combine(_root, "from");
        _into = Path.Combine(_root, "into");
        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_into);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private static ValueTask<ConflictResolution> Answer(ConflictResolution how)
        => ValueTask.FromResult(how);

    private static async Task Ended(IOperationHandle handle)
    {
        try { await handle.Completion; }
        catch (OperationCanceledException) { /* the point */ }
    }

    /// <summary>A copy that has written at least one buffer, and is still going.</summary>
    private static async Task<IOperationHandle> Writing(IOperationHandle handle)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handle.Progressed += (_, p) => { if (p.BytesDone > 0) started.TrySetResult(); };

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task The_original_is_untouched_while_its_replacement_is_being_written()
    {
        var source = Path.Combine(_from, "report.odt");
        File.WriteAllBytes(source, new byte[BigEnough]);
        var original = Write(Path.Combine(_into, "report.odt"), "the original, in full");

        var ops = new LinuxFileOperations();
        var handle = await Writing(ops.Copy([source], _into, _ => Answer(ConflictResolution.Overwrite)));

        // Mid-copy. Under the old code this read an empty, truncated file.
        Assert.Equal("the original, in full", File.ReadAllText(original));

        handle.Cancel();
        await Ended(handle);

        Assert.Equal("the original, in full", File.ReadAllText(original));
        Assert.Empty(Directory.GetFiles(_into, ".*.vaktari-*"));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_nothing_under_the_final_name_and_no_staging_file()
    {
        var source = Path.Combine(_from, "big.bin");
        File.WriteAllBytes(source, new byte[BigEnough]);

        var ops = new LinuxFileOperations();
        var handle = await Writing(ops.Copy([source], _into, _ => Answer(ConflictResolution.Overwrite)));

        Assert.False(File.Exists(Path.Combine(_into, "big.bin")), "a partial file sits under the real name");

        handle.Cancel();
        await Ended(handle);

        Assert.False(File.Exists(Path.Combine(_into, "big.bin")), "a partial file sits under the real name");
        Assert.Empty(Directory.GetFiles(_into, ".*.vaktari-*"));
    }

    [Fact]
    public async Task A_finished_overwrite_lands_the_new_bytes_and_nothing_else()
    {
        var source = Write(Path.Combine(_from, "report.odt"), "the replacement");
        Write(Path.Combine(_into, "report.odt"), "the original");

        var ops = new LinuxFileOperations();
        var handle = ops.Copy([source], _into, _ => Answer(ConflictResolution.Overwrite));
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("the replacement", File.ReadAllText(Path.Combine(_into, "report.odt")));
        Assert.Equal(["report.odt"], Directory.GetFiles(_into).Select(Path.GetFileName));
    }
}
