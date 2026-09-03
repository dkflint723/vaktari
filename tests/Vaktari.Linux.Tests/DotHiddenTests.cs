using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A folder that asks for some of its own entries to be hidden.
///
/// **A freedesktop convention Vaktari did not read, and both references do.**
/// A `.hidden` file lists names, one per line, that a folder wants left out —
/// it is how a project marks generated output and how a distribution keeps its
/// scaffolding out of a home directory, without renaming anything, because a
/// build tool or a script has to find those files under their real names.
/// Nautilus and Dolphin both honour it, so a folder tidy in either was a mess
/// here.
/// </summary>
public sealed class DotHiddenTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-dothidden-" + Guid.NewGuid().ToString("N")[..8]);

    public DotHiddenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private void Hidden(string contents)
        => File.WriteAllText(Path.Combine(_root, ".hidden"), contents);

    [Fact]
    public void The_names_a_folder_asks_to_hide_are_read()
    {
        Hidden("build\nnode_modules\n");

        Assert.Equal(
            ["build", "node_modules"],
            XdgHiddenFor(_root).Order());
    }

    /// <summary>Blank lines are not names.</summary>
    [Fact]
    public void Blank_lines_name_nothing()
    {
        Hidden("build\n\n   \nout\n");

        Assert.Equal(["build", "out"], XdgHiddenFor(_root).Order());
    }

    /// <summary>
    /// **A name, never a path.** The convention names entries in THIS
    /// directory, so honouring a "../secret" or "sub/thing" would let one
    /// folder's file hide a row in a folder that never asked — including one
    /// above it.
    /// </summary>
    [Theory]
    [InlineData("../secret")]
    [InlineData("sub/thing")]
    [InlineData("/etc/passwd")]
    public void A_path_is_not_a_name(string line)
    {
        Hidden(line + "\n");

        Assert.Empty(XdgHiddenFor(_root));
    }

    /// <summary>
    /// Ordinal, because names on this filesystem are bytes — a .hidden naming
    /// "Build" does not hide "build", and hiding the wrong file is worse than
    /// hiding none.
    /// </summary>
    [Fact]
    public void The_names_are_matched_exactly()
    {
        Hidden("Build\n");

        Assert.Contains("Build", XdgHiddenFor(_root));
        Assert.DoesNotContain("build", XdgHiddenFor(_root));
    }

    /// <summary>A folder with no such file asks for nothing.</summary>
    [Fact]
    public void A_folder_that_asks_for_nothing_hides_nothing()
        => Assert.Empty(XdgHiddenFor(_root));

    private static HashSet<string> XdgHiddenFor(string directory)
        => LinuxFileSystemProvider.HiddenNames(directory);

    // ---- and the listing really applies it ----------------------------------

    private async Task<List<FileEntry>> ListAsync(bool includeHidden)
    {
        var all = new List<FileEntry>();
        var provider = new LinuxFileSystemProvider();

        await foreach (var batch in provider.EnumerateAsync(
            _root, new ListingOptions { IncludeHidden = includeHidden }, CancellationToken.None))
        {
            all.AddRange(batch);
        }

        return all;
    }

    [Fact]
    public async Task A_named_file_is_left_out_of_the_listing()
    {
        File.WriteAllText(Path.Combine(_root, "report.txt"), "keep");
        File.WriteAllText(Path.Combine(_root, "build.log"), "hide");

        Hidden("build.log\n");

        Assert.Equal(["report.txt"], (await ListAsync(includeHidden: false)).Select(e => e.Name));
    }

    /// <summary>
    /// **And turning hidden files on brings it back as a HIDDEN row**, not as
    /// an ordinary one. Without the flag it would come back undimmed and
    /// indistinguishable from a file the folder never asked to conceal — and
    /// every rule downstream that reads IsConcealed, the search listing
    /// included, would disagree with the folder about it.
    /// </summary>
    [Fact]
    public async Task And_comes_back_marked_hidden_when_they_are_shown()
    {
        File.WriteAllText(Path.Combine(_root, "build.log"), "hide");

        Hidden("build.log\n");

        var row = Assert.Single(
            await ListAsync(includeHidden: true), e => e.Name == "build.log");

        Assert.True(row.IsHidden);
        Assert.True(row.IsConcealed);
    }

    /// <summary>
    /// The .hidden file itself starts with a dot, so it was already hidden —
    /// this is only to say the two rules do not fight.
    /// </summary>
    [Fact]
    public async Task A_dotfile_is_still_hidden_the_ordinary_way()
    {
        File.WriteAllText(Path.Combine(_root, ".config"), "x");

        Hidden("nothing-here\n");

        Assert.DoesNotContain(
            ".config", (await ListAsync(includeHidden: false)).Select(e => e.Name));
    }

    /// <summary>
    /// **A row that arrives through the WATCHER must carry the same flags.**
    /// FileEntry is a record struct compared by every member, so a listed row
    /// and a watched row that disagree about one bit are unequal — the
    /// selection will not resolve onto the file, which is the fault the flag
    /// block in GetEntryAsync already exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_single_entry_is_marked_the_same_way_the_listing_marks_it()
    {
        var path = Path.Combine(_root, "build.log");

        File.WriteAllText(path, "hide");
        Hidden("build.log\n");

        var watched = await new LinuxFileSystemProvider()
            .GetEntryAsync(path, CancellationToken.None);

        var listed = Assert.Single(
            await ListAsync(includeHidden: true), e => e.Name == "build.log");

        Assert.NotNull(watched);
        Assert.Equal(listed.Flags, watched!.Value.Flags);
    }
}
