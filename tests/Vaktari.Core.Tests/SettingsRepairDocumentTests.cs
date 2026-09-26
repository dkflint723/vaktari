using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vaktari.Core.Settings;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// A settings.json written before a true-by-default key existed.
///
/// **0.10.0 added two positively named <c>= true</c> settings, and every 0.9.x
/// install upgraded into them switched off.** <see cref="GeneralSettings.RememberRecent"/>
/// and <see cref="ContextMenuSettings.ShowOpenInNewWindow"/> are declared true,
/// but the source-generated context hands an absent key <c>default(T)</c>, and
/// no 0.9.x file has either key — so the recent lists stopped recording and
/// "Open in new window" left the menu, on upgrade, with a dialog showing both
/// unticked as though somebody had chosen that.
///
/// The fixture is not written from memory: it is exactly what 0.9.16's own
/// Vaktari.Core serialises for a fresh <c>SettingsState</c> — extracted from
/// the v0.9.16 tag with <c>git archive</c> and run — with four values changed
/// the way somebody's real file would have them, so the test can also see a
/// stored choice come through untouched. It still carries
/// <c>onOpeningExecutable</c>, which this build no longer has.
/// </summary>
public sealed class SettingsRepairDocumentTests
{
    /// <summary>settings.json as 0.9.16 wrote it, with four choices made.</summary>
    internal const string Written0916 = """
        {
          "version": 1,
          "general": {
            "naturalSorting": true,
            "caseSensitiveSorting": false,
            "rememberViewPerFolder": true,
            "showTooltips": false,
            "tabSwitchesSplitPanes": true,
            "useSystemIcons": false,
            "iconThemeFolder": "",
            "preferredTerminal": "",
            "protonDriveFolder": "",
            "closingSplitDiscardsOtherPane": false,
            "showPreviews": true,
            "maxLocalPreviewMegabytes": 0,
            "maxRemotePreviewMegabytes": 0,
            "confirmMoveToTrash": true,
            "confirmPermanentDelete": true,
            "confirmClosingMultipleTabs": false,
            "onOpeningExecutable": "OpenInApplication",
            "showStatusBar": true,
            "showFreeSpace": true
          },
          "startup": {
            "showOnStartup": "RestoreSession",
            "startupFolder": null,
            "beginInSplitView": false,
            "showFilterBar": false,
            "locationBarEditable": false,
            "showFullPathInTitleBar": false
          },
          "views": {
            "narrowDetailsPanel": "DisableToggle",
            "keepWidthAfterPanelClose": false,
            "followDesktopColours": false,
            "themeMode": "FollowDesktop",
            "customFontFamily": null,
            "icons": {
              "textWidth": 120,
              "maximumLines": 2,
              "spacing": 0
            },
            "compact": {
              "maximumTextWidth": 180,
              "spacing": 0
            },
            "details": {
              "folderSize": "ItemCount",
              "dateStyle": "Relative"
            }
          },
          "vcs": {
            "showDecorations": true
          },
          "navigation": {
            "openItemsWith": "System"
          },
          "contextMenu": {
            "showCopyTo": false,
            "showMoveTo": true,
            "showAddToPlaces": true,
            "showSortBy": true,
            "showOpenInNewTab": true,
            "showCopyLocation": true,
            "showDuplicate": true
          },
          "trash": {
            "deleteOldFiles": false,
            "deleteAfterDays": 12,
            "limitSize": false,
            "maximumPercentOfDisk": 10,
            "whenLimitReached": "Warn"
          }
        }
        """;

    private static SettingsState Read(string json, bool complete)
    {
        var document = (JsonObject)JsonNode.Parse(json)!;

        if (complete) SettingsRepair.CompleteDocument(document);

        return SettingsRepair.Complete(
            JsonSerializer.Deserialize(document, SettingsJsonContext.Default.SettingsState)!);
    }

    /// <summary>
    /// **The control.** Without the repair the file reads both keys as false —
    /// if this ever passes the other way, the serializer has started running
    /// initializers and the repair is doing nothing.
    /// </summary>
    [Fact]
    public void Without_the_repair_a_0916_file_turns_both_off()
    {
        var read = Read(Written0916, complete: false);

        Assert.False(read.General.RememberRecent);
        Assert.False(read.ContextMenu.ShowOpenInNewWindow);
    }

    /// <summary>The whole finding: an upgrading file gets the declared defaults.</summary>
    [Fact]
    public void A_0916_file_keeps_recording_and_keeps_open_in_new_window()
    {
        var read = Read(Written0916, complete: true);

        Assert.True(read.General.RememberRecent);
        Assert.True(read.ContextMenu.ShowOpenInNewWindow);
    }

    /// <summary>And the choices the file does make come through as made —
    /// false and non-default numbers included.</summary>
    [Fact]
    public void While_every_choice_it_made_is_kept()
    {
        var read = Read(Written0916, complete: true);

        Assert.False(read.General.ShowTooltips);
        Assert.True(read.General.RememberViewPerFolder);
        Assert.True(read.General.ConfirmMoveToTrash);
        Assert.False(read.ContextMenu.ShowCopyTo);
        Assert.Equal(12, read.Trash.DeleteAfterDays);
    }

    /// <summary>
    /// **A file that stores false on purpose keeps false.** This is what rules
    /// out re-defaulting the two keys by name: 0.10 and later write both, and
    /// somebody who switched the recent lists off must not find them back on.
    /// </summary>
    [Fact]
    public void A_file_that_stores_false_keeps_false()
    {
        var read = Read("""
            {"version":1,
             "general":{"rememberRecent":false},
             "contextMenu":{"showOpenInNewWindow":false}}
            """, complete: true);

        Assert.False(read.General.RememberRecent);
        Assert.False(read.ContextMenu.ShowOpenInNewWindow);
    }

    /// <summary>
    /// A group the file does not name is left for the record-level repair,
    /// which builds it fresh — so the document is not given groups here, and
    /// that repair's own lines stay reachable (AbsentSettingsTests).
    /// </summary>
    [Fact]
    public void A_group_the_file_does_not_name_is_not_invented()
    {
        var document = (JsonObject)JsonNode.Parse("""{"version":1,"general":{}}""")!;

        SettingsRepair.CompleteDocument(document);

        Assert.Null(document["contextMenu"]);
        Assert.Null(document["views"]);
        Assert.True(document["general"]!["rememberRecent"]!.GetValue<bool>());

        // Nor are strings: those are Complete(SettingsState)'s, for the same reason.
        Assert.Null(document["general"]!["protonDriveFolder"]);
    }

    // ---- and the next one ------------------------------------------------------

    /// <summary>The records a settings.json holds, found from the root.</summary>
    private static IEnumerable<Type> Groups()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(SettingsState)]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type)) continue;

            yield return type;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.PropertyType.Namespace == typeof(SettingsState).Namespace
                    && property.PropertyType.IsClass)
                    queue.Enqueue(property.PropertyType);
        }
    }

    /// <summary>
    /// **Every true-by-default or non-zero number, not just the two that bit.**
    /// The rule is what matters: the next <c>= true</c> somebody adds is
    /// protected the day it is added. For each such property, a group that
    /// names every key but that one reads it back as its declared default.
    /// Counted as well, so a walk that stopped finding them could not pass.
    /// </summary>
    [Fact]
    public void Every_scalar_with_a_non_zero_default_survives_being_absent()
    {
        var fresh = (JsonObject)JsonSerializer.SerializeToNode(
            new SettingsState(), SettingsJsonContext.Default.SettingsState)!;

        var checkedCount = 0;

        foreach (var (groupKey, groupNode) in fresh)
        {
            if (groupNode is not JsonObject group) continue;

            foreach (var (key, value) in group)
            {
                if (value is not JsonValue scalar) continue;
                if (scalar.GetValueKind() is not (JsonValueKind.True or JsonValueKind.Number)) continue;
                if (scalar.GetValueKind() == JsonValueKind.Number && scalar.GetValue<double>() == 0) continue;

                var document = (JsonObject)fresh.DeepClone();
                ((JsonObject)document[groupKey]!).Remove(key);

                SettingsRepair.CompleteDocument(document);

                Assert.True(
                    JsonNode.DeepEquals(scalar, document[groupKey]![key]),
                    $"{groupKey}.{key} came back {document[groupKey]![key]?.ToJsonString() ?? "absent"}, not {scalar.ToJsonString()}");

                checkedCount++;
            }
        }

        // Measured today: NaturalSorting, ShowTooltips, TabSwitchesSplitPanes,
        // RememberRecent, ShowPreviews, ConfirmPermanentDelete, ShowStatusBar,
        // ShowFreeSpace, ShowDecorations, eight context-menu entries and the
        // two trash numbers. Written under that so adding one is not a failure.
        Assert.True(checkedCount >= 15, $"only {checkedCount} non-zero defaults were found");
    }

    /// <summary>
    /// **Why enums may be skipped.** They are written as names, so the repair
    /// cannot tell one from a string — and it does not need to while every
    /// enum setting defaults to its zero member, which is what an absent key
    /// already reads as. The day one does not, this fails and says which.
    /// </summary>
    [Fact]
    public void Every_enum_setting_defaults_to_its_zero_member()
    {
        var enums = 0;

        foreach (var group in Groups())
        {
            var instance = Activator.CreateInstance(group)!;

            foreach (var property in group.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.PropertyType.IsEnum) continue;

                enums++;

                Assert.True(
                    Convert.ToInt64(property.GetValue(instance), System.Globalization.CultureInfo.InvariantCulture) == 0,
                    $"{group.Name}.{property.Name} defaults to {property.GetValue(instance)}, not its zero member");
            }
        }

        Assert.True(enums >= 8, $"only {enums} enum settings were found");
    }
}
