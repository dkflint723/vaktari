using System.Diagnostics;
using Vaktari.Core;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The large-folder budget, on the piece of it that is pure: sorting.
///
/// **No measurement existed; the sort's cost over a large folder was
/// believed.** This asserts it, against a budget generous enough that a slow
/// CI runner passes and a regression — a comparison that started allocating,
/// a rule that turned quadratic — does not. Nightly rather than on every
/// push, and only when <c>VAKTARI_LARGE_FIXTURE</c> names a size: a timing
/// assertion on a shared runner is weather as well as code, and it should be
/// read as a trend, not as a gate on a merge.
///
/// The platform halves — the first batch of a listing arriving, the whole
/// listing arriving — live in the Windows and Linux test projects under the
/// same name and the same variable.
/// </summary>
public sealed class LargeFolderTests
{
    [LargeFixtureFact]
    public void A_large_folders_names_sort_within_budget()
    {
        var count = LargeFixture.Count;
        var names = new string[count];
        var random = new Random(7);

        for (var i = 0; i < count; i++)
        {
            names[i] = (i % 4) switch
            {
                0 => $"IMG_{random.Next(1, 99_999):D4}.jpg",
                1 => $"Report {random.Next(1, 5_000)}.docx",
                2 => $"chapter{random.Next(1, 900)}.md",
                _ => $"file{random.Next(1, 1_000_000)}",
            };
        }

        var watch = Stopwatch.StartNew();

        Array.Sort(names, static (a, b) => NaturalOrder.Compare(a, b));

        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5),
            $"sorting {count:N0} names took {watch.Elapsed.TotalMilliseconds:N0} ms; the budget is 1,500 ms");
    }
}
