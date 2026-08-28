using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A shortcut here is a symbolic link, and the questions are where it points
/// and what it is called when the name is taken.
/// </summary>
public sealed class LinuxShortcutsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-symlink-make-" + Guid.NewGuid().ToString("N"));

    public LinuxShortcutsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_link_to_a_file_points_at_its_absolute_path()
    {
        if (!OperatingSystem.IsLinux()) return;

        var target = Path.Combine(_root, "notes.txt");
        File.WriteAllText(target, "x");
        var into = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        var landing = new LinuxShortcuts().CreateShortcut(target, into);

        Assert.Equal(Path.Combine(into, "notes.txt"), landing);

        // Absolute, so moving the LINK does not quietly re-point it.
        Assert.Equal(target, new FileInfo(landing).LinkTarget);
    }

    [Fact]
    public void A_link_beside_its_target_steps_aside()
    {
        if (!OperatingSystem.IsLinux()) return;

        var target = Path.Combine(_root, "notes.txt");
        File.WriteAllText(target, "x");

        // Same folder: the target itself holds the name.
        var landing = new LinuxShortcuts().CreateShortcut(target, _root);

        Assert.Equal(Path.Combine(_root, "notes (1).txt"), landing);
        Assert.Equal(target, new FileInfo(landing).LinkTarget);
    }

    [Fact]
    public void A_link_to_a_folder_is_a_folder_link()
    {
        if (!OperatingSystem.IsLinux()) return;

        var target = Directory.CreateDirectory(Path.Combine(_root, "my.photos")).FullName;
        var into = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        var landing = new LinuxShortcuts().CreateShortcut(target, into);

        Assert.Equal(target, new DirectoryInfo(landing).LinkTarget);

        // And when the name is taken, the folder's WHOLE name takes the number.
        var second = new LinuxShortcuts().CreateShortcut(target, into);
        Assert.Equal(Path.Combine(into, "my.photos (1)"), second);
    }
}
