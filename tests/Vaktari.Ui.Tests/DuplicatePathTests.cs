using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The path a duplicates listing is, the twin of <see cref="UsagePathTests"/>:
/// both are one folder looked at a different way, and both live behind a
/// prefix and one escaped field.
///
/// **Escaped because a path holds colons and separators.**
/// <c>PathRules.Normalise</c> runs over every path the pane compares, and an
/// unescaped folder inside this one would give it a separator to call a
/// parent.
/// </summary>
public sealed class DuplicatePathTests
{
    /// <summary>
    /// Spelled for the platform running the test: <c>LeafOf</c> asks
    /// <c>Path.GetFileName</c>, which finds no leaf in a Windows path on Linux
    /// and would name the tab after the whole thing.
    /// </summary>
    private static readonly string Folder = OperatingSystem.IsWindows()
        ? @"C:\Users\me\Pictures"
        : "/home/me/Pictures";

    [Fact]
    public void The_folder_comes_back_out_as_it_went_in()
        => Assert.Equal(Folder, VirtualPaths.FolderOf(VirtualPaths.Duplicates(Folder)));

    [Fact]
    public void A_duplicates_listing_is_a_virtual_listing()
    {
        var path = VirtualPaths.Duplicates(Folder);

        Assert.True(VirtualPaths.IsDuplicates(path));

        // Without this the pane would try to enumerate it off the disk and
        // start a watcher on it.
        Assert.True(VirtualPaths.IsVirtual(path));
    }

    /// <summary>The two schemes are the same shape, so each must refuse the
    /// other's paths rather than answer for them.</summary>
    [Fact]
    public void The_two_listings_of_one_folder_are_not_each_other()
    {
        Assert.False(VirtualPaths.IsDuplicates(VirtualPaths.Usage(Folder)));
        Assert.False(VirtualPaths.IsUsage(VirtualPaths.Duplicates(Folder)));

        Assert.NotEqual(VirtualPaths.Usage(Folder), VirtualPaths.Duplicates(Folder));

        // And both still name the folder they came from, which is the way out.
        Assert.Equal(Folder, VirtualPaths.FolderOf(VirtualPaths.Usage(Folder)));
        Assert.Equal(Folder, VirtualPaths.FolderOf(VirtualPaths.Duplicates(Folder)));
    }

    [Fact]
    public void A_real_folder_is_not_one()
    {
        Assert.False(VirtualPaths.IsDuplicates(Folder));
        Assert.False(VirtualPaths.IsDuplicates(null));
        Assert.Equal("", VirtualPaths.FolderOf(Folder));
    }

    [Fact]
    public void The_tab_is_named_for_the_folder()
        => Assert.Equal("Duplicates in Pictures", VirtualPaths.Label(VirtualPaths.Duplicates(Folder)));

    /// <summary>A drive or a mount root has no leaf, so it is shown as it is
    /// spelled rather than as nothing at all.</summary>
    [Fact]
    public void A_root_is_named_as_it_is_spelled()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";

        Assert.Equal($"Duplicates in {root}", VirtualPaths.Label(VirtualPaths.Duplicates(root)));
    }

    /// <summary>
    /// One key for every duplicates listing, for the reason the usage one has
    /// one: keyed by the path, a person who scanned fifty folders would leave
    /// fifty view records behind.
    /// </summary>
    [Fact]
    public void Every_duplicates_listing_remembers_its_view_under_one_key()
    {
        Assert.Equal(VirtualPaths.DuplicatesPrefix + "*", VirtualPaths.DuplicatesViewKey);

        Assert.NotEqual(VirtualPaths.UsageViewKey, VirtualPaths.DuplicatesViewKey);
    }

    /// <summary>
    /// Nothing here may throw on a malformed path: these come back out of
    /// session.json, where a hand-edited or truncated one has to give a
    /// harmless listing rather than stop the window opening.
    /// </summary>
    [Fact]
    public void A_malformed_path_names_a_folder_that_is_not_there()
    {
        var folder = VirtualPaths.FolderOf(VirtualPaths.DuplicatesPrefix + "%");

        Assert.Equal("%", folder);
        Assert.False(Directory.Exists(folder));
    }
}
