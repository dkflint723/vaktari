using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Rail plus switchable panel — VS Code's activity bar. The rail decides what
/// the panel shows, so each panel gets full height instead of competing for it,
/// and adding one later is a registration rather than a redesign.
///
/// A panel earns a rail slot only if it is a place you navigate *from* or a
/// result set that should survive navigation. Everything else is a command.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly IPlacesProvider? _places;
    /// <summary>
    /// A SOURCE rather than the thing itself. The trash is installed well after
    /// the shell is built — the window sets it while wiring up the hourly sweep
    /// — so a constructor that captured the value would capture null and the
    /// bin would never fill.
    /// </summary>
    private readonly Func<Vaktari.Core.FileSystem.ITrashMaintenance?>? _trash;

    public SidebarViewModel(
        IPlacesProvider? places,
        Func<Vaktari.Core.FileSystem.ITrashMaintenance?>? trash = null)
    {
        _places = places;
        _trash = trash;

        if (places is not null)
            places.PlacesChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
    }

    public ObservableCollection<PlaceGroupViewModel> Groups { get; } = new();




    [ObservableProperty] private RailState _rail = RailState.Full;
    [ObservableProperty] private double _width = 210;

    // One sidebar, every section reachable at once — the point of the workspace
    // layout is that the application never decides which of them you can see.
    // Folding is the other thing: the PERSON decides, one section at a time,
    // and it stays that way because they asked for it.
    //
    // An `ActivePanel` used to sit beside this, persisted in the session and
    // restored, with `ShowPanel` as its only mutator — and nothing ever called
    // that. Removed 30 July 2026: state that cannot be changed is not state.
    public bool IsPanelVisible => Rail != RailState.Hidden;

    [ObservableProperty] private bool _isSearching;

    // ---- folding -----------------------------------------------------------

    /// <summary>
    /// Which sections are folded away.
    ///
    /// **A full sidebar was a scrolling sidebar.** Eight groups — Places,
    /// Devices, Shares, Network, Remote, Sharing, Recent and the bin — do not
    /// fit above the fold on a laptop, so reaching Recent meant scrolling past
    /// four sections that were never going to be clicked, and both references
    /// let you fold a heading away. Nothing is folded until somebody folds it.
    ///
    /// Held here rather than on the groups because a group is rebuilt from the
    /// desktop's own list every time anything changes — plug in a stick and
    /// every PlaceGroupViewModel is thrown away. State that lives on one would
    /// last until the next reload.
    ///
    /// Without case, because half the keys are provider labels and a provider
    /// is free to change how it capitalises one between two runs.
    /// </summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the session stores. Order is not meaningful.</summary>
    public IReadOnlyList<string> CollapsedSections => _collapsed.ToList();

    public bool IsCollapsed(string key) => _collapsed.Contains(key);

    /// <summary>
    /// Folds or unfolds one section.
    ///
    /// Raises <see cref="CollapsedSections"/> whatever the key, because that is
    /// what the shell watches to know the session has changed — a per-key
    /// notification would need the shell to know every key there is.
    /// </summary>
    public void SetCollapsed(string key, bool collapsed)
    {
        var changed = collapsed ? _collapsed.Add(key) : _collapsed.Remove(key);

        if (!changed) return;

        OnPropertyChanged(nameof(CollapsedSections));

        // The four written into the markup bind to their own property, so each
        // has to be named. A provider group reads through IsCollapsed on its
        // own view model and is notified there.
        OnPropertyChanged(key switch
        {
            SidebarSections.Network => nameof(IsNetworkCollapsed),
            SidebarSections.Remote => nameof(IsRemoteCollapsed),
            SidebarSections.Sharing => nameof(IsSharingCollapsed),
            SidebarSections.Recent => nameof(IsRecentCollapsed),
            _ => nameof(CollapsedSections),
        });
    }

    /// <summary>
    /// Takes the folded set from a restored session.
    ///
    /// Applied before the places load, so the first list to arrive is already
    /// folded the way it was left rather than opening and shutting on screen.
    /// </summary>
    public void RestoreCollapsed(IEnumerable<string> keys)
    {
        _collapsed.Clear();

        foreach (var key in keys)
            if (!string.IsNullOrWhiteSpace(key)) _collapsed.Add(key);

        OnPropertyChanged(nameof(CollapsedSections));
        OnPropertyChanged(nameof(IsNetworkCollapsed));
        OnPropertyChanged(nameof(IsRemoteCollapsed));
        OnPropertyChanged(nameof(IsSharingCollapsed));
        OnPropertyChanged(nameof(IsRecentCollapsed));
    }

    public bool IsNetworkCollapsed
    {
        get => IsCollapsed(SidebarSections.Network);
        set => SetCollapsed(SidebarSections.Network, value);
    }

    public bool IsRemoteCollapsed
    {
        get => IsCollapsed(SidebarSections.Remote);
        set => SetCollapsed(SidebarSections.Remote, value);
    }

    public bool IsSharingCollapsed
    {
        get => IsCollapsed(SidebarSections.Sharing);
        set => SetCollapsed(SidebarSections.Sharing, value);
    }

    public bool IsRecentCollapsed
    {
        get => IsCollapsed(SidebarSections.Recent);
        set => SetCollapsed(SidebarSections.Recent, value);
    }

    // ---- navigation --------------------------------------------------------
    //
    // The frequently-visited list was REMOVED at the user's request once Recent
    // files and Recent locations existed — recency covers what frequency was
    // being used for, and two ranked lists of folders in one sidebar is one too
    // many. Git has it. The visit-count store it read has since been deleted
    // too, having had no other reader.
    //
    // What survives is the callback: the shell owns what a click does, and both
    // the recent entries reach it this way.

    private Action<string>? _onFolderChosen;

    /// <summary>Wired by the shell, which is the only place that knows which
    /// pane is active.</summary>
    public void AttachNavigation(Action<string> onChosen) => _onFolderChosen = onChosen;

    // ---- recent ------------------------------------------------------------
    //
    // Two fixed entries rather than a bound collection: they never change, so a
    // collection plus an item record would be machinery serving two buttons.
    // They are always shown, even on a first run when both listings are empty —
    // Dolphin does the same, and an entry that appears out of nowhere once you
    // have opened enough files is harder to find than one that was always there.
    //
    // Reuses _onFolderChosen, which is how frequent already reaches the
    // shell: the store holds the data, the shell decides what a click does.

    /// <summary>
    /// Where the active pane is, so a row can show that it is the one being
    /// viewed. Set by the shell — the sidebar has no idea which pane is active
    /// and should not learn.
    ///
    /// Compared with PathRules.Same, which is what that method is for: a
    /// trailing separator trimmed, both separators treated as one, and the
    /// platform's own case rule applied. `/home/flint` and `/home/flint/` are
    /// the same place, so are `C:\Users` and `C:/Users`, and on Windows so are
    /// `C:\Users\flint` and the `c:\users\flint` a user may well have typed
    /// into the location bar. A place list that quietly fails to highlight Home
    /// over any of those would be baffling.
    /// </summary>
    public void SetCurrentPath(string? path)
    {
        var wanted = Normalise(path);

        foreach (var group in Groups)
        foreach (var item in group.Places)
            item.IsCurrent = PathRules.Same(item.Path, path);

        CurrentPath = wanted;
        OnPropertyChanged(nameof(IsRecentFilesCurrent));
        OnPropertyChanged(nameof(IsRecentLocationsCurrent));
        OnPropertyChanged(nameof(IsComputerCurrent));
    }

    private static string Normalise(string? path)
        => PathRules.Normalise(path);

    /// <summary>The active path, for the fixed entries that are not in Groups.</summary>
    public string CurrentPath { get; private set; } = "";

    public bool IsRecentFilesCurrent => CurrentPath == VirtualPaths.Files;
    public bool IsRecentLocationsCurrent => CurrentPath == VirtualPaths.Locations;

    /// <summary>What the machine's own listing is called here.</summary>
    public string ComputerLabel => Vaktari.Core.Naming.ComputerTitle;

    public bool IsComputerCurrent => CurrentPath == VirtualPaths.Computer;

    [RelayCommand]
    private void OpenComputer() => _onFolderChosen?.Invoke(VirtualPaths.Computer);

    [RelayCommand]
    private void OpenRecentFiles() => _onFolderChosen?.Invoke(VirtualPaths.Files);

    [RelayCommand]
    private void OpenRecentLocations() => _onFolderChosen?.Invoke(VirtualPaths.Locations);

    /// <summary>
    /// Remote locations the desktop has mounted. Shown beside Devices because
    /// that is what they are from here — a path you can open, whatever protocol
    /// is behind it.
    /// </summary>
    public ObservableCollection<RemoteMount> Remotes { get; } = new();

    public bool HasRemotes => Remotes.Count > 0;

    private IRemoteMounts? _mounts;

    public void UseRemotes(IRemoteMounts? mounts)
    {
        _mounts = mounts;
        RefreshRemotes();
    }

        public void RefreshRemotes()
    {
        Remotes.Clear();

        foreach (var mount in _mounts?.Discover() ?? []) Remotes.Add(mount);

        // Published here because this is the one place that knows what is
        // mounted; thumbnails need it to tell a network file from a local one
        // without re-reading the mount table per row.
        // **Mapped network drives count too.** This was fed only from
        // IRemoteMounts.Discover(), which deliberately skips lettered
        // connections — so Z: was never remote, and RowIcon's folder-contents
        // probe ran a directory read per visible row over SMB: exactly the
        // round-trip storm its own comment exists to prevent.
        var roots = Remotes.Select(m => m.Path).ToList();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.DriveType == DriveType.Network)
                    roots.Add(drive.RootDirectory.FullName);
        }
        catch (Exception ex)
        {
            // A drive that will not answer is one we cannot mark remote, which
            // is the old behaviour rather than a failure.
            Quiet.Swallowed("places", ex);
        }

        Thumbnails.ThumbnailLoader.RemoteRoots = roots;

        OnPropertyChanged(nameof(HasRemotes));
    }




    partial void OnRailChanged(RailState value) => NotifyVisibility();

    private void NotifyVisibility() => OnPropertyChanged(nameof(IsPanelVisible));

    /// <summary>Two states now, not three: with no icon rail there is nothing
    /// meaningful between "shown" and "hidden".</summary>
    [RelayCommand]
    public void CycleRail() => Rail = Rail == RailState.Hidden ? RailState.Full : RailState.Hidden;

    /// <summary>
    /// Writes the pinned order the rows are currently in back to the provider.
    ///
    /// **Both providers have implemented this since they were written and
    /// nothing ever called it.** The pins were persisted in the order they were
    /// added and could be reordered only by editing places.json by hand — so a
    /// sidebar that had grown past a handful of pins could never be tidied,
    /// which is the point at which tidying starts to matter. Explorer and
    /// Dolphin both reorder by dragging.
    ///
    /// Every group's pins in one list, because the provider is asked about all
    /// of them at once and a group left out of the list would be sorted to the
    /// end. In practice there is one group with pins in it; the loop does not
    /// need to know that.
    ///
    /// No reload afterwards. The rows are already in the order being saved —
    /// the drag put them there — and rebuilding would flash the whole sidebar
    /// to land on what is already on screen. A later reload for its own reasons
    /// reads the saved order and agrees.
    /// </summary>
    public async Task SavePinOrderAsync()
    {
        if (_places is not { } places) return;

        var order = Groups
            .SelectMany(g => g.Places)
            .Where(p => p.IsUserPinned)
            .Select(p => p.Id)
            .ToList();

        if (order.Count == 0) return;

        try
        {
            await places.ReorderAsync(order, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The rows are already where they were dragged; a failure to write
            // the order down is a thing to note rather than to undo on screen.
            Quiet.Swallowed("places-reorder", ex);
        }
    }

    public async Task InitializeAsync()
    {
        if (_places is not { } places) return;

        // Off the calling thread — which is the UI thread, during startup.
        // Importing reads every .lnk in Links and Network Shortcuts and
        // resolves each one through the shell, which is a lot of disk before
        // the window has drawn anything.
        await Task.Run(() => places.ImportExistingAsync(CancellationToken.None).AsTask())
                  .ConfigureAwait(false);

        await ReloadAsync().ConfigureAwait(false);
    }

    private Task? _reloading;
    private bool _reloadDirty;

    /// <summary>
    /// Rebuilds the sidebar, coalescing overlapping requests.
    ///
    /// **Coalesced, and this is required rather than defensive.** Until devices
    /// were watched, this ran only at startup and after a pin, so two could
    /// never overlap. Driven by arrivals and removals they will: a stick with
    /// four partitions, or an eject, which changes the mount table while the
    /// rebuild it triggered is still running. Each overlapping rebuild parks a
    /// thread-pool thread for the whole SMB timeout on a machine with a dead
    /// mapped drive, so they must not stack.
    ///
    /// **A caller who awaits this still gets a finished rebuild.** The first
    /// version returned immediately when one was already running, which quietly
    /// broke the promise every existing caller was written against — UnpinAsync
    /// awaits this and then expects the row to be gone. Instead the request is
    /// folded into the run in flight, which loops again for it, and the caller
    /// awaits that whole chain.
    /// </summary>
    private readonly object _reloadGate = new();

    public Task ReloadAsync()
    {
        if (_places is not { } places) return Task.CompletedTask;

        lock (_reloadGate)
        {
            if (_reloading is { } inFlight)
            {
                _reloadDirty = true;
                return inFlight;
            }

            _reloading = RunReloadsAsync(places);

            return _reloading;
        }
    }

    /// <summary>
    /// **Locked, because this loop does not stay on one thread.** The rebuild
    /// inside hops to the thread pool and back, so the "anything else asked
    /// while I was working?" check and the caller setting that flag genuinely
    /// run at the same time. Without the lock there is a window between the
    /// last check and clearing the in-flight task where a request is folded
    /// into a run that is already finishing — and the caller awaits a task that
    /// completes without ever doing their rebuild.
    ///
    /// It is a narrow window, and it does not stay theoretical: it failed
    /// roughly one run in four on this machine and would have been a rare,
    /// unreproducible stale sidebar in the hand.
    /// </summary>
    /// <summary>
    /// Re-asks the bin whether it is holding anything and marks its row.
    ///
    /// Public because the answer changes without the PLACES changing: binning a
    /// file, restoring one and emptying the bin all leave the sidebar's rows
    /// exactly as they were, so a full reload would be both wasteful and, on
    /// its own, not something any of those three currently ask for.
    /// </summary>
    public void RefreshBinState()
    {
        if (_trash?.Invoke() is not { } trash) return;

        var holding = Holding(trash);

        foreach (var group in Groups)
            foreach (var place in group.Places)
                if (place.IsBin) place.BinHasItems = holding;
    }

    /// <summary>
    /// Whether the bin is holding anything, or false if it will not say. A bin
    /// that cannot be read is drawn empty rather than full: the glyph is a hint,
    /// and the wrong hint is worse than the plain one.
    /// </summary>
    private static bool Holding(Vaktari.Core.FileSystem.ITrashMaintenance trash)
    {
        try { return trash.HasAny(); }
        catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("bin-state", ex); return false; }
    }

    private async Task RunReloadsAsync(Vaktari.Core.Places.IPlacesProvider places)
    {
        while (true)
        {
            lock (_reloadGate) _reloadDirty = false;

            try
            {
                await ReloadOnceAsync(places).ConfigureAwait(false);
            }
            catch
            {
                lock (_reloadGate) _reloading = null;
                throw;
            }

            lock (_reloadGate)
            {
                if (!_reloadDirty)
                {
                    _reloading = null;
                    return;
                }
            }
        }
    }

    private async Task ReloadOnceAsync(Vaktari.Core.Places.IPlacesProvider places)
    {

        // **The providers are synchronous behind an async signature**, so this
        // ran wherever it was called from, and it is called from the UI thread
        // at startup and after every pin. On Windows it enumerates drives and
        // asks each one whether it is ready, its size and its label — and a
        // mapped drive whose server is gone answers those by blocking for the
        // SMB timeout. That is a window frozen before it has drawn, for as long
        // as the network takes to give up, and nothing on screen to say why.
        //
        // Task.Run here rather than inside each provider: there are two of them
        // and one caller, and the caller is the part that knows it is on the UI
        // thread.
        var groups = await Task.Run(() => places.GetPlacesAsync(CancellationToken.None).AsTask())
                               .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Groups.Clear();
            foreach (var group in groups)
                Groups.Add(new PlaceGroupViewModel(group, this));

                // **The bin drew the same glyph full or empty.** Asked once per
            // rebuild rather than per row, and asked with HasAny rather than
            // List — the answer is one directory entry instead of a walk of
            // every volume's bin with a sidecar read per item.
            RefreshBinState();

        // This PC is drawn directly under the Home row, so that row has to
            // know it is the one. Set here rather than computed on the item: a
            // place has no idea what it sits among, and "the first row in the
            // sidebar" is a fact about the sidebar rather than about the place.
            var first = true;

            foreach (var group in Groups)
                foreach (var place in group.Places)
                {
                    place.LeadsTheSidebar = first;
                    first = false;
                }

            // The rows are new objects, so the current-location mark has to be
            // re-applied — a refresh would otherwise silently clear the
            // highlight and leave the sidebar looking like nothing is open.
            SetCurrentPath(CurrentPath);

            // And the busy mark, for exactly the same reason: an eject spans
            // several rebuilds, and a row that came back idle would invite a
            // second click on a drive already being torn down.
            MarkEjecting();
        });
    }

    /// <summary>
    /// Which drives are being ejected right now, by place id.
    ///
    /// **On the sidebar, not on the rows**, because the rows do not survive a
    /// reload and an eject outlives several. Re-applied after every rebuild by
    /// <see cref="MarkEjecting"/>, on the same principle as the current-path
    /// mark beside it.
    /// </summary>
    private readonly HashSet<string> _ejecting = new(StringComparer.Ordinal);

    private void MarkEjecting()
    {
        foreach (var group in Groups)
            foreach (var row in group.Places)
                row.IsEjecting = _ejecting.Contains(row.Id);
    }

    /// <summary>
    /// Ejects a drive, keeping the row visibly busy across the reloads that
    /// happen while it runs.
    /// </summary>
    public async Task<Vaktari.Core.Places.EjectResult> EjectAsync(string id)
    {
        if (_places is not { } places)
            return Vaktari.Core.Places.EjectResult.Failed("no drives are available");

        _ejecting.Add(id);
        MarkEjecting();

        try
        {
            return await Task.Run(() => places.EjectAsync(id, CancellationToken.None).AsTask())
                             .ConfigureAwait(true);
        }
        finally
        {
            _ejecting.Remove(id);
            MarkEjecting();

            await ReloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Mounts a volume that is listed but not mounted, and works out what
    /// happened by looking at the sidebar afterwards.
    ///
    /// **Both providers implemented MountAsync and nothing ever called it.**
    /// Linux lists every filesystem it can see whether or not it is mounted,
    /// and gives an unmounted one an empty Path on purpose; the row's command
    /// navigated to that Path, so the click stopped at the empty-path guard and
    /// the volume could only be reached by mounting it in another application.
    ///
    /// The rebuild is what turns the mount into an answer. MountAsync returns
    /// nothing and the Linux provider swallows what udisksctl said — on
    /// purpose, a desktop without udisks2 is a real configuration — so the only
    /// honest signals are the two the rebuilt list carries: the row that
    /// offered to be mounted is gone, and a mount point that was not there
    /// before is.
    /// </summary>
    public async Task<MountOutcome> MountAsync(string id)
    {
        if (_places is not { } places) return default;

        // Taken before the mount, on the UI thread, where the rows live.
        var before = Groups
            .SelectMany(g => g.Places)
            .Select(p => p.Path)
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // Off the UI thread, like the eject and the import above it: the
        // prologue of a mount is the fork and exec of a mount helper, which
        // runs on the calling thread, and the interface promises nothing about
        // how long any of the rest of it takes. (The pin, unpin, rename and
        // reorder calls in this file do NOT do this — they write a small JSON
        // file and return.)
        try
        {
            await Task.Run(() => places.MountAsync(id, CancellationToken.None).AsTask())
                      .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The rebuild below decides whether it worked, so a provider that
            // threw is just a provider that mounted nothing.
            Quiet.Swallowed("mount", ex);
        }

        // Explicitly, rather than waiting on PlacesChanged. The Linux provider
        // does raise it and the Windows one has nothing to raise it for, and
        // either way the caller has to be able to say what happened the moment
        // this returns. The provider's own event posts a reload of its own,
        // which the gate above folds into this one when it lands inside it and
        // which is a harmless second rebuild when it lands after.
        await ReloadAsync().ConfigureAwait(true);

        var rows = Groups.SelectMany(g => g.Places).ToList();

        var arrived = rows
            .Where(p => p.IsAvailable && p.Path.Length > 0 && !before.Contains(p.Path))
            .Select(p => p.Path)
            .ToList();

        return new MountOutcome(
            Mounted: !rows.Any(p => p.Id == id),

            // Exactly one, or none. A card reader with two slots arrives as
            // several rows at once, and opening whichever of them sorted first
            // would be a guess dressed up as an answer.
            OpenedAt: arrived.Count == 1 ? arrived[0] : null);
    }

    public Task PinAsync(string path)
        => _places?.PinAsync(path, null, CancellationToken.None).AsTask() ?? Task.CompletedTask;

    /// <summary>
    /// Takes a place back off the list, and rebuilds it so the row goes.
    ///
    /// Reloaded explicitly rather than waiting on PlacesChanged: the providers
    /// raise that event on import, not on every write, and a row that stays put
    /// after being removed reads as the command having failed.
    /// </summary>
    public async Task UnpinAsync(string id)
    {
        if (_places is not { } places) return;

        await places.UnpinAsync(id, CancellationToken.None).ConfigureAwait(false);
        await ReloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a pinned place a name of its own. Reloaded explicitly for the same
    /// reason unpinning is: a row that keeps its old caption reads as the
    /// command having failed.
    /// </summary>
    public async Task RenameAsync(string id, string label)
    {
        if (_places is not { } places) return;

        var tidy = Core.Places.PlaceNames.Clean(label);
        if (tidy.Length == 0) return;

        await places.RenameAsync(id, tidy, CancellationToken.None).ConfigureAwait(false);
        await ReloadAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// What a mount attempt came to.
///
/// Two answers rather than one, because "it did not mount" and "it mounted and
/// the sidebar cannot say where it landed" are different things to tell
/// somebody — and a single nullable path would have to report the second one as
/// a failure, which is the kind of message this whole finding was about.
/// </summary>
/// <param name="Mounted">True when the volume is no longer listed as one
/// waiting to be mounted.</param>
/// <param name="OpenedAt">Where it landed, when exactly one new place appeared
/// while it was being mounted. Null when none did, and null when several
/// did.</param>
public readonly record struct MountOutcome(bool Mounted, string? OpenedAt);

public sealed partial class PlaceGroupViewModel(PlaceGroup group, SidebarViewModel? sidebar = null)
    : ObservableObject
{
    public string Label { get; } = group.Label;

    /// <summary>
    /// Whether this group is folded away.
    ///
    /// Read from the sidebar when the group is built and written back to it
    /// when it changes, because the group itself does not live long enough to
    /// remember anything: plugging in a stick rebuilds every one of them from
    /// the desktop's list. The sidebar is optional so the five test fakes and
    /// the two other construction sites need no argument — a group with no
    /// sidebar behind it simply never folds.
    /// </summary>
    private bool _isCollapsed = sidebar?.IsCollapsed(group.Label) ?? false;

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (!SetProperty(ref _isCollapsed, value)) return;

            sidebar?.SetCollapsed(Label, value);
        }
    }

    /// <summary>
    /// Observable so a reorder can move a row under the pointer. A rebuilt list
    /// would work for the commit and not for the drag: the row has to travel
    /// with the finger, not appear somewhere else once it is let go.
    /// </summary>
    public ObservableCollection<PlaceItemViewModel> Places { get; } =
        new(group.Places.Select(p => new PlaceItemViewModel(p)));

    /// <summary>
    /// The rows that may be dragged, by their position in this group.
    ///
    /// **Only the ones the person pinned.** Home, Documents, the drives, the
    /// shares and the bin are the desktop's, assembled fresh from it on every
    /// rebuild — an order imposed on them would not survive one, and the
    /// provider's reorder reads none of them. The pins are a contiguous run in
    /// both providers, so moving one onto another's row cannot displace
    /// anything that is not a pin.
    /// </summary>
    public List<int> PinnedRows()
    {
        var rows = new List<int>();

        for (var i = 0; i < Places.Count; i++)
            if (Places[i].IsUserPinned) rows.Add(i);

        return rows;
    }

    /// <summary>
    /// Puts <paramref name="pin"/> in the given SLOT of the pinned run — not at
    /// a position in the group, which would let it land on Home.
    /// </summary>
    public void MovePin(PlaceItemViewModel pin, int slot)
    {
        var rows = PinnedRows();
        var at = Places.IndexOf(pin);

        var from = rows.IndexOf(at);
        if (from < 0) return;

        slot = Math.Clamp(slot, 0, rows.Count - 1);
        if (slot == from) return;

        Places.Move(at, rows[slot]);
    }
}

public sealed partial class PlaceItemViewModel(Place place) : ObservableObject
{
    /// <summary>
    /// True when this place is what the active pane is showing, which draws the
    /// accent bar. Observable rather than computed once, because navigation
    /// changes it long after the list was built.
    /// </summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>
    /// True while a drag is over this row and would land here.
    ///
    /// **A place gave no sign at all.** Dragging onto "Documents" in the
    /// sidebar looked exactly like dragging past it, so the only way to find
    /// out where the files had gone was to release and look — and the sidebar
    /// rows are small and close together, which is precisely where a target
    /// needs to say which one it is.
    /// </summary>
    [ObservableProperty] private bool _isDropTarget;

    public string Id { get; } = place.Id;
    public string Label { get; } = place.Label;
    public string Path { get; } = place.Path;
    public string Icon { get; } = place.Icon;

    /// <summary>
    /// Whether the bin is holding anything. Meaningless on every other row.
    /// </summary>
    [ObservableProperty] private bool _binHasItems;

    /// <summary>
    /// The glyph this row draws.
    ///
    /// **The bin drew the same one whether it held a thousand items or
    /// nothing**, so the one question you ask a bin was the one thing it would
    /// not answer. Every other row is its provider's token unchanged.
    ///
    /// Computed here rather than by swapping Place.Icon, because a Place comes
    /// from the provider and is rebuilt on every reload — the fill state is a
    /// property of the moment, not of the place.
    /// </summary>
    public string IconToken => IsBin && BinHasItems ? "trash-full" : Icon;

    partial void OnBinHasItemsChanged(bool value) => OnPropertyChanged(nameof(IconToken));
    public bool IsAvailable { get; } = place.IsAvailable;

    /// <summary>
    /// Whether this row IS the bin.
    ///
    /// **Emptying it was reachable only from inside it.** EmptyTrashCommand was
    /// bound in exactly one place in the whole application — the button band
    /// that appears once you have already navigated into the bin — so the
    /// gesture everybody else offers on the icon itself required going there
    /// first. Both references empty it from the row.
    ///
    /// PathRules.Same rather than ==, which is the comparison the drop walker
    /// already uses for the same question.
    /// </summary>
    public bool IsBin { get; } = PathRules.Same(place.Path, VirtualPaths.Trash);

    /// <summary>
    /// Whether the user put this here, and so whether they can take it away.
    ///
    /// **Nothing in the interface could remove a place.** Both platform
    /// providers implement UnpinAsync and nobody called it: Ctrl+D and the menu
    /// added one, and the only way back out was to edit places.json by hand.
    /// Home, the drives and the network shares are not the user's to remove and
    /// the provider would ignore the attempt, so the entry appears only on the
    /// rows where it means something.
    /// </summary>
    public bool IsUserPinned { get; } = place.IsUserPinned;

    /// <summary>
    /// Whether this is a drive that can be safely removed.
    ///
    /// **Both providers have set this since they were written, and nothing has
    /// ever read it.** Every binding on it was dead until the eject entries
    /// landed — and a binding to a property that does not exist is a debug-log
    /// line in Avalonia, not an error, so the row would simply have shown
    /// nothing and said nothing.
    /// </summary>
    public bool CanEject { get; } = place.CanEject;

    /// <summary>
    /// Whether clicking this row should mount the volume rather than open it.
    ///
    /// Carried on the row rather than reached through to the Place, because
    /// compiled bindings are on: a binding to a property this type does not
    /// have is a build error rather than the silent nothing an interpreted one
    /// would be. The same reason CanDisconnect is here.
    ///
    /// Not to be confused with PaneViewModel.CanMountSelection, which is about
    /// a disk image file in a listing. This one is about a partition.
    /// </summary>
    public bool CanMount { get; } = place.CanMount;

    /// <summary>
    /// Whether this row is a mapped network drive, which can be given back.
    ///
    /// Compiled bindings are on, so the menu row cannot reach through to the
    /// Place: a binding to a property this type does not have is a build error
    /// rather than the silent nothing an interpreted one would be.
    /// </summary>
    public bool CanDisconnect { get; } = place.CanDisconnect;

    /// <summary>Names the drive, because "disconnect" on its own does not say
    /// which — and this is what a screen reader reads.</summary>
    public string DisconnectHint => $"disconnect {Label}";

    /// <summary>
    /// Whether this is the very first row in the sidebar — Home. This PC is
    /// drawn immediately under it, and a DataTemplate cannot ask where it sits.
    /// </summary>
    public bool LeadsTheSidebar { get; set; }

    /// <summary>
    /// Whether this row names a real folder. The bin and the two recent
    /// listings carry an internal scheme instead, so copying "their path" or
    /// asking the desktop for their properties would be meaningless.
    /// </summary>
    public bool HasRealPath =>
        Path.Length > 0 && !Vaktari.Ui.VirtualPaths.IsVirtual(Path);

    /// <summary>
    /// True while this drive is being ejected, which disables the row.
    ///
    /// Set from the sidebar rather than owned here: a reload throws every row
    /// object away and builds new ones, and once devices are watched a reload
    /// landing mid-eject is guaranteed rather than hypothetical — the dismount
    /// itself changes the mount table. A flag living on the row would vanish
    /// with it and the drive would come back looking idle and clickable while
    /// the eject was still running.
    /// </summary>
    [ObservableProperty] private bool _isEjecting;

    partial void OnIsEjectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClickEject));
        OnPropertyChanged(nameof(ShowCapacity));
    }

    public bool CanClickEject => CanEject && !IsEjecting;

    /// <summary>Names the drive, because with two sticks plugged in "eject" on
    /// its own does not say which — and this is what a screen reader reads.</summary>
    public string EjectHint => $"eject {Label}";

    /// <summary>Unreachable entries render dimmed and in place — never hidden,
    /// never silently dropped.</summary>
    public double Opacity => IsAvailable ? 1.0 : 0.4;

    public bool HasCapacity => place.CapacityBytes is > 0;

    public double UsedFraction => place.CapacityBytes is > 0 && place.FreeBytes is { } free
        ? 1.0 - (double)free / place.CapacityBytes.Value
        : 0;

    public string CapacityText => place.CapacityBytes is > 0 && place.FreeBytes is { } free
        ? $"{ByteSize.Format(free)} free"
        : "";

    /// <summary>
    /// Free space without the trailing "free". The drive row is one line now and
    /// the label beside it already says which drive this is, so that word was
    /// carrying no information at exactly the width where it cost the most.
    /// <see cref="CapacityText"/> stays, as the row's tooltip.
    /// </summary>
    public string CapacityShort => place.CapacityBytes is > 0 && place.FreeBytes is { } free
        ? ByteSize.Format(free)
        : "";

    /// <summary>
    /// The status bar used to print free space and the drive row printed it too.
    /// It is one number about one drive, so it belongs on the drive — and the
    /// setting that used to hide it in the status bar now hides it here.
    ///
    /// Read from AppSettings rather than passed in, matching the static-provider
    /// convention IconLoader and RowMetadata already use. That makes it impure:
    /// a settings save has to re-raise it, which is what
    /// <see cref="RaiseCapacityVisibilityChanged"/> exists for.
    /// </summary>
    public bool ShowCapacity =>
        HasCapacity && !IsEjecting && Settings.AppSettings.Current.General.ShowFreeSpace;

    /// <summary>
    /// The rows are separate objects from the shell that owns the setting, so
    /// raising the change there does not reach them.
    /// </summary>
    public void RaiseCapacityVisibilityChanged() => OnPropertyChanged(nameof(ShowCapacity));
}
