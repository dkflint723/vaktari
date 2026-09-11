using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The size of the synthetic large folder, from <c>VAKTARI_LARGE_FIXTURE</c>:
/// a number of entries, or <c>1</c> for the nightly's two hundred thousand.
/// Unset means the fixture tests do not run, which is every ordinary run.
/// </summary>
public static class LargeFixture
{
    public const string Variable = "VAKTARI_LARGE_FIXTURE";

    public static int Count
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(Variable);

            return int.TryParse(value, out var count) && count > 1 ? count : 200_000;
        }
    }

    public static bool Wanted => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));
}

/// <summary>
/// A fact that runs only when <c>VAKTARI_LARGE_FIXTURE</c> is set: it builds
/// hundreds of thousands of files and asserts on a stopwatch, which is a
/// nightly's work and not a merge gate's.
/// </summary>
public sealed class LargeFixtureFactAttribute : FactAttribute
{
    public LargeFixtureFactAttribute()
    {
        if (!LargeFixture.Wanted)
            Skip = $"Builds a large fixture and asserts a budget; set {LargeFixture.Variable} to run it.";
    }
}
