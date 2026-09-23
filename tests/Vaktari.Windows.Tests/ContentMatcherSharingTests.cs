using System.Runtime.Versioning;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What ContentMatcher does when another program has the file open — which on
/// Windows decides whether the open is allowed at all.
///
/// Here rather than beside the matcher's own tests because share modes are
/// Windows' rule: on Linux any number of handles may hold a file, and these
/// would pass there whatever the matcher asked for.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ContentMatcherSharingTests
{
    private static ContentVerdict Search(string path)
        => ContentMatcher.FileContains(path, new FileInfo(path).Length, "milk", false, CancellationToken.None);

    /// <summary>
    /// **A file another program is writing is still read.** A log being
    /// appended to is exactly what somebody searches, and an open that asked
    /// for FileShare.Read alone would be refused while the writer holds it.
    /// </summary>
    [WindowsFact]
    public void A_file_open_for_writing_elsewhere_is_still_read()
    {
        using var tree = new TempTree();

        var path = tree.Write("app.log", "remember the milk");

        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal(ContentVerdict.Found, Search(path));
    }

    /// <summary>
    /// And one that another program has open to delete — a handle with delete
    /// access, which refuses any open that does not share Delete.
    /// </summary>
    [WindowsFact]
    public void A_file_open_to_be_deleted_elsewhere_is_still_read()
    {
        using var tree = new TempTree();

        var path = tree.Write("scratch.tmp", "remember the milk");

        using var doomed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                                          FileShare.ReadWrite | FileShare.Delete, 4096,
                                          FileOptions.DeleteOnClose);

        Assert.Equal(ContentVerdict.Found, Search(path));
    }

    /// <summary>
    /// **A read that fails after the open succeeded is reported, not thrown.**
    /// Another handle has locked the bytes, so the open is allowed and the read
    /// is refused — the only way to reach the catch around the read, since
    /// every other refusal happens at the open.
    /// </summary>
    [WindowsFact]
    public void A_file_that_fails_part_way_is_unreadable_rather_than_thrown()
    {
        using var tree = new TempTree();

        var path = tree.Write("locked.db", "remember the milk");

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        holder.Lock(0, holder.Length);

        Assert.Equal(ContentVerdict.Unreadable, Search(path));
    }
}
