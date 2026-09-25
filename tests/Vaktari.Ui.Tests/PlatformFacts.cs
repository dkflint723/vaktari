using System.Runtime.CompilerServices;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A fact that only runs on Windows.
///
/// The same attribute Vaktari.Core.Tests defines, and for the same reason it
/// gives: a conditional expectation — <c>expected = IsWindows() ? x : y</c> —
/// asserts that the code does whatever it currently does, which is not a test.
/// Assertions about drive letters are about Windows, so they say so and run
/// there.
///
/// Written out rather than shared with the other test project: that one is on
/// xunit v2 and this one on v3, and v3 wants the source position passed through
/// so a skipped test still reports where it came from.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Asserts Windows path shapes; runs on Windows only.";
    }
}

/// <summary>
/// A fact that only runs on Linux: the counterpart of
/// <see cref="WindowsFactAttribute"/>, for assertions about what a Linux path
/// means.
///
/// **A guard in the body reported a pass where nothing ran.** The tests that
/// needed one platform opened with <c>if (OperatingSystem.IsWindows()) return;</c>,
/// so the Windows run counted them as passed without asserting anything, and a
/// revert-check made there came back green for a reason that had nothing to do
/// with the code. An attribute says the test was skipped, which is the truth.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Asserts Linux path shapes; runs on Linux only.";
    }
}

/// <summary>
/// The condition behind an Avalonia fact that only runs on Windows: a headless
/// window driven on the dispatcher, asserting something only the Windows
/// platform offers — the hosted shell menu, the case-insensitive filesystem.
///
/// AvaloniaFactAttribute is sealed, so it cannot be subclassed the way
/// <see cref="WindowsFactAttribute"/> subclasses FactAttribute; xunit v3's
/// SkipUnless does the same job by name:
/// <c>[AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]</c>.
///
/// **The whole Ui suite ran on Windows alone until 10 September 2026**, and
/// the first run on Linux found twelve tests asserting Windows facts under a
/// plain AvaloniaFact. These say so and skip, which is what lets the other
/// two thousand three hundred run where a case-sensitive filesystem would have
/// caught the two source-reading tests that had broken on CI before.
/// </summary>
internal static class OnlyOn
{
    public const string Windows = "Asserts what only the Windows platform offers; runs on Windows only.";

    public static bool IsWindows => OperatingSystem.IsWindows();
}
