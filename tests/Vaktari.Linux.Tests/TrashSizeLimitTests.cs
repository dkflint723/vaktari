using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Which items the size limit takes when the trash is over it.
///
/// The Windows sweep deleted the oldest under "Delete the largest until it
/// fits" (0.11.1 changelog check); this side has read the choice all along,
/// and these pin the ordering both now share. Against a trash of this test's
/// own making through <c>XDG_DATA_HOME</c>, with the allowance handed in rather
/// than measured from a disk.
/// </summary>
public sealed class TrashSizeLimitTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "vaktari-trashlimit-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public TrashSizeLimitTests()
    {
        Directory.CreateDirectory(Files);
        Directory.CreateDirectory(Info);

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _before);

        try { Directory.Delete(_home, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Files => Path.Combine(_home, "Trash", "files");
    private string Info => Path.Combine(_home, "Trash", "info");

    /// <summary>One item of <paramref name="size"/> bytes, the way any desktop would write it.</summary>
    private string Trashed(string name, int size, string when)
    {
        var payload = Path.Combine(Files, name);
        File.WriteAllBytes(payload, new byte[size]);

        File.WriteAllText(
            Path.Combine(Info, name + ".trashinfo"),
            $"[Trash Info]\nPath=/home/me/{name}\nDeletionDate={when}\n");

        return payload;
    }

    /// <summary>
    /// The small item is the oldest and the large one the newest, so the two
    /// orders pick different items: largest-first takes the one large file and
    /// fits, oldest-first takes the two small ones before it fits.
    /// </summary>
    private (string Small, string Medium, string Large, TrashSweepResult Result) OverTheLimit(TrashLimitAction action)
    {
        var small = Trashed("small.txt", 10, "2001-01-01T10:00:00");
        var medium = Trashed("medium.txt", 100, "2001-02-01T10:00:00");
        var large = Trashed("large.bin", 1000, "2001-03-01T10:00:00");

        var result = XdgTrashMaintenance.Sweep(
            new TrashSettings { LimitSize = true, MaximumPercentOfDisk = 10, WhenLimitReached = action },
            _ => 1050,
            CancellationToken.None);

        return (small, medium, large, result);
    }

    [Fact]
    public void Delete_the_largest_until_it_fits_deletes_the_largest()
    {
        var (small, medium, large, result) = OverTheLimit(TrashLimitAction.DeleteLargest);

        Assert.False(File.Exists(large), "the largest item should have gone first");
        Assert.True(File.Exists(small), "the oldest item is small, and should have been left");
        Assert.True(File.Exists(medium));
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Delete_the_oldest_until_it_fits_deletes_the_oldest()
    {
        var (small, medium, large, result) = OverTheLimit(TrashLimitAction.DeleteOldest);

        Assert.False(File.Exists(small));
        Assert.False(File.Exists(medium));
        Assert.True(File.Exists(large), "the newest item should have been left once the trash fitted");
        Assert.Equal(2, result.Removed);
    }
}
