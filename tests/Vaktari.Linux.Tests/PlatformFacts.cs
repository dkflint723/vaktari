using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A fact that only runs on Linux.
///
/// Most of this project's tests are path arithmetic and ordinary file I/O and
/// run anywhere, which is why they are plain facts. A handful genuinely need
/// the platform — creating a symlink needs privileges Windows does not hand out
/// by default — and those say so rather than guarding in the body.
///
/// Skipping is honest where a silent early return is not: a body guard reports
/// a pass on a machine where nothing ran, and a test that is weaker on one
/// agent than another is worse than no test, because nothing says so.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Needs a real POSIX filesystem; runs on Linux only.";
    }
}

/// <inheritdoc cref="PosixFactAttribute"/>
public sealed class PosixTheoryAttribute : TheoryAttribute
{
    public PosixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Needs a real POSIX filesystem; runs on Linux only.";
    }
}

/// <summary>
/// A fact that needs two filesystems. The trash's copy across devices runs
/// only where a rename answers EXDEV, and one temp directory cannot arrange
/// that; on the Fedora runner /tmp and /dev/shm are two tmpfs instances, so a
/// rename between them is refused, and that refusal is what this asks for —
/// measured, not read off a mount table. Where it is not refused, the fact
/// says so and skips.
/// </summary>
public sealed class CrossDeviceFactAttribute : FactAttribute
{
    /// <summary>A directory on a filesystem other than the temp directory's, or null.</summary>
    public static readonly string? Elsewhere = FindElsewhere();

    public CrossDeviceFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Needs a real POSIX filesystem; runs on Linux only.";
        else if (Elsewhere is null)
            Skip = "Needs a second filesystem beside the temp directory; a rename to /dev/shm was not refused.";
    }

    private static string? FindElsewhere()
    {
        const string candidate = "/dev/shm";

        if (!OperatingSystem.IsLinux() || !Directory.Exists(candidate)) return null;

        var name = "vaktari-xdev-" + Guid.NewGuid().ToString("N")[..8];
        var here = Path.Combine(Path.GetTempPath(), name);
        var there = Path.Combine(candidate, name);

        Directory.CreateDirectory(here);

        try
        {
            Directory.Move(here, there);

            // The rename went through, so it is one filesystem after all.
            Directory.Delete(there);
            return null;
        }
        catch (IOException)
        {
            Directory.Delete(here);
            return candidate;
        }
    }
}
