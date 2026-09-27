using Avalonia.Headless.XUnit;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Showing what has been cut.
///
/// **Explorer greys them; Vaktari showed nothing at all.** Cut wrote the paths
/// to the clipboard and the listing carried on looking exactly as it had — so
/// there was no way to tell a cut from a copy, to see what was pending, or to
/// notice that a second cut had replaced the first.
/// </summary>
public sealed class CutMarkTests : IDisposable
{
    public CutMarkTests() => CutMarks.Clear();
    public void Dispose() => CutMarks.Clear();

    [Fact]
    public void Cutting_marks_the_files()
    {
        CutMarks.Mark(["/a/one.txt", "/a/two.txt"]);

        Assert.Equal(2, CutMarks.Paths.Count);
        Assert.Contains("/a/one.txt", CutMarks.Paths);
    }

    /// <summary>
    /// **A copy replaces the cut**, which is what the clipboard itself does.
    /// Leaving the marks up would show files as pending a move that is never
    /// going to happen.
    /// </summary>
    [Fact]
    public void Clearing_removes_them()
    {
        CutMarks.Mark(["/a/one.txt"]);
        CutMarks.Clear();

        Assert.Empty(CutMarks.Paths);
    }

    /// <summary>A second cut replaces the first rather than adding to it.</summary>
    [Fact]
    public void A_second_cut_replaces_the_first()
    {
        CutMarks.Mark(["/a/one.txt"]);
        CutMarks.Mark(["/a/two.txt"]);

        Assert.Equal(["/a/two.txt"], CutMarks.Paths);
    }

    /// <summary>Paths are compared the way the rest of the application compares
    /// them — case-insensitively on Windows.</summary>
    [WindowsFact]
    public void Case_does_not_decide_whether_a_row_is_marked()
    {
        CutMarks.Mark([@"C:\Work\Notes.TXT"]);

        Assert.Contains(@"c:\work\notes.txt", CutMarks.Paths);
    }

    /// <summary>
    /// **Every listing shows the same marks**, because the clipboard is one:
    /// cutting in one tab and pasting in another is the ordinary case.
    /// </summary>
    [AvaloniaFact]
    public void Changing_them_is_announced()
    {
        var raised = 0;

        void Handler(object? sender, EventArgs e) => raised++;

        CutMarks.Changed += Handler;

        try
        {
            CutMarks.Mark(["/a/one.txt"]);
            CutMarks.Clear();

            Assert.Equal(2, raised);

            // Clearing what is already clear says nothing: every listing would
            // rebuild its bindings for no change.
            CutMarks.Clear();

            Assert.Equal(2, raised);
        }
        finally
        {
            CutMarks.Changed -= Handler;
        }
    }

    // ---- what the row actually does with it --------------------------------

    private static double Fade(string path, params string[] cut) =>
        (double)FileConverters.CutFade.Convert(
            [path, new HashSet<string>(cut)],
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture)!;

    [AvaloniaFact]
    public void A_cut_row_is_dimmed_and_the_others_are_not()
    {
        Assert.True(Fade("/a/one.txt", "/a/one.txt") < 1.0);
        Assert.Equal(1.0, Fade("/a/two.txt", "/a/one.txt"));
        Assert.Equal(1.0, Fade("/a/one.txt"));
    }

    /// <summary>A row is drawn before anything has been cut, and every frame
    /// after — so the converter has to survive being handed nothing.</summary>
    [AvaloniaFact]
    public void Nothing_sensible_leaves_the_row_alone()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal(1.0, (double)FileConverters.CutFade.Convert(
            [null, null], typeof(double), null, culture)!);

        Assert.Equal(1.0, (double)FileConverters.CutFade.Convert(
            ["/a/one.txt"], typeof(double), null, culture)!);
    }
}
