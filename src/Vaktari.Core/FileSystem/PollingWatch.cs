namespace Vaktari.Core.FileSystem;

/// <summary>
/// A watcher for folders that deliver no change notifications: it reads the
/// folder on a timer and reports the differences as if they had been
/// delivered.
///
/// **A folder on a network mount never updated on its own.** inotify reports
/// what passed through this kernel, and a file written by another machine —
/// or by the desktop's own gvfs or KIO daemon behind a FUSE mount — is not
/// that. The listing of a share sat as it was until F5, which on a share is
/// exactly where somebody else is putting files for you to see. Dolphin and
/// Nautilus both fall back to polling on such mounts; so does this, every
/// five seconds, which is a directory read over the network — the same cost
/// as the listing itself, and the reason it is not every second.
///
/// One walk at a time: a tick that arrives while the previous read is still
/// waiting on a slow share is dropped rather than queued, so a share that
/// stalls costs one outstanding read, not a pile of them.
/// </summary>
public sealed class PollingWatch : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly record struct Stamp(bool IsDirectory, long Length, DateTime LastWriteUtc);

    private readonly string _path;
    private readonly Action<FileSystemChange> _onChange;
    private readonly Timer _timer;
    private Dictionary<string, Stamp> _seen;
    private int _busy;
    private bool _disposed;

    /// <param name="interval">How often to read; <see cref="Timeout.InfiniteTimeSpan"/>
    /// makes the watch tick only when <see cref="Tick"/> is called, which is
    /// how the tests drive it.</param>
    public PollingWatch(string path, Action<FileSystemChange> onChange, TimeSpan? interval = null)
    {
        _path = path;
        _onChange = onChange;
        _seen = Snapshot(path) ?? throw new DirectoryNotFoundException(path);

        var period = interval ?? DefaultInterval;
        _timer = new Timer(_ => Tick(), null, period, period);
    }

    /// <summary>
    /// One read, compared with the last. Internal so a test can take a tick
    /// without waiting for one.
    /// </summary>
    internal void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            if (_disposed) return;

            var now = Snapshot(_path);

            if (now is null)
            {
                _onChange(new FileSystemChange(ChangeKind.Gone, _path));
                return;
            }

            foreach (var (name, stamp) in now)
            {
                if (!_seen.TryGetValue(name, out var was)) Report(ChangeKind.Added, name);
                else if (was != stamp) Report(ChangeKind.Changed, name);
            }

            foreach (var name in _seen.Keys)
                if (!now.ContainsKey(name)) Report(ChangeKind.Removed, name);

            _seen = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A share that did not answer this time. Gone is said only when the
            // folder is not there; a read that failed is tried again next tick.
            Quiet.Swallowed("watch", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Report(ChangeKind kind, string name)
        => _onChange(new FileSystemChange(kind, Path.Combine(_path, name)));

    /// <summary>What the folder holds, by name — or null when it is not there.</summary>
    private static Dictionary<string, Stamp>? Snapshot(string path)
    {
        if (!Directory.Exists(path)) return null;

        var seen = new Dictionary<string, Stamp>(StringComparer.Ordinal);

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            seen[entry.Name] = new Stamp(
                entry is DirectoryInfo,
                entry is FileInfo file ? file.Length : 0,
                entry.LastWriteTimeUtc);

        return seen;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
