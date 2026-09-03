using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The reader the source-scanning tests share.
///
/// **It looked for "Vaktari.slnx" and the file is vaktari.slnx.** On Windows
/// that matched anyway; on Linux it did not, so the walk ran off the top of the
/// filesystem and the null went into Path.Combine — an ArgumentNullException on
/// the agent that gates the merge, from tests that passed on the machine they
/// were written on.
///
/// That is the second time a source-reading test has been weaker on CI than
/// here: the first was reading CRLF as LF. Both had the same cause — the walk
/// and the read were copied into every file that needed them, so each copy was
/// a fresh chance to get it wrong. One reader now, and it fails saying what it
/// could not find rather than dereferencing null.
/// </summary>
public sealed class RepoSourceTests
{
    [Fact]
    public void The_repository_root_is_found()
        => Assert.False(string.IsNullOrEmpty(RepoSource.Read("vaktari.slnx")));

    /// <summary>
    /// By extension, not by name: nothing here may depend on how a filesystem
    /// feels about case.
    /// </summary>
    [Fact]
    public void The_root_is_found_without_relying_on_the_case_of_its_name()
    {
        var reader = RepoSource.Read("tests", "Vaktari.Core.Tests", "RepoSource.cs");

        Assert.Contains("EnumerateFiles(here, \"*.slnx\")", reader);

        // The call, not the prose: the comment above it quotes the old spelling
        // while explaining what went wrong, and should go on doing so.
        Assert.DoesNotContain("File.Exists(Path.Combine(here,", reader);
    }

    [Fact]
    public void Source_arrives_with_one_kind_of_line_ending()
    {
        var source = RepoSource.Read("src", "Vaktari.Core", "NaturalOrder.cs");

        Assert.DoesNotContain('\r', source);
        Assert.Contains('\n', source);
    }
}
