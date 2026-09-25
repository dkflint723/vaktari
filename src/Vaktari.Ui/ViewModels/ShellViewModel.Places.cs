using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The sidebar's rows: going to one, pinning one, and everything its menu
/// offers.
///
/// **A place is not always a folder**, which is what most of the care here is
/// about. It can be a drive that is present but not mounted, a share whose
/// server has gone, a bin, or a saved search — so going to one may mean
/// mounting it first, ejecting one may mean finding out what is still using
/// it, and neither may assume a path that opens.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- places --------------------------------------------------------

    [RelayCommand]
    private void GoToPlace(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _ = ActiveTab?.NavigateAsync(path);
    }

    /// <summary>
    /// What clicking a place does, which is not always to go there.
    ///
    /// **A volume that was present but not mounted could not be opened at
    /// all.** The Linux provider lists every filesystem it can see, mounted or
    /// not, and gives an unmounted one an empty Path on purpose — there is no
    /// folder to open until somebody mounts it. The row's command was
    /// GoToPlace with that Path as its parameter, so the click hit
    /// `if (!string.IsNullOrEmpty(path))` and stopped, while MountAsync sat
    /// implemented on both providers, covered by tests, and called from nowhere
    /// in this application.
    ///
    /// Concurrent executions are allowed deliberately. Every row in the sidebar
    /// binds to this one command object, and an async RelayCommand refuses a
    /// second execution while the first is running — so a mount that takes a
    /// second or two would grey out every other place on the way past. That
    /// trap is already written up on PropertiesViewModel.MeasureAsync, where it
    /// disabled the button that stops the measurement.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenPlaceAsync(PlaceItemViewModel? place)
    {
        if (place is null) return;

        // Captured before the first await, the way the eject beside this does:
        // a mount takes seconds, a tab can be switched inside them, and the
        // answer must land where it was asked for.
        if (ActiveTab is not { } pane) return;

        // The ordinary row, unchanged in effect: NavigateAsync makes the
        // empty-path check itself, which is the only thing GoToPlace's guard
        // was doing here.
        if (!place.CanMount)
        {
            await pane.NavigateAsync(place.Path).ConfigureAwait(true);
            return;
        }

        // One mount at a time per volume. The row is a Button, so a
        // double-click sends this command twice, and the second attempt would
        // report its own refusal over the first one's success.
        if (!_mounting.Add(place.Id)) return;

        pane.Status = $"mounting {place.Label}…";

        try
        {
            var outcome = await Sidebar.MountAsync(place.Id).ConfigureAwait(true);

            // Where it landed is asked first, because the two answers can
            // disagree: a volume can be mounted and listed at its new path
            // while the row that offered to mount it is still on screen.
            if (outcome.OpenedAt is { } path)
            {
                await pane.NavigateAsync(path).ConfigureAwait(true);
                return;
            }

            // It mounted and the sidebar cannot say where, or it did not mount
            // at all. Either way the click says something — which is the whole
            // of what it did not do before.
            pane.Status = outcome.Mounted
                ? $"mounted {place.Label}"
                : $"could not mount {place.Label}";
        }
        catch (Exception ex)
        {
            pane.Status = Core.FileSystem.Failures.Describe(ex, $"mount {place.Label}");
        }
        finally
        {
            _mounting.Remove(place.Id);
        }
    }

    /// <summary>
    /// The volumes with a mount in flight, by place id.
    ///
    /// Ids rather than rows, and on the shell rather than on the row: a rebuild
    /// throws every row object away and builds new ones, and a mount spans at
    /// least one of them — the mount is what changes the mount table the
    /// device watcher is polling.
    /// </summary>
    private readonly HashSet<string> _mounting = new(StringComparer.Ordinal);

    /// <summary>
    /// **Ctrl+D in the bin pinned "vaktari:trash".** The gesture asks for the
    /// folder you are in, and in a listing that is a view rather than a folder
    /// there is none — so the place that landed in the sidebar was the internal
    /// scheme, a row that could never be opened and had to be removed by hand.
    /// The menu row hides for the same reason.
    ///
    /// **The gesture said nothing, whether it worked or not**, and Ctrl+D is
    /// what Explorer deletes with. Somebody arriving from there presses it over
    /// a selected file, and every visible thing stays exactly as it was: the
    /// folder is written to places.json, the panel rebuilds from the provider's
    /// own PlacesChanged a moment later, and the only evidence is one new row
    /// in a panel that may be collapsed. Nothing told them a place had been
    /// added rather than a file removed. So it reports, in the same line the
    /// drop onto the panel already writes — and the F1 sheet's Ctrl+D row names
    /// the key that does move things to the bin.
    ///
    /// Async now, because the report is made AFTER the write returns rather
    /// than in front of it, which is the order <see cref="PinDroppedAsync"/>
    /// already reports in.
    /// </summary>
    [RelayCommand]
    private async Task PinCurrentAsync()
    {
        // Null, not an early return: the refusal is a line the helper writes,
        // and a listing with no folder in it is one of the two things this can
        // have to say.
        //
        // **A search is a place too.** It was refused with the bin and This
        // PC as "a view rather than a folder" — and it is a view, but one the
        // panes can open again from its path alone, which is exactly what a
        // place is for. The right-click history keeps twelve and forgets the
        // rest; this is how a question worth keeping is kept.
        var here = ActiveTab is { CurrentPath: { Length: > 0 } path } pane
                   && (pane.IsRealFolder || pane.IsSearchListing)
            ? path
            : null;

        await PinOneAsync(here).ConfigureAwait(true);
    }

    /// <summary>
    /// One folder onto the panel, and a line saying which of the three things
    /// happened — pinned, already there, or nothing that could be a place.
    ///
    /// **"Already there" is asked of the RENDERED ROWS, not of the pins file**,
    /// and that is measured rather than tidy: the provider drops a pin whose
    /// path is already a built-in place while BUILDING the list it renders, so
    /// pinning Downloads wrote an entry to places.json that was never drawn and
    /// that no "Remove from places" could reach, while pinning a drive drew it
    /// twice — once as its device row and once as a bookmark beside it. The
    /// pins file answers neither question: MEASURED on a real provider, it
    /// refuses a path its list already holds and never re-reads the file, so
    /// the entry behind Downloads is written once and every later press is a
    /// no-op that <c>PinAsync</c> has no way to report. The same reasoning, and
    /// the same measured fault, as <see cref="PinDroppedAsync"/>; the rows are
    /// read live here because there is one path rather than a loop over
    /// several.
    ///
    /// On the active tab because that is where this window says things: the
    /// sidebar has no line of its own.
    /// </summary>
    private async Task PinOneAsync(string? path)
    {
        if (ActiveTab is not { } pane) return;

        // VirtualPaths as well as the length, because the menu's selection row
        // falls back to the current listing — when nothing is selected, and in
        // the bin whatever is — and in the bin that fallback is the scheme
        // itself. The length alone answers Ctrl+D, which hands over a null.
        // A search is the one view that IS a place: see PinCurrentAsync.
        var search = path is { Length: > 0 } && VirtualPaths.IsSearch(path);

        if (path is not { Length: > 0 } || (VirtualPaths.IsVirtual(path) && !search))
        {
            pane.Status = Input.PinPlan.OnlyFolders;
            return;
        }

        // The question and where it was asked, for a search — the tail of its
        // path is the scheme's own punctuation, not a name.
        var name = search ? PaneViewModel.SearchStepName(path) : PathRules.LeafName(path);

        if (Sidebar.Groups.SelectMany(g => g.Places).Any(p => VirtualPaths.SamePin(p.Path, path)))
        {
            pane.Status = $"{name} is already in places";
            return;
        }

        await Sidebar.PinAsync(path, search ? name : null).ConfigureAwait(true);

        pane.Status = search ? $"saved the search {name} to places" : $"pinned {name} to places";
    }

    /// <summary>
    /// Pins what a drop onto the sidebar's own ground was carrying.
    ///
    /// **The gesture had no path into the application at all**: the only way to
    /// add a place was Ctrl+D or the menu, both of which pin the folder you are
    /// already in, so a folder you can see in the listing had to be opened
    /// before it could be pinned. Dropping it on the panel is what both
    /// references offer instead, and the sidebar refused every drop that was
    /// not on a row.
    ///
    /// The same <c>Sidebar.PinAsync</c> the Ctrl+D command calls, once per
    /// folder that is not already on the panel.
    ///
    /// **The panel's own rows are what "already" is asked of, not the pins
    /// file**, and that is measured rather than tidy. The provider dedupes a
    /// pin against the built-in places while BUILDING the list — Windows drops
    /// one whose path is already Home, Desktop, Downloads, Documents, Pictures,
    /// Music or Videos — and drives are added afterwards without being deduped
    /// at all. So a dropped Downloads was written to places.json and never
    /// drawn, and a dropped <c>D:\</c> appeared a second time as a bookmark
    /// labelled <c>D:\</c> beside its own device row. Asking PinAsync would
    /// have answered "not pinned yet" to both. The rendered rows are the only
    /// thing that knows what is on screen, so they are what is asked.
    ///
    /// Awaited rather than fired and forgotten, so a caller can say when it is
    /// done; the drop handler does not wait, because a drag must not hold the
    /// UI thread while a file is written.
    /// </summary>
    public async Task PinDroppedAsync(IReadOnlyList<string> dropped)
    {
        var plan = Input.PinnableDrop.For(dropped);

        // Snapshotted before the first pin: pinning reloads the panel, and a
        // list read again halfway through would start counting this drop's own
        // new rows as ones that were "already there".
        var shown = Sidebar.Groups.SelectMany(g => g.Places).Select(p => p.Path).ToList();

        var already = 0;

        foreach (var folder in plan.Folders)
        {
            if (shown.Any(path => Core.FileSystem.PathRules.Same(path, folder)))
            {
                already++;
                continue;
            }

            await Sidebar.PinAsync(folder).ConfigureAwait(true);
        }

        // On the active tab because that is where this window says things. The
        // sidebar has no line of its own, and a pin that reported nothing would
        // be as silent as the refusal this replaced.
        if (ActiveTab is { } pane) pane.Status = plan.Report(already);
    }

    /// <summary>
    /// The way back out, which did not exist: places could be added from two
    /// places and removed from none.
    ///
    /// No confirmation. This removes a shortcut and never a folder — the same
    /// bargain as Recent's "Forget (keeps the file)", which also asks nothing —
    /// and putting it back is the same Ctrl+D that created it.
    /// </summary>
    [RelayCommand]
    private void RemovePlace(PlaceItemViewModel? place)
    {
        if (place is { IsUserPinned: true }) _ = Sidebar.UnpinAsync(place.Id);
    }

    /// <summary>Raised so the window can put a name in front of somebody. A
    /// view model has no business owning a text prompt — the same shape the
    /// properties and connect requests already use.</summary>
    public event EventHandler<PlaceItemViewModel>? RenamePlaceRequested;

    /// <summary>
    /// Gives a pinned place a caption of its own.
    ///
    /// **Both providers have stored a per-pin label since they were written and
    /// nothing could change it.** Two folders both called "src" pinned as two
    /// rows called "src", and the only way to tell them apart was editing
    /// places.json by hand.
    ///
    /// Only the rows the user made. Home, the drives and the shares are named
    /// by the system, and renaming one would be a caption that vanished at the
    /// next reload.
    /// </summary>
    [RelayCommand]
    private void RenamePlace(PlaceItemViewModel? place)
    {
        if (place is { IsUserPinned: true }) RenamePlaceRequested?.Invoke(this, place);
    }

    /// <summary>The gate, apart from the prompt, so it can be read without
    /// driving a text box.</summary>
    public async Task RenamePlaceAsync(PlaceItemViewModel? place, string label)
    {
        if (place is not { IsUserPinned: true }) return;

        await Sidebar.RenameAsync(place.Id, label).ConfigureAwait(false);
    }

    // ---- what a sidebar row's menu offers everybody ------------------------
    //
    // **Right-clicking Home, Documents, a drive, a mapped drive or the bin
    // opened nothing at all.** The menu held two entries, both of which apply
    // to almost no rows — Remove to the ones the user pinned, Eject to the ones
    // that can be ejected — and the Opening handler cancelled the popup for
    // everything else rather than show a sliver of empty menu. Correct for a
    // menu with nothing in it, and the wrong fix: both references put Open in
    // new tab and Properties on every node of the navigation pane.

    [RelayCommand]
    private void OpenPlaceInNewTab(PlaceItemViewModel? place)
    {
        if (place is { Path.Length: > 0 }) OpenInNewTab(place.Path);
    }

    [RelayCommand]
    private void CopyPlacePath(PlaceItemViewModel? place)
    {
        if (place is { HasRealPath: true }) CopyTextRequested?.Invoke(this, place.Path);
    }

    /// <summary>The desktop's own properties dialog, the same one the listing
    /// menu opens for a row.</summary>
    [RelayCommand]
    private void ShowPlaceProperties(PlaceItemViewModel? place)
    {
        if (place is { HasRealPath: true }) ShowPropertiesRequested?.Invoke(this, place.Path);
    }

    /// <summary>Raised so the window can put up the platform properties dialog;
    /// a view model has no business owning one.</summary>
    public event EventHandler<string>? ShowPropertiesRequested;

    /// <summary>
    /// Whether one of our own transfers is still working on the drive, and says
    /// so on the status line when it is.
    ///
    /// **A veto has to name itself.** A menu row that quietly does nothing
    /// reads as a broken menu row, and the person cannot act on a reason they
    /// were never given — the transfer bar is showing a percentage, not the
    /// drive it is filling.
    /// </summary>
    private bool SomethingIsStillUsing(PlaceItemViewModel place, PaneViewModel? pane)
    {
        // **Every window, not this one.** A per-window answer would let this
        // window "safely remove" a stick another window was still filling,
        // which is the one failure in this area that costs files rather than a
        // confusing sentence. A shell on its own — every view-model test — has
        // no family and answers with its own list.
        var running = AllRunning?.Invoke() ?? _running;

        if (Core.FileSystem.InFlight.On(running, place.Path) is not { Count: > 0 }) return false;

        if (pane is not null)
            pane.Status =
                $"a transfer is still using {place.Label} — wait for it to finish, "
                + "or cancel it";

        return true;
    }

    /// <summary>
    /// Safely removes a drive, after getting out of its way.
    ///
    /// **Step one is the difference between this working and never working.**
    /// Vaktari holds the volume open itself: a pane showing a folder on the
    /// drive keeps a live directory watch on it, which is an outstanding handle
    /// like any other. Ejecting the drive somebody is looking at — overwhelmingly
    /// the common case, since looking at it is why they want it back — would
    /// fail every single time, and the veto would blame a program the user
    /// cannot find, because the program is us.
    ///
    /// **Step zero is refusing outright while a transfer is still using it.**
    /// Moving the panes off the drive is the right answer to a watch, which
    /// holds no data; it is the wrong answer to a transfer, which does. This
    /// used to run the whole sequence regardless: the tabs were sent home and
    /// the ejector was called with a copy still in flight — and what that costs
    /// is platform-shaped. On Windows Quiesce dismounts the volume even when
    /// the lock never came; on Linux udisksctl answers "busy", so the eject
    /// fails and blames a program the person cannot find, which is us again.
    /// </summary>
    [RelayCommand]
    private async Task EjectPlaceAsync(PlaceItemViewModel? place)
    {
        if (place is not { CanEject: true, IsEjecting: false }) return;

        // Captured before the first await: an eject takes seconds, a tab can be
        // switched inside them, and the answer must land where it was asked for
        // — the same reason DisconnectRemoteAsync captures it.
        var pane = ActiveTab;

        // **Before the panes are moved, not after.** A refusal that had already
        // sent every tab on the drive back to the home folder would cost
        // somebody their place for an eject that never happened — and the
        // transfer it refused on behalf of is still running, so they would
        // navigate straight back and try again.
        if (SomethingIsStillUsing(place, pane)) return;

        // Every tab in both panes, not just the active one: a background tab
        // holds its directory watch open exactly like a visible one does, and
        // an unseen tab vetoing the eject is the least explicable failure of
        // the lot. Right is null when the window is not split.
        foreach (var group in new[] { Left, Right }.OfType<PaneGroupViewModel>())
        {
            foreach (var tab in group.Tabs.ToList())
            {
                if (!IsUnder(tab.CurrentPath, place.Path)) continue;

                await tab.NavigateAsync(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    .ConfigureAwait(true);
            }
        }

        // After the navigation, never before: a finished listing clears the
        // status line, so a message set first would be erased by the very
        // navigation this just triggered.
        if (pane is not null) pane.Status = $"ejecting {place.Label}…";

        try
        {
            var result = await Sidebar.EjectAsync(place.Id).ConfigureAwait(true);

            if (pane is not null) pane.Status = result.Message;
        }
        catch (Exception ex)
        {
            if (pane is not null)
                pane.Status = Core.FileSystem.Failures.Describe(ex, $"eject {place.Label}");
        }
    }

    /// <summary>
    /// Gives a mapped network drive back.
    ///
    /// **There was no way to.** A mapped drive's row offered Open, Open in a
    /// new tab, Pin and Properties; Eject is for media you take out, so the
    /// only way to take Z: off the sidebar was `net use /delete` in a console.
    /// Explorer has Disconnect on exactly this row.
    ///
    /// Shaped like the eject beside it, and for the same reasons: every tab in
    /// both panes is moved off the drive first, because a background tab holds
    /// its directory watch open exactly like a visible one and an unseen tab
    /// vetoing the disconnect is the least explicable failure of the lot — and
    /// the message is written after that navigation, because a finished listing
    /// clears the status line.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectPlaceAsync(PlaceItemViewModel? place)
    {
        if (place is not { CanDisconnect: true }) return;
        if (_remotes is null) return;

        var pane = ActiveTab;

        foreach (var group in new[] { Left, Right }.OfType<PaneGroupViewModel>())
        {
            foreach (var tab in group.Tabs.ToList())
            {
                if (!IsUnder(tab.CurrentPath, place.Path)) continue;

                await tab.NavigateAsync(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    .ConfigureAwait(true);
            }
        }

        if (pane is not null) pane.Status = $"disconnecting {place.Label}…";

        try
        {
            var ok = await _remotes.DisconnectAsync(place.Path, CancellationToken.None)
                                   .ConfigureAwait(true);

            // The sidebar is rebuilt because the drive has gone, and the remote
            // list with it: RemoteRoots decides which paths get the cheap icon
            // treatment, and a letter left in it after the drive has gone is a
            // path nothing will ever ask about again.
            await Sidebar.ReloadAsync().ConfigureAwait(true);
            Sidebar.RefreshRemotes();

            if (pane is not null)
            {
                pane.Status = ok
                    ? $"disconnected {place.Label}"
                    : $"could not disconnect {place.Label} — something may still be using it";
            }
        }
        catch (Exception ex)
        {
            if (pane is not null)
                pane.Status = Core.FileSystem.Failures.Describe(ex, $"disconnect {place.Label}");
        }
    }

    /// <summary>
    /// What to say when a batch finished with items left behind.
    ///
    /// **Names the file when there is one to name.** "3 items could not be
    /// copied" sends someone hunting; the first name plus a count tells them
    /// where to start. The reason comes from the same register every other
    /// failure in this window uses, so "the file is open in another program"
    /// rather than an exception type.
    /// </summary>
    internal static string DescribeProblems(IReadOnlyList<Core.FileSystem.ItemProblem> problems)
    {
        // Through the same row the details list is built from, so the sentence
        // and the list can never disagree about a name or a reason. Read one
        // row rather than the whole list: a folder that could not be created
        // reports every one of its planned descendants, and building thousands
        // of rows to print one of them is work nobody asked for.
        var first = Row(problems[0]);

        return problems.Count == 1
            ? $"{first.Name} was left behind — {first.Reason}"
            : $"{first.Name} and {problems.Count - 1} more were left behind — {first.Reason}";
    }

    /// <summary>
    /// One problem as a row: the leaf name, the whole path, and why in the same
    /// words the rest of this window uses.
    ///
    /// "copy that" completes "could not …" for the handful of errors whose own
    /// message says nothing useful. It is the verb the sentence on the bar has
    /// always used, and the two sit next to each other now, so they use one.
    /// </summary>
    private static ProblemRow Row(Core.FileSystem.ItemProblem problem)
        => new(Path.GetFileName(problem.Path.TrimEnd(Path.DirectorySeparatorChar)),
               problem.Path,
               Core.FileSystem.Failures.Describe(problem.Error, "copy that"));

    /// <summary>
    /// Everything a batch left behind, one row each — the list the "details"
    /// button on the transfer bar shows.
    ///
    /// **Only the first one ever reached the screen.** The sentence beside this
    /// button abridges by design; this is the same information without the
    /// abridgement, and it is the first route in the application to the second
    /// item and the fiftieth.
    ///
    /// **A folder drags its contents down with it**, though, and the engines
    /// record every planned descendant of a folder they could not create —
    /// "noisy, but honest", as the copy loop puts it. Raw, that is 431 rows
    /// about one unreadable folder: the same number
    /// <see cref="Core.FileSystem.RetryRoots.Outermost"/> exists to refuse for
    /// the retry button, and refused here for the same reason. Deliberately the
    /// same shape as that method, down to Same-and-Contains rather than an
    /// ordinal compare: Same is why a folder does not exclude itself, and
    /// Contains is why "/media/one" does not claim "/media/onetwo". The one
    /// difference is that this reads which rows are folders off the paths,
    /// because an ItemProblem carries no such flag — and only a folder can have
    /// another failure underneath it.
    /// </summary>
    internal static IReadOnlyList<ProblemRow> ListProblems(
        IReadOnlyList<Core.FileSystem.ItemProblem> problems)
        => [.. problems
            .Where(p => !problems.Any(other =>
                !Core.FileSystem.PathRules.Same(other.Path, p.Path)
                && Core.FileSystem.PathRules.Contains(other.Path, p.Path)))
            .Select(Row)];

    /// <summary>
    /// The folder currently selected in a pane, when one is — which is what a
    /// transfer destination must not be inside.
    /// </summary>
    private static string? SelectedFolderOf(PaneViewModel? pane)
        => pane?.SelectedEntry is { IsDirectory: true } folder ? folder.FullPath : null;

    /// <summary>Whether a pane is looking at the drive, or anywhere inside it.</summary>
    private static bool IsUnder(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (Core.FileSystem.PathRules.Same(path, root)) return true;

        var prefix = Core.FileSystem.PathRules.Normalise(root);
        var full = Core.FileSystem.PathRules.Normalise(path);

        if (!full.StartsWith(prefix, Core.FileSystem.PathRules.Comparison)) return false;

        // The prefix has to end at a separator, or "E:\" would claim "E:\..."
        // correctly but "/media/one" would also claim "/media/onetwo".
        return prefix.EndsWith(Path.DirectorySeparatorChar)
               || (full.Length > prefix.Length && full[prefix.Length] == Path.DirectorySeparatorChar);
    }
}
