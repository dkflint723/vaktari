using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// The watch on the folder kdeglobals lives in, which outlives the folder.
///
/// **A config folder deleted and made again was never heard from again**
/// (batch-0.11.2b QA, KdeGoneProbe). The theme watcher is started once per
/// process, and once its folder was gone the watch had nothing left to watch
/// — the new folder at the same name is a different one — while every later
/// window found a watcher already there and started none. A theme switch after
/// that reached no window until the application was restarted.
///
/// So the folder going is heard (<see cref="ChangeKind.Gone"/>), and the watch
/// moves to the folder above it, where the config folder's coming back is a
/// name arriving; then it moves back, and says the file may have changed,
/// since it may have been written before the watch was there again. If the
/// folder above cannot be watched either, the watch lapses, and the next
/// provider made starts a new one, as a provider did when there was none.
/// </summary>
internal sealed class ConfigFolderWatch : IDisposable
{
    private const string FileName = "kdeglobals";

    private readonly Lock _gate = new();
    private readonly Action _changed;
    private IDisposable? _current;
    private bool _onParent;
    private bool _lapsed;
    private bool _disposed;

    private ConfigFolderWatch(string folder, Action changed)
    {
        Folder = folder;
        _changed = changed;
    }

    /// <summary>The config folder this watches for kdeglobals.</summary>
    public string Folder { get; }

    /// <summary>Whether the config folder itself is watched now, rather than
    /// the folder above it while it is gone. For the tests.</summary>
    public bool OnFolder
    {
        get { lock (_gate) return _current is not null && !_onParent; }
    }

    /// <summary>Whether this watch has nothing left to watch with: a new one
    /// is to be started in its place.</summary>
    public bool Lapsed
    {
        get { lock (_gate) return _lapsed; }
    }

    /// <summary>
    /// A watch on <paramref name="folder"/> that calls <paramref name="changed"/>
    /// when kdeglobals there is written, or null when the folder is not there
    /// or cannot be watched.
    /// </summary>
    public static ConfigFolderWatch? Start(string folder, Action changed)
    {
        if (!Directory.Exists(folder)) return null;

        var watch = new ConfigFolderWatch(folder, changed);

        lock (watch._gate)
        {
            if (!watch.ArmFolderLocked()) return null;
        }

        return watch;
    }

    /// <summary>Watches the config folder itself. Under the gate.</summary>
    private bool ArmFolderLocked()
    {
        try
        {
            _current = FolderWatch.Open(Folder, OnFolderChange);
            _onParent = false;
            return true;
        }
        catch (Exception ex)
        {
            // No watcher is survivable; the theme just won't follow live
            // changes. The inotify ceiling is the likeliest reason.
            Vaktari.Core.Quiet.Swallowed("theme", ex);
            return false;
        }
    }

    private void OnFolderChange(FileSystemChange change)
    {
        switch (change.Kind)
        {
            case ChangeKind.Gone:
                FolderGone();
                break;

            // Dropped events may have been the write.
            case ChangeKind.Lost:
                _changed();
                break;

            // Plasma rewrites the file on every scheme change, so this is how
            // a theme switch reaches a running application without polling.
            // Any change that names it: a write, a create, and a rename to
            // it — the way an atomic save lands a new copy over the old,
            // which the FileSystemWatcher's two handlers (Changed, Created)
            // never answered.
            default:
                if (Names(change.Path) || (change.OldPath is { } old && Names(old))) _changed();
                break;
        }
    }

    private static bool Names(string path) => Path.GetFileName(path) == FileName;

    /// <summary>The config folder has gone: wait for it in the folder above.</summary>
    private void FolderGone()
    {
        bool back;

        lock (_gate)
        {
            if (_disposed || _onParent) return;

            var gone = _current;
            _current = null;

            var parent = Path.GetDirectoryName(Folder);
            IDisposable? above = null;

            try
            {
                if (parent is not null) above = FolderWatch.Open(parent, OnParentChange);
            }
            catch (Exception ex)
            {
                Vaktari.Core.Quiet.Swallowed("theme", ex);
            }

            // Watched above before the old watch goes, so the instance is not
            // let go of and opened again in between.
            gone?.Dispose();

            if (above is null)
            {
                _lapsed = true;
                return;
            }

            _current = above;
            _onParent = true;

            // Made again before the folder above was watched, and so never
            // heard arriving.
            back = Directory.Exists(Folder);
        }

        if (back) Rearm();
    }

    private void OnParentChange(FileSystemChange change)
    {
        switch (change.Kind)
        {
            case ChangeKind.Gone:
                lock (_gate)
                {
                    if (_disposed || !_onParent) return;

                    _current?.Dispose();
                    _current = null;
                    _lapsed = true;
                }

                break;

            case ChangeKind.Lost:
            case ChangeKind.Added:
            case ChangeKind.Renamed:
                if ((change.Kind == ChangeKind.Lost || PathRules.Same(change.Path, Folder))
                    && Directory.Exists(Folder))
                {
                    Rearm();
                }

                break;
        }
    }

    /// <summary>The config folder is back: watch it again, and say the file
    /// may have changed meanwhile.</summary>
    private void Rearm()
    {
        lock (_gate)
        {
            if (_disposed || !_onParent) return;

            var above = _current;

            if (!ArmFolderLocked())
            {
                // Still watching above; a later arrival tries again.
                return;
            }

            above?.Dispose();
        }

        _changed();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            _current?.Dispose();
            _current = null;
        }
    }
}
