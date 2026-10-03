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
/// be watched, as a pane's own folder is. One callback at most, from
/// whatever thread noticed; disposing stops the wait (though a return
/// decided just before may still be announced: see <see cref="Dispose"/>).
///
/// **And every <see cref="RetryInterval"/> it asks again, watching afresh,
/// whatever it watches** — one timer per wait, no thread of its own. A watch is
/// not proof of hearing: where nothing above can be watched or read at all
/// (a drive letter or a share that is not there, a server that does not
/// answer) there is no watch to hear anything; a watch opened on a folder in
/// the moment it is deleted can go dead without ever saying Gone (batch-0.11.2d
/// QA, round 5: Windows, 1 to 7 of 8 waits under a flickering folder); a folder
/// above that refused and is readable again says nothing; and a tmpfs or FUSE
/// filesystem mounted again over the folder watched leaves the watch on the
/// folder the mount now covers. The slow look covers all four, at the cost of
/// one look and one watch opened again every half minute per waiting pane.
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
///
/// **And the looks that can wait on the disk run on threads of their own, not
/// the pool's.** The slow look, a look asked for by the places list or by the
/// network changing, and the first look of a wait made by <see cref="Start"/>
/// each run on a thread made
/// for that look, at most one per wait at a time. On the pool, a wait on a
/// dead share held a pool thread for each look, 21 to 42 s, back to back
/// with its 30 s timer; with the pool's minimum at two, eight such waits cut
/// a healthy wait to 11 of 30 looks and kept unrelated pool work 2 s late,
/// and 32 stopped both (batch-0.11.2e QA, round 7) — and the pool is where
/// the window's folder loads run. A wait whose looks take longer than its
/// interval also looks less often: the interval doubles, up to eight times,
/// and comes back once a look is quick again.
/// </summary>
public sealed class FolderReturnWatch : IDisposable
{
    /// <summary>How often a wait asks again, whatever it watches.</summary>
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

    /// <summary>How often the slow look runs now: the interval given, or up
    /// to <see cref="MostBackedOff"/> times it while looks are slow.</summary>
    private TimeSpan _interval;

    private const int MostBackedOff = 8;

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
    /// <param name="retryInterval">How often to ask again, watching afresh.</param>
    internal FolderReturnWatch(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval,
        Func<string, bool> exists, TimeSpan retryInterval)
        : this(fs, path, returned, pollInterval, exists, retryInterval, firstLookHere: true)
    {
    }

    private FolderReturnWatch(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval,
        Func<string, bool> exists, TimeSpan retryInterval, bool firstLookHere)
    {
        _fs = fs;
        _path = path;
        _returned = returned;
        _pollInterval = pollInterval;
        _exists = exists;
        _retryInterval = retryInterval;
        _interval = retryInterval;

        if (firstLookHere) Check(force: false);
        else CheckOnItsOwnThread(force: false);
    }

    /// <summary>
    /// A wait whose first look runs on a thread of its own: this returns at
    /// once, without touching the disk, for a caller that must not wait on it
    /// — or hold a pool thread while it waits (see the class's remarks).
    /// </summary>
    public static FolderReturnWatch Start(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval = null)
        => new(fs, path, returned, pollInterval, Directory.Exists, RetryInterval, firstLookHere: false);

    /// <inheritdoc cref="Start(IFileSystemProvider, string, Action, TimeSpan?)"/>
    internal static FolderReturnWatch Start(IFileSystemProvider fs, string path, Action returned, TimeSpan? pollInterval,
        Func<string, bool> exists, TimeSpan retryInterval)
        => new(fs, path, returned, pollInterval, exists, retryInterval, firstLookHere: false);

    /// <summary>How often the slow look runs now. For the tests.</summary>
    public TimeSpan Interval
    {
        get { lock (_gate) return _interval; }
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
    /// reads it off the folder. So with a short name next, an arrival's own
    /// short name is asked for and compared instead — one call per arrival,
    /// where asking whether the folder is back walked the path (batch-0.11.2d
    /// QA, round 5: 100 such waits under a busy folder made 20,000 files take
    /// 73 s, and a plain watcher there lost track). Windows only: a tilde in a
    /// name elsewhere is just a tilde, and matches as itself.
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
            //
            // **Changed too, on the next step only.** Over SMB on Windows a
            // folder made again where one was deleted a moment before arrives
            // as Changed, never Added (batch-0.11.2d QA, round 5: rm -rf a;
            // mkdir -p a/b over UNC, 14 rounds in 20 missed; with Changed heard
            // as well, 20 in 20 on all six shapes, locally and over UNC).
            case ChangeKind.Added:
            case ChangeKind.Renamed:
            case ChangeKind.Changed:
                if (IsNext(change.Path)) Check(force: false);
                break;

            // Dropped events may have hidden the arrival, and the folder
            // watched going means looking further up — or at a folder of the
            // same name made again before its going was read, which the old
            // watch, on the old folder, will never hear from.
            case ChangeKind.Lost:
            case ChangeKind.Gone:
                Check(force: true);
                break;

            // A name removed brings nothing back.
        }
    }

    /// <summary>
    /// Whether <paramref name="arrived"/> is the next step down.
    ///
    /// **Its last name first, compared in place, and only then the paths.**
    /// This runs for every arrival and every change in the folder watched, for
    /// every wait, on the watcher's one shared thread. PathRules.Same makes a
    /// normalised copy of both paths; with Changed heard as well as Added, a
    /// hundred waits under a busy folder did that four million times for
    /// twenty thousand files, and the reader fell behind the kernel's queue
    /// again (batch-0.11.2e QA, round 6: an independent watch heard 17,180 to
    /// 19,008 of 20,000 on Linux). Two paths that are the same place end in
    /// the same name, under the platform's case rule; a name compared as a
    /// span costs no copy, and only a match pays for the full compare, which
    /// <see cref="FullCompares"/> counts.
    /// </summary>
    private bool IsNext(string arrived)
    {
        var next = _next;

        if (next is null) return false;

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(arrived.AsSpan()));
        var nextName = Path.GetFileName(Path.TrimEndingDirectorySeparator(next.AsSpan()));

        if (name.Equals(nextName, PathRules.Comparison))
        {
            Interlocked.Increment(ref _fullCompares);
            if (PathRules.Same(arrived, next)) return true;
        }

        // A short next step: the arrival's own short name, asked of the
        // volume — but only for a name that could have that alias at all
        // (one API call per arrival per wait, for every file, still flooded a
        // busy folder: batch-0.11.2e QA, round 6). The short spelling shortens
        // every part of the path, the folders above as well, so only the last
        // part is compared — of an arrival in the same folder as the next step.
        return _nextIsShort && OperatingSystem.IsWindows()
            && MayBeAliasOf(name, nextName)
            && PathRules.Same(Path.GetDirectoryName(arrived), Path.GetDirectoryName(next))
            && ShortNameOf(arrived) is { } spelled
            && string.Equals(Path.GetFileName(spelled), Path.GetFileName(next), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many arrivals got as far as the full path compare. For
    /// the tests.</summary>
    public int FullCompares => Volatile.Read(ref _fullCompares);

    private int _fullCompares;

    /// <summary>How many arrivals had their short name asked of the volume.
    /// For the tests.</summary>
    public int ShortLookups => Volatile.Read(ref _shortLookups);

    private int _shortLookups;

    /// <summary>How many slow-look timers this wait has made: one, however
    /// often it looks. Counted rather than inferred from how often it looks,
    /// because a timer dropped without being disposed stops when the
    /// collector finds it — sooner on one runtime than another (batch-0.11.2e:
    /// a timer per look reddened the look count on Windows and not on Fedora).
    /// For the tests.</summary>
    internal int TimersMade
    {
        get { lock (_gate) return _timersMade; }
    }

    private int _timersMade;

    private string? ShortNameOf(string arrived)
    {
        Interlocked.Increment(ref _shortLookups);
        return OperatingSystem.IsWindows() ? ShortNames.Of(arrived) : null;
    }

    /// <summary>
    /// Whether <paramref name="name"/> could carry the 8.3 alias
    /// <paramref name="alias"/>: whether, with spaces and dots dropped as the
    /// volume drops them, it begins with the alias's safe stem.
    ///
    /// The stem is what comes before the tilde, cut at the first character
    /// that is not an ASCII letter or digit (the volume writes _ for what it
    /// cannot keep). **A hashed alias keeps two at most**: past a few aliases
    /// with one stem, NTFS writes up to two characters of the name and four
    /// hex digits of a hash (TAEAE4~1; X31AB~1 for a base of one character;
    /// D26C~1 for a base with none it can keep), so a stem of four to six
    /// characters ending in four hex digits is trusted for what comes before
    /// them. Case is ignored, as the alias is upper case.
    ///
    /// **A name with anything outside printable ASCII is always let through**
    /// (batch-0.11.2e QA, round 7: 83 of 178 real aliases were turned away).
    /// The volume drops such characters from the alias rather than replacing
    /// them — Ünïcödé folder is NCDFOL~1, 🙂emoji folder EMOJIF~1 — so where
    /// the stem starts in the name cannot be told without the volume. A name
    /// let through costs one lookup; a name turned away wrongly is a return
    /// not heard until the slow look.
    /// </summary>
    internal static bool MayBeAliasOf(ReadOnlySpan<char> name, ReadOnlySpan<char> alias)
    {
        var tilde = alias.IndexOf('~');

        if (tilde <= 0) return true;

        foreach (var c in name)
            if (c is < ' ' or > '~') return true;

        var stem = alias[..tilde];
        var safe = 0;

        while (safe < stem.Length && char.IsAsciiLetterOrDigit(stem[safe])) safe++;

        if (stem.Length is >= 4 and <= 6 && !stem[^4..].ContainsAnyExcept(Hex)) safe = Math.Min(safe, stem.Length - 4);

        var matched = 0;

        foreach (var c in name)
        {
            if (matched == safe) break;
            if (c is ' ' or '.') continue;
            if (char.ToUpperInvariant(c) != char.ToUpperInvariant(stem[matched])) return false;
            matched++;
        }

        return matched == safe;
    }

    private static readonly System.Buffers.SearchValues<char> Hex =
        System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef");

    /// <summary>
    /// Asks again whether the folder is back, and watches afresh from the
    /// nearest folder above it: for a caller that knows the disk has changed
    /// in a way no watch could hear. The places list changing and the
    /// machine's network changing (<see cref="NetworkChanges"/>) are the two
    /// the pane passes on; what they do and do not cover is written on the
    /// pane's ReturnWait. Never waits on the disk: with a check already
    /// running, it is left to that one — so however often it is asked while
    /// a look runs, one more look follows, not one per ask.
    /// </summary>
    public void Recheck() => Check(force: true);

    /// <summary>
    /// <see cref="Recheck"/>, on a thread of its own: returns at once, for a
    /// caller on the pool or the window's thread.
    /// </summary>
    public void RecheckOnItsOwnThread() => CheckOnItsOwnThread(force: true);

    /// <summary>
    /// Asks for a check, and runs it here unless one is running already — in
    /// which case that one looks again before it stops, and this returns at
    /// once.
    /// </summary>
    private void Check(bool force)
    {
        if (Ask(force)) Looks();
    }

    /// <summary>
    /// Asks for a check, and runs it on a thread made for it unless one is
    /// running already. Never on the pool: a look can wait on a dead share
    /// for most of a minute.
    /// </summary>
    private void CheckOnItsOwnThread(bool force)
    {
        if (!Ask(force)) return;

        try
        {
            new Thread(static w => ((FolderReturnWatch)w!).Looks())
            {
                IsBackground = true,
                Name = "folder return look",
            }.Start(this);
        }
        catch (Exception e) when (e is OutOfMemoryException or ThreadStartException)
        {
            Quiet.Swallowed("watch", e);
            lock (_gate) _running = false;
        }
    }

    /// <summary>Records a check asked for; true when the caller is to run
    /// it, false when one is running already or the wait is over.</summary>
    private bool Ask(bool force)
    {
        lock (_gate)
        {
            if (_done) return false;

            _wanted = Math.Max(_wanted, force ? 2 : 1);

            if (_running) return false;

            _running = true;
            return true;
        }
    }

    /// <summary>The checks asked for, one after another, until none is
    /// left. Run by whoever <see cref="Ask"/> chose.</summary>
    private void Looks()
    {
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

                var took = System.Diagnostics.Stopwatch.StartNew();
                Once(forced);
                Pace(took.Elapsed);
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

        var (watching, opened, refusedThere) = OpenNearest(above);
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

                if (_retry is null)
                {
                    _retry = new Timer(static w => ((FolderReturnWatch)w!).CheckOnItsOwnThread(force: true), this, _interval, _interval);
                    _timersMade++;
                }
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
        // asking again at once would only fail again; the slow timer asks
        // instead.
        //
        // Compared with the folder first found, and — unless that one was
        // refused while still there — with the folder watched as well. A folder
        // above that is there but refused leaves the wait watching higher up
        // for good, and comparing with the folder watched spun the check in a
        // loop, finding the refused folder again each time. But a folder first
        // found that was refused because it had gone, and was made again
        // before the watch higher up was set up, is the same path as the one
        // first found and was never heard arriving: compared only with that,
        // the wait sat above it until the slow look (batch-0.11.2e QA, round 6,
        // Linux: rm -rf a; mkdir -p a/b, 1 to 3 rounds in 100).
        var now = NearestAbove(_path, _exists);

        if (_exists(_path) || now != above || (!refusedThere && now != watching)) Check(force: false);
    }

    /// <summary>
    /// **A wait whose looks are slow looks less often.** A look that took
    /// longer than the slow look's interval doubles it, up to
    /// <see cref="MostBackedOff"/> times the interval given; a look that took
    /// under a quarter of the interval given, and ended with a folder above
    /// watched, puts it back. The interval only doubles while a look outlasts
    /// it, so on a dead share it stops where the looks do: SMB gives up after
    /// 21 or 42 s, and a 30 s interval settles at 60 s — a look a minute rather
    /// than one back to back, measured over 14 minutes against an unreachable
    /// address (batch-0.11.2f QA, round 9). Eight times is the ceiling for a
    /// path whose looks take longer still, not the usual dead share.
    ///
    /// **Quick is not enough to put it back** (batch-0.11.2e QA, round 8). The
    /// Windows network client remembers for about 30 s that a server did not
    /// answer, so the look after a 42 s one answered in no time — and, counted
    /// as quick, put the interval back every other look: on a real dead share
    /// it never went past twice the interval given. A look that found nothing
    /// to watch has not shown the share is back; one that watches a folder
    /// above has. (A look that finds the folder itself ends the wait.)
    /// </summary>
    private void Pace(TimeSpan took)
    {
        lock (_gate)
        {
            // No timer yet, or one that never fires (a test's): nothing to pace.
            if (_done || _retry is not { } timer || _retryInterval <= TimeSpan.Zero) return;

            var next = _interval;

            if (took > _interval) next = TimeSpan.FromTicks(Math.Min(_interval.Ticks * 2, _retryInterval.Ticks * MostBackedOff));
            else if (took < _retryInterval / 4 && _watching is not null) next = _retryInterval;

            if (next == _interval) return;

            _interval = next;
            timer.Change(next, next);
        }
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
    /// not answer), nothing is watched, and the slow timer, the places list
    /// or F5 asks again.
    /// </summary>
    /// <returns>The folder watched and its watch, and whether the folder
    /// first tried was refused while it was still there.</returns>
    private (string? Folder, IDisposable? Watch, bool RefusedThere) OpenNearest(string? above)
    {
        var tries = Depth(_path);
        var refusedThere = false;

        for (var tried = 0; above is not null && tried < tries; tried++)
        {
            if (Open(above) is { } watch) return (above, watch, refusedThere);

            var again = NearestAbove(_path, _exists);
            var stillThere = PathRules.Same(again, above);

            if (tried == 0) refusedThere = stillThere;

            above = stillThere ? NearestAbove(above, _exists) : again;
        }

        return (null, null, refusedThere);
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

    /// <summary>
    /// Ends the wait. Never waits on the disk: a check still running lets go
    /// of whatever it opens once it is done.
    ///
    /// **A return already decided may still be announced after this
    /// returns** (batch-0.11.2e QA, round 8: once in 2,000 hammer rounds on
    /// Linux). The return is decided under the lock and announced outside
    /// it, so that the callback never runs under the lock that this takes;
    /// a check that decided just before this call announces just after it.
    /// Closing that gap would mean waiting here for the callback to finish,
    /// and this never waits. So the callback must guard against a wait it no
    /// longer wants — the pane's checks its load generation, its path and
    /// whether it is disposed. A return decided after this call is never
    /// announced.
    /// </summary>
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

    /// <summary>
    /// Completes once no look is running, or at <paramref name="deadline"/>,
    /// whichever is first; true when none is.
    ///
    /// **Disposing does not stop a look already under way** (see Dispose), and
    /// a look opens a watch on a folder above the one it waits for — which,
    /// for a pane waiting inside a folder about to be renamed, is a folder
    /// inside it. The look lets go of what it opened once it sees it was
    /// disposed; this is how somebody who needs that folder free waits for it
    /// (rename-notes, review finding 3). Waited on a timer, never by holding a
    /// thread.
    /// </summary>
    public async Task<bool> WhenIdleAsync(TimeSpan deadline)
    {
        var until = DateTime.UtcNow + deadline;

        while (true)
        {
            lock (_gate)
            {
                if (!_running) return true;
            }

            if (DateTime.UtcNow >= until) return false;

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
