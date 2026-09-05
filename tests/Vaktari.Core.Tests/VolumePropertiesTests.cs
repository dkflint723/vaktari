using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The volume rows on a folder's properties.
///
/// **Opening properties on a drive answered nothing anybody opens it to ask.**
/// The window's general group comes from FileDetails — a name, a location,
/// dates — and a volume has none of the useful ones: no size, no capacity, no
/// filesystem, no label. The sidebar had drawn a free-space bar and a
/// "1 TiB free of 4 TiB" tooltip for a while, so the figure existed; the dialog
/// somebody opens to ask for it did not have it.
///
/// The drives on the machine running this are nobody's business, so every test
/// here but the last one stands a reader in for them and asserts about the ROWS.
/// </summary>
public sealed class VolumePropertiesTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;

    public void Dispose()
    {
        VolumeProperties.Reader = null;

        GC.SuppressFinalize(this);
    }

    /// <summary>Any folder, named the way this platform names one.</summary>
    private static string Folder(string name)
        => Path.Combine(Path.GetTempPath(), name);

    /// <summary>A path that IS a volume, with no trailing separator, so the
    /// tests about the root case and the test about the trailing separator
    /// cannot accidentally be the same test.</summary>
    private static string Drive() => Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    private static void Reads(VolumeUsage? usage)
        => VolumeProperties.Reader = _ => usage;

    private static VolumeUsage On(string root, long total, long free,
                                  string label = "Storage", string format = "NTFS")
        => new(root, label, format, total, free);

    private static IReadOnlyList<PropertyRow> Rows(PropertyGroup? group)
    {
        Assert.NotNull(group);
        Assert.Equal("volume", group!.Label);

        return group.Rows;
    }

    private static string Value(PropertyGroup? group, string label)
        => Rows(group).Single(r => r.Label == label).Value;

    // ---- the drive itself --------------------------------------------------

    /// <summary>
    /// **A drive gets the drive's own figures**, which is the half of the
    /// finding about opening properties on This PC's rows or on a mount point.
    /// </summary>
    [Fact]
    public void A_folder_that_is_the_volume_carries_the_volumes_own_figures()
    {
        var drive = Drive();

        Reads(On(drive, total: 8 * Gib, free: 2 * Gib));

        var group = VolumeProperties.Describe(drive, isDirectory: true);

        Assert.Equal("Storage", Value(group, "label"));
        Assert.Equal("NTFS", Value(group, "file system"));
        Assert.Equal("8 GiB", Value(group, "capacity"));
        Assert.Equal("2 GiB", Value(group, "free"));
    }

    /// <summary>
    /// Used is derived, not read: no filesystem reports it, and a window that
    /// showed capacity and free and left the subtraction to the reader would be
    /// answering the easy two thirds of the question.
    /// </summary>
    [Fact]
    public void Used_is_what_the_capacity_has_lost()
    {
        var drive = Drive();

        Reads(On(drive, total: 8 * Gib, free: 2 * Gib));

        Assert.Equal("6 GiB", Value(
            VolumeProperties.Describe(drive, isDirectory: true), "used"));
    }

    /// <summary>
    /// **A trailing separator is how a drive usually arrives.** "D:\" and "/"
    /// are roots, and a folder handed over with a separator on the end is the
    /// same folder — so the comparison that decides which set of rows to draw
    /// trims both sides. Without it a drive fell through to the folder rows and
    /// reported its own root as the volume it sits on, which is true and
    /// useless.
    /// </summary>
    [Fact]
    public void A_trailing_separator_does_not_stop_a_drive_being_the_drive()
    {
        var drive = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        Reads(On(drive, total: 8 * Gib, free: 2 * Gib));

        var group = VolumeProperties.Describe(
            drive + Path.DirectorySeparatorChar, isDirectory: true);

        Assert.Equal("8 GiB", Value(group, "capacity"));
        Assert.DoesNotContain(Rows(group), r => r.Label == "volume");
    }

    /// <summary>
    /// An unformatted or encrypted volume answers that it is ready and then
    /// refuses one of these, and a row reading "label:" with nothing after it
    /// says less than no row at all.
    /// </summary>
    [Fact]
    public void A_drive_that_will_not_name_itself_shows_no_empty_rows()
    {
        var drive = Drive();

        Reads(On(drive, total: 8 * Gib, free: 2 * Gib, label: "", format: ""));

        var rows = Rows(VolumeProperties.Describe(drive, isDirectory: true));

        Assert.DoesNotContain(rows, r => r.Label == "label");
        Assert.DoesNotContain(rows, r => r.Label == "file system");
        Assert.Contains(rows, r => r.Label == "capacity");
    }

    // ---- a folder on it ----------------------------------------------------

    /// <summary>
    /// **The other half of the finding, and the more common ask.** "Will this
    /// fit" is a question about the folder being copied into, not about its
    /// drive — and a folder's properties said nothing about room at all.
    /// </summary>
    [Fact]
    public void A_folder_on_a_volume_is_told_which_one_and_how_much_is_left()
    {
        var drive = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        Reads(On(drive, total: 8 * Gib, free: 2 * Gib));

        var group = VolumeProperties.Describe(Folder("somewhere"), isDirectory: true);

        Assert.Equal(drive, Value(group, "volume"));
        Assert.Equal("2 GiB of 8 GiB", Value(group, "free"));
    }

    /// <summary>
    /// Which volume, because the figure is otherwise unattributed: a folder
    /// under a mount point is on a different disk from its parent, and a bare
    /// "2 GiB free" would read as the parent's.
    /// </summary>
    [Fact]
    public void A_folder_under_a_mount_names_the_mount_and_not_its_parent()
    {
        var mount = Folder("stick");

        Reads(On(mount, total: 8 * Gib, free: 2 * Gib));

        Assert.Equal(mount, Value(
            VolumeProperties.Describe(Path.Combine(mount, "photos"), isDirectory: true),
            "volume"));
    }

    // ---- where there is nothing to say -------------------------------------

    /// <summary>
    /// A file lives on a volume too, and the row would be noise on the
    /// properties of every file anybody opened.
    /// </summary>
    [Fact]
    public void A_file_is_not_asked_where_it_lives()
    {
        Reads(On(Drive(), total: 8 * Gib, free: 2 * Gib));

        Assert.Null(VolumeProperties.Describe(Folder("notes.txt"), isDirectory: false));
    }

    [Fact]
    public void A_volume_that_cannot_be_read_says_nothing()
    {
        Reads(null);

        Assert.Null(VolumeProperties.Describe(Folder("here"), isDirectory: true));
    }

    /// <summary>
    /// /proc, /sys and a FUSE mount that does not account report zero, and
    /// "0 B free of 0 B" is worse than silence — it reads as a full disk.
    /// </summary>
    [Fact]
    public void A_filesystem_that_accounts_for_nothing_says_nothing()
    {
        Reads(On(Drive(), total: 0, free: 0));

        Assert.Null(VolumeProperties.Describe(Folder("here"), isDirectory: true));
    }

    // ---- the real machine, once --------------------------------------------

    /// <summary>
    /// **A UNC path is not a drive, and DriveInfo says so by throwing.**
    /// Measured: <c>new DriveInfo(@"\\localhost\C$")</c> raises
    /// ArgumentException, "Drive name must be a root directory (i.e. 'C:\') or
    /// a drive letter ('C')." Without the catch, opening properties on a
    /// network share threw out of the load and the window stayed half filled.
    ///
    /// Windows only, and not by preference: on Linux those backslashes are
    /// ordinary characters in a relative name, so the same string is a path
    /// under the working directory and DriveInfo answers about the disk it is
    /// on. The assertion is about a Windows path shape and runs there.
    /// </summary>
    [WindowsFact]
    public void A_network_share_is_not_a_drive_and_gets_no_rows()
    {
        VolumeProperties.Reader = null;

        Assert.Null(VolumeProperties.Read(@"\\localhost\C$"));
        Assert.Null(VolumeProperties.Describe(@"\\localhost\C$", isDirectory: true));
    }

    /// <summary>
    /// The seam runs both ways: a test that stands in for the machine is
    /// answered by its stand-in, and the application, which sets nothing, is
    /// answered by the machine. Asserting only the first half would have let
    /// the fall-through be deleted without a word.
    /// </summary>
    [Fact]
    public void The_reader_asks_the_machine_when_nobody_has_stood_in_for_it()
    {
        var invented = new VolumeUsage("nowhere", "Invented", "none", 1, 1);

        VolumeProperties.Reader = _ => invented;

        Assert.Equal(invented, VolumeProperties.Read(Path.GetTempPath()));

        VolumeProperties.Reader = null;

        var real = VolumeProperties.Read(Path.GetTempPath());

        Assert.NotNull(real);
        Assert.NotEqual(invented, real!.Value);
    }
}
