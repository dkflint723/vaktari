using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// One band, one header — and a header that says how big its band is and can
/// be used to take it.
///
/// **Every band used to appear twice.** Folders-first was applied before the
/// group key, so the order was [every folder, by band][every file, by band] —
/// and a header is emitted wherever the label changes. Grouping a folder and a
/// file both modified today produced "Today", the folders, then "Today" again,
/// the files. Explorer and Dolphin both put them under one heading, folders at
/// the top of it.
///
/// Size and Kind never showed the fault, because both give folders a band of
/// their own; it was Name and Modified, which band folders and files the same
/// way, that repeated.
///
/// **And then the header sat there.** RecomputeGroups stored the bare label, so
/// a heading said "This month" over a run of unknown length, and it was a
/// ContentControl, so the only way to take the run it named was to click its
/// first row and shift-click its last — which needs both ends on screen at
/// once, and a band is exactly as long as it wants to be. The tests for that
/// half start at "the heading says how big its band is" below.
///
/// **And underneath both of those, no heading was ever drawn at all.** RowGroup
/// finds the pane by walking up from the row, and the row's bindings are
/// applied while the template content is still being built — before the control
/// has a parent to walk. Measured on a real window before anything here was
/// changed: every heading control came back IsVisible=false with empty content.
/// A count nobody can see and a button nobody can press are not a fix, so that
/// is fixed here too.
///
/// **And a heading that IS drawn has to keep up with the folder.** The map is
/// rebuilt per watcher burst as well as per listing, but a realized row re-read
/// it only when its own entry changed — so a file arriving at the head of a
/// band left two headings on screen and a deletion left none. Those start at
/// "and the heading has to keep up with the folder" below.
/// </summary>
public sealed class GroupHeaderTests : OwnedViewModels
{
    private static FileEntry Entry(string name, bool directory, DateTimeOffset when)
        => new(name, "/tmp/" + name, 10, when,
               directory ? EntryFlags.Directory : EntryFlags.None);

    private async Task<PaneViewModel> Listing(GroupMode mode, params FileEntry[] entries)
    {
        var pane = Own(new PaneViewModel(new Canned(entries), null, null)
        {
            ViewportWidth = 1400,
            GroupBy = mode,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    /// <summary>The headers as they would be drawn, in row order.</summary>
    private static List<string> Headers(PaneViewModel pane)
        => pane.DetailsEntries
            .Select(e => pane.HeaderFor(e.FullPath)?.Label)
            .OfType<string>()
            .ToList();

    /// <summary>The fault, in the smallest shape that shows it.</summary>
    [AvaloniaFact]
    public async Task A_folder_and_a_file_from_the_same_day_share_one_header()
    {
        var today = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Modified,
            Entry("a-folder", directory: true, today),
            Entry("b-file.txt", directory: false, today));

        var headers = Headers(pane);

        Assert.Single(headers);
    }

    /// <summary>And by name, the other mode that bands folders and files
    /// alike: "A" must not appear over the folders and again over the
    /// files.</summary>
    [AvaloniaFact]
    public async Task Grouping_by_name_does_not_repeat_a_letter()
    {
        var when = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Name,
            Entry("Apples", directory: true, when),
            Entry("apricot.txt", directory: false, when),
            Entry("Bananas", directory: true, when),
            Entry("berry.txt", directory: false, when));

        var headers = Headers(pane);

        Assert.Equal(headers.Count, headers.Distinct().Count());
        Assert.Equal(2, headers.Count);
    }

    /// <summary>
    /// Folders still come first — inside the band, which is where the
    /// convention actually lives. Fixing the duplicate must not cost the
    /// ordering everyone expects.
    /// </summary>
    [AvaloniaFact]
    public async Task Folders_still_come_first_inside_the_band()
    {
        var today = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Modified,
            Entry("zzz-folder", directory: true, today),
            Entry("aaa-file.txt", directory: false, today));

        var order = pane.DetailsEntries.Select(e => e.Name).ToList();

        Assert.Equal(["zzz-folder", "aaa-file.txt"], order);
    }

    /// <summary>
    /// With no grouping at all, folders-first is the only rule and still
    /// applies — the tie-break moved, it did not go.
    /// </summary>
    [AvaloniaFact]
    public async Task Without_grouping_folders_still_come_first()
    {
        var when = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.None,
            Entry("aaa-file.txt", directory: false, when),
            Entry("zzz-folder", directory: true, when));

        Assert.Equal("zzz-folder", pane.DetailsEntries.First().Name);
    }

    /// <summary>
    /// Grouping by size was never wrong, because folders get a band of their
    /// own there. Pinned so the reordering above cannot quietly break it.
    /// </summary>
    [AvaloniaFact]
    public async Task Grouping_by_size_still_puts_folders_in_their_own_band()
    {
        var when = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Size,
            Entry("a-folder", directory: true, when),
            Entry("b-file.txt", directory: false, when));

        var headers = Headers(pane);

        Assert.Equal(2, headers.Count);
        Assert.Contains("Folders", headers);
    }

    // ---- the heading says how big its band is ------------------------------

    /// <summary>Two md and three txt, so neither band's count can be right by
    /// accident and neither is 1 — a band of one would pass a count that was
    /// hard-wired to the number of bands, or to nothing.</summary>
    private Task<PaneViewModel> TwoBands()
    {
        var when = DateTimeOffset.Now;

        return Listing(
            GroupMode.Kind,
            Entry("a.txt", directory: false, when),
            Entry("b.txt", directory: false, when),
            Entry("c.txt", directory: false, when),
            Entry("notes.md", directory: false, when),
            Entry("other.md", directory: false, when));
    }

    /// <summary>
    /// **The heading was the band's name and nothing else**, so "TXT" stood
    /// over a run whose length you could only learn by counting rows.
    ///
    /// Both bands, because they are written by different lines: a run is closed
    /// when the next label arrives, and the last run in the listing has no
    /// successor to close it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_heading_says_how_many_rows_are_under_it()
    {
        var pane = await TwoBands();

        // Kind orders by extension, so md comes first and txt is the run that
        // ends the listing.
        Assert.Equal(new GroupHeader("MD", 2), pane.HeaderFor("/tmp/notes.md"));
        Assert.Equal(new GroupHeader("TXT", 3), pane.HeaderFor("/tmp/a.txt"));
    }

    /// <summary>The heading as it is spelled on screen. Separate from the
    /// record above because the row draws Text and nothing else.</summary>
    [AvaloniaFact]
    public async Task The_count_is_drawn_beside_the_label()
    {
        var pane = await TwoBands();

        Assert.Equal("TXT (3)", pane.HeaderFor("/tmp/a.txt")?.Text);
    }

    /// <summary>
    /// **A heading named a run and gave no way to take it.** Selecting a band
    /// meant clicking its first row and shift-clicking its last, which needs
    /// both ends on screen at once.
    ///
    /// Something else is selected first, so this pins the band REPLACING the
    /// selection: a heading means "these", not "these as well".
    /// </summary>
    [AvaloniaFact]
    public async Task A_heading_picks_every_row_in_its_band()
    {
        var pane = await TwoBands();

        pane.SelectedEntries.Add(pane.DetailsEntries.Single(e => e.Name == "notes.md"));

        pane.SelectGroupCommand.Execute("/tmp/a.txt");

        Assert.Equal(["a.txt", "b.txt", "c.txt"],
                     pane.Selection.Select(e => e.Name).ToList());

        // And the keyboard is on the band, not still on whatever it was on —
        // the same thing Reselect does after a rebuild.
        Assert.Equal("a.txt", pane.SelectedEntry?.Name);

        // Now the band that is NOT last in the listing, which is what says the
        // run stops at its own end rather than at the end of the folder — and
        // that a second heading replaces the first one's rows rather than
        // adding to them.
        pane.SelectGroupCommand.Execute("/tmp/notes.md");

        Assert.Equal(["notes.md", "other.md"],
                     pane.Selection.Select(e => e.Name).ToList());
    }

    /// <summary>
    /// Only the row a heading actually sits on starts a band. Without the
    /// lookup guard, any path at all would take a run from wherever it happened
    /// to be — and the pane is asked by a command parameter bound to a row.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_that_starts_no_band_picks_nothing()
    {
        var pane = await TwoBands();

        pane.SelectedEntries.Add(pane.DetailsEntries.Single(e => e.Name == "notes.md"));

        // b.txt is INSIDE the txt band, not at its head.
        pane.SelectGroupCommand.Execute("/tmp/b.txt");

        Assert.Equal(["notes.md"], pane.Selection.Select(e => e.Name).ToList());
    }

    // ---- and the heading has to reach the screen at all ---------------------

    /// <summary>
    /// **No heading was ever drawn.** RowGroup finds the pane by walking up
    /// from the row, and a row's bindings are applied while the template's
    /// content is still being built — before the new control has a logical
    /// parent. So the walk found nothing, the control stayed hidden, and
    /// nothing ran it again: the map is rebuilt per listing, not per row.
    ///
    /// This is that sequence exactly: the property is set while the control is
    /// unparented, which is the moment the row template does it, and only then
    /// is it put in a tree that leads to the pane.
    ///
    /// The window is closed at the end for the reason AccentContrastTests
    /// gives: one left open is torn down later on whatever thread happens to be
    /// running, and surfaces in the cleanup of an unrelated test.
    /// </summary>
    [AvaloniaFact]
    public async Task A_heading_set_before_the_row_is_in_the_tree_still_appears()
    {
        var pane = await TwoBands();

        var heading = new ContentControl();

        Vaktari.Ui.Thumbnails.RowGroup.SetEntry(
            heading, pane.DetailsEntries.Single(e => e.Name == "a.txt"));

        Assert.False(heading.IsVisible,
                     "unparented there is no way to reach the pane, which is the fault");

        var window = new Window { DataContext = pane, Content = heading };

        try
        {
            window.Show();

            Assert.True(heading.IsVisible, "the heading never appeared");
            Assert.Equal("TXT (3)", heading.Content);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- and the heading is not the row it is drawn in -----------------------

    private static FileEntry? EntryAt(object? source)
        => (FileEntry?)typeof(MainWindow)
            .GetMethod("EntryAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source]);

    /// <summary>
    /// **The heading is drawn inside the row it stands over, and inherits that
    /// row's FileEntry**, so the walk that answers "which row was this?"
    /// answered with the row for anything that landed on the heading. The left
    /// drag arm, OnTapped and OnDoubleTapped all ask it — so a twitch while
    /// pressing a heading armed a drag of the row underneath, and a click on it
    /// opened that row in single-click mode.
    ///
    /// The Border is the other half: the walk still has to answer for the row's
    /// own cells, which is the whole reason it exists.
    ///
    /// A hand-made chain rather than a realized template, because the WALK is
    /// what is under test and this is exactly the shape it walks — a control
    /// with the class, a descendant of it carrying the inherited entry. The
    /// real one is checked in the window test below.
    /// </summary>
    [AvaloniaFact]
    public void A_press_inside_a_heading_is_not_a_press_on_the_row()
    {
        var entry = new FileEntry("report.txt", "/tmp/report.txt", 1,
                                  DateTimeOffset.Now, EntryFlags.None);

        var inside = new TextBlock { DataContext = entry };
        var heading = new Panel { DataContext = entry, Children = { inside } };

        heading.Classes.Add(MainWindow.GroupHeadingClass);

        Assert.Null(EntryAt(heading));
        Assert.Null(EntryAt(inside));

        Assert.Equal(entry, EntryAt(new Border { DataContext = entry }));
    }

    // ---- and in the real window, where the heading is really pressed --------

    private static async Task Layout(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The shipped heading, pressed with a real pointer in a real window.
    ///
    /// Everything above tests the pane; none of it would notice the heading
    /// still being an inert ContentControl, the command binding resolving to
    /// nothing, the heading never becoming visible, or the button having no
    /// width to press. This is also the only place the REAL walk is measured —
    /// the control the pointer lands on is the presenter inside the button, not
    /// the button.
    ///
    /// It is deliberately the whole path in one test rather than four: each
    /// part is already pinned above, and what this adds is that they are
    /// connected in the window that ships.
    /// </summary>
    [AvaloniaFact]
    public async Task The_shipped_heading_is_a_button_that_picks_its_band()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-groupband-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "notes.md"), "n");

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var was = shell.ActiveTab?.CurrentPath;

        try
        {
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);

            pane.GroupBy = GroupMode.Kind;

            await Layout(window);

            var heading = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains(MainWindow.GroupHeadingClass))
                .Where(b => b.IsEffectivelyVisible)
                .Single(b => b.DataContext is FileEntry row
                             && row.FullPath == Path.Combine(root, "a.txt"));

            Assert.Equal("TXT (2)", heading.Content);

            Assert.True(heading.Bounds.Width > 0 && heading.Bounds.Height > 0,
                        "the heading has no size, so the press below would land on the row");

            // The control a pointer actually hits, which is inside the button
            // and carries the row's entry by inheritance.
            var hit = heading.GetVisualDescendants().OfType<TextBlock>().Single();

            Assert.Null(EntryAt(hit));

            // Something ELSE picked out first, and that is the whole point of
            // it: with nothing selected, the assertion between the press and
            // the release is satisfied by the very fault it is there to catch.
            pane.SelectedEntries.Add(
                pane.DetailsEntries.Single(e => e.Name == "notes.md"));

            await Layout(window);

            var at = heading.TranslatePoint(
                new Point(heading.Bounds.Width / 2, heading.Bounds.Height / 2), window);

            Assert.NotNull(at);

            window.MouseDown(at!.Value, MouseButton.Left);

            // **The press must not read as a press on empty listing space.**
            // Measured on this window before the heading was refused there:
            // e.Source is the presenter INSIDE the button, so the content
            // refusal in that walk never saw it, the walk reached the ListBox,
            // and on the press the selection went from two files to empty while
            // the rubber band came back armed. Press, move 40px, release on
            // that build and the selection ended EMPTY — the click never
            // completed, so the band was never taken either.
            Assert.Equal(["notes.md"], pane.Selection.Select(e => e.Name).ToList());

            window.MouseUp(at.Value, MouseButton.Left);

            await Layout(window);

            Assert.Equal(["a.txt", "b.txt"], pane.Selection.Select(e => e.Name).ToList());
        }
        finally
        {
            if (shell.ActiveTab is { } tab)
            {
                tab.GroupBy = GroupMode.None;

                if (was is { } back) await tab.NavigateAsync(back);

                Dispatcher.UIThread.RunJobs();
            }

            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    // ---- and the heading has to keep up with the folder ---------------------

    /// <summary>The path a watcher event about this name would carry, which is
    /// under the folder <see cref="Watched"/> navigates to rather than the
    /// "/tmp" the tests above spell by hand.</summary>
    private static string In(string name) => Path.Combine(Path.GetTempPath(), name);

    private static FileEntry Child(string name)
        => new(name, In(name), 10, DateTimeOffset.UnixEpoch, EntryFlags.None);

    /// <summary>Runs the dispatcher until a watcher burst has been drained —
    /// the queued events and the single pass below them.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }

    /// <summary>A listing grouped by kind whose folder can send news.</summary>
    private async Task<(PaneViewModel Pane, Canned Fs)> Watched(params FileEntry[] entries)
    {
        var fs = new Canned(entries);

        var pane = Own(new PaneViewModel(fs, null, null)
        {
            ViewportWidth = 1400,
            GroupBy = GroupMode.Kind,
        });

        await pane.NavigateAsync(Path.GetTempPath());
        await Settle();

        return (pane, fs);
    }

    /// <summary>
    /// A realized heading, in a tree that leads to the pane — which is the only
    /// way RowGroup can reach the map at all.
    /// </summary>
    private static (ContentControl Heading, Panel Host, Window Window) Realized(PaneViewModel pane)
    {
        var heading = new ContentControl();
        var host = new StackPanel { Children = { heading } };
        var window = new Window { DataContext = pane, Content = host };

        window.Show();

        return (heading, host, window);
    }

    /// <summary>
    /// **A heading went stale on every watcher event.** The map is rebuilt per
    /// listing AND per watcher burst, but a realized row re-read it only when
    /// its own entry changed or when it entered the tree — and an insert into
    /// the listing is neither for the rows that keep their entry.
    ///
    /// Measured here before RowGroup listened: with a.txt arriving at the head
    /// of the txt band, the pane's map moved the heading to a.txt and gave
    /// b.txt none, while the control over b.txt went on reading "TXT (2)" — two
    /// headings over one band of three, the second one wrong.
    ///
    /// A hand-built control rather than the shipped row, because the contract
    /// under test is RowGroup's: the same attached property, in the same
    /// sequence the row template uses it. The shipped one is pressed for real
    /// in the window test above.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_arriving_at_the_head_of_a_band_takes_the_heading_with_it()
    {
        var (pane, fs) = await Watched(Child("notes.md"), Child("b.txt"), Child("c.txt"));

        var (heading, _, window) = Realized(pane);

        try
        {
            Thumbnails.RowGroup.SetEntry(
                heading, pane.DetailsEntries.Single(e => e.Name == "b.txt"));

            Assert.True(heading.IsVisible);
            Assert.Equal("TXT (2)", heading.Content);

            fs.Describe(Child("a.txt"));
            fs.Raise(new FileSystemChange(ChangeKind.Added, In("a.txt")));

            await Settle();

            // The map has moved on: a.txt heads the band now, and b.txt is
            // inside it.
            Assert.Equal(new GroupHeader("TXT", 3), pane.HeaderFor(In("a.txt")));
            Assert.Null(pane.HeaderFor(In("b.txt")));

            Assert.False(heading.IsVisible,
                         "the old heading stayed, so the band had two of them");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And the mirror, which is the worse half: deleting a band's first row
    /// left the band with NO heading at all, and therefore nothing to click.
    /// </summary>
    [AvaloniaFact]
    public async Task Deleting_a_bands_first_row_hands_the_heading_to_the_next()
    {
        var (pane, fs) = await Watched(
            Child("notes.md"), Child("a.txt"), Child("b.txt"), Child("c.txt"));

        var (heading, _, window) = Realized(pane);

        try
        {
            Thumbnails.RowGroup.SetEntry(
                heading, pane.DetailsEntries.Single(e => e.Name == "b.txt"));

            Assert.False(heading.IsVisible, "b.txt is inside the band, not at its head");

            fs.Raise(new FileSystemChange(ChangeKind.Removed, In("a.txt")));

            await Settle();

            Assert.Equal(new GroupHeader("TXT", 2), pane.HeaderFor(In("b.txt")));

            Assert.True(heading.IsVisible, "the band lost its heading, and its click target");
            Assert.Equal("TXT (2)", heading.Content);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The handlers the pane is holding for its header map.</summary>
    private static int Listening(PaneViewModel pane)
        => ((Delegate?)typeof(PaneViewModel)
                .GetField("GroupingChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(pane))
           ?.GetInvocationList().Length ?? 0;

    /// <summary>
    /// One subscription per realized heading, and none once the row has gone.
    ///
    /// Rows are virtualized: one control is handed a new entry every time the
    /// panel recycles it, so a subscription per assignment would pile up — and
    /// the run goes pane -> listener -> control, so a row that never let go
    /// would still be reachable from the pane after the listing had finished
    /// with it, and would go on re-reading the map from outside the tree, where
    /// the walk reaches no pane and can only hide.
    ///
    /// Counted through the pane's own event rather than inferred: piling up is
    /// invisible from the outside, which is exactly why it needs measuring.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_taken_out_of_the_listing_stops_reading_the_map()
    {
        var (pane, _) = await Watched(Child("notes.md"), Child("a.txt"), Child("b.txt"));

        var (heading, host, window) = Realized(pane);

        try
        {
            foreach (var name in new[] { "a.txt", "notes.md", "b.txt" })
                Thumbnails.RowGroup.SetEntry(
                    heading, pane.DetailsEntries.Single(e => e.Name == name));

            Assert.Equal(1, Listening(pane));

            host.Children.Remove(heading);

            Assert.Equal(0, Listening(pane));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The RowGroup handlers on one control's tree events.
    ///
    /// Reflection because these are field-like events with no public reader,
    /// and the invocation list is the only place the count exists. Filtered to
    /// RowGroup's own handlers so nothing Avalonia hangs on a ContentControl is
    /// mistaken for one of ours.
    /// </summary>
    private static int Hooks(Control control, string name)
    {
        var field = typeof(StyledElement)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return ((Delegate?)field.GetValue(control))
               ?.GetInvocationList()
               .Count(d => d.Method.DeclaringType == typeof(Thumbnails.RowGroup)) ?? 0;
    }

    /// <summary>
    /// One hook per control per event, however many entries it is given.
    ///
    /// The attached property is set once per row REALIZATION, and a virtualized
    /// panel realizes one control over and over — so hooking without removing
    /// first leaves a control carrying one handler per entry it has ever held,
    /// each of them re-running the same work when the row re-enters the tree.
    /// Nothing on screen changes, because applying a heading twice draws the
    /// same heading; that invisibility is why this counts rather than watches.
    /// </summary>
    [AvaloniaFact]
    public async Task A_recycled_row_carries_one_hook_per_event_and_not_a_pile()
    {
        var pane = await TwoBands();

        var heading = new ContentControl();

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            Thumbnails.RowGroup.SetEntry(
                heading, pane.DetailsEntries.Single(e => e.Name == name));

        Assert.Equal(1, Hooks(heading, "AttachedToLogicalTree"));
        Assert.Equal(1, Hooks(heading, "DetachedFromLogicalTree"));
    }

    // ---- and a band big enough to need a separator --------------------------

    /// <summary>
    /// **A count of four figures is unreadable run together.** The heading
    /// spells its count the way the status bar spells its own, which is the
    /// only place the two are ever seen side by side — and every count in the
    /// tests above is 1, 2 or 3, where grouped and ungrouped are the same
    /// string and the format carries nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_band_of_a_thousand_separates_its_thousands()
    {
        var when = DateTimeOffset.Now;

        var entries = new List<FileEntry> { Entry("notes.md", directory: false, when) };

        for (var i = 0; i < 1000; i++)
            entries.Add(Entry($"f{i:0000}.txt", directory: false, when));

        var pane = await Listing(GroupMode.Kind, [.. entries]);

        var text = pane.HeaderFor("/tmp/f0000.txt")?.Text;

        Assert.Equal($"TXT ({1000:N0})", text);
        Assert.NotEqual("TXT (1000)", text);
    }

    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        private readonly Dictionary<string, FileEntry> _described = new(StringComparer.Ordinal);
        private readonly List<Action<FileSystemChange>> _watchers = [];

        /// <summary>A file a later watcher event can bring in. Nothing else
        /// stats, the way a vanished file does not.</summary>
        public void Describe(FileEntry entry) => _described[entry.FullPath] = entry;

        /// <summary>News from the folder on screen, delivered on the watcher it
        /// was given — which is the last one handed out.</summary>
        public void Raise(FileSystemChange change) => _watchers[^1](change);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(
                _described.TryGetValue(path, out var entry) ? entry : (FileEntry?)null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            _watchers.Add(onChange);
            return new Nothing();
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
