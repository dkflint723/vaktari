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

    public SidebarViewModel(
        IPlacesProvider? places,
        ISearchProvider? search = null,
        Func<string?>? currentPath = null)
    {
        _places = places;
        Search = new SearchViewModel(search, currentPath ?? (() => null));

        if (places is not null)
            places.PlacesChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
    }

    public ObservableCollection<PlaceGroupViewModel> Groups { get; } = new();

    public SearchViewModel Search { get; }


    [ObservableProperty] private RailState _rail = RailState.Full;
    [ObservableProperty] private double _width = 210;

    // One sidebar, all sections visible at once — the point of the workspace
    // layout is that the things you organise by are never behind a toggle.
    //
    // An `ActivePanel` used to sit beside this, persisted in the session and
    // restored, with `ShowPanel` as its only mutator — and nothing ever called
    // that. Removed 30 July 2026: state that cannot be changed is not state.
    public bool IsPanelVisible => Rail != RailState.Hidden;

    /// <summary>The folder tree is the one section worth collapsing: it is tall,
    /// and it is the least used of the four.</summary>



    [ObservableProperty] private bool _isSearching;

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
    }

    private static string Normalise(string? path)
        => PathRules.Normalise(path);

    /// <summary>The active path, for the fixed entries that are not in Groups.</summary>
    public string CurrentPath { get; private set; } = "";

    public bool IsRecentFilesCurrent => CurrentPath == VirtualPaths.Files;
    public bool IsRecentLocationsCurrent => CurrentPath == VirtualPaths.Locations;

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
        Thumbnails.ThumbnailLoader.RemoteRoots = Remotes.Select(m => m.Path).ToList();

        OnPropertyChanged(nameof(HasRemotes));
    }


    /// <summary>
    /// Ctrl+F puts the caret in the toolbar's search box.
    ///
    /// The field lives in the path bar now, not behind the sidebar, so revealing
    /// the rail is no longer what this gesture is for — but it is kept, because
    /// the rail is also where a search result's place context is read from.
    /// </summary>
    [RelayCommand]
    private void FocusSearch()
    {
        Rail = RailState.Full;
        IsSearchOpen = true;

        // Re-raised rather than set once. The flag was already true after the
        // first Ctrl+F, so a second one changed nothing and the caret stayed
        // where it was. Same pattern as PaneViewModel.RefreshScale().
        IsSearching = false;
        IsSearching = true;
    }

    /// <summary>
    /// Whether the toolbar shows the search FIELD or just its icon.
    ///
    /// **Separate from <see cref="IsSearching"/>, which is not a state.** That
    /// flag is a one-shot trigger for the focus behaviour — set false then true
    /// to re-fire it — so it is true forever after the first Ctrl+F and cannot
    /// answer "is the field open".
    ///
    /// The field used to be a fixed 230px that never yielded, and on the active
    /// side of a split it plus the filter button consumed the entire path bar:
    /// measured, the crumbs were left showing "C:" and nothing else, so the one
    /// thing a path bar exists to say was the thing there was no room for.
    /// </summary>
    [ObservableProperty] private bool _isSearchOpen;

    /// <summary>
    /// Collapses the field back to its icon when you leave it, but only when it
    /// is empty.
    ///
    /// **A query holds it open**, because the results popup is anchored to the
    /// field: collapsing with a query live would take away both the results and
    /// the text that produced them, and clicking a result means moving focus
    /// out of the box to reach it.
    /// </summary>
    [RelayCommand]
    private void CloseSearchIfEmpty()
    {
        if (!Search.HasQuery) IsSearchOpen = false;
    }

    /// <summary>Escape: abandon the search outright, whatever is in it.</summary>
    [RelayCommand]
    private void DismissSearch()
    {
        Search.Query = "";
        IsSearching = false;
        IsSearchOpen = false;
    }


    partial void OnRailChanged(RailState value) => NotifyVisibility();

    private void NotifyVisibility() => OnPropertyChanged(nameof(IsPanelVisible));

    /// <summary>Two states now, not three: with no icon rail there is nothing
    /// meaningful between "shown" and "hidden".</summary>
    [RelayCommand]
    public void CycleRail() => Rail = Rail == RailState.Hidden ? RailState.Full : RailState.Hidden;

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

    private bool _reloading;
    private bool _reloadDirty;

    public async Task ReloadAsync()
    {
        if (_places is not { } places) return;

        // **Coalesced, and this is required rather than defensive.** Until
        // devices were watched, this ran only at startup and after a pin, so
        // two could never overlap. Driven by arrivals and removals they will:
        // one stick with four partitions, or an eject, which changes the mount
        // table while the rebuild it triggered is still running. Each overlapping
        // rebuild parks a thread-pool thread for the whole SMB timeout on a
        // machine with a dead mapped drive, so they must not stack — and the
        // last one still has to happen, or the sidebar settles on stale rows.
        if (_reloading)
        {
            _reloadDirty = true;
            return;
        }

        _reloading = true;

        try
        {
            do
            {
                _reloadDirty = false;
                await ReloadOnceAsync(places).ConfigureAwait(false);
            }
            while (_reloadDirty);
        }
        finally
        {
            _reloading = false;
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
                Groups.Add(new PlaceGroupViewModel(group));

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
}

public sealed class PlaceGroupViewModel(PlaceGroup group)
{
    public string Label { get; } = group.Label;
    public IReadOnlyList<PlaceItemViewModel> Places { get; } =
        group.Places.Select(p => new PlaceItemViewModel(p)).ToList();
}

public sealed partial class PlaceItemViewModel(Place place) : ObservableObject
{
    /// <summary>
    /// True when this place is what the active pane is showing, which draws the
    /// accent bar. Observable rather than computed once, because navigation
    /// changes it long after the list was built.
    /// </summary>
    [ObservableProperty] private bool _isCurrent;

    public string Id { get; } = place.Id;
    public string Label { get; } = place.Label;
    public string Path { get; } = place.Path;
    public string Icon { get; } = place.Icon;
    public bool IsAvailable { get; } = place.IsAvailable;

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
