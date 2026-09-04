using CommunityToolkit.Mvvm.ComponentModel;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Folders opened in place in the details listing, the way Dolphin's
/// expandable folders work: a triangle on a folder row splices that folder's
/// contents in underneath it, indented, without leaving the folder you are in.
///
/// **The tree is a PROJECTION, and <see cref="PaneViewModel.Entries"/> keeps
/// its meaning.** Entries is the flat, sorted, filtered contents of
/// CurrentPath, and everything that describes the FOLDER reads it that way: the
/// status bar's count, the empty state, the "N of M" filter line, the
/// look-alike set, the watcher's sorted insert, the row a rename steps to — and
/// the grid and compact layouts, which bind the very same collection. Splicing
/// children into it would have put indented rows into two layouts that lay out
/// fixed-size cells and can draw neither an indent nor a triangle.
///
/// So the splice lives here, in a second collection that only the details
/// listing binds, and it is the IDENTITY when nothing is expanded: with no
/// folder open, <c>DetailsEntries</c> hands back <c>Entries</c> itself, so an
/// ordinary listing costs no second copy of its rows and runs what it ran
/// before this file existed plus two calls that do nothing on an empty
/// dictionary.
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- what is open ------------------------------------------------------

    /// <summary>
    /// Each expanded folder and the rows it holds, read once when it was
    /// opened.
    ///
    /// Keyed by path rather than by row, because a row is a
    /// <see cref="FileEntry"/> record struct carrying a length and a timestamp:
    /// the entry for a folder whose contents changed is not equal to the one
    /// that was clicked. The same reason <c>Reselect</c> works in paths.
    ///
    /// **Nothing stays in here that is not on screen.** Closing a folder drops
    /// everything opened inside it as well — see <c>Forget</c>. Keeping the
    /// inner ones was tried first and measured wrong: with docs and docs/inner
    /// both open, closing docs left this dictionary holding one entry that
    /// nothing could see, which kept <c>ExpansionApplies</c> true, kept
    /// <c>DetailsEntries</c> handing back a second full copy of the listing
    /// forever, and made every later refresh re-read a folder for rows nobody
    /// would ever be shown.
    /// </summary>
    private readonly Dictionary<string, List<FileEntry>> _open =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Folders whose read is in flight.
    ///
    /// **A second press on one of these cancels the read rather than starting
    /// another.** Measured without it: two presses on a folder that had not
    /// answered ran two enumerations, both landed, both wrote <c>_open</c>, and
    /// the folder finished OPEN — the opposite of what the press handler
    /// promises and of what a second press means anywhere else.
    /// </summary>
    private readonly HashSet<string> _opening = new(StringComparer.Ordinal);

    /// <summary>
    /// The spliced listing. Only ever bound while something is expanded — see
    /// the identity rule in this class's summary.
    /// </summary>
    private readonly BulkObservableCollection<FileEntry> _rows = new();

    /// <summary>Whether <c>DetailsEntries</c> is currently handing back
    /// <see cref="_rows"/> rather than <c>Entries</c>. Kept so the swap between
    /// the two raises exactly one notification, on the edge.</summary>
    private bool _projected;

    /// <summary>Whether a reload of the open folders is already running — see
    /// <see cref="ReloadExpandedAsync"/>.</summary>
    private bool _reloading;

    /// <summary>
    /// How deep each spliced row sits. The depths, not the pixels: the pixels
    /// depend on the pane's zoom and this does not, so a zoom rebuilds
    /// <see cref="Indents"/> out of this rather than re-running the splice.
    /// </summary>
    private readonly Dictionary<string, int> _depths = new(StringComparer.Ordinal);

    /// <summary>
    /// How far one level of nesting shifts a row, in pixels.
    ///
    /// **Derived from the row icon rather than written as 16**, so it keeps its
    /// ratio to IconSize through the pane zoom — the rule PaneScale's own
    /// IconStroke comment states. At 200% the icons and the type double, and a
    /// fixed step would have left the nesting reading as noise beside them.
    /// </summary>
    public double IndentStep => Math.Round(PaneScale.RowIcon * IconScale, 1);

    /// <summary>
    /// Whether this listing offers expansion at all.
    ///
    /// **Only a real folder.** The bin and Recent hold rows naming where a file
    /// USED to be, This PC holds volumes rather than directories, and a search
    /// result is a path from anywhere on the machine — so "open this row in
    /// place" in any of them would list something the row does not stand for,
    /// or nest a result underneath a result already in the same list. It is the
    /// same gate <c>IsRealFolder</c> already draws for the address bar, the
    /// terminal and Ctrl+D.
    ///
    /// Bound by the row template and by the column heading, which reserve the
    /// triangle's slot together — a name of its own rather than
    /// <c>IsRealFolder</c> at both sites, so that narrowing or widening which
    /// listings expand is one edit here rather than a search for every binding
    /// that happened to mean this.
    ///
    /// Raised beside <c>IsRealFolder</c> in <c>OnCurrentPathChanged</c>, which
    /// is the only thing that can change it.
    /// </summary>
    public bool CanExpandRows => IsRealFolder;

    /// <summary>
    /// The paths of every folder that is open, for the triangle to read.
    ///
    /// Same shape as <c>Confusable</c> and for the same reason: the row
    /// supplies its own path and the pane supplies the set, and binding the SET
    /// is what makes every realized row re-evaluate the moment one of them is
    /// opened.
    /// </summary>
    [ObservableProperty] private IReadOnlySet<string> _expanded =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// How far in each spliced row is drawn, in PIXELS. Rows of the folder
    /// itself are absent rather than present with a zero, so an unexpanded
    /// listing holds an empty map.
    ///
    /// Pixels rather than levels because the step scales with the pane's icon
    /// zoom — see <see cref="IndentStep"/> — and a converter that multiplied
    /// would need the scale bound into it a third time.
    /// </summary>
    [ObservableProperty] private IReadOnlyDictionary<string, double> _indents =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Whether this path's folder is open. Public because the keyboard
    /// asks it before deciding what an arrow key means.</summary>
    public bool IsExpanded(string? path) => path is not null && _open.ContainsKey(path);

    /// <summary>
    /// Whether the listing on screen holds rows from more than this folder.
    ///
    /// Asked by the window's select-everything gestures, which have to tick the
    /// folder's rows rather than the screen's — see
    /// <c>MainWindow.SelectWholeFolder</c>.
    /// </summary>
    internal bool RowsAreSpliced => _projected;

    /// <summary>
    /// The rows the listing is really showing, for the window to read.
    ///
    /// Exposed for the rename run, which steps from one row to the row beside
    /// it: a run that started on a child would have stopped dead against
    /// <c>Entries</c>, because <c>RenameRun.Next</c> answers null for a path
    /// the list it was given does not hold.
    /// </summary>
    internal IReadOnlyList<FileEntry> Rows => VisibleRows;

    /// <summary>
    /// Whether the splice is being applied right now.
    ///
    /// **Details only, and only with no filter up** — and in both cases the
    /// expansion is IGNORED rather than cleared, exactly as
    /// <c>EffectiveGroupBy</c> ignores a grouping outside the details view. Go
    /// to the grid and back, or type in the filter box and clear it, and the
    /// tree you opened is still open.
    ///
    /// The filter asks a question about this folder and answers it as a flat
    /// list. Applying it to the children instead would mean a child that
    /// matches under a parent that does not, which is either an orphan row or a
    /// rule that keeps unmatched ancestors on screen — and then the count line
    /// beside it can no longer say what it filtered.
    /// </summary>
    private bool ExpansionApplies
        => _open.Count > 0 && View == ViewMode.Details && FilterText.Length == 0;

    /// <summary>
    /// The rows the layout on screen is actually showing.
    ///
    /// **The same object as <c>Entries</c> whenever nothing is expanded**, which
    /// is what lets the selection restore, the type-ahead and the heading's
    /// tick box read one thing and behave identically to before.
    /// </summary>
    private IReadOnlyList<FileEntry> VisibleRows => _projected ? _rows : Entries;

    // ---- the gesture -------------------------------------------------------

    /// <summary>
    /// Opens the folder on this row in place, or closes it again.
    ///
    /// Collapsing is synchronous and expanding is not: closing needs nothing
    /// the pane does not already hold, while opening is a directory read that
    /// on a share can take as long as any other listing.
    /// </summary>
    public async Task ToggleExpandAsync(FileEntry entry)
    {
        if (!CanExpandRows || !entry.IsDirectory) return;
        if (entry.FullPath is not { Length: > 0 } path) return;

        // A press that arrives while this folder is still being read cancels
        // that read instead of starting a second one — see _opening for what
        // two presses did without this.
        if (!_opening.Add(path)) { _opening.Remove(path); return; }

        if (_open.ContainsKey(path))
        {
            _opening.Remove(path);

            // **The keyboard goes to the folder, not with the rows.** Measured:
            // with a child row focused, closing its folder left SelectedEntry
            // naming a path the listing no longer showed, and Reselect does not
            // repoint it because it restores nothing. Dolphin puts it on the
            // folder; so does this.
            if (SelectedEntry is { } focused
                && PathRules.Contains(path, focused.FullPath))
                SelectedEntry = entry;

            // Everything opened inside it goes too, or the dictionary would
            // hold rows nobody can see — see _open.
            Forget(path);

            Republish();
            return;
        }

        // Captured before the read and re-checked after it, the rule every
        // other awaited step in this pane follows: a navigation started while
        // the folder was being read belongs to a different listing, and
        // splicing into that one would show rows from somewhere else.
        var generation = _generation;

        var children = await ReadChildrenAsync(path).ConfigureAwait(true);

        // Cancelled by a second press while it was reading, or superseded by a
        // navigation.
        if (!_opening.Remove(path) || generation != _generation) return;

        if (children is null)
        {
            Status = $"could not open {entry.Name}";
            return;
        }

        _open[path] = children;

        Republish();
    }

    /// <summary>
    /// Everything one directory holds, in the order it will be shown.
    ///
    /// Null for a folder that could not be read, which is a message rather than
    /// a crash: the row is one click away from a permission error on every
    /// platform.
    ///
    /// <c>CancellationToken.None</c> and a generation re-check rather than the
    /// pane's own token, which is what the watcher's stat pass does and for the
    /// same reason — the token source is disposed by the next navigation, and
    /// the generation is the check that actually decides whether the answer is
    /// still wanted. What stops the reads piling up is that only one press and
    /// only one reload can be in flight per folder: see <c>_opening</c> and
    /// <c>_reloading</c>.
    /// </summary>
    private async Task<List<FileEntry>?> ReadChildrenAsync(string path)
    {
        var options = new ListingOptions { IncludeHidden = ShowHidden, BatchSize = 500 };
        var children = new List<FileEntry>();

        try
        {
            await foreach (var batch in _fs
                               .EnumerateAsync(path, options, CancellationToken.None)
                               .ConfigureAwait(false))
                children.AddRange(batch);
        }
        catch (Exception ex)
        {
            // NO KILLING MUTATION, and it was looked for: replacing this with
            // `_ = ex;` left the whole suite green, because every test that
            // reaches a refused folder asserts on the Status line and the row
            // staying shut rather than on the log. It stays for the reason
            // every other Swallowed call does — a failure nothing records is a
            // failure nobody can diagnose.
            Vaktari.Core.Quiet.Swallowed("expand", ex);
            return null;
        }

        children.Sort(CompareWithin);

        return children;
    }

    // ---- the projection ----------------------------------------------------

    /// <summary>
    /// Rebuilds the spliced listing and puts the selection back.
    ///
    /// **The capture is a guard, and the measurement says so.** A ReplaceAll
    /// raises a Reset, which is what <c>ApplyFilter</c> and <c>ResortInPlace</c>
    /// both record emptying the real listing's selection — but against a
    /// ListBox bound to this pane the same two ways the markup binds it
    /// (ItemsSource and SelectedItem, with DetailsSelection as SelectedItems),
    /// neither this rebuild's Reset NOR the ItemsSource swap on the first open
    /// lost the selection: FileEntry is a record struct with value equality and
    /// the selected row is in both collections. So no test here goes red
    /// without these two lines. They stay because the two rebuilds either side
    /// of this one record the opposite against the full listing, and a third
    /// that skipped what they do would be the odd one out for no reason.
    /// </summary>
    private void Republish()
    {
        var keep = SelectedPaths();

        Reproject();

        Reselect(keep);
    }

    /// <summary>
    /// Puts every open folder's rows back into the order the listing is now in.
    ///
    /// A folder's rows are sorted once, when it is read, and the order can
    /// change afterwards — clicking a heading turns the whole listing over, and
    /// a subtree left in the order it was read in would run the other way to
    /// everything around it.
    ///
    /// Asked for by the two rebuilds that can change the order and by nothing
    /// else: the watcher's batch, a view switch and an expand all rebuild the
    /// splice without touching the comparer, and a sort per open folder per
    /// watcher burst is a cost with nothing to buy.
    /// </summary>
    private void ReorderOpenFolders()
    {
        foreach (var children in _open.Values)
            children.Sort(CompareWithin);
    }

    /// <summary>
    /// Rebuilds <see cref="_rows"/> from <c>Entries</c> and what is open.
    ///
    /// Called after every rebuild of Entries — the sort, the filter, the
    /// watcher's batch, the end of a load — because the splice is derived from
    /// it and nothing else keeps the two in step.
    /// </summary>
    private void Reproject()
    {
        var applies = ExpansionApplies;

        _depths.Clear();

        if (!applies)
        {
            // NO KILLING MUTATION, and it is declared: VisibleRows reads
            // _projected rather than `applies`, so a stale _rows is never shown
            // and no assertion can see one. It is memory hygiene — without it a
            // pane that expanded once in a 200k folder would hold a second copy
            // of that listing until the tab closed.
            _rows.Reset();

            // Empty rather than the open set: with the splice not applying,
            // nothing on screen IS open, and a triangle turned down over a
            // folder showing none of its rows would be saying otherwise.
            PublishIndents();
            Expanded = new HashSet<string>(StringComparer.Ordinal);
        }
        else
        {
            var rows = new List<FileEntry>(Entries.Count);

            Splice(rows, Entries, 0);

            _rows.ReplaceAll(rows);

            PublishIndents();
            Expanded = new HashSet<string>(_open.Keys, StringComparer.Ordinal);
        }

        if (applies == _projected) return;

        _projected = applies;

        // The one place the details listing changes which collection it is
        // bound to. On the edge only, because re-assigning an ItemsSource
        // re-realizes every container and empties the list's selection.
        NotifyLayoutEntries();
    }

    /// <summary>
    /// Publishes <see cref="_depths"/> as the pixel widths the rows draw.
    ///
    /// Split from the splice because the two change for different reasons: the
    /// depths change when a folder is opened or closed, and the pixels change
    /// again whenever the pane is zoomed — which must not re-run the splice,
    /// since that is a Reset over every row on screen per wheel tick.
    /// </summary>
    private void PublishIndents()
    {
        var step = IndentStep;
        var map = new Dictionary<string, double>(_depths.Count, StringComparer.Ordinal);

        foreach (var (path, depth) in _depths) map[path] = depth * step;

        Indents = map;
    }

    /// <summary>
    /// One level of the tree, and everything open underneath it.
    ///
    /// Recursion depth is the number of folders somebody has opened inside one
    /// another, so it is bounded by clicks rather than by the filesystem.
    /// </summary>
    private void Splice(List<FileEntry> into, IReadOnlyList<FileEntry> source, int depth)
    {
        foreach (var row in source)
        {
            into.Add(row);

            if (depth > 0) _depths[row.FullPath] = depth;

            // NO KILLING MUTATION for `row.IsDirectory &&`, and it was looked
            // for: dropping it left the suite green, because only a directory
            // is ever put in _open in the first place — ToggleExpandAsync
            // refuses anything else. It stays as the cheap half of the test,
            // which spares a dictionary lookup on every file in the listing.
            if (row.IsDirectory && _open.TryGetValue(row.FullPath, out var children))
                Splice(into, children, depth + 1);
        }
    }

    /// <summary>
    /// Forgets every open folder.
    ///
    /// **Navigating somewhere else, never a refresh.** A path opened in this
    /// folder means nothing in the next one, and the tree would otherwise be
    /// carried into a listing that has none of its rows — but a refresh is the
    /// same folder, and refreshes are constant: a rename, a paste, a delete and
    /// an undo all end in one. Collapsing on those would make the feature
    /// unusable while files are being worked with, which is when it is wanted.
    /// The same <c>PathRules.Same</c> test the selection carry two lines above
    /// it already makes.
    /// </summary>
    private void ClearExpansion()
    {
        if (_open.Count == 0) return;

        _open.Clear();

        Reproject();
    }

    /// <summary>
    /// Drops a folder from the open set, and everything opened inside it.
    ///
    /// The descendants are the point. A folder collapsed with folders open
    /// inside it must take them with it, or the set holds rows nothing on
    /// screen accounts for; and a folder that has been DELETED must not leave
    /// them either, because re-created under the same name it would come back
    /// holding the contents its namesake had before it went.
    ///
    /// <c>PathRules.Contains</c> rather than a string prefix, because "/a" must
    /// not claim "/ab" — and it answers true for the path itself, so the folder
    /// and everything under it go in one sweep. A `_open.Remove(path)` beside
    /// it was written first and measured redundant: removing it changed no
    /// test, because this loop had already taken the folder out.
    /// </summary>
    private void Forget(string path)
    {
        foreach (var key in _open.Keys.ToList())
            if (PathRules.Contains(path, key))
                _open.Remove(key);

        // And the reads still in flight underneath it. Measured: open docs,
        // press docs/inner, collapse docs before inner answered — inner's read
        // landed afterwards and put itself back into the open set, where
        // nothing on screen could show it and nothing could take it out again.
        // The generation check cannot catch this one: a collapse is not a
        // navigation, so the generation has not moved.
        foreach (var key in _opening.ToList())
            if (PathRules.Contains(path, key))
                _opening.Remove(key);
    }

    /// <summary>
    /// Re-reads every open folder after a reload of the same listing, and drops
    /// the ones that are no longer there.
    ///
    /// A folder's children are read once, when it is opened, and the watcher
    /// watches CurrentPath alone — so nothing tells an open subfolder that
    /// something inside it changed. A refresh is the moment the rest of the
    /// listing is re-read from disk, and re-reading these alongside it is what
    /// keeps a paste into an open subfolder from leaving the rows it added
    /// invisible.
    ///
    /// Order does not matter: each read stands alone, and a folder whose parent
    /// has gone simply fails its own read.
    /// </summary>
    private async Task ReloadExpandedAsync(int generation)
    {
        if (_open.Count == 0) return;

        // **One reload at a time.** Measured: three refreshes against a folder
        // that had not answered left three un-cancellable enumerations in
        // flight, because ReadChildrenAsync takes CancellationToken.None and a
        // refresh is what a rename, a paste, a delete and an undo all end in.
        // A refresh that arrives while one is running is dropped: the one
        // running is reading the same folders from the same disk.
        if (_reloading) return;

        _reloading = true;

        try
        {
            foreach (var path in _open.Keys.ToList())
            {
                var children = await ReadChildrenAsync(path).ConfigureAwait(true);

                // The listing this reload belongs to has gone — navigated away
                // from, or away and back. Splicing these rows in would put a
                // folder's old contents underneath a row in a listing that was
                // read again after them.
                if (generation != _generation) return;

                if (children is null) _open.Remove(path);
                else _open[path] = children;
            }

            Republish();
        }
        finally { _reloading = false; }
    }
}
