using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The folder tree: what it reads, when it reads it, and what it forgets.
///
/// **A tree is a way to get somewhere rather than a second listing**, so these
/// hold it to showing folders and nothing else, to reading one level at the
/// moment a folder is opened, and to dropping what it read when the folder is
/// closed — a tree that hands back what it read an hour ago shows folders that
/// have gone and misses ones that arrived.
///
/// Driven through a fake provider rather than a real tree: the interesting
/// half is which nodes exist and how many reads it took, and a fake is the
/// only thing that can count reads.
/// </summary>
public sealed class FolderTreeTests
{
    private static string P(params string[] parts)
        => OperatingSystem.IsWindows()
            ? @"C:\" + string.Join('\\', parts)
            : "/" + string.Join('/', parts);

    private static string Root => OperatingSystem.IsWindows() ? @"C:\" : "/";

    /// <summary>A provider over a fixed set of paths, counting what it is asked.</summary>
    private sealed class Tree : IFileSystemProvider
    {
        private readonly Dictionary<string, List<FileEntry>> _folders = new(PathRules.Comparer);

        public List<string> Read { get; } = [];

        public HashSet<string> Refuse { get; } = new(PathRules.Comparer);

        public Tree Holding(string folder, params (string Name, bool IsDirectory)[] children)
        {
            _folders[folder] = [.. children.Select(c => new FileEntry(
                c.Name,
                Path.Combine(folder, c.Name),
                0,
                default,
                c.IsDirectory ? EntryFlags.Directory : EntryFlags.None))];

            return this;
        }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Read.Add(path);

            await Task.CompletedTask;

            if (Refuse.Contains(path)) throw new UnauthorizedAccessException("no");

            yield return _folders.TryGetValue(path, out var rows) ? rows : [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    private static FolderTreeViewModel With(Tree fs, params (string, string)[] roots)
    {
        var tree = new FolderTreeViewModel(fs);

        tree.SetRoots(roots);

        return tree;
    }

    // ---- what it shows ------------------------------------------------------

    [Fact]
    public async Task Opening_a_folder_shows_the_folders_in_it_and_not_the_files()
    {
        var fs = new Tree().Holding(P("home"),
            ("notes.txt", false), ("docs", true), ("pics", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();

        Assert.Equal(["docs", "pics"], tree.Roots[0].Children.Select(c => c.Label));
    }

    /// <summary>**Nothing is read until a folder is opened.** A tree that read
    /// ahead would walk a level of every visible row before anyone asked.</summary>
    [Fact]
    public void Setting_the_roots_reads_nothing()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true));

        var tree = With(fs, (P("home"), "Home"));

        Assert.Empty(fs.Read);
        Assert.Single(tree.Roots);
    }

    [Fact]
    public async Task Children_come_back_in_the_order_the_listing_would_use()
    {
        var fs = new Tree().Holding(P("home"),
            ("file10", true), ("file2", true), ("Ardour", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();

        Assert.Equal(["Ardour", "file2", "file10"], tree.Roots[0].Children.Select(c => c.Label));
    }

    /// <summary>
    /// Every folder offers a triangle until opening one says otherwise —
    /// knowing sooner costs a read of every child of every visible row.
    /// </summary>
    [Fact]
    public async Task A_folder_that_holds_none_loses_its_triangle_when_opened()
    {
        var fs = new Tree().Holding(P("home"), ("empty", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();

        var empty = Assert.Single(tree.Roots[0].Children);

        Assert.True(empty.MayHaveChildren, "the triangle went before anything could know");

        await empty.EnsureOpenAsync();

        Assert.False(empty.MayHaveChildren);
        Assert.Empty(empty.Children);
    }

    /// <summary>One unreadable folder keeps its row and says so, rather than
    /// taking down the tree.</summary>
    [Fact]
    public async Task A_folder_that_will_not_open_says_so_and_the_tree_stands()
    {
        var fs = new Tree().Holding(P("home"), ("locked", true), ("docs", true));
        fs.Refuse.Add(P("home", "locked"));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();

        var locked = tree.Roots[0].Children.First(c => c.Label == "locked");

        await locked.EnsureOpenAsync();

        Assert.True(locked.IsUnreadable);
        Assert.False(locked.IsLoading);
        Assert.Equal(2, tree.Roots[0].Children.Count);
    }

    // ---- what it forgets ----------------------------------------------------

    /// <summary>
    /// **Closing forgets what was read.** Keeping it makes re-opening instant
    /// and makes it a lie: the folder may have changed while it was shut, and
    /// nothing on screen would say which rows are stale.
    /// </summary>
    [Fact]
    public async Task Closing_a_folder_forgets_what_was_in_it()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true));

        var tree = With(fs, (P("home"), "Home"));
        var home = tree.Roots[0];

        await home.EnsureOpenAsync();
        Assert.Single(home.Children);

        home.IsExpanded = false;

        Assert.Empty(home.Children);

        // And re-opening reads again rather than showing what it had.
        home.IsExpanded = true;
        await Task.Yield();

        Assert.Equal([P("home"), P("home")], fs.Read);
    }

    /// <summary>Closing a branch closes what was open inside it, or a pending
    /// read lands in a collection nothing is showing.</summary>
    [Fact]
    public async Task Closing_a_branch_closes_what_was_open_inside_it()
    {
        var fs = new Tree()
            .Holding(P("home"), ("docs", true))
            .Holding(P("home", "docs"), ("inner", true));

        var tree = With(fs, (P("home"), "Home"));
        var home = tree.Roots[0];

        await home.EnsureOpenAsync();

        var docs = Assert.Single(home.Children);
        await docs.EnsureOpenAsync();

        Assert.True(docs.IsExpanded);

        home.IsExpanded = false;

        Assert.False(docs.IsExpanded);
        Assert.Empty(docs.Children);
    }

    /// <summary>
    /// **A read that lands after its folder was closed is dropped.** Opening a
    /// folder on a slow disk and closing it again before it answers is one
    /// click and then another; without the check after the await, the rows
    /// arrive into a node nobody is looking at, and the branch silently opens
    /// itself back up underneath the person.
    /// </summary>
    [Fact]
    public async Task A_read_that_lands_after_its_folder_was_closed_is_dropped()
    {
        var gate = new TaskCompletionSource();

        var fs = new Held(gate).Holding(P("home"), ("docs", true));

        var tree = new FolderTreeViewModel(fs);

        tree.SetRoots([(P("home"), "Home")]);

        var home = tree.Roots[0];

        // Started, and stuck in the enumeration.
        var opening = home.EnsureOpenAsync();

        Assert.True(home.IsExpanded);
        Assert.Empty(home.Children);

        // Closed while it is still out there.
        home.IsExpanded = false;

        gate.SetResult();
        await opening;

        Assert.False(home.IsExpanded);
        Assert.Empty(home.Children);
    }

    /// <summary>A provider that does not answer until it is let go.</summary>
    private sealed class Held(TaskCompletionSource gate) : IFileSystemProvider
    {
        private readonly Tree _inner = new();

        public Held Holding(string folder, params (string Name, bool IsDirectory)[] children)
        {
            _inner.Holding(folder, children);
            return this;
        }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await gate.Task;

            await foreach (var batch in _inner.EnumerateAsync(path, options, ct))
                yield return batch;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => _inner.GetEntryAsync(path, ct);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
            => _inner.Watch(path, onChange);

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => _inner.IsReachableAsync(path, timeout, ct);

        public string Combine(string basePath, string name) => _inner.Combine(basePath, name);
        public string? GetParent(string path) => _inner.GetParent(path);
        public bool IsCaseSensitive => _inner.IsCaseSensitive;
    }

    // ---- the flat list the sidebar draws ------------------------------------

    /// <summary>
    /// **The rows are the tree flattened, not a TreeView's own idea of it.**
    /// The sidebar draws lists of buttons, and a TreeView would bring a
    /// selection model competing with the pane's — so what is on screen is
    /// this, indented by Depth.
    /// </summary>
    [Fact]
    public void The_rows_are_the_roots_until_something_is_opened()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true));

        var tree = With(fs, (P("home"), "Home"), (P("work"), "Work"));

        Assert.Equal(["Home", "Work"], tree.Rows.Select(r => r.Label));
        Assert.All(tree.Rows, row => Assert.Equal(0, row.Depth));
    }

    [Fact]
    public async Task Opening_a_folder_splices_its_children_in_under_it()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true), ("pics", true));

        var tree = With(fs, (P("home"), "Home"), (P("work"), "Work"));

        await tree.Roots[0].EnsureOpenAsync();

        // Under Home and above Work, which is what "in order, deepest last
        // within each branch" has to mean on screen.
        Assert.Equal(["Home", "docs", "pics", "Work"], tree.Rows.Select(r => r.Label));
        Assert.Equal([0, 1, 1, 0], tree.Rows.Select(r => r.Depth));
    }

    [Fact]
    public async Task Closing_a_folder_takes_its_rows_away_again()
    {
        var fs = new Tree()
            .Holding(P("home"), ("docs", true))
            .Holding(P("home", "docs"), ("work", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();
        await tree.Roots[0].Children[0].EnsureOpenAsync();

        Assert.Equal(["Home", "docs", "work"], tree.Rows.Select(r => r.Label));

        tree.Roots[0].IsExpanded = false;

        Assert.Equal(["Home"], tree.Rows.Select(r => r.Label));
    }

    /// <summary>A folder that could not be read still changed — its row says so
    /// now — and must not stay drawn as an open one.</summary>
    [Fact]
    public async Task A_folder_that_will_not_open_still_redraws_its_row()
    {
        var fs = new Tree().Holding(P("home"), ("locked", true));
        fs.Refuse.Add(P("home", "locked"));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();
        await tree.Roots[0].Children[0].EnsureOpenAsync();

        Assert.Equal(["Home", "locked"], tree.Rows.Select(r => r.Label));
    }

    [Fact]
    public async Task Revealing_a_branch_puts_every_step_of_it_on_screen()
    {
        var fs = new Tree()
            .Holding(P("home"), ("docs", true), ("pics", true))
            .Holding(P("home", "docs"), ("work", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.RevealAsync(P("home", "docs", "work"));

        Assert.Equal(["Home", "docs", "work", "pics"], tree.Rows.Select(r => r.Label));
        Assert.Equal([0, 1, 2, 1], tree.Rows.Select(r => r.Depth));
    }

    /// <summary>Rebuilding the roots — plugging in a stick — replaces what is
    /// drawn rather than adding to it.</summary>
    [Fact]
    public async Task Setting_the_roots_again_replaces_the_rows()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.Roots[0].EnsureOpenAsync();
        Assert.Equal(2, tree.Rows.Count);

        tree.SetRoots([(P("work"), "Work")]);

        Assert.Equal(["Work"], tree.Rows.Select(r => r.Label));
    }

    // ---- revealing where the pane is ---------------------------------------

    [Fact]
    public async Task Revealing_a_path_opens_the_branch_down_to_it()
    {
        var fs = new Tree()
            .Holding(P("home"), ("docs", true), ("pics", true))
            .Holding(P("home", "docs"), ("work", true))
            .Holding(P("home", "docs", "work"), ("q3", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.RevealAsync(P("home", "docs", "work"));

        var home = tree.Roots[0];

        Assert.True(home.IsExpanded);

        var docs = home.Children.First(c => c.Label == "docs");

        Assert.True(docs.IsExpanded);
        Assert.Equal(P("home", "docs", "work"), tree.CurrentPath);

        // The branch that was not asked for stays shut.
        Assert.False(home.Children.First(c => c.Label == "pics").IsExpanded);
    }

    /// <summary>
    /// **The prefix has to end at a separator**, or a root would claim a
    /// sibling whose name merely starts the same way — the trap PathRules
    /// documents, and the reason this asks Contains rather than StartsWith.
    /// </summary>
    [Fact]
    public async Task A_root_does_not_claim_a_folder_whose_name_merely_starts_the_same()
    {
        var fs = new Tree().Holding(P("media", "one"), ("inner", true));

        var tree = With(fs, (P("media", "one"), "One"));

        await tree.RevealAsync(P("media", "onetwo"));

        Assert.False(tree.Roots[0].IsExpanded);
        Assert.Null(tree.CurrentPath);
        Assert.Empty(fs.Read);
    }

    /// <summary>A virtual listing is under no root at all, and revealing it
    /// must open nothing rather than guess.</summary>
    [Fact]
    public async Task Revealing_somewhere_that_is_in_no_root_opens_nothing()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.RevealAsync("vaktari:trash");

        Assert.False(tree.Roots[0].IsExpanded);
        Assert.Empty(fs.Read);
    }

    /// <summary>
    /// **The deepest root wins.** A drive and a folder pinned inside it are
    /// both roots, and revealing under the drive would open a branch the person
    /// already has a shorter way to.
    /// </summary>
    [Fact]
    public async Task The_deepest_root_is_the_one_revealed_under()
    {
        var fs = new Tree()
            .Holding(Root, ("home", true))
            .Holding(P("home"), ("docs", true));

        var tree = With(fs, (Root, "This PC"), (P("home"), "Home"));

        await tree.RevealAsync(P("home", "docs"));

        Assert.False(tree.Roots[0].IsExpanded);
        Assert.True(tree.Roots[1].IsExpanded);

        // And the drive above it was never read.
        Assert.DoesNotContain(Root, fs.Read);
    }

    /// <summary>Revealing what is already open re-reads nothing: half the
    /// branch is usually open, and a tree that re-read it would collapse and
    /// rebuild rows under the pointer.</summary>
    [Fact]
    public async Task Revealing_twice_reads_each_folder_once()
    {
        var fs = new Tree()
            .Holding(P("home"), ("docs", true))
            .Holding(P("home", "docs"), ("work", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.RevealAsync(P("home", "docs", "work"));
        await tree.RevealAsync(P("home", "docs", "work"));

        Assert.Equal([P("home"), P("home", "docs")], fs.Read);
    }

    /// <summary>A path that is not there stops where the tree runs out, rather
    /// than marking a row that does not exist.</summary>
    [Fact]
    public async Task Revealing_a_folder_that_has_gone_stops_where_it_runs_out()
    {
        var fs = new Tree().Holding(P("home"), ("docs", true));

        var tree = With(fs, (P("home"), "Home"));

        await tree.RevealAsync(P("home", "gone", "deeper"));

        Assert.True(tree.Roots[0].IsExpanded);
        Assert.Null(tree.CurrentPath);
    }

    /// <summary>
    /// Hidden folders follow the pane rather than a second opinion: a tree
    /// hiding what the listing beside it shows is two answers about one folder.
    /// </summary>
    [Fact]
    public async Task Whether_hidden_folders_are_read_follows_the_setting()
    {
        var seen = new List<bool>();

        var fs = new Watching(seen).Holding(P("home"), ("docs", true));

        var tree = new FolderTreeViewModel(fs) { ShowHidden = true };

        tree.SetRoots([(P("home"), "Home")]);

        await tree.Roots[0].EnsureOpenAsync();

        Assert.Equal([true], seen);
    }

    /// <summary>Records the options each read was given.</summary>
    private sealed class Watching(List<bool> hidden) : IFileSystemProvider
    {
        private readonly Tree _inner = new();

        public Watching Holding(string folder, params (string Name, bool IsDirectory)[] children)
        {
            _inner.Holding(folder, children);
            return this;
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, CancellationToken ct)
        {
            hidden.Add(options.IncludeHidden);
            return _inner.EnumerateAsync(path, options, ct);
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => _inner.GetEntryAsync(path, ct);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
            => _inner.Watch(path, onChange);

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => _inner.IsReachableAsync(path, timeout, ct);

        public string Combine(string basePath, string name) => _inner.Combine(basePath, name);
        public string? GetParent(string path) => _inner.GetParent(path);
        public bool IsCaseSensitive => _inner.IsCaseSensitive;
    }
}
