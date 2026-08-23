using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Emptying the Recycle Bin when what was recycled is read-only.
///
/// **This destroyed data and reported success at it.** Windows refuses to delete
/// a read-only file, and .NET's recursive delete removes everything it reaches
/// BEFORE throwing — so emptying the bin gutted a recycled folder up to the
/// first read-only file, then threw, leaving the $I metadata behind. The entry
/// still listed, still advertised its original size, and Restore handed back
/// what was left of it. The application said "removed 0".
///
/// One cloned git repository is enough: git writes its pack files read-only.
/// And it was reachable with nobody watching, because the hourly policy sweep
/// calls the same method — while a single recycled read-only FILE could never
/// be purged at all, so Empty would never actually empty the bin.
///
/// WindowsFileOperations.Delete has cleared the read-only flags first since the
/// same fault was found there; only the bin was missed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleBinPurgeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-purge-" + Guid.NewGuid().ToString("N"));

    public RecycleBinPurgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Only what this test built, under its own root — and its own read-only
        // flags have to come off first or the cleanup hits the same wall.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    /// <summary>A payload and the metadata file that keeps it listed.</summary>
    private RecycleEntry Entry(bool directory, out string payload)
    {
        var info = Path.Combine(_root, "$I" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(info, "metadata");

        payload = Path.Combine(_root, "$R" + Guid.NewGuid().ToString("N")[..6]);

        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(payload, "objects", "pack"));

            var ordinary = Path.Combine(payload, "README.md");
            File.WriteAllText(ordinary, "readme");

            // Exactly what git writes: a pack file, read-only.
            var pack = Path.Combine(payload, "objects", "pack", "pack-abc.pack");
            File.WriteAllText(pack, "pack");
            File.SetAttributes(pack, FileAttributes.ReadOnly);
        }
        else
        {
            File.WriteAllText(payload, "locked");
            File.SetAttributes(payload, FileAttributes.ReadOnly);
        }

        return new RecycleEntry(info, payload, Path.Combine(_root, "original"),
            DateTimeOffset.UtcNow, 4, directory);
    }

    /// <summary>
    /// The one that loses data: the recycled folder must go entirely, or not at
    /// all — never half.
    /// </summary>
    [Fact]
    public void A_recycled_folder_holding_a_read_only_file_is_purged_whole()
    {
        var entry = Entry(directory: true, out var payload);

        Assert.True(WindowsTrashMaintenance.Purge(entry), "the purge should succeed");

        Assert.False(Directory.Exists(payload), "the payload should be gone entirely");
        Assert.False(File.Exists(entry.InfoPath), "and its metadata with it");
    }

    /// <summary>
    /// A single read-only file could never be purged, so Empty never emptied
    /// the bin — it just reported removing nothing, for ever.
    /// </summary>
    [Fact]
    public void A_recycled_read_only_file_is_purged()
    {
        var entry = Entry(directory: false, out var payload);

        Assert.True(WindowsTrashMaintenance.Purge(entry));

        Assert.False(File.Exists(payload));
        Assert.False(File.Exists(entry.InfoPath));
    }

    /// <summary>
    /// An ordinary payload still goes, and its metadata with it — the case that
    /// always worked, which must keep working.
    /// </summary>
    [Fact]
    public void An_ordinary_payload_is_still_purged()
    {
        var info = Path.Combine(_root, "$Iplain.txt");
        var payload = Path.Combine(_root, "$Rplain.txt");

        File.WriteAllText(info, "metadata");
        File.WriteAllText(payload, "plain");

        var entry = new RecycleEntry(
            info, payload, Path.Combine(_root, "original"), DateTimeOffset.UtcNow, 5, false);

        Assert.True(WindowsTrashMaintenance.Purge(entry));
        Assert.False(File.Exists(payload));
        Assert.False(File.Exists(info));
    }

    /// <summary>
    /// A payload that is not there at all is not a crash. The bin's metadata
    /// outliving its payload is an ordinary state — Restore is what removes the
    /// two together.
    /// </summary>
    [Fact]
    public void A_missing_payload_does_not_throw()
    {
        var info = Path.Combine(_root, "$Ighost.txt");
        File.WriteAllText(info, "metadata");

        var entry = new RecycleEntry(
            info, Path.Combine(_root, "$Rghost.txt"),
            Path.Combine(_root, "original"), DateTimeOffset.UtcNow, 0, false);

        // Whatever it answers, it must not throw out of a sweep nobody asked for.
        WindowsTrashMaintenance.Purge(entry);
    }
}
