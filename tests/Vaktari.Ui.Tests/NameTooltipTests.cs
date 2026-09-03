using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Reading a name that did not fit.
///
/// **The name was the one trimmed thing in a row with nothing to say what it
/// trimmed.** Every other column already tips — the modified column explains
/// its shading, the look-alike chip explains itself, the recent listing's path
/// column pops the full path — and the name, which is the only column that
/// ellipsizes, said nothing at all. In a narrow split pane, or a grid tile that
/// gets two lines and then an ellipsis, "Q3-forecast-…-final.xlsx" could be
/// read only by pressing F2 to see the edit box and Escape to get back out.
///
/// All three listing layouts, because the trimming is in all three and a fix in
/// the default view only would have looked finished.
/// </summary>
public sealed class NameTooltipTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XDocument Markup()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

    /// <summary>
    /// Discovered from the markup rather than listed here, so a fourth layout
    /// added later is held to this without anybody remembering.
    /// </summary>
    [AvaloniaFact]
    public void Every_listing_says_what_it_trimmed()
    {
        var listings = Markup().Descendants(Avalonia + "ListBox")
            .Where(l => (string?)l.Attribute("ItemsSource")
                        is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                           or "{Binding GridEntries}")
            .ToList();

        // A guard, not decoration: a renamed listing must fail here rather than
        // silently drop out of the check below.
        Assert.Equal(3, listings.Count);

        var silent = listings
            .Select(l => (
                List: (string)l.Attribute("ItemsSource")!,
                // The name cell is the one bound through DisplayName, which
                // hides a Windows shortcut's extension the way Explorer does.
                Name: l.Descendants(Avalonia + "TextBlock")
                       .Single(t => ((string?)t.Attribute("Text"))
                                    ?.Contains("FileConverters.DisplayName",
                                               StringComparison.Ordinal) == true)))
            .Where(x => ((string?)x.Name.Attribute("ToolTip.Tip"))
                        ?.Contains("NameTip", StringComparison.Ordinal) != true)
            .Select(x => x.List)
            .ToList();

        Assert.True(silent.Count == 0,
            "these listings trim the name and never say what was trimmed: "
            + string.Join(", ", silent));
    }

    /// <summary>
    /// The tip repeats the label whenever the name DOES fit, so it must not
    /// arrive at hover speed — a tooltip over text you can already read is
    /// noise, and it would fire on every row you pass on the way somewhere.
    /// </summary>
    [AvaloniaFact]
    public void The_tip_waits_rather_than_popping_over_text_you_can_read()
    {
        var tips = Markup().Descendants(Avalonia + "TextBlock")
            .Where(t => ((string?)t.Attribute("ToolTip.Tip"))
                        ?.Contains("NameTip", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(3, tips.Count);

        foreach (var tip in tips)
            Assert.True(int.Parse((string)tip.Attribute("ToolTip.ShowDelay")!) >= 1000,
                        "the name tip pops at hover speed over text that already fits");
    }

    /// <summary>
    /// And every listing shows the display name, so a Windows shortcut loses
    /// its extension in all three places rows are drawn rather than in the one
    /// that was easiest to reach.
    ///
    /// Three, not four: the fourth was the search popup's own row template, and
    /// search results are drawn by these three now like anything else.
    /// </summary>
    [AvaloniaFact]
    public void Every_row_template_shows_the_display_name()
    {
        var shown = Markup().Descendants(Avalonia + "TextBlock")
            .Count(t => ((string?)t.Attribute("Text"))
                        ?.Contains("FileConverters.DisplayName", StringComparison.Ordinal) == true);

        Assert.Equal(3, shown);
    }

    [AvaloniaFact]
    public void The_tip_is_the_whole_name()
    {
        var entry = new FileEntry(
            "a-very-long-quarterly-forecast-final.xlsx",
            Path.Combine(Path.GetTempPath(), "a-very-long-quarterly-forecast-final.xlsx"),
            1, DateTimeOffset.UnixEpoch, EntryFlags.None);

        var tip = FileConverters.NameTip.Convert(
            entry, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(entry.Name, tip);
    }

    /// <summary>An unrealized row hands the converter a default entry, and a
    /// tooltip that is an empty box is worse than none.</summary>
    [AvaloniaFact]
    public void A_row_with_no_name_yet_shows_nothing()
    {
        var tip = FileConverters.NameTip.Convert(
            default(FileEntry), typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(tip);
    }
}
