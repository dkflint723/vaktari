using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>A single access flag, made two-way bindable for a checkbox.</summary>
public sealed partial class AccessToggleViewModel : ObservableObject
{
    private readonly AccessToggle _model;

    public AccessToggleViewModel(AccessToggle model)
    {
        _model = model;
        _value = model.Value;
    }

    public string Key => _model.Key;
    public string Group => _model.Group;
    public string Label => _model.Label;

    [ObservableProperty] private bool _value;

    public AccessToggle ToModel() => _model with { Value = Value };
}

public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly IPropertiesProvider _provider;
    private readonly IReadOnlyList<string> _paths;
    private CancellationTokenSource? _measureCts;

    private readonly IAccessEditor? _access;

    public PropertiesViewModel(
        IPropertiesProvider provider, IReadOnlyList<string> paths, IAccessEditor? access = null)
    {
        _provider = provider;
        _paths = paths;
        _access = access;
    }

    // ---- permissions ---------------------------------------------------

    public ObservableCollection<AccessToggleViewModel> Access { get; } = new();

    [ObservableProperty] private bool _canEditAccess;
    [ObservableProperty] private bool _canRecurse;
    [ObservableProperty] private bool _applyRecursively;
    [ObservableProperty] private string _accessSummary = "";
    [ObservableProperty] private string _accessStatus = "";

    // ---- who it belongs to ------------------------------------------------
    //
    // **The two names were text.** A mode is three sets of bits and two
    // principals, and a sheet that let you set the bits and not the principals
    // answered two thirds of the question -- "group: read, write" says nothing
    // until you know WHICH group. Both desktops offer them.

    [ObservableProperty] private bool _hasOwnership;
    [ObservableProperty] private bool _canEditOwner;
    [ObservableProperty] private bool _canEditGroup;
    [ObservableProperty] private string _owner = "";
    [ObservableProperty] private string _group = "";

    public ObservableCollection<string> OwnerChoices { get; } = new();
    public ObservableCollection<string> GroupChoices { get; } = new();

    /// <summary>
    /// What was on the file when the window opened.
    ///
    /// **Compared rather than sent unconditionally.** chown is refused for
    /// everybody but root, so a sheet that ran it on every Apply would report a
    /// permission failure to somebody who only ticked a box -- and it would be
    /// telling the truth about a change they never asked for.
    /// </summary>
    private string _loadedOwner = "";
    private string _loadedGroup = "";

    private async Task LoadAccessAsync(string path, bool isDirectory)
    {
        if (_access is not { CanEdit: true }) return;

        var state = await _access.GetAccessAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (state is null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Access.Clear();
            foreach (var toggle in state.Toggles) Access.Add(new AccessToggleViewModel(toggle));

            AccessSummary = state.Summary;
            CanEditAccess = true;
            CanRecurse = isDirectory;

            HasOwnership = state.Ownership is not null;

            if (state.Ownership is { } owned)
            {
                _loadedOwner = Owner = owned.Owner;
                _loadedGroup = Group = owned.Group;

                Fill(OwnerChoices, owned.Owners);
                Fill(GroupChoices, owned.Groups);

                CanEditOwner = owned.CanChangeOwner;
                CanEditGroup = owned.CanChangeGroup;
            }
        });
    }

    /// <summary>
    /// Hands the files to whoever the two boxes now name, if anybody changed
    /// them.
    ///
    /// <returns>Null when there was nothing to do or it took; the reason in
    /// words when it did not.</returns>
    ///
    /// **Nothing sent when nothing moved.** chown is refused for everybody but
    /// root, so running it on every Apply would report a permission failure to
    /// somebody who only ticked a box — and it would be telling the truth about
    /// a change they had not asked for.
    ///
    /// The first refusal ends it, and that is the same rule the recursive apply
    /// follows: the reason is almost always about the caller rather than about
    /// the file, so carrying on collects the same sentence once per path.
    /// </summary>
    private async ValueTask<string?> HandOverAsync()
    {
        if (_access is null || !HasOwnership) return null;
        if (Owner == _loadedOwner && Group == _loadedGroup) return null;

        foreach (var path in _paths)
        {
            var refused = await _access
                .SetOwnershipAsync(path, Owner, Group, ApplyRecursively, CancellationToken.None)
                .ConfigureAwait(false);

            if (refused is not null) return refused;
        }

        return null;
    }

    /// <summary>
    /// Replaces the contents in place. The collections are bound, so assigning
    /// a new one would leave the box pointed at the old list.
    /// </summary>
    private static void Fill(ObservableCollection<string> into, IReadOnlyList<string> names)
    {
        into.Clear();

        foreach (var name in names) into.Add(name);
    }

    [RelayCommand]
    private async Task ApplyAccessAsync()
    {
        if (_access is null || _paths.Count == 0) return;

        AccessStatus = "applying…";

        var toggles = Access.Select(a => a.ToModel()).ToList();
        var progress = new Progress<int>(done => AccessStatus = $"{done:N0} entries…");

        try
        {
            var skipped = 0;
            Exception? first = null;

            foreach (var path in _paths)
            {
                var outcome = await _access.SetAccessAsync(
                    path, toggles, ApplyRecursively, progress, CancellationToken.None)
                    .ConfigureAwait(false);

                skipped += outcome.Skipped;
                first ??= outcome.FirstFailure;
            }

            // **The names before the read-back, or the read-back reports the
            // old ones.** LoadAccessAsync re-reads the owner from the file, so
            // a chown applied after it would show as having done nothing until
            // the window was reopened.
            var handover = await HandOverAsync().ConfigureAwait(false);

            // Read back rather than trusting what we sent — the filesystem may
            // have refused part of it, and showing the request as if it were
            // the result would be a lie.
            await LoadAccessAsync(_paths[0], CanRecurse).ConfigureAwait(false);

            if (handover is { } refused)
            {
                await Dispatcher.UIThread.InvokeAsync(() => AccessStatus = refused);
                return;
            }

            // **"applied" only when it was.** A recursive apply skips whatever
            // it cannot write, and saying so is the whole point of a
            // permissions dialog: a tree where every child belongs to another
            // user used to look exactly like one where the change took.
            await Dispatcher.UIThread.InvokeAsync(() =>
                AccessStatus = skipped == 0
                    ? "applied"
                    : first is null
                        ? $"applied, but {skipped:N0} item(s) would not change"
                        : $"applied, but {skipped:N0} item(s) would not change — "
                          + Vaktari.Core.FileSystem.Failures.Describe(first, "change all of those"));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                AccessStatus = Vaktari.Core.FileSystem.Failures.Describe(ex, "read that"));
        }
    }

    public ObservableCollection<PropertyGroup> Groups { get; } = new();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _canMeasure;
    [ObservableProperty] private bool _isMeasuring;

    // ---- checksums -------------------------------------------------------
    //
    // A section rather than a tab: this window is a flat run of sections and
    // adding a TabControl for one more would restructure it for no gain.
    //
    // Opt-in, like measuring a folder. Hashing a multi-gigabyte file is real
    // work, and doing it automatically to every file whose properties you open
    // would make the window slow for the common case in service of the rare one.

    private CancellationTokenSource? _hashCts;

    /// <summary>
    /// Stops whatever is still running when the window goes.
    ///
    /// A folder measurement walks the whole tree and a checksum reads the whole
    /// file, and both carried on after the dialog closed — nothing lands
    /// anywhere wrong, but a gigabyte of disk reads for a window nobody can see
    /// is work with no beneficiary, and on a laptop it is battery.
    /// </summary>
    public void CancelBackgroundWork()
    {
        try { _measureCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _hashCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    [ObservableProperty] private bool _canChecksum;
    [ObservableProperty] private bool _isHashing;
    [ObservableProperty] private string _hashStatus = "";
    [ObservableProperty] private string _md5 = "";
    [ObservableProperty] private string _sha1 = "";
    [ObservableProperty] private string _sha256 = "";

    public bool HasChecksums => Md5.Length > 0;

    partial void OnMd5Changed(string value) => OnPropertyChanged(nameof(HasChecksums));

    /// <summary>A real bool: binding a string's Length straight to IsVisible
    /// does not convert under compiled bindings.</summary>
    public bool HasHashStatus => HashStatus.Length > 0;

    partial void OnHashStatusChanged(string value) => OnPropertyChanged(nameof(HasHashStatus));

    /// <summary>
    /// Label doubles as the cancel affordance, as Measure does.
    ///
    /// **Both words were lower case, on a window whose Apply button was
    /// not.** Sentence case is the one rule now; FolderSizeTests holds
    /// both branches of this and both of MeasureLabel.
    /// </summary>
    public string ChecksumButtonText => IsHashing ? "Stop" : "Compute";

    partial void OnIsHashingChanged(bool value)
        => OnPropertyChanged(nameof(ChecksumButtonText));

    [RelayCommand]
    private async Task ComputeChecksumsAsync()
    {
        if (IsHashing)
        {
            _hashCts?.Cancel();
            return;
        }

        if (_paths.Count != 1 || !File.Exists(_paths[0])) return;

        _hashCts?.Dispose();
        _hashCts = new CancellationTokenSource();
        var ct = _hashCts.Token;

        IsHashing = true;
        HashStatus = "reading…";
        Md5 = Sha1 = Sha256 = "";

        var progress = new Progress<double>(fraction =>
            HashStatus = $"{fraction:P0}");

        try
        {
            var result = await Checksums.ComputeAsync(_paths[0], progress, ct)
                                        .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Md5 = result.Md5;
                Sha1 = result.Sha1;
                Sha256 = result.Sha256;
                HashStatus = "";
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => HashStatus = "cancelled");
        }
        catch (Exception ex)
        {
            // Unreadable, vanished, or a permission problem — said out loud
            // rather than leaving three empty rows and no explanation.
            await Dispatcher.UIThread.InvokeAsync(() =>
                HashStatus = Vaktari.Core.FileSystem.Failures.Describe(ex, "read that"));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsHashing = false);
        }
    }

    public async Task LoadAsync()
    {
        if (_paths.Count == 0) return;

        if (_paths.Count > 1)
        {
            await LoadManyAsync().ConfigureAwait(false);
            return;
        }

        var details = await _provider.GetAsync(_paths[0], CancellationToken.None)
                                     .ConfigureAwait(false);

        await LoadAccessAsync(_paths[0], details.IsDirectory).ConfigureAwait(false);

        // **On the pool deliberately, not for tidiness.** The Windows provider's
        // GetAsync returns an already-completed ValueTask, so nothing above here
        // has yielded and the continuation is still running on the UI thread that
        // opened the window -- and reading a volume is a stat. Volumes.MountPoints
        // carries the measurement that made that matter: on Unix DriveInfo.IsReady
        // is a Directory.Exists, and a stat on a hung NFS or sshfs mount does not
        // return.
        var volume = await Task.Run(
            () => VolumeProperties.Describe(_paths[0], details.IsDirectory))
            .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = details.Name;
            Location = Path.GetDirectoryName(details.FullPath) ?? details.FullPath;
            Kind = details.Kind;
            SizeText = details.IsDirectory ? "not measured" : ByteSize.Format(details.Size);
            CanMeasure = details.IsDirectory;

            // **Both references start counting the moment the window opens**,
            // and this waited to be asked — so the one figure somebody opens a
            // folder's properties FOR was the one thing not on the page.
            if (ShouldMeasureNow) _ = MeasureAsync();

            // Only ever for one actual file: a folder has no digest, and
            // hashing a multi-selection would be three answers to a question
            // nobody asked in the singular.
            CanChecksum = !details.IsDirectory && _paths.Count == 1;

            var general = new List<PropertyRow>();

            if (details.SymlinkTarget is { } target)
                general.Add(new PropertyRow("symlink to", target));

            if (details.Modified is { } modified)
                general.Add(new PropertyRow("modified", modified.ToString("yyyy-MM-dd HH:mm:ss")));
            if (details.Accessed is { } accessed)
                general.Add(new PropertyRow("accessed", accessed.ToString("yyyy-MM-dd HH:mm:ss")));
            if (details.Created is { } created)
                general.Add(new PropertyRow("created", created.ToString("yyyy-MM-dd HH:mm:ss")));

            Groups.Clear();
            if (general.Count > 0) Groups.Add(new PropertyGroup("general", general));

            // Above the platform's own groups: where this sits is part of the
            // same "what am I looking at" question the general rows answer,
            // where permissions and ownership are about the item itself.
            if (volume is { } room) Groups.Add(room);

            foreach (var group in details.Groups) Groups.Add(group);
        });
    }

    private async Task LoadManyAsync()
    {
        long total = 0;
        var files = 0;
        var folders = 0;

        foreach (var path in _paths)
        {
            if (Directory.Exists(path)) folders++;
            else if (File.Exists(path)) { files++; total += new FileInfo(path).Length; }
        }

        // **The lower half of this window was empty for a selection.** One item
        // fills it from the platform's own groups and a selection asked for
        // none — so the window that Windows falls back to for a multi-select,
        // having declined the shell's sheet, showed a count and a total and
        // then nothing where that sheet shows read-only and hidden.
        //
        // One call for the whole list rather than GetAsync per path: that call
        // is per-item expensive by design, and looping it would spawn a `stat`
        // per file on Linux — and an `xdg-mime` on top for every one the glob
        // table cannot name — to fill one panel.
        var shared = await _provider.GetSharedAsync(_paths, CancellationToken.None)
                                    .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = $"{_paths.Count} items";
            Location = Path.GetDirectoryName(_paths[0]) ?? "";
            Kind = "multiple selection";

            // Folders are counted but not walked — measuring is opt-in here too.
            SizeText = folders > 0
                ? $"{ByteSize.Format(total)} in {files} file(s), plus {folders} folder(s) unmeasured"
                : $"{ByteSize.Format(total)} in {files} file(s)";

            CanMeasure = folders > 0;

            foreach (var group in shared) Groups.Add(group);

            if (ShouldMeasureNow) _ = MeasureAsync();
        });
    }

    /// <summary>
    /// Whether to start counting without being asked.
    ///
    /// **Local disks only.** Measuring walks the whole tree, which over SMB or
    /// SFTP is a round trip per directory — so opening properties on a folder
    /// of a mounted share would spend the connection before anybody had decided
    /// they wanted the number. On this machine's own disks it is the figure the
    /// window is opened for, and both references produce it unasked.
    ///
    /// Asked of ThumbnailLoader because that is where the judgement already
    /// lives, roots and UNC shape and all — its own comment says asking the
    /// question twice from two lists is how the two come to disagree.
    ///
    /// Any remote path in a multiple selection is enough to wait: the walk
    /// would cross it either way.
    /// </summary>
    private bool ShouldMeasureNow
        => CanMeasure && !_paths.Any(Thumbnails.ThumbnailLoader.IsRemote);

    /// <summary>
    /// **The stop button was disabled while measuring.** This is one command
    /// that both starts and stops, and an async RelayCommand refuses a second
    /// execution while the first is running — so CanExecute went false the
    /// instant the walk began, the button that then reads "Stop" greyed out,
    /// and the cancel branch below could not be reached from the interface at
    /// all. A measurement of a home directory ran to the end whatever anybody
    /// pressed.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            _measureCts?.Cancel();
            return;
        }

        _measureCts?.Dispose();
        _measureCts = new CancellationTokenSource();
        var ct = _measureCts.Token;

        IsMeasuring = true;

        var progress = new Progress<SizeProgress>(p =>
            SizeText = $"{ByteSize.Format(p.Bytes)} · {p.Files:N0} files · {p.Folders:N0} folders…");

        try
        {
            // **The loose files were dropped from the total.** A mixed
            // selection reads "12 MB in 3 file(s), plus 1 folder(s)
            // unmeasured" until you press measure, and then reported only what
            // the FOLDER held — a smaller number than the line it replaced,
            // for an operation whose whole purpose is to make the number
            // bigger and right.
            long bytes = 0;
            var files = 0;
            var folders = 0;

            foreach (var path in _paths.Where(p => !Directory.Exists(p)))
            {
                try
                {
                    var info = new FileInfo(path);

                    if (!info.Exists) continue;

                    bytes += info.Length;
                    files++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A file that will not answer is left out of the count, the
                    // same as an unreadable folder inside the walk.
                }
            }

            foreach (var path in _paths.Where(Directory.Exists))
            {
                var result = await _provider.MeasureAsync(path, progress, ct).ConfigureAwait(false);
                bytes += result.Bytes;
                files += result.Files;
                folders += result.Folders;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                SizeText = $"{ByteSize.Format(bytes)} · {files:N0} files · {folders:N0} folders");
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SizeText += " (cancelled)");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsMeasuring = false);
        }
    }

}
