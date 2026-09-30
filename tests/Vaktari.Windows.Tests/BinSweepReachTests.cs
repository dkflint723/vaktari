using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// How far the Recycle Bin sweep reaches, beyond BinSweepScopeTests (fix-12
/// verification): which bin it picks when the drives are named as Windows
/// names them, a Windows installed somewhere other than C:, the Warn choice,
/// and the two halves run together.
///
/// Nothing here touches the real Recycle Bin: bins are folders under this
/// test's own temporary root, or plain strings that are never opened.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BinSweepReachTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset January = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset May = new(2001, 5, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-binreach").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    // ---- which bin, by name -------------------------------------------------------------

    private const string Sid = "S-1-5-21-0-0-0-1001";

    /// <summary>The bins RecycleBin.Directories hands over on a machine with
    /// a fixed C:, a second fixed disk D: and a USB stick E: — the shape
    /// DriveInfo.RootDirectory gives, never opened here.</summary>
    private static readonly string[] Bins =
    [
        $@"C:\$Recycle.Bin\{Sid}",
        $@"D:\$Recycle.Bin\{Sid}",
        $@"E:\$Recycle.Bin\{Sid}",
    ];

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\", @"D:\")] // Windows installed on D:
    [InlineData(@"E:\", @"E:\")]
    [InlineData(@"d:\", @"D:\")] // a root spelt in the other case is the same drive
    public void Only_the_system_drives_bin_is_swept_whatever_its_letter(string systemRoot, string expectedDrive)
        => Assert.Equal([$@"{expectedDrive}$Recycle.Bin\{Sid}"], WindowsTrashMaintenance.SweptBins(Bins, systemRoot));

    /// <summary>The system root comes from the System folder's own path, so a
    /// Windows on D: names D:, and one whose root cannot be read sweeps
    /// nothing rather than everything.</summary>
    [Fact]
    public void A_windows_on_another_drive_names_that_drive_and_no_root_sweeps_nothing()
    {
        Assert.Equal(@"D:\", Path.GetPathRoot(@"D:\Windows\system32"));
        Assert.Equal([$@"D:\$Recycle.Bin\{Sid}"], WindowsTrashMaintenance.SweptBins(Bins, Path.GetPathRoot(@"D:\Windows\system32")));

        Assert.Empty(WindowsTrashMaintenance.SweptBins(Bins, null));
        Assert.Empty(WindowsTrashMaintenance.SweptBins(Bins, ""));
    }

    /// <summary>
    /// **The unattended sweep is handed the system drive's bin and the system
    /// drive's allowance, and nothing else.** The seam the other tests run is
    /// only half of it; the call that feeds it the real bins is the other
    /// half, and it cannot be run here without the real Recycle Bin. Read
    /// instead, so dropping SweptBins from the wiring (every drive swept again)
    /// or asking another share of the disk is caught.
    /// </summary>
    [Fact]
    public void The_timer_sweep_is_wired_through_the_system_drives_bin()
    {
        var source = RepoSource.Read("src", "Vaktari.Windows", "WindowsTrashMaintenance.cs");
        var wiring = RepoSource.Body(source, "private static TrashSweepResult Sweep(");

        Assert.Contains("SweptBins(RecycleBin.Directories(), SystemRoot())", wiring, StringComparison.Ordinal);
        Assert.Contains("policy.LimitSize ? Allowance(policy.MaximumPercentOfDisk) : 0", wiring, StringComparison.Ordinal);
        Assert.Contains(
            "Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))",
            RepoSource.Body(source, "private static string? SystemRoot("), StringComparison.Ordinal);
    }

    // ---- the sweep, run on bins of this test's own ---------------------------------------

    private string Bin(string drive)
        => Directory.CreateDirectory(Path.Combine(_root, drive, "$Recycle.Bin", Sid)).FullName;

    private string DriveRoot(string drive) => Path.Combine(_root, drive);

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

    private string Binned(string bin, string name, long size, DateTimeOffset deleted)
    {
        var id = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant() + ".txt";

        File.WriteAllBytes(Path.Combine(bin, "$I" + id), Version2(Path.Combine(_root, name), size, deleted));

        var payload = Path.Combine(bin, "$R" + id);
        File.WriteAllText(payload, name);

        return payload;
    }

    private TrashSweepResult Sweep(TrashSettings policy, string systemDrive, long allowance, params string[] bins)
        => WindowsTrashMaintenance.Sweep(
            policy, WindowsTrashMaintenance.SweptBins(bins, DriveRoot(systemDrive)), allowance, Now, CancellationToken.None);

    /// <summary>
    /// **Warn warns about the system drive's bin only.** Another drive's bin
    /// far over the allowance is not a reason to say so; the system drive's
    /// over it is — and Warn deletes nothing in either.
    /// </summary>
    [WindowsFact]
    public void Warn_speaks_of_the_system_drives_bin_only_and_deletes_nothing()
    {
        var home = Bin("C");
        var stick = Bin("E");

        var small = Binned(home, "small.txt", 50, January);
        var huge = Binned(stick, "huge.iso", 10_000, January);

        var policy = new TrashSettings { LimitSize = true, MaximumPercentOfDisk = 10, WhenLimitReached = TrashLimitAction.Warn };

        var quiet = Sweep(policy, "C", allowance: 100, home, stick);

        Assert.False(quiet.OverLimit, "a USB stick's bin was counted against the system drive's share");
        Assert.Equal(0, quiet.Removed);

        var large = Binned(home, "large.bin", 500, January);

        var loud = Sweep(policy, "C", allowance: 100, home, stick);

        Assert.True(loud.OverLimit, "the system drive's bin is over its share and Warn said nothing");
        Assert.Equal(0, loud.Removed);
        Assert.True(File.Exists(small) && File.Exists(large) && File.Exists(huge), "Warn deleted something");
    }

    /// <summary>
    /// **The two halves together, on the system drive with a Windows on D:.**
    /// The age half takes the old large item; the size half then counts what
    /// is LEFT — so it fits, and nothing else goes. The other drives' old
    /// items are left by both halves.
    /// </summary>
    [WindowsFact]
    public void Age_then_size_counts_what_the_age_half_left_and_stays_on_the_system_drive()
    {
        var home = Bin("D");
        var first = Bin("C");
        var stick = Bin("E");

        var oldLarge = Binned(home, "old-large.bin", 1000, January);
        var recentA = Binned(home, "recent-a.txt", 40, May);
        var recentB = Binned(home, "recent-b.txt", 40, May);
        var otherOld = Binned(first, "other-old.bin", 5000, January);
        var stickOld = Binned(stick, "stick-old.bin", 5000, January);

        var result = Sweep(
            new TrashSettings
            {
                DeleteOldFiles = true, DeleteAfterDays = 30,
                LimitSize = true, MaximumPercentOfDisk = 10, WhenLimitReached = TrashLimitAction.DeleteLargest,
            },
            "D", allowance: 100, home, first, stick);

        Assert.False(File.Exists(oldLarge), "past the cutoff on the system drive, and still there");
        Assert.True(File.Exists(recentA) && File.Exists(recentB), "the size half deleted what fitted once the age half had run");
        Assert.True(File.Exists(otherOld), "C: is not the system drive here, and its bin was swept");
        Assert.True(File.Exists(stickOld), "a USB stick's bin was swept");
        Assert.Equal(1, result.Removed);
        Assert.Equal(1000, result.BytesFreed);
        Assert.False(result.OverLimit);
    }
}
