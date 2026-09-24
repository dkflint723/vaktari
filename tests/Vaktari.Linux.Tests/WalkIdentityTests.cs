using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The shared walk and the duplicate finder on Linux: a folder that lists but
/// cannot be searched, and two names for one file.
///
/// **One such folder failed the whole scan.** Creating the enumeration opens
/// nothing, so the walk's guard around it caught nothing; the first MoveNext
/// in a folder that can be read but not searched (mode 0600) threw straight
/// out of the walk, and a space-usage or duplicates scan of any folder above it
/// failed as a whole. And a hard link, a second name for one file, was offered
/// as a copy of itself.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class WalkIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-walkid-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly Func<string, (ulong, ulong, ulong)?>? _identityBefore = DuplicateFinder.Identity;

    public WalkIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        DuplicateFinder.Identity = _identityBefore;

        if (OperatingSystem.IsLinux())
        {
            foreach (var folder in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (Exception) { }
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [PosixFact]
    public void A_folder_that_lists_but_cannot_be_searched_does_not_end_the_walk()
    {
        Write("closed/a.bin", "inside");
        Directory.CreateDirectory(Path.Combine(_root, "closed", "inner"));
        var beside = Write("beside.txt", "still found");

        var closed = Path.Combine(_root, "closed");
        File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var unreadable = new List<string>();
        var found = SafeWalk.Descend(_root, default, unreadable.Add).Select(f => f.Path).ToList();

        Assert.Contains(beside, found);
        Assert.Contains(closed, unreadable);
    }

    /// <summary>
    /// The space-usage view lists its own top level outside the walk, and a
    /// folder there that can be read but not searched failed the whole view.
    /// </summary>
    [PosixFact]
    public void Space_usage_of_a_folder_that_cannot_be_searched_says_so()
    {
        Write("closed/a.bin", "inside");
        Directory.CreateDirectory(Path.Combine(_root, "closed", "inner"));

        var closed = Path.Combine(_root, "closed");
        File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var listing = SpaceUsage.Underneath(closed, null, default);

        Assert.Equal(1, listing.Total.Unreadable);
    }

    /// <summary>A second name for one file is not its copy. <c>ln</c> needs no privilege.</summary>
    [PosixFact]
    public void Two_hard_links_to_one_file_are_not_duplicates()
    {
        var first = Write("a.txt", "the same bytes, one file");
        var second = Path.Combine(_root, "b.txt");

        using (var ln = Process.Start("ln", [first, second]))
            ln.WaitForExit();

        Assert.True(File.Exists(second), "ln made no hard link, so this proves nothing");

        DuplicateFinder.Identity = null;
        Assert.Single(DuplicateFinder.Find(_root, null, default).Sets);

        DuplicateFinder.Identity = path => FileIdentity.Of(path) is { } id ? (id.Device, id.Inode, 0) : null;
        Assert.Empty(DuplicateFinder.Find(_root, null, default).Sets);
    }

    /// <summary>
    /// **What is not a file says so.** A FIFO, and a link to one, are special;
    /// a file and a folder are not — and properties carries it, which is what
    /// keeps the checksum button away from a pipe.
    /// </summary>
    [PosixFact]
    public async Task A_fifo_and_a_link_to_one_are_special_and_a_file_is_not()
    {
        var fifo = Path.Combine(_root, "pipe");

        using (var mkfifo = Process.Start("mkfifo", [fifo]))
            await mkfifo.WaitForExitAsync();

        var link = Path.Combine(_root, "to-pipe");
        File.CreateSymbolicLink(link, fifo);

        var file = Write("notes.txt", "text");

        Assert.True(FileIdentity.IsSpecial(fifo));
        Assert.True(FileIdentity.IsSpecial(link));
        Assert.False(FileIdentity.IsSpecial(file));
        Assert.False(FileIdentity.IsSpecial(_root));

        Assert.True((await new LinuxPropertiesProvider().GetAsync(fifo, default)).IsSpecial);
        Assert.False((await new LinuxPropertiesProvider().GetAsync(file, default)).IsSpecial);
    }

    /// <summary>The reader itself: one folder by two paths, and two folders.</summary>
    [PosixFact]
    public void A_folder_reached_through_a_link_is_the_same_folder()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var link = Path.Combine(_root, "via");
        Directory.CreateSymbolicLink(link, real);

        Assert.True(FileIdentity.Same(real, link));
        Assert.False(FileIdentity.Same(real, other));
        Assert.False(FileIdentity.Same(real, Path.Combine(_root, "not-there")));
    }
}
