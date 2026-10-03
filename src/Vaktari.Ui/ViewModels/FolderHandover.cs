using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The application's side of <see cref="IFolderHandover"/>: before the file
/// operations move a folder whole, every tab in every window lets go of what
/// it holds at or under it; while the folder moves, nothing re-opens there;
/// afterwards the tabs, and everything else that remembers the folder by
/// name, follow it — or, when the move failed, are put back exactly where
/// they were.
///
/// **Vaktari refused its own renames.** On Windows a folder cannot be renamed
/// or binned whole while anything beneath it is open, and Vaktari itself held
/// handles there: the watch of a tab in a subfolder, in any window, the watch
/// it keeps on a repository's .git, a load still reading, a folder read on a
/// timer, the git status of the pane F2 was pressed in, a search walking
/// through it and a Size total being measured (rename-notes, plan §1e and §2).
/// Renaming the folder above any of them answered "Access to the path … is
/// denied." This is what lets go of them.
///
/// **Only Vaktari's own.** Nothing here looks at, names, or asks anything of
/// another program: when something else still holds the folder, the engine
/// says so in plain words (InUseException), and the window says where a
/// person can look.
///
/// One per application, made where the stores are and handed to the one
/// engine every window shares (WindowServices.Create); every pane finds it
/// through that engine. Everything here runs on the dispatcher.
/// </summary>
internal sealed class FolderHandover : IFolderHandover
{
    private readonly Func<IEnumerable<ShellViewModel>> _shells;

    public FolderHandover(Func<IEnumerable<ShellViewModel>> shells) => _shells = shells;

    /// <summary>
    /// How long a hold waits for what is still letting go — a load mid-read,
    /// a folder read on a timer mid-tick, a git status being stopped — before
    /// the engine tries anyway. The engine's own second try covers a moment
    /// more; past that the person is told something has it open, which is
    /// then true. Settable so a test can make it short.
    /// </summary>
    internal static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The folders being moved right now, one entry per hold.
    /// Read from the pool (a load opens its watch there), so under a lock.</summary>
    private readonly List<string> _held = [];

    private readonly Lock _gate = new();

    /// <summary>
    /// Panes that wanted to open something while it was held, and what they
    /// are owed when the hold ends: a reload, or only the version-control
    /// read again.
    /// </summary>
    private readonly Dictionary<PaneViewModel, bool> _declined = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether <paramref name="path"/> is at or under a folder being
    /// moved — where nothing may be opened.</summary>
    public bool IsHeldOff(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        lock (_gate) return _held.Any(f => PathRules.Contains(f, path));
    }

    /// <summary>Whether a read rooted at <paramref name="path"/> would walk a
    /// folder being moved: <paramref name="path"/> is inside one, or one is
    /// inside it.</summary>
    public bool HoldsAround(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        lock (_gate) return _held.Any(f => PathRules.Contains(f, path) || PathRules.Contains(path, f));
    }

    /// <summary>
    /// A pane that was refused something while it was held, and so owes a
    /// reload — or just its version-control read — when the hold ends.
    /// **Without this a tab opened into the folder mid-rename stayed
    /// unwatched for good**, which the hold must never leave behind. From any
    /// thread.
    /// </summary>
    public void Declined(PaneViewModel pane, bool reload)
    {
        lock (_gate)
            _declined[pane] = reload || (_declined.TryGetValue(pane, out var owed) && owed);
    }

    /// <summary>Every live tab in every window.</summary>
    private List<PaneViewModel> Panes() => [.. _shells().SelectMany(s => s.AllTabs)];

    public async ValueTask<IFolderLease> ReleaseAsync(IReadOnlyList<string> folders, CancellationToken ct)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ReleaseHereAsync(folders));

        return await ReleaseHereAsync(folders).ConfigureAwait(true);
    }

    private async Task<IFolderLease> ReleaseHereAsync(IReadOnlyList<string> folders)
    {
        var held = folders.Select(PathRules.Normalise).ToList();

        // **Held before anything is let go**, so nothing let go of below can
        // be opened again behind it.
        lock (_gate) _held.AddRange(held);

        var waits = new List<Task>();
        var letGo = new List<PaneViewModel.LetGo>();

        try
        {
            foreach (var pane in Panes())
                if (pane.LetGoUnder(held, waits, Deadline) is { } let)
                    letGo.Add(let);

            waits.AddRange(Thumbnails.RowMetadata.CancelUnder(held));

            // Bounded: a load stuck on a share that has gone, or a git that
            // will not die, must not hold the rename up for ever. What is
            // still holding then is what the person is told about.
            var all = Task.WhenAll(waits);

            if (await Task.WhenAny(all, Task.Delay(Deadline)).ConfigureAwait(true) != all)
                Console.Error.WriteLine($"[vaktari] handover: still letting go after {Deadline.TotalSeconds:0.#} s · {string.Join(", ", held)}");
        }
        catch (Exception ex)
        {
            // Never the reason a rename fails: the engine tries anyway, and
            // the lease below still puts back whatever was let go.
            Quiet.Swallowed("handover", ex);
        }

        return new Lease(this, held, letGo);
    }

    /// <summary>
    /// Follows a folder that moved without being held — a move between
    /// folders, which goes item by item and which nothing of Vaktari's can
    /// block — so the tabs that were inside it end up where it went.
    /// </summary>
    public void Followed(string from, string to)
        => Dispatcher.UIThread.Post(() =>
        {
            var reload = new Dictionary<PaneViewModel, bool>(ReferenceEqualityComparer.Instance);

            Follow(PathRules.Normalise(from), PathRules.Normalise(to), reload);
            Put(reload);
        });

    /// <summary>
    /// Carries everything that names <paramref name="from"/> to
    /// <paramref name="to"/>: every tab and its history in every window, the
    /// tabs closed and the split put away, the folder tree, the shared links,
    /// the view each folder remembers, the recent lists, the pins and the cut
    /// waiting to be pasted. A tab that has a listing is added to
    /// <paramref name="reload"/>, to be read at its new place.
    /// </summary>
    private void Follow(string from, string to, Dictionary<PaneViewModel, bool> reload)
    {
        var shells = _shells().ToList();

        foreach (var shell in shells)
        {
            foreach (var pane in shell.AllTabs)
                if (pane.Follow(from, to) && pane.IsLive)
                    reload[pane] = true;

            shell.FollowRemembered(from, to);
        }

        PaneViewModel.FolderViews?.Rebase(from, to);
        PaneViewModel.Recents?.Rebase(from, to);

        if (PaneViewModel.Places is { } places)
            _ = RepointAsync(places, from, to, shells);

        if (shells.FirstOrDefault() is { } first) _ = first.FollowCutAsync(from, to);
    }

    /// <summary>
    /// The name a pin at <paramref name="path"/> is given when nobody chooses
    /// one: what ShellViewModel pins a search under, and a folder's leaf.
    /// </summary>
    internal static string GivenName(string path)
        => VirtualPaths.IsSearch(path) ? PaneViewModel.SearchStepName(path) : PathRules.LeafName(path);

    /// <summary>The pins, carried, and every sidebar rebuilt if one moved.</summary>
    private static async Task RepointAsync(
        Core.Places.IPlacesProvider places, string from, string to, List<ShellViewModel> shells)
    {
        try
        {
            // By the tabs' own rule, so a pinned search started in the folder
            // follows it as a tab on that search does.
            if (!await places.RepointAsync(path => VirtualPaths.Rebase(path, from, to), GivenName, CancellationToken.None)
                    .ConfigureAwait(true)) return;

            foreach (var shell in shells) await shell.Sidebar.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("handover", ex);
        }
    }

    /// <summary>
    /// Reads again every pane owed it, and asks the version-control read again
    /// of the rest — including every pane that was refused something while
    /// the hold lasted.
    /// </summary>
    private void Put(Dictionary<PaneViewModel, bool> reload)
    {
        lock (_gate)
        {
            foreach (var (pane, full) in _declined)
                reload[pane] = full || (reload.TryGetValue(pane, out var owed) && owed);

            _declined.Clear();
        }

        foreach (var (pane, full) in reload)
        {
            if (full) pane.ReloadAfterHandover();
            else pane.RereadVcsAfterHandover();
        }
    }

    /// <summary>
    /// The end of one hold. On the dispatcher. Whatever was told it moved is
    /// followed, then every pane that let go — and every pane refused while
    /// it lasted — is read again: at its new place, or exactly where it was
    /// when the move failed or the folder went to the bin.
    /// </summary>
    private void End(Lease lease)
    {
        lock (_gate)
            foreach (var folder in lease.Folders)
                _held.Remove(folder);

        var reload = new Dictionary<PaneViewModel, bool>(ReferenceEqualityComparer.Instance);

        foreach (var let in lease.LetGo)
            reload[let.Pane] = let.Reload || (reload.TryGetValue(let.Pane, out var owed) && owed);

        foreach (var (from, to) in lease.Moves)
            Follow(from, to, reload);

        Put(reload);
    }

    private sealed class Lease(FolderHandover owner, List<string> folders, List<PaneViewModel.LetGo> letGo)
        : IFolderLease
    {
        private readonly Lock _gate = new();
        private readonly List<(string From, string To)> _moves = [];
        private int _ended;

        public List<string> Folders { get; } = folders;

        public List<PaneViewModel.LetGo> LetGo { get; } = letGo;

        public List<(string From, string To)> Moves
        {
            get { lock (_gate) return [.. _moves]; }
        }

        public void Moved(string from, string to)
        {
            lock (_gate) _moves.Add((PathRules.Normalise(from), PathRules.Normalise(to)));
        }

        /// <summary>
        /// Nothing to follow: the panes inside are read again where they were,
        /// find the folder gone, and wait for it to come back — which an undo
        /// of the bin brings.
        /// </summary>
        public void Gone(string folder) { }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;

            if (Dispatcher.UIThread.CheckAccess()) owner.End(this);
            else await Dispatcher.UIThread.InvokeAsync(() => owner.End(this));
        }
    }
}
