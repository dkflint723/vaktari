using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where a drop is about to land.
///
/// **The only thing marked was the pane, and the pane was never the question.**
/// Which half of the window the pointer is in is obvious. What a drag could not
/// tell you is whether releasing puts the files into the folder under the
/// pointer or into the folder being listed — two different places, one of them
/// a mistake you then have to notice and undo. The sidebar was worse: dragging
/// onto Downloads looked exactly like dragging past it.
///
/// The rows keep a constant border thickness and change only its colour, and
/// the sidebar changes a fill rather than gaining a border, because the row you
/// are aiming at must not move while you aim at it.
/// </summary>
public sealed class DropHighlightTests
{
    private static object? Convert(IMultiValueConverter converter, params object?[] values)
        => converter.Convert(values, typeof(object), null, CultureInfo.InvariantCulture);

    private static Color ColourOf(object? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>Resources have to be loaded for the accent to resolve.</summary>
    private static Window Themed()
    {
        var window = new Window();

        ThemeApplier.Apply(window, new Vaktari.Core.ThemePalette
        {
            IsDark = true,
            Colours = new Dictionary<string, string>(),
        });

        return window;
    }

    // ---- the folder row ------------------------------------------------------

    [AvaloniaFact]
    public void The_row_a_drop_lands_in_is_ringed()
    {
        var window = Themed();

        try
        {
            var target = Path.Combine(Path.GetTempPath(), "inbox");

            Assert.True(ColourOf(Convert(FileConverters.DropRingBrush, target, target)).A > 0,
                "the folder under the pointer draws no ring, so the drop is unannounced");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Every_other_row_stays_clear()
    {
        var window = Themed();

        try
        {
            var target = Path.Combine(Path.GetTempPath(), "inbox");
            var other = Path.Combine(Path.GetTempPath(), "archive");

            Assert.Equal(0, ColourOf(Convert(FileConverters.DropRingBrush, other, target)).A);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Nothing under the pointer means nothing ringed. Without this the empty
    /// string would match a row whose path was also empty, and every row in a
    /// listing that had not finished loading would light up at once.
    /// </summary>
    [AvaloniaFact]
    public void No_target_rings_nothing()
    {
        var window = Themed();

        try
        {
            Assert.Equal(0, ColourOf(Convert(FileConverters.DropRingBrush, "", "")).A);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Paths are compared the way the rest of the application compares
    /// them, so a drive letter in the other case still rings its own row.</summary>
    [AvaloniaFact]
    public void Case_does_not_decide_which_row_is_ringed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var window = Themed();

        try
        {
            var target = Path.Combine(Path.GetTempPath(), "Inbox");

            Assert.True(ColourOf(Convert(FileConverters.DropRingBrush, target.ToUpperInvariant(), target)).A > 0);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **All three layouts, not just the one that was easiest to reach.** The
    /// details view is the default, so a highlight only there would have looked
    /// finished and left the compact and tile views exactly as they were.
    /// </summary>
    [AvaloniaFact]
    public void All_three_listing_layouts_carry_the_ring()
    {
        var markup = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        var layouts = markup.Split("<DataTemplate x:DataType=\"fs:FileEntry\">").Length - 1;
        var rings = markup.Split("FileConverters.DropRingBrush").Length - 1;

        Assert.True(layouts >= 3, $"expected the three listing layouts, found {layouts}");
        Assert.Equal(3, rings);
    }

    // ---- the sidebar place ---------------------------------------------------

    [AvaloniaFact]
    public void A_place_a_drop_would_land_in_is_washed()
    {
        var window = Themed();

        try
        {
            var drop = ColourOf(Convert(FileConverters.PlaceRowFill, false, true));

            Assert.True(drop.A > 0, "a sidebar place gives no sign it is the target");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// It has to be stronger than "you are here", or the two are the same mark
    /// meaning different things — and the row you are dragging onto is often
    /// also the row you are in.
    /// </summary>
    [AvaloniaFact]
    public void The_drop_wash_is_stronger_than_the_you_are_here_wash()
    {
        var window = Themed();

        try
        {
            var here = ColourOf(Convert(FileConverters.PlaceRowFill, true, false));
            var drop = ColourOf(Convert(FileConverters.PlaceRowFill, false, true));

            Assert.True(drop.A > here.A, $"drop {drop.A} is not stronger than current {here.A}");
            Assert.True(here.A > 0, "the current place lost its wash");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_ordinary_place_has_no_wash()
    {
        var window = Themed();

        try
        {
            Assert.Equal(0, ColourOf(Convert(FileConverters.PlaceRowFill, false, false)).A);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- the wiring ----------------------------------------------------------

    /// <summary>
    /// The converters cannot mark anything the drag never tells them about. This
    /// is the line that carries the folder row and the place from the drag-over
    /// to the two view models.
    /// </summary>
    [AvaloniaFact]
    public void The_drag_reports_the_row_and_the_place_it_is_over()
    {
        var source = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml.cs"));

        // Every accepting branch, not just one of them: the drag-over accepts a
        // drop by two routes, and a branch that forgets to report leaves the
        // last ring on screen while the pointer has moved on.
        Assert.DoesNotContain("HighlightDropTarget(place is null ? pane : null);", source);

        var reporting =
            source.Split("HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place)")
                  .Length - 1;

        Assert.True(reporting >= 2,
            $"only {reporting} of the accepting branches report the row and the place");
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
