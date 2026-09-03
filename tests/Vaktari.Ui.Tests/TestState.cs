using System.Runtime.CompilerServices;
using Vaktari.Ui.Session;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where this test run keeps the state a window writes.
///
/// **Every headless test that built a MainWindow wrote the developer's own
/// state.** Its constructor makes eight stores out of one directory — the
/// session, the settings, the folder views, the recents, the drive links, the
/// icon index and the platform's own — and closing the window flushes them. So
/// running this suite overwrote the open tabs, the window geometry and the back
/// stack of whoever ran it. The back stack on the machine where this was found
/// held about eighty entries named after temp folders a rename test had
/// visited, and a test that left a tab in the bin made the bin the folder the
/// application opened on next launch — which then failed two unrelated tests,
/// because renaming is refused there. The suite was writing the bug it then
/// reported, in a class that had nothing to do with either.
///
/// **A module initializer rather than a base class or a fixture**, because a
/// test must not be able to forget. Four classes build a window and one of them
/// — RenamePromptTenancyTests, which is where the damage surfaced — does not
/// derive from <see cref="OwnedViewModels"/>, so a base-class hook would have
/// missed exactly the class that mattered. This runs once, before any test in
/// the assembly, and covers anything anyone adds later without their knowing it
/// is here.
///
/// **And one directory per test CLASS, not one per run.** A window flushes its
/// session when it closes and the next window restores it, so a single
/// directory for the whole suite leaves the tests poisoning each other exactly
/// as they poisoned the developer: with one, F6RegionTests opened on the bin
/// that BinPurgeTests had left behind, and its rename bar would not open,
/// because renaming is refused there. The store asks for the directory each
/// time it is built, so the answer can depend on which test is running.
/// </summary>
internal static class TestState
{
    /// <summary>The root this run works under. Removed when the process ends.</summary>
    internal static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "vaktari-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Where the stores of the test running right now belong.
    ///
    /// Named after the test class rather than the test, because a class is the
    /// unit these window tests already share a harness at — and because a name
    /// that changes mid-test would hand one window two different directories.
    /// "shared" covers anything that reaches a store outside a test, which
    /// nothing does today and which must still land somewhere disposable.
    /// </summary>
    internal static string Current()
    {
        var name = TestContext.Current.TestClass?.TestClassSimpleName ?? "shared";

        return Path.Combine(Root, name);
    }

    [ModuleInitializer]
    internal static void Redirect()
    {
        JsonSessionStore.DirectoryOverride = Current;

        // Best effort, and deliberately quiet: a temp directory left behind is
        // untidy, and a test run that fails because it could not delete one is
        // worse. ProcessExit rather than a fixture teardown because the stores
        // hold debounce timers that may still be flushing when the last test
        // ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception) { /* not worth failing a green run over */ }
        };
    }
}
