using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What the trash does with a link when a rename will not do.
///
/// **It followed them.** The copy that stands in for a rename across devices
/// went through every link it met: Directory.EnumerateDirectories hands back
/// a link to a folder as a folder, File.Copy reads through a link to a file,
/// and File.Move's own fallback does the same for a link deleted on its own.
/// So a folder holding a link to a photo library, on its way to a bin on
/// another filesystem, arrived holding the library; a link pointing back up
/// its own folder was descended into forty times — the kernel's limit — and
/// then failed the whole folder, eighty empty folders later; and a link to
/// nothing failed the folder outright. Restoring took the same road back.
///
/// A link is now one entry, remade from the text it held, and nothing it
/// points at is copied. The first tests call the copy directly, as the xattr
/// test does; the last three reach it through MoveAcrossDevices on a runner
/// that has two filesystems to rename between, and skip on one that has not.
/// </summary>
public sealed class TrashCopyLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-trashlink-" + Guid.NewGuid().ToString("N")[..8]);

    // Beside the root rather than under it, so a link to ".." inside the
    // folder being copied reaches a parent holding nothing but that folder.
    private readonly string _landing = Path.Combine(
        Path.GetTempPath(), "vaktari-trashlanding-" + Guid.NewGuid().ToString("N")[..8]);

    // On the other filesystem, made only by the tests that need it.
    private string? _elsewhere;

    public TrashCopyLinkTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_landing);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _landing, _elsewhere })
        {
            if (dir is null) continue;

            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }

        GC.SuppressFinalize(this);
    }

    private string Trip => Path.Combine(_root, "Trip");

    private string Landed => Path.Combine(_landing, "Trip");

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(Trip, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A library beside the folder, with one thing in it.</summary>
    private string Library()
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "big.bin"), "every photo there is");
        return library;
    }

    /// <summary>What a name in the copy points at, or null for an ordinary entry.</summary>
    private string? PointsAt(string name)
        => new FileInfo(Path.Combine(Landed, name)).LinkTarget;

    private static List<string> Names(string dir)
        => new DirectoryInfo(dir).EnumerateFileSystemInfos().Select(e => e.Name).Order().ToList();

    /// <summary>A path on the other filesystem, under a folder of this test's own.</summary>
    private string Across(string name)
    {
        _elsewhere ??= Directory.CreateDirectory(Path.Combine(
            CrossDeviceFactAttribute.Elsewhere!,
            "vaktari-trashlink-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        return Path.Combine(_elsewhere, name);
    }

    // ---- the copy itself ------------------------------------------------------

    [PosixFact]
    public void A_link_to_a_file_is_reproduced_as_a_link()
    {
        Write("real.txt", "payload");
        File.CreateSymbolicLink(Path.Combine(Trip, "alias.txt"), "real.txt");

        XdgTrash.CopyDirectory(Trip, Landed);

        Assert.Equal("real.txt", PointsAt("alias.txt"));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(Landed, "real.txt")));
    }

    /// <summary>
    /// **The library is not copied.** The link is one entry in the copy, and
    /// what it points at stays where it was, however large.
    /// </summary>
    [PosixFact]
    public void A_link_to_a_folder_is_reproduced_and_the_folder_is_not_copied()
    {
        var library = Library();

        Write("note.txt");
        Directory.CreateSymbolicLink(Path.Combine(Trip, "photos"), library);

        XdgTrash.CopyDirectory(Trip, Landed);

        Assert.Equal(library, PointsAt("photos"));

        // Two entries, the note and the link — not a folder named photos with
        // the library's contents inside it.
        Assert.Equal(["note.txt", "photos"], Names(Landed));
    }

    /// <summary>
    /// **A link to nothing is still an entry.** It used to be handed to
    /// File.Copy, which followed it and threw, and the throw failed the
    /// whole folder: one dead shortcut made the folder impossible to delete
    /// across devices.
    /// </summary>
    [PosixFact]
    public void A_link_to_nothing_is_reproduced_rather_than_failing_the_folder()
    {
        Write("note.txt");
        File.CreateSymbolicLink(Path.Combine(Trip, "gone"), "nowhere");

        XdgTrash.CopyDirectory(Trip, Landed);

        Assert.Equal("nowhere", PointsAt("gone"));
        Assert.True(File.Exists(Path.Combine(Landed, "note.txt")), "the rest of the folder was not copied");
    }

    /// <summary>
    /// **Followed, a link back up the folder was descended into forty times**
    /// — the kernel's limit on links in one path — with an empty folder written
    /// at each of the eighty levels, no file copied, and then the whole folder
    /// refused with "Too many levels of symbolic links". Measured.
    /// </summary>
    [PosixFact]
    public void A_link_back_up_the_folder_is_reproduced_rather_than_followed()
    {
        Write("map.png");
        Directory.CreateSymbolicLink(Path.Combine(Trip, "up"), "..");

        XdgTrash.CopyDirectory(Trip, Landed);

        Assert.Equal("..", PointsAt("up"));
        Assert.Equal(["map.png", "up"], Names(Landed));
    }

    // ---- and through the rename that will not do ------------------------------

    /// <summary>
    /// **File.Move's own fallback read through the link** and wrote a file
    /// holding what the link's target held.
    /// </summary>
    [CrossDeviceFact]
    public void A_link_to_a_file_taken_across_devices_stays_a_link()
    {
        Write("real.txt", "payload");
        var alias = Path.Combine(Trip, "alias.txt");
        File.CreateSymbolicLink(alias, "real.txt");

        var there = Across("alias.txt");

        XdgTrash.MoveAcrossDevices(alias, there);

        Assert.Equal("real.txt", new FileInfo(there).LinkTarget);
        Assert.False(File.Exists(alias), "the link was left behind at the source");
        Assert.Equal("payload", File.ReadAllText(Path.Combine(Trip, "real.txt")));
    }

    /// <summary>
    /// **Directory.Exists said the link was a folder**, so it was copied as
    /// one — the library's contents, under the link's name — and the library
    /// itself was then in the way of nothing, since only the link was deleted.
    /// </summary>
    [CrossDeviceFact]
    public void A_link_to_a_folder_taken_across_devices_stays_a_link()
    {
        var library = Library();
        var photos = Path.Combine(Trip, "photos");
        Directory.CreateDirectory(Trip);
        Directory.CreateSymbolicLink(photos, library);

        var there = Across("photos");

        XdgTrash.MoveAcrossDevices(photos, there);

        Assert.Equal(library, new FileInfo(there).LinkTarget);
        Assert.False(Directory.Exists(photos) || File.Exists(photos), "the link was left behind at the source");
        Assert.Equal("every photo there is", File.ReadAllText(Path.Combine(library, "big.bin")));
    }

    /// <summary>The whole road: a folder with a link inside, renamed across devices.</summary>
    [CrossDeviceFact]
    public void A_folder_holding_a_link_taken_across_devices_keeps_the_link()
    {
        Write("real.txt", "payload");
        File.CreateSymbolicLink(Path.Combine(Trip, "alias.txt"), "real.txt");

        var there = Across("Trip");

        XdgTrash.MoveAcrossDevices(Trip, there);

        Assert.Equal("real.txt", new FileInfo(Path.Combine(there, "alias.txt")).LinkTarget);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(there, "real.txt")));
        Assert.False(Directory.Exists(Trip), "the folder was left behind at the source");
    }
}
