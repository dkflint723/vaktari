using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Show tooltips on rows", and the one row tooltip it did not reach.
///
/// **The look-alike mark went on popping its tooltip with the preference off.**
/// The setting was read in converters only — the modified column's age
/// description, the recent listing's PathTip, the trimmed name's NameTip — and
/// the look-alike words were a literal ToolTip.Tip written out once per listing
/// layout, so unticking the box silenced three row tooltips out of four. A
/// preference that silences some tooltips and not others is worse than one that
/// does nothing, because the ones that remain read as a fault rather than a
/// setting.
///
/// Both halves are held here, and the last test holds them together in the
/// SHIPPED window: the converter answers nothing when the preference is off,
/// all three layouts ask the converter rather than carrying the sentence
/// themselves, and all three really pop the sentence and really stop.
/// </summary>
public sealed class LookAlikeTooltipTests : OwnedViewModels
{
    private readonly SettingsState _before = AppSettings.Current;

    public override void Dispose()
    {
        AppSettings.Apply(_before);

        base.Dispose();

        GC.SuppressFinalize(this);
    }

    private const string Sentence =
        "Another row here is named the same, or close enough to look it.";

    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private static void RowTooltips(bool on)
        => AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { ShowTooltips = on },
        });

    private static FileEntry Colliding =>
        new("Ember Setup 0.1.0 .exe", "/a/Ember Setup 0.1.0 .exe",
            1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    // ---- what the converter answers -----------------------------------------

    private static object? Tip()
        => FileConverters.LookAlikeTip.Convert(
               Colliding, typeof(string), null,
               System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The mark is two words in the details and compact rows and a bare "≈" on
    /// a tile, so the sentence is the whole of the explanation.
    /// </summary>
    [AvaloniaFact]
    public void The_mark_says_why_it_is_there()
    {
        RowTooltips(true);

        Assert.Equal(Sentence, Tip());
    }

    /// <summary>The finding itself: the preference has to reach this tip too.</summary>
    [AvaloniaFact]
    public void Switching_row_tooltips_off_silences_it()
    {
        RowTooltips(false);

        Assert.Null(Tip());
    }

    /// <summary>
    /// An unrealized container hands a value-type converter a null DataContext,
    /// and FuncValueConverter answers UnsetValue for that without ever calling
    /// the lambda — so a row with no entry yet gets no tooltip rather than the
    /// sentence.
    ///
    /// **This records an idiom, not a hover anybody could reach.** The second
    /// assertion is the reason, measured rather than argued: the mark's own
    /// visibility comes from Confusable, which answers false for that same null
    /// because its first value is not a string path — so a mark with no entry
    /// behind it is collapsed, and nothing collapsed can be hovered. What is
    /// pinned here is that LookAlikeTip is typed on FileEntry like its siblings
    /// NameTip and PathTip rather than on FileEntry?. The tooltip a user can
    /// actually reach is the last test in this file.
    /// </summary>
    [AvaloniaFact]
    public void A_row_with_no_entry_yet_is_not_given_the_words()
    {
        RowTooltips(true);

        Assert.Equal(AvaloniaProperty.UnsetValue,
                     FileConverters.LookAlikeTip.Convert(
                         null, typeof(string), null,
                         System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(false,
                     FileConverters.Confusable.Convert(
                         [null, new HashSet<string> { "/a/Ember Setup 0.1.0 .exe" }],
                         typeof(bool), null,
                         System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- and every layout asks it -------------------------------------------

    /// <summary>
    /// Discovered from the markup rather than listed here, so a fourth layout
    /// added later is held to this without anybody remembering — the same way
    /// the row name and the name tooltip are.
    ///
    /// The marks are found by their shared visibility converter, which is what
    /// makes an element a look-alike mark in the first place, rather than by
    /// their text: one of the three is a "≈" badge with no words at all.
    ///
    /// **This says each mark's ToolTip.Tip NAMES the converter, and no more.**
    /// A binding that names it and still resolves to nothing passes here, so it
    /// cannot stand in for the shipped tooltip appearing — that is the
    /// real-window test at the end of this file.
    /// </summary>
    [AvaloniaFact]
    public void Every_look_alike_mark_asks_the_converter()
    {
        var marks = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "MultiBinding")
            .Where(b => ((string?)b.Attribute("Converter"))
                        ?.Contains("FileConverters.Confusable", StringComparison.Ordinal) == true)
            // MultiBinding -> the X.IsVisible property element -> the mark.
            .Select(b => b.Parent!.Parent!)
            .ToList();

        // A guard, not decoration: a layout that stops marking its look-alikes
        // this way must fail here rather than silently drop out of the check
        // below.
        Assert.Equal(3, marks.Count);

        var ungated = marks
            .Where(m => ((string?)m.Attribute("ToolTip.Tip"))
                        ?.Contains("FileConverters.LookAlikeTip", StringComparison.Ordinal) != true)
            .Select(m => m.Name.LocalName + " " + (string?)m.Attribute("ToolTip.Tip"))
            .ToList();

        Assert.True(ungated.Count == 0,
            "these look-alike marks pop a tooltip that \"Show tooltips on rows\" cannot silence: "
            + string.Join(" | ", ungated));
    }

    // ---- through a binding, on a drawn row ----------------------------------

    /// <summary>
    /// What this establishes is narrow and worth having on its own: a null from
    /// this converter CLEARS ToolTip.Tip rather than setting it to the string
    /// "null" or leaving the previous value in place. A binding target decides
    /// that, not a converter, so it is measured rather than assumed.
    ///
    /// It does not stand in for the shipped templates. This window is loaded at
    /// runtime and declares no x:DataType, so its binding is a reflection one,
    /// while MainWindow.axaml's three marks sit in `x:DataType="fs:FileEntry"`
    /// templates compiled under AvaloniaUseCompiledBindingsByDefault. The
    /// shipped ones are measured for themselves, at the end of this file.
    /// </summary>
    private const string Row = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Vaktari.Ui.ViewModels;assembly=Vaktari.Ui">
          <ListBox x:Name="Rows">
            <ListBox.ItemTemplate>
              <DataTemplate>
                <TextBlock Text="Look-alike"
                           ToolTip.Tip="{Binding Converter={x:Static vm:FileConverters.LookAlikeTip}}"/>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
        </Window>
        """;

    private static object? TipOnScreen()
    {
        var window = (Window)AvaloniaRuntimeXamlLoader.Load(Row);
        var list = window.FindControl<ListBox>("Rows")!;

        list.ItemsSource = new[] { Colliding };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        var mark = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "Look-alike");

        var tip = ToolTip.GetTip(mark);

        window.Close();

        return tip;
    }

    [AvaloniaFact]
    public void A_drawn_mark_carries_the_words()
    {
        RowTooltips(true);

        Assert.Equal(Sentence, TipOnScreen());
    }

    /// <summary>
    /// **A null Tip is what makes the preference cost one line.** The mark is
    /// still drawn and still says "Look-alike"; only the hover goes.
    /// </summary>
    [AvaloniaFact]
    public void A_drawn_mark_carries_nothing_when_the_preference_is_off()
    {
        RowTooltips(false);

        Assert.Null(TipOnScreen());
    }

    // ---- and in the real window, where the bindings are compiled ------------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// The marks of one layout, found the way somebody looking at the window
    /// finds them — by what is drawn. Details and compact write the word; a
    /// tile writes "≈" inside a filled badge, and the badge is what carries the
    /// tooltip.
    /// </summary>
    private static List<Control> DrawnMarks(Window window, ViewMode view)
        => view == ViewMode.Grid
            ? window.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Child is TextBlock t && t.Text == "≈")
                .Cast<Control>().ToList()
            : window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text == "Look-alike")
                .Cast<Control>().ToList();

    /// <summary>
    /// The shipped marks, because the three ToolTip.Tip attributes ship as
    /// COMPILED bindings and everything above this line is either a read of the
    /// source text or a runtime-loaded reflection binding. Nothing else here
    /// would notice a shipped binding that named the converter and resolved to
    /// nothing.
    ///
    /// **Measured on this same window with the details mark's binding given a
    /// path — `{Binding Name, Converter=...}`, which compiles — the mark still
    /// drew, still read "Look-alike", and its tooltip was null with the
    /// preference ON.** That is the entire user-visible payload of this change
    /// gone at one site, with the source-reading test above still green because
    /// the attribute still names LookAlikeTip.
    ///
    /// Both preference states and all three layouts, because the fault being
    /// closed was exactly a mark that behaved differently from its neighbours.
    /// </summary>
    [AvaloniaFact]
    public async Task The_shipped_marks_pop_the_words_and_the_preference_stops_them()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-lookalike-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        // The pair from ConfusableNames' own summary: one space before the
        // extension, legal and invisible.
        File.WriteAllText(Path.Combine(root, "Ember Setup 0.1.0.exe"), "x");
        File.WriteAllText(Path.Combine(root, "Ember Setup 0.1.0 .exe"), "x");

        // Built before the setting is touched: the constructor applies settings
        // from disk, so anything set first is overwritten.
        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        // Closing this window flushes a session and the temp folder above would
        // be in it, so the tab goes back where it was first — see the row-name
        // tests, which learned this the same way.
        var was = shell.ActiveTab?.CurrentPath;

        try
        {
            await shell.ActiveTab!.NavigateAsync(root);
            Settle();

            foreach (var on in new[] { true, false })
            {
                RowTooltips(on);

                foreach (var view in new[] { ViewMode.Details, ViewMode.Compact, ViewMode.Grid })
                {
                    // Switching layout is what re-reads the setting: each mode
                    // hands its ListBox a different collection, so the
                    // containers — and their tooltip bindings — are built
                    // again. Nothing pushes a settings change at a live row.
                    shell.ActiveTab!.View = view;
                    Settle();
                    window.UpdateLayout();
                    Settle();

                    var marks = DrawnMarks(window, view);

                    Assert.Equal(2, marks.Count);

                    foreach (var mark in marks)
                    {
                        Assert.True(mark.IsVisible,
                            $"the {view} mark stopped being drawn (tooltips {on})");

                        Assert.Equal(on ? Sentence : null, ToolTip.GetTip(mark));
                    }
                }
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
