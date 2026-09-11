using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

        await Until(() => shell.Left.ActiveTab!.Entries.Count == leftCount
                          && shell.Right.ActiveTab!.Entries.Count == rightCount);

        return shell;
    }

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

        await Until(() => leftPane.Entries.Count == 2);

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
}
