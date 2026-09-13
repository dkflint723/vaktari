using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the properties dialog says a folder holds, when a junction is in it.
///
/// **A junction was counted as a folder.** The walk refused to descend into it
/// — "a junction can point at an ancestor, and following one turns a
/// measurement into a loop" — and then counted it among the folders anyway,
/// because the test that decided was the Directory attribute, which a junction
/// carries. Measured on 2026-09-12: a folder holding a real subfolder and a
/// junction reported two folders. Its bytes were already right, since nothing
/// behind it was read.
///
/// The rule is <see cref="SpaceUsage"/>'s, which has tests behind it and which
/// the Linux measure follows too: a link is one entry, of no size, and never a
/// folder. Only the folder half can be pinned here — a symbolic link to a FILE
/// needs Developer Mode or elevation, so the length half is the Linux twin's.
/// </summary>
[SupportedOSPlatform("windows")]
public class MeasureLinkTests
{
    private static ValueTask<SizeProgress> Measure(string path)
        => new WindowsPropertiesProvider().MeasureAsync(path, new Silent(), CancellationToken.None);

    [WindowsFact]
    public async Task A_junction_is_an_entry_not_a_folder()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");

        var measured = tree.Dir("measured");
        tree.Dir("measured", "real");
        tree.Junction("measured/shortcut", outside);

        var total = await Measure(measured);

        Assert.Equal(1, total.Folders);
        Assert.Equal(1, total.Files);

        // Nothing behind the junction is read, as before.
        Assert.Equal(0, total.Bytes);
    }

    /// <summary>
    /// GUARD, not a test of the change, and it says so: a folder with no
    /// junction in it is measured exactly as it always was. Measured passing
    /// before.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_without_junctions_is_measured_as_before()
    {
        using var tree = new TempTree();

        var measured = tree.Dir("measured");
        tree.Write("measured/a.txt", new string('a', 40));
        tree.Write("measured/inner/b.txt", "bb");

        var total = await Measure(measured);

        Assert.Equal(42, total.Bytes);
        Assert.Equal(2, total.Files);
        Assert.Equal(1, total.Folders);
    }

    private sealed class Silent : IProgress<SizeProgress>
    {
        public void Report(SizeProgress value) { }
    }
}
