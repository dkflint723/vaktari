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
    /// <param name="IsLink">A symbolic link, yielded but never descended into.</param>
    public readonly record struct Found(string Path, bool IsDirectory, bool IsLink);

    /// <summary>
    /// Everything under <paramref name="root"/>, deepest last, with links
    /// reported and never followed.
    ///
    /// An unreadable folder is skipped rather than thrown from: a walk that
    /// dies on the first permission denied reports nothing about the thousands
    /// of entries it could have handled.
    /// </summary>
    public static IEnumerable<Found> Descend(string root, CancellationToken ct = default)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var folder = pending.Pop();

            IEnumerable<FileSystemInfo> children;

            try
            {
                children = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                // **Reported, never entered.** Following one is how a recursive
                // operation escapes the tree the person was looking at.
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
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
                    yield return new Found(child.FullName, IsDirectory: false, IsLink: false);
                }
            }
        }
    }
}
