using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which of the two chooser routes this platform takes.
///
/// The interface now has both: the system's own dialog, and a list of
/// everything installed for a chooser Vaktari draws itself. Windows takes the
/// first and must not offer the second — SHOpenWithDialog browses for an
/// executable and writes the association the rest of the system reads, and a
/// home-made list does neither, so a machine that got both would be offered the
/// worse one at the moment it asked for the better.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChooserRouteTests
{
    /// Through the interface, because that is how the pane holds it — and
    /// because the answer is the interface's default: this launcher declares no
    /// AllApplications of its own, and "nothing to draw a window from" is
    /// exactly what it means to have a dialog already.
    [Fact]
    public void The_shell_dialog_is_the_only_chooser_windows_offers()
    {
        IApplicationLauncher launcher = new WindowsLauncher();

        Assert.True(launcher.CanChooseApplication);

        // Empty is how the pane knows to stop at the dialog above rather than
        // drawing a window behind it.
        Assert.Empty(launcher.AllApplications);
    }
}
