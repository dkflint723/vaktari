using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The smaller pieces of renaming a folder something has open: the listings
/// that name a folder following it, each operation's own verb on the
/// transfer bar, and the process leaving the folder it was started in.
/// </summary>
public sealed class HandoverSmallTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vaktari-small");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>A search carries the folder it was started from, and keeps
    /// every field of its question.</summary>
    [Fact]
    public void A_search_started_in_the_folder_follows_it()
    {
        var search = VirtualPaths.Search("report", At("one", "sub"), scoped: true, matchCase: true);

        var moved = VirtualPaths.Rebase(search, At("one"), At("uno"));

        Assert.NotNull(moved);
        Assert.True(PathRules.Same(At("uno", "sub"), VirtualPaths.OriginOf(moved!)));
        Assert.Equal("report", VirtualPaths.QueryOf(moved!));
        Assert.True(VirtualPaths.IsScoped(moved!));
        Assert.True(VirtualPaths.MatchesCase(moved!));
    }

    [Fact]
    public void A_usage_listing_follows_and_a_sibling_s_does_not()
    {
        Assert.True(PathRules.Same(
            At("uno", "x"),
            VirtualPaths.FolderOf(VirtualPaths.Rebase(VirtualPaths.Usage(At("one", "x")), At("one"), At("uno"))!)));

        Assert.Null(VirtualPaths.Rebase(VirtualPaths.Usage(At("onetwo")), At("one"), At("uno")));
        Assert.Null(VirtualPaths.Rebase(VirtualPaths.Trash, At("one"), At("uno")));
    }

    /// <summary>
    /// **"you do not have permission to copy that", for a folder sent to the
    /// bin.** Every kind was worded as a copy; each says its own now.
    /// </summary>
    [Fact]
    public void Each_operation_says_its_own_verb()
    {
        var refused = new Core.FileSystem.ItemProblem(At("old"), new UnauthorizedAccessException("denied"));

        Assert.Equal(
            $"you do not have permission to move that to {Vaktari.Core.Naming.TheBin}",
            Assert.Single(ShellViewModel.ListProblems([refused], OperationKind.Trash)).Reason);

        Assert.Equal("you do not have permission to move that",
                     Assert.Single(ShellViewModel.ListProblems([refused], OperationKind.Move)).Reason);

        Assert.Equal("you do not have permission to delete that",
                     Assert.Single(ShellViewModel.ListProblems([refused], OperationKind.Delete)).Reason);

        Assert.Contains("you do not have permission to move that",
                        ShellViewModel.DescribeProblems([refused], OperationKind.Move));
    }

    /// <summary>The bar uses the new wording for something in use.</summary>
    [Fact]
    public void Something_in_use_reads_as_in_use_on_the_bar()
    {
        var held = new Core.FileSystem.ItemProblem(
            At("old"), new InUseException(At("old"), isDirectory: true, unchecked((int)0x80070020)));

        Assert.Equal("something inside that folder is open",
                     Assert.Single(ShellViewModel.ListProblems([held], OperationKind.Trash)).Reason);
    }

    /// <summary>
    /// **"vaktari ." pinned that folder for the session** on Windows, where a
    /// process's own folder cannot be renamed and nor can anything above it.
    /// </summary>
    [Fact]
    public void On_Windows_the_process_works_in_its_install_folder_after_startup()
    {
        if (OperatingSystem.IsWindows())
            Assert.Equal(AppContext.BaseDirectory, Program.WorkingFolderAfterStartup());
        else
            Assert.Null(Program.WorkingFolderAfterStartup());
    }

    /// <summary>And only once the arguments have been read against the folder
    /// it was started in, since that is what "." means.</summary>
    [Fact]
    public void The_folder_is_left_only_after_the_arguments_are_read()
    {
        var source = RepoSource.Ui("Program.cs");

        var run = source.IndexOf("private static void Run(", StringComparison.Ordinal);
        var resolved = source.IndexOf("ResolveArguments(", run, StringComparison.Ordinal);
        var left = source.IndexOf("LeaveStartingFolder();", run, StringComparison.Ordinal);
        var started = source.IndexOf("StartWithClassicDesktopLifetime", run, StringComparison.Ordinal);

        Assert.True(run > 0 && resolved > run, "Run no longer reads its arguments");
        Assert.True(left > resolved, "the folder is left before the arguments are read against it");
        Assert.True(left < started, "the folder is left only after the window has run");
    }
}
