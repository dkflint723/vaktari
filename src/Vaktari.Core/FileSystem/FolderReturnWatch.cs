namespace Vaktari.Core.FileSystem;

/// <summary>
/// Waits for a folder that is not there to come back, from the nearest folder
/// above it that is, and says so once when it does.
///
/// **A folder renamed or trashed from the other pane, and then put back, left
/// this one saying it was not there** (batch-0.11.2c QA, GoneFlowProbe). Its
/// watcher heard it go — Gone, which a FileSystemWatcher never said — and the
/// reload that followed found nothing; Undo then restored the folder, and the
/// pane went on saying "that folder is not there any more", watching nothing,
/// until F5 or a navigation. Before Gone was heard the pane simply went on
/// showing the old rows, which came back to life with the Undo.
///
/// So the pane keeps waiting where it can: the nearest folder above the
/// missing one that exists is watched, and any arrival there — a name made,
/// a name renamed in, events dropped — asks again whether the folder is back.
/// A nearer folder above it that comes back first moves the wait down to it;
/// the folder watched going too moves it up. Read on a timer where it cannot
/// be watched, as a pane's own folder is. One callback at most, from whatever
/// thread noticed; disposing stops the wait.
/// </summary>
public sealed class FolderReturnWatch : IDisposable
{
    private readonly IFileSystemProvider _fs;
    private readonly string _path;
    private readonly Action _returned;
    private readonly TimeSpan? _pollInterval;
    private readonly Lock _gate = new();
    private IDisposable? _current;
    private string? _watching;
    private bool _done;

    /// <param name="pollInterval">For a folder above that cannot be watched:
    /// how often to read it. Null is <see cref="PollingWatch.DefaultInterval"/>.</param>
    public FolderReturnWatch(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval = null)
    {
        _fs = fs;
        _path = path;
        _returned = returned;
        _pollInterval = pollInterval;

        Check();
    }

    /// <summary>The folder above being watched now, or null when there is
    /// none (or the wait is over). For the tests.</summary>
    public string? Watching
    {
        get { lock (_gate) return _watching; }
    }

    private void OnChange(FileSystemChange change)
    {
        // A name made or renamed in may be the folder, or a folder on the way
        // down to it; dropped events may have hidden either; the folder above
        // going means looking further up. A change to a file, or one removed,
        // brings nothing back.
        if (change.Kind is ChangeKind.Added or ChangeKind.Renamed or ChangeKind.Lost or ChangeKind.Gone)
            Check();
    }

    /// <summary>Back, and said; or waiting at the nearest folder above.</summary>
    private void Check()
    {
        var back = false;

        lock (_gate)
        {
            if (_done) return;

            if (Directory.Exists(_path))
            {
                back = true;
                _done = true;
                Let(go: _current);
                _current = null;
                _watching = null;
            }
            else
            {
                var above = NearestAbove(_path);

                if (above == _watching) return;

                var old = _current;
                _current = above is null ? null : Open(above);
                _watching = _current is null ? null : above;

                Let(go: old);
            }
        }

        if (back)
        {
            _returned();
            return;
        }

        // Made again between the look and the watch, and so never heard
        // arriving: look once more now that the watch is there.
        if (Directory.Exists(_path)) Check();
    }

    private IDisposable? Open(string folder)
    {
        try
        {
            return _fs.Watch(folder, OnChange);
        }
        catch (Exception refused)
        {
            Quiet.Swallowed("watch", refused);

            try
            {
                return new PollingWatch(folder, OnChange, _pollInterval);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Quiet.Swallowed("watch", e);
                return null;
            }
        }
    }

    private static void Let(IDisposable? go)
    {
        try
        {
            go?.Dispose();
        }
        catch (Exception e)
        {
            Quiet.Swallowed("watch", e);
        }
    }

    /// <summary>The nearest folder above <paramref name="path"/> that exists,
    /// or null when none does.</summary>
    internal static string? NearestAbove(string path)
    {
        for (var up = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
             !string.IsNullOrEmpty(up);
             up = Path.GetDirectoryName(up))
        {
            if (Directory.Exists(up)) return up;
        }

        return null;
    }

    public void Dispose()
    {
        IDisposable? old;

        lock (_gate)
        {
            _done = true;
            old = _current;
            _current = null;
            _watching = null;
        }

        Let(go: old);
    }
}
