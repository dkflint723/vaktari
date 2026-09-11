using System.Text.Json.Nodes;

namespace Vaktari.Core.Settings;

/// <summary>
/// Brings a settings document written by an earlier Vaktari up to the format
/// this one reads.
///
/// **A file from any other version was thrown away.** Load kept a file only
/// when its version number was exactly the current one and answered anything
/// else with defaults — so the first release to change the number would have
/// reset every choice on six pages for everyone who upgraded, silently, on
/// the morning they installed it. A reset is the failure a version number
/// exists to prevent, not the one it exists to cause.
///
/// One step per version, on the raw document rather than on the record: the
/// old shape need not be kept alive as a type, and a step can rename, split
/// or drop a key with the whole file in view. A document is walked up one
/// step at a time — 0 to 1, 1 to 2 — so a file from three versions back
/// arrives by the same road as one from last week, and the road has to
/// exist: a version this build has never heard of is refused, not guessed at.
/// </summary>
public static class SettingsMigrations
{
    /// <summary>One step: takes a document at version <c>n</c> and leaves it
    /// at <c>n + 1</c>. The version key itself is stamped by the walker.</summary>
    public delegate void Step(JsonObject document);

    /// <summary>The road, keyed by the version a step starts from. Adding a
    /// version to <see cref="SettingsState.CurrentVersion"/> means adding
    /// the step that leads to it here; a test says so if it is missing.</summary>
    internal static IReadOnlyDictionary<int, Step> Steps { get; } = new Dictionary<int, Step>
    {
        // 0 → 1: a file that names no version. Nothing this application ever
        // wrote lacks one — the field has been there since the first release
        // — so this is a hand-written or hand-edited file, and its keys are
        // the first format's keys, because there has been no other.
        [0] = _ => { },
    };

    /// <summary>
    /// The document brought up to <paramref name="target"/>, or null when
    /// there is no road there: a version newer than this build, or a number
    /// nothing ever wrote.
    /// </summary>
    public static JsonObject? Upgrade(JsonObject document, int target)
        => Upgrade(document, Steps, target);

    internal static JsonObject? Upgrade(JsonObject document, IReadOnlyDictionary<int, Step> steps, int target)
    {
        var version = VersionOf(document);

        while (version < target)
        {
            if (!steps.TryGetValue(version, out var step)) return null;

            step(document);
            version++;
            document["version"] = version;
        }

        return version == target ? document : null;
    }

    /// <summary>The number the file names, and 0 when it names none.</summary>
    public static int VersionOf(JsonObject document)
        => document["version"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;
}
