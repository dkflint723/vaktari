using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The sentence shown when a batch finished with items left behind.
///
/// **It names a file.** "3 items could not be copied" sends someone hunting
/// through a folder comparing two listings; the first name plus a count tells
/// them where to start.
/// </summary>
public sealed class ProblemMessageTests
{
    [Fact]
    public void One_item_left_behind_is_named()
    {
        var said = ShellViewModel.DescribeProblems(
        [
            new ItemProblem(Path.Combine("C:", "work", "report.docx"), new IOException("in use")),
        ]);

        Assert.Contains("report.docx", said);
        Assert.DoesNotContain("more", said);
    }

    [Fact]
    public void Several_are_named_once_and_counted()
    {
        var said = ShellViewModel.DescribeProblems(
        [
            new ItemProblem(Path.Combine("C:", "work", "report.docx"), new IOException("in use")),
            new ItemProblem(Path.Combine("C:", "work", "notes.txt"), new IOException("in use")),
            new ItemProblem(Path.Combine("C:", "work", "data.csv"), new IOException("in use")),
        ]);

        Assert.Contains("report.docx", said);
        Assert.Contains("2 more", said);
    }

    /// <summary>
    /// The reason comes from the same register every other failure in this
    /// window uses — never a .NET type name in front of the person.
    /// </summary>
    [Fact]
    public void The_reason_is_in_plain_words()
    {
        var said = ShellViewModel.DescribeProblems(
        [
            new ItemProblem("/home/flint/notes.txt", new UnauthorizedAccessException()),
        ]);

        Assert.DoesNotContain("Exception", said);
        Assert.DoesNotContain("System.", said);
    }

    /// <summary>A trailing separator must not swallow the name — a directory
    /// left behind still has to say which one.</summary>
    [Fact]
    public void A_folder_left_behind_is_still_named()
    {
        var said = ShellViewModel.DescribeProblems(
        [
            new ItemProblem(
                "/home/flint/photos" + Path.DirectorySeparatorChar,
                new IOException("busy")),
        ]);

        Assert.Contains("photos", said);
    }
}
