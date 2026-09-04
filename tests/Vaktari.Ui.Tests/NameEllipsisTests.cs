using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a name that does not fit its cell gives up first.
///
/// **It gave up the extension, which is the part that says what the file is.**
/// Every listing row asked for CharacterEllipsis, which fills from the left and
/// stops, so a 160px name cell in the shipped details row drew
/// "quarterly-forecast-final-revision.xlsx" as "quarterly-…" — a row that no
/// longer says whether it is a spreadsheet, a photograph or an installer. The
/// column that would have said so is Type,
/// which has no initialiser behind it (<c>PaneViewModel.ShowTypeColumn</c>) and
/// exists in the details layout alone, so for anybody who has not turned it on
/// the extension was the only thing in the window carrying that fact.
///
/// The delete confirmation had already reached this conclusion and elides a
/// name in the middle for it — <c>Confirmations.Elide</c>, "so the extension
/// survives" — but it counts CHARACTERS, which a dialog can afford and a
/// proportional-font column cannot: "WWW" and "iii" are the same three
/// characters and nothing like the same width. So the cut had to move into the
/// text layout, which is the only thing that knows.
/// </summary>
public sealed class NameEllipsisTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private const string Long = "quarterly-forecast-final-revision.xlsx";

    /// <summary>
    /// What the layout actually put on screen, rather than what was handed to
    /// it: a trimmed line's runs are the head, the ellipsis and whatever tail
    /// survived, so reading them back is reading the drawn text.
    /// </summary>
    private static string Drawn(TextBlock block)
    {
        var text = "";

        foreach (var line in block.TextLayout.TextLines)
            foreach (var run in line.TextRuns)
                text += run.Text.ToString();

        return text;
    }

    private static string Drawn(
        string name, double width, TextTrimming? trimming = null, double size = 13)
    {
        var block = new TextBlock
        {
            Text = name,
            FontSize = size,
            TextTrimming = trimming ?? NameEllipsis.KeepingTheExtension,
        };

        block.Measure(new Size(width, 40));
        block.Arrange(new Rect(0, 0, width, 40));

        return Drawn(block);
    }

    // ---- every listing asks for it ------------------------------------------

    /// <summary>
    /// Discovered from the markup rather than listed here, so a fourth layout
    /// added later is held to this without anybody remembering — the same way
    /// the ghosting, the row name and the name tooltip are.
    ///
    /// The tile is named as the one exception rather than left out of the
    /// sweep, so that turning it over to the middle cut fails here and has to
    /// be argued for. Measured: the only line a tile collapses is a wrapped
    /// fragment, so a middle cut there elides around a dot that is not the
    /// extension. See its template.
    /// </summary>
    [AvaloniaFact]
    public void Every_single_line_listing_cuts_a_name_in_the_middle()
    {
        var listings = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ListBox")
            .Where(l => (string?)l.Attribute("ItemsSource")
                        is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                           or "{Binding GridEntries}")
            .ToDictionary(
                l => (string)l.Attribute("ItemsSource")!,
                l => (string?)l.Descendants(Xaml + "TextBlock")
                        .Single(t => ((string?)t.Attribute("Text"))
                                     ?.Contains("FileConverters.DisplayName",
                                                StringComparison.Ordinal) == true)
                        .Attribute("TextTrimming"));

        // A guard, not decoration: a renamed listing must fail here rather than
        // silently drop out of the checks below.
        Assert.Equal(3, listings.Count);

        var trailing = listings
            .Where(x => x.Key != "{Binding GridEntries}")
            .Where(x => x.Value?.Contains("NameEllipsis.KeepingTheExtension",
                                          StringComparison.Ordinal) != true)
            .Select(x => x.Key)
            .ToList();

        Assert.True(trailing.Count == 0,
            "these listings cut a name at the end, which is where its extension is: "
            + string.Join(", ", trailing));

        Assert.Equal("CharacterEllipsis", listings["{Binding GridEntries}"]);
    }

    // ---- and what it draws --------------------------------------------------

    [AvaloniaFact]
    public void A_name_too_long_for_its_cell_keeps_its_extension()
    {
        var drawn = Drawn(Long, 140);

        Assert.EndsWith(".xlsx", drawn, StringComparison.Ordinal);
        Assert.Contains('…', drawn);
        Assert.StartsWith("q", drawn, StringComparison.Ordinal);
        Assert.True(drawn.Length < Long.Length, "nothing was trimmed at all: " + drawn);
    }

    /// <summary>
    /// A guard, and the fault itself: the same cell, the same width, the
    /// trimming this replaced. It cannot go red for a mistake in NameEllipsis —
    /// it is Avalonia's own trailing ellipsis — but it is what the fix is
    /// measured against, and it fails if the framework ever starts doing this
    /// on its own.
    /// </summary>
    [AvaloniaFact]
    public void The_trimming_it_replaced_threw_the_extension_away()
    {
        var drawn = Drawn(Long, 140, TextTrimming.CharacterEllipsis);

        Assert.EndsWith("…", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain(".xlsx", drawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// The listing's own extension rule, borrowed rather than copied: a leading
    /// dot begins a name rather than an extension, so ".gitignore" is a file
    /// called .gitignore and has nothing at the end worth saving.
    ///
    /// **A GUARD on the borrow, not a pin on the rule.** The rule lives in
    /// FileEntry.ExtensionOf and is pinned there — FileKindTests.A_dotfile_is_a
    /// _file_not_a_gitignore_file dies on its `dot &lt;= 0`. Here it cannot be:
    /// treating the leading dot as an extension makes the "extension" nearly
    /// the whole name, which leaves no room for a head, and the too-narrow
    /// fallback below then draws exactly what this asserts. Measured.
    /// </summary>
    [AvaloniaFact]
    public void A_dotfile_is_cut_at_the_end_like_any_other_word()
    {
        var drawn = Drawn(".gitignore-with-a-very-long-tail-indeed", 140);

        Assert.EndsWith("…", drawn, StringComparison.Ordinal);
        Assert.StartsWith(".gitign", drawn, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_name_with_no_extension_at_all_is_cut_at_the_end()
    {
        var drawn = Drawn("Some Folder Without An Extension At All", 140);

        Assert.EndsWith("…", drawn, StringComparison.Ordinal);
        Assert.StartsWith("Some", drawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// **An ellipsis with nothing in front of it says less than three letters
    /// do.** A cell too narrow to hold a head, the ellipsis AND the extension
    /// has nothing to gain from the middle, and a listing is scanned down the
    /// left edge — so the narrow case keeps the beginning and gives up the
    /// extension rather than the other way round.
    /// </summary>
    [AvaloniaFact]
    public void A_cell_too_narrow_for_both_keeps_the_beginning()
    {
        // Both widths reach the same guard, by different arithmetic, and
        // that is the point of running both: at 80 the room left over is
        // measured at 2px and answers character 0; at 60 it is measured at
        // -18px, and a negative distance answers character 0 as well. That
        // measurement is why there is no separate `room <= 0` guard.
        foreach (var width in new double[] { 60, 80 })
        {
            var drawn = Drawn(Long, width);

            Assert.EndsWith("…", drawn, StringComparison.Ordinal);
            Assert.StartsWith("qua", drawn, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void A_name_that_fits_is_left_alone()
        => Assert.Equal("short.txt", Drawn("short.txt", 400));

    /// <summary>
    /// **The ellipsis has a width, and something has to pay for it.** The head
    /// is measured against the room left over once the ellipsis and the
    /// extension are both paid for; charging only the extension leaves the head
    /// one glyph too long, and the leading-prefix collapse balances the books
    /// out of the TAIL — which is the extension, which is the whole point.
    ///
    /// The ellipsis is not in the line being collapsed, so it has to be laid
    /// out separately to be measured.
    /// </summary>
    [AvaloniaFact]
    public void The_ellipsis_is_paid_for_before_the_head_is_measured()
    {
        // Widths across the range a name column plausibly takes, so this cannot
        // pass by landing on the one width where the arithmetic happens to
        // come out even.
        foreach (var width in new double[] { 100, 120, 140, 160, 180, 200, 240, 300 })
            Assert.EndsWith(".xlsx", Drawn(Long, width), StringComparison.Ordinal);

        // And at a font size the listing does not use, because the ellipsis is
        // measured per typeface AND per size and cached under both. Measured
        // with the size dropped from that key: 200px at 26 drew "qu…xlsx",
        // which has lost the dot the extension is known by.
        foreach (var width in new double[] { 200, 280 })
            Assert.EndsWith(".xlsx", Drawn(Long, width, size: 26), StringComparison.Ordinal);
    }

    /// <summary>
    /// **A wrapped line starts partway through the string, and character hits
    /// are indices into the STRING.** Avalonia's line hands back distances for
    /// text-source positions, not for positions within the line, so a line
    /// whose FirstTextSourceIndex is ten measures the wrong characters unless
    /// that ten is added back.
    ///
    /// Reached by any wrapped name whose visible last line is collapsed —
    /// which the shipped tile does on every name that needs three lines, and
    /// which a HEIGHT constraint does here.
    /// </summary>
    [AvaloniaFact]
    public void A_wrapped_line_is_measured_from_its_own_start()
    {
        var block = new TextBlock
        {
            Text = "aaaa bbbb cccc.dd eeee.txt",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = NameEllipsis.KeepingTheExtension,
        };

        // Three lines of room for four lines of name, so the third one is the
        // one that has to be collapsed.
        block.Measure(new Size(110, 46));
        block.Arrange(new Rect(0, 0, 110, 46));

        // "aaaa " and "bbbb " fit; the third line is "cccc.dd " cut in the
        // middle of its own text, at its own offset.
        Assert.Contains("….dd", Drawn(block), StringComparison.Ordinal);
    }

    // ---- and in the real window, where the markup is compiled ---------------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// The shipped row, because the markup above ships COMPILED: a trimming
    /// named through <c>x:Static</c> is resolved by the XAML compiler, and
    /// nothing else here would notice if the row template stopped reaching it
    /// or if the shipped font size moved the arithmetic.
    ///
    /// Measured on this same window with the details template put back to
    /// CharacterEllipsis, at these three widths: "quarter…", "quarterly-…" and
    /// "quarterly-fore…" — three answers, none of which says the row is a
    /// spreadsheet.
    /// </summary>
    [AvaloniaFact]
    public async Task The_shipped_row_keeps_the_extension_at_any_width()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-ellipsis-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Long), "x");

        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        // Closing this window flushes a session and the temp folder below would
        // be in it, so the tab goes back where it was first — see the row-name
        // tests, which learned this the same way.
        var was = shell.ActiveTab?.CurrentPath;

        try
        {
            await shell.ActiveTab!.NavigateAsync(root);
            Settle();
            window.UpdateLayout();
            Settle();

            var cell = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Long);

            // Re-measured rather than resized: the pane's own width is a
            // window's width minus a sidebar and four other columns, and what
            // this is about is the name cell.
            foreach (var width in new double[] { 120, 160, 220 })
            {
                cell.InvalidateMeasure();
                cell.Measure(new Size(width, 1000));

                var drawn = Drawn(cell);

                Assert.EndsWith(".xlsx", drawn, StringComparison.Ordinal);
                Assert.StartsWith("qu", drawn, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (was is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => shell.ActiveTab!.NavigateAsync(was));
                Settle();
            }

            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }
}
