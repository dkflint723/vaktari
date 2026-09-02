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
