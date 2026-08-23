using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// F4 when the preferred terminal will not start.
///
/// **This took the whole application down.** OpenTerminal(dir, terminal) fell
/// back to OpenTerminal(dir), which picks Terminals.FirstOrDefault() and calls
/// straight back into the first — and the list is cached for the life of the
/// process, so the choice never changed. A terminal that refuses to start put
/// the two methods into a loop until the stack ran out. WindowsLauncher was
/// given the fix and carries the note; the Linux copy was not, and there was no
/// test project here to notice.
///
/// **A failing run here does not report a failure — it kills the test host**,
/// because a stack overflow cannot be caught. That is what makes the bug worth
/// a test rather than a comment.
/// </summary>
public sealed class TerminalFallbackTests
{
    private static TerminalOption Missing(string id) =>
        new(id, id, "vaktari-no-such-terminal-" + id, ["{dir}"]);

    /// <summary>
    /// Every candidate refuses, and the call still has to come back. What it
    /// cannot do is ask the same question again.
    /// </summary>
    [Fact]
    public void A_terminal_that_will_not_start_does_not_recurse()
    {
        var launcher = new LinuxLauncher();

        launcher.UseTerminals([Missing("one"), Missing("two")]);

        // Returns at all — under the old code this never came back.
        launcher.OpenTerminal(Path.GetTempPath(), launcher.Terminals[0]);
    }

    /// <summary>
    /// The same, entered by the overload that picks for you — the other half of
    /// the loop.
    /// </summary>
    [Fact]
    public void The_picking_overload_does_not_recurse_either()
    {
        var launcher = new LinuxLauncher();

        launcher.UseTerminals([Missing("only")]);

        launcher.OpenTerminal(Path.GetTempPath());
    }

    /// <summary>
    /// Nothing detected at all is the ordinary state on a headless box, and it
    /// must still be quiet rather than throwing out of a keypress.
    /// </summary>
    [Fact]
    public void No_terminals_at_all_is_not_an_error()
    {
        var launcher = new LinuxLauncher();

        launcher.UseTerminals([]);

        launcher.OpenTerminal(Path.GetTempPath());
    }
}
