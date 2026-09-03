using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Which empty files the New menu offers, on which machine.
///
/// **The menu offered a shell script on Windows.** The list was written on
/// Linux and never asked which machine it was on, so the Windows build offered
/// to make a .sh — nothing on a stock Windows runs one, and the executable bit
/// that is the whole point of the entry is skipped there by the create path
/// itself. The slot is worth keeping; what fills it is now the thing the
/// platform actually executes.
/// </summary>
public sealed class NewFileKindsTests
{
    private static IEnumerable<string> Extensions(bool windows)
        => FileKinds.For(windows).Select(k => k.Extension);

    [Fact]
    public void Windows_is_offered_the_script_its_own_shell_runs()
    {
        Assert.Contains(".cmd", Extensions(true));
        Assert.DoesNotContain(".sh", Extensions(true));
    }

    [Fact]
    public void Elsewhere_keeps_the_shell_script_and_its_executable_bit()
    {
        var script = Assert.Single(FileKinds.For(false), k => k.Extension == ".sh");

        Assert.True(script.Executable);
    }

    /// <summary>
    /// Python is not a Unix file type — a .py on Windows is associated with the
    /// launcher the installer puts there — so dropping it would take away
    /// something that works.
    /// </summary>
    [Fact]
    public void Python_is_offered_on_both()
    {
        Assert.Contains(".py", Extensions(true));
        Assert.Contains(".py", Extensions(false));
    }

    /// <summary>
    /// **The flag is honest rather than quietly ignored.** Windows has no
    /// executable bit to set and the create path refuses to try, so a kind that
    /// claimed one there would be a promise nothing keeps.
    /// </summary>
    [Fact]
    public void Nothing_on_Windows_claims_an_executable_bit()
        => Assert.DoesNotContain(FileKinds.For(true), k => k.Executable);

    /// <summary>The escape hatch stays last: the platform choice sits in the
    /// middle of the list and must not disturb the order around it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_extensionless_escape_hatch_is_last_on_both(bool windows)
        => Assert.Equal("", FileKinds.For(windows)[^1].Extension);

    [WindowsFact]
    public void This_machine_gets_the_Windows_list()
        => Assert.Contains(FileKinds.Common, k => k.Extension == ".cmd");

    [PosixFact]
    public void A_Linux_machine_gets_the_shell_script()
        => Assert.Contains(FileKinds.Common, k => k.Extension == ".sh");
}
