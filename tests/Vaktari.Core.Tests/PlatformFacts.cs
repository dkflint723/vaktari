using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Skip reasons, shared so the two attribute pairs below cannot drift apart.
/// </summary>
internal static class PlatformSkip
{
    internal const string Posix =
        "Asserts POSIX path shapes; runs on Linux only.";

    internal const string Windows =
        "Asserts Windows path shapes; runs on Windows only.";
}

/// <summary>
/// A fact that only runs on Linux.
///
/// **Why these exist.** `PathRulesTests` used to claim its assertions "run on
/// any platform: the assertions are about POSIX paths, which the rules must
/// handle identically wherever they execute." That is not true, and ten of them
/// failed on Windows. A POSIX literal does not mean the same thing on both
/// systems: `/` is the filesystem root on Linux, and on Windows it is a legal
/// separator naming the root of the *current drive*, so `Path.GetPathRoot("/")`
/// answers `\`.
///
/// Skipping is honest where a conditional expectation would not be. A test
/// reading `expected = IsWindows() ? @"\home" : "/home"` asserts that the code
/// does whatever it currently does, which is not a test. These assertions are
/// about Linux, so they say so and run there.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = PlatformSkip.Posix;
    }
}

/// <inheritdoc cref="PosixFactAttribute"/>
public sealed class PosixTheoryAttribute : TheoryAttribute
{
    public PosixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = PlatformSkip.Posix;
    }
}

/// <summary>A fact that only runs on Windows. See <see cref="PosixFactAttribute"/>.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = PlatformSkip.Windows;
    }
}

/// <summary>
/// A fact that needs 8.3 short names on the temp volume — Windows only, and
/// only where the volume generates them. **Skipped, and shown as skipped**,
/// where the volume does not: the test it replaced returned early and
/// passed, so a run on such a volume looked like a proof it was not.
/// </summary>
public sealed class ShortNamesFactAttribute : FactAttribute
{
    public ShortNamesFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = PlatformSkip.Windows;
            return;
        }

        var probe = Directory.CreateTempSubdirectory("vaktari-8dot3").FullName;

        try
        {
            File.WriteAllText(Path.Combine(probe, "longfilename.txt"), "x");

            if (!File.Exists(Path.Combine(probe, "LONGFI~1.TXT")))
                Skip = "8.3 short names are turned off on the temp volume, so there is no alias to collide with.";
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = PlatformSkip.Windows;
    }
}
