using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Vcs;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A row on screen follows the repository, however it came to be on screen.
///
/// **A row listens for version-control snapshots only while it is attached**,
/// since a closed window's rows listening forever was what kept every closed
/// window in memory. The other half of that rule is the one a user sees: a row
/// that leaves the screen and comes back — another tab and back, the grid and
/// back, a split opened and closed, group headings, a scroll — must be
/// listening again once it is back, and must show a snapshot that landed while
/// it was away. Nothing else in the suite drives a mark through a real window;
/// the listener test in NewWindowTests counts handlers and asserts no mark.
///
/// Every step here changes what the repository says about a.txt, asks the pane
/// to look again the way the .git watcher does, and waits for the row's own
/// letter. And after every step, each row control on screen is listening
/// exactly once and no control off screen is listening at all: twice would be
/// a detach that leaves one behind, which is the leak again; never would be a
/// row that stops following the repository.
/// </summary>
public sealed class RowVcsOnScreenTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private readonly IVersionControl? _vcsBefore = PaneViewModel.Vcs;

    public override void Dispose()
    {
        PaneViewModel.Vcs = _vcsBefore;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A repository whose answer the test sets, for every folder under it.</summary>
    private sealed class Repository(string root) : IVersionControl
    {
        private volatile IReadOnlyDictionary<string, VcsState> _states = new Dictionary<string, VcsState>();

        public void Say(string path, VcsState state)
            => _states = new Dictionary<string, VcsState>(StringComparer.Ordinal) { [path] = state };

        public string Name => "scripted";

        public bool IsAvailable => true;

        public string? FindRoot(string folder) => root;

        public Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
            => Task.FromResult<VcsSnapshot?>(new VcsSnapshot(root, _states));
    }

    private sealed record Rig(MainWindow Window, Repository Repo, string Root, string A, HashSet<object?> Before)
        : IDisposable
    {
        public PaneViewModel Pane => Window.Shell.ActiveTab!;

        public void Dispose()
        {
            Window.Close();
            Settle();

            try { Directory.Delete(Root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> done, Func<string> why)
    {
        var deadline = DateTime.UtcNow + Ceiling;

        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, why());

            Settle();
            await Task.Delay(5);
        }

        Settle();
    }

    private async Task<Rig> BuildAsync(int files = 3)
    {
        UseSearch(PaneViewModel.Search);

        var root = Directory.CreateTempSubdirectory("vaktari-rowvcs").FullName;

        Directory.CreateDirectory(Path.Combine(root, ".git"));

        for (var i = 0; i < files; i++)
        {
            var name = i switch { 0 => "a.txt", 1 => "b.txt", 2 => "c.txt", _ => $"z{i:D4}.txt" };
            File.WriteAllText(Path.Combine(root, name), name);
        }

        // A fresh session: the class's state directory otherwise hands this
        // window whatever tabs and split the previous test's window left.
        var state = TestState.Current();
        Directory.CreateDirectory(state);

        var session = new Vaktari.Ui.Session.JsonSessionStore(state);
        session.NotifyChanged(new SessionState { Version = SessionState.CurrentVersion, Windows = [] });
        await session.FlushAsync(CancellationToken.None);
        await session.DisposeAsync();

        var before = Listening();
        var window = new MainWindow();

        // After the window, which installs the real git: the constructor
        // applies what it builds over anything set before it.
        var repo = new Repository(root);
        PaneViewModel.Vcs = repo;

        var a = Path.Combine(root, "a.txt");
        repo.Say(a, VcsState.Modified);

        window.Show();
        Settle();

        await window.Shell.ActiveTab!.NavigateAsync(root);
        await window.Shell.ActiveTab!.RefreshAsync();
        window.UpdateLayout();
        Settle();

        var rig = new Rig(window, repo, root, a, before);

        await Until(() => window.Shell.ActiveTab!.IsRepository, () => "GUARD: the pane never found the repository");
        await Shows(rig, "M", "the first snapshot");

        return rig;
    }

    // ---- what is listening, and what is on screen ------------------------------------

    private static FieldInfo ChangedField
        => typeof(RowVcs).GetField("Changed", BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException("RowVcs.Changed has no backing field");

    private static AvaloniaProperty WiredProperty
        => (AvaloniaProperty)(typeof(RowVcs).GetField("WiredProperty", BindingFlags.NonPublic | BindingFlags.Static)
                              ?.GetValue(null)
                              ?? throw new InvalidOperationException("RowVcs.WiredProperty is gone"));

    private static HashSet<object?> Listening()
        => [.. ((Delegate?)ChangedField.GetValue(null))?.GetInvocationList().Select(d => d.Target) ?? []];

    /// <summary>The control a handler belongs to: the handler holds the row's
    /// reapply delegate, which holds the row.</summary>
    private static Control? ControlOf(object? o, int depth = 0)
    {
        switch (o)
        {
            case Control c: return c;
            case null or string: return null;
            case Delegate d: return depth > 5 ? null : ControlOf(d.Target, depth + 1);
        }

        if (depth > 5) return null;

        foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (!f.FieldType.IsValueType && ControlOf(f.GetValue(o), depth + 1) is { } found)
                return found;

        return null;
    }

    /// <summary>
    /// Each wired row control in the window listens exactly once, and nothing
    /// this window made listens from off screen.
    /// </summary>
    private static void EveryRowOnScreenListensOnce(Rig rig, string step)
    {
        var added = ((Delegate?)ChangedField.GetValue(null))?.GetInvocationList()
                        .Where(d => !rig.Before.Contains(d.Target))
                        .Select(d => ControlOf(d.Target))
                        .ToList() ?? [];

        Assert.All(added, c => Assert.NotNull(c));

        var perControl = added.GroupBy(c => c!).ToDictionary(g => g.Key, g => g.Count());

        var wired = rig.Window.GetVisualDescendants().OfType<Control>()
                       .Where(c => c.GetValue(WiredProperty) is true)
                       .ToList();

        Assert.True(wired.Count > 0, $"{step}: GUARD: no wired row control on screen");

        var twice = perControl.Where(p => p.Value > 1).Select(p => p.Key).ToList();
        var offScreen = perControl.Keys.Where(c => TopLevel.GetTopLevel(c) is null).ToList();
        var deaf = wired.Where(c => !perControl.ContainsKey(c)).ToList();

        Assert.True(twice.Count == 0,
            $"{step}: {twice.Count} row control(s) listen more than once — a detach will leave one behind");
        Assert.True(offScreen.Count == 0,
            $"{step}: {offScreen.Count} row control(s) off screen are still listening");
        Assert.True(deaf.Count == 0,
            $"{step}: {deaf.Count} of {wired.Count} row control(s) on screen are not listening");
    }

    /// <summary>The letters a.txt's row shows right now, one per mark on screen.</summary>
    private static List<string> MarksOnA(Rig rig)
    {
        rig.Window.UpdateLayout();

        return [.. rig.Window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => RowVcs.GetEntry(t) is { } e && e.FullPath == rig.A && t.IsEffectivelyVisible)
                    .Select(t => t.Text ?? "")];
    }

    private static Task Shows(Rig rig, string glyph, string step)
        => Until(() => MarksOnA(rig) is { Count: > 0 } marks && marks.All(m => m == glyph),
                 () => $"{step}: a.txt's row shows [{string.Join(",", MarksOnA(rig))}], not {glyph}");

    /// <summary>What the .git watcher does when a file changes: the repository
    /// says something new, and the pane asks again.</summary>
    private static async Task Changes(Rig rig, PaneViewModel pane, VcsState state, string glyph, string step)
    {
        rig.Repo.Say(rig.A, state);
        pane.RefreshDecorations();

        await Shows(rig, glyph, step);
        EveryRowOnScreenListensOnce(rig, step);
    }

    // ---- the ways a row leaves the screen and comes back --------------------------------

    [AvaloniaFact]
    public async Task A_row_follows_the_repository_where_it_first_appears()
    {
        using var rig = await BuildAsync();

        EveryRowOnScreenListensOnce(rig, "first listing");

        await Changes(rig, rig.Pane, VcsState.Added, "A", "on screen");
        await Changes(rig, rig.Pane, VcsState.Untracked, "?", "on screen again");
    }

    [AvaloniaFact]
    public async Task A_row_follows_it_after_another_tab_and_back()
    {
        using var rig = await BuildAsync();

        var group = rig.Window.Shell.Left;
        var first = rig.Pane;
        Assert.Same(first, group.ActiveTab);

        var other = group.AddTab(Path.Combine(rig.Root, ".git"));

        group.ActiveTab = other;
        rig.Window.UpdateLayout();
        Settle();
        Assert.Same(other, group.ActiveTab);

        // A snapshot that lands while the row is not on screen.
        rig.Repo.Say(rig.A, VcsState.Conflicted);
        first.RefreshDecorations();

        await Until(() => first.VcsStates.TryGetValue(rig.A, out var s) && s == VcsState.Conflicted,
                    () => "GUARD: the hidden tab never heard the new snapshot");

        group.ActiveTab = first;
        rig.Window.UpdateLayout();
        Settle();

        await Shows(rig, "!", "back from the other tab, with a snapshot that landed while away");
        EveryRowOnScreenListensOnce(rig, "back from the other tab");

        await Changes(rig, first, VcsState.Added, "A", "after another tab and back");
    }

    [AvaloniaFact]
    public async Task A_row_follows_it_in_every_view_and_back()
    {
        using var rig = await BuildAsync();

        var pane = rig.Pane;

        pane.View = ViewMode.Grid;
        rig.Window.UpdateLayout();
        Settle();
        await Changes(rig, pane, VcsState.Added, "A", "grid");

        pane.View = ViewMode.Compact;
        rig.Window.UpdateLayout();
        Settle();
        await Changes(rig, pane, VcsState.Untracked, "?", "compact");

        pane.View = ViewMode.Details;
        rig.Window.UpdateLayout();
        Settle();
        await Changes(rig, pane, VcsState.Conflicted, "!", "details again");
    }

    [AvaloniaFact]
    public async Task A_row_follows_it_through_a_split_and_group_headings()
    {
        using var rig = await BuildAsync();

        var pane = rig.Pane;

        rig.Window.Shell.ToggleSplit();
        await Until(() => rig.Window.Shell.Right?.ActiveTab?.IsLoaded == true, () => "GUARD: the split never loaded");
        rig.Window.UpdateLayout();
        Settle();
        await Changes(rig, pane, VcsState.Added, "A", "split open");

        rig.Window.Shell.ToggleSplit();
        rig.Window.UpdateLayout();
        Settle();
        Assert.False(rig.Window.Shell.IsSplit);
        await Changes(rig, pane, VcsState.Untracked, "?", "split closed");

        pane.GroupBy = GroupMode.Kind;
        rig.Window.UpdateLayout();
        Settle();
        await Changes(rig, pane, VcsState.Conflicted, "!", "grouped");

        pane.GroupBy = GroupMode.None;
        rig.Window.UpdateLayout();
        Settle();
        await Changes(rig, pane, VcsState.Modified, "M", "ungrouped");
    }

    [AvaloniaFact]
    public async Task A_row_follows_it_after_scrolling_away_and_back()
    {
        using var rig = await BuildAsync(files: 400);

        var pane = rig.Pane;
        var list = rig.Window.GetVisualDescendants().OfType<TextBlock>()
                      .Where(t => RowVcs.GetEntry(t) is { } e && e.FullPath == rig.A)
                      .Select(t => t.FindAncestorOfType<ListBox>())
                      .First(l => l is not null)!;

        list.ScrollIntoView(pane.Rows.Count - 1);
        rig.Window.UpdateLayout();
        Settle();

        Assert.Empty(MarksOnA(rig));

        rig.Repo.Say(rig.A, VcsState.Added);
        pane.RefreshDecorations();

        await Until(() => pane.VcsStates.TryGetValue(rig.A, out var s) && s == VcsState.Added,
                    () => "GUARD: the pane never heard the new snapshot");

        list.ScrollIntoView(0);
        rig.Window.UpdateLayout();
        Settle();

        await Shows(rig, "A", "scrolled back, with a snapshot that landed while away");
        EveryRowOnScreenListensOnce(rig, "scrolled back");

        await Changes(rig, pane, VcsState.Untracked, "?", "after scrolling away and back");
    }
}
