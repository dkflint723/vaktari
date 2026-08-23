using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Copying and moving symbolic links.
///
/// **The link was followed instead of reproduced, and on a move that emptied
/// the real folder.** BuildPlan tested Directory.Exists — which a link to a
/// directory answers yes to — and then walked it with
/// SearchOption.AllDirectories, which follows links. So copying a folder that
/// held a link to a photo library duplicated the library, and MOVING it deleted
/// every file out of the library after copying them, because the walk had
/// enumerated the target's contents as though they lived inside the thing being
/// moved.
///
/// WindowsFileOperations plans links as leaves and reproduces them; the Linux
/// copy did neither. These run on Linux only: creating a symlink on Windows
/// needs Developer Mode, and the behaviour under test is what Linux does.
/// </summary>
public sealed class SymlinkCopyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-symlink-" + Guid.NewGuid().ToString("N"));

    private readonly string _from;
    private readonly string _into;
    private readonly string _library;

    public SymlinkCopyTests()
    {
        _from = Path.Combine(_root, "from");
        _into = Path.Combine(_root, "into");
        _library = Path.Combine(_root, "library");

        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_into);
        Directory.CreateDirectory(_library);

        File.WriteAllText(Path.Combine(_library, "photo.jpg"), "photo");
        File.WriteAllText(Path.Combine(_library, "another.jpg"), "another");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task Run(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);
    }

    private static ValueTask<ConflictResolution> Overwrite(FileConflict _)
        => ValueTask.FromResult(ConflictResolution.Overwrite);

    /// <summary>
    /// **The one that destroys data.** Moving a folder that contains a link to
    /// somewhere else must not touch what the link points at.
    /// </summary>
    [Fact]
    public async Task Moving_a_folder_holding_a_link_leaves_the_real_folder_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var holder = Path.Combine(_from, "holder");
        Directory.CreateDirectory(holder);
        File.WriteAllText(Path.Combine(holder, "own.txt"), "own");
        Directory.CreateSymbolicLink(Path.Combine(holder, "photos"), _library);

        await Run(new LinuxFileOperations().Move([holder], _into, Overwrite));

        // The library still has everything it started with.
        Assert.Equal(
            ["another.jpg", "photo.jpg"],
            Directory.GetFiles(_library).Select(Path.GetFileName).Order());

        // And the link came across as a link, not as a copy of the contents.
        var landed = Path.Combine(_into, "holder", "photos");
        Assert.True(Directory.Exists(landed));
        Assert.Equal(_library, new DirectoryInfo(landed).LinkTarget);
    }

    /// <summary>Copying reproduces the link rather than the tree behind it.</summary>
    [Fact]
    public async Task Copying_a_link_to_a_folder_reproduces_the_link()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        await Run(new LinuxFileOperations().Copy([link], _into, Overwrite));

        var landed = Path.Combine(_into, "photos");

        Assert.Equal(_library, new DirectoryInfo(landed).LinkTarget);

        // One entry arrived — the link itself. A followed link would have
        // produced a real folder holding copies of the library's files.
        Assert.Single(new DirectoryInfo(_into).EnumerateFileSystemInfos());
        Assert.True(
            (File.GetAttributes(landed) & FileAttributes.ReparsePoint) != 0,
            "what landed should be a link, not a folder of copies");
    }

    /// <summary>A link to a FILE is reproduced too, and keeps its target text
    /// exactly — a relative link is usually relative on purpose.</summary>
    [Fact]
    public async Task A_relative_link_keeps_its_target_verbatim()
    {
        if (!OperatingSystem.IsLinux()) return;

        File.WriteAllText(Path.Combine(_from, "real.txt"), "real");
        File.CreateSymbolicLink(Path.Combine(_from, "alias.txt"), "real.txt");

        await Run(new LinuxFileOperations().Copy(
            [Path.Combine(_from, "alias.txt")], _into, Overwrite));

        Assert.Equal("real.txt", new FileInfo(Path.Combine(_into, "alias.txt")).LinkTarget);
    }

    /// <summary>
    /// Moving the link itself removes the link and nothing else — the file it
    /// pointed at stays exactly where it was.
    /// </summary>
    [Fact]
    public async Task Moving_a_link_moves_only_the_link()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        await Run(new LinuxFileOperations().Move([link], _into, Overwrite));

        Assert.False(Directory.Exists(link) || File.Exists(link), "the link should have gone");
        Assert.True(Directory.Exists(_library), "what it pointed at should not have");
        Assert.Equal(2, Directory.GetFiles(_library).Length);
    }

    /// <summary>An ordinary folder with no links behaves exactly as before.</summary>
    [Fact]
    public async Task An_ordinary_folder_still_copies_whole()
    {
        if (!OperatingSystem.IsLinux()) return;

        var plain = Path.Combine(_from, "plain");
        Directory.CreateDirectory(Path.Combine(plain, "inner"));
        File.WriteAllText(Path.Combine(plain, "a.txt"), "a");
        File.WriteAllText(Path.Combine(plain, "inner", "b.txt"), "b");

        await Run(new LinuxFileOperations().Copy([plain], _into, Overwrite));

        Assert.Equal("a", File.ReadAllText(Path.Combine(_into, "plain", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(_into, "plain", "inner", "b.txt")));
    }
}
