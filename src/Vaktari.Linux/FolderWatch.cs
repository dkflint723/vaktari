using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// The one way this assembly watches a folder: through the process's own
/// inotify instance (<see cref="Inotify"/>) wherever there is one, and
/// through a FileSystemWatcher, as before, where there is not.
///
/// **Where there is not** is a host that is not Linux — this project's tests
/// run on the Windows runner too, and a libc call there throws
/// DllNotFoundException (measured 2026-09-18, four MoveConflictTests red on
/// main) — or a C library without inotify. Neither is a Linux desktop, so the
/// FileSystemWatcher's leak on a deleted folder does not reach a user.
/// </summary>
internal static class FolderWatch
{
    /// <summary>
    /// Watches the direct children of <paramref name="path"/>. Throws when the
    /// folder cannot be watched; the caller decides what to do instead.
    /// </summary>
    internal static IDisposable Open(string path, Action<FileSystemChange> onChange)
    {
        if (Inotify.Available)
        {
            try
            {
                return Inotify.Watch(path, onChange);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Vaktari.Core.Quiet.Swallowed("watch", e);
            }
        }

        return ThroughFileSystemWatcher(path, onChange);
    }

    /// <summary>
    /// The watcher this assembly used everywhere before it had its own
    /// instance, kept for a host without inotify. Unchanged: the same filters,
    /// the same buffer, the same mapping.
    /// </summary>
    internal static FileSystemWatcher ThroughFileSystemWatcher(string path, Action<FileSystemChange> onChange)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
        };

        watcher.Created += (_, e) => onChange(new FileSystemChange(ChangeKind.Added, e.FullPath));
        watcher.Deleted += (_, e) => onChange(new FileSystemChange(ChangeKind.Removed, e.FullPath));
        watcher.Changed += (_, e) => onChange(new FileSystemChange(ChangeKind.Changed, e.FullPath));
        watcher.Renamed += (_, e) => onChange(new FileSystemChange(ChangeKind.Renamed, e.FullPath, e.OldFullPath));

        // **A watcher that falls behind says nothing at all.** The kernel buffer
        // is fixed, and an extraction, a build or a big download in the watched
        // folder overruns it — after which events are simply dropped and the
        // listing goes quietly out of date. Raising the buffer makes that rarer;
        // reporting it is what makes it recoverable.
        //
        // The folder disappearing arrives through the same event, so the two are
        // told apart by asking whether it is still there.
        watcher.Error += (_, _) => onChange(new FileSystemChange(
            Directory.Exists(path) ? ChangeKind.Lost : ChangeKind.Gone, path));

        // 64 KB rather than the 8 KB default. It is non-paged pool, so it is not
        // free, but one page per pane against a listing that stops updating is
        // an easy trade.
        watcher.InternalBufferSize = 64 * 1024;

        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
