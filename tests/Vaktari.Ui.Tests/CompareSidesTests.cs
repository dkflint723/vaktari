using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The two sides of a split compared row by row.
///
/// The window's own markup calls comparing two folders side by side the point
/// of the split, and until now that comparing was done by eye, with nothing to
/// say which rows differed. These pin what the marks say, that they
/// follow a side as it changes, and what stops them — against real folders in
/// a real window, because the marks are only as good as the listings they are
/// worked out from.
///
/// **Every window here starts with one side and leaves with one.** A window
/// closed while split writes the split into the run's session, and the next
/// window opened anywhere in the run then starts split: measured, the second
/// test here found its "open the split" closing one instead.
/// </summary>
public sealed class CompareSidesTests : OwnedViewModels
{
    private static readonly DateTime Noon = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly List<MainWindow> _windows = [];
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-compare").FullName;

    public override void Dispose()
    {
        foreach (var window in _windows)
        {
            if (window.Shell.IsSplit) window.Shell.ToggleSplit();

            window.Close();
        }

        AppSettings.Apply(_settingsBefore);

        // Only what this class built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }

        base.Dispose();
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string Write(string folder, string name, string content, DateTime written)
    {
        var path = Path.Combine(folder, name);

        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, written);

        return path;
    }

    /// <summary>A real window with one side, whatever the session last held.</summary>
    private MainWindow Shown()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 800 };

        _windows.Add(window);
        window.Show();
        Pump();

        if (window.Shell.IsSplit)
        {
            window.Shell.ToggleSplit();
            Pump();
        }

        return window;
    }

    /// <summary>A real window split over two folders, each side loaded.</summary>
    private async Task<ShellViewModel> Split(string left, string right, int leftCount, int rightCount)
    {
        var shell = Shown().Shell;

        await shell.Left.ActiveTab!.NavigateAsync(left);

        shell.ToggleSplit();
        Pump();

        await shell.Right!.ActiveTab!.NavigateAsync(right);

        await Until(() => Settled(shell.Left.ActiveTab!, leftCount) && Settled(shell.Right!.ActiveTab!, rightCount));

        return shell;
    }

    /// <summary>Read to the end with this many rows. The comparison waits for
    /// that, so a test that did not would be asserting about a comparison not
    /// yet made.</summary>
    private static bool Settled(PaneViewModel pane, int rows)
        => pane.IsLoaded && !pane.IsLoading && pane.Entries.Count == rows;

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Pump();
            await Task.Delay(5);
        }

        Pump();
    }

    [AvaloniaFact]
    public async Task Comparing_marks_each_side_against_the_other()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "same.txt", "x", Noon);
        Write(right, "same.txt", "x", Noon);
        var newer = Write(left, "notes.txt", "new", Noon.AddDays(1));
        var older = Write(right, "notes.txt", "old", Noon);
        var alone = Write(right, "only-right.txt", "x", Noon);

        var shell = await Split(left, right, 2, 3);

        shell.ToggleCompareCommand.Execute(null);

        var leftMarks = shell.Left.ActiveTab!.CompareMarks;
        var rightMarks = shell.Right!.ActiveTab!.CompareMarks;

        Assert.Equal(CompareMark.NewerHere, leftMarks[newer]);
        Assert.Equal(CompareMark.OlderHere, rightMarks[older]);
        Assert.Equal(CompareMark.OnlyHere, rightMarks[alone]);
        Assert.False(leftMarks.ContainsKey(Path.Combine(left, "same.txt")));

        // The right side is the active one after the split opens.
        Assert.Equal("Compared with the other side: 1 only here, 1 older here", shell.CompareSummary);
    }

    /// <summary>
    /// **The marks follow a side as it changes.** A comparison worked out once
    /// and left standing says what was true when it was asked, which in a file
    /// manager is a few seconds; this one is worked out again whenever either
    /// listing settles.
    /// </summary>
    [AvaloniaFact]
    public async Task The_marks_follow_a_side_as_it_changes()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "a.txt", "x", Noon);
        Write(right, "a.txt", "x", Noon);

        var shell = await Split(left, right, 1, 1);

        shell.ToggleCompareCommand.Execute(null);

        Assert.Empty(shell.Left.ActiveTab!.CompareMarks);

        var arrived = Write(left, "arrived.txt", "x", Noon);

        await Until(() => shell.Left.ActiveTab!.CompareMarks.ContainsKey(arrived));

        Assert.Equal(CompareMark.OnlyHere, shell.Left.ActiveTab!.CompareMarks[arrived]);
    }

    [AvaloniaFact]
    public async Task Closing_the_split_stops_comparing_and_takes_the_marks_away()
    {
        var left = Folder("left");
        var right = Folder("right");

        var alone = Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);

        shell.ToggleCompareCommand.Execute(null);

        Assert.True(shell.Left.ActiveTab!.CompareMarks.ContainsKey(alone));

        shell.ToggleSplit();
        Pump();

        Assert.False(shell.IsComparing);
        Assert.Empty(shell.Left.ActiveTab!.CompareMarks);
        Assert.Equal("", shell.CompareSummary);
    }

    /// <summary>Comparing is between two sides, so with one the command does
    /// nothing rather than comparing a folder with nothing.</summary>
    [AvaloniaFact]
    public void Comparing_needs_two_sides()
    {
        var shell = Shown().Shell;

        shell.ToggleCompareCommand.Execute(null);

        Assert.False(shell.IsComparing);
    }

    [AvaloniaFact]
    public async Task Select_what_differs_selects_the_marked_rows()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "same.txt", "x", Noon);
        Write(right, "same.txt", "x", Noon);
        var alone = Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 2, 1);

        shell.ToggleCompareCommand.Execute(null);

        var pane = shell.Left.ActiveTab!;

        pane.SelectDifferencesCommand.Execute(null);
        Pump();

        Assert.Equal([alone], pane.Selection.Select(e => e.FullPath));
    }

    /// <summary>
    /// **Hidden files only when both sides show them.** A side hiding them has
    /// none in its listing, so a hidden file on the side that shows them is not
    /// marked "only here" when the other folder may well have one too.
    /// </summary>
    [AvaloniaFact]
    public async Task Hidden_files_are_compared_only_when_both_sides_show_them()
    {
        var left = Folder("left");
        var right = Folder("right");

        var hidden = Write(left, ".secret", "x", Noon);

        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Write(left, "a.txt", "x", Noon);
        Write(right, "a.txt", "x", Noon);

        var shell = await Split(left, right, 1, 1);
        var leftPane = shell.Left.ActiveTab!;
        var rightPane = shell.Right!.ActiveTab!;

        leftPane.ShowHidden = true;
        rightPane.ShowHidden = false;

        await Until(() => Settled(leftPane, 2));

        shell.ToggleCompareCommand.Execute(null);

        Assert.False(leftPane.CompareMarks.ContainsKey(hidden));

        rightPane.ShowHidden = true;

        await Until(() => leftPane.CompareMarks.ContainsKey(hidden));

        Assert.Equal(CompareMark.OnlyHere, leftPane.CompareMarks[hidden]);
    }

    /// <summary>
    /// **A side showing another tab is showing another folder**, so the
    /// comparison moves to that tab, and the tab left behind loses its marks
    /// rather than keeping ones about a folder nobody is comparing.
    /// </summary>
    [AvaloniaFact]
    public async Task Showing_another_tab_compares_that_tab()
    {
        var left = Folder("left");
        var right = Folder("right");
        var elsewhere = Folder("elsewhere");

        var alone = Write(left, "only-left.txt", "x", Noon);
        var there = Write(elsewhere, "elsewhere.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);

        shell.ToggleCompareCommand.Execute(null);

        var first = shell.Left.ActiveTab!;

        Assert.True(first.CompareMarks.ContainsKey(alone));

        shell.Left.AddTab(elsewhere);
        Pump();

        var second = shell.Left.ActiveTab!;

        await Until(() => second.CompareMarks.ContainsKey(there));

        Assert.NotSame(first, second);
        Assert.Empty(first.CompareMarks);
        Assert.Equal(CompareMark.OnlyHere, second.CompareMarks[there]);
    }

    /// <summary>
    /// **An empty folder is a listing too.** A side that opens one has none of
    /// what the other side has, so everything over there is only there.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_an_empty_folder_compares_the_sides_again()
    {
        var left = Folder("left");
        var right = Folder("right");
        var empty = Folder("empty");

        Write(left, "shared.txt", "x", Noon);
        var there = Write(right, "shared.txt", "x", Noon);

        var shell = await Split(left, right, 1, 1);
        var rightPane = shell.Right!.ActiveTab!;

        shell.ToggleCompareCommand.Execute(null);

        Assert.Empty(rightPane.CompareMarks);

        await shell.Left.ActiveTab!.NavigateAsync(empty);
        await Until(() => rightPane.CompareMarks.ContainsKey(there));

        Assert.Equal(CompareMark.OnlyHere, rightPane.CompareMarks[there]);
    }

    /// <summary>
    /// All three layouts carry the word. A mark drawn in one layout and not
    /// the others would say, in the others, that the rows are the same.
    /// </summary>
    [Fact]
    public void Every_layout_carries_the_comparison_word()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        Assert.Equal(3, markup.Split("FileConverters.CompareWord").Length - 1);
    }

    /// <summary>The word a row carries for each mark, and none for a row that
    /// looks the same on both sides.</summary>
    [Fact]
    public void A_row_says_what_the_comparison_found()
    {
        var marks = new Dictionary<string, CompareMark>(StringComparer.Ordinal)
        {
            ["/a"] = CompareMark.OnlyHere,
            ["/b"] = CompareMark.NewerHere,
            ["/c"] = CompareMark.OlderHere,
            ["/d"] = CompareMark.Differs,
        };

        string Word(string path)
            => (string)FileConverters.CompareWord.Convert(
                [path, marks], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!;

        Assert.Equal("Only here", Word("/a"));
        Assert.Equal("Newer", Word("/b"));
        Assert.Equal("Older", Word("/c"));
        Assert.Equal("Different", Word("/d"));
        Assert.Equal("", Word("/same"));
    }

    /// <summary>
    /// **Two folders, or no marks.** Search results, the bin and the list of
    /// drives are views whose rows come from anywhere, so a name matched
    /// against a folder says nothing about either: on whichever side the
    /// view is, and for as long as it is showing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_side_showing_a_view_rather_than_a_folder_is_not_compared()
    {
        var left = Folder("left");
        var right = Folder("right");

        var alone = Write(right, "only-right.txt", "x", Noon);

        var shell = await Split(left, right, 0, 1);
        var leftPane = shell.Left.ActiveTab!;
        var rightPane = shell.Right!.ActiveTab!;

        shell.ToggleCompareCommand.Execute(null);

        Assert.True(rightPane.CompareMarks.ContainsKey(alone));

        await leftPane.NavigateAsync(VirtualPaths.Computer);
        await Until(() => rightPane.CompareMarks.Count == 0);

        Assert.Empty(rightPane.CompareMarks);
        Assert.Empty(leftPane.CompareMarks);
        Assert.Equal("Not compared: one side is a view, not a folder", shell.CompareSummary);

        await leftPane.NavigateAsync(left);
        await Until(() => rightPane.CompareMarks.ContainsKey(alone));

        Assert.True(rightPane.CompareMarks.ContainsKey(alone), "leaving the view did not compare again");

        await rightPane.NavigateAsync(VirtualPaths.Computer);
        await Until(() => rightPane.CompareMarks.Count == 0);

        Assert.Empty(rightPane.CompareMarks);
        Assert.Empty(leftPane.CompareMarks);
    }

    // ---- copying across ------------------------------------------------------

    /// <summary>
    /// Asks for copying across from the left side. Nothing is left selected
    /// on either side first: Enter that missed the prompt would open the
    /// selection, and these rows are real files.
    /// </summary>
    private static void AskToCopyAcrossFromTheLeft(ShellViewModel shell)
    {
        shell.ActiveGroup = shell.Left;

        foreach (var pane in new[] { shell.Left.ActiveTab, shell.Right?.ActiveTab })
        {
            if (pane is null) continue;

            pane.SelectedEntries.Clear();
            pane.SelectedEntry = null;
        }

        // Settled before asking, so the window's own focus work for the side
        // just made active cannot land after the prompt has taken the keyboard.
        Pump();

        shell.RequestCopyAcrossCommand.Execute(null);
        Pump();
    }

    private bool Asking(MainWindow window) => window.FindControl<Border>("PromptBar")!.IsVisible;

    /// <summary>
    /// Answers the prompt the way a person does, with Enter; waits for the copy
    /// it starts on the side receiving it to finish, and then for that side's
    /// refresh to select what arrived. The refresh is posted when the copy
    /// completes, and one that lands after the window has closed is the leak
    /// <see cref="OwnedViewModels"/> describes.
    /// </summary>
    private static async Task AnswerYesAndWait(MainWindow window, PaneViewModel receiving, params string[] arriving)
    {
        Assert.True(window.FindControl<Button>("PromptConfirm")!.IsFocused,
                    "the prompt's button does not have the keyboard, so Enter would go elsewhere");

        IOperationHandle? started = null;

        void Started(object? sender, IOperationHandle handle) => started = handle;

        receiving.OperationStarted += Started;

        try
        {
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            await Until(() => started is not null);

            Assert.True(started is not null, "no copy started on the side it copies to");

            await started!.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            Pump();

            bool Arrived() => arriving.All(name => receiving.SelectedEntries.Any(e => e.Name == name));

            await Until(Arrived);

            Assert.True(Arrived(), "the side it copied to never refreshed to select what arrived");
        }
        finally
        {
            receiving.OperationStarted -= Started;
        }
    }

    /// <summary>
    /// **Copying across brings the other side up to date, and no further.**
    /// What it lacks arrives and what is older there is replaced; a file that
    /// is newer there is not touched.
    /// </summary>
    [AvaloniaFact]
    public async Task Copying_across_brings_the_other_side_up_to_date()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "only-left.txt", "mine", Noon);
        Write(left, "notes.txt", "new", Noon.AddDays(1));
        Write(right, "notes.txt", "old", Noon);
        Write(left, "plan.txt", "stale", Noon);
        Write(right, "plan.txt", "fresh", Noon.AddDays(1));

        var shell = await Split(left, right, 3, 2);
        var window = _windows[^1];

        shell.ToggleCompareCommand.Execute(null);
        AskToCopyAcrossFromTheLeft(shell);

        Assert.True(Asking(window), "the prompt did not open");

        // The destination by its path, cut in the middle when long, so it
        // still ends with the folder's own name.
        var asked = window.FindControl<TextBlock>("PromptLabel")!.Text!;

        Assert.StartsWith("copy 2 items to ", asked);
        Assert.EndsWith(Path.DirectorySeparatorChar + "right? 1 of them replaces an older file there for good", asked);

        await AnswerYesAndWait(window, shell.Right!.ActiveTab!, "only-left.txt", "notes.txt");

        Assert.Equal("mine", File.ReadAllText(Path.Combine(right, "only-left.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(right, "notes.txt")));
        Assert.Equal("fresh", File.ReadAllText(Path.Combine(right, "plan.txt")));
    }

    /// <summary>
    /// **What turned up there after the prompt is left alone, and said to have
    /// been.** Each clash is decided when the copy reaches it, against what is
    /// there then, and the prompt said this name was missing: it offered to
    /// replace nothing of that name.
    ///
    /// Answered only once the comparison has seen the new file, so a copy that
    /// planned again at the answer, and would then replace it, fails here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_that_turned_up_there_after_the_prompt_is_left_alone()
    {
        var left = Folder("left");
        var right = Folder("right");

        var mine = Write(left, "report.pdf", "mine", Noon.AddDays(1));
        Write(left, "extra.txt", "x", Noon);

        var shell = await Split(left, right, 2, 0);
        var window = _windows[^1];

        shell.ToggleCompareCommand.Execute(null);
        AskToCopyAcrossFromTheLeft(shell);

        Assert.True(Asking(window), "the prompt did not open");

        Write(right, "report.pdf", "theirs", Noon);

        await Until(() => shell.Left.ActiveTab!.CompareMarks.TryGetValue(mine, out var mark)
                          && mark == CompareMark.NewerHere);

        Assert.Equal(CompareMark.NewerHere, shell.Left.ActiveTab!.CompareMarks[mine]);

        await AnswerYesAndWait(window, shell.Right!.ActiveTab!, "extra.txt");

        Assert.Equal("theirs", File.ReadAllText(Path.Combine(right, "report.pdf")));

        const string said = "left report.pdf alone: it changed after the prompt";

        await Until(() => shell.OperationStatus == said);

        Assert.Equal(said, shell.OperationStatus);
    }

    /// <summary>Asked for while the sides are not being compared, it compares
    /// them first, rather than calling nothing newer on sides nobody compared.</summary>
    [AvaloniaFact]
    public async Task Copying_across_compares_the_sides_first()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);

        AskToCopyAcrossFromTheLeft(shell);

        Assert.True(shell.IsComparing);
        Assert.True(Asking(_windows[^1]), "the prompt did not open");
    }

    /// <summary>A file only the other side has is not this side's to copy,
    /// so with nothing newer or missing here it says so and asks nothing.</summary>
    [AvaloniaFact]
    public async Task With_nothing_newer_or_missing_it_says_so_and_asks_nothing()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "same.txt", "x", Noon);
        Write(right, "same.txt", "x", Noon);
        Write(right, "only-right.txt", "x", Noon);

        var shell = await Split(left, right, 1, 2);

        AskToCopyAcrossFromTheLeft(shell);

        Assert.False(Asking(_windows[^1]));
        Assert.Equal("nothing here is newer or missing on the other side", shell.Left.ActiveTab!.Status);
    }

    [AvaloniaFact]
    public async Task Copying_across_needs_a_folder_on_both_sides()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);

        await shell.Right!.ActiveTab!.NavigateAsync(VirtualPaths.Computer);
        Pump();

        AskToCopyAcrossFromTheLeft(shell);

        Assert.False(Asking(_windows[^1]));
        Assert.Equal("cannot copy across: one side is a view, not a folder", shell.Left.ActiveTab!.Status);

        // Refused before comparing was switched on, so the window is as it was.
        Assert.False(shell.IsComparing);
    }

    [AvaloniaFact]
    public void Copying_across_needs_two_sides()
    {
        var window = Shown();
        var shell = window.Shell;

        shell.RequestCopyAcrossCommand.Execute(null);
        Pump();

        Assert.False(shell.IsComparing);
        Assert.False(Asking(window));
        Assert.Equal("there is no other side to copy to — split the window first", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// **The prompt keeps the keyboard after a click elsewhere.** While its
    /// button has the focus the button answers Enter itself; once a click has
    /// moved the focus to the listing, the window answers every key, and it
    /// answers for this prompt only if it counts it among the questions
    /// waiting for an answer.
    /// </summary>
    [AvaloniaFact]
    public async Task Escape_puts_the_prompt_away_after_a_click_on_the_listing()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);
        var window = _windows[^1];

        AskToCopyAcrossFromTheLeft(shell);

        Assert.True(Asking(window), "the prompt did not open");

        var listing = window.GetVisualDescendants()
            .OfType<ListBox>()
            .First(l => ReferenceEquals(l.DataContext, shell.Left.ActiveTab));

        listing.Focus();
        Pump();

        Assert.False(window.FindControl<Button>("PromptConfirm")!.IsFocused, "the listing did not take the focus");
        Assert.True(Asking(window), "moving the focus put the prompt away by itself");

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();

        Assert.False(Asking(window), "Escape reached the listing instead of the prompt");
    }

    /// <summary>
    /// **A side still loading is not compared, and nothing is copied across
    /// it.** Its rows so far are not its folder, so marks worked out against
    /// them, or against the folder it has just left, would be wrong. The load
    /// is held open by hand: a real folder passes through it too quickly to
    /// catch.
    /// </summary>
    [AvaloniaFact]
    public async Task A_side_still_loading_is_not_compared_or_copied_across()
    {
        var left = Folder("left");
        var right = Folder("right");

        var alone = Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);
        var leftPane = shell.Left.ActiveTab!;
        var rightPane = shell.Right!.ActiveTab!;

        shell.ToggleCompareCommand.Execute(null);

        Assert.True(leftPane.CompareMarks.ContainsKey(alone));

        rightPane.IsLoading = true;

        try
        {
            Assert.Empty(leftPane.CompareMarks);
            Assert.Equal("Not compared: one side is still loading", shell.CompareSummary);

            AskToCopyAcrossFromTheLeft(shell);

            Assert.False(Asking(_windows[^1]));
            Assert.Equal("cannot copy across: one side is still loading", leftPane.Status);
        }
        finally
        {
            rightPane.IsLoading = false;
        }

        Assert.True(leftPane.CompareMarks.ContainsKey(alone), "the marks did not come back once it had loaded");

        // Not loaded at all is not loaded to the end either.
        rightPane.IsLoaded = false;

        try
        {
            Assert.Empty(leftPane.CompareMarks);
        }
        finally
        {
            rightPane.IsLoaded = true;
        }
    }

    /// <summary>
    /// **A side that could not be read is not compared.** It has no rows, so
    /// every row on the other side would read "only here", and copying across
    /// from there would offer to copy the lot into a folder that would not
    /// open. The error is set by hand, as a load that fails sets it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_side_that_could_not_be_read_is_not_compared_or_copied_across()
    {
        var left = Folder("left");
        var right = Folder("right");

        var alone = Write(left, "only-left.txt", "x", Noon);

        var shell = await Split(left, right, 1, 0);
        var leftPane = shell.Left.ActiveTab!;
        var rightPane = shell.Right!.ActiveTab!;

        shell.ToggleCompareCommand.Execute(null);

        rightPane.LoadError = "you do not have permission to open that folder";

        try
        {
            Assert.Empty(leftPane.CompareMarks);
            Assert.Equal("Not compared: one side could not be read", shell.CompareSummary);

            AskToCopyAcrossFromTheLeft(shell);

            Assert.False(Asking(_windows[^1]));
            Assert.Equal("cannot copy across: one side could not be read", leftPane.Status);
        }
        finally
        {
            rightPane.LoadError = "";
        }

        Assert.True(leftPane.CompareMarks.ContainsKey(alone), "the marks did not come back once it could be read");
    }

    /// <summary>
    /// **A folder the other side is inside is left out.** One side showing a
    /// folder within the other marks that folder "only here", and sent across
    /// it would be a folder copied into itself, which both engines refuse for
    /// the whole copy.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_holds_the_other_side_is_left_out_of_the_copy()
    {
        var outer = Folder("outer");
        var inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;

        Write(outer, "a.txt", "x", Noon);

        var shell = await Split(outer, inner, 2, 0);
        var window = _windows[^1];

        shell.ToggleCompareCommand.Execute(null);
        AskToCopyAcrossFromTheLeft(shell);

        Assert.True(Asking(window), "the prompt did not open");
        Assert.StartsWith("copy a.txt to ", window.FindControl<TextBlock>("PromptLabel")!.Text);
        Assert.Equal("left out of the copy: inner, which holds the other side", shell.Left.ActiveTab!.Status);

        await AnswerYesAndWait(window, shell.Right!.ActiveTab!, "a.txt");

        Assert.True(File.Exists(Path.Combine(inner, "a.txt")));
    }

    /// <summary>A filter narrows what is copied as it narrows what is seen.</summary>
    [AvaloniaFact]
    public async Task Copying_across_takes_only_the_rows_the_filter_shows()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(left, "only-left.txt", "x", Noon);
        Write(left, "only-left.jpg", "x", Noon);

        var shell = await Split(left, right, 2, 0);
        var leftPane = shell.Left.ActiveTab!;

        leftPane.FilterText = "*.txt";

        await Until(() => leftPane.Entries.Count == 1);

        AskToCopyAcrossFromTheLeft(shell);

        Assert.True(Asking(_windows[^1]), "the prompt did not open");
        Assert.StartsWith("copy only-left.txt to ", _windows[^1].FindControl<TextBlock>("PromptLabel")!.Text);
    }

    /// <summary>
    /// **The summary counts what is missing here.** With an empty folder on
    /// the active side, every row on the other side reads "only here", and
    /// the bar said "nothing differs".
    /// </summary>
    [AvaloniaFact]
    public async Task The_summary_counts_what_is_missing_here()
    {
        var left = Folder("left");
        var right = Folder("right");

        Write(right, "a.txt", "x", Noon);
        Write(right, "b.txt", "x", Noon);

        var shell = await Split(left, right, 0, 2);

        shell.ToggleCompareCommand.Execute(null);
        shell.ActiveGroup = shell.Left;

        Assert.Equal("Compared with the other side: 2 missing here", shell.CompareSummary);
    }

    /// <summary>
    /// **Copying across is offered by the listing's menu, which each half of a
    /// split carries.** It was in the view-options flyout, whose button only
    /// the right half shows and whose press makes the right half active, so
    /// from a menu it could only ever copy right to left.
    /// </summary>
    [Fact]
    public void Copying_across_is_on_the_menu_both_halves_carry()
    {
        var markup = System.Xml.Linq.XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var offers = markup.Descendants()
            .Where(e => ((string?)e.Attribute("Command"))?.Contains("RequestCopyAcrossCommand") == true)
            .ToList();

        var offer = Assert.Single(offers);

        Assert.Equal("MenuItem", offer.Name.LocalName);
        Assert.Contains(offer.Ancestors(), a => a.Name.LocalName == "ContextMenu");
    }
}
