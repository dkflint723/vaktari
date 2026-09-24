namespace Vaktari.Core.FileSystem;

/// <summary>
/// Walking a folder tree without following symbolic links out of it.
///
/// **This is a data-safety rule, not a tidiness one.** The obvious walk —
/// <c>EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)</c> —
/// descends INTO linked directories, and the operations people then perform on
/// what it yields follow links again: <c>chmod</c> is not <c>lchmod</c>, so a
/// folder holding a link to someone's photo library, given a recursive 700,
/// silently rewrites the real library. A link that points at an ancestor never
/// finishes at all.
///
/// The copy engine measured and fixed this hazard for itself and left the rule
/// in its own file; every other recursive walk in the project kept the unguarded
/// version. This is that walk, in the one place all of them can reach.
/// </summary>
public static class SafeWalk
{
    /// <summary>One entry found underneath a root.</summary>
    /// <param name="Path">Where it is.</param>
    /// <param name="IsDirectory">A real directory — never a link to one.</param>
    /// <param name="IsLink">A link, as <see cref="SafeWalk.IsLink(FileSystemInfo)"/>
    /// decides — yielded but never descended into.</param>
    /// <param name="Length">Bytes, for a file; zero for a directory or a
    /// link. **Carried because the walk already has it** — the enumeration
    /// has just read the entry, and a caller that totals sizes would
    /// otherwise stat every file a second time for a number it was handed
    /// and dropped. Defaulted so the callers that only want paths are
    /// untouched.</param>
    public readonly record struct Found(string Path, bool IsDirectory, bool IsLink, long Length = 0);

    /// <summary>
    /// How this platform reads an entry's reparse tag without following it:
    /// the tag, or null when it cannot be read. Adopted by the Windows platform,
    /// because reading one is a call into the operating system and this
    /// assembly makes none — its project file says so. Null everywhere else,
    /// and then <see cref="IsLink(FileSystemInfo)"/> takes every reparse point
    /// for a link, which is what this walk did before there was a reader.
    /// </summary>
    public static Func<string, uint?>? ReparseTag { get; set; }

    /// <summary>IsReparseTagNameSurrogate: the bit Windows sets on the tag of an
    /// entry that stands for another named entry.</summary>
    private const uint NameSurrogate = 0x20000000;

    /// <summary>
    /// Whether <paramref name="entry"/> is a link: reported where it stands,
    /// never entered, and of no size.
    ///
    /// **On Windows a link is a reparse point whose tag is a name surrogate.**
    /// The ReparsePoint attribute alone used to be taken for one, and entries
    /// that are not links carry it with sizes of their own — measured: a
    /// 1,234-byte file given a third-party tag was yielded as a link of length
    /// 0, and an app execution alias, tag 0x8000001B, was a link the same way.
    /// A LinkTarget is no test either. A symbolic link made by WSL carries tag
    /// 0xA000001D and no target .NET can read — measured through /mnt/c, where
    /// a folder one reads as a folder that cannot be opened, and through
    /// \\wsl.localhost, where every one reads as a file the length of its
    /// target's text. The name-surrogate bit was set on a junction and on WSL's
    /// links, and on neither of the others; Windows defines symbolic links'
    /// tag with it too.
    ///
    /// A tag that cannot be read makes a link, because what cannot be told
    /// apart is never followed. On Linux no reader is adopted and the bit means
    /// a symbolic link, so every one is a link, dangling or not.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return false;

        return IsLink(entry.Attributes, ReparseTag?.Invoke(entry.FullName));
    }

    /// <summary>
    /// The same rule for a caller that already holds the tag — the Windows
    /// listing reads it once per marked row at the enumeration, and its
    /// details line and properties window read it for one entry — so that
    /// none of them keeps a copy of the bit. A null tag is one that could not
    /// be read, and makes a link, as above.
    /// </summary>
    public static bool IsLink(FileAttributes attributes, uint? tag)
        => (attributes & FileAttributes.ReparsePoint) != 0
           && (tag is not { } known || (known & NameSurrogate) != 0);

    /// <summary>
    /// Everything under <paramref name="root"/>, deepest last, with links
    /// reported and never followed.
    ///
    /// An unreadable folder is skipped rather than thrown from: a walk that
    /// dies on the first permission denied reports nothing about the thousands
    /// of entries it could have handled.
    ///
    /// **<paramref name="unreadable"/> is how a total stays honest.** A walk
    /// that steps over a folder silently hands back a figure short by
    /// whatever was behind it, and a caller totalling bytes cannot tell that
    /// from a folder that really is that size. Told, it can say so.
    /// </summary>
    public static IEnumerable<Found> Descend(
        string root, CancellationToken ct = default, Action<string>? unreadable = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var folder = pending.Pop();

            // **A folder whose name ends in a dot or a space was walked as its
            // neighbour.** Windows' path rules strip the character before the
            // call, so "dir." and "dir " were listed as "dir" — its files
            // yielded again under paths that are really dir's. A duplicate
            // scan then paired a file with itself and offered the only copy as
            // its own spare, and a size counted dir three times. Such a folder
            // cannot be read by name at all (ReachablePath), so it is what
            // every other folder that will not list is: unreadable, and said.
            if (!ReachablePath.IsReachable(folder))
            {
                unreadable?.Invoke(folder);
                continue;
            }

            IEnumerable<FileSystemInfo> children;

            try
            {
                children = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable?.Invoke(folder);
                continue;
            }

            // **The enumeration can fail after it has started**, which the try
            // above cannot see: creating it opens nothing, and the first
            // MoveNext is where a folder that lists but cannot be searched
            // (mode 644 on Linux, /proc/<pid>/map_files) or one whose entries
            // Windows cannot name throws. That threw straight out of the walk,
            // so one such folder anywhere failed a whole space-usage or
            // duplicates scan. Driven by hand, so a failure part-way through a
            // folder is that folder's alone — counted, and the walk goes on
            // with everything it had already found.
            using var entries = children.GetEnumerator();

            while (true)
            {
                FileSystemInfo child;

                try
                {
                    if (!entries.MoveNext()) break;

                    child = entries.Current;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable?.Invoke(folder);
                    break;
                }

                ct.ThrowIfCancellationRequested();

                // **Reported, never entered.** Following one is how a recursive
                // operation escapes the tree the person was looking at. Which
                // entries are links is IsLink's question.
                //
                // A folder that carries a reparse point and is not a link is
                // entered. A cloud placeholder folder is one to a process that
                // exposes placeholders; to a process in the default mode, which
                // is where .NET starts, it carries no ReparsePoint at all —
                // measured against a Proton Drive sync root in both modes — so the
                // walk already entered those. One whose tag no filter owns cannot
                // be opened, measured, and is reported unreadable like any other
                // folder that will not list.
                if (IsLink(child))
                {
                    yield return new Found(child.FullName, IsDirectory: false, IsLink: true);
                    continue;
                }

                if (child is DirectoryInfo)
                {
                    yield return new Found(child.FullName, IsDirectory: true, IsLink: false);
                    pending.Push(child.FullName);
                }
                else
                {
                    yield return new Found(
                        child.FullName,
                        IsDirectory: false,
                        IsLink: false,
                        child is FileInfo file ? file.Length : 0);
                }
            }
        }
    }
}
