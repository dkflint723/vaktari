using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **An ordinary path is read, and handed on, exactly as it is written.**
/// The trailing-name routes read through <see cref="ReachablePath.Exact"/> —
/// the preview, a row's size and papers, the properties window, the watcher's
/// stat, a restore — and a "\\?\" spelling there for a path that folds
/// nothing would change what every one of them does: "\\?\" is not accepted by
/// the shell's parser at all (measured in fix8's verification: an icon for
/// "\\?\…\plainfile" comes back empty), and a share or a mapped drive would
/// be reached by another spelling than the one the listing showed. So the
/// spelling is the path itself for every name that does not end in a space or
/// a dot — interior dots, a leading space, unicode, a share, a device or
/// already-extended spelling, a path past 260 characters — and nothing here is
/// refused or withheld.
///
/// The one place an ordinary path IS spelled "\\?\" on purpose is a walk root
/// (<see cref="ReachablePath.Extended"/>), and a share must come out as
/// "\\?\UNC\server\share", never "\\?\\\server".
/// </summary>
public sealed class OrdinaryNamesReadAsWrittenTests
{
    private static readonly string Long =
        @"C:\work\" + string.Join('\\', Enumerable.Range(0, 12).Select(i => "a-folder-name-of-some-length-" + i)) + @"\file.txt";

    public static TheoryData<string> Ordinary() => new()
    {
        @"C:\work\a.b",
        @"C:\work\v1.2.3.txt",
        @"C:\work\.hidden",
        @"C:\work\ lead.txt",
        @"C:\work\ lead dir\inner.txt",
        @"C:\work\x..y",
        @"C:\work\..dots",
        @"C:\work\with space .txt",
        @"C:\work\résumé.txt",
        @"C:\work\日本\写真.jpg",
        @"C:\work\a.b - Shortcut.lnk",
        @"C:\work\site.url",
        @"C:\work\disk.iso",
        @"C:\work\sub.d\",
        @"C:\",
        @"Z:\mapped\report",
        @"\\server\share\report",
        @"\\server\share\v1.2\a.b",
        @"\\?\C:\work\report",
        @"\\?\UNC\server\share\report",
        @"\\.\C:\work\report",
        Long,
    };

    [WindowsTheory]
    [MemberData(nameof(Ordinary))]
    public void An_ordinary_path_is_read_and_handed_on_as_written(string path)
    {
        Assert.Equal(path, ReachablePath.Exact(path));
        Assert.Null(ReachablePath.RefuseHandedOut(path));
        Assert.True(ReachablePath.IsReachable(path));
    }

    [WindowsTheory]
    [InlineData(@"\\server\share\report", @"\\?\UNC\server\share\report")]
    [InlineData(@"\\server\share\v1.2\ lead.txt", @"\\?\UNC\server\share\v1.2\ lead.txt")]
    [InlineData(@"\\server\share\", @"\\?\UNC\server\share\")]
    [InlineData(@"Z:\mapped\report", @"\\?\Z:\mapped\report")]
    [InlineData(@"\\?\UNC\server\share\report", @"\\?\UNC\server\share\report")]
    public void A_walk_root_on_a_share_or_a_mapped_drive_is_spelled_the_way_win32_reads_it(string path, string extended)
        => Assert.Equal(extended, ReachablePath.Extended(path));

    [PosixFact]
    public void On_linux_every_one_of_them_is_its_own_spelling()
    {
        foreach (var path in new[] { "/home/me/ lead.txt", "/home/me/x..y", "/home/me/日本/写真.jpg", "/" })
        {
            Assert.Equal(path, ReachablePath.Exact(path));
            Assert.Equal(path, ReachablePath.Extended(path));
            Assert.Null(ReachablePath.RefuseHandedOut(path));
        }
    }
}
