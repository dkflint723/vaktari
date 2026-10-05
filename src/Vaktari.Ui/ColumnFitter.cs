using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Fitting a tab's details columns to what they hold: a double-click on a
/// heading's edge for one column, *Size all columns to fit* for every column
/// drawn.
///
/// **What is measured is what the listing SHOWS, all of it.** Every row of
/// <see cref="PaneViewModel.DetailsEntries"/> — the rows of a folder opened in
/// place included, the rows a filter hides not, as Explorer does — and not
/// only the rows on screen, which are a few dozen of a listing that may hold a
/// hundred thousand. A folder whose rows are still being read is fitted to
/// what has arrived. The heading is measured too, with the sort arrow either
/// way round, so sorting by the column later does not trim the word it was
/// fitted to.
///
/// **In the typeface and size each cell is drawn in, read off a cell.** The
/// name, the type, the size and the two dates are not all one font — the size
/// and the dates are the monospace family — and a fit measured in the wrong
/// one passes every test whose text is the headless stub's, where every glyph
/// is the font size wide whatever the family. So the faces are taken from a
/// realised row and from the heading, and only an empty listing falls back to
/// the resources those cells take theirs from.
///
/// **What a cell will read, not only what it reads now.** The dates are fitted
/// to the widest string their format can produce in this culture as well as to
/// the data — a folder of today's files reads "14:02" now and "03 Oct 14:02"
/// tomorrow, and month names differ in width — and a folder's size cell still
/// waiting for its count is fitted to the widest answer it can get.
///
/// **On the UI thread up to a budget, the rest in the background.** See
/// <see cref="ColumnFit"/> for why most strings are never laid out; a folder
/// of near-identical names can still need many, so the first
/// <see cref="SyncLayouts"/> layouts or <see cref="SyncTime"/> are spent here
/// and whatever is left is finished on a task that widens the column once,
/// never narrows it, and is cancelled by navigating, closing the tab or
/// fitting again.
/// </summary>
internal static class ColumnFitter
{
    /// <summary>The most full layouts done on the UI thread in one fit.</summary>
    internal static int SyncLayouts { get; set; } = 256;

    /// <summary>The longest the UI thread spends laying out in one fit.</summary>
    internal static TimeSpan SyncTime { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>For tests: the typeface and size each column's rows were
    /// measured in by the last fit, and how many full layouts it did.</summary>
    internal static IReadOnlyDictionary<DetailsColumn, (Typeface Face, double Size)> LastFaces { get; private set; }
        = new Dictionary<DetailsColumn, (Typeface, double)>();

    internal static int LastExact { get; private set; }

    /// <summary>For tests: the background finish of the last fit, if it
    /// needed one.</summary>
    internal static Task? LastBackground { get; private set; }

    /// <summary>For tests: the token the last fit was given.</summary>
    internal static CancellationToken LastToken { get; private set; }

    /// <summary>The fixed slot the version-control letter takes ahead of a
    /// name inside a repository: 11 wide and 5 of gap, as the row draws it.</summary>
    private const double VcsSlot = 16;

    /// <summary>The gap ahead of the chips after a name: the compare word's
    /// 6 + 2 and Look-alike's 8.</summary>
    private const double ChipGap = 8;

    /// <summary>The margins either side of the heading's and every row's grid.</summary>
    internal const double RowMargins = 30;

    /// <summary>The most rows measured on the UI thread. A listing with more
    /// is measured there from the rows on screen, and in full in the
    /// background.</summary>
    internal static int SyncRows { get; set; } = 1000;

    private sealed record Job(
        DetailsColumn Column, Func<string, double> Exact, ColumnFit.Search Search, double Heading);

    /// <summary>
    /// Everything a fit reads from the window and the tab, read once on the
    /// UI thread, so the rest can run anywhere: the faces each column is drawn
    /// in, and the pane's maps as they stand.
    /// </summary>
    private sealed record Snapshot(
        IReadOnlyList<DetailsColumn> Columns,
        IReadOnlyDictionary<DetailsColumn, (Typeface Face, double Size)> Rows,
        IReadOnlyDictionary<DetailsColumn, (Typeface Face, double Size)> Headings,
        (Typeface Face, double Size) Tiny,
        bool IsRepository,
        IReadOnlyDictionary<string, double> Indents,
        IReadOnlySet<string> Confusable,
        IReadOnlyDictionary<string, CompareMark> Marks,
        FolderSizeMode Folders,
        CultureInfo Culture);

    /// <summary>
    /// Fits <paramref name="only"/>, or every column drawn when it is null.
    /// </summary>
    public static void Fit(
        PaneViewModel pane, Grid heading, ScrollViewer headingScroller, ListBox? rows,
        DetailsColumn? only, CancellationToken token)
    {
        var scale = pane.TextScale > 0 ? pane.TextScale : 1.0;

        LastToken = token;

        var columns = new List<DetailsColumn>();

        if (only is { } one) columns.Add(one);
        else
        {
            columns.Add(DetailsColumn.Name);
            if (pane.ShowType) columns.Add(DetailsColumn.Type);
            if (pane.ShowSize) columns.Add(DetailsColumn.Size);
            if (pane.ShowModified) columns.Add(DetailsColumn.Modified);
            if (pane.ShowCreated) columns.Add(DetailsColumn.Created);
        }

        var row = RealisedRow(rows);
        var snapshot = new Snapshot(
            columns,
            columns.ToDictionary(c => c, c => RowFace(c, row, heading)),
            columns.ToDictionary(c => c, c => HeadingFace(c, heading)),
            TinyFace(heading, row),
            pane.IsRepository,
            pane.Indents,
            pane.Confusable,
            pane.CompareMarks,
            Settings.AppSettings.Current.Views.Details.FolderSize,
            CultureInfo.CurrentCulture);

        LastFaces = snapshot.Rows;

        // Every row of the listing when there are few enough to read here; the
        // rows on screen when there are not, with the rest left to the
        // background — so the column fits what can be seen at once, and widens
        // later if something further down is wider.
        var all = pane.DetailsEntries.Where(e => e.FullPath is not null).ToList();
        var everything = all.Count <= SyncRows;
        var here = everything
            ? all
            : rows?.GetRealizedContainers().Select(c => c.DataContext).OfType<FileEntry>()
                  .Where(e => e.FullPath is not null).ToList() ?? [];

        var jobs = Jobs(snapshot, here);

        // The UI thread's share, across all the columns in turn.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var layouts = SyncLayouts;

        foreach (var job in jobs)
        {
            var before = job.Search.Exact;
            var left = SyncTime - clock.Elapsed;

            if (layouts > 0 && left > TimeSpan.Zero) job.Search.Run(job.Exact, layouts, left, token);

            layouts -= job.Search.Exact - before;
        }

        LastExact = jobs.Sum(j => j.Search.Exact);

        Choose(pane, heading, headingScroller, jobs, scale, widenOnly: false);
        heading.InvalidateMeasure();

        if (everything && jobs.All(j => j.Search.Done))
        {
            LastBackground = null;
            return;
        }

        LastBackground = Task.Run(() =>
        {
            // Every row, or the searches the UI thread could not finish.
            var rest = everything ? jobs : Jobs(snapshot, all);

            foreach (var job in rest)
                job.Search.Run(job.Exact, int.MaxValue, TimeSpan.MaxValue, token);

            return rest;
        }, token).ContinueWith(done =>
        {
            if (!done.IsCompletedSuccessfully) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;

                Choose(pane, heading, headingScroller, done.Result, scale, widenOnly: true);
                LastExact = done.Result.Sum(j => j.Search.Exact);
                heading.InvalidateMeasure();
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>One search per column over <paramref name="entries"/>, each
    /// with its heading measured. Runs on any thread.</summary>
    private static List<Job> Jobs(Snapshot snapshot, List<FileEntry> entries)
    {
        var jobs = new List<Job>();

        foreach (var column in snapshot.Columns)
        {
            var (face, size) = snapshot.Rows[column];
            var exact = Exact(face, size);
            var (headFace, headSize) = snapshot.Headings[column];
            var headExact = Exact(headFace, headSize);

            var headingWidth = HeadingWords(column).Max(word => headExact(word))
                               + (column == DetailsColumn.Name && snapshot.IsRepository ? VcsSlot : 0);

            var items = column == DetailsColumn.Name
                ? NameItems(snapshot, entries)
                : Texts(snapshot, column, entries).Select(text => (text, 0.0));

            jobs.Add(new Job(column, exact, new ColumnFit.Search(ColumnFit.Prepare(items, exact)), headingWidth));
        }

        return jobs;
    }

    /// <summary>
    /// Hands the widths to the tab: every column at its fit, or — when the
    /// background finish found something wider — only the columns it widens.
    /// </summary>
    private static void Choose(
        PaneViewModel pane, Grid heading, ScrollViewer headingScroller,
        List<Job> jobs, double scale, bool widenOnly)
    {
        var widths = new Dictionary<DetailsColumn, double>();

        foreach (var job in jobs)
        {
            var fitted = ColumnFit.Fitted(Math.Max(job.Heading, job.Search.Widest), scale);
            var current = PaneScale.ColumnWidth(pane.ColumnWidths, job.Column);

            if (widenOnly && fitted <= current) continue;

            widths[job.Column] = fitted;
        }

        if (widths.Count == 0) return;

        var drawnName = heading.ColumnDefinitions.Count > 1
            ? heading.ColumnDefinitions[1].ActualWidth / scale
            : 0;
        var visibleRow = Math.Max(0, headingScroller.Viewport.Width - RowMargins) / scale;

        pane.ChooseColumnWidths(widths, drawnName, visibleRow);
    }

    // ---- what each column holds -----------------------------------------------

    private static IEnumerable<string> HeadingWords(DetailsColumn column)
    {
        var word = column switch
        {
            DetailsColumn.Name => "Name",
            DetailsColumn.Type => "Type",
            DetailsColumn.Size => "Size",
            DetailsColumn.Modified => "Modified",
            _ => "Created",
        };

        // The arrows PaneViewModel.Glyph draws, both ways round.
        return [word + " \u25BE", word + " \u25B4"];
    }

    /// <summary>The name of every row, with what sits beside it in the
    /// name column: its indent, the version-control slot and the chips.</summary>
    private static IEnumerable<(string, double)> NameItems(Snapshot snapshot, List<FileEntry> entries)
    {
        var tiny = Exact(snapshot.Tiny.Face, snapshot.Tiny.Size);
        var lookAlike = ChipGap + tiny("Look-alike");
        var words = new Dictionary<string, double>(StringComparer.Ordinal);
        var vcs = snapshot.IsRepository ? VcsSlot : 0;

        foreach (var entry in entries)
        {
            var extra = vcs + (snapshot.Indents.TryGetValue(entry.FullPath, out var indent) ? indent : 0);

            if (FileConverters.Confusable.Convert([entry.FullPath, snapshot.Confusable], typeof(bool), null, snapshot.Culture) is true)
                extra += lookAlike;

            if (FileConverters.CompareWord.Convert([entry.FullPath, snapshot.Marks], typeof(string), null, snapshot.Culture)
                    is string { Length: > 0 } word)
            {
                if (!words.TryGetValue(word, out var width)) words[word] = width = ChipGap + tiny(word);
                extra += width;
            }

            yield return (FileKind.DisplayName(entry), extra);
        }
    }

    private static IEnumerable<string> Texts(Snapshot snapshot, DetailsColumn column, List<FileEntry> entries)
    {
        switch (column)
        {
            case DetailsColumn.Type:
                foreach (var entry in entries) yield return FileKind.Describe(entry);
                break;

            case DetailsColumn.Size:
            {
                var pending = false;

                foreach (var entry in entries)
                {
                    var (text, owed) = Thumbnails.RowMetadata.SizeTextNow(entry, snapshot.Folders);
                    pending |= owed;
                    yield return text;
                }

                if (pending)
                    foreach (var placeholder in SizeAnswers(snapshot.Folders))
                        yield return placeholder;

                break;
            }

            default:
            {
                foreach (var entry in entries)
                    yield return Date(column == DetailsColumn.Modified ? entry.LastWriteTime : entry.CreationTime, snapshot.Culture);

                foreach (var shape in DateShapes(snapshot.Culture)) yield return shape;
                break;
            }
        }
    }
    private static string Date(DateTimeOffset value, CultureInfo culture)
        => FileConverters.Modified.Convert(value, typeof(string), null, culture) as string ?? "";

    /// <summary>
    /// Every shape a date cell can take today, at its widest: the time alone,
    /// and each month this year and in an earlier one, late in the day and the
    /// month so every number has its full width.
    /// </summary>
    internal static IEnumerable<string> DateShapes(CultureInfo culture)
    {
        var now = DateTime.Now;

        yield return Date(new DateTimeOffset(new DateTime(now.Year, now.Month, now.Day, 23, 58, 0, DateTimeKind.Local)), culture);

        foreach (var year in new[] { now.Year, now.Year - 1 })
            for (var month = 1; month <= 12; month++)
                yield return Date(new DateTimeOffset(new DateTime(year, month, 28, 23, 58, 0, DateTimeKind.Local)), culture);
    }

    /// <summary>
    /// The widest answers a folder still waiting for its size can get: the
    /// most each platform's count says, or a total just short of each unit.
    /// </summary>
    internal static IEnumerable<string> SizeAnswers(FolderSizeMode folders)
    {
        if (folders == FolderSizeMode.ItemCount)
        {
            yield return "9999+ items";
            yield return "10,000+ items";
            yield break;
        }

        for (var unit = 0; unit <= 5; unit++)
            yield return ByteSize.Format((long)(1023.94 * Math.Pow(1024, unit)));
    }

    // ---- where the faces come from ---------------------------------------------

    private static Func<string, double> Exact(Typeface face, double size)
        => text =>
        {
            if (text.Length == 0) return 0;

            // Trailing spaces included: a name can end in them on Linux, and
            // the cell draws them.
            using var layout = new TextLayout(text, face, size, null);
            return layout.WidthIncludingTrailingWhitespace;
        };

    private static (Typeface, double) FaceOf(TextBlock text)
        => (new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize);

    /// <summary>A row of the listing on screen, to read the cells' faces off.</summary>
    private static Grid? RealisedRow(ListBox? rows)
        => rows?.GetRealizedContainers()
               .OfType<ListBoxItem>()
               .Where(item => item.DataContext is FileEntry { FullPath: not null })
               .Select(item => item.GetVisualDescendants().OfType<Grid>()
                                   .FirstOrDefault(g => g.ColumnDefinitions.Count > 7))
               .FirstOrDefault(grid => grid is not null);

    /// <summary>The cell in one column of a details grid: a heading's button,
    /// or a row's text.</summary>
    private static Control? Cell(Grid grid, int column)
        => grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == column && c is not Avalonia.Controls.Primitives.Thumb);

    internal static (Typeface Face, double Size) RowFace(DetailsColumn column, Grid? row, Grid heading)
    {
        if (row is not null)
        {
            var text = column == DetailsColumn.Name
                ? row.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("filename"))
                : Cell(row, Index(column)) as TextBlock;

            if (text is not null) return FaceOf(text);
        }

        // An empty listing: the resources the cells take theirs from.
        var (family, _) = HeadingFace(DetailsColumn.Name, heading);
        var mono = column is DetailsColumn.Size or DetailsColumn.Modified or DetailsColumn.Created;
        var face = mono && heading.TryFindResource("AppMonoFamily", out var found) && found is FontFamily monoFamily
            ? new Typeface(monoFamily)
            : family;
        var key = column == DetailsColumn.Name ? "FontSizeBase" : "FontSizeSmall";

        return (face, heading.TryFindResource(key, out var size) && size is double points ? points : 14);
    }

    internal static (Typeface Face, double Size) HeadingFace(DetailsColumn column, Grid heading)
    {
        var text = Cell(heading, Index(column))?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault();

        return text is null ? (Typeface.Default, 12) : FaceOf(text);
    }

    private static (Typeface, double) TinyFace(Grid heading, Grid? row)
    {
        var (face, _) = RowFace(DetailsColumn.Name, row, heading);

        return (face, heading.TryFindResource("FontSizeTiny", out var size) && size is double points ? points : 11);
    }

    /// <summary>Which grid column a details column is drawn in.</summary>
    internal static int Index(DetailsColumn column) => column == DetailsColumn.Name ? 1 : 3 + (int)column;
}
