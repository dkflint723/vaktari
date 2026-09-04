using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The interface doc on IsReachableAsync names its one caller, and that half of
/// a doc comment has already drifted once: it said lazy session restore used it
/// to mark a tab dead while nothing in the tree called it at all.
/// </summary>
public sealed class ReachabilityCallerTests
{
    private static IEnumerable<string> Mentioning(string text)
        => Directory
            .EnumerateFiles(Path.Combine(RepoSource.Root, "src"), "*.cs",
                            SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Only_the_declaration_the_two_providers_and_the_named_caller_mention_the_probe()
    {
        var files = Mentioning("IsReachableAsync")
                    .Select(f => Path.GetFileName(f) ?? f)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

        Assert.Equal(
            [
                "IFileSystemProvider.cs",
                "LinuxFileSystemProvider.cs",
                "PaneViewModel.cs",
                "WindowsFileSystemProvider.cs",
            ],
            files);
    }

    [Fact]
    public void The_method_the_doc_names_is_the_one_that_calls_it()
    {
        var doc = RepoSource.Read("src", "Vaktari.Core", "FileSystem", "IFileSystemProvider.cs");

        Assert.Contains("PaneViewModel.LoadRestoredAsync", doc, StringComparison.Ordinal);

        var pane = RepoSource.Read("src", "Vaktari.Ui", "ViewModels", "PaneViewModel.cs");

        var at = pane.IndexOf("private async Task LoadRestoredAsync(", StringComparison.Ordinal);

        Assert.True(at >= 0, "the method the interface doc names is not declared any more");

        var end = pane.IndexOf("\n    }\n", at, StringComparison.Ordinal);

        Assert.True(end > at, "could not find the end of LoadRestoredAsync");

        Assert.Contains("IsReachableAsync", pane[at..end], StringComparison.Ordinal);
    }
}
