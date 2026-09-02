using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Deleting on a volume whose top directory you cannot write to.
///
/// **The file could not be deleted at all.** RootFor is careful to fall back to
/// the home trash when a volume will not say where it is mounted — and nothing
/// guarded the very next step. Creating $topdir/.Trash-$uid needs write
/// permission on the TOP of the volume, and plenty of mounts hand out a
/// writable subtree under a root-owned top: a data mount, /srv, /opt on its own
/// filesystem, a stick whose top belongs to root. The mkdir threw straight out,
/// so the delete failed, while the user's own home trash was available the
/// whole time and is what the spec names as the fallback.
/// </summary>
public sealed class VolumeTrashTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-voltrash-" + Guid.NewGuid().ToString("N")[..8]);

    public VolumeTrashTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_writable_volume_keeps_its_own_trash()
    {
        var preferred = Path.Combine(_root, ".Trash-1000");

        Assert.Equal(preferred, XdgTrash.PrepareRoot(preferred));

        // And it really made it, rather than merely agreeing it would.
        Assert.True(Directory.Exists(Path.Combine(preferred, "files")));
        Assert.True(Directory.Exists(Path.Combine(preferred, "info")));
    }

    /// <summary>
    /// The case the finding is about. A path that cannot be created falls back
    /// to the home trash rather than throwing, which is the difference between
    /// a file that goes and a file that cannot be deleted.
    /// </summary>
    [PosixFact]
    public void A_volume_top_that_will_not_take_a_trash_falls_back_home()
    {
        // A file where the trash directory would go: creating a directory over
        // it is refused by the kernel, without needing a root-owned mount.
        var blocked = Path.Combine(_root, ".Trash-1000");
        File.WriteAllText(blocked, "not a directory");

        var chosen = XdgTrash.PrepareRoot(blocked);

        Assert.Equal(XdgTrash.TrashRoot, chosen);
        Assert.True(Directory.Exists(Path.Combine(chosen, "files")));
    }

    /// <summary>
    /// **The home trash is the one that just failed.** There is nowhere left to
    /// fall back to, and swallowing it would hide the real reason from the
    /// per-item report the caller builds — a file reported as deleted that was
    /// not is worse than a file reported as refused.
    /// </summary>
    /// <summary>
    /// The two halves no behaviour test can reach: that Trash goes through the
    /// fallback at all, and that a failing HOME trash is reported rather than
    /// swallowed. Proving the second by making the developer's own trash
    /// unusable is not something a test should attempt.
    /// </summary>
    [Fact]
    public void The_fallback_is_reached_and_does_not_swallow_a_home_failure()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var source = File.ReadAllText(
            Path.Combine(here!, "src", "Vaktari.Linux", "XdgTrash.cs"));

        Assert.Contains("var root = PrepareRoot(RootFor(full));", source);

        // The unguarded pair this replaced would put the throw back.
        Assert.DoesNotContain("Directory.CreateDirectory(filesDir);", source);

        // Nowhere left to fall back to: rethrow, or a file reported as deleted
        // was not, which is worse than one reported as refused.
        Assert.Contains(
            "if (string.Equals(preferred, home, StringComparison.Ordinal)) throw;",
            source);
    }
}
