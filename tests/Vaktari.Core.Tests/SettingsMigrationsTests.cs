using System.Text.Json.Nodes;
using Vaktari.Core.Settings;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Walking a settings document up the versions.
///
/// **There was no walk.** A file whose version was not the current one was
/// answered with defaults, which is a reset of every choice the day the
/// number changes. These pin the road: steps run in order, each version is
/// stamped as it is reached, a version with no step out of it is refused
/// rather than guessed at, and so is one newer than the target.
/// </summary>
public sealed class SettingsMigrationsTests
{
    private static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly IReadOnlyDictionary<int, SettingsMigrations.Step> Road =
        new Dictionary<int, SettingsMigrations.Step>
        {
            [1] = d => { d["b"] = d["a"]!.GetValue<int>(); d.Remove("a"); },
            [2] = d => d["c"] = true,
        };

    [Fact]
    public void Steps_run_in_order_and_each_version_is_stamped()
    {
        var doc = SettingsMigrations.Upgrade(Doc("""{"version":1,"a":7}"""), Road, target: 3);

        Assert.NotNull(doc);
        Assert.Equal(3, SettingsMigrations.VersionOf(doc));
        Assert.Equal(7, doc["b"]!.GetValue<int>());
        Assert.True(doc["c"]!.GetValue<bool>());
        Assert.Null(doc["a"]);
    }

    /// <summary>Half the road is still the road: a document already partway
    /// along walks the rest of it.</summary>
    [Fact]
    public void A_document_partway_along_walks_the_rest()
    {
        var doc = SettingsMigrations.Upgrade(Doc("""{"version":2,"b":1}"""), Road, target: 3);

        Assert.NotNull(doc);
        Assert.Equal(3, SettingsMigrations.VersionOf(doc));
        Assert.Equal(1, doc["b"]!.GetValue<int>());
    }

    [Fact]
    public void A_version_with_no_step_out_of_it_is_refused()
        => Assert.Null(SettingsMigrations.Upgrade(Doc("""{"version":1}"""), new Dictionary<int, SettingsMigrations.Step>(), target: 2));

    /// <summary>Newer than the target is not "already there": it is a file this
    /// build cannot read, and the caller decides what that means.</summary>
    [Fact]
    public void A_version_newer_than_the_target_is_refused()
        => Assert.Null(SettingsMigrations.Upgrade(Doc("""{"version":5}"""), Road, target: 3));

    [Fact]
    public void A_document_already_at_the_target_is_left_alone()
    {
        var doc = SettingsMigrations.Upgrade(Doc("""{"version":3,"a":7}"""), Road, target: 3);

        Assert.NotNull(doc);
        Assert.Equal(7, doc["a"]!.GetValue<int>());
    }

    // ---- the real road -------------------------------------------------------

    /// <summary>A file naming no version is the first format, which is the
    /// only one there has been.</summary>
    [Fact]
    public void A_file_naming_no_version_is_read_as_the_first_format()
    {
        var doc = SettingsMigrations.Upgrade(Doc("""{"general":{"naturalSorting":false}}"""), target: 1);

        Assert.NotNull(doc);
        Assert.Equal(1, SettingsMigrations.VersionOf(doc));
        Assert.False(doc["general"]!["naturalSorting"]!.GetValue<bool>());
    }

    /// <summary>
    /// **The test that fails on the morning the version is bumped without a
    /// step.** Every version before the current one has to have a way up, or
    /// the users on it are reset — which is the whole failure this replaces.
    /// </summary>
    [Fact]
    public void Every_version_before_the_current_one_has_a_step_out_of_it()
    {
        for (var version = 0; version < SettingsState.CurrentVersion; version++)
            Assert.True(SettingsMigrations.Steps.ContainsKey(version),
                $"no migration from settings version {version} to {version + 1}");
    }
}
