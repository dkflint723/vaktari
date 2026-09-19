using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a reparse point that is not a link is shown as, at the three places
/// that still decided "link" from the attribute alone after the walk and the
/// file operations had stopped.
///
/// **Every reparse point drew the link emblem.** The listing's flag, the
/// details line's "link" and the properties window's "Folder link" each read
/// FileAttributes.ReparsePoint and nothing more — and Windows sets it on an
/// app execution alias, and on whatever else a filter marks for its own
/// purposes, none of which stands for another name. Measured with a file
/// under a third-party tag: the walk said not a link; the row said link, the
/// details line said "link", and a folder under the same tag was a "Folder
/// link". The aliases every Store app leaves under WindowsApps, tag
/// 0x8000001B to the last one, were a folder of arrows.
///
/// Not a cloud placeholder and not a compressed file, though both carry a
/// reparse point underneath: their filters hide the attribute from a process
/// in the default mode, which is where .NET starts — measured against a Proton
/// Drive sync root and against a file compact.exe had just compressed — so
/// nothing here changes for them.
///
/// All three now ask the walk's rule: a reparse point is a link when its tag
/// is a name surrogate, or when the tag cannot be read. The listing reads the
/// tag once per marked row, at the enumeration, and never for an unmarked one
/// — measured at 19-24 us per marked row and nothing for the rest.
///
/// A link WSL made is the other side of every case: tag 0xA000001D, no
/// LinkTarget .NET can read, and a link all the same.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReparseFlagTests
{
    private static readonly WindowsFileSystemProvider Listing = new();

    private static async Task<FileEntry> Enumerated(string folder, string path)
    {
        await foreach (var batch in Listing.EnumerateAsync(
                           folder, new ListingOptions { IncludeHidden = true }, CancellationToken.None))
            foreach (var entry in batch)
                if (PathRules.Same(entry.FullPath, path))
                    return entry;

        throw new InvalidOperationException($"{path} was not enumerated");
    }

    private static async Task<FileEntry> Watched(string path)
        => await Listing.GetEntryAsync(path, CancellationToken.None)
           ?? throw new InvalidOperationException($"{path} was not described");

    private static async Task<FileEntry> Found(string scope, string text)
    {
        var query = new SearchQuery { Text = text, ScopePath = scope, MaxResults = 100 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            return entry;

        throw new InvalidOperationException($"{text} was not found under {scope}");
    }

    private static string Marked(TempTree tree, string name)
    {
        var path = tree.Write(name, "some content, so it has a size");
        ReparseFixture.SetThirdParty(path, directory: false);
        return path;
    }

    private static string WslLink(TempTree tree, string name)
    {
        var path = tree.Write(name, "x");
        ReparseFixture.SetWsl(path, directory: false);
        return path;
    }

    // ---- the listing's flag, on each of the three paths a row arrives by ----

    [WindowsFact]
    public async Task A_marked_file_that_is_not_a_link_is_listed_without_the_flag()
    {
        using var tree = new TempTree();
        var marked = Marked(tree, "placeholder.txt");

        Assert.False((await Enumerated(tree.Root, marked)).IsSymlink, "a marked file that is no link was drawn as one");
    }

    [WindowsFact]
    public async Task A_link_WSL_made_is_listed_with_the_flag()
    {
        using var tree = new TempTree();
        var link = WslLink(tree, "alias");

        Assert.True((await Enumerated(tree.Root, link)).IsSymlink, "a link with no readable target was not drawn as one");
    }

    [WindowsFact]
    public async Task The_watcher_answers_as_the_listing_does()
    {
        using var tree = new TempTree();
        var marked = Marked(tree, "placeholder.txt");
        var link = WslLink(tree, "alias");

        Assert.False((await Watched(marked)).IsSymlink, "the watcher drew a marked file as a link");
        Assert.True((await Watched(link)).IsSymlink, "the watcher missed a link");
    }

    [WindowsFact]
    public async Task A_search_hit_answers_as_the_listing_does()
    {
        using var tree = new TempTree();
        var marked = Marked(tree, "placeholder.txt");

        var hit = await Found(tree.Root, "placeholder");

        Assert.True(PathRules.Same(marked, hit.FullPath), "the search found something else");
        Assert.False(hit.IsSymlink, "the search drew a marked file as a link");
    }

    // ---- the details line and the properties window --------------------------

    [WindowsFact]
    public async Task The_details_line_calls_only_a_link_a_link()
    {
        using var tree = new TempTree();
        var marked = Marked(tree, "placeholder.txt");
        var link = WslLink(tree, "alias");

        var provider = new WindowsMetadataProvider();

        var forMarked = await provider.DescribeAccessAsync(marked, isDirectory: false, CancellationToken.None);
        var forLink = await provider.DescribeAccessAsync(link, isDirectory: false, CancellationToken.None);

        Assert.DoesNotContain("link", forMarked ?? "");
        Assert.Contains("link", forLink ?? "");
    }

    [WindowsFact]
    public async Task The_properties_window_calls_a_marked_folder_a_folder()
    {
        using var tree = new TempTree();

        var marked = tree.Dir("projected");
        ReparseFixture.SetThirdParty(marked, directory: true);

        var link = tree.Dir("alias");
        ReparseFixture.SetWsl(link, directory: true);

        var provider = new WindowsPropertiesProvider();

        Assert.Equal("Folder", (await provider.GetAsync(marked, CancellationToken.None)).Kind);
        Assert.Equal("Folder link", (await provider.GetAsync(link, CancellationToken.None)).Kind);
    }
}
