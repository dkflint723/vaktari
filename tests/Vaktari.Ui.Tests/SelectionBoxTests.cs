using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Ticking rows with the pointer alone.
///
/// **Every route to a multi-selection went through a modifier or a drag.**
/// Ctrl+click, shift+click and the rubber band were the whole of it, so somebody
/// on a trackpad, working one-handed, or unaware that ctrl does anything could
/// pick exactly one file at a time. Both references answer this with tick
/// boxes; Dolphin ships them on and Explorer ships them off behind a View tick,
/// and this follows Explorer — the boxes take a slot out of the name column, so
/// a listing that grows one uninvited is a worse first impression than a feature
/// nobody finds.
///
/// The hard part is not the drawing. A press on a box has to add or remove ONE
/// row without disturbing the rest, and must not become a rubber band or a file
/// drag — all three of which an ordinary press on a row does.
/// </summary>
public sealed class SelectionBoxTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;

    public override void Dispose()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Boxes(bool on)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(
            before with { Views = before.Views with { ShowSelectionBoxes = on } });
    }

    private static XDocument Markup() => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

    // ---- the setting ---------------------------------------------------------

    /// <summary>
    /// **Off is the answer, and off has to be the ZERO value.** Deserialization
    /// here does not run property initializers — a key absent from settings.json
    /// arrives as default(T) — so a `= false` would be decorative and a `= true`
    /// would be a lie for every file written before the key existed. Both halves
    /// are asserted: the fresh record, and a file that has never heard of it.
    /// </summary>
    [Fact]
    public void The_boxes_are_off_until_somebody_asks_for_them()
    {
        Assert.False(new SettingsState().Views.ShowSelectionBoxes);

        var older = JsonSerializer.Deserialize(
            "{\"version\":1,\"views\":{\"themeMode\":\"FollowDesktop\"}}",
            SettingsJsonContext.Default.SettingsState);

        Assert.NotNull(older);
        Assert.False(older!.Views.ShowSelectionBoxes);
    }

    /// <summary>A pane reads the live setting, so nothing has to be threaded
    /// down to a row template that has no constructor to thread it into.</summary>
    [AvaloniaFact]
    public void A_pane_shows_the_boxes_only_while_the_setting_is_on()
    {
        var pane = Own(new PaneViewModel(new Inert()));

        Boxes(false);
        Assert.False(pane.ShowSelectionBoxes);

        Boxes(true);
        Assert.True(pane.ShowSelectionBoxes);
    }

    /// <summary>
    /// **A setting that reaches nothing until the next launch is the trap the
    /// font setting fell into for weeks.** Nothing raises a property that is
    /// computed from a static, so a row already on screen keeps the boxes it
    /// had — the save has to say so.
    /// </summary>
    [AvaloniaFact]
    public void Saving_the_setting_reaches_a_listing_already_on_screen()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        var pane = shell.ActiveTab!;
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Boxes(true);
        shell.OnSettingsChanged();

        Assert.Contains(nameof(PaneViewModel.ShowSelectionBoxes), raised);
    }

    // ---- what the heading's box says -----------------------------------------

    private PaneViewModel WithRows(int count)
    {
        var pane = Own(new PaneViewModel(new Inert()));

        for (var i = 0; i < count; i++)
            pane.Entries.Add(new FileEntry(
                $"f{i}.txt", Path.Combine(Path.GetTempPath(), $"f{i}.txt"),
                1, DateTimeOffset.UnixEpoch, EntryFlags.None));

        return pane;
    }

    /// <summary>None, some, all — the three the heading box has to draw, and the
    /// dash in the middle is the one a two-state box could not have said.</summary>
    [AvaloniaFact]
    public void The_heading_box_reads_none_some_and_all()
    {
        var pane = WithRows(3);

        Assert.False(pane.AllChosen);

        pane.DetailsSelection.Add(pane.Entries[0]);
        Assert.Null(pane.AllChosen);

        pane.DetailsSelection.Add(pane.Entries[1]);
        pane.DetailsSelection.Add(pane.Entries[2]);
        Assert.True(pane.AllChosen);
    }

    /// <summary>
    /// **An empty listing is NONE, not all.** "All of nothing" is true by
    /// arithmetic and reads, in a folder with no files in it, as a listing that
    /// has ticked itself.
    /// </summary>
    [AvaloniaFact]
    public void An_empty_listing_has_not_chosen_everything()
        => Assert.False(WithRows(0).AllChosen);

    /// <summary>The selection is the only input, so every route to one has to
    /// raise it — which is why it hangs off the single notification they all
    /// go through rather than off any one of them.</summary>
    [AvaloniaFact]
    public void Ticking_a_row_re_asks_the_heading()
    {
        var pane = WithRows(2);
        var raised = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.AllChosen)) raised++;
        };

        pane.DetailsSelection.Add(pane.Entries[0]);

        Assert.True(raised > 0, "the heading box was never told the selection had moved");
    }

    /// <summary>
    /// Clicking the heading's box when some rows are ticked means "the lot",
    /// not "none" — which is what both references do, and what the box's own
    /// value after the click would NOT have said: a three-state CheckBox cycles
    /// through indeterminate on its way round.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void The_heading_box_selects_all_from_anything_but_all(bool? chosen, bool selectsAll)
        => Assert.Equal(selectsAll, MainWindow.SelectAllFrom(chosen));

    /// <summary>And the handler asks that question rather than asking the box
    /// what it has just become.</summary>
    [Fact]
    public void The_heading_handler_asks_the_pane_rather_than_the_box()
        => Assert.Contains(
            "SelectAllFrom(pane.AllChosen)",
            RepoSource.Body(
                RepoSource.Ui("MainWindow.axaml.cs"),
                "private void OnSelectAllBoxClicked("));

    // ---- what a press on a box does ------------------------------------------

    /// <summary>
    /// A list shaped like a listing: full-width rows, each with a box in it.
    /// The box is a Panel carrying the class the handler looks for, with a
    /// Border inside it — the same two layers the row templates draw, because
    /// the press lands on the inner one and the walk has to find the outer.
    /// </summary>
    private static (Window Window, ListBox List) Build()
    {
        var list = new ListBox
        {
            Width = 400,
            ItemsSource = new[] { "a.txt", "b.txt", "c.txt" },
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<string>(
                (_, _) =>
                {
                    var box = new Panel
                    {
                        Width = 24,
                        Height = 20,
                        Background = Avalonia.Media.Brushes.Transparent,
                    };

                    // The literal, not the constant: the markup and the handler
                    // agree on a string, and a test that reads the constant
                    // would agree with whatever it was changed to.
                    box.Classes.Add("pick");
                    box.Children.Add(new Border { Width = 14, Height = 14 });

                    var row = new DockPanel { Background = Avalonia.Media.Brushes.Transparent };
                    DockPanel.SetDock(box, Dock.Left);
                    row.Children.Add(box);
                    row.Children.Add(new TextBlock { Text = "name" });

                    return row;
                },
                supportsRecycling: true),
        };

        var window = new Window { Content = list, Width = 400, Height = 300 };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        return (window, list);
    }

    private static Panel BoxIn(ListBox list, int index)
        => ((Control)list.ContainerFromIndex(index)!)
           .GetVisualDescendants().OfType<Panel>()
           .First(p => p.Classes.Contains("pick"));

    /// <summary>
    /// The press lands on the Border inside the box, never on the box itself,
    /// so finding it is a walk rather than a test of e.Source.
    /// </summary>
    [AvaloniaFact]
    public void The_box_is_found_from_the_glyph_inside_it()
    {
        var (window, list) = Build();

        try
        {
            var box = BoxIn(list, 0);
            var glyph = box.GetVisualDescendants().OfType<Border>().First();

            Assert.Same(box, MainWindow.SelectionBoxAt(glyph));

            // And a press anywhere else in the row is not a tick.
            var name = ((Control)list.ContainerFromIndex(0)!)
                .GetVisualDescendants().OfType<TextBlock>().First();

            Assert.Null(MainWindow.SelectionBoxAt(name));
            Assert.Null(MainWindow.SelectionBoxAt(list));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The half that makes a box worth having: it ADDS.</summary>
    [AvaloniaFact]
    public void Ticking_a_box_adds_that_row_and_leaves_the_others()
    {
        var (window, list) = Build();

        try
        {
            list.SelectedItems!.Add("a.txt");
            list.SelectedItems!.Add("b.txt");

            MainWindow.ToggleInSelection(list, "c.txt");

            Assert.Equal(
                ["a.txt", "b.txt", "c.txt"],
                list.SelectedItems!.Cast<string>().OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>And the other half: unticking takes out one, not the lot.</summary>
    [AvaloniaFact]
    public void Unticking_a_box_removes_only_that_row()
    {
        var (window, list) = Build();

        try
        {
            list.SelectedItems!.Add("a.txt");
            list.SelectedItems!.Add("b.txt");
            list.SelectedItems!.Add("c.txt");

            MainWindow.ToggleInSelection(list, "a.txt");

            Assert.Equal(
                ["b.txt", "c.txt"],
                list.SelectedItems!.Cast<string>().OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The press has to be claimed before the ListBox sees it, and nothing
    /// after the claim may run.** An unmodified press collapses a selection to
    /// the row under the pointer — measured, in this harness, at two rows
    /// selected going to one — and the band and drag arms sit further down the
    /// same handler.
    ///
    /// Read from the source because it is a fact about the ORDER of statements
    /// inside one event handler, which is exactly what
    /// <see cref="RepoSource.Body"/> exists for. The real-window test below
    /// covers the behaviour.
    /// </summary>
    [Fact]
    public void A_press_on_a_box_is_claimed_before_the_band_and_the_drag_are_armed()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnPointerPressedAnywhere(");

        var guard = body.IndexOf("SelectionBoxAt(e.Source)", StringComparison.Ordinal);
        var band = body.IndexOf("_bandList =", StringComparison.Ordinal);
        var drag = body.IndexOf("_dragSource =", StringComparison.Ordinal);
        var focus = body.IndexOf("FocusListIfEmptySpace(e.Source", StringComparison.Ordinal);

        Assert.True(guard > 0, "the press handler never asks whether it landed on a box");
        Assert.True(band > guard, "the rubber band is armed before the box is asked about");
        Assert.True(drag > guard, "the file drag is armed before the box is asked about");
        Assert.True(focus > guard, "the selection is cleared before the box is asked about");

        var block = body[guard..band];

        Assert.Contains("e.Handled = true;", block);
        Assert.Contains("ToggleInSelection(", block);
        Assert.Contains("ArmNothing();", block);
    }

    /// <summary>
    /// **Returning early is not the same as arming nothing.** The drag fields
    /// outlive their press — the release handler clears the band and the tab
    /// drag but never _dragSource, and the move handler clears it only on a
    /// move with the button up — so a press that returns before the arming
    /// block inherits the PREVIOUS press's row and origin, and a drag from the
    /// box would carry the old row.
    /// </summary>
    [Fact]
    public void Arming_nothing_really_forgets_the_drag()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"), "private void ArmNothing()");

        Assert.Contains("_bandList = null;", body);
        Assert.Contains("_dragSource = null;", body);
        Assert.Contains("_dragTrigger = null;", body);
    }

    // ---- the markup ----------------------------------------------------------

    private static List<XElement> Listings(XDocument doc)
        => doc.Descendants(Xaml + "ListBox")
              .Where(l => (string?)l.Attribute("ItemsSource")
                          is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                             or "{Binding GridEntries}")
              .ToList();

    /// <summary>
    /// All three layouts, discovered from the markup rather than listed here —
    /// a fix in the default view only would have looked finished, which is how
    /// the hidden-file fade and the look-alike chip both shipped half done.
    /// </summary>
    [Fact]
    public void Every_row_layout_carries_a_selection_box()
    {
        var listings = Listings(Markup());

        Assert.Equal(3, listings.Count);

        foreach (var listing in listings)
        {
            var box = Assert.Single(
                listing.Descendants(),
                e => (string?)e.Attribute("Classes") == MainWindow.SelectionBoxClass);

            Assert.Equal(
                "{Binding $parent[ListBox].((vm:PaneViewModel)DataContext).ShowSelectionBoxes}",
                (string?)box.Attribute("IsVisible"));

            // Transparent rather than unpainted: a Panel with no Background is
            // invisible to the pointer in Avalonia, so the box would draw and
            // never take a press.
            Assert.Equal("Transparent", (string?)box.Attribute("Background"));
        }
    }

    /// <summary>
    /// **The reveal is opacity, never IsVisible**, and this is the rule
    /// MarkupRulesTests.Selection_decoration_is_not_a_hit_target states in
    /// general: decoration that appears when a row becomes selected changes
    /// what the pointer hits between the two clicks of a double-click, and the
    /// gesture then never forms. The box is drawn from the moment the row is,
    /// and only its contents fade.
    /// </summary>
    [Fact]
    public void The_box_never_appears_and_disappears_under_the_pointer()
    {
        var styles = Markup().Descendants(Xaml + "Style")
            .Where(s => ((string?)s.Attribute("Selector"))
                        ?.Contains("Panel.pick", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(styles);

        foreach (var style in styles)
            Assert.All(style.Elements(Xaml + "Setter"),
                       setter => Assert.Contains(
                           (string?)setter.Attribute("Property"),
                           new[] { "Opacity", "Background", "BorderBrush" }));

        // Both states that reveal it, so a ticked row stays legible once the
        // pointer has moved on.
        Assert.Contains(styles, s => ((string)s.Attribute("Selector")!).Contains(":pointerover"));
        Assert.Contains(styles, s => ((string)s.Attribute("Selector")!).Contains(":selected"));
    }

    /// <summary>
    /// The heading's box and the rows' boxes are two grids kept in step by
    /// hand, exactly as the version-control slot is — so the numbers are read
    /// from both sites rather than trusted.
    /// </summary>
    [Fact]
    public void The_heading_box_reserves_the_same_slot_the_row_boxes_do()
    {
        var doc = Markup();

        var heading = doc.Descendants(Xaml + "CheckBox")
            .Single(c => (string?)c.Attribute(X + "Name") == "SelectAllBox");

        // Both halves must carry the numbers, or the equality below would be
        // satisfied by two absent attributes.
        Assert.NotNull((string?)heading.Attribute("Width"));
        Assert.NotNull((string?)heading.Attribute("Margin"));

        var details = Listings(doc)
            .Single(l => (string?)l.Attribute("ItemsSource") == "{Binding DetailsEntries}")
            .Descendants()
            .Single(e => (string?)e.Attribute("Classes") == MainWindow.SelectionBoxClass);

        Assert.Equal((string?)details.Attribute("Width"), (string?)heading.Attribute("Width"));
        Assert.Equal((string?)details.Attribute("Margin"), (string?)heading.Attribute("Margin"));

        // A DockPanel lays out in document order, so a box written after the
        // icon slot would sit between the icon and the name instead of ahead of
        // both.
        var cell = heading.Parent;

        Assert.NotNull(cell);
        Assert.Equal("DockPanel", cell!.Name.LocalName);
        Assert.Equal("0", (string?)cell.Attribute("Grid.Column"));
        Assert.Same(heading, cell.Elements().First());

        // And it goes when the setting does, or the heading would keep a slot
        // the rows had given back.
        Assert.Equal("{Binding ShowSelectionBoxes}", (string?)heading.Attribute("IsVisible"));

        // Three states, because "some of them" is the one a plain box could not
        // have said.
        Assert.Equal("True", (string?)heading.Attribute("IsThreeState"));
        Assert.Equal("{Binding AllChosen, Mode=OneWay}", (string?)heading.Attribute("IsChecked"));
    }

    /// <summary>Four gates — one per layout and one on the heading — so the
    /// feature really does cost nothing while it is off.</summary>
    [Fact]
    public void Nothing_is_drawn_while_the_setting_is_off()
    {
        var gated = Markup().Descendants()
            .Count(e => ((string?)e.Attribute("IsVisible"))
                        ?.Contains("ShowSelectionBoxes", StringComparison.Ordinal) == true);

        Assert.Equal(4, gated);
    }

    /// <summary>The dialog offers it, or the setting could only be reached by
    /// editing settings.json by hand — which is what the desktop-colours flag
    /// spent months being.</summary>
    [Fact]
    public void The_view_page_offers_the_setting()
        => Assert.Contains(
            XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml")).Descendants(Xaml + "CheckBox"),
            c => (string?)c.Attribute("IsChecked") == "{Binding ShowSelectionBoxes}");

    /// <summary>And the dialog carries it both ways.</summary>
    [AvaloniaFact]
    public void The_setting_survives_the_dialog()
    {
        var vm = new SettingsViewModel(new SettingsState());

        Assert.False(vm.ShowSelectionBoxes);

        vm.ShowSelectionBoxes = true;
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result.Views.ShowSelectionBoxes);

        var back = new SettingsViewModel(vm.Result);

        Assert.True(back.ShowSelectionBoxes);
    }

    // ---- and in the real window ---------------------------------------------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// **The whole finding, in the window it has to work in.** An unmodified
    /// press on a row collapses the selection to that row — this asserts the
    /// precondition first, so the test cannot pass because nothing was selected
    /// to begin with — and a press on the box does not.
    /// </summary>
    [AvaloniaFact]
    public async Task Ticking_a_box_in_the_real_window_keeps_the_rows_already_chosen()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-pick-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(root, name), name);

        // AFTER the window, never before: its constructor applies whatever is on
        // disk, so a setting made first is overwritten by the real one.
        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            Boxes(true);
            shell.OnSettingsChanged();

            await shell.ActiveTab!.NavigateAsync(root);
            Settle();

            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Settle();

            var pane = shell.ActiveTab;

            Assert.Equal(3, pane.Entries.Count);

            var list = window.GetVisualDescendants().OfType<ListBox>()
                .First(l => l.IsVisible
                            && ReferenceEquals(l.DataContext, pane)
                            && l.SelectionMode.HasFlag(SelectionMode.Multiple));

            var rows = pane.Entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

            list.SelectedItems!.Add(rows[0]);
            list.SelectedItems!.Add(rows[1]);

            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Settle();

            var third = (Control)list.ContainerFromIndex(2)!;

            var box = third.GetVisualDescendants().OfType<Panel>()
                .First(p => p.Classes.Contains("pick"));

            Assert.True(box.Bounds.Width > 0,
                        "the box has no width, so the press below would land on the row");

            var at = box.TranslatePoint(
                new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window);

            Assert.NotNull(at);

            window.MouseDown(at!.Value, MouseButton.Left);
            window.MouseUp(at.Value, MouseButton.Left);
            Settle();

            Assert.Equal(3, pane.Selection.Count);
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// **A three-state box that is told nothing keeps whatever the click cycled
    /// it to.** The binding is OneWay, so the click's own value sits on the
    /// control until the pane raises AllChosen again — and in a folder with no
    /// rows in it, SelectAll changes nothing, raises nothing, and the box was
    /// left showing a tick over an empty listing: exactly the "all of nothing"
    /// this feature refuses to say anywhere else.
    /// </summary>
    [AvaloniaFact]
    public async Task The_heading_box_does_not_tick_itself_over_an_empty_folder()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-none-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            Boxes(true);
            shell.OnSettingsChanged();

            await shell.ActiveTab!.NavigateAsync(root);
            Settle();

            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Settle();

            var pane = shell.ActiveTab;

            Assert.Empty(pane.Entries);

            var heading = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => c.Name == "SelectAllBox");

            Assert.True(heading.Bounds.Width > 0,
                        "the heading box has no width, so the press below would land elsewhere");

            var at = heading.TranslatePoint(
                new Point(heading.Bounds.Width / 2, heading.Bounds.Height / 2), window);

            Assert.NotNull(at);

            window.MouseDown(at!.Value, MouseButton.Left);
            window.MouseUp(at.Value, MouseButton.Left);
            Settle();

            Assert.Empty(pane.Selection);
            Assert.Equal(false, heading.IsChecked);
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// **The heading's answer depends on the listing as well as the selection.**
    /// It hung off the selection alone, so a file arriving in a folder where
    /// everything was ticked left the heading still saying "all" while it had
    /// become "some" — and the watcher adds rows without touching the
    /// selection.
    /// </summary>
    [AvaloniaFact]
    public void A_row_arriving_re_asks_the_heading()
    {
        var pane = Own(new PaneViewModel(new Inert()));

        for (var i = 0; i < 2; i++)
            pane.Entries.Add(new FileEntry(
                $"f{i}.txt", Path.Combine(Path.GetTempPath(), $"f{i}.txt"),
                1, DateTimeOffset.UnixEpoch, EntryFlags.None));

        pane.DetailsSelection.Add(pane.Entries[0]);
        pane.DetailsSelection.Add(pane.Entries[1]);

        Assert.True(pane.AllChosen);

        var raised = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.AllChosen)) raised++;
        };

        pane.Entries.Add(new FileEntry(
            "f2.txt", Path.Combine(Path.GetTempPath(), "f2.txt"),
            1, DateTimeOffset.UnixEpoch, EntryFlags.None));

        Assert.Null(pane.AllChosen);
        Assert.True(raised > 0, "the heading box was never told a row had arrived");
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
}
