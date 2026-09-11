using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The repository's own guard rails: the files that make dependencies
/// visible and keep contributions in one shape.
///
/// **Nothing audited the dependencies and nothing stated the style.** No
/// Dependabot, so a package with a published advisory stayed until somebody
/// happened to read about it; no vulnerable-package check in CI, so a
/// transitive CVE shipped in silence; no bill of materials for anybody
/// downstream to check against; and no .editorconfig, so the consistency of
/// the tree was one author's habit. These pin that each exists and says the
/// one thing it is for — a source-reading test of the kind the notices file
/// already has, because a deleted config file fails nothing else.
/// </summary>
public sealed class RepoHygieneTests
{
    private static string Read(params string[] parts) => RepoSource.Read(parts);

    [Fact]
    public void Dependabot_watches_the_packages_and_the_actions()
    {
        var config = Read(".github", "dependabot.yml");

        Assert.Contains("package-ecosystem: nuget", config);
        Assert.Contains("package-ecosystem: github-actions", config);
    }

    /// <summary>
    /// The audit is a gate, not a report: a new advisory against anything the
    /// binary carries — transitively, which is where they mostly are — turns
    /// the build red on the job that gates a merge.
    /// </summary>
    [Fact]
    public void Continuous_integration_refuses_a_known_vulnerable_package()
    {
        var workflow = Read(".github", "workflows", "build.yml");

        Assert.Contains("--vulnerable --include-transitive", workflow);
        Assert.Contains("has the following vulnerable packages", workflow);
    }

    /// <summary>
    /// The Ui suite runs on the Linux job too. **It ran on Windows alone until
    /// September 2026**, and two of its source-reading tests had broken on CI
    /// for exactly that reason; the first Linux run found twelve more tests
    /// asserting Windows facts. Nothing else notices this line going.
    /// </summary>
    [Fact]
    public void Continuous_integration_runs_the_Ui_suite_on_Linux()
    {
        var workflow = Read(".github", "workflows", "build.yml");

        var linuxJob = workflow[..workflow.IndexOf("  windows-x64:", StringComparison.Ordinal)];

        Assert.Contains("dotnet test tests/Vaktari.Ui.Tests --configuration Debug", linuxJob);
    }

    /// <summary>
    /// The large-folder fixture runs somewhere: nightly, on both platforms,
    /// with the variable that turns it on. Every ordinary run skips it, so
    /// nothing else would notice the workflow going.
    /// </summary>
    [Fact]
    public void A_nightly_run_measures_the_large_folder_budget_on_both_platforms()
    {
        var nightly = Read(".github", "workflows", "nightly.yml");

        Assert.Contains("VAKTARI_LARGE_FIXTURE: '200000'", nightly);
        Assert.Contains("os: [ubuntu-latest, windows-latest]", nightly);
        Assert.Contains("FullyQualifiedName~LargeFolderTests", nightly);
    }

    /// <summary>A bill of materials goes up with every release, in the
    /// CycloneDX form the scanners read.</summary>
    [Fact]
    public void A_bill_of_materials_is_built_and_released()
    {
        var workflow = Read(".github", "workflows", "build.yml");

        Assert.Contains("CycloneDX", workflow);
        Assert.Contains("vaktari-sbom", workflow);
        Assert.Contains("assets/vaktari-sbom/*", workflow);
    }

    /// <summary>
    /// Formatting and naming only. **The build treats warnings as errors**, so
    /// a style rule raised to `warning` here would fail every build over a
    /// brace; every rule this file states is a suggestion.
    /// </summary>
    [Fact]
    public void The_editorconfig_states_the_house_style_without_touching_the_build()
    {
        var config = Read(".editorconfig");

        Assert.Contains("root = true", config);
        Assert.Contains("end_of_line = lf", config);
        Assert.Contains("indent_size = 4", config);
        Assert.Contains("csharp_style_namespace_declarations = file_scoped", config);

        Assert.DoesNotContain(":warning", config);
        Assert.DoesNotContain(":error", config);
        Assert.DoesNotContain("dotnet_diagnostic", config);
    }
}
