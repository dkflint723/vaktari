using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The walk that does not follow links out of the tree.
///
/// **This is a data-safety rule.** The obvious walk descends into linked
/// directories, and the operations performed on what it yields follow links
/// again — chmod is not lchmod — so a folder holding a link to someone's photo
/// library, given a recursive 700, silently rewrote the real library. A link
/// pointing at an ancestor never finished at all.
/// </summary>
public sealed class SafeWalkTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-walk").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Everything_real_underneath_is_found()
    {
        File_("top.txt");
        File_(Path.Combine("a", "b", "deep.txt"));

        var found = SafeWalk.Descend(_root).ToList();

        Assert.Contains(found, f => f.Path.EndsWith("top.txt"));
        Assert.Contains(found, f => f.Path.EndsWith("deep.txt"));
        Assert.Contains(found, f => f.IsDirectory && f.Path.EndsWith("b"));
    }

    /// <summary>
    /// **The one that matters.** The link is reported so a caller can count or
    /// skip it, and what it points at is never entered.
    /// </summary>
    [Fact]
    public void A_linked_directory_is_reported_but_never_entered()
    {
        var outside = Directory.CreateTempSubdirectory("vaktari-outside").FullName;

        try
        {
            File.WriteAllText(Path.Combine(outside, "precious.raw"), "the photo library");

            var inside = Dir("folder");
            var link = Path.Combine(inside, "shortcut");

            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation to make one. The
                // rule is the same on both platforms; only Linux can prove it
                // here, and CI runs there.
                return;
            }

            var found = SafeWalk.Descend(_root).ToList();

            Assert.Contains(found, f => f.IsLink && f.Path.EndsWith("shortcut"));

            Assert.DoesNotContain(
                found,
                f => f.Path.EndsWith("precious.raw"));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A link pointing back up the tree used to mean an endless walk. Bounded
    /// here by the same rule, so the test finishes rather than hanging.
    /// </summary>
    [Fact]
    public void A_link_pointing_at_an_ancestor_does_not_loop()
    {
        var inside = Dir("folder");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(inside, "up"), _root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // The assertion is that this returns at all.
        var found = SafeWalk.Descend(_root).Take(500).ToList();

        Assert.Contains(found, f => f.IsLink);
        Assert.True(found.Count < 500, "the walk did not terminate");
    }

    /// <summary>
    /// An unreadable folder is skipped rather than thrown from: a walk that
    /// dies on the first permission denied reports nothing about the thousands
    /// of entries it could have handled.
    /// </summary>
    [Fact]
    public void A_missing_root_yields_nothing_rather_than_throwing()
        => Assert.Empty(SafeWalk.Descend(Path.Combine(_root, "not-there")).ToList());

    // ---- what each entry carries -------------------------------------------

    /// <summary>
    /// **A file's length comes back with it.** The enumeration has just read
    /// the entry, so a caller totalling sizes that had to ask again would stat
    /// every file a second time for a number it was handed and dropped.
    /// </summary>
    [Fact]
    public void A_files_length_comes_back_with_it()
    {
        File.WriteAllBytes(Path.Combine(Dir("folder"), "sized.bin"), new byte[1_234]);

        var found = SafeWalk.Descend(_root).ToList();

        Assert.Equal(1_234, Assert.Single(found, f => f.Path.EndsWith("sized.bin")).Length);

        // A directory has no length of its own to report.
        Assert.Equal(0, Assert.Single(found, f => f.IsDirectory).Length);
    }

    /// <summary>
    /// **A link's length is zero, not what it points at.** Length here is read
    /// from the entry the enumeration produced; a link resolved instead would
    /// make a folder holding links to a media library count as though it held
    /// the library.
    /// </summary>
    [Fact]
    public void A_links_length_is_zero_rather_than_what_it_points_at()
    {
        var outside = Directory.CreateTempSubdirectory("vaktari-outside").FullName;

        try
        {
            var big = Path.Combine(outside, "huge.raw");

            File.WriteAllBytes(big, new byte[9_000]);

            try
            {
                File.CreateSymbolicLink(Path.Combine(_root, "shortcut.raw"), big);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var link = Assert.Single(SafeWalk.Descend(_root).ToList(), f => f.IsLink);

            Assert.Equal(0, link.Length);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **The folder it could not open is named, not just counted.** A caller
    /// told only how many were skipped can say "two folders could not be read";
    /// told which, it can say where. Nothing asserted the path before, so
    /// handing back the root instead of the folder would have gone unnoticed.
    /// </summary>
    [PosixFact, SupportedOSPlatform("linux")]
    public void The_folder_it_could_not_open_is_named()
    {
        // Root reads a mode-000 directory anyway, so the premise does not hold
        // there and the test would fail on something it cannot control.
        if (Environment.IsPrivilegedProcess) return;

        var closed = Dir("closed");

        File.WriteAllText(Path.Combine(closed, "unseen.txt"), "x");
        File.SetUnixFileMode(closed, UnixFileMode.None);

        try
        {
            var skipped = new List<string>();

            var found = SafeWalk.Descend(_root, CancellationToken.None, skipped.Add).ToList();

            Assert.Equal(closed, Assert.Single(skipped));
            Assert.DoesNotContain(found, f => f.Path.EndsWith("unseen.txt"));
        }
        finally
        {
            // Put it back before Dispose, which cannot delete what it cannot
            // open.
            File.SetUnixFileMode(
                closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
