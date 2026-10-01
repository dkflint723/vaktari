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
/// be watched, as a pane's own folder is. Where nothing above can be watched
/// or read at all — a drive letter or a share that is not there, a server
/// that does not answer — it asks again every <see cref="RetryInterval"/>.
/// One callback at most, from whatever thread noticed; disposing stops the
/// wait.
///
/// **Nothing waits behind the disk.** The disk is asked — whether the folder
/// is back, which folder above is there, a watch opened on it — by one check
/// at a time, and never under the lock that <see cref="Dispose"/> and
/// <see cref="Watching"/> take. A check asked for while another runs is left
/// to the one running, which looks again before it stops; so neither the
/// watcher's own thread, which every watch in the process shares, nor the
/// window's thread disposing the wait is ever held by a share that does not
/// answer (batch-0.11.2c QA, round 4: Directory.Exists on a dead server took
/// 42 s, under the lock the window's thread took to dispose the wait on a
/// navigation or a tab closed).
/// </summary>
public sealed class FolderReturnWatch : IDisposable
{
    /// <summary>How often a wait with nothing above it to watch asks again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly IFileSystemProvider _fs;
    private readonly string _path;
    private readonly Action _returned;
    private readonly TimeSpan? _pollInterval;
    private readonly Func<string, bool> _exists;
    private readonly TimeSpan _retryInterval;

    /// <summary>Guards the fields below. Held only to read or swap them —
    /// never across a call to the disk or the provider.</summary>
    private readonly Lock _gate = new();
    private IDisposable? _current;
    private string? _watching;
    private Timer? _retry;
    private bool _done;

    /// <summary>A check asked for and not yet begun: 0 none, 1 a look, 2 a
    /// look that watches the folder above afresh.</summary>
    private int _wanted;

    /// <summary>A check is running; any other asked for now is its to do.</summary>
    private bool _running;

    /// <param name="pollInterval">For a folder above that cannot be watched:
    /// how often to read it. Null is <see cref="PollingWatch.DefaultInterval"/>.</param>
    public FolderReturnWatch(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval = null)
        : this(fs, path, returned, pollInterval, Directory.Exists, RetryInterval)
    {
    }

    /// <param name="exists">Whether a folder is there — the disk's own answer
    /// in the application; a test's, to make the disk slow or absent.</param>
    /// <param name="retryInterval">How often to ask again with nothing above
    /// watched.</param>
    internal FolderReturnWatch(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval,
        Func<string, bool> exists, TimeSpan retryInterval)
    {
        _fs = fs;
        _path = path;
        _returned = returned;
        _pollInterval = pollInterval;
        _exists = exists;
        _retryInterval = retryInterval;

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

    /// <summary>
    /// The next step down is spelled as an 8.3 short name, and an arrival
    /// cannot be matched against it.
    ///
    /// **A missing folder spelled TARGET~1 was never heard coming back**
    /// (batch-0.11.2c QA, round 4, a regression from the filter above): the
    /// watcher reports the long name the folder is made with, which a string
    /// compare can never match to the short one — and the long name of a
    /// folder that is not there cannot be asked for, since GetLongPathName
    /// reads it off the folder. So with a short name next, every arrival asks
    /// the disk, as every arrival did before the filter. Windows only: a tilde
    /// in a name elsewhere is just a tilde, and matches as itself.
    /// </summary>
    private volatile bool _nextIsShort;

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
                if (_nextIsShort || PathRules.Same(change.Path, _next)) Check(force: false);
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
    /// in a way no watch could hear. The places list changing is the one the
    /// pane passes on; what that does and does not cover is written on the
    /// pane's ReturnWait. Never waits on the disk: with a check already
    /// running, it is left to that one.
    /// </summary>
    public void Recheck() => Check(force: true);

    /// <summary>
    /// Asks for a check, and runs it here unless one is running already — in
    /// which case that one looks again before it stops, and this returns at
    /// once.
    /// </summary>
    private void Check(bool force)
    {
        lock (_gate)
        {
            if (_done) return;

            _wanted = Math.Max(_wanted, force ? 2 : 1);

            if (_running) return;

            _running = true;
        }

        var stopped = false;

        try
        {
            while (true)
            {
                bool forced;

                lock (_gate)
                {
                    if (_done || _wanted == 0)
                    {
                        _running = false;
                        stopped = true;
                        return;
                    }

                    forced = _wanted == 2;
                    _wanted = 0;
                }

                Once(forced);
            }
        }
        finally
        {
            if (!stopped)
            {
                lock (_gate) _running = false;
            }
        }
    }

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
    private void Once(bool force)
    {
        Interlocked.Increment(ref _checks);

        if (_exists(_path))
        {
            IDisposable? last;

            lock (_gate)
            {
                if (_done) return;

                _done = true;
                last = Clear();
            }

            Let(go: last);
            _returned();
            return;
        }

        var above = NearestAbove(_path, _exists);

        if (!force && above is not null && above == Watching) return;

        var (watching, opened) = OpenNearest(above);
        IDisposable? old;

        lock (_gate)
        {
            if (_done)
            {
                // Disposed while the watch was being opened: it is nobody's.
                old = opened;
            }
            else
            {
                old = _current;
                _current = opened;
                _watching = watching;
                var next = watching is null ? null : Toward(watching, _path);
                _next = next;
                _nextIsShort = next is not null && OperatingSystem.IsWindows() && Path.GetFileName(next).Contains('~');

                if (watching is null) _retry ??= new Timer(static w => ((FolderReturnWatch)w!).Check(force: true), this, _retryInterval, _retryInterval);
                else Let(go: Interlocked.Exchange(ref _retry, null));
            }
        }

        Let(go: old);

        if (watching is null) return;

        // Made again between the look and the watch, and so never heard
        // arriving: look once more now that the watch is there. And not only
        // the folder itself — a folder on the way down to it, made in that
        // same moment, is never heard arriving either, and the wait would sit
        // above it hearing nothing of what happens inside it (measured: a
        // folder above deleted and made again at once, 16 rounds in 20).
        //
        // Asked only while something is watched: with nothing watched,
        // asking again at once would only fail again; the retry timer asks
        // instead. And compared with the folder first found, not the one
        // watched: a folder above that is there but refused leaves the wait
        // watching higher up for good, and comparing with that spun the
        // check in a loop, finding the refused folder again each time.
        if (_exists(_path) || NearestAbove(_path, _exists) != above) Check(force: false);
    }

    /// <summary>
    /// A watch on the nearest folder above that will take one, starting from
    /// <paramref name="above"/>, and the folder it is on — or nothing, when
    /// none will.
    ///
    /// **A folder above gone between the look and the watch left the wait
    /// watching nothing, for good** (batch-0.11.2c QA, round 4: rm -rf a;
    /// mkdir -p a/b under a wait on a/b/c missed a/b/c most rounds on Linux,
    /// and every round over UNC on Windows). The look found a, a went before
    /// its watch opened, the watch and the timer that stands in for one were
    /// both refused — and the second look ran only while something was
    /// watched, so nothing ever asked again. A refused folder is looked past
    /// now: the nearest folder above is found again, and when that is the one
    /// just refused — there, but not to be watched or read — the one above
    /// it. Each try is one step nearer the root, so the path's own depth
    /// bounds them; a folder flickering in and out can do no worse than use
    /// them up. Refused all the way to the root (a drive or share that does
    /// not answer), nothing is watched, and the retry timer, the places list
    /// or F5 asks again.
    /// </summary>
    private (string? Folder, IDisposable? Watch) OpenNearest(string? above)
    {
        var tries = Depth(_path);

        for (var tried = 0; above is not null && tried < tries; tried++)
        {
            if (Open(above) is { } watch) return (above, watch);

            var again = NearestAbove(_path, _exists);

            above = PathRules.Same(again, above) ? NearestAbove(above, _exists) : again;
        }

        return (null, null);
    }

    /// <summary>How many folders lie above <paramref name="path"/>, up to and
    /// including its root.</summary>
    private static int Depth(string path)
    {
        var depth = 0;

        for (var up = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
             !string.IsNullOrEmpty(up);
             up = Path.GetDirectoryName(up))
        {
            depth++;
        }

        return depth;
    }

    /// <summary>The fields let go, for a wait that is over; what was watched
    /// is handed back to be disposed outside the lock.</summary>
    private IDisposable? Clear()
    {
        var old = _current;
        _current = null;
        _watching = null;
        _next = null;
        Let(go: Interlocked.Exchange(ref _retry, null));
        return old;
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
    internal static string? NearestAbove(string path) => NearestAbove(path, Directory.Exists);

    private static string? NearestAbove(string path, Func<string, bool> exists)
    {
        for (var up = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
             !string.IsNullOrEmpty(up);
             up = Path.GetDirectoryName(up))
        {
            if (exists(up)) return up;
        }

        return null;
    }

    /// <summary>Ends the wait. Never waits on the disk: a check still running
    /// lets go of whatever it opens once it is done.</summary>
    public void Dispose()
    {
        IDisposable? old;

        lock (_gate)
        {
            _done = true;
            old = Clear();
        }

        Let(go: old);
    }
}
