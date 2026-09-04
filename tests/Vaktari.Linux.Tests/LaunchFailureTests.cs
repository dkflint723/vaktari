using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The desktop's opener refusing to start.
///
/// **It was dropped on the floor.** Open handed xdg-open to a spawn helper
/// whose catch returned nothing at all, and the interface returned void, so a
/// session with no xdg-open on PATH — a container, a desktop put together by
/// hand — opened nothing and said nothing. The pane had no way to know there
/// was anything to say.
///
/// Through the opener seam rather than xdg-open: the suite runs on Windows
/// too, where nothing is on PATH under either name, so the seam is what makes
/// this test mean the same thing on a machine that HAS an opener.
/// </summary>
public sealed class LaunchFailureTests
{
    [Fact]
    public void An_opener_that_will_not_start_is_handed_back()
    {
        var launcher = new LinuxLauncher();

        launcher.UseOpener("vaktari-no-such-opener");

        Assert.NotNull(launcher.Open("/srv/work/notes.txt"));
    }

    /// <summary>
    /// The default, and that the stand-in really replaces it.
    ///
    /// Both halves are unobservable through Open on this machine — Windows has
    /// neither program on PATH, so every name fails identically and the test
    /// above passes whatever the field holds.
    /// </summary>
    [Fact]
    public void The_opener_is_xdg_open_until_a_test_stands_in_for_it()
    {
        Assert.Equal("xdg-open", new LinuxLauncher().Opener);

        var standing = new LinuxLauncher();
        standing.UseOpener("vaktari-no-such-opener");

        Assert.Equal("vaktari-no-such-opener", standing.Opener);
    }
}
