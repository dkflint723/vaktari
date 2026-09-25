using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One folder in the tree: its name, whatever of it has been opened, and
/// whether it is open at all.
///
/// **A node is a place, not a listing.** It holds folders only — a tree that
/// showed files would be a second, worse copy of the pane beside it — and it
/// holds them one level at a time, read when the node is opened and dropped
/// when it is closed.
/// </summary>
public sealed partial class FolderNode : ObservableObject
{
    private readonly FolderTreeViewModel _tree;

    internal FolderNode(FolderTreeViewModel tree, string path, string label, int depth)
    {
        _tree = tree;
        Path = path;
        Label = label;
        Depth = depth;
    }

    public string Path { get; }

    /// <summary>What the row says. A root carries the name the sidebar gave it
    /// — "Home", a drive's label — and everything below carries its own leaf,
    /// because a tree that renamed folders as it went would be describing
    /// somewhere else.</summary>
    public string Label { get; }

    /// <summary>How far to indent. Carried rather than computed by walking up,
    /// because the row is drawn thousands of times and the answer never
    /// changes.</summary>
    public int Depth { get; }

    public ObservableCollection<FolderNode> Children { get; } = [];

    /// <summary>
    /// Whether to draw a triangle.
    ///
    /// **Assumed true until opening one says otherwise.** Knowing for certain
    /// costs a read of every child of every visible row — the tree would
    /// enumerate a whole level ahead of anything anyone asked for, on every
    /// expand. So every folder offers a triangle, and a folder that turns out
    /// to hold none loses it at the moment that becomes visible, which is the
    /// only moment a person could have noticed either way.
    /// </summary>
    [ObservableProperty] private bool _mayHaveChildren = true;

    /// <summary>True while its own read is in flight, so the row can say so
    /// rather than looking like a folder that is simply empty.</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>The folder could not be read. Kept on the node because the row
    /// is the only place that can say which folder it was.</summary>
    [ObservableProperty] private bool _isUnreadable;

    private bool _isExpanded;

    /// <summary>
    /// Open or closed. Setting it is what reads the folder — and closing it is
    /// what forgets what was read.
    ///
    /// **Closing forgets, deliberately.** Keeping a closed folder's children
    /// makes re-opening instant and makes it a lie: a tree that hands back what
    /// it read an hour ago shows folders that have since gone and misses ones
    /// that have arrived, and the person cannot tell which. Re-reading one
    /// level is a single enumeration, which is what opening it cost the first
    /// time. The listing's own expandable folders learned the same rule from
    /// the other end — see PaneViewModel.Expansion, where keeping them left
    /// rows alive that nothing could see.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;

            if (value)
            {
                _ = _tree.OpenAsync(this);
            }
            else
            {
                Forget();

                // Closing shows immediately; opening waits for the read, and
                // publishes when it lands.
                _tree.Reflow();
            }
        }
    }

    /// <summary>
    /// Drops what was read, and marks the children closed on the way out.
    ///
    /// The nodes being dropped are about to be unreachable, but their
    /// <see cref="IsExpanded"/> is what a pending read checks before it
    /// publishes — so a node that is thrown away mid-read has to be told, or
    /// the read lands in a collection nothing is showing.
    /// </summary>
    private void Forget()
    {
        foreach (var child in Children) child.IsExpanded = false;

        Children.Clear();
    }

    /// <summary>Opens without re-reading if it is already open — what revealing
    /// a path down a branch needs, since half of it may be open already.</summary>
    internal Task EnsureOpenAsync()
    {
        if (_isExpanded) return Task.CompletedTask;

        SetProperty(ref _isExpanded, true, nameof(IsExpanded));

        return _tree.OpenAsync(this);
    }
}

/// <summary>
/// The folder tree: the places the sidebar shows as roots, and whatever has
/// been opened under them.
///
/// **A tree is a way to get somewhere, not a second listing.** It shows
/// folders and never files, reads one level at a time, and navigates the pane
/// rather than holding a selection of its own — so everything that acts on
/// files goes on acting on the pane, and this cannot grow into a half-working
/// copy of it.
///
/// The reading is <see cref="IFileSystemProvider"/>'s, the same one the pane
/// enumerates through, so hidden files, links and the platform's own rules are
/// whatever they already are elsewhere rather than a second opinion.
/// </summary>
public sealed partial class FolderTreeViewModel : ObservableObject
{
    private readonly IFileSystemProvider _fs;

    public FolderTreeViewModel(IFileSystemProvider fs) => _fs = fs;

    public ObservableCollection<FolderNode> Roots { get; } = [];

    /// <summary>
    /// Every node currently on screen, in the order it is drawn, deepest last
    /// within each branch.
    ///
    /// **Flattened rather than a TreeView, and that is a design decision.** The
    /// sidebar draws every section as a list of buttons, and a TreeView would
    /// bring a selection model of its own — a second place that thinks it owns
    /// "what is selected", competing with the pane that actually does. A flat
    /// list indented by <see cref="FolderNode.Depth"/> draws the same picture
    /// with none of that, and keeps the rule this class is built on: the tree
    /// navigates the pane and holds nothing.
    ///
    /// Rebuilt whole whenever a branch opens or closes. A folder's worth of
    /// rows is a few hundred at most — this is a sidebar, not a listing — and
    /// splicing ranges in and out at the right offsets is the kind of
    /// arithmetic that is wrong once and then wrong for ever.
    /// </summary>
    public BulkObservableCollection<FolderNode> Rows { get; } = [];

    /// <summary>
    /// Walks what is open and republishes <see cref="Rows"/>.
    ///
    /// Called after anything that changes the shape, rather than by each of
    /// them separately: a node that opened, one that closed, and a rebuild of
    /// the roots all mean the same thing to a flat list.
    /// </summary>
    internal void Reflow()
    {
        var rows = new List<FolderNode>();

        void Walk(IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                rows.Add(node);

                // GUARD, and the mutation says so: taking it away reddens
                // nothing, because no reachable state has a closed node with
                // children in it. Forget clears them on the way out, and
                // OpenAsync refuses to publish into a node that has been
                // closed. It stays as the statement of which of those two is
                // load-bearing — this walk draws what is OPEN, and it should
                // not start depending on a collection being empty for its
                // answer.
                if (node.IsExpanded) Walk(node.Children);
            }
        }

        Walk(Roots);

        // One notification for the whole shape, as sorting does: a row-by-row
        // rebuild makes the sidebar flicker through every intermediate state.
        Rows.ReplaceAll(rows);
    }

    /// <summary>
    /// Whether folders the platform conceals are shown, which the pane decides
    /// and this follows — a tree hiding what the listing beside it shows would
    /// be two answers about one folder. Written by
    /// <see cref="SidebarViewModel.FollowHidden"/>, which also re-reads what
    /// is open when it changes — see <see cref="RereadAsync"/>.
    /// </summary>
    [ObservableProperty] private bool _showHidden;

    /// <summary>
    /// Where the pane is, so the row can be marked. **Path text, not a node**:
    /// the folder may not be anywhere in the tree, and holding a node would
    /// mean holding one that has been dropped by a collapse.
    /// </summary>
    [ObservableProperty] private string? _currentPath;

    /// <summary>
    /// The roots, from the places the sidebar already knows about.
    ///
    /// Replaces whatever was there: plugging in a stick rebuilds the sidebar's
    /// own groups from the desktop's list, and a tree that kept a root for a
    /// drive that has gone would offer a triangle onto nothing.
    /// </summary>
    public void SetRoots(IEnumerable<(string Path, string Label)> places)
    {
        foreach (var root in Roots) root.IsExpanded = false;

        Roots.Clear();

        foreach (var (path, label) in places)
            Roots.Add(new FolderNode(this, path, label, depth: 0));

        Reflow();
    }

    /// <summary>
    /// Reads one level into <paramref name="node"/>.
    ///
    /// **Everything here is checked again after the await.** A person can close
    /// a folder, or the roots can be rebuilt under it, while the enumeration is
    /// in flight — so the node's own state decides whether the answer is still
    /// wanted, and a stale one is dropped rather than spliced into a collection
    /// nobody is looking at.
    /// </summary>
    internal async Task OpenAsync(FolderNode node)
    {
        node.IsLoading = true;
        node.IsUnreadable = false;

        var found = new List<FolderNode>();

        try
        {
            var options = new ListingOptions { IncludeHidden = ShowHidden };

            await foreach (var batch in _fs.EnumerateAsync(node.Path, options, CancellationToken.None)
                               .ConfigureAwait(true))
            {
                foreach (var entry in batch)
                {
                    // Folders only, and a link to one counts: it is somewhere a
                    // person can go, which is the whole question a tree answers.
                    // Following it is one click at a time, so a link that leads
                    // back up its own branch costs a triangle rather than a hang.
                    if (!entry.IsDirectory) continue;

                    found.Add(new FolderNode(this, entry.FullPath, entry.Name, node.Depth + 1));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that will not open keeps its row and says so. Throwing
            // here would take down the tree for one unreadable folder, which is
            // the shape SafeWalk exists to refuse everywhere else.
            node.IsLoading = false;
            node.IsUnreadable = true;
            node.MayHaveChildren = false;

            // **No Reflow, and that was measured rather than assumed.** One
            // stood here saying the row had changed — and a folder that cannot
            // be opened gains no children, so the flat list is identical
            // either way. What the row draws differently comes from the two
            // properties above, which notify on their own. Taking it out
            // reddens nothing, so it is out.
            return;
        }

        node.IsLoading = false;

        // Closed while the read was running, so the answer is not wanted.
        if (!node.IsExpanded) return;

        // The listing's own ordering, so "file2" comes before "file10" here as
        // it does in the pane rather than beside it.
        found.Sort(static (a, b) => Core.NaturalOrder.Compare(a.Label, b.Label));

        // **A re-read keeps the nodes it already had**, by path, so a folder
        // that was open under this one stays open with its own children —
        // see RereadAsync, which is the only way an open node is read twice.
        // On a first open there is nothing to keep and this is a plain fill.
        var kept = new Dictionary<string, FolderNode>(PathRules.Comparer);

        foreach (var child in node.Children) kept.TryAdd(child.Path, child);

        var children = found
            .Select(child => kept.Remove(child.Path, out var had) ? had : child)
            .ToList();

        // What has gone is closed on the way out, for Forget's reason: a read
        // still pending under it must not land.
        foreach (var gone in kept.Values) gone.IsExpanded = false;

        node.Children.Clear();

        foreach (var child in children) node.Children.Add(child);

        // **The triangle goes when the folder turns out to hold nothing**, and
        // this is the moment it could first be known — see MayHaveChildren.
        node.MayHaveChildren = found.Count > 0;

        Reflow();
    }

    /// <summary>
    /// Reads every open folder again, keeping open what is still there, and
    /// then reveals <paramref name="path"/>.
    ///
    /// **Showing hidden folders in the pane left the tree answering the old
    /// question.** <see cref="EnsureOpenAsync"/> does not re-read a folder that
    /// is already open — rightly, see Revealing_twice_reads_each_folder_once —
    /// so a home folder opened without its hidden children went on lacking
    /// them after the switch, and a reveal into ~/.config stopped at home
    /// because the step it wanted was not a child. The open folders are the
    /// ones that were read under the old rule, so they are the ones read
    /// again; a closed one reads under the new rule when it is next opened.
    ///
    /// Top down and one at a time, so a folder is read after its parent has
    /// decided whether it still exists.
    ///
    /// **It revealed where the pane had been when it started.** The pane's
    /// hidden-files answer can change a moment before the pane's place does —
    /// switching to a tab that shows them, or a folder whose remembered view
    /// turns them on, both change the answer first — so the path this was
    /// given was the folder being left. The newer place's own reveal ran at
    /// once, through folders still read under the old rule, and stopped short;
    /// then this finished last and marked the folder the pane had left.
    /// <paramref name="where"/> is asked when the reading is done, so what is
    /// revealed is wherever the pane is by then.
    /// </summary>
    public async Task RereadAsync(Func<string?> where)
    {
        async Task Walk(IEnumerable<FolderNode> nodes)
        {
            // Over a copy: the read below replaces the collection being walked.
            foreach (var node in nodes.ToList())
            {
                if (!node.IsExpanded) continue;

                await OpenAsync(node).ConfigureAwait(true);

                await Walk(node.Children).ConfigureAwait(true);
            }
        }

        await Walk(Roots).ConfigureAwait(true);

        await RevealAsync(where()).ConfigureAwait(true);
    }

    /// <summary>
    /// Counts reveals, so that only the newest one may mark a row. See
    /// <see cref="RevealAsync"/>.
    /// </summary>
    private int _reveals;

    /// <summary>
    /// Opens the branch down to <paramref name="path"/>, so that where the pane
    /// is can be seen in the tree.
    ///
    /// **Ancestors rather than text.** Which root a path belongs under is
    /// <see cref="PathRules.Contains"/>'s question — it ends the prefix at a
    /// separator, so "/media/one" cannot claim "/media/onetwo" — and the steps
    /// down are <see cref="PathRules.Ancestors"/>', whose own note records the
    /// inline loop that spun forever on a Windows path.
    ///
    /// The deepest root wins, so a drive and a folder pinned inside it both
    /// being roots reveals under the one that says more. Nothing is opened at
    /// all when the path is under none of them, which is the ordinary case for
    /// a virtual listing: the bin is not in any tree.
    ///
    /// **An older reveal could finish last and mark the folder already left.**
    /// A reveal waits on a read at every level it has to open, and the pane
    /// does not wait for it: go into a branch nobody has opened and then
    /// straight to a folder whose branch is open, and the second reveal marked
    /// its row at once while the first was still reading — then the first one
    /// landed and moved the mark back. Each reveal takes a number on the way in
    /// and gives up after any wait if a newer one has started since.
    /// </summary>
    public async Task RevealAsync(string? path)
    {
        var mine = ++_reveals;

        if (string.IsNullOrEmpty(path)) return;

        var root = Roots
            .Where(r => PathRules.Contains(r.Path, path))
            .OrderByDescending(r => PathRules.Normalise(r.Path).Length)
            .FirstOrDefault();

        if (root is null) return;

        var node = root;

        foreach (var step in PathRules.Ancestors(path))
        {
            if (PathRules.Same(step, node.Path)) continue;

            // Only the steps below the root it was found under: Ancestors walks
            // from the filesystem root, and the ones above are not in the tree.
            if (!PathRules.Contains(root.Path, step)) continue;

            await node.EnsureOpenAsync().ConfigureAwait(true);

            if (mine != _reveals) return;

            if (node.Children.FirstOrDefault(c => PathRules.Same(c.Path, step)) is not { } next)
                return;

            node = next;
        }

        CurrentPath = node.Path;
    }
}
