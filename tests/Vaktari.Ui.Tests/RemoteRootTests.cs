using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which paths count as being over a wire.
///
/// **The answer decides whether a per-row directory probe runs.** RowIcon reads
/// a folder's contents to pick its icon, and that read over SMB is the
/// round-trip storm the loader's own comment exists to prevent — so a mapped
/// drive wrongly judged local means one network round trip per visible row,
/// every time the listing scrolls.
/// </summary>
public sealed class RemoteRootTests : IDisposable
{
    private readonly IReadOnlyList<string> _saved = ThumbnailLoader.RemoteRoots;

    public void Dispose() => ThumbnailLoader.RemoteRoots = _saved;

    /// <summary>
    /// **Compared with the platform's own case rule.** This used Ordinal on the
    /// one platform where paths are case-insensitive, so a root discovered as
    /// "Z:\" did not match a path spelled "z:\" — out of step with every other
    /// comparison in the application.
    /// </summary>
    [Fact]
    public void Case_does_not_decide_whether_a_path_is_remote_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        ThumbnailLoader.RemoteRoots = [@"Z:\"];

        Assert.True(ThumbnailLoader.IsRemote(@"Z:\photos\a.jpg"));
        Assert.True(ThumbnailLoader.IsRemote(@"z:\photos\a.jpg"));
    }

    /// <summary>
    /// A UNC path is remote by its shape — nothing has to have been discovered
    /// for \\server\share to be over a wire.
    /// </summary>
    [Fact]
    public void A_unc_path_is_remote_with_nothing_discovered()
    {
        ThumbnailLoader.RemoteRoots = [];

        Assert.True(ThumbnailLoader.IsRemote(@"\\server\share\file.txt"));
    }

    [Fact]
    public void An_ordinary_local_path_is_not_remote()
    {
        ThumbnailLoader.RemoteRoots = [@"Z:\"];

        Assert.False(ThumbnailLoader.IsRemote(
            OperatingSystem.IsWindows() ? @"C:\work\a.txt" : "/home/flint/a.txt"));
    }

    /// <summary>Case still matters on Linux, where two paths differing in case
    /// are two different files.</summary>
    [Fact]
    public void Case_still_matters_on_linux()
    {
        if (OperatingSystem.IsWindows()) return;

        ThumbnailLoader.RemoteRoots = ["/run/user/1000/gvfs/smb-share"];

        Assert.True(ThumbnailLoader.IsRemote("/run/user/1000/gvfs/smb-share/a.txt"));
        Assert.False(ThumbnailLoader.IsRemote("/run/user/1000/GVFS/smb-share/a.txt"));
    }
}
