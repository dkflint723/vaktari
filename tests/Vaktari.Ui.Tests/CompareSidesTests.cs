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
    [AvaloniaFact]
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

    /// <summary>
    /// **A marked folder holding a name the copy would refuse is left out
    /// before the prompt, and the rest goes.** The plan asked only each row's
    /// own name while the engine asks every item down a folder, so "report "
    /// inside "docs" stopped the WHOLE copy — the file beside the folder too
    /// — and the person heard only afterwards (batch-0.11.2 QA). Asked of a
    /// folder off the window's thread, so the prompt comes a moment later;
    /// the status line names the folder and the name inside it.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folder_holding_a_name_the_copy_would_refuse_is_left_out_and_the_rest_goes()
    {
        var left = Folder("left");
        var right = Folder("right");
        var nested = @"\\?\" + Path.Combine(left, "docs", "report ");

        Directory.CreateDirectory(Path.Combine(left, "docs"));
        File.WriteAllText(nested, "nested");
        Write(left, "plain.txt", "plain", Noon);

        try
        {
            var shell = await Split(left, right, 2, 0);
            var window = _windows[^1];

            shell.ToggleCompareCommand.Execute(null);
            AskToCopyAcrossFromTheLeft(shell);

            await Until(() => Asking(window));

            Assert.True(Asking(window), "the prompt did not open");
            Assert.StartsWith("copy plain.txt to ", window.FindControl<TextBlock>("PromptLabel")!.Text);
            Assert.Equal("left out of the copy: docs, which holds \"report \", whose name Windows cannot open",
                         shell.Left.ActiveTab!.Status);

            await AnswerYesAndWait(window, shell.Right!.ActiveTab!, "plain.txt");

            Assert.Equal("plain", File.ReadAllText(Path.Combine(right, "plain.txt")));
            Assert.False(Directory.Exists(Path.Combine(right, "docs")), "the folder left out was copied anyway");
        }
        finally
        {
            // The plain spelling the class's own clean-up uses cannot reach it.
            File.Delete(nested);
        }
    }

    // ---- the walk inside marked folders, off the window's thread ---------------

    /// <summary>The status line while the marked folders are being looked
    /// inside: see ShellViewModel.RequestCopyAcrossAsync.</summary>
    private const string Looking = "looking inside the folders to copy…";

    /// <summary>
    /// A left side whose marked folder takes a while to walk — "docs" with
    /// thousands of empty folders in it, each one a read — beside one plain
    /// file, split against an empty right side and compared. Long enough that
    /// a second request, a closed window or a side that moves lands while the
    /// first walk is still going; a tenth of that walks in a few milliseconds.
    /// </summary>
    private async Task<(ShellViewModel Shell, List<CopyAcrossPlan> Plans, string Left, string Right)> Walking(int folders = 3000, int rightRows = 0)
    {
        var left = Folder("left");
        var right = Folder("right");
        var docs = Directory.CreateDirectory(Path.Combine(left, "docs")).FullName;

        for (var i = 0; i < folders; i++) Directory.CreateDirectory(Path.Combine(docs, $"f{i:D5}"));

        Write(left, "plain.txt", "plain", Noon);

        var shell = await Split(left, right, 2, rightRows);

        shell.ToggleCompareCommand.Execute(null);
        Pump();

        var plans = new List<CopyAcrossPlan>();
        shell.CopyAcrossRequested += (_, plan) => plans.Add(plan);

        shell.ActiveGroup = shell.Left;

        foreach (var pane in new[] { shell.Left.ActiveTab!, shell.Right!.ActiveTab! })
        {
            pane.SelectedEntries.Clear();
            pane.SelectedEntry = null;
        }

        // **Settled before anything is asked** (batch-0.11.2c QA). A pane's
        // count line is written 200 ms after the last change its watcher
        // heard, and it writes over "looking inside…" — measured on Windows: a
        // timer started by the setup's own late changes cleared the line 62 ms
        // into a 1.7 s walk. Let any such timer fire first, so that what a test
        // reads on the line is what the request wrote there.
        for (var i = 0; i < 40; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Pump();

        return (shell, plans, left, right);
    }

    /// <summary>
    /// **Asked again while the first walk is going, the person is asked
    /// once.** The second request calls the first walk off; without that, the
    /// first one's prompt arrives as well, after or over the second one's.
    /// The status says it is looking while it does, and nothing once the
    /// prompt is up with nothing left out.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Asked_again_while_looking_inside_the_person_is_asked_once()
    {
        var (shell, plans, left, _) = await Walking();
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");
        Assert.Equal(Looking, pane.Status);
        Assert.True(shell.IsComparing);

        var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        await Until(() => first.IsCompleted && second.IsCompleted && Asking(window));

        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully, "a request failed");

        var plan = Assert.Single(plans);

        Assert.Equal([Path.Combine(left, "docs"), Path.Combine(left, "plain.txt")], plan.Missing);
        Assert.Empty(plan.Withheld);
        Assert.Equal("", pane.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **A window closed while its walk is going asks nothing afterwards.**
    /// Two guards, each hiding the other (measured, batch-0.11.2b QA): closing
    /// disposes the shell, which calls the walk off; and the panes it
    /// disposes leave neither side with an active tab, so the check for sides
    /// that moved drops the plan too. With either taken out this stays green;
    /// with both, a prompt is raised for a window that has gone. A third guard
    /// now stands in front of those two — OnClosing calls the walk off at the
    /// first close — and hides them as well; the test below is the one that
    /// reddens without it.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_window_closed_while_looking_inside_asks_nothing()
    {
        var (shell, plans, _, _) = await Walking();
        var window = _windows[^1];

        var request = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(request.IsCompleted, "the marked folder was not walked off the window's thread");

        // Closed still split; Shown() closes a split the next window
        // inherits, and this class's Dispose has nothing left to do.
        window.Close();
        _windows.Remove(window);

        await Until(() => request.IsCompleted && !window.IsVisible);

        Assert.True(request.IsCompletedSuccessfully, "the request failed");
        Assert.Empty(plans);
    }

    /// <summary>
    /// **A window closed as its walk finishes asks nothing either.** The
    /// close is not one step: OnClosing calls it off, saves the session on the
    /// pool, and only then closes for real, and the shell is disposed — the
    /// walk called off — in OnClosed at the end of that. A walk that finished
    /// inside the gap was delivered on this thread while the session was
    /// still being written, saw no token cancelled and both sides as they
    /// were, and raised its prompt over a window on its way out (main's
    /// Windows CI, run 36862490124: the test above, on a runner whose
    /// session write outlasted a 3,000-folder walk). One folder makes the
    /// walk finish inside the gap every time.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_window_closed_as_its_walk_finishes_asks_nothing()
    {
        var (shell, plans, _, _) = await Walking(folders: 1);
        var window = _windows[^1];

        var request = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(request.IsCompleted, "the marked folder was not walked off the window's thread");

        window.Close();
        _windows.Remove(window);

        await Until(() => request.IsCompleted && !window.IsVisible);

        Assert.True(request.IsCompletedSuccessfully, "the request failed");
        Assert.Empty(plans);
    }

    /// <summary>
    /// **A side that moves while the walk is going gets no prompt about the
    /// folders it showed.** The plan is dropped, and the status line stops
    /// saying it is looking. Either side moving does it; here, the other one.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_side_that_moves_while_looking_inside_gets_no_prompt()
    {
        var (shell, plans, _, _) = await Walking();
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;
        var elsewhere = Folder("elsewhere");

        var request = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(request.IsCompleted, "the marked folder was not walked off the window's thread");

        var moved = shell.Right!.ActiveTab!.NavigateAsync(elsewhere);

        Assert.Equal(elsewhere, shell.Right.ActiveTab.CurrentPath);
        Assert.False(request.IsCompleted, "the walk finished before the side moved, so this proves nothing");

        await Until(() => request.IsCompleted && moved.IsCompleted);

        Assert.True(request.IsCompletedSuccessfully, "the request failed");
        Assert.Empty(plans);
        Assert.False(Asking(window), "a prompt opened for the side as it was");
        Assert.Equal("", pane.Status);
    }

    /// <summary>
    /// **A request that needs no walk calls off the walk under way too**
    /// (batch-0.11.2b QA). The person filters the listing down to the plain
    /// file while the marked folder is looked inside, and asks again: nothing
    /// shown is a folder now, so the second request is planned and prompted
    /// at once — and the first walk, finishing after, put its own prompt, for
    /// the folder no longer shown, over it. One prompt, the second's, and the
    /// line no longer says it is looking.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_request_planned_at_once_calls_off_the_walk_under_way()
    {
        var (shell, plans, left, _) = await Walking(20000);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");

        try
        {
            pane.FilterText = "plain";
            await Until(() => pane.Entries.Count == 1);

            Assert.False(first.IsCompleted, "the walk finished before the second request, so this proves nothing");

            var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

            Assert.True(second.IsCompleted, "the second request walked");

            await Until(() => first.IsCompleted);
            await Task.Delay(100);
            Pump();

            Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully, "a request failed");

            var plan = Assert.Single(plans);

            Assert.Equal([Path.Combine(left, "plain.txt")], plan.Missing);
            Assert.True(Asking(window), "the second request's prompt is not up");
            Assert.NotEqual(Looking, pane.Status);
        }
        finally
        {
            pane.FilterText = "";
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
        }
    }

    /// <summary>
    /// **The count line does not say "" over the walk** (batch-0.11.2c QA,
    /// older than this branch). The pane rewrites its status 200 ms after any
    /// change its watcher hears — a download finishing, a build writing — and
    /// that wiped "looking inside…" while the walk went on for seconds. A file
    /// arrives in the folder while the walk goes; the row arrives, the count
    /// line's moment passes, and the line still says it is looking.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_change_in_the_folder_while_looking_inside_leaves_the_line_saying_so()
    {
        var (shell, plans, left, _) = await Walking(40000);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var request = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(request.IsCompleted, "the marked folder was not walked off the window's thread");
        Assert.Equal(Looking, pane.Status);

        File.WriteAllText(Path.Combine(left, "arrived.txt"), "arrived");

        await Until(() => pane.Entries.Any(e => e.Name == "arrived.txt"));
        Assert.Contains(pane.Entries, e => e.Name == "arrived.txt");

        // Past the 200 ms the count line waits after the last change.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 500)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.False(request.IsCompleted, "the walk finished before the count line's moment, so this proves nothing");
        Assert.Equal(Looking, pane.Status);

        clock.Restart();
        while (!(request.IsCompleted && Asking(window)) && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.True(request.IsCompletedSuccessfully, "the request failed");
        Assert.Single(plans);
        Assert.Equal("", pane.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **A reload while looking inside leaves the line saying so too**
    /// (batch-0.11.2c QA, round 3). The end of a load writes the same line the
    /// count does — "" for a folder shown in full — and is held off the same
    /// way; taking that guard out left every test green. The folder is
    /// reloaded while the walk goes, as F5 or a watcher's Lost would, and the
    /// line still says it is looking once the reload is done.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_reload_while_looking_inside_leaves_the_line_saying_so()
    {
        var (shell, plans, _, _) = await Walking(40000);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var request = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(request.IsCompleted, "the marked folder was not walked off the window's thread");
        Assert.Equal(Looking, pane.Status);

        var reload = pane.RefreshAsync();

        await Until(() => reload.IsCompleted && pane.IsLoaded && !pane.IsLoading);

        Assert.False(request.IsCompleted, "the walk finished before the reload did, so this proves nothing");
        Assert.Equal(Looking, pane.Status);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!(request.IsCompleted && Asking(window)) && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.True(request.IsCompletedSuccessfully, "the request failed");
        Assert.Single(plans);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **A walk called off stops saying it is looking.** The person turns to
    /// the other side while the first walk goes and asks there, where nothing
    /// needs a walk: that side is prompted at once, and the first side's line,
    /// which said it was looking, says nothing once its walk is called off —
    /// the walk ends there now, before the check for a side that moved, which
    /// used to clear it.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_walk_called_off_by_the_other_side_stops_saying_it_is_looking()
    {
        var (shell, plans, _, right) = await Walking(20000);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;
        var there = shell.Right!.ActiveTab!;

        var only = Write(right, "there.txt", "there", Noon);
        await there.RefreshAsync();
        await Until(() => there.CompareMarks.ContainsKey(only));

        Assert.Equal(CompareMark.OnlyHere, there.CompareMarks[only]);

        shell.ActiveGroup = shell.Left;

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");
        Assert.Equal(Looking, pane.Status);

        shell.ActiveGroup = shell.Right;

        var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.True(second.IsCompleted, "the second request walked");
        Assert.False(first.IsCompleted, "the walk finished before the second request, so this proves nothing");

        await Until(() => first.IsCompleted);
        Pump();

        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully, "a request failed");
        Assert.Equal([only], Assert.Single(plans).Missing);
        Assert.Equal("", pane.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **A walk that has finished, but whose ending has not run yet, is called
    /// off too** (batch-0.11.2b QA). The window's thread is busy — a long
    /// layout, a slow handler — while the walk completes on the pool, so its
    /// continuation waits in the queue behind a second request; a completed
    /// walk is not cancelled by its token, and the first request then raised
    /// its prompt as well as the second's.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_walk_finished_behind_a_second_request_asks_nothing()
    {
        var (shell, plans, left, _) = await Walking(300);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");

        // Three hundred folders walk in milliseconds; the thread is held for
        // far longer, without running anything queued on it.
        Thread.Sleep(2000);

        Assert.False(first.IsCompleted, "the first request ended without the window's thread");

        var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        await Until(() => first.IsCompleted && second.IsCompleted && Asking(window));
        await Task.Delay(100);
        Pump();

        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully, "a request failed");

        var plan = Assert.Single(plans);

        Assert.Equal([Path.Combine(left, "docs"), Path.Combine(left, "plain.txt")], plan.Missing);
        Assert.Equal("", pane.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **Comparing switched off while the walk goes asks nothing.** The prompt
    /// copies what the marks say, and with comparing off there are none on
    /// screen; the line stops saying it is looking.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Comparing_switched_off_while_looking_inside_asks_nothing()
    {
        var (shell, plans, _, _) = await Walking();
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var request = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(request.IsCompleted, "the marked folder was not walked off the window's thread");

        shell.ToggleCompareCommand.Execute(null);
        Pump();

        Assert.False(shell.IsComparing);
        Assert.False(request.IsCompleted, "the walk finished before comparing was switched off, so this proves nothing");

        await Until(() => request.IsCompleted);

        Assert.True(request.IsCompletedSuccessfully, "the request failed");
        Assert.Empty(plans);
        Assert.False(Asking(window), "a prompt opened with comparing off");
        Assert.Equal("", pane.Status);
    }

    /// <summary>
    /// **A walk called off by a walk on the other side stops saying it is
    /// looking** (batch-0.11.2c QA). Both sides hold a marked folder that
    /// takes a while to walk. The first side asks, then the other: the second
    /// request walks too, so a walk is under way when the first one's is
    /// called off — but on the other side, which is no reason to leave the
    /// first side's line saying it is looking. The first side's line clears
    /// while the second walk is still going, and the second gets its prompt.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_walk_called_off_by_a_walk_on_the_other_side_stops_saying_it_is_looking()
    {
        // Made before the sides are shown, not after, so that the right
        // side's watcher has as little as possible to hear: see below.
        var theirs = Directory.CreateDirectory(Path.Combine(Folder("right"), "theirs")).FullName;
        for (var i = 0; i < 20000; i++) Directory.CreateDirectory(Path.Combine(theirs, $"t{i:D5}"));

        var (shell, plans, _, _) = await Walking(20000, rightRows: 1);
        var window = _windows[^1];
        var here = shell.Left.ActiveTab!;
        var there = shell.Right!.ActiveTab!;

        await Until(() => there.CompareMarks.ContainsKey(theirs));

        Assert.Equal(CompareMark.OnlyHere, there.CompareMarks[theirs]);

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");
        Assert.Equal(Looking, here.Status);

        shell.ActiveGroup = shell.Right;
        Pump();

        var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(second.IsCompleted, "the other side's request did not walk, so this proves nothing");
        Assert.Equal(Looking, there.Status);

        await Until(() => first.IsCompleted);
        Pump();

        Assert.False(second.IsCompleted, "the second walk finished with the first, so this proves nothing");
        Assert.True(first.IsCompletedSuccessfully, "the first request failed");
        Assert.Equal("", here.Status);

        // Not asserted here: that the other side's line still says it is
        // looking. Its own count line, 200 ms after anything its watcher
        // hears, writes over it (measured on Windows under load: a change to
        // the marked folder arrived after it was shown), which this test is
        // not about.

        await Until(() => second.IsCompleted && Asking(window));
        Pump();

        Assert.True(second.IsCompletedSuccessfully, "the second request failed");
        Assert.Equal([theirs], Assert.Single(plans).Missing);
        Assert.Equal("", there.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **A walk that finished, called off from the other side before its
    /// ending ran, stops saying it is looking too** (batch-0.11.2c QA). The
    /// window's thread is held while the walk completes, so its ending waits
    /// behind a request on the other side that needs no walk and is prompted
    /// at once; when the first one's ending runs, its token says it was
    /// called off, and its side's line is cleared then — nothing else would.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_finished_walk_called_off_from_the_other_side_stops_saying_it_is_looking()
    {
        var (shell, plans, _, right) = await Walking(300);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;
        var there = shell.Right!.ActiveTab!;

        var only = Write(right, "there.txt", "there", Noon);
        await there.RefreshAsync();
        await Until(() => there.CompareMarks.ContainsKey(only));

        for (var i = 0; i < 40; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        shell.ActiveGroup = shell.Left;
        Pump();

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");
        Assert.Equal(Looking, pane.Status);

        // Three hundred folders walk in milliseconds; the thread is held for
        // far longer, without running anything queued on it.
        Thread.Sleep(2000);

        Assert.False(first.IsCompleted, "the first request ended without the window's thread");

        shell.ActiveGroup = shell.Right;

        var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.True(second.IsCompleted, "the second request walked");

        await Until(() => first.IsCompleted);
        Pump();

        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully, "a request failed");
        Assert.Equal([only], Assert.Single(plans).Missing);
        Assert.Equal("", pane.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    /// <summary>
    /// **A walk called off by a walk on the same side leaves the line saying
    /// it is looking** (batch-0.11.2c QA) — the other half of the rule above:
    /// the line is the second walk's now, and the first one ending must not
    /// clear it while the second is still going. (Taking out what remembers
    /// which side a walk is looking inside for reddened nothing before this.)
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_walk_called_off_by_a_walk_on_the_same_side_leaves_the_line_saying_so()
    {
        var (shell, plans, left, _) = await Walking(20000);
        var window = _windows[^1];
        var pane = shell.Left.ActiveTab!;

        var first = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(first.IsCompleted, "the marked folder was not walked off the window's thread");

        var second = shell.RequestCopyAcrossCommand.ExecuteAsync(null);

        Assert.False(second.IsCompleted, "the second request did not walk, so this proves nothing");

        await Until(() => first.IsCompleted);
        Pump();

        Assert.False(second.IsCompleted, "the second walk finished with the first, so this proves nothing");
        Assert.True(first.IsCompletedSuccessfully, "the first request failed");
        Assert.Equal(Looking, pane.Status);

        await Until(() => second.IsCompleted && Asking(window));
        Pump();

        Assert.True(second.IsCompletedSuccessfully, "the second request failed");
        Assert.Equal([Path.Combine(left, "docs"), Path.Combine(left, "plain.txt")], Assert.Single(plans).Missing);
        Assert.Equal("", pane.Status);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
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
    /// from a menu it could only ever copy right to left. The background menu,
    /// now the listing's menu is two: comparing is about the two folders, and
    /// the row sits flat in its Analyse submenu.
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
        Assert.Contains(offer.Ancestors(), a => a.Name.LocalName == "ContextMenu"
            && (string?)a.Attribute(System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")
               == ListingMenus.Background);
    }
}
