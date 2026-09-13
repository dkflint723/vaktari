using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What the properties dialog says a folder holds, when links are in it.
///
/// **A link was counted at the length of its own text.** The walk refused to
/// descend into a link, and its own comment said why — a folder holding a link
/// to the home directory would otherwise report the size of the home directory
/// — but the length it added for that same link was the link's own size as the
/// filesystem reports it, which for a symbolic link is the length of the path
/// it holds. Measured on 2026-09-12: ten bytes of content beside a link to a
/// 9,000-byte file came to 80 bytes, the 70 being the characters of the link's
/// target path. And a link to a folder was counted among the folders.
///
/// The rule these pin is <see cref="SpaceUsage"/>'s, which has tests behind it:
/// a link is one entry, of no size, and never a folder.
/// </summary>
public sealed class MeasureLinkTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-measure-" + Guid.NewGuid().ToString("N"));

    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "vaktari-measure-outside-" + Guid.NewGuid().ToString("N"));

    public MeasureLinkTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    /// <summary>Only the two folders this class made.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private static ValueTask<SizeProgress> Measure(string path)
        => new LinuxPropertiesProvider().MeasureAsync(path, new Silent(), CancellationToken.None);

    /// <summary>
    /// **The number the dialog shows beside the counts.** Ten bytes of its own
    /// and a link to nine thousand elsewhere is ten bytes — not nine thousand
    /// and ten, and not ten plus the length of the link's text.
    /// </summary>
    [PosixFact]
    public async Task A_link_to_a_file_is_counted_where_it_stands_at_no_size()
    {
        File.WriteAllBytes(Path.Combine(_root, "own.bin"), new byte[10]);

        var big = Path.Combine(_outside, "huge.raw");

        File.WriteAllBytes(big, new byte[9_000]);
        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.raw"), big);

        var total = await Measure(_root);

        Assert.Equal(10, total.Bytes);
        Assert.Equal(2, total.Files);
    }

    /// <summary>
    /// **A link to a folder is one entry, and not a folder.** It was counted as
    /// one because <c>FileSystemEntry.IsDirectory</c> answered about what the
    /// link points at rather than about the thing standing in the folder.
    /// </summary>
    [PosixFact]
    public async Task A_link_to_a_folder_is_an_entry_not_a_folder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "shortcut"), _outside);

        var total = await Measure(_root);

        Assert.Equal(1, total.Folders);
        Assert.Equal(1, total.Files);
        Assert.Equal(0, total.Bytes);
    }

    /// <summary>
    /// GUARD, not a test of the change, and it says so: a folder with no links
    /// in it is measured exactly as it always was. Measured passing before.
    /// </summary>
    [PosixFact]
    public async Task A_folder_without_links_is_measured_as_before()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[40]);
        Directory.CreateDirectory(Path.Combine(_root, "inner"));
        File.WriteAllBytes(Path.Combine(_root, "inner", "b.bin"), new byte[2]);

        var total = await Measure(_root);

        Assert.Equal(42, total.Bytes);
        Assert.Equal(2, total.Files);
        Assert.Equal(1, total.Folders);
    }

    private sealed class Silent : IProgress<SizeProgress>
    {
        public void Report(SizeProgress value) { }
    }
}
