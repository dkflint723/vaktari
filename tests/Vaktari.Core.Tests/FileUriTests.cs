using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Turning what another application sends into a path this machine can open.
///
/// **Everything that asks a file manager to show something sends a URI**, and
/// the window's own doc comment records what not decoding one cost: the
/// installed desktop entry said %U, so on every desktop that honours it
/// literally "open containing folder" arrived as "file:///home/me/Documents",
/// failed Directory.Exists, and was dropped without a word — on the primary
/// Linux install route.
/// </summary>
public sealed class FileUriTests
{
    [Theory]
    [InlineData("/home/me/Documents", "/home/me/Documents")]
    [InlineData("file:///home/me/Documents", "/home/me/Documents")]
    [InlineData("file:///home/me/My%20Documents", "/home/me/My Documents")]
    public void A_uri_becomes_the_path_it_names(string raw, string expected)
        => Assert.Equal(expected, FileUri.ToLocalPath(raw));

    /// <summary>
    /// RFC 8089 says the empty host and "localhost" both mean this machine.
    ///
    /// **Uri.LocalPath cannot be used once a host is present**, and this is the
    /// trap: System.Uri sets its UNC flag for ANY non-empty host on a file:
    /// URI, so it hands back \\localhost\tmp\x. That is not Windows-only
    /// behaviour, and it fails Directory.Exists on Linux — the same silent drop
    /// this class exists to end, arriving by a different door.
    /// </summary>
    [Theory]
    [InlineData("file://localhost/tmp/x", "/tmp/x")]
    [InlineData("file://localhost/tmp/My%20Notes", "/tmp/My Notes")]
    public void The_local_machine_named_as_a_host_is_still_a_local_path(
        string raw, string expected)
        => Assert.Equal(expected, FileUri.ToLocalPath(raw));

    /// <summary>
    /// And separately from the value: no separator flipped. The failure mode is
    /// a wrong-LOOKING string rather than a null, so a backslash is worth
    /// asking about on its own — in a POSIX path it is a character in a file
    /// name, not a separator.
    /// </summary>
    [Fact]
    public void Without_turning_into_a_network_path()
        => Assert.DoesNotContain('\\', FileUri.ToLocalPath("file://localhost/tmp/x")!);

    /// <summary>
    /// The empty-host branch really is LocalPath, and this is the row that says
    /// so: for file:///C:/Users/me, LocalPath gives the drive path while
    /// AbsolutePath gives "/C:/Users/me". On a POSIX path the two agree, so
    /// nothing else here can tell them apart.
    /// </summary>
    [WindowsFact]
    public void A_drive_named_as_a_uri_comes_back_as_a_drive()
        => Assert.Equal(@"C:\Users\me", FileUri.ToLocalPath("file:///C:/Users/me"));

    /// <summary>'#' is an ordinary character in a POSIX file name and a control
    /// character to Uri, which splits it off as a fragment and hands back a real
    /// file with the wrong name.</summary>
    [Theory]
    [InlineData("file:///tmp/notes#2.txt", "/tmp/notes#2.txt")]
    [InlineData("file:///tmp/notes%232.txt", "/tmp/notes#2.txt")]
    [InlineData("file:///tmp/a?b.txt", "/tmp/a?b.txt")]
    public void A_name_that_looks_like_a_query_or_a_fragment_stays_a_name(
        string raw, string expected)
        => Assert.Equal(expected, FileUri.ToLocalPath(raw));

    [Theory]
    [InlineData("trash:///")]
    [InlineData("sftp://box/home/me/x")]
    [InlineData("recent:///")]
    [InlineData("file://otherbox/share/x")]
    public void What_this_process_cannot_open_is_refused_rather_than_guessed_at(string raw)
        => Assert.Null(FileUri.ToLocalPath(raw));

    /// <summary>A drive letter is not a scheme, and there are more paths
    /// beginning with one than there are one-letter schemes in the world. Pure
    /// string shape, so it runs identically on both CI jobs — which is exactly
    /// why the scheme test rejects a one-character prefix rather than asking
    /// Uri, whose TryCreate accepts a Windows path as an absolute file URI.
    /// </summary>
    [Fact]
    public void A_windows_drive_is_handed_back_whole()
        => Assert.Equal(@"C:\Users\me", FileUri.ToLocalPath(@"C:\Users\me"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_nothing(string? raw) => Assert.Null(FileUri.ToLocalPath(raw));

    /// <summary>
    /// The house rule, checked rather than trusted: a capability a platform does
    /// not have is ABSENT, not a no-op that reports success.
    ///
    /// And Windows has no such role to claim — the shell owns that gesture — so
    /// nothing there names it. The directory sweep is what stops somebody
    /// adding a stub later "for symmetry", which is how an absent capability
    /// becomes one that silently does nothing.
    /// </summary>
    [Fact]
    public void A_platform_that_says_nothing_has_no_file_manager_service()
    {
        Assert.Contains(
            "IFileManagerService? FileManagerService => null;",
            RepoSource.Read("src", "Vaktari.Core", "IPlatform.cs"),
            StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoSource.Root, "src", "Vaktari.Windows"), "*.cs"))
            Assert.DoesNotContain(
                "FileManagerService", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
