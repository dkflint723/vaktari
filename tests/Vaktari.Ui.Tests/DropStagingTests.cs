using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Rescuing a drop whose source is about to delete itself.
///
/// **From a failure that reproduced every time.** Dragging a folder out of a
/// 7-Zip archive hands over a path into a temporary folder 7-Zip extracted for
/// the drag. Logged at the instant of the drop, that folder held 541 files and
/// 8,985,809 bytes; the copy that followed failed with "Could not find a part
/// of the path", the folder having been deleted the moment the drop returned.
/// The copy could never have won that race — it starts after the race is over.
/// </summary>
public sealed class DropStagingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-staging-" + Guid.NewGuid().ToString("N"));

    private readonly string _temporary;
    private readonly string _elsewhere;
    private readonly string _staging;

    public DropStagingTests()
    {
        // A stand-in for the temporary directory, so the test never depends on
        // what is really in the machine's own.
        _temporary = Path.Combine(_root, "temp");
        _elsewhere = Path.Combine(_root, "documents");
        _staging = Path.Combine(_root, "staging");

        Directory.CreateDirectory(_temporary);
        Directory.CreateDirectory(_elsewhere);
    }

    public void Dispose()
    {
        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Tree(string under, string name)
    {
        var folder = Path.Combine(under, name);

        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllText(Path.Combine(folder, "backend.py"), "print('hi')");
        File.WriteAllText(Path.Combine(folder, "inner", "manifest.json"), "{}");

        return folder;
    }

    /// <summary>
    /// The one that matters: the source is deleted immediately after the drop,
    /// exactly as 7-Zip does, and what was rescued still has to be there.
    /// </summary>
    [Fact]
    public void A_folder_deleted_the_moment_the_drop_returns_is_still_there()
    {
        var dropped = Tree(_temporary, "battlenet_ba170431");

        var staged = DropStaging.Rescue([dropped], _temporary, _staging);

        // The source goes away, as it does in the failure this comes from. A
        // delete-if-present, because a same-volume rescue is a MOVE and has
        // already taken it — which is the point: 7-Zip's cleanup finds nothing
        // and nobody spent time copying.
        if (Directory.Exists(dropped)) Directory.Delete(dropped, recursive: true);

        Assert.True(staged.Rescued);
        var landing = Assert.Single(staged.Paths);

        Assert.True(Directory.Exists(landing));
        Assert.Equal("print('hi')", File.ReadAllText(Path.Combine(landing, "backend.py")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(landing, "inner", "manifest.json")));
    }

    /// <summary>
    /// **On the same volume the rescue is a rename, not a copy.** The rescue
    /// runs inside the drop handler on the UI thread — it must, or the source
    /// deletes the files first — and a copy there froze the window for as long
    /// as the archive was large, with nothing capping it. Both ends live under
    /// the temporary directory, so a move costs nothing at any size; this is
    /// the assertion that keeps it one. The per-item copy fallback below the
    /// move is the SAME CopyTree the rescue always used, exercised by every
    /// test in this file before the move existed.
    /// </summary>
    [Fact]
    public void A_same_volume_rescue_moves_rather_than_copies()
    {
        var dropped = Tree(_temporary, "big-archive");

        var staged = DropStaging.Rescue([dropped], _temporary, _staging);

        Assert.True(staged.Rescued);
        Assert.Equal(1, staged.Moved);
        Assert.Equal(0, staged.CopiedBytes);
        Assert.False(Directory.Exists(dropped), "a move takes the source with it");
        Assert.True(File.Exists(Path.Combine(staged.Paths[0], "backend.py")));
    }

    /// <summary>
    /// **An ordinary drop is not duplicated.** Copying every drag twice to fix
    /// the archive case would be a tax on the common one, and a move that
    /// staged first would leave the original in place.
    /// </summary>
    [Fact]
    public void A_drop_from_an_ordinary_folder_is_left_alone()
    {
        var dropped = Tree(_elsewhere, "notes");

        var staged = DropStaging.Rescue([dropped], _temporary, _staging);

        Assert.False(staged.Rescued);
        Assert.Equal([dropped], staged.Paths);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void A_single_volatile_file_is_rescued_too()
    {
        var file = Path.Combine(_temporary, "readme.txt");
        File.WriteAllText(file, "contents");

        var staged = DropStaging.Rescue([file], _temporary, _staging);
        if (File.Exists(file)) File.Delete(file);

        Assert.True(staged.Rescued);
        Assert.Equal("contents", File.ReadAllText(staged.Paths[0]));
    }

    /// <summary>A drop mixing both keeps each on its own terms.</summary>
    [Fact]
    public void A_mixed_drop_rescues_only_the_volatile_half()
    {
        var volatileTree = Tree(_temporary, "from-archive");
        var ordinary = Tree(_elsewhere, "from-disk");

        var staged = DropStaging.Rescue([volatileTree, ordinary], _temporary, _staging);

        Assert.True(staged.Rescued);
        Assert.Equal(2, staged.Paths.Count);
        Assert.StartsWith(_staging, staged.Paths[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ordinary, staged.Paths[1]);
    }

    /// <summary>
    /// **A prefix match is not a path match.** "temp-other" begins with "temp"
    /// and is a different directory; sweeping it in would copy files nobody
    /// asked to have copied.
    /// </summary>
    [Fact]
    public void A_folder_whose_name_merely_starts_the_same_is_not_volatile()
    {
        var sibling = Path.Combine(_root, "temp-other");
        Directory.CreateDirectory(sibling);

        Assert.False(DropStaging.IsVolatile(Path.Combine(sibling, "file.txt"), _temporary));
        Assert.True(DropStaging.IsVolatile(Path.Combine(_temporary, "file.txt"), _temporary));
    }

    /// <summary>
    /// A path that is already gone cannot be rescued, and must come back
    /// unchanged so the drop fails with its own message rather than silently
    /// losing the entry.
    /// </summary>
    [Fact]
    public void A_path_that_is_already_gone_comes_back_unchanged()
    {
        var missing = Path.Combine(_temporary, "never-existed");

        var staged = DropStaging.Rescue([missing], _temporary, _staging);

        Assert.False(staged.Rescued);
        Assert.Equal([missing], staged.Paths);
    }
}
