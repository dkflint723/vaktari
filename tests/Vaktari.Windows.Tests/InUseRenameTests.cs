using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Renaming, undoing and binning a folder while something has it, or
/// something inside it, open — against a real second process
/// (<see cref="AnotherProgram"/>), on NTFS.
///
/// **"Access to the path … is denied."** That is what a folder held from
/// below answered with, whoever held it — Vaktari's own watcher or a document
/// open in Word — and it read as a permission problem (rename-notes, plan §0,
/// §1a). The engine now tells the three cases apart by asking the disk
/// (InUseCheck), lets go of Vaktari's own handles before it moves a folder
/// whole (IFolderHandover), and leaves every other program exactly as it was:
/// each test checks the holder is still running afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InUseRenameTests
{
    /// <summary>A hand-over that records what it was told, and runs an
    /// action of the test's when it is asked to let go.</summary>
    private sealed class Recording(Func<IReadOnlyList<string>, ValueTask>? release = null) : IFolderHandover
    {
        public List<string> Released { get; } = [];
        public List<(string From, string To)> Moves { get; } = [];
        public List<string> Gone { get; } = [];
        public int Ended;

        public async ValueTask<IFolderLease> ReleaseAsync(IReadOnlyList<string> folders, CancellationToken ct)
        {
            lock (Released) Released.AddRange(folders);

            if (release is not null) await release(folders);

            return new Lease(this);
        }

        public void Followed(string from, string to)
        {
        }

        public bool MovedFrom(string from, string to)
        {
            lock (Moves) return Moves.Any(m => PathRules.Same(m.From, from) && PathRules.Same(m.To, to));
        }

        private sealed class Lease(Recording owner) : IFolderLease
        {
            public void Moved(string from, string to)
            {
                lock (owner.Moves) owner.Moves.Add((from, to));
            }

            public void Gone(string folder)
            {
                lock (owner.Gone) owner.Gone.Add(folder);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Ended);
                return ValueTask.CompletedTask;
            }
        }
    }

    // ---- what is said ----------------------------------------------------------

    [WindowsFact]
    public async Task A_folder_another_program_has_a_file_open_in_is_in_use_not_permission()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("photos");
        tree.Dir("photos", "deep");

        using var holder = AnotherProgram.HoldingFile(tree.At("photos", "deep", "held.txt"));

        var refused = await Assert.ThrowsAsync<InUseException>(async () =>
            await new WindowsFileOperations().RenameAsync(folder, "pictures", CancellationToken.None));

        Assert.True(refused.IsDirectory);
        Assert.False(refused.ItselfOpen);
        Assert.Equal("something has a file inside that folder open", Failures.Describe(refused, "rename that"));
        Assert.True(Directory.Exists(folder), "the folder moved although the rename failed");
        Assert.True(holder.IsRunning, "the other program was closed");
    }

    [WindowsFact]
    public async Task A_folder_another_program_is_working_in_is_open_itself()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("project");

        using var holder = AnotherProgram.InFolder(folder);

        var refused = await Assert.ThrowsAsync<InUseException>(async () =>
            await new WindowsFileOperations().RenameAsync(folder, "renamed", CancellationToken.None));

        Assert.True(refused.ItselfOpen);
        Assert.Equal("something has that folder open", Failures.Describe(refused, "rename that"));
        Assert.True(holder.IsRunning);
    }

    /// <summary>A program working in a SUBfolder holds the folder from
    /// below, like a file open in it.</summary>
    [WindowsFact]
    public async Task A_folder_another_program_is_working_below_is_held_from_inside()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("project");
        var sub = tree.Dir("project", "src");

        using var holder = AnotherProgram.InFolder(sub);

        var refused = await Assert.ThrowsAsync<InUseException>(async () =>
            await new WindowsFileOperations().RenameAsync(folder, "renamed", CancellationToken.None));

        Assert.False(refused.ItselfOpen);
        Assert.Equal("something has a file inside that folder open", Failures.Describe(refused, "rename that"));
    }

    [WindowsFact]
    public async Task A_file_another_program_has_open_says_so()
    {
        using var tree = new TempTree();
        var file = tree.Write("report.docx");

        using var holder = AnotherProgram.HoldingFile(file);

        var refused = await Assert.ThrowsAsync<InUseException>(async () =>
            await new WindowsFileOperations().RenameAsync(file, "final.docx", CancellationToken.None));

        Assert.False(refused.IsDirectory);
        Assert.Equal("something else has that file open", Failures.Describe(refused, "rename that"));
    }

    /// <summary>
    /// **A real refusal of permission is still one.** Denied DELETE on the
    /// folder and DELETE_CHILD on its parent — the measured shape (rename
    /// notes, plan §1d) — through icacls on a folder this test made, put back
    /// in a finally.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_this_person_may_not_rename_is_a_permission_failure()
    {
        using var tree = new TempTree();
        var parent = tree.Dir("parent");
        var folder = tree.Dir("parent", "locked");
        var me = $"{Environment.UserDomainName}\\{Environment.UserName}";

        Icacls(folder, "/deny", $"{me}:(DE)");
        Icacls(parent, "/deny", $"{me}:(DC)");

        try
        {
            var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await new WindowsFileOperations().RenameAsync(folder, "open", CancellationToken.None));

            Assert.Equal("you do not have permission to rename that", Failures.Describe(refused, "rename that"));
        }
        finally
        {
            Icacls(folder, "/remove:d", me);
            Icacls(parent, "/remove:d", me);
        }
    }

    /// <summary>A parent that will not take a new folder is the other
    /// permission shape the engine asks about.</summary>
    [WindowsFact]
    public async Task A_parent_that_refuses_new_folders_is_a_permission_failure()
    {
        using var tree = new TempTree();
        var parent = tree.Dir("parent");
        var folder = tree.Dir("parent", "inside");
        var me = $"{Environment.UserDomainName}\\{Environment.UserName}";

        Icacls(parent, "/deny", $"{me}:(AD)");

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await new WindowsFileOperations().RenameAsync(folder, "moved", CancellationToken.None));
        }
        finally
        {
            Icacls(parent, "/remove:d", me);
        }
    }

    private static void Icacls(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add(path);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"icacls {string.Join(' ', arguments)} failed: {said}");
    }

    // ---- letting go of our own ---------------------------------------------------

    /// <summary>
    /// **Vaktari's own watch on a subfolder refused the rename** — the one
    /// every tab keeps on its folder. The control half shows the watch really
    /// does block; the hand-over that disposes it is what lets the rename
    /// through, and the folder is then followed to its new name.
    /// </summary>
    [WindowsFact]
    public async Task Renaming_a_folder_lets_go_of_our_own_watch_on_a_subfolder()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("music");
        var sub = tree.Dir("music", "albums");

        var watch = new WindowsFileSystemProvider().Watch(sub, _ => { });

        try
        {
            await Assert.ThrowsAsync<InUseException>(async () =>
                await new WindowsFileOperations().RenameAsync(folder, "songs", CancellationToken.None));

            var handover = new Recording(_ =>
            {
                watch.Dispose();
                return ValueTask.CompletedTask;
            });

            await new WindowsFileOperations { Handover = handover }.RenameAsync(folder, "songs", CancellationToken.None);

            Assert.True(Directory.Exists(tree.At("songs", "albums")));
            Assert.Contains(handover.Released, f => PathRules.Same(f, folder));
            Assert.True(handover.MovedFrom(folder, tree.At("songs")), "the folder was not followed to its new name");
            Assert.Equal(1, handover.Ended);
        }
        finally
        {
            watch.Dispose();
        }
    }

    /// <summary>
    /// **One more try, after a moment.** A watch that was mid-read when it
    /// was let go of can hold its handle a moment longer — here it lets go
    /// 60 ms after the hold began, which the first try meets and the second,
    /// 150 ms on, does not.
    /// </summary>
    [WindowsFact]
    public async Task A_handle_let_go_of_a_moment_late_does_not_fail_the_rename()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("music");
        var sub = tree.Dir("music", "albums");

        var watch = new WindowsFileSystemProvider().Watch(sub, _ => { });

        try
        {
            var handover = new Recording(folders =>
            {
                _ = Task.Delay(60).ContinueWith(_ => watch.Dispose(), TaskScheduler.Default);
                return ValueTask.CompletedTask;
            });

            await new WindowsFileOperations { Handover = handover }.RenameAsync(folder, "songs", CancellationToken.None);

            Assert.True(Directory.Exists(tree.At("songs")));
        }
        finally
        {
            watch.Dispose();
        }
    }

    /// <summary>
    /// A rename that fails after letting go ends the hold without saying the
    /// folder moved, so everything that let go is put back where it was.
    /// </summary>
    [WindowsFact]
    public async Task A_failed_rename_ends_the_hold_without_a_move()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("photos");

        using var holder = AnotherProgram.HoldingFile(tree.At("photos", "held.txt"));

        var handover = new Recording();

        await Assert.ThrowsAsync<InUseException>(async () =>
            await new WindowsFileOperations { Handover = handover }.RenameAsync(folder, "pictures", CancellationToken.None));

        Assert.Contains(handover.Released, f => PathRules.Same(f, folder));
        Assert.Empty(handover.Moves);
        Assert.Empty(handover.Gone);
        Assert.Equal(1, handover.Ended);
    }

    [WindowsFact]
    public async Task Undoing_a_folder_rename_lets_go_of_the_folder_first()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("photos");
        var handover = new Recording();
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(folder, "pictures", CancellationToken.None);

        ops.Handover = handover;

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(Directory.Exists(folder));
        Assert.Contains(handover.Released, f => PathRules.Same(f, tree.At("pictures")));
        Assert.True(handover.MovedFrom(tree.At("pictures"), folder), "the undone rename was not followed back");
    }

    /// <summary>
    /// **A refused undo of a folder rename was lost from the history** (review
    /// finding 8): the step was popped before the walk and never put back, so
    /// Ctrl+Z changed nothing and took the step away. It stays, and works once
    /// the other program lets go.
    /// </summary>
    [WindowsFact]
    public async Task A_refused_undo_of_a_folder_rename_stays_on_the_stack()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("photos");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(folder, "pictures", CancellationToken.None);

        var described = ops.UndoDescription;

        using (AnotherProgram.HoldingFile(tree.At("pictures", "held.txt")))
        {
            await Assert.ThrowsAsync<InUseException>(async () => await ops.UndoAsync(CancellationToken.None));

            Assert.True(ops.CanUndo, "the refused undo was taken off the stack");
            Assert.Equal(described, ops.UndoDescription);
        }

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(Directory.Exists(folder));
    }

    // ---- the bin, through its stand-in -------------------------------------------

    /// <summary>
    /// A folder binned whole is let go of first and told gone after — asked of
    /// the disk. Through RecycleOverride: the real bin is never touched.
    /// </summary>
    [WindowsFact]
    public async Task A_binned_folder_is_let_go_of_and_told_gone()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("old");
        var handover = new Recording();

        var ops = new WindowsFileOperations
        {
            Handover = handover,
            RecycleOverride = paths =>
            {
                foreach (var path in paths) Directory.Delete(path, recursive: true);
                return new RecycleResult(0, false);
            },
        };

        var handle = ops.Trash([folder]);
        await handle.Completion;

        Assert.Contains(handover.Released, f => PathRules.Same(f, folder));
        Assert.Contains(handover.Gone, f => PathRules.Same(f, folder));
        Assert.Equal(1, handover.Ended);
    }

    /// <summary>
    /// **A refusal from the shell names what is true.** The shell answers a
    /// folder with something open beneath it 0x20 (review, shellmove), and
    /// that reads as something having a file inside it open — not as a copy,
    /// and not as permission.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_the_bin_refused_as_in_use_says_so()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("old");

        var ops = new WindowsFileOperations
        {
            RecycleOverride = _ => new RecycleResult(0x20, false),
        };

        var handle = ops.Trash([folder]);
        await handle.Completion;

        var problem = Assert.Single(handle.Problems);

        Assert.IsType<InUseException>(problem.Error);
        Assert.Equal("something has a file inside that folder open", Failures.Describe(problem.Error));
    }
}
