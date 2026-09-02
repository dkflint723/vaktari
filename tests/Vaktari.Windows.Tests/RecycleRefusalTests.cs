using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a refused recycle says, and what survives one.
///
/// **It said "SHFileOperation returned 32".** No file was named, nothing
/// suggested what to do, and the number comes from an API the reader has never
/// heard of — while the very same refusal arriving through any other route in
/// this application reads "something else has that file open". The Linux engine
/// has produced plain sentences from its own per-item loop since it was written.
///
/// **And the refusal took the undo with it.** SHFileOperation may recycle
/// several files before it stops, and the failure path returned before the
/// bookkeeping that records what landed — so a batch that stumbled on its last
/// file left every file ahead of it with no way back, which is exactly the
/// moment somebody reaches for Ctrl+Z.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleRefusalTests
{
    /// <summary>
    /// The wording comes from Failures.Describe, which every other refusal in
    /// the application already goes through — so a denied recycle reads exactly
    /// as a denied copy does, rather than in its own dialect.
    /// </summary>
    [Fact]
    public void A_denied_recycle_reads_like_every_other_denial()
    {
        var said = Failures.Describe(RecycleRefusal.For(0x05), "bin that");

        Assert.Contains("permission", said);
        Assert.DoesNotContain("SHFileOperation", said);
    }

    /// <summary>
    /// 32 is ERROR_SHARING_VIOLATION, and it is the commonest refusal there is:
    /// a document still open in the program that made it.
    /// </summary>
    [Fact]
    public void A_file_something_else_has_open_says_so()
    {
        var said = Failures.Describe(RecycleRefusal.For(32), "bin that");

        Assert.Contains("open", said);
        Assert.DoesNotContain("32", said);
    }

    /// <summary>
    /// **Two numbering schemes arrive through one int.** From 0x71 to 0xB7 the
    /// shell answers with its own DE_* set, which predates Win32 and collides
    /// with it — 0x81 is DE_FILENAMETOOLONG and also ERROR_WAIT_NO_CHILDREN.
    /// Below that range the codes are ordinary Win32 ones, and 112,
    /// ERROR_DISK_FULL, lands one short of it.
    /// </summary>
    [Theory]
    [InlineData(0x74, "root")]
    [InlineData(0x85, "too big")]
    [InlineData(0x81, "too long")]
    public void A_shell_code_is_read_as_a_shell_code(int code, string expected)
        => Assert.Contains(expected, RecycleRefusal.For(code).Message);

    /// <summary>The boundary itself: 112 is a Win32 code, not DE_*, and reading
    /// it as one would produce a sentence about the wrong thing.</summary>
    [Fact]
    public void A_full_disk_is_not_mistaken_for_a_shell_code()
    {
        var said = Failures.Describe(RecycleRefusal.For(112), "bin that");

        Assert.DoesNotContain("the bin would not take it", said);
    }

    /// <summary>ERRORONDEST is ored into the code and would otherwise turn every
    /// refusal into an unrecognised number.</summary>
    [Fact]
    public void The_destination_flag_does_not_hide_the_code()
        => Assert.IsType<UnauthorizedAccessException>(RecycleRefusal.For(0x05 | 0x10000));

    /// <summary>A code nothing recognises still produces a sentence rather than
    /// a bare number, and carries the HRESULT so the describer can try.</summary>
    [Fact]
    public void An_unknown_code_still_produces_words()
    {
        var refusal = RecycleRefusal.For(0x4242);

        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.DoesNotContain("SHFileOperation", refusal.Message);
    }

    // ---- and what survives a refusal ----------------------------------------

    /// <summary>Answers the batch with a refusal and each single path with
    /// success, which is what the shell does when one file of many is held
    /// open — except that the shell will not say which.</summary>
    private sealed class RefusesTheBatch
    {
        public List<int> Sizes { get; } = [];

        public RecycleResult Answer(IReadOnlyList<string> paths)
        {
            Sizes.Add(paths.Count);

            return paths.Count > 1
                ? new RecycleResult(32, false)   // ERROR_SHARING_VIOLATION
                : new RecycleResult(0, false);
        }
    }

    /// <summary>
    /// **One number for a whole batch names nothing.** Asked one path at a time,
    /// the shell answers one path at a time — which is the only way the status
    /// line can say which file refused.
    /// </summary>
    [Fact]
    public async Task A_refused_batch_is_asked_again_one_path_at_a_time()
    {
        var shell = new RefusesTheBatch();

        var ops = new WindowsFileOperations { Bin = null, RecycleOverride = shell.Answer };

        // Real files: the re-ask deliberately SKIPS a path that is no longer
        // there, because the batch call may have recycled several before it
        // refused and asking again would report a success as a failure.
        var root = Path.Combine(Path.GetTempPath(), "vaktari-refuse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var files = new[] { "a.txt", "b.txt", "c.txt" }
                .Select(n => Path.Combine(root, n))
                .ToList();

            foreach (var file in files) await File.WriteAllTextAsync(file, "x");

            var handle = ops.Trash(files);

            await Settled(handle);

            // The batch, then each path on its own.
            Assert.Equal([3, 1, 1, 1], shell.Sizes);

            // And it finished rather than failing: the ones that went really
            // went, and the status line reports what was left behind.
            Assert.Equal(OperationState.Completed, handle.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// **A file already gone is not asked about again.** The batch call may
    /// have recycled several before it stopped; asking a second time would
    /// answer "not there any more", and a success would be reported as a
    /// failure.
    /// </summary>
    [Fact]
    public async Task A_path_the_batch_already_took_is_not_re_asked()
    {
        var shell = new RefusesTheBatch();

        var ops = new WindowsFileOperations { Bin = null, RecycleOverride = shell.Answer };

        var root = Path.Combine(Path.GetTempPath(), "vaktari-partial-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var kept = Path.Combine(root, "kept.txt");
            var gone = Path.Combine(root, "gone.txt");

            await File.WriteAllTextAsync(kept, "x");

            var handle = ops.Trash([kept, gone]);

            await Settled(handle);

            // The batch, then only the one still on disk.
            Assert.Equal([2, 1], shell.Sizes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Settled(IOperationHandle handle)
    {
        for (var i = 0; i < 200 && handle.State is OperationState.Running or OperationState.Queued; i++)
            await Task.Delay(10);
    }
}
