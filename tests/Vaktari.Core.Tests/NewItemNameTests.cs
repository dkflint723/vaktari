using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The name Vaktari gives something it has just been asked to make.
///
/// **Three creates carried three copies of the rule and all three spelled it
/// differently from the rest of the application.** New folder, new file and
/// new-from-template each had their own numbering loop, and each produced
/// "New folder 2" — a space and a bare digit — while every other name this
/// program invents is parenthesised, on both platforms: the Windows copy
/// engine makes "report (2).txt" and the Linux one makes "report (1).txt".
/// Explorer's own answer to this very gesture is "New folder (2)".
/// </summary>
public sealed class NewItemNameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-newname-" + Guid.NewGuid().ToString("N")[..8]);

    public NewItemNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string At(string name) => Path.Combine(_root, name);

    [Fact]
    public void Nothing_in_the_way_gets_the_plain_name()
        => Assert.Equal(At("New file.txt"), NewItemName.Free(_root, "New file", ".txt"));

    /// <summary>The finding itself: parentheses, and counting from two.</summary>
    [Fact]
    public void A_second_one_is_numbered_in_parentheses()
    {
        Directory.CreateDirectory(At("New folder"));

        Assert.Equal(At("New folder (2)"), NewItemName.Free(_root, "New folder", ""));
    }

    [Fact]
    public void The_number_goes_before_the_extension()
    {
        File.WriteAllText(At("New file.txt"), "first");

        Assert.Equal(At("New file (2).txt"), NewItemName.Free(_root, "New file", ".txt"));
    }

    /// <summary>
    /// **A file counts, not only a folder.** New folder asked Directory.Exists
    /// alone, so a FILE sitting at the name was invisible to it: the create
    /// went ahead on a path it had just been told was free, System.IO refused,
    /// and the gesture made nothing while the status bar showed an IO error.
    /// </summary>
    [Fact]
    public void A_file_of_that_name_counts_as_taken()
    {
        File.WriteAllText(At("New folder"), "not a folder");

        Assert.Equal(At("New folder (2)"), NewItemName.Free(_root, "New folder", ""));
    }

    [Fact]
    public void A_folder_of_that_name_counts_as_taken()
    {
        Directory.CreateDirectory(At("New file.txt"));

        Assert.Equal(At("New file (2).txt"), NewItemName.Free(_root, "New file", ".txt"));
    }

    [Fact]
    public void The_numbering_keeps_going()
    {
        Directory.CreateDirectory(At("New folder"));
        Directory.CreateDirectory(At("New folder (2)"));

        Assert.Equal(At("New folder (3)"), NewItemName.Free(_root, "New folder", ""));
    }
}
