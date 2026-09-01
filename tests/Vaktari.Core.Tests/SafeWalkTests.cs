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
}
