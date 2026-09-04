using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which files "Run as administrator" is offered for.
///
/// **This rule lived in the view model, as a list of Windows file extensions.**
/// Right here and an answer for nowhere else: it decided the question for every
/// platform, and on a desktop where an executable usually has no extension at
/// all it could only ever say no. Linux gained pkexec and would still have had
/// nothing to offer it for. The rule belongs to the launcher that knows what
/// its own desktop can start, and this is where the Windows one is pinned.
///
/// The rule itself is unchanged: the runas verb on a .txt does nothing at all —
/// no error, no elevation, no editor — so offering it for every file would be
/// an entry that silently fails on most of them. This is the set Explorer
/// itself offers it for.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatableFilesTests
{
    [WindowsTheory]
    [InlineData(@"C:\tools\setup.exe", true)]
    [InlineData(@"C:\tools\install.msi", true)]
    [InlineData(@"C:\tools\go.bat", true)]
    [InlineData(@"C:\tools\go.cmd", true)]
    [InlineData(@"C:\tools\task.ps1", true)]
    [InlineData(@"C:\tools\old.com", true)]
    [InlineData(@"C:\tools\shortcut.lnk", true)]
    [InlineData(@"C:\tools\services.msc", true)]
    [InlineData(@"C:\tools\script.vbs", true)]
    [InlineData(@"C:\tools\keys.reg", true)]
    [InlineData(@"C:\notes.txt", false)]
    [InlineData(@"C:\photo.png", false)]
    [InlineData(@"C:\archive.zip", false)]
    [InlineData(@"C:\tools\README", false)]
    public void Only_what_the_shell_can_start_elevated_is_offered(string path, bool offered)
        => Assert.Equal(offered, new WindowsLauncher().CanElevateFile(path));

    /// <summary>
    /// Case is not part of the question — Windows has never treated it as one,
    /// and SETUP.EXE off a mounted image is exactly how it arrives shouting.
    /// </summary>
    [WindowsFact]
    public void The_case_of_the_extension_is_not_part_of_the_question()
        => Assert.True(new WindowsLauncher().CanElevateFile(@"D:\SETUP.EXE"));

    /// <summary>
    /// And the platform still says yes in general: a launcher that answered no
    /// here would take both entries off the menu on the one desktop that has
    /// always had them.
    /// </summary>
    [WindowsFact]
    public void Windows_still_has_a_route_to_elevate_at_all()
        => Assert.True(new WindowsLauncher().CanElevate);
}
