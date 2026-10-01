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

        Check(force: false);
    }

    /// <summary>The folder above being watched now, or null when there is
    /// none (or the wait is over). For the tests.</summary>
    public string? Watching
    {
        get { lock (_gate) return _watching; }
    }

    /// <summary>The name in the folder watched that is the next step down to
    /// the missing folder — the only arrival there that can bring it back.</summary>
    private volatile string? _next;

    /// <summary>How many times the disk has been asked whether the folder is
    /// back. For the tests.</summary>
    public int Checks => Volatile.Read(ref _checks);

    private int _checks;

    private void OnChange(FileSystemChange change)
    {
        switch (change.Kind)
        {
            // **Only the name on the way down.** Every arrival in the folder
            // above used to ask the disk twice whether the missing folder was
            // back, on the watcher's own thread — the one thread every watch
            // in the process shares. Ten waits under a busy parent (a
            // download folder, /tmp) were twenty stats per file arriving, and
            // twenty thousand arrivals overflowed the kernel's queue, which
            // tells every pane in the process it has lost track (batch-0.11.2c
            // QA, round 3). An arrival brings the folder back only if it is
            // the folder, or the folder above it on the way down; anything
            // else is a string compared and nothing more.
            case ChangeKind.Added:
            case ChangeKind.Renamed:
                if (PathRules.Same(change.Path, _next)) Check(force: false);
                break;

            // Dropped events may have hidden the arrival, and the folder
            // watched going means looking further up — or at a folder of the
            // same name made again before its going was read, which the old
            // watch, on the old folder, will never hear from.
            case ChangeKind.Lost:
            case ChangeKind.Gone:
                Check(force: true);
                break;

            // A change to a file, or one removed, brings nothing back.
        }
    }

    /// <summary>
    /// Asks again whether the folder is back, and watches afresh from the
    /// nearest folder above it: for a caller that knows the disk has changed
    /// in a way no watch could hear — a drive mounted where there was nothing
    /// to watch above the folder, a drive letter or a server answering again.
    /// </summary>
    public void Recheck() => Check(force: true);

    /// <summary>
    /// Back, and said; or waiting at the nearest folder above.
    ///
    /// **<paramref name="force"/> watches the folder above afresh even when
    /// it is the one already watched.** A folder above deleted and made again
    /// before its going is read is the same path and a new folder: keeping the
    /// watch because the path matched kept a watch on the old one, which hears
    /// nothing ever again (batch-0.11.2c QA, round 3: 20 rounds in 20 on Linux,
    /// about half on Windows; a pane on a/b after rm -rf a, mkdir -p a/b stayed
    /// on its error until F5).
    /// </summary>
    private void Check(bool force)
    {
        var back = false;

        lock (_gate)
        {
            if (_done) return;

            Interlocked.Increment(ref _checks);

            if (Directory.Exists(_path))
            {
                back = true;
                _done = true;
                Let(go: _current);
                _current = null;
                _watching = null;
                _next = null;
            }
            else
            {
                var above = NearestAbove(_path);

                if (!force && above == _watching) return;

                var old = _current;
                _current = above is null ? null : Open(above);
                _watching = _current is null ? null : above;
                _next = _watching is null ? null : Toward(_watching, _path);

                Let(go: old);
            }
        }

        if (back)
        {
            _returned();
            return;
        }

        // Made again between the look and the watch, and so never heard
        // arriving: look once more now that the watch is there. And not only
        // the folder itself — a folder on the way down to it, made in that
        // same moment, is never heard arriving either, and the wait would sit
        // above it hearing nothing of what happens inside it (measured: a
        // folder above deleted and made again at once, 16 rounds in 20).
        //
        // Asked only while something is watched: a folder above that cannot
        // be opened leaves nothing watched, and asking again would only fail
        // again, forever.
        if (Directory.Exists(_path) || (Watching is { } now && NearestAbove(_path) != now)) Check(force: false);
    }

    /// <summary>The path one step below <paramref name="above"/> on the way
    /// down to <paramref name="path"/>.</summary>
    private static string Toward(string above, string path)
    {
        var step = path;

        for (var up = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(step));
             up is not null && !PathRules.Same(up, above);
             up = Path.GetDirectoryName(step))
        {
            step = up;
        }

        return step;
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
