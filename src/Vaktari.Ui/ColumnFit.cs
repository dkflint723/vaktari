using System.Diagnostics;
using System.Globalization;

namespace Vaktari.Ui;

/// <summary>
/// Finding the widest of many strings without laying every one of them out.
///
/// **Laying out every name is too slow for a large folder.** A real-font
/// TextLayout costs 10–60 microseconds, so 100,000 names took one to two and a
/// half seconds on the UI thread — measured with Skia and HarfBuzz on Windows.
/// So every string is first given an ESTIMATE, the sum of its grapheme
/// clusters' advances, each cluster laid out once and remembered, and only the
/// strings the estimate cannot rule out are laid out in full.
///
/// **Per grapheme cluster, not per code point.** Summed per code point, a run
/// of keycaps ("0️⃣1️⃣…") came out 38% under its real width, which put a
/// widest name below 64 narrower ones and left the column it was fitted to
/// 24.7 pixels short (measured in the adversarial review). A cluster is drawn
/// as one glyph or one shaped run, so its own layout is its width.
///
/// **What the estimate is trusted for depends on the string.**
/// <list type="bullet">
/// <item>A SIMPLE string — every cluster one code point, in a script that
/// does not join — is drawn as its clusters side by side, less any kerning,
/// so its estimate is an upper bound. Once the widest found so far is at least
/// the next simple string's estimate, no simple string after it can be wider,
/// and the search stops for them: ties stop at once.</item>
/// <item>A COMPLEX string — combining marks, emoji sequences, joining or
/// shaping scripts — can be drawn narrower or wider than its clusters (ZWJ
/// families +90%, Devanagari +67%, keycap runs below), so it is laid out
/// whenever its estimate is within <see cref="Window"/> of the widest.</item>
/// <item>And the top <see cref="MinimumExact"/> by estimate are always laid
/// out, whatever they are, so one estimate that is badly low is still caught
/// while it ranks near the top.</item>
/// </list>
/// </summary>
public static class ColumnFit
{
    /// <summary>How many of the highest estimates are laid out whatever the
    /// rules below say.</summary>
    public const int MinimumExact = 64;

    /// <summary>A complex string is laid out while its estimate is at least
    /// this share of the widest found. Three times the worst under-estimate
    /// measured for a per-cluster estimate.</summary>
    public const double Window = 0.9;

    /// <summary>How far a simple string's estimate may exceed the widest and
    /// still be taken as no wider: floating-point sums of the same advances,
    /// not a real difference.</summary>
    public const double Tolerance = 0.05;

    /// <summary>The padding added to the widest text, in pixels at 100%: the
    /// gap Type's own margin already gives its neighbour.</summary>
    public const double Padding = 8;

    /// <summary>One thing to measure: the text, a width that goes with it on
    /// its row and is not text (an indent, a slot, a chip), and the estimate
    /// of the whole.</summary>
    public readonly record struct Candidate(string Text, double Extra, double Estimate, bool Complex);

    /// <summary>
    /// Whether a string's per-cluster estimate can be wrong in either
    /// direction — see the class summary.
    /// </summary>
    public static bool IsComplex(string text)
    {
        foreach (var ch in text)
        {
            // Combining marks, ZWJ, variation selectors, the keycap mark.
            if (ch is '‍' or '⃣' or (>= '︀' and <= '️')) return true;

            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
                return true;

            // Scripts whose letters join or reorder: Arabic and Syriac,
            // Thaana, N'Ko, the Indic block through Sinhala, Thai and Lao,
            // Tibetan, Myanmar, Khmer, Mongolian, and Arabic's presentation
            // forms.
            if (ch is (>= '؀' and <= 'ࣿ') or (>= 'ऀ' and <= '࿿')
                or (>= 'က' and <= '႟') or (>= 'ក' and <= '᢯')
                or (>= 'ﭐ' and <= '﷿') or (>= 'ﹰ' and <= '﻿'))
                return true;
        }

        // A cluster of more than one code point: a flag, a skin tone, a
        // decomposed accent the category test above did not already catch.
        var clusters = StringInfo.GetTextElementEnumerator(text);

        while (clusters.MoveNext())
        {
            var cluster = (string)clusters.Current;

            if (cluster.Length > 2 || (cluster.Length == 2 && !char.IsSurrogatePair(cluster, 0)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The estimate of one string: its clusters' advances, each measured once
    /// with <paramref name="exact"/> and kept in <paramref name="advances"/>.
    /// </summary>
    public static double Estimate(string text, Func<string, double> exact, Dictionary<string, double> advances)
    {
        var sum = 0.0;
        var clusters = StringInfo.GetTextElementEnumerator(text);

        while (clusters.MoveNext())
        {
            var cluster = (string)clusters.Current;

            if (!advances.TryGetValue(cluster, out var width))
                advances[cluster] = width = exact(cluster);

            sum += width;
        }

        return sum;
    }

    /// <summary>Characters below this are each a cluster of their own and
    /// never complex: everything before the combining diacritics.</summary>
    private const int Narrow = 0x0300;

    private static bool AllNarrow(string text)
    {
        foreach (var ch in text)
            if (ch >= Narrow || ch == '\r')
                return false;

        return true;
    }

    private static double EstimateNarrow(string text, Func<string, double> exact, double[] advances)
    {
        var sum = 0.0;

        foreach (var ch in text)
        {
            var width = advances[ch];

            if (double.IsNaN(width)) advances[ch] = width = exact(ch.ToString());

            sum += width;
        }

        return sum;
    }

    /// <summary>
    /// The candidates for a set of strings with their extras: one per
    /// distinct pair, estimated, highest estimate first.
    /// </summary>
    public static List<Candidate> Prepare(
        IEnumerable<(string Text, double Extra)> items, Func<string, double> exact)
    {
        var advances = new Dictionary<string, double>(StringComparer.Ordinal);
        var seen = new HashSet<(string, double)>();
        var candidates = new List<Candidate>();

        // **The common case without the cluster walk**: a string of nothing
        // but characters below the combining marks is one cluster per
        // character, so its estimate is the sum of those characters' own
        // advances, kept in an array rather than a dictionary. The same
        // number, about three times sooner on a folder of Latin names.
        var narrow = new double[Narrow];
        Array.Fill(narrow, double.NaN);

        foreach (var (text, extra) in items)
        {
            if (!seen.Add((text, extra))) continue;

            var estimate = AllNarrow(text)
                ? EstimateNarrow(text, exact, narrow)
                : Estimate(text, exact, advances);

            candidates.Add(new Candidate(text, extra, estimate + extra, IsComplex(text)));
        }

        candidates.Sort((a, b) => b.Estimate.CompareTo(a.Estimate));

        return candidates;
    }

    /// <summary>
    /// A search for the widest candidate that can stop part-way and be
    /// carried on later — on the UI thread up to a budget, then on a
    /// background task for whatever is left.
    /// </summary>
    public sealed class Search(IReadOnlyList<Candidate> candidates)
    {
        private int _next;
        private bool _simpleDone;
        private bool _complexDone;

        /// <summary>The widest found so far, extra included; zero before any.</summary>
        public double Widest { get; private set; }

        /// <summary>How many candidates have been laid out.</summary>
        public int Exact { get; private set; }

        /// <summary>True once no candidate left could be wider.</summary>
        public bool Done { get; private set; }

        /// <summary>
        /// Lays out candidates until none left could be wider, or until
        /// <paramref name="layouts"/> more have been laid out or
        /// <paramref name="time"/> has passed. Answers <see cref="Done"/>.
        /// </summary>
        public bool Run(Func<string, double> exact, int layouts, TimeSpan time, CancellationToken token = default)
        {
            var clock = Stopwatch.StartNew();
            var budget = layouts;

            while (!Done)
            {
                if (_next >= candidates.Count || (_simpleDone && _complexDone))
                {
                    Done = true;
                    break;
                }

                if (budget <= 0 || clock.Elapsed > time) return false;

                token.ThrowIfCancellationRequested();

                var candidate = candidates[_next++];

                if (Exact >= MinimumExact)
                {
                    if (candidate.Complex ? _complexDone : _simpleDone) continue;

                    // Highest estimate first, and the widest only grows, so the
                    // first of a kind that cannot be wider ends that kind.
                    var ruledOut = candidate.Complex
                        ? candidate.Estimate < Widest * Window
                        : candidate.Estimate <= Widest + Tolerance;

                    if (ruledOut)
                    {
                        if (candidate.Complex) _complexDone = true;
                        else _simpleDone = true;

                        continue;
                    }
                }

                Widest = Math.Max(Widest, exact(candidate.Text) + candidate.Extra);
                Exact++;
                budget--;
            }

            return true;
        }
    }

    /// <summary>
    /// The width to store for a column whose widest content is
    /// <paramref name="widest"/> pixels as drawn, at a pane scale of
    /// <paramref name="scale"/>: the content and the padding, at 100%, rounded
    /// UP to the tenth of a pixel the widths are kept to.
    ///
    /// **Up, because the metric rounds again on the way out**
    /// (PaneScale.ColumnMetrics multiplies by the scale and rounds to a tenth),
    /// and a width kept a hair under the content draws the widest cell with an
    /// ellipsis.
    /// </summary>
    public static double Fitted(double widest, double scale)
    {
        if (scale <= 0) scale = 1;

        var need = (widest + Padding * scale) / scale;

        // A whisker off before the ceiling, so a width that is already a whole
        // tenth is not pushed up by the error in the division.
        return Math.Ceiling(need * 10 - 1e-6) / 10;
    }
}
