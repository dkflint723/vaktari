using System.Globalization;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The search for a column's widest entry (ColumnFit), with a fake "layout"
/// whose widths are known, so each rule of the search can be shown to be
/// the one that finds — or, mutated, misses — the widest.
/// </summary>
public sealed class ColumnFitTests
{
    /// <summary>A fake layout: every character 7 wide, unless a test says
    /// otherwise for a whole string.</summary>
    private static Func<string, double> Layout(Dictionary<string, double>? exact = null, Action? counted = null)
        => text =>
        {
            counted?.Invoke();
            return exact is not null && exact.TryGetValue(text, out var width) ? width : text.Length * 7.0;
        };

    private static ColumnFit.Candidate Candidate(string text, double estimate, bool complex = false)
        => new(text, 0, estimate, complex);

    /// <summary>
    /// **A complex string whose estimate is a little low is still laid out
    /// while it is within the window**, even ranked 200th, past the minimum.
    /// Below it 199 strings estimate wider and are narrower; the true widest
    /// estimates 8% under its real width.
    /// </summary>
    [Fact]
    public void A_complex_string_ranked_200th_and_8_percent_low_is_found_by_the_window()
    {
        var exact = new Dictionary<string, double>();
        var candidates = new List<ColumnFit.Candidate>();

        for (var i = 0; i < 199; i++)
        {
            var text = $"c{i}";
            exact[text] = 950;
            candidates.Add(Candidate(text, 1000 - i * 0.1, complex: true));
        }

        exact["widest"] = 1000;
        candidates.Add(Candidate("widest", 920, complex: true));

        var search = new ColumnFit.Search(candidates);

        Assert.True(search.Run(Layout(exact), int.MaxValue, TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal(1000, search.Widest);
    }

    /// <summary>
    /// **The top 64 by estimate are laid out whatever they are**: a simple
    /// string ranked 10th, 15% under its real width, is found by the minimum
    /// and by nothing else — the strings above it are exactly as wide as they
    /// estimate, so the upper-bound rule alone would stop after the first.
    /// </summary>
    [Fact]
    public void A_simple_string_ranked_10th_and_15_percent_low_is_found_by_the_minimum()
    {
        var exact = new Dictionary<string, double>();
        var candidates = new List<ColumnFit.Candidate>();

        for (var i = 0; i < 9; i++)
        {
            var text = $"s{i}";
            exact[text] = 900 - i;
            candidates.Add(Candidate(text, 900 - i));
        }

        exact["widest"] = 1000;
        candidates.Add(Candidate("widest", 850));

        for (var i = 0; i < 100; i++)
        {
            exact[$"t{i}"] = 800 - i;
            candidates.Add(Candidate($"t{i}", 800 - i));
        }

        var search = new ColumnFit.Search(candidates);

        Assert.True(search.Run(Layout(exact), int.MaxValue, TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal(1000, search.Widest);
    }

    /// <summary>
    /// **A hundred thousand strings that all estimate the same cost the
    /// minimum and no more** — the folder of IMG_20261004_123456.jpg names
    /// that laid out every one of its rows under the first version of the
    /// bound (measured: 100,000 layouts, 1.4 s). Ties stop at once.
    /// </summary>
    [Fact]
    public void A_hundred_thousand_equal_estimates_cost_at_most_64_layouts()
    {
        var names = Enumerable.Range(0, 100_000).Select(i => $"IMG_20261004_{i:000000}.jpg").ToList();
        var layouts = 0;
        var exact = Layout(counted: () => layouts++);

        var candidates = ColumnFit.Prepare(names.Select(n => (n, 0.0)), Layout());

        var search = new ColumnFit.Search(candidates);

        Assert.True(search.Run(exact, int.MaxValue, TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.True(layouts <= ColumnFit.MinimumExact, $"{layouts} layouts");
        Assert.Equal(names[0].Length * 7.0, search.Widest);
    }

    /// <summary>
    /// The same for a date column: absolute dates are nearly all distinct, so
    /// removing duplicates leaves them all, and in the monospace face every
    /// one estimates the same.
    /// </summary>
    [Fact]
    public void A_hundred_thousand_distinct_dates_of_one_width_cost_at_most_64_layouts()
    {
        var start = new DateTime(2020, 1, 1);
        var dates = Enumerable.Range(0, 100_000)
                              .Select(i => start.AddMinutes(i * 7).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                              .ToList();

        Assert.Equal(100_000, dates.Distinct().Count());

        var layouts = 0;
        var search = new ColumnFit.Search(ColumnFit.Prepare(dates.Select(d => (d, 0.0)), Layout()));

        Assert.True(search.Run(Layout(counted: () => layouts++), int.MaxValue, TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.True(layouts <= ColumnFit.MinimumExact, $"{layouts} layouts");
    }

    /// <summary>
    /// **A run of keycaps is found even though each of its code points is
    /// narrow.** Summed per code point, "0️⃣" is a digit and two marks that draw
    /// nothing; drawn, it is one wide glyph (the adversarial review measured a
    /// name of keycaps 38% under, ranked below 64 Latin names, and the column
    /// fitted to it 24.7 pixels short). Estimated per grapheme cluster, the
    /// cluster's own width is used, and the name ranks first.
    /// </summary>
    [Fact]
    public void A_name_of_keycaps_is_estimated_by_its_clusters_and_found()
    {
        const string keycaps = "track 0️⃣1️⃣2️⃣3️⃣4️⃣.mp3";

        // A keycap cluster draws 20 wide; alone, its marks draw nothing and its
        // digit 7, the way a font with emoji keycaps draws them.
        double Draw(string text)
        {
            var width = 0.0;
            var clusters = StringInfo.GetTextElementEnumerator(text);

            while (clusters.MoveNext())
            {
                var cluster = (string)clusters.Current;

                width += cluster.Contains('⃣') ? 20
                    : cluster is "️" or "⃣" ? 0
                    : 7;
            }

            return width;
        }

        var names = Enumerable.Range(0, 100).Select(i => $"latin-name-{i:000}-{new string('x', 3)}").ToList();
        names.Add(keycaps);

        Assert.True(ColumnFit.IsComplex(keycaps));
        Assert.True(Draw(keycaps) > names.Take(100).Max(Draw), "the keycap name is not the widest, so this measures nothing");

        var candidates = ColumnFit.Prepare(names.Select(n => (n, 0.0)), Draw);

        Assert.Equal(keycaps, candidates[0].Text);

        var search = new ColumnFit.Search(candidates);

        Assert.True(search.Run(Draw, int.MaxValue, TimeSpan.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal(Draw(keycaps), search.Widest);
    }

    [Theory]
    [InlineData("report-final.xlsx", false)]
    [InlineData("文件夹照片资料.txt", false)]
    [InlineData("😀 party.txt", false)]
    [InlineData("résumé.pdf", false)]
    [InlineData("résumé.pdf", true)]
    [InlineData("0️⃣.mp3", true)]
    [InlineData("👨‍👩‍👧.png", true)]
    [InlineData("👍🏽.png", true)]
    [InlineData("🇫🇷 holiday.jpg", true)]
    [InlineData("مرحبا.txt", true)]
    [InlineData("नमस्ते.txt", true)]
    public void Strings_whose_clusters_can_mislead_are_complex(string text, bool complex)
        => Assert.Equal(complex, ColumnFit.IsComplex(text));

    /// <summary>
    /// **The width stored is never short of the widest text and its padding
    /// once the metric rounds it again.** PaneScale.ColumnMetrics multiplies a
    /// width by the pane's scale and rounds to a tenth, so a width rounded to
    /// the nearest tenth here, rather than up, can draw up to 0.05 × the scale
    /// short — at 150%, more than a tenth.
    /// </summary>
    [Fact]
    public void A_fitted_width_is_never_short_once_drawn()
    {
        foreach (var scale in new[] { 1.0, 1.15, 1.25, 1.5 })
            for (var widest = 10.0; widest < 400; widest += 0.0137)
            {
                var drawn = Math.Round(ColumnFit.Fitted(widest, scale) * scale, 1);

                Assert.True(drawn >= widest + ColumnFit.Padding * scale - 0.06,
                            $"at {scale}: {widest} fitted to {ColumnFit.Fitted(widest, scale)}, drawn {drawn}");
            }
    }
}
