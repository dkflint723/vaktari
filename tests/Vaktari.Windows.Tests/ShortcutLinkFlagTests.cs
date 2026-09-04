using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The mark a Windows shortcut carries, on all three paths a row can arrive by.
///
/// **A .lnk drew no arrow anywhere.** Only FileAttributes.ReparsePoint set the
/// Symlink flag, and no attribute marks a shortcut — so a symbolic link and a
/// junction got the listing's link emblem and the one indirection a Windows
/// desktop is actually full of got nothing. Desktop and the Start Menu are
/// folders of nothing but shortcuts, drawn exactly like the things they point
/// at. The word beside them already said "Shortcut" and the properties window
/// said "Shortcut"; only the picture disagreed.
///
/// Three paths because there were three copies of the same five lines — the
/// listing's enumeration, the watcher's single-entry lookup and the search
/// walk — agreeing only by hand. They are one function now, and these tests
/// hold all three to it, so a rule taught to one of them cannot go untaught in
/// the others.
///
/// Real files rather than fakes, for the reason <see cref="TempTree"/> gives:
/// the whole question is what the OS puts in a directory entry.
/// </summary>
[SupportedOSPlatform("windows")]
public class ShortcutLinkFlagTests
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

    private static async Task<List<FileEntry>> Search(string scope, string text)
    {
        var query = new SearchQuery { Text = text, ScopePath = scope, MaxResults = 100 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<FileEntry>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry);

        return found;
    }

    [WindowsFact]
    public async Task A_shortcut_is_listed_with_the_flag_the_emblem_draws_from()
    {
        using var tree = new TempTree();

        var desktop = tree.Dir("Desktop");
        var shortcut = tree.Write("Desktop/Chrome.lnk");

        Assert.True(
            (await Enumerated(desktop, shortcut)).IsSymlink,
            "the shortcut was listed, but not as a link");
    }

    /// <summary>And the rule did not widen to every file on the way in.</summary>
    [WindowsFact]
    public async Task An_ordinary_file_is_still_not_a_link()
    {
        using var tree = new TempTree();

        var desktop = tree.Dir("Desktop");

        Assert.False(
            (await Enumerated(desktop, tree.Write("Desktop/notes.txt"))).IsSymlink,
            "an ordinary file was marked as a link");
    }

    /// <summary>
    /// An extension is a fact about a file. A folder somebody called
    /// "Chrome.lnk" is a folder, and FileKind refuses the question for a
    /// directory too.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_named_like_a_shortcut_is_a_folder()
    {
        using var tree = new TempTree();

        var desktop = tree.Dir("Desktop");
        var folder = tree.Dir("Desktop/Chrome.lnk");

        var entry = await Enumerated(desktop, folder);

        Assert.True(entry.IsDirectory);
        Assert.False(entry.IsSymlink, "a folder was marked as a shortcut because of its name");
    }

    /// <summary>
    /// The listing and the watcher describe one file identically. They are two
    /// values for the same row otherwise, and the second one arrives half a
    /// second after the first without redrawing anything that says so.
    /// </summary>
    [WindowsFact]
    public async Task A_shortcut_seen_through_the_watcher_is_the_same_row()
    {
        using var tree = new TempTree();

        var desktop = tree.Dir("Desktop");
        var shortcut = tree.Write("Desktop/Chrome.lnk");

        var enumerated = await Enumerated(desktop, shortcut);

        // Load-bearing: without it this passes while BOTH paths are wrong.
        Assert.True(enumerated.IsSymlink, "the listing lost the shortcut's link flag");

        Assert.Equal(enumerated.Flags, (await Watched(shortcut)).Flags);
    }

    /// <summary>
    /// And so does the search backend, whose results ARE a listing: they fill
    /// the same Entries the three row templates draw, so a backend that
    /// described a shortcut differently would draw the arrow in the folder and
    /// not in the results.
    /// </summary>
    [WindowsFact]
    public async Task A_shortcut_found_by_a_search_is_the_row_the_listing_shows()
    {
        using var tree = new TempTree();

        var desktop = tree.Dir("Desktop");
        var shortcut = tree.Write("Desktop/Chrome.lnk");

        var hit = Assert.Single(await Search(desktop, "Chrome"));

        Assert.Equal(shortcut, hit.FullPath);
        Assert.True(hit.IsSymlink, "the search hit was not marked as a link");
        Assert.Equal((await Enumerated(desktop, shortcut)).Flags, hit.Flags);
    }

    /// <summary>
    /// The lines that were merely MOVED, held to both paths — so folding three
    /// copies into one cannot quietly drop one of them.
    /// </summary>
    [WindowsFact]
    public async Task The_attributes_a_row_carries_survive_the_watcher_too()
    {
        using var tree = new TempTree();

        var desktop = tree.Dir("Desktop");

        var hidden = tree.Write("Desktop/hidden.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var system = tree.Write("Desktop/system.txt");
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        var locked = tree.WriteReadOnly("Desktop/locked.txt");

        foreach (var (path, flag) in new[]
                 {
                     (hidden, EntryFlags.Hidden),
                     (system, EntryFlags.System),
                     (locked, EntryFlags.ReadOnly),
                 })
        {
            var enumerated = await Enumerated(desktop, path);

            Assert.True((enumerated.Flags & flag) != 0, $"{Path.GetFileName(path)} lost {flag}");
            Assert.Equal(enumerated.Flags, (await Watched(path)).Flags);
        }
    }
}
