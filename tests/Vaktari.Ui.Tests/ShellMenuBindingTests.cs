using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Handing the desktop's own menu to the interface.
///
/// The reading half is tested against the real shell in Vaktari.Windows.Tests,
/// where the entries come from whatever is installed. This is the other half:
/// what the view model does with them, which is where the lifetime rules live
/// and where getting it wrong is silent.
///
/// **The ids are offsets into one live menu.** Releasing it while the user is
/// still looking leaves every row pointing at nothing; never releasing leaks an
/// apartment thread per right-click. Both failures look like nothing at all
/// from the outside, which is why they are pinned here.
/// </summary>
public sealed class ShellMenuBindingTests : OwnedViewModels
{
    private sealed class FakeShellMenu : IShellMenu
    {
        public FakeShellMenu(IReadOnlyList<ShellMenuEntry> entries) => Entries = entries;

        public IReadOnlyList<ShellMenuEntry> Entries { get; }
        public bool Disposed { get; private set; }
        public List<int> Invoked { get; } = [];

        public void Invoke(int id) => Invoked.Add(id);
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A shell that answers when told to, because the interesting case is the
    /// one where it has not answered yet.
    ///
    /// **The real thing has no time limit**, so this must be able to model a
    /// build that is still running: with <see cref="Slow"/> set, the task it
    /// hands back stays pending until <see cref="Answer"/> is called, which is
    /// what a cold machine paging in handler DLLs looks like from here.
    /// </summary>
    private sealed class FakeProvider : IShellMenuProvider
    {
        private TaskCompletionSource<IShellMenu?>? _pending;

        public FakeShellMenu? Last { get; private set; }
        public int Builds { get; private set; }
        public IReadOnlyList<string> AskedFor { get; private set; } = [];

        /// <summary>
        /// Which of the shell's two questions the last build asked.
        ///
        /// **They are different menus, not one menu reached two ways** — a
        /// folder's own menu acts on it from outside, its background menu is
        /// what it offers as a place — so recording only the paths would let a
        /// pane ask the wrong one of them and still look right here.
        /// </summary>
        public bool AskedForBackground { get; private set; }

        /// <summary>Whether the build stays in flight until told otherwise.</summary>
        public bool Slow { get; set; }

        /// <summary>What a finished build hands back; null is the shell saying
        /// it has nothing for these paths.</summary>
        public bool OffersNothing { get; set; }

        public Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths)
            => Build(paths, background: false);

        public Task<IShellMenu?> BuildBackgroundAsync(string folder)
            => Build([folder], background: true);

        private Task<IShellMenu?> Build(IReadOnlyList<string> paths, bool background)
        {
            Builds++;
            AskedFor = paths;
            AskedForBackground = background;

            Last = new FakeShellMenu(
            [
                new ShellMenuEntry("Open", 1),
                new ShellMenuEntry("", 0, IsSeparator: true),
                new ShellMenuEntry("7-Zip", 2, Children:
                [
                    new ShellMenuEntry("Add to archive…", 3),
                ]),
                new ShellMenuEntry("Scan", 4, IsEnabled: false),
            ]);

            if (!Slow) return Task.FromResult(Answered());

            _pending = new TaskCompletionSource<IShellMenu?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            return _pending.Task;
        }

        /// <summary>Lets a slow build finish.</summary>
        public void Answer() => _pending!.SetResult(Answered());

        private IShellMenu? Answered() => OffersNothing ? null : Last;
    }

    private sealed class InertFileSystem : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private readonly FakeProvider _provider = new();

    public ShellMenuBindingTests() => PaneViewModel.ShellMenu = _provider;

    /// <summary>
    /// **Chained, because a pane that is never disposed keeps a watcher and two
    /// dispatcher timers running past the end of the test.** OwnedViewModels
    /// says what that costs and why the victim is always some later test; this
    /// class used to take the base class and then override its whole teardown
    /// away, which is the one way to get none of it.
    /// </summary>
    public override void Dispose()
    {
        PaneViewModel.ShellMenu = null;
        base.Dispose();
    }

    private PaneViewModel Pane() =>
        Own(new PaneViewModel(new InertFileSystem()) { CurrentPath = Path.GetTempPath() });

    [AvaloniaFact]
    public async Task Opening_it_fills_the_menu()
    {
        var pane = Pane();

        await pane.OpenShellMenuAsync();

        Assert.NotEmpty(pane.ShellMenuItems);
    }

    /// <summary>
    /// **A shell that has not answered yet is not a shell that offered
    /// nothing**, and the menu must not say the second while the first is true.
    ///
    /// This is the fault. Building used to be given four seconds and then
    /// answered as though the shell had nothing — reported from GitHub Actions,
    /// where the answer arrived after longer than that under load and the menu
    /// came back empty about one run in twenty. A cold machine got the same
    /// treatment, with no way to tell it from an empty menu.
    ///
    /// **Deliberately not awaited at the top.** What has to hold is that the
    /// call returns while the shell is still thinking — nothing is blocked, and
    /// the row on screen says so — which awaiting first would hide.
    ///
    /// The quarter-second is the wait itself. No test can outlast an arbitrary
    /// deadline, so what is pinned is that there is none: a deadline shorter
    /// than the hold turns these assertions red, and one of a millisecond was
    /// measured doing exactly that.
    /// </summary>
    [AvaloniaFact]
    public async Task A_shell_still_reading_is_not_a_shell_that_offered_nothing()
    {
        var pane = Pane();
        _provider.Slow = true;

        var opening = pane.OpenShellMenuAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.False(opening.IsCompleted, "the shell was given up on while it read");
        Assert.DoesNotContain(pane.ShellMenuItems,
            i => i is ShellMenuEntry { Label: "Nothing offered here" });
        Assert.Contains(pane.ShellMenuItems,
            i => i is ShellMenuEntry { Label: "Reading the shell…" });

        // However long that took, the answer is still wanted when it comes.
        _provider.Answer();
        await opening;

        Assert.Contains(pane.ShellMenuItems, i => i is ShellMenuEntry { Label: "7-Zip" });
    }

    /// <summary>
    /// The other half of the pair: once the shell has actually answered, and
    /// answered nothing, the menu says so rather than leaving the row that
    /// claims it is still reading.
    ///
    /// **A GUARD for the deadline, not a pin.** The old code said "Nothing
    /// offered here" when the shell answered nothing too, so this cannot go red
    /// for the fault it sits next to — measured: with the deadline back on the
    /// build, this passed and only
    /// <see cref="A_shell_still_reading_is_not_a_shell_that_offered_nothing"/>
    /// failed. What it does hold up is that the two rows stay distinct.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_offered_is_said_only_after_the_shell_has_answered()
    {
        var pane = Pane();
        _provider.Slow = true;
        _provider.OffersNothing = true;

        var opening = pane.OpenShellMenuAsync();
        _provider.Answer();
        await opening;

        Assert.Contains(pane.ShellMenuItems,
            i => i is ShellMenuEntry { Label: "Nothing offered here" });
        Assert.DoesNotContain(pane.ShellMenuItems,
            i => i is ShellMenuEntry { Label: "Reading the shell…" });
    }

    /// <summary>
    /// **The rows are never empty, not even for the instant between two
    /// statements** — which is not what "never empty" used to mean here.
    ///
    /// Both places that replace the rows used to clear the collection and then
    /// refill it, three lines under a comment saying an empty ItemsSource
    /// closes the submenu out from under the pointer. The end state was never
    /// empty; the collection was, once per close and once per rebuild, and that
    /// gap is a state readers land in. Avalonia's ItemsControl is one — it
    /// handles the Reset synchronously, which is the submenu closing itself out
    /// from under the pointer, the exact failure the placeholder exists to
    /// prevent. The test above was the other: reported failing on two
    /// whole-assembly runs out of sixteen with "Assert.Contains() Failure …
    /// Collection: []", which is that gap read from outside, on a collection
    /// whose end state was one row.
    ///
    /// **Deterministic here, where that was one run in eight.** The subscriber
    /// below lands in the gap by construction rather than by luck. Measured:
    /// restoring the clear-then-fill puts three Reset notifications carrying a
    /// count of zero into the list below, one for the close and two for the
    /// rebuild.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rows_are_never_empty_while_they_are_replaced()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();

        var emptied = new List<string>();

        ((System.Collections.Specialized.INotifyCollectionChanged)pane.ShellMenuItems)
            .CollectionChanged += (_, e) =>
            {
                if (pane.ShellMenuItems.Count == 0) emptied.Add(e.Action.ToString());
            };

        // The close, which is one of the two.
        pane.CloseShellMenu();

        // And the rebuild, which is the other.
        pane.CurrentPath = Path.Combine(Path.GetTempPath(), "elsewhere");
        await pane.OpenShellMenuAsync();

        Assert.Empty(emptied);
        Assert.Contains(pane.ShellMenuItems, i => i is ShellMenuEntry { Label: "7-Zip" });
    }

    /// <summary>
    /// The same, for the row that says the shell had nothing: it replaces the
    /// placeholder without the collection passing through empty either.
    /// </summary>
    [AvaloniaFact]
    public async Task An_empty_answer_replaces_the_placeholder_without_a_gap()
    {
        var pane = Pane();

        var emptied = new List<string>();

        ((System.Collections.Specialized.INotifyCollectionChanged)pane.ShellMenuItems)
            .CollectionChanged += (_, e) =>
            {
                if (pane.ShellMenuItems.Count == 0) emptied.Add(e.Action.ToString());
            };

        _provider.OffersNothing = true;

        await pane.OpenShellMenuAsync();

        Assert.Empty(emptied);
        Assert.Contains(pane.ShellMenuItems,
            i => i is ShellMenuEntry { Label: "Nothing offered here" });
    }

    /// <summary>
    /// **Separators arrive as real Separator controls.** Avalonia uses a Control
    /// in an ItemsSource as its own container, and that is the only way a
    /// data-driven menu draws a rule — the shell's menu leans on them heavily
    /// enough that dropping them turns twenty rows into one undifferentiated
    /// column.
    /// </summary>
    [AvaloniaFact]
    public async Task A_rule_becomes_something_the_menu_can_draw()
    {
        var pane = Pane();

        await pane.OpenShellMenuAsync();

        Assert.Contains(pane.ShellMenuItems, item => item is Separator);
        Assert.Contains(pane.ShellMenuItems, item => item is ShellMenuEntry { Label: "7-Zip" });
    }

    /// <summary>
    /// Building runs every shell extension on the machine, so it happens when
    /// the submenu opens and never as part of an ordinary right-click.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_is_built_until_the_submenu_opens()
    {
        var pane = Pane();

        // The placeholder is there from the start; the shell has not been asked.
        Assert.Single(pane.ShellMenuItems);
        Assert.Equal(0, _provider.Builds);

        await pane.OpenShellMenuAsync();

        Assert.Equal(1, _provider.Builds);
    }

    [AvaloniaFact]
    public async Task Closing_it_releases_the_shell_objects()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();
        var built = _provider.Last!;

        pane.CloseShellMenu();

        Assert.True(built.Disposed);

        // Back to the placeholder rather than empty: Avalonia refuses to open a
        // submenu with no items and draws no chevron for one, so an emptied
        // list would make "More options" unopenable ever again.
        Assert.Single(pane.ShellMenuItems);
        Assert.Contains(pane.ShellMenuItems,
            i => i is ShellMenuEntry { IsEnabled: false });
    }

    /// <summary>
    /// Opening for a different selection must not strand the one before: each
    /// menu owns an apartment thread, so a leak here is a thread per
    /// right-click.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_for_a_new_selection_releases_the_one_before()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();
        var first = _provider.Last!;

        pane.CurrentPath = Path.Combine(Path.GetTempPath(), "elsewhere");
        await pane.OpenShellMenuAsync();

        Assert.Equal(2, _provider.Builds);
        Assert.True(first.Disposed);
        Assert.False(_provider.Last!.Disposed);
    }

    /// <summary>
    /// **The submenu that blinked and never appeared.**
    ///
    /// SubmenuOpened bubbles, and the shell's menu nests — so opening 7-Zip's
    /// own submenu raised the event again on the way up, and handling it called
    /// straight back in here. The first thing a rebuild does is clear the
    /// collection the menu is drawn from, which destroyed the container of the
    /// popup that had just opened: it appeared and vanished in the same
    /// instant, for every extension that cascades.
    ///
    /// **Deliberately not awaited.** The damage was done by the synchronous
    /// half, before the shell was ever asked again; awaiting would let the
    /// rebuild finish and refill the list, and the assertion would pass against
    /// the bug. What has to hold is that the rows never go away at all.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_it_again_does_not_empty_the_menu_underneath_itself()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();

        var reopened = pane.OpenShellMenuAsync();

        Assert.Contains(pane.ShellMenuItems, i => i is ShellMenuEntry { Label: "7-Zip" });

        await reopened;

        // And the live menu is the one still on screen, not a replacement.
        Assert.Equal(1, _provider.Builds);
        Assert.False(_provider.Last!.Disposed);
        Assert.Contains(pane.ShellMenuItems, i => i is ShellMenuEntry { Label: "7-Zip" });
    }

    [AvaloniaFact]
    public async Task Choosing_an_entry_invokes_its_id()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();

        pane.InvokeShellEntry(new ShellMenuEntry("Open", 1));

        Assert.Equal([1], _provider.Last!.Invoked);
    }

    /// <summary>
    /// A parent row exists to open its children. Invoking it would ask the
    /// handler to run a command it never issued — and what a third party's
    /// handler does with an id it did not hand out is not knowable from here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_that_only_opens_a_submenu_invokes_nothing()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();

        pane.InvokeShellEntry(pane.ShellMenuItems
            .OfType<ShellMenuEntry>()
            .First(e => e.HasChildren));

        Assert.Empty(_provider.Last!.Invoked);
    }

    /// <summary>
    /// With nothing selected the click was on empty space, so the folder's
    /// BACKGROUND is what the shell should be asked for — the menu the folder
    /// offers about itself as a place.
    ///
    /// **This is the fault.** The pane asked for the folder's own menu, which
    /// is the one its row carries in the parent listing: the entries that act
    /// on the folder from outside — Pin to Quick access, Send to, Create
    /// shortcut — offered for a click that was on nothing. The two menus are
    /// separately bound in the shell and measurably different; the difference
    /// is pinned against the real shell in Vaktari.Windows.Tests, and what is
    /// pinned here is that the pane asks the right one.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_selection_the_folders_background_is_what_gets_asked_for()
    {
        var pane = Pane();

        await pane.OpenShellMenuAsync();

        Assert.True(_provider.AskedForBackground, "the folder's own menu was asked for");
        Assert.Equal([Path.GetTempPath()], _provider.AskedFor);
    }

    /// <summary>
    /// The other half of the same choice: a click that landed on a row wants
    /// that row's own menu. A background is a question about a place you are
    /// inside, and a selected file is not one, so this half of the branch has
    /// to be pinned too — a pane that asked for a background whatever was
    /// selected would still pass the test above.
    /// </summary>
    [AvaloniaFact]
    public async Task With_a_selection_the_selections_own_menu_is_what_gets_asked_for()
    {
        var pane = Pane();
        var file = Path.Combine(Path.GetTempPath(), "notes.txt");

        pane.SelectedEntry = new FileEntry(
            "notes.txt", file, 0, DateTimeOffset.UnixEpoch, EntryFlags.None);

        await pane.OpenShellMenuAsync();

        Assert.False(_provider.AskedForBackground, "a selected file was treated as empty space");
        Assert.Equal([file], _provider.AskedFor);
    }

    /// <summary>The bin and Recent hold rows whose paths are not where the file
    /// is now, which is the hazard the whole listing already guards.</summary>
    [AvaloniaFact]
    public async Task The_bin_and_recent_offer_no_shell_menu()
    {
        Assert.False(Own(new PaneViewModel(new InertFileSystem())
            { CurrentPath = VirtualPaths.Trash }).HasShellMenu);

        Assert.False(Own(new PaneViewModel(new InertFileSystem())
            { CurrentPath = VirtualPaths.Files }).HasShellMenu);

        Assert.True(Pane().HasShellMenu);
    }
}
