using System.Text;

using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What a copy has to carry besides the bytes, the times and the mode.
///
/// **Nothing in this repository ever called getxattr.** FileMetadata carried
/// times, Windows attributes and the unix mode; a grep for getxattr, setxattr
/// and listxattr found no P/Invoke at all, and the only sentence that mentioned
/// them pointed at a class that had been deleted. So every copy — and every
/// move across a filesystem — dropped the file's Baloo tags and its
/// user.xdg.origin.url, the address a download came from.
///
/// The syscalls sit behind three seams, so everything here except the syscalls
/// themselves runs on a Windows agent: which names are reproduced, what happens
/// to the ones that are not, which path is asked and which is written, and that
/// the write happens while there is something at the target to write to.
/// </summary>
public sealed class ExtendedAttributeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-xattr-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly Func<string, IReadOnlyList<string>>? _names = Xattrs.NamesOverride;
    private readonly Func<string, string, byte[]?>? _read = Xattrs.ReadOverride;
    private readonly Action<string, string, byte[]>? _write = Xattrs.WriteOverride;

    /// <summary>What the fake source is wearing. A null value is an attribute
    /// that listed but would not read.</summary>
    private readonly Dictionary<string, byte[]?> _on = new(StringComparer.Ordinal);

    /// <summary>Every path listxattr was asked about, and whether there was
    /// anything there at the time.</summary>
    private readonly List<(string Path, bool Existed)> _listed = [];

    /// <summary>Every (path, name) getxattr was asked for.</summary>
    private readonly List<(string Path, string Name)> _asked = [];

    /// <summary>Every setxattr, and whether the path it was aimed at existed
    /// when it happened — which is what the kernel needs and what an ordering
    /// mistake takes away.</summary>
    private readonly List<(string Path, string Name, byte[] Value, bool Existed)> _written = [];

    public ExtendedAttributeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Restored whatever happened: the suite runs one collection at a time,
        // but a seam left pointing at a dead dictionary would still poison
        // every class after this one.
        Xattrs.NamesOverride = _names;
        Xattrs.ReadOverride = _read;
        Xattrs.WriteOverride = _write;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Points all three seams at <see cref="_on"/>, <see cref="_listed"/>,
    /// <see cref="_asked"/> and <see cref="_written"/>.
    ///
    /// **The fakes answer for <paramref name="wearing"/> and for nothing
    /// else.** Fakes that threw their path argument away left the reading side
    /// entirely unpinned: swapping source for target in either call compiled
    /// and kept all 448 tests green, while on a real kernel either swap makes
    /// the whole feature a silent no-op, because a target that was just created
    /// has no user.* attributes to list or read.
    /// </summary>
    private void Fake(params string[] wearing)
    {
        Xattrs.NamesOverride = path =>
        {
            _listed.Add((path, Path.Exists(path)));

            return wearing.Contains(path, StringComparer.Ordinal) ? [.. _on.Keys] : [];
        };

        Xattrs.ReadOverride = (path, name) =>
        {
            _asked.Add((path, name));

            return wearing.Contains(path, StringComparer.Ordinal) ? _on[name] : null;
        };

        Xattrs.WriteOverride = (path, name, value)
            => _written.Add((path, name, value, Path.Exists(path)));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private string File_(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);

        System.IO.File.WriteAllText(path, content);

        return path;
    }

    // ---- which names travel --------------------------------------------------

    /// <summary>
    /// The three the desktop actually writes: Dolphin's tags and rating, and
    /// the download address the freedesktop specification puts on a fetched
    /// file.
    /// </summary>
    [Fact]
    public void The_desktops_own_attributes_are_carried()
    {
        Assert.True(Xattrs.Carried("user.xdg.tags"));
        Assert.True(Xattrs.Carried("user.xdg.origin.url"));
        Assert.True(Xattrs.Carried("user.baloo.rating"));
    }

    /// <summary>
    /// **And nothing else, which is a decision rather than an omission.**
    /// security.capability is where a setcap binary's capabilities live, so
    /// reproducing it would hand cap_net_raw to a duplicate because somebody
    /// pressed copy; security.selinux is assigned by policy on create;
    /// system.posix_acl_access is an ACL, whose bytes name ids that need not
    /// mean the same thing at the destination.
    /// </summary>
    [Fact]
    public void Nothing_outside_the_user_namespace_is()
    {
        Assert.False(Xattrs.Carried("security.capability"));
        Assert.False(Xattrs.Carried("security.selinux"));
        Assert.False(Xattrs.Carried("system.posix_acl_access"));
        Assert.False(Xattrs.Carried("trusted.overlay.opaque"));
    }

    /// <summary>
    /// listxattr answers with one buffer of NUL-terminated names — no count and
    /// no length prefix — and the last name's terminator must not become an
    /// empty fourth entry.
    /// </summary>
    [Fact]
    public void A_list_of_names_is_cut_at_every_nul()
        => Assert.Equal(
            ["user.xdg.tags", "user.xdg.origin.url", "security.selinux"],
            Xattrs.Split(Bytes("user.xdg.tags\0user.xdg.origin.url\0security.selinux\0")));

    /// <summary>
    /// **And a tail with no terminator is dropped rather than guessed at.** The
    /// kernel terminates every name it writes, so an unterminated remainder can
    /// only be a buffer that came up short — and half a name is worse than no
    /// name, because setxattr would take it and the copy would wear an
    /// attribute nobody named.
    /// </summary>
    [Fact]
    public void A_name_with_no_terminator_is_dropped()
        => Assert.Equal(["user.a"], Xattrs.Split(Bytes("user.a\0user.b")));

    // ---- what carrying does --------------------------------------------------

    /// <summary>The whole finding, in one assertion: the tag's own bytes, on
    /// the copy.</summary>
    [Fact]
    public void A_copied_files_tags_land_on_the_copy()
    {
        _on["user.xdg.tags"] = Bytes("Holiday\nReceipts");
        Fake("/src/photo.jpg");

        Xattrs.Carry("/src/photo.jpg", "/dst/photo.jpg");

        var written = Assert.Single(_written);

        Assert.Equal("/dst/photo.jpg", written.Path);
        Assert.Equal("user.xdg.tags", written.Name);
        Assert.Equal(Bytes("Holiday\nReceipts"), written.Value);
    }

    /// <summary>
    /// And on the copy only. Carry takes two paths and writes to one of them;
    /// crossing them would rewrite the original's attributes with its own,
    /// which looks like success until the source is the file that changed.
    /// </summary>
    [Fact]
    public void The_source_is_never_written()
    {
        _on["user.xdg.origin.url"] = Bytes("https://example.invalid/x.tar.gz");
        Fake("/src/x.tar.gz");

        Xattrs.Carry("/src/x.tar.gz", "/dst/x.tar.gz");

        Assert.DoesNotContain(_written, w => w.Path == "/src/x.tar.gz");
    }

    /// <summary>
    /// **And the source is the path both reads go to.** The one above pins only
    /// the write; asking the target for its names instead compiles, and on a
    /// real kernel it answers with the empty list a just-created file has — so
    /// every copy carries nothing and the whole feature is silently gone.
    /// </summary>
    [Fact]
    public void The_names_and_the_values_are_asked_of_the_source()
    {
        _on["user.xdg.tags"] = Bytes("Holiday");
        Fake("/src/photo.jpg");

        Xattrs.Carry("/src/photo.jpg", "/dst/photo.jpg");

        Assert.Equal(["/src/photo.jpg"], _listed.Select(l => l.Path));
        Assert.Equal([("/src/photo.jpg", "user.xdg.tags")], _asked);
    }

    /// <summary>The namespace rule, at the place it is applied.</summary>
    [Fact]
    public void An_attribute_outside_the_user_namespace_is_not_reproduced()
    {
        _on["security.capability"] = Bytes("cap_net_raw");
        _on["user.xdg.tags"] = Bytes("Tools");
        Fake("/src/ping");

        Xattrs.Carry("/src/ping", "/dst/ping");

        Assert.DoesNotContain(_written, w => w.Name == "security.capability");
        Assert.Contains(_written, w => w.Name == "user.xdg.tags");
    }

    /// <summary>
    /// **An attribute that would not read is not an empty attribute.** The list
    /// and the read are two syscalls, so a name can go away between them — and
    /// an empty value is itself legal, so writing one would put an attribute on
    /// the copy that the source does not have.
    /// </summary>
    [Fact]
    public void An_attribute_that_could_not_be_read_is_not_written_empty()
    {
        _on["user.gone"] = null;
        _on["user.xdg.tags"] = Bytes("Tools");
        Fake("/src/f");

        Xattrs.Carry("/src/f", "/dst/f");

        Assert.DoesNotContain(_written, w => w.Name == "user.gone");
        Assert.Contains(_written, w => w.Name == "user.xdg.tags");
    }

    /// <summary>
    /// **The run-time platform check, which the assembly attribute is not.**
    /// [SupportedOSPlatform("linux")] is a promise to the analyser; this suite
    /// runs the Linux copy engine on a Windows agent, where libc does not
    /// resolve and an unguarded listxattr throws DllNotFoundException out of
    /// the middle of a copy.
    ///
    /// On a real Linux box this asks the kernel about a plain temp file, which
    /// answers "no attributes" and gets the same silence.
    /// </summary>
    [Fact]
    public void Carrying_where_the_syscalls_are_missing_does_nothing()
    {
        var source = File_("plain.txt");
        var target = File_("plain-copy.txt");

        Assert.Null(Record.Exception(() => Xattrs.Carry(source, target)));
    }

    // ---- and the copy engine asks --------------------------------------------

    /// <summary>
    /// The call site. Runs the real engine, with only the syscalls faked, so
    /// nothing but the three libc calls is taken on trust.
    ///
    /// **And the target exists when the attribute is written.** setxattr needs
    /// an inode; a call moved to before the bytes are written answers ENOENT
    /// for every attribute and loses the feature with nothing to show for it
    /// but a Quiet line that is off by default. The order test below compares
    /// two statements in the source and cannot see that, so the precondition is
    /// captured at the moment of the write instead.
    /// </summary>
    [Fact]
    public async Task A_copy_carries_the_attributes_onto_the_new_file()
    {
        var source = File_("notes.txt", "hello");
        var destination = Path.Combine(_root, "elsewhere");

        Directory.CreateDirectory(destination);

        _on["user.xdg.tags"] = Bytes("Notes");
        Fake(source);

        await new LinuxFileOperations()
            .Copy([source], destination, _ => ValueTask.FromResult(ConflictResolution.Skip))
            .Completion;

        var landed = Path.Combine(destination, "notes.txt");

        var written = Assert.Single(
            _written, w => w.Path == landed && w.Name == "user.xdg.tags");

        Assert.True(written.Existed, "setxattr needs the copy to be there already");
    }

    /// <summary>
    /// A folder is recreated rather than copied, so the branch that makes it is
    /// the only place its own tags could travel — and Dolphin tags folders.
    /// </summary>
    [Fact]
    public async Task A_copied_folder_carries_its_own()
    {
        var folder = Path.Combine(_root, "Trip");
        var destination = Path.Combine(_root, "elsewhere");

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(destination);
        System.IO.File.WriteAllText(Path.Combine(folder, "map.png"), "x");

        _on["user.xdg.tags"] = Bytes("Holiday");
        Fake(folder);

        await new LinuxFileOperations()
            .Copy([folder], destination, _ => ValueTask.FromResult(ConflictResolution.Skip))
            .Completion;

        Assert.Contains(_written, w => w.Path == Path.Combine(destination, "Trip"));
    }

    /// <summary>
    /// **But a folder that was already there keeps its own.** "Overwrite" on a
    /// folder is a merge — the contents go in, the folder itself is the user's
    /// own object and not a copy of anything — and CreateDirectory on it does
    /// nothing, so carrying unconditionally re-tagged a folder the user had
    /// only agreed to merge into, replacing its Baloo tags with the source
    /// folder's.
    /// </summary>
    [Fact]
    public async Task Merging_onto_a_folder_that_is_already_there_leaves_its_own_alone()
    {
        var folder = Path.Combine(_root, "Trip");
        var destination = Path.Combine(_root, "elsewhere");
        var standing = Path.Combine(destination, "Trip");

        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(Path.Combine(folder, "map.png"), "x");
        Directory.CreateDirectory(standing);

        _on["user.xdg.tags"] = Bytes("Holiday");
        Fake(folder);

        await new LinuxFileOperations()
            .Copy([folder], destination, _ => ValueTask.FromResult(ConflictResolution.Overwrite))
            .Completion;

        Assert.DoesNotContain(_written, w => w.Path == standing);
    }

    // ---- and putting one back ------------------------------------------------

    /// <summary>
    /// **Undoing a move must not be the step that loses the tags.** Undo, the
    /// trash and the restore all go through MoveAcrossDevices, and its
    /// cross-device fallback is a byte copy: before this, a move between two
    /// drives kept the attributes and Ctrl+Z threw them away.
    ///
    /// On this agent the move is a rename inside one temp directory, which
    /// would have kept them regardless; what is pinned is that the routine
    /// itself puts them at the destination, which is the only thing that saves
    /// the crossing case — and that it read them while the source was still
    /// there, which is the only order File.Move leaves room for.
    /// </summary>
    [Fact]
    public void A_moved_file_arrives_wearing_its_attributes()
    {
        var source = File_("notes.txt");
        var destination = Path.Combine(_root, "moved.txt");

        _on["user.xdg.tags"] = Bytes("Notes");
        Fake(source);

        XdgTrash.MoveAcrossDevices(source, destination);

        Assert.Contains(_written, w => w.Path == destination && w.Name == "user.xdg.tags");
        Assert.All(_listed, l => Assert.True(l.Existed, "read before the move, not after"));
    }

    /// <summary>
    /// The folder half of the same routine: the folder's own attributes and
    /// every file's.
    ///
    /// Called directly. Reaching it through MoveAcrossDevices needs
    /// Directory.Move to refuse, which needs two filesystems, and this agent
    /// has one.
    /// </summary>
    [Fact]
    public void A_folder_copied_across_devices_carries_its_own_and_its_files()
    {
        var folder = Path.Combine(_root, "Trip");
        var landing = Path.Combine(_root, "Landed");
        var map = Path.Combine(folder, "map.png");

        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(map, "x");

        _on["user.xdg.tags"] = Bytes("Holiday");
        Fake(folder, map);

        XdgTrash.CopyDirectory(folder, landing);

        Assert.Contains(_written, w => w.Path == landing);
        Assert.Contains(_written, w => w.Path == Path.Combine(landing, "map.png"));
    }

    // ---- and in the right order ----------------------------------------------

    /// <summary>
    /// **The order of the two calls, which no unit can be asked about here.**
    /// The kernel checks write permission on the inode before it will take a
    /// user.* attribute, and FileMetadata.Carry is what reproduces a 0400
    /// source as a 0400 copy — so carrying the attributes afterwards would lose
    /// them on exactly the files with the tightest modes. That cannot be
    /// demonstrated on a Windows agent, which has neither the permission check
    /// nor the syscall, so the statement order is read out of the source
    /// instead.
    ///
    /// This says nothing about the other end of the window — that the target
    /// exists by then. A_copy_carries_the_attributes_onto_the_new_file does.
    /// </summary>
    [Fact]
    public void The_attributes_are_carried_before_the_mode_is()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "LinuxFileOperations.cs");

        var attributes = source.IndexOf("Xattrs.Carry(source, target);", StringComparison.Ordinal);
        var mode = source.IndexOf("FileMetadata.Carry(source, target);", StringComparison.Ordinal);

        Assert.True(attributes >= 0, "the copy no longer carries extended attributes at all");
        Assert.True(mode >= 0, "the copy no longer carries the mode at all");
        Assert.True(attributes < mode, "the attributes must be carried before the mode");
    }

    /// <summary>
    /// The sentence that sent this finding looking in the wrong place: a
    /// comment explaining why the P/Invokes are source-generated pointed at
    /// LinuxTagStore, a class that no longer exists — and it was the only line
    /// in the repository that spelled "xattr" at all.
    /// </summary>
    [Fact]
    public void The_marshalling_note_points_at_a_type_that_exists()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "LinuxRemoteMounts.cs");

        Assert.Contains("reasoning as the xattr calls in " + nameof(Xattrs), source, StringComparison.Ordinal);
    }
}
