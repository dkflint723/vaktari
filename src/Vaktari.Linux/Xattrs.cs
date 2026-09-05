using System.Runtime.InteropServices;
using System.Text;

using Vaktari.Core;

namespace Vaktari.Linux;

/// <summary>
/// The extended attributes a copy has to carry.
///
/// **A stream copy landed with none of them.** Both engines copy byte by byte
/// and then apply times, attributes and the unix mode; nothing ever called
/// getxattr, so a file's Baloo tags and its <c>user.xdg.origin.url</c> — the
/// address a download came from, which is how a browser's file can still say
/// where it was fetched — were dropped by every copy, and by every move that
/// crossed a filesystem. A repository-wide grep for getxattr, setxattr and
/// listxattr found no P/Invoke at all.
///
/// **Only the <c>user.</c> namespace.** The other three are not ours to
/// reproduce:
///
/// - <c>security.</c> holds the SELinux label, which the policy assigns on
///   create and which a copy must be allowed to differ in, and
///   <c>security.capability</c>, where a setcap binary's capabilities live —
///   copying that one onto a duplicate hands the duplicate cap_net_raw or
///   cap_setuid because a file manager was asked to duplicate a file.
/// - <c>system.</c> is where POSIX ACLs sit, as a blob naming the uids and
///   gids allowed in — and those need not mean the same people at the
///   destination: uid 1000 on a stick carried in from another machine is
///   somebody else. Deciding who may read the copy is not a decision to make
///   by copying bytes.
/// - <c>trusted.</c> is CAP_SYS_ADMIN only and does not even appear in an
///   ordinary user's listxattr.
///
/// **Dropping the ACL while the mode is copied can widen access, and that is
/// left standing rather than solved.** On a file that carries a POSIX ACL the
/// group bits of st_mode are the ACL *mask*, not the ACL_GROUP_OBJ entry — so
/// a file whose group entry is r-- under a rw- mask, copied by FileMetadata's
/// <c>File.SetUnixFileMode</c> and by nothing here, lands as a plain 0660 with
/// no ACL and the owning group has gained write. Written down because it is a
/// consequence of the mode copy that this class neither makes nor undoes, and
/// undoing it means answering the ids question above. Unverified from here:
/// the agent this was written on has neither getxattr nor acl(5).
///
/// That is also what makes the rule cheap to state and cheap to test: one
/// prefix, checked per attribute name, and the syscalls behind a seam so the
/// decision can be exercised on an agent that has no getxattr at all.
///
/// Regular files and folders only, which is not an omission either: xattr(7)
/// says the <c>user.</c> namespace is permitted on nothing else, so a copied
/// symlink has none to carry and setxattr on one would be refused. The plain
/// getxattr and setxattr are therefore the right calls rather than the l-
/// prefixed pair — a link is reproduced by CopyLink and never reaches here.
/// </summary>
internal static partial class Xattrs
{
    /// <summary>
    /// The one namespace a copy reproduces. Baloo's tags are
    /// <c>user.xdg.tags</c>, its rating <c>user.baloo.rating</c>, and a
    /// download's source <c>user.xdg.origin.url</c> — all of them here.
    /// </summary>
    private const string Ours = "user.";

    // ---- seams ---------------------------------------------------------------
    //
    // Null in the application; the syscalls below answer instead. A test sets
    // all three together — the real Read and Write carry no platform guard of
    // their own, because they are only ever reached through a non-empty result
    // from Names, and on a machine without libc that result is empty unless a
    // test put something in it.

    internal static Func<string, IReadOnlyList<string>>? NamesOverride { get; set; }

    internal static Func<string, string, byte[]?>? ReadOverride { get; set; }

    internal static Action<string, string, byte[]>? WriteOverride { get; set; }

    /// <summary>
    /// Reproduces <paramref name="source"/>'s user attributes on
    /// <paramref name="target"/>.
    ///
    /// Does not clear what the target already has. Every caller writes into
    /// something it has just created, or a file the user chose to replace — and
    /// on a replaced file an attribute the old one had and the source has not
    /// is left standing, which is a smaller wrong than deleting somebody's
    /// tags. A folder answered "overwrite" is a merge and not a replacement, so
    /// the copy engine does not call this on a folder that was already there.
    /// </summary>
    internal static void Carry(string source, string target)
        => Apply(target, Capture(source));

    /// <summary>
    /// The user attributes <paramref name="path"/> is wearing.
    ///
    /// **Split from <see cref="Apply"/> because File.Move deletes what it
    /// copied.** A move that crosses a filesystem is a byte copy and an unlink
    /// inside File.Move, and that copy carries the mode and the times only — so
    /// by the time there is a target to write to, the source whose attributes
    /// were wanted is already gone. Reading them first is the only order that
    /// works, and <see cref="Carry"/> is the two halves back to back for the
    /// callers where the source stays put.
    /// </summary>
    internal static IReadOnlyList<(string Name, byte[] Value)> Capture(string path)
    {
        var carried = new List<(string, byte[])>();

        foreach (var name in (NamesOverride ?? Names)(path))
        {
            if (!Carried(name)) continue;

            var value = (ReadOverride ?? Read)(path, name);

            // **An attribute that could not be read is not an empty one.** The
            // list and the read are two syscalls and a name can go away between
            // them; an empty value is itself a legal value, so treating a
            // failed read as one would put an attribute on the copy that the
            // source does not have.
            if (value is null) continue;

            carried.Add((name, value));
        }

        return carried;
    }

    /// <summary>
    /// Writes captured attributes onto <paramref name="path"/>, which must
    /// already exist: setxattr needs an inode to hang them on, and a name that
    /// is not there yet answers ENOENT.
    /// </summary>
    internal static void Apply(string path, IReadOnlyList<(string Name, byte[] Value)> attributes)
    {
        foreach (var (name, value) in attributes)
            (WriteOverride ?? Write)(path, name, value);
    }

    /// <summary>
    /// Whether an attribute is one a copy reproduces. See the class note for
    /// why the answer is the <c>user.</c> namespace and nothing else.
    /// </summary>
    internal static bool Carried(string name)
        => name.StartsWith(Ours, StringComparison.Ordinal);

    /// <summary>
    /// listxattr hands back one buffer holding every name, each terminated by
    /// a NUL — not a count and not a length prefix, so the names have to be cut
    /// out of it.
    ///
    /// A tail with no terminator is dropped rather than guessed at: the kernel
    /// terminates every name it writes, so an unterminated remainder can only
    /// be a short buffer, and half a name is worse than no name.
    /// </summary>
    internal static List<string> Split(ReadOnlySpan<byte> list)
    {
        var names = new List<string>();

        int end;

        while ((end = list.IndexOf((byte)0)) >= 0)
        {
            names.Add(Encoding.UTF8.GetString(list[..end]));
            list = list[(end + 1)..];
        }

        return names;
    }

    // ---- the syscalls --------------------------------------------------------
    //
    // **The errnos named below are read out of xattr(7), not measured.** This
    // agent has no libc to ask, and the ordering comment in LinuxFileOperations
    // carries the same hedge. What the code does never depends on telling them
    // apart: every non-positive answer means "carry on with nothing", which is
    // right for a filesystem that has no attributes and right for a source that
    // has gone.
    //
    // And it says nothing, where Write does say something. That asymmetry is
    // deliberate rather than an oversight: a refused setxattr is one target on
    // one filesystem, while a listxattr that answers ENOTSUP does so for every
    // file on the stick — a traced line per file would bury the one a
    // VAKTARI_QUIET_DEBUG session was opened to find.

    private static IReadOnlyList<string> Names(string path)
    {
        // **A run-time check, not the assembly's [SupportedOSPlatform].** That
        // attribute is a promise to the analyser and nothing more, and this
        // project's own suite runs the Linux copy engine on a Windows agent —
        // where libc does not resolve and the first call throws
        // DllNotFoundException. Deleting this line reddens
        // Carrying_where_the_syscalls_are_missing_does_nothing.
        if (!OperatingSystem.IsLinux()) return [];

        // Size first: listxattr with a null buffer reports the bytes needed and
        // touches nothing. Zero means the file has no attributes. A negative is
        // ENOTSUP on a filesystem that has none to give — what a FAT stick or a
        // phone over MTP answers — or ENOENT if the source went away between
        // the plan and the copy. Neither is a failure worth a word.
        var size = ListSize(path, 0, 0);

        if (size <= 0) return [];

        var buffer = new byte[(int)size];
        var got = ListInto(path, buffer, (nuint)size);

        // A negative here is ERANGE if something added an attribute between the
        // two calls so the buffer no longer fits, and ENOENT if the same race
        // took the file instead. The next copy will see whichever list is there
        // then; retrying in a loop would race the same writer again.
        if (got <= 0) return [];

        return Split(buffer.AsSpan(0, (int)got));
    }

    private static byte[]? Read(string path, string name)
    {
        var size = GetSize(path, name, 0, 0);

        // Removed between the list and this call, or unreadable. Null rather
        // than empty, because Capture treats the two differently.
        if (size < 0) return null;

        // An attribute with no value is legal — setxattr takes a length of
        // zero, and the name is then the whole of the information — so an empty
        // array here is a value, not the absence of one.
        if (size == 0) return [];

        var value = new byte[(int)size];
        var got = GetInto(path, name, value, (nuint)size);

        if (got < 0) return null;

        return got < size ? value[..(int)got] : value;
    }

    private static void Write(string path, string name, byte[] value)
    {
        // Flags 0: create it, or replace it if the target already had one.
        if (SetXattr(path, name, value, (nuint)value.Length, 0) == 0) return;

        // A refused attribute is not a failed copy — the target may simply be
        // a filesystem with nowhere to put it — but a silent one is a debugging
        // session later, which is the whole reason Quiet exists.
        Quiet.Swallowed(
            "file-ops",
            new IOException($"setxattr {name} on {path}: errno {Marshal.GetLastPInvokeError()}"));
    }

    // Source-generated marshalling rather than DllImport, so these survive
    // trimming and the AOT publish Directory.Build.props checks for. The paths
    // and names are UTF-8 because that is what the kernel takes: both are byte
    // strings to it, and .NET's own file APIs encode them the same way.
    //
    // Two imports per call where the size has to be asked for first — an nint
    // buffer so a real null can be passed, and a Span for the fetch. The same
    // shape RegGetValue takes on the Windows side, and for the same reason: an
    // empty Span does not reliably marshal as a null pointer.

    [LibraryImport("libc", EntryPoint = "listxattr",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ListSize(string path, nint list, nuint size);

    [LibraryImport("libc", EntryPoint = "listxattr",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ListInto(string path, Span<byte> list, nuint size);

    [LibraryImport("libc", EntryPoint = "getxattr",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint GetSize(string path, string name, nint value, nuint size);

    [LibraryImport("libc", EntryPoint = "getxattr",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint GetInto(string path, string name, Span<byte> value, nuint size);

    [LibraryImport("libc", EntryPoint = "setxattr",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetXattr(
        string path, string name, ReadOnlySpan<byte> value, nuint size, int flags);
}
