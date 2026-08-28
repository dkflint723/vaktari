using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The .lnk writer, closed as a loop: a shortcut is only proven by the shell
/// reading back the target it stores — a file with the right name and size
/// proves nothing about its vtable arithmetic.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShortcutsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-lnk-" + Guid.NewGuid().ToString("N"));

    public WindowsShortcutsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_shortcut_to_a_file_points_back_at_it()
    {
        var target = Path.Combine(_root, "report.pdf");
        File.WriteAllText(target, "x");

        var landing = new WindowsShortcuts().CreateShortcut(target, _root);

        Assert.EndsWith(@"report.pdf - Shortcut.lnk", landing);
        Assert.True(File.Exists(landing));

        // The loop closed: the shell reads back what it stored.
        Assert.Equal(target, WindowsShortcuts.ReadTarget(landing), ignoreCase: true);
    }

    [Fact]
    public void A_shortcut_to_a_folder_works_too()
    {
        var target = Path.Combine(_root, "photos");
        Directory.CreateDirectory(target);

        var landing = new WindowsShortcuts().CreateShortcut(target, _root);

        Assert.EndsWith(@"photos - Shortcut.lnk", landing);
        Assert.Equal(target, WindowsShortcuts.ReadTarget(landing), ignoreCase: true);
    }

    /// <summary>Explorer's own numbering when the name is taken.</summary>
    [Fact]
    public void A_second_shortcut_steps_aside()
    {
        var target = Path.Combine(_root, "notes.txt");
        File.WriteAllText(target, "x");

        var maker = new WindowsShortcuts();

        var first = maker.CreateShortcut(target, _root);
        var second = maker.CreateShortcut(target, _root);

        Assert.EndsWith("notes.txt - Shortcut.lnk", first);
        Assert.EndsWith("notes.txt - Shortcut (2).lnk", second);
        Assert.Equal(target, WindowsShortcuts.ReadTarget(second), ignoreCase: true);
    }
}
