using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// How a folder opens when nobody has told this tab anything.
///
/// **There was no such thing as a default layout.** PaneViewModel's fields said
/// <c>ViewMode.Details</c>, <c>SortField.Name</c> and <c>GroupMode.None</c> as
/// literals, and settings.json had no key that could say otherwise — so
/// somebody who works in the large grid got Details from the tab strip's "+",
/// from a new window, from the second half of every split and from the first
/// tab of a fresh install, every time. The session remembers the tabs that were
/// open and the per-folder store answers for folders already given a layout;
/// nothing anywhere answered "how should a folder I have not arranged look".
///
/// **And the "+" did not even carry the view across.** Ctrl+T, Duplicate and
/// the middle-click that opens a folder behind you all pass <c>like</c> to
/// AddTab; the button beside the tab strip omitted it, so the same gesture kept
/// or dropped five settings depending on which of the two you reached for.
///
/// So: four defaults, read where the pane is built, and one command that puts
/// the pane you are looking at into them. The command forgets the per-folder
/// overrides as it goes, because "all folders" that skips the folders you have
/// already arranged is not the promise the words make.
/// </summary>
public sealed class DefaultViewTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private readonly IFolderViewStore? _viewsBefore = PaneViewModel.FolderViews;

    public override void Dispose()
    {
        // Both are process-wide statics, and a pane reads the first of them at
        // construction — so a test that left either changed would decide the
        // starting layout of every pane built after it.
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        PaneViewModel.FolderViews = _viewsBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Inert : IFileSystemProvider
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

    /// <summary>
    /// A folder-view store that holds what it is given, so a test can see
    /// whether the command emptied it.
    ///
    /// **Keyed through PathRules.Normalise, because the shipped store is.**
    /// JsonFolderViewStore normalises on Read, Write and Forget alike, and a
    /// plain ordinal dictionary here does not: RestoreFrom records the raw
    /// <c>tab.Path</c> — <c>Path.GetTempPath()</c> ends in a separator — while
    /// an arrival looks the folder up after LoadListingAsync has normalised it.
    /// Measured: without this the two never meet, so a test asking whether an
    /// override reaches a later arrival passed for a reason that has nothing to
    /// do with the code under test.
    /// </summary>
    private sealed class Remembering : IFolderViewStore
    {
        private readonly Dictionary<string, FolderViewState> _views = new(StringComparer.Ordinal);

        public FolderViewState? Read(string path)
            => _views.TryGetValue(PathRules.Normalise(path), out var view) ? view : null;

        public void Write(string path, FolderViewState state)
            => _views[PathRules.Normalise(path)] = state;

        public void Forget(string path) => _views.Remove(PathRules.Normalise(path));

        public int Remembered => _views.Count;

        public int ForgetAll()
        {
            var had = _views.Count;
            _views.Clear();
            return had;
        }
    }

    private static void Prefer(ViewSettings views)
        => Vaktari.Ui.Settings.AppSettings.Apply(new SettingsState { Views = views });

    /// <summary>Started, so Left holds one tab that has been through the same
    /// construction path every other tab takes.</summary>
    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    // ---- the default itself -------------------------------------------------

    /// <summary>
    /// **The upgrade case, and the reason all four are named for their zero
    /// value.** A settings.json written before these keys existed has none of
    /// them, and this codebase has measured that deserialization does not run
    /// property initializers — so whatever `default(T)` is, is what every
    /// existing install gets. Details, Name, ascending and ungrouped is exactly
    /// what it had before, so nothing moves under anybody.
    /// </summary>
    [AvaloniaFact]
    public void A_settings_file_that_says_nothing_opens_a_folder_the_way_it_always_did()
    {
        Prefer(new ViewSettings());

        var pane = Shell().Left.Tabs[0];

        Assert.Equal(ViewMode.Details, pane.View);
        Assert.Equal(SortField.Name, pane.Sort);
        Assert.False(pane.SortDescending);
        Assert.Equal(GroupMode.None, pane.GroupBy);
    }

    /// <summary>
    /// And a file that has never heard of the keys is such a file. The half
    /// above builds a fresh record, which DOES run the initializers; this one
    /// is the upgrade path itself, where they are skipped and only the enum's
    /// own zero decides. Measured here for the same reason
    /// <see cref="SelectionBoxTests"/> measures it: the paragraph in
    /// SettingsModel.cs claims exactly this, and a claim in a comment is not a
    /// measurement.
    /// </summary>
    [Fact]
    public void And_a_file_written_before_those_keys_existed_is_such_a_file()
    {
        var older = System.Text.Json.JsonSerializer.Deserialize(
            "{\"version\":1,\"views\":{\"themeMode\":\"FollowDesktop\"}}",
            SettingsJsonContext.Default.SettingsState);

        Assert.NotNull(older);
        Assert.Equal(ViewMode.Details, older!.Views.DefaultView);
        Assert.Equal(SortField.Name, older.Views.DefaultSort);
        Assert.False(older.Views.DefaultSortDescending);
        Assert.Equal(GroupMode.None, older.Views.DefaultGroupBy);
    }

    [AvaloniaFact]
    public void A_pane_starts_in_the_layout_the_settings_name()
    {
        Prefer(new ViewSettings { DefaultView = ViewMode.Grid });

        Assert.Equal(ViewMode.Grid, Shell().Left.Tabs[0].View);
    }

    [AvaloniaFact]
    public void And_in_the_order_they_name()
    {
        Prefer(new ViewSettings { DefaultSort = SortField.Modified, DefaultSortDescending = true });

        var pane = Shell().Left.Tabs[0];

        Assert.Equal(SortField.Modified, pane.Sort);
        Assert.True(pane.SortDescending);
    }

    [AvaloniaFact]
    public void And_with_the_bands_they_name()
    {
        Prefer(new ViewSettings { DefaultGroupBy = GroupMode.Kind });

        Assert.Equal(GroupMode.Kind, Shell().Left.Tabs[0].GroupBy);
    }

    /// <summary>
    /// **A default must not overrule a tab that was saved.** The pane reads the
    /// preference where its fields are initialised, which is before RestoreFrom
    /// runs — so a session tab that was left in Details comes back in Details
    /// however the default is set. Reversing those two would silently rewrite
    /// everybody's restored session the first time they chose a default.
    /// </summary>
    [AvaloniaFact]
    public void A_restored_tab_keeps_the_view_it_was_saved_with()
    {
        Prefer(new ViewSettings { DefaultView = ViewMode.Grid });

        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes =
                    [
                        new PaneState
                        {
                            Tabs = [new TabState { Path = Path.GetTempPath(), View = ViewMode.Details }],
                        },
                    ],
                },
            ],
        });

        Assert.Equal(ViewMode.Details, shell.Left.Tabs[0].View);
    }

    /// <summary>
    /// **And it must not pin that folder to the view it was saved with.**
    /// RestoreFrom assigns CurrentPath before it assigns View, Sort and
    /// GroupBy, and those three setters call RememberFolderView unconditionally
    /// — so once the pane starts in something other than Details, restoring a
    /// Details tab was a real property change and wrote the folder an override
    /// nobody asked for. Measured here before the fix: this store held one
    /// entry, and the fresh tab below then came up Details, so the folders you
    /// had open when you chose a default were the folders the default could
    /// never reach.
    /// </summary>
    [AvaloniaFact]
    public void And_it_does_not_leave_that_folder_an_opinion_it_never_had()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(new SettingsState
        {
            General = new GeneralSettings { RememberViewPerFolder = true },
            Views = new ViewSettings { DefaultView = ViewMode.Grid },
        });

        var store = new Remembering();
        PaneViewModel.FolderViews = store;

        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes =
                    [
                        new PaneState
                        {
                            Tabs = [new TabState { Path = Path.GetTempPath(), View = ViewMode.Details }],
                        },
                    ],
                },
            ],
        });

        Assert.Equal(0, store.Remembered);

        // And nothing was pinned, so the default really does reach that folder
        // on the next arrival — the half the count alone cannot show.
        var fresh = shell.Left.AddTab(Path.GetTempPath());
        Assert.Equal(ViewMode.Grid, fresh.View);
    }

    // ---- the "+" that reset it ----------------------------------------------

    /// <summary>
    /// The default is Details here on purpose: the new tab is Grid only because
    /// it copied the tab it was opened from, so this cannot pass by the default
    /// happening to agree.
    /// </summary>
    [AvaloniaFact]
    public void The_plus_beside_the_tab_strip_carries_the_view_across()
    {
        Prefer(new ViewSettings());

        var group = Shell().Left;

        group.ActiveTab!.View = ViewMode.Grid;
        group.ActiveTab.GroupBy = GroupMode.Kind;
        group.ActiveTab.ShowHidden = true;

        group.NewTabHereCommand.Execute(null);

        Assert.Equal(2, group.Tabs.Count);
        Assert.Equal(ViewMode.Grid, group.Tabs[1].View);
        Assert.Equal(GroupMode.Kind, group.Tabs[1].GroupBy);
        Assert.True(group.Tabs[1].ShowHidden);
    }

    // ---- "use this view for all folders" ------------------------------------

    /// <summary>
    /// **A second tab, arranged differently, is the whole point of the name.**
    /// With one tab per shell, "the pane you are looking at" and Tabs[0] are the
    /// same object, so reading the wrong one passed — measured: replacing the
    /// command's <c>ActiveTab</c> with <c>Left.Tabs[0]</c> compiled and left
    /// every test in this class green. The first tab here is deliberately given
    /// a different layout, sort, direction and grouping from the active one, so
    /// all four assertions read the pane in front of you rather than the first
    /// one opened.
    /// </summary>
    [AvaloniaFact]
    public void The_pane_you_are_looking_at_becomes_the_default()
    {
        Prefer(new ViewSettings());

        var shell = Shell();

        var first = shell.Left.Tabs[0];
        first.View = ViewMode.Grid;
        first.Sort = SortField.Kind;
        first.SortDescending = false;
        first.GroupBy = GroupMode.None;

        // AddTab activates what it opens, so this is now the pane in front.
        var pane = shell.Left.AddTab(Path.GetTempPath());
        Assert.Same(pane, shell.ActiveTab);

        pane.View = ViewMode.Compact;
        pane.Sort = SortField.Size;
        pane.SortDescending = true;
        pane.GroupBy = GroupMode.Kind;

        shell.UseThisViewEverywhereCommand.Execute(null);

        var views = Vaktari.Ui.Settings.AppSettings.Current.Views;

        Assert.Equal(ViewMode.Compact, views.DefaultView);
        Assert.Equal(SortField.Size, views.DefaultSort);
        Assert.True(views.DefaultSortDescending);
        Assert.Equal(GroupMode.Kind, views.DefaultGroupBy);
    }

    /// <summary>
    /// **And it says so.** Pressing it moved nothing on screen: the open tabs
    /// are left alone on purpose, the flyout holds no layout control to
    /// re-render, and the effect is entirely on folders opened later — so the
    /// button read as broken while emptying the per-folder store. Both halves
    /// are reported, the way the settings dialog's "Forget remembered views"
    /// reports the same forget.
    /// </summary>
    [AvaloniaFact]
    public void And_it_says_what_it_did()
    {
        Prefer(new ViewSettings());

        var store = new Remembering();
        store.Write(@"C:\one", new FolderViewState { View = ViewMode.Grid });
        store.Write(@"C:\two", new FolderViewState { View = ViewMode.Compact });

        PaneViewModel.FolderViews = store;

        var shell = Shell();
        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Compact;
        shell.UseThisViewEverywhereCommand.Execute(null);

        // The layouts are named the way the toolbar chip names them, not the
        // way the enum spells them.
        Assert.Contains("small grid", pane.Status, StringComparison.Ordinal);
        Assert.Contains("2 folders", pane.Status, StringComparison.Ordinal);

        pane.View = ViewMode.Grid;
        shell.UseThisViewEverywhereCommand.Execute(null);

        Assert.Contains("large grid", pane.Status, StringComparison.Ordinal);

        // Nothing left to forget, so nothing is claimed about forgetting.
        Assert.DoesNotContain("forgotten", pane.Status, StringComparison.Ordinal);

        pane.View = ViewMode.Details;
        store.Write(@"C:\three", new FolderViewState { View = ViewMode.Grid });
        shell.UseThisViewEverywhereCommand.Execute(null);

        Assert.Contains("a list", pane.Status, StringComparison.Ordinal);
        Assert.Contains("one folder", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// Applied rather than only saved: a pane reads these when it is
    /// constructed, so the next tab opened is where the choice shows. A
    /// preference that reached only the file would need a restart, which is the
    /// trap MainWindow's own save handler carries several notes about.
    /// </summary>
    [AvaloniaFact]
    public void And_the_next_tab_opens_in_it()
    {
        Prefer(new ViewSettings());

        var shell = Shell();

        shell.ActiveTab!.View = ViewMode.Compact;
        shell.UseThisViewEverywhereCommand.Execute(null);

        // No `like`: this is the route a default has to answer, the way a
        // restored window or a folder handed over by the desktop arrives.
        var fresh = shell.Left.AddTab(Path.GetTempPath());

        Assert.Equal(ViewMode.Compact, fresh.View);
    }

    /// <summary>
    /// **The half that makes "all folders" true.** A folder that had been given
    /// a layout of its own would go on overriding the brand-new default, so the
    /// setting would appear to work everywhere except in the folders somebody
    /// asking this question has been arranging.
    /// </summary>
    [AvaloniaFact]
    public void It_forgets_the_folders_that_had_a_view_of_their_own()
    {
        Prefer(new ViewSettings());

        var store = new Remembering();
        store.Write(@"C:\one", new FolderViewState { View = ViewMode.Grid });
        store.Write(@"C:\two", new FolderViewState { View = ViewMode.Compact });

        PaneViewModel.FolderViews = store;

        var shell = Shell();
        shell.ActiveTab!.View = ViewMode.Grid;

        shell.UseThisViewEverywhereCommand.Execute(null);

        Assert.Equal(0, store.Remembered);
    }

    /// <summary>
    /// The shell has no settings store; the window does. It is handed the state
    /// that is already in force, so the file and the running application cannot
    /// disagree about what was chosen.
    /// </summary>
    [AvaloniaFact]
    public void The_window_is_handed_the_state_to_write()
    {
        Prefer(new ViewSettings());

        var shell = Shell();
        SettingsState? handed = null;

        shell.DefaultViewChanged += (_, state) => handed = state;

        shell.ActiveTab!.View = ViewMode.Grid;
        shell.UseThisViewEverywhereCommand.Execute(null);

        Assert.NotNull(handed);
        Assert.Equal(ViewMode.Grid, handed!.Views.DefaultView);
    }

    /// <summary>
    /// And the window does write it, through the same store the settings dialog
    /// saves with. Read out of the source because that is where the gap would
    /// be: every assertion above passes with nothing subscribed at all, and the
    /// choice would then last exactly as long as the process.
    /// </summary>
    [AvaloniaFact]
    public void The_window_writes_it_to_the_settings_file()
        => Assert.Contains(
            "_shell.DefaultViewChanged += (_, settings) => _services.SettingsStore.Save(settings);",
            RepoSource.Ui("MainWindow.axaml.cs"));

    /// <summary>
    /// And there is a way to press it. A command nothing binds is a feature
    /// nobody can reach — the same gap the settings dialog's "Forget remembered
    /// views" button was added to close.
    /// </summary>
    [AvaloniaFact]
    public void The_view_options_flyout_offers_it()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));
        var ns = markup.Root!.GetDefaultNamespace();

        var button = markup.Descendants(ns + "Button").SingleOrDefault(
            b => ((string?)b.Attribute("Command"))?.Contains(
                     "UseThisViewEverywhereCommand", StringComparison.Ordinal) == true);

        Assert.NotNull(button);
        Assert.Equal("Use this view for all folders", (string?)button!.Attribute("Content"));
    }
}
