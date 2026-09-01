using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Tidying a name somebody typed.
///
/// **Explorer strips leading and trailing spaces silently**, and Windows drops
/// trailing spaces and dots at the API level — so a name typed with one asks
/// for a file and gets a different one, and what it leaves behind can be
/// awkward for other tools to open or delete.
/// </summary>
public class FileNameTests
{
    [Theory]
    [InlineData("  notes.txt  ", "notes.txt")]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Space_around_a_name_is_not_part_of_it(string? typed, string expected)
    {
        Assert.Equal(expected, FileNames.Clean(typed));
    }

    /// <summary>
    /// Windows discards these, so keeping them means asking for one name and
    /// getting another.
    /// </summary>
    [WindowsTheory]
    [InlineData("report.", "report")]
    [InlineData("report...", "report")]
    [InlineData("report ", "report")]
    [InlineData("archive.tar.gz", "archive.tar.gz")]
    [InlineData("...", "")]
    public void A_trailing_dot_or_space_is_dropped_on_windows(string typed, string expected)
    {
        Assert.Equal(expected, FileNames.Clean(typed));
    }

    /// <summary>
    /// **A leading dot begins a name rather than ending one**, so a dotfile
    /// survives — trimming from both ends would turn .gitignore into gitignore.
    /// </summary>
    [Fact]
    public void A_dotfile_keeps_its_dot()
    {
        Assert.Equal(".gitignore", FileNames.Clean(".gitignore"));
        Assert.Equal(".gitignore", FileNames.Clean("  .gitignore  "));
    }

    /// <summary>
    /// **A space inside a name is somebody's business.** This is not a
    /// tidy-up-everything: it removes what the platform would remove anyway,
    /// and nothing else.
    /// </summary>
    [Fact]
    public void A_space_within_a_name_is_left_alone()
    {
        Assert.Equal("Ember Setup 0.1.0.exe", FileNames.Clean("Ember Setup 0.1.0.exe"));

        // Including one before the extension, which is legal and is how two
        // names can differ by something invisible in a listing.
        Assert.Equal("Ember Setup 0.1.0 .exe", FileNames.Clean("Ember Setup 0.1.0 .exe"));
    }

    // ---- what a name may not be -------------------------------------------
    //
    // Rename checked empty-or-separator and nothing else, so on Windows a colon
    // reached the filesystem and came back as "The parameter is incorrect." —
    // and "d:notes" is drive-RELATIVE, so Path.Combine discarded the folder and
    // the file silently left the listing for the current directory of drive D:.

    [Theory]
    [InlineData("report.txt")]
    [InlineData("a name with spaces.txt")]
    [InlineData("Ünïcödé.txt")]
    public void An_ordinary_name_is_accepted(string name)
        => Assert.Null(FileNames.Refuse(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_name_is_refused(string? name)
        => Assert.Equal("a name cannot be empty", FileNames.Refuse(name));

    [Fact]
    public void A_separator_is_refused_on_both_platforms()
    {
        Assert.NotNull(FileNames.Refuse("a/b.txt"));
        Assert.NotNull(FileNames.Refuse(Path.Combine("a", "b.txt")));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void The_directory_shorthands_are_not_names(string name)
        => Assert.NotNull(FileNames.Refuse(name));

    /// <summary>
    /// **The one that silently moved a file.** "d:notes" is drive-relative, so
    /// the folder was discarded and the file went to the current directory of
    /// drive D: — it simply vanished from the listing.
    /// </summary>
    [Fact]
    public void A_colon_is_refused_on_windows_and_allowed_on_linux()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(FileNames.Refuse("d:notes"));
            Assert.Contains(":", FileNames.Refuse("notes:stream")!);
        }
        else
        {
            // ext4 takes it, and refusing it here would stop a Linux user
            // naming a file something their filesystem is happy with.
            Assert.Null(FileNames.Refuse("notes:stream"));
        }
    }

    [Fact]
    public void A_reserved_device_name_is_refused_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.NotNull(FileNames.Refuse("CON"));
        Assert.NotNull(FileNames.Refuse("con.txt"));
        Assert.NotNull(FileNames.Refuse("LPT1.log"));

        // Not a false positive on a name that merely starts with one.
        Assert.Null(FileNames.Refuse("CONTENTS.txt"));
    }

    /// <summary>
    /// A trailing space is trimmed rather than refused: Clean already removes
    /// it, so reporting it as a fault would reject a name the application is
    /// perfectly willing to use.
    /// </summary>
    [Fact]
    public void A_trailing_space_is_tidied_rather_than_refused()
        => Assert.Null(FileNames.Refuse("report.txt "));
}
