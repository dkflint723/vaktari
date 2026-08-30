using System.Runtime.Versioning;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The duplicate filter at its REAL seam: the menu Windows actually builds for
/// a file on this machine, through the production entry point. The unit tests
/// beside this pin the filter's decisions against recorded verbs; this one pins
/// that the decisions happen at all — a marshalling slip in GetCommandString,
/// or an offset off by one, passes every unit test and still ships the
/// duplicates.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellMenuFilterTests
{
    /// <summary>
    /// The rows Vaktari answers natively must not come back from the shell.
    /// Labels, not verbs, on purpose: the user sees labels, and this asserts
    /// what the user sees. English labels are safe here for the same reason
    /// they are safe in the app — Vaktari's own menu is English-only today.
    /// </summary>
    [Fact]
    public void The_hosted_menu_for_a_real_file_lists_no_native_twins()
    {
        var file = Path.Combine(Path.GetTempPath(), "vaktari-menu-filter-check.txt");
        File.WriteAllText(file, "probe");

        try
        {
            using var menu = ShellContextMenu.For([file]);

            // The shell always has SOMETHING for a .txt — an empty answer means
            // the plumbing failed, and an empty menu trivially "contains no
            // duplicates".
            Assert.NotNull(menu);
            Assert.NotEmpty(menu.Entries);

            var labels = menu.Entries
                .Where(entry => !entry.IsSeparator)
                .Select(entry => entry.Label)
                .ToList();

            foreach (var twin in new[]
            {
                "Open", "Open with", "Cut", "Copy", "Paste", "Delete",
                "Properties", "Copy as path", "Share", "Give access to",
                "Rename",
            })
                Assert.DoesNotContain(twin, labels);

            // And the filter is a scalpel, not a hatchet: "Create shortcut"
            // (verb "link") has no native twin and every Windows offers it, so
            // its absence would mean the filter took the wrong rows with it.
            Assert.Contains("Create shortcut", labels);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
