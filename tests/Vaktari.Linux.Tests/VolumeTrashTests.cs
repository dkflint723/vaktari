using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Which trash a delete goes to.
///
/// **Everything went to the home trash.** Deleting a twenty-gigabyte video off
/// a USB stick therefore COPIED twenty gigabytes onto the home partition —
/// slowly, filling a disk the user was not deleting from. The entry then
/// survived the stick being unplugged, and restoring it failed because the
/// original path had gone with the drive. The settings page has told people the
/// opposite all along: "Files deleted from another drive live in a trash on
/// that drive".
/// </summary>
public sealed class VolumeTrashTests
{
    /// <summary>
    /// The spec's fallback spelling, which is what Dolphin and Nautilus create
    /// and read: <c>.Trash-$uid</c> at the top of the volume.
    /// </summary>
    [Fact]
    public void A_volume_gets_its_own_trash_at_its_top()
    {
        if (!OperatingSystem.IsLinux()) return;

        var mount = Path.Combine(Path.GetTempPath(), "vaktari-fake-mount");
        Directory.CreateDirectory(mount);

        try
        {
            var trash = XdgTrash.VolumeTrash(mount);

            Assert.StartsWith(Path.Combine(mount, ".Trash-"), trash);

            // The uid, not a name or a guess.
            var suffix = Path.GetFileName(trash)[".Trash-".Length..];
            Assert.True(uint.TryParse(suffix, out _), $"expected a uid, got '{suffix}'");
        }
        finally
        {
            try { Directory.Delete(mount, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **A file on the home volume still goes to the home trash**, which is the
    /// overwhelmingly common case and the one place a per-volume directory
    /// would be wrong — nobody wants a .Trash-1000 appearing at the root of
    /// their system disk.
    /// </summary>
    [Fact]
    public void A_file_on_the_home_volume_uses_the_home_trash()
    {
        if (!OperatingSystem.IsLinux()) return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var file = Path.Combine(home, "notes.txt");

        Assert.Equal(XdgTrash.TrashRoot, XdgTrash.RootFor(file));
    }

    /// <summary>
    /// The home trash is always among the roots the listing sweeps, even before
    /// anything has been deleted anywhere else.
    /// </summary>
    [Fact]
    public void The_home_trash_is_always_one_of_the_roots()
    {
        var roots = XdgTrash.AllRoots().ToList();

        Assert.Contains(XdgTrash.TrashRoot, roots);
    }

    /// <summary>
    /// **Only trashes that exist are listed.** Naming one on every mounted
    /// volume would have a read of the trash create directories on read-only
    /// media, and on every stick the user merely plugged in.
    /// </summary>
    [Fact]
    public void No_trash_directory_is_created_merely_by_listing()
    {
        var before = XdgTrash.AllRoots().ToList();

        // Asked twice: if the first call created anything, the second would see
        // more roots than the first.
        var after = XdgTrash.AllRoots().ToList();

        Assert.Equal(before.Count, after.Count);

        foreach (var root in after.Skip(1))
            Assert.True(Directory.Exists(root), $"{root} was listed but does not exist");
    }
}
