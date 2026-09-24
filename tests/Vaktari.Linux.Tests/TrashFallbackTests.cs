using System.Runtime.Versioning;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What a move into the bin does when it cannot simply be a rename.
///
/// **A folder could be emptied and its only copy hidden.** Across devices the
/// move is a copy and then a delete. Any refused rename took that route —
/// including one refused because the parent folder cannot be written, which a
/// copy does not get round — and when the delete failed partway the catch
/// removed the entry's info file, although the whole folder was by then in the
/// trash. Nothing lists a payload without its info file, so the files were gone
/// from their folder and in no bin anybody could see.
///
/// Now: only a cross-device refusal is copied; a copy that fails takes its
/// partial result away; and an entry whose payload reached the bin keeps its
/// info file, listed and restorable, with what was left behind reported.
/// Permissions and two filesystems need real Linux, and say so.
/// </summary>
public sealed class TrashFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-trashfb-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    private string? _elsewhere;

    public TrashFallbackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _before);
        XdgTrash.MountPointsOverride = null;

        foreach (var dir in new[] { _root, _elsewhere })
        {
            if (dir is null) continue;

            if (OperatingSystem.IsLinux()) Unlock(dir);

            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Gives every folder under <paramref name="dir"/> its write bit back, so it can be cleaned up.</summary>
    [SupportedOSPlatform("linux")]
    private static void Unlock(string dir)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var folder in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Prepend(dir))
        {
            try { File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception) { }
        }
    }

    private string Write(string relative, string text = "x", string? under = null)
    {
        var path = Path.Combine(under ?? _root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// **A refused rename is not a reason to copy.** The parent cannot be
    /// written, so the folder cannot leave it — and it could not be removed
    /// after a copy either. The old route copied it, deleted everything inside
    /// it, and then failed to remove the folder itself: emptied in place.
    /// </summary>
    [PosixFact]
    [SupportedOSPlatform("linux")]
    public void A_rename_refused_for_permission_is_not_copied_and_emptied()
    {
        var kept = Write("parent/site/a.txt", "the only copy");
        var parent = Path.Combine(_root, "parent");
        var destination = Path.Combine(_root, "bin-files", "site");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.ThrowsAny<IOException>(
                () => XdgTrash.MoveAcrossDevices(Path.Combine(parent, "site"), destination));
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(File.Exists(kept), "the folder was emptied");
        Assert.False(Directory.Exists(destination), "a copy was left in the bin");
    }

    /// <summary>
    /// **A payload that reached the bin keeps its info file.** Across devices,
    /// with a folder inside that cannot be emptied: the copy lands whole, the
    /// delete stops partway, and the entry must stay listed — with the key it
    /// can be restored by — rather than vanish with the only complete copy.
    /// </summary>
    [CrossDeviceFact]
    [SupportedOSPlatform("linux")]
    public void A_complete_copy_keeps_its_info_file_when_the_source_will_not_empty()
    {
        _elsewhere = Directory.CreateDirectory(Path.Combine(
            CrossDeviceFactAttribute.Elsewhere!, "vaktari-trashfb-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        var site = Path.Combine(_elsewhere, "site");
        Write("top.txt", "top", site);
        Write("locked/deep.txt", "deep", site);

        var locked = Path.Combine(site, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var files = Directory.CreateDirectory(Path.Combine(_root, "Trash", "files")).FullName;
        var info = Directory.CreateDirectory(Path.Combine(_root, "Trash", "info")).FullName;
        var infoPath = Path.Combine(info, "site.trashinfo");
        File.WriteAllText(infoPath, "[Trash Info]\nPath=/somewhere/site\nDeletionDate=2026-09-01T10:00:00\n");

        var destination = Path.Combine(files, "site");

        var partly = Assert.Throws<XdgTrash.PartlyTrashedException>(
            () => XdgTrash.MoveIntoTrash(site, destination, infoPath));

        Assert.Equal(infoPath, partly.Key);
        Assert.True(File.Exists(infoPath), "the info file of a complete copy was removed");
        Assert.Equal("deep", File.ReadAllText(Path.Combine(destination, "locked", "deep.txt")));
        Assert.True(File.Exists(Path.Combine(locked, "deep.txt")), "the leftover it reports is not there");
    }

    /// <summary>
    /// And a move that got nothing into the bin takes its info file away, as
    /// ever — that is the phantom entry the removal was written for.
    /// </summary>
    [Fact]
    public void A_move_that_got_nothing_across_drops_its_info_file()
    {
        var infoPath = Write("Trash/info/gone.trashinfo", "[Trash Info]\nPath=/x/gone\n");
        var destination = Path.Combine(_root, "Trash", "files", "gone");

        Assert.ThrowsAny<IOException>(
            () => XdgTrash.MoveIntoTrash(Path.Combine(_root, "does-not-exist"), destination, infoPath));

        Assert.False(File.Exists(infoPath));
    }

    /// <summary>
    /// **A volume's trash is readable by its owner alone.** It was made as
    /// the umask allowed, 0755 as a rule, so a file deleted from a private
    /// folder on a shared volume could be read out of the trash by anyone.
    /// </summary>
    [PosixFact]
    public void A_volume_trash_is_made_private()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));

        var volume = Path.Combine(_root, "volume", ".Trash-1000");
        Directory.CreateDirectory(Path.GetDirectoryName(volume)!);

        Assert.Equal(volume, XdgTrash.PrepareRoot(volume));

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        foreach (var folder in new[] { volume, Path.Combine(volume, "files"), Path.Combine(volume, "info") })
            Assert.Equal(Private, File.GetUnixFileMode(folder) & (UnixFileMode)0x1FF);
    }

    /// <summary>
    /// **One an earlier build left readable by everyone is closed up.**
    /// </summary>
    [PosixFact]
    public void An_existing_open_volume_trash_is_made_private()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));

        var volume = Directory.CreateDirectory(Path.Combine(_root, "volume", ".Trash-1000")).FullName;
        File.SetUnixFileMode(volume, (UnixFileMode)0x1ED); // 0755

        Assert.Equal(volume, XdgTrash.PrepareRoot(volume));
        Assert.Equal((UnixFileMode)0x1C0, File.GetUnixFileMode(volume) & (UnixFileMode)0x1FF); // 0700
    }

    /// <summary>
    /// The owner is read from the entry itself: root owns "/", and this user
    /// owns the folder it just made. What pins the st_uid offset.
    /// </summary>
    [PosixFact]
    public void The_owner_read_is_the_real_one()
    {
        Assert.Equal(0u, FileIdentity.OwnerOf("/"));

        using var id = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", "-u")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        var uid = uint.Parse(id.StandardOutput.ReadToEnd().Trim(), System.Globalization.CultureInfo.InvariantCulture);
        id.WaitForExit();

        Assert.Equal(uid, FileIdentity.OwnerOf(_root));

        // Group and owner told apart: a folder given another of this user's
        // groups still reads as owned by the user. Where the user has no
        // other group, owner and group are one number and this says nothing.
        var groups = Run("id", "-G").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var primary = Run("id", "-g");

        if (groups.FirstOrDefault(g => g != primary) is { } other)
        {
            var grouped = Directory.CreateDirectory(Path.Combine(_root, "grouped")).FullName;
            Run("chgrp", other, grouped);
            Assert.Equal(uid, FileIdentity.OwnerOf(grouped));
        }

        // And a real folder another user owns is not this user's trash.
        if (uid != 0) Assert.False(XdgTrash.Mine("/"));
    }

    private static string Run(string program, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    /// <summary>
    /// **A filesystem that makes owners up is not someone else's planting.**
    /// A share mounted without uid= says root owns everything, the trash just
    /// made included; that trash is owned like the volume's top, and kept.
    /// One owned by another user who does not own the top is not.
    /// </summary>
    [PosixFact]
    public void A_trash_owned_like_its_volume_is_kept_and_one_owned_otherwise_is_not()
    {
        var top = Directory.CreateDirectory(Path.Combine(_root, "share")).FullName;
        var trash = Directory.CreateDirectory(Path.Combine(top, ".Trash-1000")).FullName;

        try
        {
            XdgTrash.OwnerOverride = path => path == top ? 0u : 0u;
            Assert.True(XdgTrash.Mine(trash));

            XdgTrash.OwnerOverride = path => path == top ? 0u : 4242u;
            Assert.False(XdgTrash.Mine(trash));
        }
        finally
        {
            XdgTrash.OwnerOverride = null;
        }
    }

    /// <summary>
    /// **And one that is not a folder of this user's own is not used.** A
    /// .Trash-1000 planted by someone else — here a link to a folder of
    /// theirs — took every delete from the volume. The home trash is used
    /// instead, and nothing is made inside the planted one.
    /// </summary>
    [PosixFact]
    public void A_planted_volume_trash_is_refused()
    {
        var data = Path.Combine(_root, "data");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);

        var theirs = Directory.CreateDirectory(Path.Combine(_root, "theirs")).FullName;
        var planted = Path.Combine(_root, "volume", ".Trash-1000");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        Directory.CreateSymbolicLink(planted, theirs);

        Assert.Equal(Path.Combine(data, "Trash"), XdgTrash.PrepareRoot(planted));
        Assert.Empty(Directory.EnumerateFileSystemEntries(theirs));
    }

    /// <summary>
    /// **A payload with no info file does not lend its name.** One left in
    /// files/ — another program's crash — held "report.txt"; the move onto it
    /// failed, and the failure was taken for an arrival, so the stranger was
    /// listed as this file and restored in its place while the file itself
    /// never moved.
    /// </summary>
    [PosixFact]
    public void A_payload_without_an_info_file_does_not_take_the_name()
    {
        var data = Path.Combine(_root, "data");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);

        var stranger = Write("Trash/files/report.txt", "a stranger", under: data);
        var mine = Write("desk/report.txt", "mine");

        var key = XdgTrash.Trash(mine);

        Assert.False(File.Exists(mine), "the file did not go to the bin");
        Assert.NotEqual(Path.Combine(data, "Trash", "info", "report.txt.trashinfo"), key);
        Assert.Equal("a stranger", File.ReadAllText(stranger));

        Assert.Equal(mine, XdgTrash.Restore(key));
        Assert.Equal("mine", File.ReadAllText(mine));
    }

    /// <summary>
    /// **Nor is one reached through a linked folder.** The mount table holds
    /// real paths, and "net/nas" is not one of them though "real/nas" is.
    /// </summary>
    [PosixFact]
    public void A_mount_point_reached_through_a_link_is_refused()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));

        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var kept = Write("real/nas/photo.jpg", "a photo");
        var net = Path.Combine(_root, "net");
        Directory.CreateSymbolicLink(net, real);

        var mounted = Path.Combine(FileIdentity.RealPath(real)!, "nas");
        XdgTrash.MountPointsOverride = () => [mounted];

        Assert.ThrowsAny<IOException>(() => XdgTrash.Trash(Path.Combine(net, "nas")));
        Assert.True(File.Exists(kept));
    }

    /// <summary>
    /// **A mounted drive is not binned.** Its trash would be inside it; the copy
    /// walked into its own destination until the path ran out. Refused before
    /// anything is written — no info file, no trash folder, nothing moved.
    /// </summary>
    [Fact]
    public void A_mount_point_is_refused_before_anything_is_written()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));

        var nas = Path.Combine(_root, "nas");
        var kept = Write("nas/photo.jpg", "a photo");

        XdgTrash.MountPointsOverride = () => [nas];

        var refused = Assert.ThrowsAny<IOException>(() => XdgTrash.Trash(nas));

        Assert.Contains("mounted", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(kept));
        Assert.Empty(Directory.EnumerateDirectories(nas));
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "Trash", "info"))
                     && Directory.EnumerateFiles(Path.Combine(_root, "data", "Trash", "info")).Any());
    }
}
