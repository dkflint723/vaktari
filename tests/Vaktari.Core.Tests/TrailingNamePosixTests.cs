using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **On a freedesktop filesystem a trailing space or dot is an ordinary
/// character, and none of ReachablePath's three rules may refuse it.** Each
/// rule opens with a Windows-only guard, and two of the three guards reddened
/// nothing when removed on Fedora (fix-7 round-3 verification): with
/// RefuseHandedOut's guard gone, every drag of "report " out of a Linux pane
/// would be refused and the clipboard would leave it out of the list other
/// programs read; with RefuseLanding's gone, every copy or move of such a name
/// would be refused. Refuse's own guard was reached only through an archive
/// test. Asked here of the same shapes the Windows tests refuse.
/// </summary>
public sealed class TrailingNamePosixTests
{
    [PosixFact]
    public void No_rule_refuses_a_trailing_space_or_dot_on_linux()
    {
        foreach (var path in new[]
                 {
                     "/home/u/report ", "/home/u/report.", "/home/u/x...", "/home/u/album /a.txt",
                     "/home/u/notes /x. /y ", "/tmp/d. /in ",
                 })
        {
            Assert.Null(ReachablePath.Refuse(path));
            Assert.Null(ReachablePath.RefuseLanding(path));
            Assert.Null(ReachablePath.RefuseHandedOut(path));
        }
    }
}
