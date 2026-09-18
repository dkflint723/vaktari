using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the walk under space usage takes for a link, with the reader of reparse
/// tags the Windows platform adopts.
///
/// **The walk took every reparse point for a link.** SafeWalk decided by the
/// ReparsePoint attribute alone, yielded such an entry as a link of no length
/// and never entered it, and SpaceUsage.Underneath asked the same question of
/// its own. On Windows the attribute is carried by entries that are not links,
/// with sizes of their own — measured with the third-party tag these tests set:
/// a link of length 0 while FileInfo.Length said 1,234. The first repair asked
/// for a link target instead, and a link made by WSL has none that .NET reads:
/// measured, a folder one reads [Directory, ReparsePoint] with LinkTarget null,
/// and cannot be entered. The rule is the tag's name-surrogate bit, and the
/// tests below pin it from both sides.
///
/// No test builds a cloud placeholder. That takes a registered sync root, and a
/// .NET process runs in placeholder compatibility mode 1, which disguises
/// placeholders — measured against a Proton Drive sync root, its folders and
/// files carried no ReparsePoint at all in such a process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SafeWalkLinkTests : IDisposable
{
    private readonly TempTree _tree = new();

    private readonly List<(string Path, bool Directory, bool Wsl)> _tagged = [];

    public SafeWalkLinkTests()
    {
        // Adopted the way WindowsPlatform adopts it, and never put back: every
        // walk in this assembly wants the platform's answer, and no test here
        // sets anything else, so classes running side by side cannot disagree.
        SafeWalk.ReparseTag = ReparseTags.Of;
    }

    /// <summary>Untagged first — a folder under a tag no filter owns cannot be
    /// entered, and the tree's delete would leave it behind — then the tree.</summary>
    public void Dispose()
    {
        foreach (var (path, directory, wsl) in _tagged)
        {
            try
            {
                if (wsl) ReparseFixture.RemoveWsl(path, directory);
                else ReparseFixture.RemoveThirdParty(path, directory);
            }
            catch (Exception) { /* the tree's delete says what it cannot take */ }
        }

        _tree.Dispose();
    }

    private string ThirdPartyFile(int length, params string[] parts)
    {
        var path = _tree.At(parts);

        File.WriteAllBytes(path, new byte[length]);
        ReparseFixture.SetThirdParty(path, directory: false);
        _tagged.Add((path, false, false));

        return Premise(path);
    }

    private string ThirdPartyFolder(params string[] parts)
    {
        var path = _tree.Dir(parts);

        ReparseFixture.SetThirdParty(path, directory: true);
        _tagged.Add((path, true, false));

        return Premise(path);
    }

    private string WslLink(bool directory, params string[] parts)
    {
        var path = _tree.At(parts);

        if (directory) Directory.CreateDirectory(path);
        else File.WriteAllBytes(path, []);

        ReparseFixture.SetWsl(path, directory);
        _tagged.Add((path, directory, true));

        return Premise(path);
    }

    /// <summary>The premise, checked rather than assumed: the attribute is
    /// there, and .NET reads no link behind it.</summary>
    private static string Premise(string path)
    {
        Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "the reparse point did not take");

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

        Assert.Null(info.LinkTarget);

        return path;
    }

    /// <summary>**The walk yields a file under a tag that is not a link as the
    /// file it is, with its length.**</summary>
    [WindowsFact]
    public void A_file_under_a_tag_that_is_not_a_link_is_walked_as_a_file()
    {
        ThirdPartyFile(1_234, "sized.bin");

        var found = Assert.Single(SafeWalk.Descend(_tree.Root).ToList());

        Assert.False(found.IsLink, "a reparse point whose tag is not a name surrogate was reported as a link");
        Assert.Equal(1_234, found.Length);
    }

    /// <summary>And a measure of the folder counts it, beside a file that
    /// carries nothing.</summary>
    [WindowsFact]
    public void Measuring_a_folder_counts_such_a_file_at_its_size()
    {
        ThirdPartyFile(1_234, "sized.bin");
        _tree.Write("plain.bin", "sixsix");

        var usage = SpaceUsage.Measure(_tree.Root, null, CancellationToken.None);

        Assert.Equal(1_240, usage.Bytes);
        Assert.Equal(2, usage.Files);
    }

    /// <summary>
    /// **The listing asks its own question**, so it is pinned on its own: a row
    /// that says link is drawn as one, and its bytes are what the folder's total
    /// adds up.
    /// </summary>
    [WindowsFact]
    public void The_listing_shows_such_a_file_as_a_file_with_its_bytes()
    {
        var path = ThirdPartyFile(1_234, "sized.bin");

        var row = Assert.Single(SpaceUsage.Underneath(_tree.Root, null, CancellationToken.None).Rows);

        Assert.Equal(path, row.Path);
        Assert.False(row.IsLink, "a reparse point whose tag is not a name surrogate was listed as a link");
        Assert.Equal(1_234, row.Usage.Bytes);
    }

    /// <summary>
    /// A junction is still a link: reported, never entered. It passed before
    /// this change as well, and says so — the attribute took it for a link too —
    /// but it is the half of the tag rule a mutation can break.
    /// </summary>
    [WindowsFact]
    public void A_junction_is_still_a_link_and_is_never_entered()
    {
        var outside = _tree.Dir("outside");
        _tree.Write("outside/precious.raw", "the photo library");

        var measured = _tree.Dir("measured");
        var junction = _tree.Junction("measured/shortcut", outside);

        var found = SafeWalk.Descend(measured).ToList();

        Assert.Contains(found, f => f.IsLink && f.Path == junction);
        Assert.DoesNotContain(found, f => f.Path.EndsWith("precious.raw", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A folder link made by WSL is a link: reported, never entered, and
    /// nothing unreadable about it.** Asked for a link target, the walk took it
    /// for a folder, entered it, and could not open it.
    /// </summary>
    [WindowsFact]
    public void A_folder_link_made_by_wsl_is_a_link_and_is_never_entered()
    {
        var measured = _tree.Dir("measured");
        var link = WslLink(directory: true, "measured", "linked");

        var unreadable = new List<string>();
        var only = Assert.Single(SafeWalk.Descend(measured, CancellationToken.None, unreadable.Add).ToList());

        Assert.Equal(link, only.Path);
        Assert.True(only.IsLink, "a folder link made by WSL was not reported as a link");
        Assert.False(only.IsDirectory, "a folder link made by WSL was reported as a folder");
        Assert.Empty(unreadable);

        var row = Assert.Single(SpaceUsage.Underneath(measured, null, CancellationToken.None).Rows);

        Assert.True(row.IsLink, "a folder link made by WSL was not listed as a link");
    }

    /// <summary>And a file link made by WSL is listed as a link of no size.</summary>
    [WindowsFact]
    public void A_file_link_made_by_wsl_is_listed_as_a_link()
    {
        var link = WslLink(directory: false, "notes.txt");

        var row = Assert.Single(SpaceUsage.Underneath(_tree.Root, null, CancellationToken.None).Rows);

        Assert.Equal(link, row.Path);
        Assert.True(row.IsLink, "a file link made by WSL was not listed as a link");
        Assert.Equal(0, row.Usage.Bytes);
    }

    /// <summary>
    /// **A tag that cannot be read makes a link.** The entry's attributes are
    /// read while it is there, and it is gone by the time its tag is asked for —
    /// the shape of a walk racing a delete — so the reader answers null. Under
    /// this tag, a reader that could still read it would have said "not a link".
    /// </summary>
    [WindowsFact]
    public void An_entry_whose_tag_cannot_be_read_is_taken_for_a_link()
    {
        var path = ThirdPartyFile(10, "going.bin");

        var entry = new FileInfo(path);

        // Read now, and kept by the FileInfo from here on.
        Assert.True((entry.Attributes & FileAttributes.ReparsePoint) != 0);

        ReparseFixture.RemoveThirdParty(path, directory: false);
        _tagged.Clear();
        File.Delete(path);

        Assert.Null(ReparseTags.Of(path));
        Assert.True(SafeWalk.IsLink(entry), "an entry whose tag could not be read was taken for something other than a link");
    }

    /// <summary>
    /// **A folder under a tag that is not a link is entered**, as a placeholder
    /// folder is — the decision this rule makes deliberately. One whose tag no
    /// filter owns cannot be opened, measured, so the walk says it could not
    /// read it rather than calling it a link.
    /// </summary>
    [WindowsFact]
    public void A_folder_under_a_tag_that_is_not_a_link_is_entered_and_said_to_be_unreadable()
    {
        var measured = _tree.Dir("measured");
        var tagged = ThirdPartyFolder("measured", "held");

        var unreadable = new List<string>();
        var only = Assert.Single(SafeWalk.Descend(measured, CancellationToken.None, unreadable.Add).ToList());

        Assert.Equal(tagged, only.Path);
        Assert.True(only.IsDirectory, "a folder under a tag that is not a link was not walked as a folder");
        Assert.Equal(tagged, Assert.Single(unreadable));
    }

    /// <summary>
    /// The wiring, which no unit can be asked about: Core holds the seam and
    /// cannot reference this assembly, so the one place a platform is chosen is
    /// the one place it can be filled in.
    /// </summary>
    [WindowsFact]
    public void The_windows_platform_adopts_the_reader()
        => Assert.Contains(
            "SafeWalk.ReparseTag = ReparseTags.Of;",
            RepoSource.Read("src", "Vaktari.Windows", "WindowsPlatform.cs"),
            StringComparison.Ordinal);
}
