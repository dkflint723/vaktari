using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Where a trashed file says it came from.
///
/// **An absolute path on a removable volume records where the stick was mounted
/// THAT time.** /run/media/me/USB today, /media/USB1 tomorrow — and a restore
/// then puts the file back at a path on some other filesystem, or nowhere at
/// all. The spec allows the path to be relative to the volume for exactly this
/// reason.
///
/// The reading half matters more than the writing half: gvfs and Dolphin both
/// write a relative path for a trash on a removable drive, so anything trashed
/// on a stick by another file manager already carries one — and reading it raw
/// resolves it against this process's working directory, which is somewhere in
/// the user's home.
/// </summary>
public sealed class TrashInfoPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-trashinfo-" + Guid.NewGuid().ToString("N")[..8]);

    public TrashInfoPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>
    /// Compared with one separator, because separators are not what these are
    /// about. The code under test runs on Linux, where "/" is the only one —
    /// but this suite also runs on the Windows agents, where Path.Combine
    /// writes "\\" and a path read out of a trashinfo file keeps the "/" it
    /// was written with. Asserting on the spelling would fail there while the
    /// resolution being tested is exactly right.
    /// </summary>
    private static string Same(string? path)
        => path?.Replace(Path.DirectorySeparatorChar, '/') ?? "";

    // ---- which volume a trash belongs to ------------------------------------

    /// <summary>The spelling every desktop actually creates.</summary>
    [Fact]
    public void A_per_volume_trash_knows_its_volume()
    {
        var volume = At("run", "media", "me", "USB");

        Assert.Equal(Same(volume), Same(XdgTrash.TopDirOf(Path.Combine(volume, ".Trash-1000"))));
    }

    /// <summary>And the administrator-made one, which is a level deeper.</summary>
    [Fact]
    public void And_so_does_the_shared_one()
    {
        var volume = At("mnt", "data");

        Assert.Equal(Same(volume), Same(XdgTrash.TopDirOf(Path.Combine(volume, ".Trash", "1000"))));
    }

    /// <summary>
    /// **The home trash has no volume to be relative to**, and must keep
    /// writing absolute paths — everything already in it says where it came
    /// from that way.
    /// </summary>
    [Theory]
    [InlineData("/home/me/.local/share/Trash")]
    [InlineData("/home/me/.local/share/Trash-1000")]
    [InlineData("/mnt/data/.Trash-notanumber")]
    [InlineData("/mnt/data/.Trash/notanumber")]
    [InlineData("/mnt/data/Trash-1000")]
    public void Anything_that_is_not_a_volume_trash_has_no_top(string root)
        => Assert.Null(XdgTrash.TopDirOf(root));

    // ---- writing one --------------------------------------------------------

    /// <summary>
    /// The whole point of the writing half: what goes in the file is where the
    /// file sits ON THE VOLUME, not where the volume happened to be mounted.
    /// </summary>
    [Fact]
    public void A_file_on_a_volume_records_where_it_sits_on_that_volume()
    {
        var volume = At("run", "media", "me", "USB");

        Assert.Equal(
            Same(Path.Combine("photos", "notes.txt")),
            Same(XdgTrash.RecordedPath(
                Path.Combine(volume, "photos", "notes.txt"),
                Path.Combine(volume, ".Trash-1000"))));
    }

    /// <summary>
    /// The home trash keeps writing absolute paths. It has no volume to be
    /// relative to, and everything already in it says where it came from that
    /// way — a reader cannot tell the two apart except by whether the path is
    /// rooted.
    /// </summary>
    [Fact]
    public void A_file_in_the_home_trash_records_its_whole_path()
    {
        var home = At("home", "me");
        var file = Path.Combine(home, "notes.txt");

        Assert.Equal(file, XdgTrash.RecordedPath(
            file, Path.Combine(home, ".local", "share", "Trash")));
    }

    /// <summary>
    /// **And a path that is not inside the volume at all keeps its own.**
    /// GetRelativePath answers with ".." rather than refusing, so a bind mount
    /// or a symlinked tree can produce a file whose trash lives on one volume
    /// and whose home is on another — and "../../etc/thing" resolved against a
    /// stick on the next machine is a path pointing anywhere.
    /// </summary>
    [Fact]
    public void A_file_outside_the_volume_keeps_its_whole_path()
    {
        var file = At("elsewhere", "notes.txt");

        Assert.Equal(file, XdgTrash.RecordedPath(
            file, Path.Combine(At("run", "media", "me", "USB"), ".Trash-1000")));
    }

    /// <summary>
    /// **The round trip, which is the only thing that really matters.** The two
    /// halves are written apart and have to agree: a writer that records a
    /// relative path beside a reader that resolves against a different
    /// directory restores to the wrong place just as surely as recording an
    /// absolute one did. This is also the only test that touches the line that
    /// actually writes the file.
    /// </summary>
    [Fact]
    public void A_file_trashed_on_a_stick_still_restores_after_it_is_remounted()
    {
        // Where the stick was when the file was deleted.
        var monday = At("run", "media", "me", "USB");
        var root = Path.Combine(monday, ".Trash-1000");
        var file = Path.Combine(monday, "my photos", "notes.txt");

        Directory.CreateDirectory(Path.Combine(root, "info"));

        var name = XdgTrash.ReserveName("notes.txt", file, root);

        // **And where it is on Tuesday**, which is the whole finding: an
        // absolute path records the mount point of the day, and every restore
        // after that aims at a path on some other filesystem or at nothing.
        // Nothing about the stick changed — only where the desktop put it.
        var tuesday = At("media", "USB1");

        Directory.CreateDirectory(Path.GetDirectoryName(tuesday)!);
        Directory.Move(monday, tuesday);

        var info = Path.Combine(tuesday, ".Trash-1000", "info", name + ".trashinfo");

        Assert.Equal(
            Same(Path.Combine(tuesday, "my photos", "notes.txt")),
            Same(XdgTrash.OriginalPathOf(info)));
    }

    /// <summary>And the home trash round-trips too, unchanged.</summary>
    [Fact]
    public void What_is_written_in_the_home_trash_is_read_back_unchanged()
    {
        var root = At("home", ".local", "share", "Trash");
        var file = At("home", "me", "notes.txt");

        Directory.CreateDirectory(Path.Combine(root, "info"));

        var name = XdgTrash.ReserveName("notes.txt", file, root);

        Assert.Equal(
            Same(file),
            Same(XdgTrash.OriginalPathOf(Path.Combine(root, "info", name + ".trashinfo"))));
    }

    // ---- reading one --------------------------------------------------------

    private string Info(string root, string name, string recorded)
    {
        Directory.CreateDirectory(Path.Combine(root, "info"));

        var path = Path.Combine(root, "info", name + ".trashinfo");

        File.WriteAllText(
            path, $"[Trash Info]\nPath={recorded}\nDeletionDate=2026-01-01T00:00:00\n");

        return path;
    }

    /// <summary>
    /// The case this whole file is about: a stick trashed elsewhere, read here.
    /// </summary>
    [Fact]
    public void A_relative_path_is_resolved_against_its_own_volume()
    {
        var volume = At("run", "media", "me", "USB");
        var info = Info(Path.Combine(volume, ".Trash-1000"), "notes.txt", "photos/notes.txt");

        Assert.Equal(
            Same(Path.Combine(volume, "photos", "notes.txt")),
            Same(XdgTrash.OriginalPathOf(info)));
    }

    /// <summary>
    /// And an absolute one is left exactly as it is — every info file already
    /// written carries one, and the home trash goes on writing them.
    /// </summary>
    [Fact]
    public void An_absolute_path_is_left_alone()
    {
        var info = Info(At("home", ".local", "share", "Trash"), "notes.txt",
                        "/home/me/notes.txt");

        Assert.Equal("/home/me/notes.txt", XdgTrash.OriginalPathOf(info));
    }

    /// <summary>
    /// **Percent-decoding still happens first.** The path is encoded in the
    /// file, so a folder with a space in it arrives as "my%20photos" — and
    /// joining that to the volume would restore into a directory of that
    /// literal name.
    /// </summary>
    [Fact]
    public void A_relative_path_is_decoded_before_it_is_joined()
    {
        var volume = At("run", "media", "me", "USB");
        var info = Info(Path.Combine(volume, ".Trash-1000"), "a.txt", "my%20photos/a.txt");

        Assert.Equal(
            Same(Path.Combine(volume, "my photos", "a.txt")),
            Same(XdgTrash.OriginalPathOf(info)));
    }

    /// <summary>
    /// A relative path in a trash that has no volume — a malformed layout, or
    /// the home trash — is handed back rather than joined to a guess.
    /// </summary>
    [Fact]
    public void A_relative_path_with_no_volume_behind_it_is_returned_as_it_stands()
    {
        var info = Info(At("home", ".local", "share", "Trash"), "a.txt", "photos/a.txt");

        Assert.Equal(Same(Path.Combine("photos", "a.txt")), Same(XdgTrash.OriginalPathOf(info)));
    }

    /// <summary>
    /// An empty Path stays empty. Joined to the volume it would name the top of
    /// the drive — a real folder, and the wrong one, which a restore aimed at
    /// would write over.
    /// </summary>
    [Fact]
    public void An_empty_path_is_not_read_as_the_volume_itself()
    {
        var volume = At("run", "media", "me", "USB");
        var info = Info(Path.Combine(volume, ".Trash-1000"), "a.txt", "");

        Assert.Equal("", XdgTrash.OriginalPathOf(info));
    }

    /// <summary>An info file with no Path line at all answers nothing.</summary>
    [Fact]
    public void An_info_file_with_no_path_answers_nothing()
    {
        Directory.CreateDirectory(At("Trash", "info"));

        var path = At("Trash", "info", "a.txt.trashinfo");
        File.WriteAllText(path, "[Trash Info]\nDeletionDate=2026-01-01T00:00:00\n");

        Assert.Null(XdgTrash.OriginalPathOf(path));
    }
}
