using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What "remember the view for each folder" actually remembers.
///
/// **It remembered three of the six things the view options menu offers.**
/// <see cref="FolderViewState"/> carried the layout, the sort, its direction,
/// the grouping and a pair of scales — and nothing else. Hidden files and the
/// four column ticks were not fields on the record, so the two hooks that could
/// have written them had nowhere to put them and the arrival that re-applies a
/// folder's view had nothing to apply.
///
/// **And the scales were the worse half, because they WERE re-applied.**
/// RememberFolderView was reached only from the View, Sort, SortDescending and
/// GroupBy hooks, so a folder's scale was snapshotted as a side effect of the
/// last layout change and then pinned by ApplyFolderView on every arrival.
/// Ctrl+wheel held while you stayed in the folder and was undone by leaving it
/// — a loop where the application quietly reverses something you just did.
///
/// The third strand here is the value that means "I did not say": the record's
/// fields were all non-nullable, so one key in a Dolphin <c>.directory</c>
/// produced an opinion about every one of them.
/// </summary>
public sealed class FolderViewMemoryTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly IFolderViewStore? _viewsBefore = PaneViewModel.FolderViews;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-folder-memory-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly Recording _store = new();

    public FolderViewMemoryTests()
    {
        Directory.CreateDirectory(Here);
        Directory.CreateDirectory(Elsewhere);

        // Both are process-wide statics and a pane reads the first at
        // construction, so a test that left either changed would decide the
        // starting layout of every pane built after it.
        AppSettings.Apply(new SettingsState
        {
            General = new GeneralSettings { RememberViewPerFolder = true },
        });

        PaneViewModel.FolderViews = _store;
    }

    public override void Dispose()
    {
        AppSettings.Apply(_settingsBefore);
        PaneViewModel.FolderViews = _viewsBefore;

        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Here => Path.Combine(_root, "here");
    private string Elsewhere => Path.Combine(_root, "elsewhere");

    /// <summary>
    /// Holds what it is given and keeps every state it was handed, in order, so
    /// a test can ask what reached the store and not only what survived.
    ///
    /// **Keyed through PathRules.Normalise, because the shipped store is.**
    /// JsonFolderViewStore normalises on Read, Write and Forget alike; a plain
    /// ordinal dictionary does not, and an arrival looks the folder up after
    /// LoadListingAsync has normalised it.
    /// </summary>
    private sealed class Recording : IFolderViewStore
    {
        private readonly Dictionary<string, FolderViewState> _views = new(StringComparer.Ordinal);

        internal List<FolderViewState> Written { get; } = [];

        public FolderViewState? Read(string path)
            => _views.TryGetValue(PathRules.Normalise(path), out var view) ? view : null;

        public void Write(string path, FolderViewState state)
        {
            _views[PathRules.Normalise(path)] = state;
            Written.Add(state);
        }

        public void Forget(string path) => _views.Remove(PathRules.Normalise(path));

        public int Remembered => _views.Count;

        public int ForgetAll()
        {
            var had = _views.Count;
            _views.Clear();
            return had;
        }
    }

    private PaneViewModel Pane()
        => Own(new PaneViewModel(new Listing(), null, null) { ViewportWidth = 1400 });

    private FolderViewState Stored(string path)
    {
        var view = _store.Read(path);
        Assert.NotNull(view);
        return view!;
    }

    // ---- hidden files -------------------------------------------------------

    /// <summary>
    /// **Ctrl+H was one of the five view changes a folder could not keep**, the
    /// four column ticks being the others. The whole of OnShowHiddenChanged was
    /// the reload it asks for, so showing the dotfiles in a source tree lasted
    /// exactly as long as you stayed in it.
    /// </summary>
    [AvaloniaFact]
    public async Task Showing_hidden_files_is_recorded_against_the_folder()
    {
        var pane = Pane();
        await pane.NavigateAsync(Here);

        pane.ToggleHiddenCommand.Execute(null);

        Assert.True(pane.ShowHidden);
        Assert.True(Stored(Here).ShowHidden);
    }

    /// <summary>And restored, which is the half that shows on screen.</summary>
    [AvaloniaFact]
    public async Task And_a_folder_left_showing_them_comes_back_showing_them()
    {
        _store.Write(Here, new FolderViewState { ShowHidden = true });

        var pane = Pane();
        Assert.False(pane.ShowHidden);

        await pane.NavigateAsync(Here);

        Assert.True(pane.ShowHidden);
    }

    /// <summary>
    /// And the folder that said nothing about them does not decide either way.
    /// ApplyFolderView runs before the listing is asked for, so a folder that
    /// turned hidden files off here would enumerate without them.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_with_no_opinion_leaves_hidden_files_as_they_were()
    {
        // No CurrentPath yet, so this cannot write the store: the pane's own
        // state is being set up, not the folder's.
        var pane = Pane();
        pane.ShowHidden = true;

        _store.Write(Here, new FolderViewState { View = ViewMode.Grid });

        await pane.NavigateAsync(Here);

        // It really did read that entry...
        Assert.Equal(ViewMode.Grid, pane.View);

        // ...and left alone the field the entry says nothing about.
        Assert.True(pane.ShowHidden);
    }

    /// <summary>
    /// The same silence on the four axes that were already in the record, from
    /// an entry this application wrote rather than from a Dolphin file.
    ///
    /// **All four were non-nullable, so every entry had an answer for all of
    /// them.** That did not show through the application's own writer, which
    /// fills all four every time — it showed on arrival at a folder whose entry
    /// came from anywhere else. Each of the three the entry says nothing about
    /// is set away from the record's old default first, so none can pass by
    /// agreeing with what that default would have imposed.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_named_only_a_layout_leaves_the_rest_alone()
    {
        var pane = Pane();
        pane.Sort = SortField.Size;
        pane.SortDescending = true;
        pane.GroupBy = GroupMode.Kind;

        _store.Write(Here, new FolderViewState { View = ViewMode.Grid });

        await pane.NavigateAsync(Here);

        Assert.Equal(ViewMode.Grid, pane.View);

        Assert.Equal(SortField.Size, pane.Sort);
        Assert.True(pane.SortDescending);
        Assert.Equal(GroupMode.Kind, pane.GroupBy);
    }

    /// <summary>
    /// **A reveal turns hidden files on before it navigates**, so the pane is
    /// still standing in the folder it is LEAVING when that happens — and the
    /// record written there is the whole pane, layout and sort and columns and
    /// scales included. Measured before the guard: a pane in a folder the store
    /// had never heard of came out of a single "show in folder" with a full
    /// FolderViewState against that folder, on the strength of a click aimed
    /// somewhere else.
    /// </summary>
    [AvaloniaFact]
    public async Task A_reveal_gives_the_folder_it_leaves_no_opinion_it_never_had()
    {
        File.WriteAllText(Path.Combine(Here, ".secret"), "x");

        var pane = Pane();
        await pane.NavigateAsync(Elsewhere);

        // The arrival above records nothing; this only says so out loud.
        _store.ForgetAll();

        await pane.ShowAsync(Here, [Path.Combine(Here, ".secret")]);

        Assert.Null(_store.Read(Elsewhere));
    }

    /// <summary>
    /// And the folder it lands in must not undo it before the listing is built.
    ///
    /// **ApplyFolderView runs inside that navigation**, ahead of the
    /// enumeration, so a destination recorded as "hide them" put the item back
    /// out of sight on the way in: measured, the listing came back empty and
    /// "Show in folder" on a dotfile reported it was no longer there while
    /// sitting in the folder that holds it. Asserted on the row rather than on
    /// ShowHidden, because the row is the thing that was missing.
    ///
    /// The plain arrival first is not scene-setting: it is what proves the
    /// folder really does conceal the file, so the reveal below cannot pass by
    /// the listing having held it all along.
    /// </summary>
    [AvaloniaFact]
    public async Task And_the_folder_it_lands_in_does_not_undo_it()
    {
        var secret = Path.Combine(Here, ".secret");
        File.WriteAllText(secret, "x");

        _store.Write(Here, new FolderViewState { ShowHidden = false });

        var pane = Pane();
        await pane.NavigateAsync(Here);

        Assert.DoesNotContain(pane.Entries, row => PathRules.Same(row.FullPath, secret));

        await pane.NavigateAsync(Elsewhere);

        await pane.ShowAsync(Here, [secret]);

        Assert.Contains(pane.Entries, row => PathRules.Same(row.FullPath, secret));
        Assert.Contains(pane.SelectedEntries, row => PathRules.Same(row.FullPath, secret));
    }

    /// <summary>
    /// And the reveal ends when it lands, so the next folder's own answer is
    /// heard again.
    ///
    /// **A flag left set would have concealed a whole class of records for the
    /// rest of the session** — every folder that had been told to hide them
    /// would have been ignored, which is the same silence the flag exists to
    /// break, pointing the other way.
    /// </summary>
    [AvaloniaFact]
    public async Task And_the_reveal_ends_when_it_lands()
    {
        var secret = Path.Combine(Here, ".secret");
        File.WriteAllText(secret, "x");

        _store.Write(Elsewhere, new FolderViewState { ShowHidden = false });

        var pane = Pane();
        await pane.NavigateAsync(Elsewhere);

        await pane.ShowAsync(Here, [secret]);
        Assert.True(pane.ShowHidden);

        await pane.NavigateAsync(Elsewhere);

        Assert.False(pane.ShowHidden);
    }

    // ---- the four column ticks ---------------------------------------------

    /// <summary>
    /// Each tick, on its own, against a store emptied in between — so this is
    /// red if ANY ONE of the four hooks stops recording, rather than passing on
    /// whichever happened to be toggled last.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_column_tick_is_recorded_against_the_folder()
    {
        var pane = Pane();
        await pane.NavigateAsync(Here);

        pane.ToggleSizeColumnCommand.Execute(null);
        Assert.True(Stored(Here).Columns!.HideSize, "the size tick was not recorded");
        _store.ForgetAll();

        pane.ToggleModifiedColumnCommand.Execute(null);
        Assert.True(Stored(Here).Columns!.HideModified, "the modified tick was not recorded");
        _store.ForgetAll();

        pane.ToggleTypeColumnCommand.Execute(null);
        Assert.True(Stored(Here).Columns!.ShowType, "the type tick was not recorded");
        _store.ForgetAll();

        pane.ToggleCreatedColumnCommand.Execute(null);
        Assert.True(Stored(Here).Columns!.ShowCreated, "the created tick was not recorded");
    }

    /// <summary>
    /// And restored. The four are deliberately not all the same value: three
    /// trues and a false is the arrangement a copy-paste slip between the four
    /// assignments cannot satisfy.
    ///
    /// **And the pane starts at the opposite of every one of them**, so no
    /// assertion here can be satisfied by the assignment simply not happening.
    /// Measured without that: the modified tick was the entry's one false, the
    /// pane's default was false too, and deleting its assignment outright left
    /// this test green.
    /// </summary>
    [AvaloniaFact]
    public async Task And_a_folder_comes_back_with_the_columns_it_was_given()
    {
        _store.Write(Here, new FolderViewState
        {
            Columns = new FolderColumns
            {
                HideSize = true,
                HideModified = false,
                ShowType = true,
                ShowCreated = true,
            },
        });

        var pane = Pane();
        pane.HideSizeColumn = false;
        pane.HideModifiedColumn = true;
        pane.ShowTypeColumn = false;
        pane.ShowCreatedColumn = false;

        await pane.NavigateAsync(Here);

        Assert.True(pane.HideSizeColumn);
        Assert.False(pane.HideModifiedColumn);
        Assert.True(pane.ShowTypeColumn);
        Assert.True(pane.ShowCreatedColumn);
    }

    /// <summary>
    /// A folder that never chose columns must not blank the ones the pane is
    /// showing.
    ///
    /// **All four are moved off their defaults first**, because false is what
    /// three of them start at AND what a <c>?? false</c> would produce: with
    /// only the type tick set, breaking the size, modified or created branch
    /// stayed green — measured, <c>?? HideModifiedColumn</c> replaced by
    /// <c>?? false</c> left this whole class passing. One tick per branch is
    /// what makes each of the four its own mutation.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_with_no_opinion_leaves_the_columns_as_they_were()
    {
        var pane = Pane();
        pane.HideSizeColumn = true;
        pane.HideModifiedColumn = true;
        pane.ShowTypeColumn = true;
        pane.ShowCreatedColumn = true;

        _store.Write(Here, new FolderViewState { View = ViewMode.Grid });

        await pane.NavigateAsync(Here);

        Assert.Equal(ViewMode.Grid, pane.View);

        Assert.True(pane.HideSizeColumn, "the size tick was blanked");
        Assert.True(pane.HideModifiedColumn, "the modified tick was blanked");
        Assert.True(pane.ShowTypeColumn, "the type tick was blanked");
        Assert.True(pane.ShowCreatedColumn, "the created tick was blanked");
    }

    // ---- the zoom that was undone by leaving --------------------------------

    /// <summary>
    /// **The reversion loop, end to end.** The folder is given an entry the old
    /// way — one layout change — which snapshots the scale it had at that
    /// moment. Then it is zoomed, left and returned to. Before this change the
    /// pane came back at the snapshot, so the zoom you had just applied was
    /// undone by walking to the parent and back.
    /// </summary>
    [AvaloniaFact]
    public async Task A_zoom_survives_leaving_the_folder_and_coming_back()
    {
        var pane = Pane();
        await pane.NavigateAsync(Here);

        // The only thing that used to record a folder at all.
        pane.View = ViewMode.Grid;

        pane.FontScale = 1.3;
        pane.IconScale = 1.4;

        await pane.NavigateAsync(Elsewhere);
        await pane.NavigateAsync(Here);

        Assert.Equal(1.3, pane.FontScale);
        Assert.Equal(1.4, pane.IconScale);
    }

    /// <summary>
    /// Each axis on its own, against a store emptied in between, because the
    /// wheel and the two typed boxes move them independently and a fix applied
    /// to one hook only would pass a test that moves both.
    /// </summary>
    [AvaloniaFact]
    public async Task Either_axis_alone_records_the_folder()
    {
        var pane = Pane();
        await pane.NavigateAsync(Here);

        pane.FontScale = 1.3;
        Assert.Equal(1.3, Stored(Here).FontScale);
        _store.ForgetAll();

        pane.IconScale = 1.4;
        Assert.Equal(1.4, Stored(Here).IconScale);
    }

    /// <summary>
    /// **A mode switch assigns the two scales one at a time**, from
    /// `_scales[incoming]`, so between the two assignments the pane holds the
    /// incoming layout's font beside the outgoing layout's icon — a pair that
    /// was never on screen. Both layouts here are square (font and icon equal),
    /// so any recorded state whose two scales differ is that torn instant and
    /// nothing else. OnViewChanged records the finished pair itself.
    /// </summary>
    [AvaloniaFact]
    public async Task A_layout_switch_never_records_a_pair_that_was_never_on_screen()
    {
        var pane = Pane();
        await pane.NavigateAsync(Here);

        pane.View = ViewMode.Grid;
        pane.FontScale = 1.5;
        pane.IconScale = 1.5;

        pane.View = ViewMode.Details;

        // Everything above is setup; only the switch below is under test.
        _store.Written.Clear();

        pane.View = ViewMode.Grid;

        Assert.NotEmpty(_store.Written);
        Assert.All(_store.Written, state => Assert.Equal(state.FontScale, state.IconScale));
    }

    /// <summary>
    /// And it records the folder exactly once, which is what pins the SECOND
    /// half of that guard.
    ///
    /// **The icon hook's `_swappingScales` check had no test of its own.**
    /// SwapScales assigns the font first and the icon second, so an unguarded
    /// write on the icon assignment always sees a pair that agrees and the
    /// torn-pair test above cannot see it — measured: dropping the check from
    /// OnIconScaleChanged left all seventeen tests in this class green. What it
    /// does change is the count: the swap writes, and then OnViewChanged writes
    /// the finished state again. Two disk-bound records for one keystroke, in a
    /// store whose own header says it is debounced because a sort is a
    /// keystroke-frequency action.
    /// </summary>
    [AvaloniaFact]
    public async Task A_layout_switch_records_the_folder_once()
    {
        var pane = Pane();
        await pane.NavigateAsync(Here);

        pane.View = ViewMode.Grid;
        pane.FontScale = 1.5;
        pane.IconScale = 1.5;

        pane.View = ViewMode.Details;

        // Everything above is setup; only the switch below is under test.
        _store.Written.Clear();

        pane.View = ViewMode.Grid;

        Assert.Single(_store.Written);
    }

    // ---- when nothing should be written -------------------------------------

    /// <summary>
    /// With the preference off, none of the new hooks may record anything —
    /// the store fills itself by merely being looked at, and that is exactly
    /// what the setting exists to refuse.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_is_recorded_when_the_preference_is_off()
    {
        AppSettings.Apply(new SettingsState());

        var pane = Pane();
        await pane.NavigateAsync(Here);

        pane.ToggleHiddenCommand.Execute(null);
        pane.ToggleTypeColumnCommand.Execute(null);
        pane.FontScale = 1.3;
        pane.IconScale = 1.4;

        Assert.Equal(0, _store.Remembered);
    }

    /// <summary>
    /// And restoring a session must not either. RestoreFrom replays a saved tab
    /// field by field, and every one of those assignments is a real property
    /// change that now reaches a recording hook — so without `_restoringView`
    /// the folders somebody had open would each be handed an override they
    /// never asked for, which is the trap
    /// <see cref="DefaultViewTests.And_it_does_not_leave_that_folder_an_opinion_it_never_had"/>
    /// was written for on the layout side.
    /// </summary>
    [AvaloniaFact]
    public void Restoring_a_session_gives_a_folder_no_opinion_it_never_had()
    {
        var pane = Pane();

        pane.RestoreFrom(new TabState
        {
            Path = Here,
            ShowHidden = true,
            ShowType = true,
            ShowCreated = true,
            HideSize = true,
            FontScale = 1.3,
            IconScale = 1.4,
        });

        Assert.Equal(0, _store.Remembered);
    }

    // ---- what a Dolphin .directory is allowed to say ------------------------

    /// <summary>
    /// **One key used to mean an opinion about all of them.** The record's
    /// fields were non-nullable, so a `.directory` naming nothing but a sort
    /// role came back carrying Details and ungrouped as well, and arriving in
    /// that folder pulled a pane out of the grid it was in.
    /// </summary>
    [AvaloniaFact]
    public void A_dot_directory_says_only_what_it_says()
    {
        Write(Here, "[Dolphin]\nSortRole=size\n");

        var view = new JsonFolderViewStore(_root).Read(Here);

        Assert.NotNull(view);
        Assert.Equal(SortField.Size, view!.Sort);
        Assert.Null(view.View);
        Assert.Null(view.GroupBy);
        Assert.Null(view.SortDescending);
    }

    /// <summary>
    /// And the pane honours that silence rather than the record's defaults,
    /// which is where the damage would have shown. The three the file does not
    /// mention are each set to something other than their default first, so
    /// none of them can pass by agreeing with what the record used to impose.
    /// </summary>
    [AvaloniaFact]
    public async Task So_a_dolphin_folder_that_named_only_a_sort_leaves_the_layout_alone()
    {
        Write(Here, "[Dolphin]\nSortRole=size\n");
        PaneViewModel.FolderViews = new JsonFolderViewStore(_root);

        var pane = Pane();
        pane.View = ViewMode.Grid;
        pane.GroupBy = GroupMode.Kind;
        pane.SortDescending = true;

        await pane.NavigateAsync(Here);

        // The one thing it did say.
        Assert.Equal(SortField.Size, pane.Sort);

        Assert.Equal(ViewMode.Grid, pane.View);
        Assert.Equal(GroupMode.Kind, pane.GroupBy);
        Assert.True(pane.SortDescending);
    }

    /// <summary>
    /// Dolphin's hidden-files key, which maps onto Ctrl+H exactly and was being
    /// dropped. Read under either heading — which heading a real Dolphin writes
    /// was not measured here, and taking both costs nothing now that the key
    /// can only ever answer "shown".
    /// </summary>
    [AvaloniaFact]
    public void A_dot_directory_can_say_hidden_files_are_shown()
    {
        Write(Here, "[Dolphin]\nViewMode=1\nHiddenFilesShown=true\n");
        Write(Elsewhere, "[Dolphin]\nViewMode=1\n[Settings]\nHiddenFilesShown=true\n");

        var store = new JsonFolderViewStore(_root);

        Assert.True(store.Read(Here)!.ShowHidden, "not read from [Dolphin]");
        Assert.True(store.Read(Elsewhere)!.ShowHidden, "not read from [Settings]");
    }

    /// <summary>
    /// And a file that says only that is still worth reading — the key is
    /// accepted with no layout beside it, so a `.directory` holding nothing
    /// else is not thrown away.
    /// </summary>
    [AvaloniaFact]
    public void Even_when_that_is_all_it_says()
    {
        Write(Here, "[Settings]\nHiddenFilesShown=true\n");

        var view = new JsonFolderViewStore(_root).Read(Here);

        Assert.NotNull(view);
        Assert.True(view!.ShowHidden);
        Assert.Null(view.View);
    }

    /// <summary>
    /// **But it may never say they are hidden.** A `.directory` is content of
    /// the folder being listed — an extracted archive, a network share and a
    /// synced directory all carry whatever their producer put in them — so
    /// honouring <c>HiddenFilesShown=false</c> handed that producer a switch
    /// that turns the reader's files out of sight. So the key is read in one
    /// direction: it may reveal and may not conceal, and a file that says
    /// "false" is a file that said nothing.
    ///
    /// Two folders, because the two halves fail differently: alone the key
    /// decides whether there is a record at all, and beside a layout it decides
    /// one field of one.
    /// </summary>
    [AvaloniaFact]
    public void A_dot_directory_can_ask_for_hidden_files_but_never_against_them()
    {
        Write(Here, "[Dolphin]\nHiddenFilesShown=false\n");
        Write(Elsewhere, "[Dolphin]\nViewMode=1\nHiddenFilesShown=false\n");

        var store = new JsonFolderViewStore(_root);

        Assert.Null(store.Read(Here));

        Assert.Equal(ViewMode.Compact, store.Read(Elsewhere)!.View);
        Assert.Null(store.Read(Elsewhere)!.ShowHidden);
    }

    /// <summary>
    /// And the pane keeps its own answer, which is the half that shows.
    ///
    /// **Measured before the one-direction rule**, with the pane at Ctrl+H on:
    /// arriving in that folder turned hidden files off, and walking back out
    /// left them off — a foreign file quietly concealing files for the rest of
    /// the session, and into session.json on close. Asserted after leaving,
    /// because that is where it stopped being about one folder.
    /// </summary>
    [AvaloniaFact]
    public async Task And_a_pane_that_lands_in_such_a_folder_keeps_its_own_answer()
    {
        Write(Here, "[Dolphin]\nHiddenFilesShown=false\n");
        PaneViewModel.FolderViews = new JsonFolderViewStore(_root);

        var pane = Pane();
        pane.ShowHidden = true;

        await pane.NavigateAsync(Here);
        await pane.NavigateAsync(Elsewhere);

        Assert.True(pane.ShowHidden);
    }

    /// <summary>A file with no such key says nothing about hidden files, rather
    /// than saying "hide them" — and the same for the sort it never named.
    /// </summary>
    [AvaloniaFact]
    public void And_a_file_that_never_mentions_them_says_nothing_about_them()
    {
        Write(Here, "[Dolphin]\nViewMode=1\n");

        var view = new JsonFolderViewStore(_root).Read(Here);

        Assert.Equal(ViewMode.Compact, view!.View);
        Assert.Null(view.ShowHidden);
        Assert.Null(view.Sort);
    }

    // ---- and it all reaches the file ---------------------------------------

    /// <summary>
    /// The new fields survive the round trip through folder-views.json. Every
    /// assertion above holds with an in-memory store, and a field the
    /// serializer skipped would be forgotten by the next launch instead — which
    /// is the same symptom as never recording it.
    /// </summary>
    [AvaloniaFact]
    public void What_is_remembered_reaches_the_file()
    {
        var first = new JsonFolderViewStore(_root);

        first.Write(Here, new FolderViewState
        {
            View = ViewMode.Compact,
            ShowHidden = true,
            Columns = new FolderColumns { HideModified = true, ShowCreated = true },
        });

        first.Flush();

        var view = new JsonFolderViewStore(_root).Read(Here);

        Assert.NotNull(view);
        Assert.Equal(ViewMode.Compact, view!.View);
        Assert.True(view.ShowHidden);
        Assert.NotNull(view.Columns);
        Assert.True(view.Columns!.HideModified);
        Assert.True(view.Columns.ShowCreated);
        Assert.False(view.Columns.HideSize);
    }

    private static void Write(string folder, string body)
        => File.WriteAllText(Path.Combine(folder, ".directory"), body);

    /// <summary>
    /// Lists the real temp folder, honouring IncludeHidden and calling a
    /// leading dot hidden.
    ///
    /// **The hidden rule is spelled here rather than asked of the platform**,
    /// because these folders live in the temp directory and nothing sets the
    /// Windows hidden ATTRIBUTE on a file called ".secret" — a provider that
    /// deferred to the OS would call it visible, and the reveal tests would
    /// then pass without ever exercising an unhide.
    ///
    /// Every folder here is created empty, so the tests that are about view
    /// state and not about contents see exactly what an empty provider gave
    /// them; only the reveal tests put a file in one.
    /// </summary>
    private sealed class Listing : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            var rows = new List<FileEntry>();

            foreach (var file in Directory.EnumerateFiles(path))
                if (Entry(file) is { } row && (options.IncludeHidden || !row.IsConcealed))
                    rows.Add(row);

            yield return rows;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(File.Exists(path) ? Entry(path) : null);

        private static FileEntry? Entry(string path)
        {
            var name = Path.GetFileName(path);

            return new FileEntry(
                name, path, 0, default,
                name.StartsWith('.') ? EntryFlags.Hidden : EntryFlags.None);
        }

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
