using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Double-click a heading's edge to fit the column to its widest entry**,
/// and *Size all columns to fit* for every column drawn — in a real
/// MainWindow, with real presses.
///
/// The headless window draws text with a stub — every glyph the font size
/// wide, whatever the family — so these show that the fit equals what the
/// CELLS draw, measured the same way, and in the face each cell is drawn in.
/// Real proportional widths are the console check's (see the plan).
/// </summary>
public sealed class ColumnFitWindowTests : ColumnWindow
{
    /// <summary>Two presses on a column's right edge, the second
    /// <paramref name="slip"/> pixels to the right of the first, and
    /// <paramref name="wobble"/> pixels of movement before the second
    /// release.</summary>
    private static void DoubleClick(MainWindow window, PaneViewModel pane, DetailsColumn column,
                                    double slip = 0, double wobble = 0)
    {
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(column)];
        var y = heading.TranslatePoint(new Point(0, heading.Bounds.Height / 2), window)!.Value.Y;
        var first = new Point(edge, y);
        var second = new Point(edge + slip, y);

        window.MouseMove(first);
        window.MouseDown(first, MouseButton.Left);
        window.MouseUp(first, MouseButton.Left);
        Pump();

        window.MouseMove(second);
        window.MouseDown(second, MouseButton.Left);
        Pump();

        if (wobble != 0)
        {
            window.MouseMove(second + new Point(wobble, 0));
            Pump();
            window.UpdateLayout();
        }

        window.MouseUp(second + new Point(wobble, 0), MouseButton.Left);
        Settle(window);
    }

    private static double Measure(string text, Typeface face, double size)
    {
        using var layout = new TextLayout(text, face, size, null);
        return layout.WidthIncludingTrailingWhitespace;
    }

    private static (Typeface Face, double Size) FaceOf(TextBlock text)
        => (new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize);

    /// <summary>The cell a column draws on a realised row.</summary>
    private static TextBlock Cell(Grid row, DetailsColumn column)
        => column == DetailsColumn.Name
            ? row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("filename"))
            : row.Children.OfType<TextBlock>().First(t => Grid.GetColumn(t) == ColumnFitter.Index(column));

    /// <summary>
    /// What the column should be fitted to, worked out the way a person would
    /// check it: each row's text as its cell shows it, measured in its cell's
    /// own face and size, plus the heading — and for the dates, every month.
    /// </summary>
    private static double Widest(Window window, PaneViewModel pane, DetailsColumn column)
    {
        var row = RowGrids(window, pane).First().Grid;
        var (face, size) = FaceOf(Cell(row, column));

        var word = column switch
        {
            DetailsColumn.Name => "Name",
            DetailsColumn.Type => "Type",
            DetailsColumn.Size => "Size",
            DetailsColumn.Modified => "Modified",
            _ => "Created",
        };

        var (headFace, headSize) = ColumnFitter.HeadingFace(column, Heading(window, pane));
        var widest = Measure(word + " ▾", headFace, headSize);

        IEnumerable<string> texts = column switch
        {
            DetailsColumn.Name => pane.DetailsEntries.Select(FileKind.DisplayName),
            DetailsColumn.Type => pane.DetailsEntries.Select(FileKind.Describe),
            DetailsColumn.Size => pane.DetailsEntries.Select(e => Thumbnails.RowMetadata.SizeTextNow(e, FolderSizeMode.ItemCount).Text),
            DetailsColumn.Modified => pane.DetailsEntries.Select(e => (string)FileConverters.Modified.Convert(e.LastWriteTime, typeof(string), null, CultureInfo.CurrentCulture)!)
                                         .Concat(ColumnFitter.DateShapes(CultureInfo.CurrentCulture)),
            _ => pane.DetailsEntries.Select(e => (string)FileConverters.Modified.Convert(e.CreationTime, typeof(string), null, CultureInfo.CurrentCulture)!)
                     .Concat(ColumnFitter.DateShapes(CultureInfo.CurrentCulture)),
        };

        foreach (var text in texts)
        {
            var indent = 0.0;

            if (column == DetailsColumn.Name)
            {
                var entry = pane.DetailsEntries.FirstOrDefault(e => FileKind.DisplayName(e) == text);
                indent = pane.Indents.TryGetValue(entry.FullPath ?? "", out var by) ? by : 0;
            }

            widest = Math.Max(widest, Measure(text, face, size) + indent);
        }

        return widest;
    }

    private static void NothingTrimmed(Window window, PaneViewModel pane, DetailsColumn column)
    {
        foreach (var (entry, row) in RowGrids(window, pane))
        {
            var cell = Cell(row, column);

            Assert.False(cell.TextLayout.TextLines.Any(line => line.HasCollapsed),
                         $"{entry.Name}'s {column} cell is trimmed after the fit");
        }
    }

    /// <summary>
    /// **A double-click on an edge fits the column to its left** — every row's
    /// entry and the heading, measured in the cell's own face, with the
    /// padding — at 100% and at 115%: no cell is trimmed, the listing is not
    /// sorted, and no drag is left running.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(DetailsColumn.Name, 1.0)]
    [InlineData(DetailsColumn.Type, 1.0)]
    [InlineData(DetailsColumn.Size, 1.0)]
    [InlineData(DetailsColumn.Modified, 1.0)]
    [InlineData(DetailsColumn.Created, 1.0)]
    [InlineData(DetailsColumn.Name, 1.15)]
    [InlineData(DetailsColumn.Size, 1.15)]
    [InlineData(DetailsColumn.Created, 1.15)]
    public async Task Double_clicking_an_edge_fits_the_column_to_its_left(DetailsColumn column, double zoom)
    {
        Fill(12);

        var (window, _, pane) = await Open(zoom: zoom);
        var sort = (pane.Sort, pane.SortDescending);

        // What the column must be DRAWN at, worked out here rather than by the
        // code under test: the widest text and 8 pixels at 100%, both at the
        // pane's scale, give or take the tenth the metric rounds to.
        var need = Widest(window, pane, column) + ColumnFit.Padding * pane.TextScale;

        DoubleClick(window, pane, column);

        var drawn = Math.Round(PaneScale.ColumnWidth(pane.ColumnWidths, column) * pane.TextScale, 1);

        Assert.True(drawn >= need - 0.06 && drawn <= need + 0.2,
                    $"{column} drawn {drawn} for content that needs {need}");
        Assert.Equal(sort, (pane.Sort, pane.SortDescending));
        Assert.False(pane.IsResizingColumns, "the double-click left a drag running");

        NothingTrimmed(window, pane, column);
        Assert.True(Worst(window, pane) < 0.6);
    }

    /// <summary>
    /// **A double-click that wobbles does not drag.** The second press has
    /// already started a drag by the time the double-click is known, and a
    /// 15-pixel move before the release would be added to the fitted width.
    /// </summary>
    [AvaloniaFact]
    public async Task A_double_click_that_wobbles_does_not_drag()
    {
        Fill(12);

        var (window, _, pane) = await Open();
        var expected = ColumnFit.Fitted(Widest(window, pane, DetailsColumn.Size), pane.TextScale);

        DoubleClick(window, pane, DetailsColumn.Size, wobble: 15);

        Assert.Equal(expected, PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size), 0.05);
        Assert.False(pane.IsResizingColumns);
    }

    /// <summary>
    /// **A double-click across the line is still a double-click.** Avalonia
    /// counts a second press as a double only on the element the first landed
    /// on, and the one-pixel line on the edge — the obvious place to aim — was
    /// a different element from the grip around it: a press on the line and
    /// one a pixel beside it were two clicks.
    /// </summary>
    [AvaloniaFact]
    public async Task A_double_click_across_the_edge_line_still_fits()
    {
        Fill(12);

        var (window, _, pane) = await Open();
        var grip = Grip(window, pane, "Size");
        var line = grip.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("edge"));
        var heading = Heading(window, pane);
        var y = heading.TranslatePoint(new Point(0, heading.Bounds.Height / 2), window)!.Value.Y;
        var onLine = line.TranslatePoint(new Point(0.5, 0), window)!.Value.X;
        var edge = Edges(heading, window)[Cell(DetailsColumn.Size)];

        // The press on the line and the press a pixel off go to the same
        // element, which is what lets them make a double.
        Assert.Same(window.InputHitTest(new Point(onLine, y)), window.InputHitTest(new Point(onLine + 1, y)));

        var expected = ColumnFit.Fitted(Widest(window, pane, DetailsColumn.Size), pane.TextScale);

        DoubleClick(window, pane, DetailsColumn.Size, slip: onLine + 1 - edge);

        Assert.Equal(expected, PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size), 0.05);
    }

    /// <summary>
    /// **Every row is measured, not the few on screen**: the widest name is
    /// near the end of three hundred rows, where no container was ever made.
    /// Twice: with every row read on the UI thread, and with fewer allowed
    /// there than the listing holds, when the rows on screen are fitted at
    /// once and the rest in the background, which widens the column.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Double_clicking_the_name_fits_rows_that_are_not_realised(bool inTheBackground)
    {
        if (inTheBackground) ColumnFitter.SyncRows = 50;

        for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(Root, $"a{i:000}.txt"), "a");
        File.WriteAllText(Path.Combine(Root, "zz-" + new string('w', 60) + ".txt"), "w");

        var (window, _, pane) = await Open();
        var widest = pane.DetailsEntries.Last(e => e.Name.StartsWith("zz-", StringComparison.Ordinal));

        Assert.Null(List(window, pane).ContainerFromItem(widest));

        var expected = ColumnFit.Fitted(Widest(window, pane, DetailsColumn.Name), pane.TextScale);

        DoubleClick(window, pane, DetailsColumn.Name);

        if (inTheBackground)
        {
            Assert.True(pane.ColumnWidths.Name < expected - 1, "the rows on screen already held the widest");
            Assert.NotNull(ColumnFitter.LastBackground);

            await ColumnFitter.LastBackground!;

            for (var i = 0; i < 50 && Math.Abs(pane.ColumnWidths.Name - expected) > 0.05; i++)
            {
                Pump();
                await Task.Delay(10);
            }
        }

        Assert.Equal(expected, pane.ColumnWidths.Name, 0.05);
    }

    /// <summary>
    /// **The indent of a folder opened in place is part of its rows' name
    /// column**: the widest name is a nested one, and it fits with its indent.
    /// </summary>
    [AvaloniaFact]
    public async Task The_fit_counts_the_indent_of_an_opened_folders_rows()
    {
        Directory.CreateDirectory(Path.Combine(Root, "deep"));
        File.WriteAllText(Path.Combine(Root, "deep", new string('k', 30) + ".txt"), "k");
        File.WriteAllText(Path.Combine(Root, new string('k', 30) + "x.txt"), "k");

        var (window, _, pane) = await Open();

        await pane.ToggleExpandAsync(pane.DetailsEntries.Single(e => e.Name == "deep"));
        Settle(window);

        var nested = pane.DetailsEntries.Single(e => e.FullPath.StartsWith(Path.Combine(Root, "deep") + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        Assert.True(pane.Indents[nested.FullPath] > 0);

        var expected = ColumnFit.Fitted(Widest(window, pane, DetailsColumn.Name), pane.TextScale);

        DoubleClick(window, pane, DetailsColumn.Name);

        Assert.Equal(expected, pane.ColumnWidths.Name, 0.05);
        NothingTrimmed(window, pane, DetailsColumn.Name);
    }

    /// <summary>
    /// **The heading is measured too**, with its sort arrow: a column of sizes
    /// shorter than the word "Size" fits the word.
    /// </summary>
    [AvaloniaFact]
    public async Task The_fit_counts_the_heading()
    {
        File.WriteAllText(Path.Combine(Root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(Root, "b.txt"), "b");

        var (window, _, pane) = await Open();
        var heading = Heading(window, pane);
        var text = heading.Children.OfType<Button>().Single(b => b.CommandParameter as string == "size")
                          .GetLogicalDescendants().OfType<TextBlock>().First();
        var (face, size) = FaceOf(text);

        DoubleClick(window, pane, DetailsColumn.Size);

        Assert.Equal(ColumnFit.Fitted(Measure("Size ▾", face, size), pane.TextScale),
                     PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size), 0.05);
    }

    /// <summary>
    /// **Each column is measured in the face its cells are drawn in.** The
    /// headless stub draws every family at the same width, so a fit measured
    /// in the wrong family would pass every other test here: the face the fit
    /// used is read back and compared with the realised cell's own.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_column_is_measured_in_its_cells_face()
    {
        Fill(5);

        var (window, _, pane) = await Open(zoom: 1.25);

        pane.SizeAllColumnsToFitCommand.Execute(null);
        Settle(window);

        var row = RowGrids(window, pane).First().Grid;

        foreach (var column in new[] { DetailsColumn.Name, DetailsColumn.Type, DetailsColumn.Size,
                                       DetailsColumn.Modified, DetailsColumn.Created })
        {
            var cell = Cell(row, column);
            var (face, size) = ColumnFitter.LastFaces[column];

            Assert.Equal(cell.FontFamily, face.FontFamily);
            Assert.Equal(cell.FontStyle, face.Style);
            Assert.Equal(cell.FontWeight, face.Weight);
            Assert.Equal(cell.FontStretch, face.Stretch);
            Assert.Equal(cell.FontSize, size);
        }

        Assert.NotEqual(ColumnFitter.LastFaces[DetailsColumn.Name].Face.FontFamily,
                        ColumnFitter.LastFaces[DetailsColumn.Size].Face.FontFamily);
    }

    /// <summary>
    /// **Fitting a column in a tab whose name fills leaves the name where it
    /// is**: the name is given the width it is drawn at, as a drag gives it,
    /// or it would take up whatever the fitted column gave back.
    /// </summary>
    [AvaloniaFact]
    public async Task Fitting_a_column_in_a_filling_tab_leaves_the_name_where_it_is()
    {
        Fill(12);

        var (window, _, pane) = await Open();

        Assert.True(pane.NameFills);

        var name = Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0];

        DoubleClick(window, pane, DetailsColumn.Modified);

        Assert.False(pane.NameFills);
        Assert.Equal(name, Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0], 0.5);
        Assert.Equal(VisibleRow(window, pane) / pane.TextScale, pane.ColumnWidths.Span, 0.5);
    }

    /// <summary>
    /// ***Size all columns to fit* on the headings' menu fits every column
    /// drawn in one change to the tab**, and *Reset column widths* puts them
    /// back as designed.
    /// </summary>
    [AvaloniaFact]
    public async Task Size_all_columns_to_fit_from_the_heading_menu()
    {
        Fill(12);

        var (window, _, pane) = await Open();

        // All of it on the UI thread, so the one change is the whole fit: the
        // headless stub lays text out slowly enough that 50 ms is a few dozen.
        ColumnFitter.SyncLayouts = int.MaxValue;
        ColumnFitter.SyncTime = TimeSpan.FromMinutes(1);

        var expected = new[] { DetailsColumn.Name, DetailsColumn.Type, DetailsColumn.Size, DetailsColumn.Modified, DetailsColumn.Created }
            .ToDictionary(c => c, c => ColumnFit.Fitted(Widest(window, pane, c), pane.TextScale));

        var band = Band(window, pane);
        var menu = band.ContextMenu!;

        // Opened, so its rows are bound to the pane as they are for a person.
        menu.Open(band);
        Settle(window);

        var item = menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Size all columns to _fit");
        var changes = 0;

        pane.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PaneViewModel.ColumnWidths)) changes++; };

        Assert.True(item.Command!.CanExecute(item.CommandParameter));

        item.Command.Execute(item.CommandParameter);
        menu.Close();
        Settle(window);

        Assert.Equal(1, changes);

        foreach (var (column, width) in expected)
            Assert.Equal(width, PaneScale.ColumnWidth(pane.ColumnWidths, column), 0.05);

        pane.ResetColumnWidthsCommand.Execute(null);

        Assert.Equal(ColumnWidths.Designed, pane.ColumnWidths);
    }

    /// <summary>
    /// ***Size all columns to fit* is off outside the List layout**, which
    /// draws no headings to fit — in both menus and the palette, which all
    /// run the tab's own command.
    /// </summary>
    [AvaloniaFact]
    public async Task Size_all_columns_to_fit_is_off_outside_the_list_layout()
    {
        Fill(3);

        var (window, _, pane) = await Open();

        Assert.True(pane.SizeAllColumnsToFitCommand.CanExecute(null));

        pane.View = Vaktari.Core.Session.ViewMode.Grid;
        Settle(window);

        Assert.False(pane.SizeAllColumnsToFitCommand.CanExecute(null));

        var before = pane.ColumnWidths;

        var palette = Input.Commands.Get("SizeAllColumnsToFit").Command!((ShellViewModel)window.DataContext!);

        Assert.NotNull(palette);
        Assert.False(palette!.CanExecute(null));

        palette.Execute(null);
        Settle(window);

        Assert.Equal(before, pane.ColumnWidths);
    }

    /// <summary>
    /// **A date column fits the widest date its format can write, not only
    /// the ones it holds**: a folder of today's files reads "14:02" today and
    /// "03 Oct 14:02" tomorrow, and the month names differ in width.
    /// </summary>
    [AvaloniaFact]
    public async Task A_date_column_fits_every_month_it_could_show()
    {
        File.WriteAllText(Path.Combine(Root, "today.txt"), "t");

        var (window, _, pane) = await Open();
        var row = RowGrids(window, pane).First().Grid;
        var (face, size) = FaceOf(Cell(row, DetailsColumn.Modified));
        var widestShape = ColumnFitter.DateShapes(CultureInfo.CurrentCulture).Max(s => Measure(s, face, size));
        var today = (string)FileConverters.Modified.Convert(pane.DetailsEntries.First().LastWriteTime, typeof(string), null, CultureInfo.CurrentCulture)!;

        Assert.True(widestShape > Measure(today, face, size), "today's date is as wide as any month, so this measures nothing");

        DoubleClick(window, pane, DetailsColumn.Modified);

        Assert.True(PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Modified) * pane.TextScale
                    >= widestShape + ColumnFit.Padding * pane.TextScale - 0.06);
    }

    /// <summary>
    /// **A folder still waiting for its size is fitted to the widest answer it
    /// can get**, not to the dash it shows until then.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_waiting_for_its_size_fits_the_widest_answer()
    {
        Directory.CreateDirectory(Path.Combine(Root, "waiting"));

        var (window, _, pane) = await Open();

        // Its count forgotten and nothing to count it again with: it waits,
        // the way a folder whose count is still in flight does.
        Thumbnails.RowMetadata.Provider = null;
        Thumbnails.RowMetadata.Forget([Path.Combine(Root, "waiting")]);

        Assert.True(Thumbnails.RowMetadata.SizeTextNow(pane.DetailsEntries.Single(e => e.Name == "waiting"), FolderSizeMode.ItemCount).Pending);
        var row = RowGrids(window, pane).First().Grid;
        var (face, size) = FaceOf(Cell(row, DetailsColumn.Size));


        DoubleClick(window, pane, DetailsColumn.Size);

        var widest = ColumnFitter.SizeAnswers(FolderSizeMode.ItemCount).Max(s => Measure(s, face, size));

        Assert.True(PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size) * pane.TextScale
                    >= widest + ColumnFit.Padding * pane.TextScale - 0.06,
                    $"fitted {PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size)} for an answer {widest} wide; cell now '{Cell(RowGrids(window, pane).First(r => r.Entry.Name == "waiting").Grid, DetailsColumn.Size).Text}'");
    }

    /// <summary>
    /// **What does not fit in the UI thread's share is finished in the
    /// background, and widens the column once**; navigating away first
    /// cancels it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rest_of_a_fit_finishes_in_the_background_and_navigating_cancels_it()
    {
        Fill(40);
        File.WriteAllText(Path.Combine(Root, "zz-" + new string('w', 70) + ".txt"), "w");

        var (window, _, pane) = await Open();

        // None on the UI thread: the column is fitted to its heading there,
        // and every name is left to the background.
        ColumnFitter.SyncLayouts = 0;

        var expected = ColumnFit.Fitted(Widest(window, pane, DetailsColumn.Name), pane.TextScale);

        DoubleClick(window, pane, DetailsColumn.Name);

        Assert.NotNull(ColumnFitter.LastBackground);

        await ColumnFitter.LastBackground!;

        for (var i = 0; i < 50 && Math.Abs(pane.ColumnWidths.Name - expected) > 0.05; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.Equal(expected, pane.ColumnWidths.Name, 0.05);

        // Again, and a fit of another column before it can finish: the first
        // one's finish is dropped, so the name keeps what the UI thread found.
        pane.ResetColumnWidthsCommand.Execute(null);
        Settle(window);

        DoubleClick(window, pane, DetailsColumn.Name);

        var partial = pane.ColumnWidths.Name;
        var first = ColumnFitter.LastBackground!;

        Assert.True(partial < expected - 1, "the UI thread's share already found the widest, so nothing is left to cancel");

        pane.FitColumn(DetailsColumn.Type);

        try { await first; } catch (OperationCanceledException) { }

        for (var i = 0; i < 20; i++)
        {
            Pump();
            await Task.Delay(5);
        }

        Assert.Equal(partial, pane.ColumnWidths.Name);

        // And navigating away cancels whatever is still running.
        DoubleClick(window, pane, DetailsColumn.Name);

        var token = ColumnFitter.LastToken;

        await pane.NavigateAsync(Path.Combine(Root, "folder0"));
        Settle(window);

        Assert.True(token.IsCancellationRequested, "navigating left the fit running");    }

    /// <summary>
    /// **A size or date cell narrower than its text trims it** rather than
    /// drawing it over the next column: a 40-pixel Modified cell holding
    /// "15:58" ran 22.5 pixels into Created (measured in the review). Type and
    /// the name always trimmed; these three did not.
    /// </summary>
    [AvaloniaFact]
    public async Task Size_and_date_cells_trim_rather_than_run_into_the_next_column()
    {
        Fill(12);

        var (window, _, pane) = await Open();

        pane.ColumnWidths = pane.ColumnWidths with { Size = 40, Modified = 40, Created = 40 };
        Settle(window);

        var (_, row) = RowGrids(window, pane).First(r => r.Entry.Name.StartsWith("file-011", StringComparison.Ordinal));

        foreach (var column in new[] { DetailsColumn.Size, DetailsColumn.Modified, DetailsColumn.Created })
        {
            var cell = Cell(row, column);

            // A filesystem that keeps no creation time draws an empty Created
            // cell, which has nothing to trim: measured on Fedora's ext4 in WSL.
            if (column == DetailsColumn.Created && string.IsNullOrEmpty(cell.Text)) continue;

            Assert.True(cell.TextLayout.TextLines.Any(line => line.HasCollapsed),
                        $"the {column} cell '{cell.Text}' runs past its 40 pixels instead of trimming");
            Assert.True(cell.TextLayout.WidthIncludingTrailingWhitespace <= cell.Bounds.Width + 0.5);
        }
    }

    /// <summary>
    /// **Rows the filter hides are not measured**: the listing is fitted to
    /// what it shows, as Explorer fits it.
    /// </summary>
    [AvaloniaFact]
    public async Task Rows_the_filter_hides_are_not_measured()
    {
        Fill(12);
        File.WriteAllText(Path.Combine(Root, "hidden-by-filter-" + new string('w', 70) + ".txt"), "w");

        var (window, _, pane) = await Open();

        pane.FilterText = "file-";
        await Task.Delay(250);
        Settle(window);

        Assert.DoesNotContain(pane.DetailsEntries, e => e.Name.StartsWith("hidden", StringComparison.Ordinal));

        var expected = ColumnFit.Fitted(Widest(window, pane, DetailsColumn.Name), pane.TextScale);

        DoubleClick(window, pane, DetailsColumn.Name);

        Assert.Equal(expected, pane.ColumnWidths.Name, 0.05);
    }
}