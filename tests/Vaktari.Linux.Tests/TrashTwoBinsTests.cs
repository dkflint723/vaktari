using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Two trashes holding one name.
///
/// **A trash key was the bare name, and a name is only unique in one trash.**
/// A file deleted from the home folder and another of the same name deleted
/// from a stick both became "notes.txt", one in each trash — and every route
/// that took a key found the wrong one. Restore took the first trash holding
/// the name, which is always the home one; deleting a row for good destroyed
/// whichever item of that name the bin listed first, the newest. Ctrl+Z after
/// deleting from the stick went through Restore too, and brought back the
/// other file.
///
/// The second trash is a folder shaped like one at the top of a volume,
/// $topdir/.Trash-1000, handed to the listing through a seam — two real
/// volumes are not something a test can arrange.
/// </summary>
public sealed class TrashTwoBinsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-twobins-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    private string Home => Path.Combine(_root, "data");
    private string Stick => Path.Combine(_root, "stick");
    private string StickTrash => Path.Combine(Stick, ".Trash-1000");

    public TrashTwoBinsTests()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Home);
        XdgTrash.ExtraRoots = () => [StickTrash];

        // The home item: deleted from a folder under this test's root, the day
        // before the stick's.
        Binned(Path.Combine(Home, "Trash"), Path.Combine(_root, "desk", "notes.txt"),
               "2026-09-01T10:00:00", "from the desk");

        // The stick's: recorded relative to the top of its volume, as the
        // spec says and as Dolphin writes it.
        Binned(StickTrash, "notes.txt", "2026-09-02T10:00:00", "from the stick");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _before);
        XdgTrash.ExtraRoots = null;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private static void Binned(string trash, string recorded, string when, string content)
    {
        Directory.CreateDirectory(Path.Combine(trash, "files"));
        Directory.CreateDirectory(Path.Combine(trash, "info"));

        File.WriteAllText(Path.Combine(trash, "files", "notes.txt"), content);
        File.WriteAllText(Path.Combine(trash, "info", "notes.txt.trashinfo"),
                          $"[Trash Info]\nPath={recorded}\nDeletionDate={when}\n");
    }

    private static TrashedItem Item(XdgTrashMaintenance bin, string originalEndsWith)
        => bin.List().Single(i => i.OriginalPath.EndsWith(originalEndsWith, StringComparison.Ordinal));

    [Fact]
    public void Both_items_are_listed_under_keys_that_differ()
    {
        var items = new XdgTrashMaintenance().List();

        Assert.Equal(2, items.Count);
        Assert.NotEqual(items[0].TrashName, items[1].TrashName);
    }

    /// <summary>
    /// **Deleting the older row for good destroys the older item.** Matched by
    /// name, the newest "notes.txt" in any trash — the stick's — went instead,
    /// and the row that was picked stayed on screen.
    /// </summary>
    [Fact]
    public void Deleting_one_destroys_that_one()
    {
        var bin = new XdgTrashMaintenance();
        var home = Item(bin, Path.Combine("desk", "notes.txt"));

        bin.Delete(home.TrashName);

        Assert.False(File.Exists(Path.Combine(Home, "Trash", "files", "notes.txt")),
                     "the item that was named is still in its trash");
        Assert.True(File.Exists(Path.Combine(StickTrash, "files", "notes.txt")),
                    "an item that was not named was destroyed");
    }

    /// <summary>
    /// **And the key a delete hands its undo names the trash it went to.** A
    /// bare name was looked for in the home trash first, so Ctrl+Z after a
    /// delete from a stick restored the home item of that name instead. Only a
    /// real second volume sends a delete anywhere but home, which a test cannot
    /// mount — so this pins what the key is: the item's own .trashinfo.
    /// </summary>
    [PosixFact]
    public void A_deletes_key_is_its_own_info_file()
    {
        var file = Path.Combine(_root, "desk", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "report");

        var key = XdgTrash.Trash(file);

        Assert.Equal(Path.Combine(Home, "Trash", "info", "report.txt.trashinfo"), key);
        Assert.Equal(file, XdgTrash.Restore(key));
    }

    /// <summary>
    /// **Restoring the stick's row brings back the stick's file.** By name,
    /// Restore took the home trash first and brought back the other one.
    /// </summary>
    [Fact]
    public void Restoring_one_brings_back_that_one()
    {
        var bin = new XdgTrashMaintenance();
        var stick = Item(bin, Path.Combine("stick", "notes.txt"));

        var landed = XdgTrash.Restore(stick.TrashName);

        Assert.Equal(Path.Combine(Stick, "notes.txt"), landed);
        Assert.Equal("from the stick", File.ReadAllText(landed));
        Assert.True(File.Exists(Path.Combine(Home, "Trash", "files", "notes.txt")),
                    "the home item left its trash");
    }
}
