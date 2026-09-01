using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The rule that decides whether a move renames or rewrites.
///
/// **It is invisible from the outside** — both paths leave exactly the same
/// files behind — which is why it went unnoticed that every move copied every
/// byte, and why the rule is tested directly rather than through the files it
/// produces. The tell that the trick was already known: undoing a move has
/// always used File.Move, so undoing a fifty-gigabyte move was instant while
/// making it was not.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MoveFastPathTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-move").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_move_within_one_volume_renames_instead_of_copying()
    {
        var source = Path.Combine(_root, "clip.mp4");
        var target = Path.Combine(_root, "sorted", "clip.mp4");

        Assert.True(WindowsFileOperations.CanRename(source, target, move: true));
    }

    /// <summary>
    /// A copy must never take the rename path, whatever the volumes: renaming
    /// would remove the original, which is the one thing a copy promises not
    /// to do.
    /// </summary>
    [Fact]
    public void A_copy_never_renames()
    {
        var source = Path.Combine(_root, "clip.mp4");
        var target = Path.Combine(_root, "sorted", "clip.mp4");

        Assert.False(WindowsFileOperations.CanRename(source, target, move: false));
    }

    /// <summary>
    /// Across volumes there is nothing to rename — the bytes genuinely have to
    /// be read and written.
    /// </summary>
    [Fact]
    public void A_move_across_volumes_still_copies()
    {
        var other = OtherVolume();

        if (other is null) return;

        var source = Path.Combine(_root, "clip.mp4");
        var target = Path.Combine(other, "clip.mp4");

        Assert.False(WindowsFileOperations.CanRename(source, target, move: true));
    }

    /// <summary>
    /// The decision matches Volumes.Same, which is the routine the drag layer
    /// has been using to decide copy-versus-move all along. Two answers to the
    /// same question would eventually disagree.
    /// </summary>
    [Fact]
    public void The_rule_agrees_with_the_one_the_drag_layer_uses()
    {
        var source = Path.Combine(_root, "a.txt");
        var target = Path.Combine(_root, "b", "a.txt");

        Assert.Equal(
            Volumes.Same(source, target),
            WindowsFileOperations.CanRename(source, target, move: true));
    }

    /// <summary>A drive letter other than the one the temp folder is on, or
    /// null on a single-volume machine.</summary>
    private string? OtherVolume()
    {
        var mine = Path.GetPathRoot(_root);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;

            try
            {
                if (!drive.IsReady) continue;
            }
            catch (IOException)
            {
                continue;
            }

            if (!string.Equals(drive.Name, mine, StringComparison.OrdinalIgnoreCase))
                return drive.Name;
        }

        return null;
    }
}
