using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a kept-both arrival is called.
///
/// **One naming served two different questions, and only one of them is about
/// copying.** Keeping both across a MOVE named the arrival "report - Copy.txt"
/// — after an operation that copied nothing and left nothing behind, in a
/// folder where no "report.txt" of yours had ever been. The word was describing
/// the mechanism rather than what happened.
///
/// Explorer splits them: " - Copy" for a duplicate in place, where the word is
/// true and the two files really are one beside its copy; "(2)" for a conflict,
/// which says only that this is the second thing here wanting the name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeepBothNamingTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "vaktari-keepboth-" + Guid.NewGuid().ToString("N")[..8]);

    public KeepBothNamingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);

        GC.SuppressFinalize(this);
    }

    private string Taken(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");

        return path;
    }

    private string Leaf(string name, bool isDirectory, bool inPlace)
        => Path.GetFileName(
            WindowsFileOperations.Deduplicate(Path.Combine(_folder, name), isDirectory, inPlace));

    /// <summary>The finding. Nothing about a cross-folder arrival is a copy of
    /// anything the user can see.</summary>
    [Fact]
    public void A_conflict_arrives_numbered_rather_than_claiming_to_be_a_copy()
    {
        Taken("report.txt");

        Assert.Equal("report (2).txt", Leaf("report.txt", isDirectory: false, inPlace: false));
    }

    /// <summary>From two, because the thing already sitting there is the
    /// first.</summary>
    [Fact]
    public void And_counts_on_from_there()
    {
        Taken("report.txt");
        Taken("report (2).txt");
        Taken("report (3).txt");

        Assert.Equal("report (4).txt", Leaf("report.txt", isDirectory: false, inPlace: false));
    }

    /// <summary>A duplicate in place keeps the word, because there it is
    /// true.</summary>
    [Fact]
    public void A_duplicate_in_place_is_still_a_copy()
    {
        Taken("report.txt");

        Assert.Equal("report - Copy.txt", Leaf("report.txt", isDirectory: false, inPlace: true));
    }

    [Fact]
    public void And_the_second_one_is_numbered_after_the_word()
    {
        Taken("report.txt");
        Taken("report - Copy.txt");

        Assert.Equal("report - Copy (2).txt", Leaf("report.txt", isDirectory: false, inPlace: true));
    }

    /// <summary>
    /// The kind still travels, in both namings. A folder called "my.photos" has
    /// no ".photos" extension to keep, and splitting on the last dot would
    /// produce "my (2).photos" — the whole reason the kind travels with the
    /// call.
    /// </summary>
    [Theory]
    [InlineData(false, "my.photos (2)")]
    [InlineData(true, "my.photos - Copy")]
    public void A_folder_name_is_atomic_either_way(bool inPlace, string expected)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "my.photos"));

        Assert.Equal(expected, Leaf("my.photos", isDirectory: true, inPlace));
    }

    /// <summary>And a dotfile is a name that starts with a dot rather than a
    /// bare extension.</summary>
    [Fact]
    public void A_dotfile_keeps_its_whole_name()
    {
        Taken(".bashrc");

        Assert.Equal(".bashrc (2)", Leaf(".bashrc", isDirectory: false, inPlace: false));
    }

    /// <summary>
    /// The two callers ask different questions, and the source says which is
    /// which: the duplicate branch is the one where the target IS the source.
    /// </summary>
    [Fact]
    public void The_duplicate_branch_asks_for_the_copy_and_the_conflict_does_not()
    {
        var source = RepoSource.Read("src", "Vaktari.Windows", "WindowsFileOperations.cs");

        Assert.Contains("item.Kind == ItemKind.Directory, inPlace: true)", source);
        Assert.Contains("item.Kind == ItemKind.Directory, inPlace: false)", source);

        // And no caller is left taking a default, because getting this wrong is
        // silent and shows up in a filename somebody reads a week later.
        Assert.DoesNotContain("bool inPlace = false", source);
    }
}
