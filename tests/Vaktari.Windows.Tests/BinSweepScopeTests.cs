using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the Recycle Bin sweep deletes, run for real — on bins of this test's
/// own making.
///
/// **Two findings from the 0.11.1 changelog check.** The sweep reached every
/// drive's bin although the bin page's help says files deleted from another
/// drive are not covered; and "Delete the largest until it fits" deleted the
/// oldest, because the sweep never read the choice.
///
/// None of this goes near the real Recycle Bin. A bin here is a folder shaped
/// <c>&lt;drive&gt;\$Recycle.Bin\&lt;sid&gt;</c> under a temporary root, holding
/// "$I" metadata files and "$R" payloads, and the sweep is handed those folders
/// and nothing else. Every date is in 2001, so even a sweep that somehow read a
/// real bin would find nothing that old to age out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BinSweepScopeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-binsweep").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    /// <summary>A stand-in for one drive's root, under this test's own folder.</summary>
    private string Drive(string letter) => Directory.CreateDirectory(Path.Combine(_root, letter)).FullName;

    /// <summary>That drive's bin for one user, shaped as Windows shapes it.</summary>
    private static string Bin(string drive)
        => Directory.CreateDirectory(Path.Combine(drive, "$Recycle.Bin", "S-1-5-21-0-0-0-1001")).FullName;

    /// <summary>A version 2 record, the shape Windows 10 and later write.</summary>
    private static byte[] Version2(string path, long size, DateTimeOffset deleted)
    {
        var chars = Encoding.Unicode.GetBytes(path);
        var bytes = new byte[28 + chars.Length + 2];

        BitConverter.TryWriteBytes(bytes.AsSpan(0), 2L);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), deleted.ToFileTime());
        BitConverter.TryWriteBytes(bytes.AsSpan(24), path.Length + 1);
        chars.CopyTo(bytes, 28);

        return bytes;
    }

    /// <summary>One item in <paramref name="bin"/>: its "$I" record, claiming
    /// <paramref name="size"/> bytes, and a "$R" payload beside it.</summary>
    private string Binned(string bin, string name, long size, DateTimeOffset deleted)
    {
        var id = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant() + ".txt";

        File.WriteAllBytes(Path.Combine(bin, "$I" + id), Version2(Path.Combine(_root, name), size, deleted));

        var payload = Path.Combine(bin, "$R" + id);
        File.WriteAllText(payload, name);

        return payload;
    }

    private static TrashSweepResult Sweep(TrashSettings policy, IReadOnlyList<string> bins, long allowance = 0)
        => WindowsTrashMaintenance.Sweep(policy, bins, allowance, Now, CancellationToken.None);

    /// <summary>
    /// The age half: an item past the cutoff on the system drive goes, and the
    /// same item on another drive stays, as the page says it does.
    /// </summary>
    [WindowsFact]
    public void An_old_item_in_another_drives_bin_is_not_swept()
    {
        var system = Drive("C");
        var home = Bin(system);
        var other = Bin(Drive("D"));

        var mine = Binned(home, "old-here.txt", 10, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var theirs = Binned(other, "old-there.txt", 10, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = Sweep(
            new TrashSettings { DeleteOldFiles = true, DeleteAfterDays = 30 },
            WindowsTrashMaintenance.SweptBins([home, other], system));

        Assert.False(File.Exists(mine), "the system drive's old item should have been swept");
        Assert.True(File.Exists(theirs), "another drive's bin is not covered, and its item should still be there");
        Assert.Equal(1, result.Removed);
    }

    /// <summary>
    /// The size half: another drive's bin is neither counted against the
    /// allowance nor trimmed to meet it. The system drive's bin is under the
    /// allowance on its own, so nothing is deleted anywhere.
    /// </summary>
    [WindowsFact]
    public void Another_drives_bin_is_neither_counted_nor_trimmed()
    {
        var system = Drive("C");
        var home = Bin(system);
        var other = Bin(Drive("D"));

        var mine = Binned(home, "small.txt", 50, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var theirs = Binned(other, "large.bin", 1000, new DateTimeOffset(2001, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var result = Sweep(
            new TrashSettings
            {
                LimitSize = true, MaximumPercentOfDisk = 10, WhenLimitReached = TrashLimitAction.DeleteOldest,
            },
            WindowsTrashMaintenance.SweptBins([home, other], system),
            allowance: 100);

        Assert.True(File.Exists(mine));
        Assert.True(File.Exists(theirs));
        Assert.Equal(0, result.Removed);
    }

    /// <summary>
    /// The small item is the oldest and the large one the newest, so the two
    /// orders pick different items: largest-first takes the one large file and
    /// fits, oldest-first takes the two small ones before it fits.
    /// </summary>
    private (string Small, string Medium, string Large, TrashSweepResult Result) OverTheLimit(TrashLimitAction action)
    {
        var bin = Bin(Drive("C"));

        var small = Binned(bin, "small.txt", 10, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var medium = Binned(bin, "medium.txt", 100, new DateTimeOffset(2001, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var large = Binned(bin, "large.bin", 1000, new DateTimeOffset(2001, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var result = Sweep(
            new TrashSettings { LimitSize = true, MaximumPercentOfDisk = 10, WhenLimitReached = action },
            [bin],
            allowance: 1050);

        return (small, medium, large, result);
    }

    [WindowsFact]
    public void Delete_the_largest_until_it_fits_deletes_the_largest()
    {
        var (small, medium, large, result) = OverTheLimit(TrashLimitAction.DeleteLargest);

        Assert.False(File.Exists(large), "the largest item should have gone first");
        Assert.True(File.Exists(small), "the oldest item is small, and should have been left");
        Assert.True(File.Exists(medium));
        Assert.Equal(1, result.Removed);
        Assert.Equal(1000, result.BytesFreed);
    }

    /// <summary>The control: the other choice still takes the oldest first.</summary>
    [WindowsFact]
    public void Delete_the_oldest_until_it_fits_deletes_the_oldest()
    {
        var (small, medium, large, result) = OverTheLimit(TrashLimitAction.DeleteOldest);

        Assert.False(File.Exists(small));
        Assert.False(File.Exists(medium));
        Assert.True(File.Exists(large), "the newest item should have been left once the bin fitted");
        Assert.Equal(2, result.Removed);
    }
}
