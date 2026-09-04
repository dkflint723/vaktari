using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the elevated copy of the program actually does when it is handed a
/// request — run here without any rights at all, which is every part of it
/// except the consent dialog.
///
/// **The engine, not a second implementation.** The alternative was to shell
/// out to robocopy, and then an administrator copy would name its collisions
/// differently, refuse different paths and report different problems from every
/// other copy in the application.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedWorkerTests
{
    [WindowsFact]
    public async Task It_copies_what_it_was_named_and_says_nothing_was_left()
    {
        using var tree = new TempTree();

        var source = tree.Write("a.txt", "one");
        var into = tree.Dir("dst");

        var code = await ElevatedFileOp.RunAsync(
            new WindowsFileOperations(),
            new ElevatedRequest(ElevatedVerb.Copy, into, [source]));

        Assert.Equal(0, code);
        Assert.Equal("one", tree.Read("dst", "a.txt"));

        // A copy, so the original is still there. A move would pass every
        // assertion above.
        Assert.True(File.Exists(source));
    }

    [WindowsFact]
    public async Task It_deletes_what_it_was_named()
    {
        using var tree = new TempTree();

        var doomed = tree.Write("gone.txt", "one");

        var code = await ElevatedFileOp.RunAsync(
            new WindowsFileOperations(),
            new ElevatedRequest(ElevatedVerb.Delete, null, [doomed]));

        Assert.Equal(0, code);
        Assert.False(File.Exists(doomed));
    }

    /// <summary>
    /// **The count, which is the only thing it can say.** Windows' consent verb
    /// forbids redirecting the started process's output, so an exit code is the
    /// whole vocabulary — and it has to mean "how many of the things you named
    /// are still not done".
    /// </summary>
    [WindowsFact]
    public async Task It_says_how_many_it_could_not_do()
    {
        using var tree = new TempTree();

        var fine = tree.Write("fine.txt", "one");
        var locked = tree.Write("busy.txt", "two");

        using var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var code = await ElevatedFileOp.RunAsync(
            new WindowsFileOperations(),
            new ElevatedRequest(ElevatedVerb.Delete, null, [fine, locked]));

        Assert.Equal(1, code);
        Assert.False(File.Exists(fine));
        Assert.True(File.Exists(locked));
    }

    /// <summary>
    /// **What comes back is a count of the SOURCES, never of what happened
    /// inside them.** The engine counts outermost failures, which can be
    /// descendants: one folder with three files held open by another program
    /// answered 3 for a request naming 1 source, and the caller reads 3-of-1 as
    /// an administrator run that never spoke — so a run that said exactly what
    /// it did was reported to the person as incoherent.
    /// </summary>
    [WindowsFact]
    public async Task It_never_answers_with_more_than_it_was_given()
    {
        using var tree = new TempTree();

        var folder = tree.Dir("stuff");

        var held = new[] { "a.txt", "b.txt", "c.txt" }
            .Select(name => tree.Write($"stuff/{name}", name))
            .Select(path => new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None))
            .ToList();

        try
        {
            var code = await ElevatedFileOp.RunAsync(
                new WindowsFileOperations(),
                new ElevatedRequest(ElevatedVerb.Copy, tree.Dir("dst"), [folder]));

            Assert.Equal(1, code);
        }
        finally
        {
            foreach (var stream in held) stream.Dispose();
        }
    }

    /// <summary>
    /// **A clash is skipped, not overwritten and not renamed around.** This is
    /// a retry of something that failed, with nobody to ask: overwriting would
    /// destroy a file nobody agreed to lose, and keeping both would invent
    /// "a (1).txt" inside a protected folder, which is not what "go again with
    /// rights" means. The count says it happened.
    /// </summary>
    [WindowsFact]
    public async Task A_clash_is_skipped_rather_than_overwritten()
    {
        using var tree = new TempTree();

        var source = tree.Write("a.txt", "mine");
        tree.Write("dst/a.txt", "theirs");

        var code = await ElevatedFileOp.RunAsync(
            new WindowsFileOperations(),
            new ElevatedRequest(ElevatedVerb.Copy, tree.At("dst"), [source]));

        Assert.Equal(1, code);
        Assert.Equal("theirs", tree.Read("dst", "a.txt"));

        // And nothing invented beside it.
        Assert.Equal(
            ["a.txt"],
            Directory.EnumerateFiles(tree.At("dst")).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// A request whose verb and destination disagree is refused rather than
    /// guessed at — a copy with nowhere to copy to is not a delete.
    /// </summary>
    [WindowsFact]
    public async Task A_copy_with_nowhere_to_go_is_refused()
    {
        using var tree = new TempTree();

        var source = tree.Write("a.txt", "one");

        var code = await ElevatedFileOp.RunAsync(
            new WindowsFileOperations(),
            new ElevatedRequest(ElevatedVerb.Copy, null, [source]));

        Assert.Equal(ElevatedRequest.Refused, code);
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// The command line the elevated process is started with, as the system
    /// sees it.
    ///
    /// **The shape is what can be pinned; the elevation cannot.** Starting this
    /// for real raises the consent dialog, and there is nobody at a test
    /// machine to answer it. What matters here is that consent is ASKED FOR —
    /// the runas verb, which is the system's decision and not ours — and that
    /// the arguments go over as an argv rather than as a string somebody built.
    /// </summary>
    [WindowsFact]
    public void Starting_ourselves_elevated_asks_the_system_for_consent()
    {
        var request = new ElevatedRequest(
            ElevatedVerb.Delete, null, [@"C:\work\; rm -rf ~.txt"]);

        var info = WindowsLauncher.ElevatedSelf(@"C:\apps\vaktari.exe", request.ToArguments());

        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute, "the verb only exists on the shell's route");
        Assert.Equal(@"C:\apps\vaktari.exe", info.FileName);

        // The executable's own folder, not whatever the file manager happened
        // to be looking at. Nothing the elevated side does depends on it — it
        // accepts only fully-qualified paths — which is exactly why it must not
        // be somewhere a reader could think it mattered.
        Assert.Equal(@"C:\apps", info.WorkingDirectory);

        // The list, not a line: a name full of punctuation stays one argument,
        // and nothing was joined into Arguments behind its back.
        Assert.Equal(request.ToArguments(), info.ArgumentList);
        Assert.Equal("", info.Arguments);
    }
}
