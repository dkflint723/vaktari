using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Reaching an application that is not in the "Open with" list.
///
/// **There was no way to, unless you were on Windows.** The launcher interface
/// offered a chooser only as "the platform shows its own dialog" — which
/// Windows does, through SHOpenWithDialog, and a desktop does not: xdg-open
/// launches the default and has no ask. So the row was appended on one platform
/// and on the other a file whose type nothing claims drew an "Open with"
/// submenu with nothing in it whatsoever.
///
/// The two routes stay separate on purpose. Windows' dialog browses for an
/// executable and writes the association the rest of the system reads, and
/// neither is reachable from here — so it is asked first and nothing drawn here
/// is offered in its place.
/// </summary>
public sealed class ChooseApplicationTests : OwnedViewModels
{
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

    /// <summary>
    /// A launcher told which of the two chooser routes it has, so that a
    /// platform careless about both — and a platform claiming one while
    /// offering the other — are states this suite can actually produce.
    /// </summary>
    private sealed class FakeLauncher(bool ownDialog, params LaunchOption[] installed)
        : IApplicationLauncher
    {
        public bool CanChooseApplication
        {
            get
            {
                AskedOnTheUiThread = Dispatcher.UIThread.CheckAccess();
                return ownDialog || installed.Length > 0;
            }
        }

        /// <summary>Where the capability was read from. The scan behind it on a
        /// desktop reads every .desktop file the machine has.</summary>
        public bool? AskedOnTheUiThread { get; private set; }

        public bool ShownItsOwn { get; private set; }

        public bool ChooseApplication(string path)
        {
            if (!ownDialog) return false;

            ShownItsOwn = true;
            return true;
        }

        public IReadOnlyList<LaunchOption> AllApplications => installed;

        public List<(string Path, LaunchOption With)> Launched { get; } = [];

        public void OpenWith(string path, LaunchOption option) => Launched.Add((path, option));

        public Exception? Open(string path) => null;
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
    }

    private static readonly LaunchOption Writer = new("Writer", "writer.desktop");
    private static readonly LaunchOption HexEditor = new("Hex editor", "ghex.desktop");

    private static readonly LaunchOption TheChooserRow =
        new("Choose another app…", "") { IsChooser = true };

    private PaneViewModel Pane(FakeLauncher launcher, string folder)
    {
        var pane = Own(new PaneViewModel(new InertFileSystem(), null, launcher)
        {
            CurrentPath = folder,
        });

        pane.SelectedEntry = new FileEntry(
            "notes.txt", Path.Combine(folder, "notes.txt"), 0, DateTimeOffset.Now, EntryFlags.None);

        return pane;
    }

    private static ChooseApplicationViewModel? Asked(PaneViewModel pane, LaunchOption row)
    {
        ChooseApplicationViewModel? model = null;

        pane.ChooseApplicationRequested += (_, m) => model = m;
        pane.OpenWithApp(row);

        return model;
    }

    // ---- the menu row -------------------------------------------------------

    /// <summary>
    /// **The row itself, on a platform that has no dialog of its own.** It was
    /// appended only where CanChooseApplication was true, and only Windows
    /// could ever say so.
    /// </summary>
    [AvaloniaFact]
    public void A_platform_that_can_enumerate_gets_the_chooser_row()
    {
        var pane = Pane(new FakeLauncher(ownDialog: false, Writer), Path.GetTempPath());

        Settle(() => pane.OpenWithOptions.Count > 0);

        Assert.Contains(pane.OpenWithOptions, o => o.IsChooser);

        // And the submenu is drawn at all, which is the second half of the
        // fault: HasOpenWithOptions is a count, and the count was zero on
        // exactly the file types that need this.
        Assert.True(pane.HasOpenWithOptions);
    }

    /// <summary>
    /// The other way round, so the row is not simply unconditional: a launcher
    /// with neither route still shows nothing, which is the interface's own
    /// rule — no entry beats an entry that does nothing.
    /// </summary>
    [AvaloniaFact]
    public void A_platform_with_neither_route_gets_no_row()
    {
        var pane = Pane(new FakeLauncher(ownDialog: false), Path.GetTempPath());

        Settle(() => false);

        Assert.Empty(pane.OpenWithOptions);
    }

    /// <summary>
    /// **Read where the enumeration is, not on the thread that draws the
    /// menu.** It sat inside the dispatcher Post, which cost nothing while the
    /// only platform answering yes returned a constant; a desktop answers it by
    /// scanning every .desktop file the machine has.
    /// </summary>
    [AvaloniaFact]
    public void The_capability_is_asked_off_the_ui_thread()
    {
        var launcher = new FakeLauncher(ownDialog: false, Writer);
        var pane = Pane(launcher, Path.GetTempPath());

        Settle(() => launcher.AskedOnTheUiThread is not null);

        Assert.False(launcher.AskedOnTheUiThread,
                     "the chooser capability was read on the UI thread");
    }

    // ---- which chooser ------------------------------------------------------

    /// <summary>
    /// Windows' own dialog is asked first and nothing is drawn behind it. It
    /// browses for an executable and remembers the choice in the association
    /// the rest of the system reads; a list of installed applications does
    /// neither.
    /// </summary>
    [AvaloniaFact]
    public void The_platforms_own_dialog_is_what_a_platform_with_one_shows()
    {
        var launcher = new FakeLauncher(ownDialog: true, Writer);
        var pane = Pane(launcher, Path.GetTempPath());

        Assert.Null(Asked(pane, TheChooserRow));
        Assert.True(launcher.ShownItsOwn);
    }

    /// <summary>And a platform with no dialog of its own is given ours, filled
    /// with what it enumerated.</summary>
    [AvaloniaFact]
    public void A_platform_with_no_dialog_of_its_own_gets_ours()
    {
        var pane = Pane(new FakeLauncher(ownDialog: false, Writer, HexEditor), Path.GetTempPath());

        var model = Asked(pane, TheChooserRow);

        Assert.NotNull(model);
        Assert.Equal([Writer, HexEditor], model!.Shown);
        Assert.Equal("notes.txt", model.FileName);
    }

    /// <summary>
    /// **And a launcher with nothing to offer opens nothing.** The row is
    /// gated on CanChooseApplication, but the two are separate questions and a
    /// platform careless about one is a state this suite can produce — an empty
    /// chooser window is the worst of the three possible answers.
    /// </summary>
    [AvaloniaFact]
    public void A_launcher_with_nothing_to_offer_opens_no_empty_window()
    {
        var pane = Pane(new FakeLauncher(ownDialog: false), Path.GetTempPath());

        Assert.Null(Asked(pane, TheChooserRow));
    }

    /// <summary>
    /// Picking a row opens the file with it — through the launcher, and with
    /// the path of the selected entry rather than its name.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_an_application_opens_the_file_with_it()
    {
        var launcher = new FakeLauncher(ownDialog: false, Writer, HexEditor);
        var pane = Pane(launcher, Path.GetTempPath());

        var model = Asked(pane, TheChooserRow)!;

        model.Selected = HexEditor;
        model.OpenCommand.Execute(null);

        Assert.Equal([(Path.Combine(Path.GetTempPath(), "notes.txt"), HexEditor)], launcher.Launched);
    }

    /// <summary>
    /// **A dismissed chooser opened nothing and must not claim to.** The
    /// Windows branch keeps that rule by asking before it records; this one
    /// keeps it by recording inside the callback.
    /// </summary>
    [AvaloniaFact]
    public void A_cancelled_chooser_opens_nothing_and_is_not_a_recent()
    {
        var launcher = new FakeLauncher(ownDialog: false, Writer);
        var pane = Pane(launcher, Path.GetTempPath());

        var recents = new RecordingRecents();
        var before = PaneViewModel.Recents;
        PaneViewModel.Recents = recents;

        try
        {
            var model = Asked(pane, TheChooserRow)!;

            model.CancelCommand.Execute(null);

            Assert.Empty(launcher.Launched);
            Assert.Empty(recents.Recorded);

            // The control: the same pane through the same window DOES record
            // when something is picked, so "empty" above is the cancel and not
            // an inert fake.
            var second = Asked(pane, TheChooserRow)!;
            second.OpenCommand.Execute(null);

            Assert.Equal([Path.Combine(Path.GetTempPath(), "notes.txt")], recents.Recorded);
        }
        finally
        {
            PaneViewModel.Recents = before;
        }
    }

    /// <summary>
    /// **Nothing consulted a setting: every open went in.** Every folder and
    /// every file opened was recorded, and the only route out was a per-row
    /// "Forget" needing the entry still on screen. The switch is read where the
    /// store is reached rather than at each of the five call sites, so a sixth
    /// cannot be added that forgets to ask.
    ///
    /// Through the chooser because that is a real opening route with a harness
    /// already here, and its sibling above proves the same fake DOES record
    /// when the setting is on.
    /// </summary>
    [AvaloniaFact]
    public void Nothing_is_recorded_when_the_setting_is_off()
    {
        var launcher = new FakeLauncher(ownDialog: false, Writer);
        var pane = Pane(launcher, Path.GetTempPath());

        var recents = new RecordingRecents();
        var before = PaneViewModel.Recents;
        var settings = Vaktari.Ui.Settings.AppSettings.Current;

        PaneViewModel.Recents = recents;

        Vaktari.Ui.Settings.AppSettings.Apply(settings with
        {
            General = settings.General with { RememberRecent = false },
        });

        try
        {
            var model = Asked(pane, TheChooserRow)!;

            model.OpenCommand.Execute(null);

            Assert.NotEmpty(launcher.Launched);
            Assert.Empty(recents.Recorded);
        }
        finally
        {
            Vaktari.Ui.Settings.AppSettings.Apply(settings);
            PaneViewModel.Recents = before;
        }
    }

    private sealed class RecordingRecents : IRecentStore
    {
        public List<string> Recorded { get; } = [];

        public void Record(string path, RecentKind kind) => Recorded.Add(path);

        public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count) => [];
        public void Forget(string path) { }
        public int Count => Recorded.Count;
        public int ForgetAll() { var had = Recorded.Count; Recorded.Clear(); return had; }
        public event EventHandler? Changed { add { } remove { } }
    }

    /// <summary>
    /// The bin guard covers this door too. "Open with" is drawn on a bin row,
    /// and the chooser hands the launcher a path itself — so choosing an
    /// application for a binned notes.txt would open whatever now holds that
    /// path.
    /// </summary>
    [AvaloniaFact]
    public void The_bin_refuses_the_chooser()
    {
        var launcher = new FakeLauncher(ownDialog: false, Writer);
        var pane = Pane(launcher, VirtualPaths.Trash);

        Assert.Null(Asked(pane, TheChooserRow));
        Assert.False(launcher.ShownItsOwn);
        Assert.Contains(Vaktari.Core.Naming.TheBin, pane.Status);
    }

    // ---- the window's own behaviour ----------------------------------------

    private static ChooseApplicationViewModel Model(
        Action<LaunchOption>? pick = null, params LaunchOption[] installed)
        => new("notes.txt", installed.Length > 0 ? installed : [Writer, HexEditor],
               pick ?? (_ => { }));

    /// <summary>
    /// The list is everything installed — several hundred rows on a real
    /// desktop — so the filter is the only way through it. Both fields, because
    /// an application whose Name is "Text Editor" is found by nobody typing the
    /// command they know it by.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("hex", "ghex.desktop")]
    [InlineData("GHEX", "ghex.desktop")]
    // "editor" is in the name and in neither id; "writer.desktop" is in an id
    // and in neither name. One row for each half, so dropping either clause
    // fails this.
    [InlineData("editor", "ghex.desktop")]
    [InlineData("writer.desktop", "writer.desktop")]
    public void The_filter_narrows_by_name_and_by_id(string typed, string left)
    {
        var model = Model();

        model.Filter = typed;

        Assert.Equal(left, Assert.Single(model.Shown).Id);
    }

    /// <summary>
    /// **The selection follows the filter to the first match.** Typing a name
    /// and pressing Enter is the whole gesture this window is for, and a
    /// ListBox nulls its SelectedItem the moment the source clears — so the old
    /// value has to be read before the refill and the first row taken when it
    /// is gone.
    /// </summary>
    [AvaloniaFact]
    public void The_filter_moves_the_selection_to_the_first_match()
    {
        var model = Model();

        Assert.Equal(Writer, model.Selected);

        model.Filter = "hex";

        Assert.Equal(HexEditor, model.Selected);
    }

    /// <summary>And a selection the filter still shows is kept, rather than
    /// being knocked back to the top on every keystroke.</summary>
    [AvaloniaFact]
    public void A_selection_the_filter_keeps_is_not_moved()
    {
        var model = Model();

        model.Selected = HexEditor;
        model.Filter = "e";

        Assert.Equal(HexEditor, model.Selected);
    }

    /// <summary>
    /// **CanOpen is computed, so something has to say it changed.** The Open
    /// button binds it; without the notification the button is drawn from
    /// whatever the value was when the window opened and never enables, so the
    /// chooser can be filtered and selected and never used.
    /// </summary>
    [AvaloniaFact]
    public void Selecting_a_row_tells_the_open_button()
    {
        var model = Model();
        var raised = new List<string?>();

        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Selected = HexEditor;

        Assert.Contains(nameof(model.CanOpen), raised);
    }

    /// <summary>
    /// The window is shown from the pane's event, and the pane raises it once
    /// per pane. Read out of WirePane rather than driven: showing it for real
    /// means a modal ShowDialog, which does not return until something closes
    /// it.
    /// </summary>
    [AvaloniaFact]
    public void Every_pane_is_wired_to_the_window_that_draws_the_chooser()
    {
        var body = RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                                   "private void WirePane(PaneViewModel pane)");

        Assert.Contains("pane.ChooseApplicationRequested += OnChooseApplicationRequested;",
                        body, StringComparison.Ordinal);

        // And unhooked first. WirePane runs again every time a pane is rebound,
        // and a handler added twice shows the chooser twice — the reason every
        // other pair in that method is written this way.
        Assert.Contains("pane.ChooseApplicationRequested -= OnChooseApplicationRequested;",
                        body, StringComparison.Ordinal);
    }

    /// <summary>Nothing matching means nothing selected, and nothing to
    /// open.</summary>
    [AvaloniaFact]
    public void A_filter_that_matches_nothing_leaves_nothing_to_open()
    {
        var model = Model();

        model.Filter = "no-such-application";

        Assert.Empty(model.Shown);
        Assert.False(model.CanOpen);

        var picked = false;
        Model(_ => picked = true).OpenCommand.Execute(null);
        Assert.True(picked, "the control: Open does something when there IS a selection");
    }

    /// <summary>
    /// The window builds and lays out with its list bound.
    ///
    /// **Compiling is not running.** Avalonia checks binding paths at build
    /// time and a style that compiled perfectly is what killed the process in
    /// 0.7.0; the fault only exists once controls are realised, and this window
    /// is new markup on a path that runs only when somebody goes looking past
    /// the registered applications.
    /// </summary>
    [AvaloniaFact]
    public void The_window_opens_with_the_list_bound_and_the_filter_focused()
    {
        var model = Model();

        var window = new Vaktari.Ui.ChooseApplicationWindow(model);

        window.Show();

        // Realises the item template: a binding that throws does so here rather
        // than at construction.
        window.Measure(new Avalonia.Size(440, 520));
        window.Arrange(new Avalonia.Rect(0, 0, 440, 520));

        var list = window.GetVisualDescendants().OfType<ListBox>().Single();

        Assert.Same(model.Shown, list.ItemsSource);

        var filter = window.GetVisualDescendants().OfType<TextBox>().Single();

        Assert.True(filter.IsFocused, "the caret did not land in the filter");

        window.Close();
    }

    /// <summary>
    /// And the model's Closed takes the window with it, whichever button raised
    /// it — otherwise choosing an application opens the file and leaves the
    /// chooser standing over it.
    /// </summary>
    [AvaloniaFact]
    public void Picking_something_closes_the_window()
    {
        var model = Model();

        var window = new Vaktari.Ui.ChooseApplicationWindow(model);

        window.Show();

        Assert.True(window.IsVisible);

        model.OpenCommand.Execute(null);

        Assert.False(window.IsVisible);
    }

    /// <summary>
    /// The pointer's half. A row is opened by double-clicking it, and the
    /// gesture is carried by an attached property on the template root — which
    /// needs a Background, because Avalonia hit tests against a brush and null
    /// is not one.
    /// </summary>
    [AvaloniaFact]
    public void A_row_can_be_opened_by_double_clicking_it()
    {
        var markup = System.Xml.Linq.XDocument.Parse(
            RepoSource.Ui("ChooseApplicationWindow.axaml"));

        var root = markup.Descendants(
            System.Xml.Linq.XNamespace.Get("https://github.com/avaloniaui") + "StackPanel")
            .Single(p => p.Attribute("Background") is not null);

        // "DoubleClick.Command" is the whole local name: the prefix becomes
        // the namespace, and only the dotted remainder stays on the attribute.
        Assert.Contains(root.Attributes(),
                        a => a.Name.LocalName == "DoubleClick.Command"
                             && a.Value.Contains("OpenCommand", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The list writes the selection back, and the button reads it.**
    /// Everything about opening hangs off Selected — what CanOpen answers, what
    /// Open hands the launcher — so a list bound one way is a window where
    /// clicking a row does nothing and the Open button never comes alive.
    /// Nothing else here touches the ListBox's own SelectedItem, and nothing
    /// else reads the button.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_a_row_arms_the_open_button()
    {
        var model = Model();
        var window = Shown(model);

        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        var open = window.GetVisualDescendants().OfType<Button>()
            .Single(b => (string?)b.Content == "Open");

        list.SelectedItem = HexEditor;

        Assert.Equal(HexEditor, model.Selected);
        Assert.True(open.IsEnabled, "the Open button stayed dead with a row selected");

        model.Filter = "no-such-application";

        Assert.False(open.IsEnabled, "the Open button stayed alive with nothing to open");

        window.Close();
    }

    /// <summary>
    /// The window says which file it is opening, and says what it will not do.
    ///
    /// **Both carry weight.** The chooser is reached from a menu on a row, and
    /// by the time it is up the row is behind it; and the one-shot behaviour is
    /// a decision, not an omission — picking here does not become the type's
    /// default — so the window has to say so rather than leave somebody to
    /// discover it by opening the file again tomorrow.
    /// </summary>
    [AvaloniaFact]
    public void The_window_names_the_file_and_says_it_remembers_nothing()
    {
        var window = Shown(Model());

        var said = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").ToList();

        Assert.Contains("Open notes.txt with", said);
        Assert.Contains(said, t => t.Contains("Nothing here is remembered", StringComparison.Ordinal));

        window.Close();
    }

    /// <summary>
    /// **Enter opens, from either place a person can be standing.** Typing a
    /// name and pressing Enter is the whole gesture this window is for, and
    /// arrowing into the list and pressing Enter is the other half of it.
    ///
    /// Both measured, and each half rests on a different thing. Removing
    /// IsDefault from the Open button leaves the FILTER case unopened;
    /// removing the ListBox's own Enter binding leaves the ROW case unopened,
    /// because a focused list item eats the key before the default button sees
    /// it. Focus the realised container rather than the ListBox for that
    /// second one — with the ListBox itself focused, IsDefault answers and the
    /// missing binding goes unnoticed.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enter_opens_what_is_selected(bool fromTheList)
    {
        LaunchOption? picked = null;

        var window = Shown(Model(o => picked = o));

        if (fromTheList)
        {
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            var row = list.ContainerFromIndex(0);

            Assert.NotNull(row);
            Assert.True(row!.Focus(), "the row did not take focus");
        }

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");

        Assert.Equal(Writer, picked);

        window.Close();
    }

    /// <summary>
    /// **The Cancel button leaves, and leaves nothing behind.** Escape is
    /// answered by IsCancel and closes the window on its own — measured: with
    /// the button's Command taken away, EscapeClosesDialogsTests still passes —
    /// so the command is what the POINTER half rests on, and without it the
    /// button a person reaches for is one that does nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void The_cancel_button_leaves_without_opening_anything()
    {
        LaunchOption? picked = null;

        var window = Shown(Model(o => picked = o));

        var cancel = window.GetVisualDescendants().OfType<Button>()
            .Single(b => (string?)b.Content == "Cancel");

        var at = cancel.TranslatePoint(
            new Avalonia.Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), window)
            ?? new Avalonia.Point(0, 0);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);

        Assert.Null(picked);
        Assert.False(window.IsVisible, "the chooser stayed up after Cancel");
    }

    /// <summary>
    /// Shown and laid out, which is what realises the item template: a binding
    /// that throws does so here rather than at construction.
    /// </summary>
    private static Vaktari.Ui.ChooseApplicationWindow Shown(ChooseApplicationViewModel model)
    {
        var window = new Vaktari.Ui.ChooseApplicationWindow(model);

        window.Show();
        window.Measure(new Avalonia.Size(440, 520));
        window.Arrange(new Avalonia.Rect(0, 0, 440, 520));

        return window;
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>
    /// The "Open with" list is filled from a background task that posts back,
    /// so a test that reads it straight after the selection reads it empty.
    /// Pumped rather than slept on: the Post cannot run without a dispatcher
    /// turn.
    /// </summary>
    private static void Settle(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
