using System.Text.Json;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A settings.json that does not mention a section.
///
/// **`{"version":1}` loaded as a SettingsState whose every group was null.**
/// MEASURED 5 September 2026 through the real source-generated context: it does
/// not run property initializers, so a key absent from the file arrives as
/// `default(T)` rather than as the declared default — and for a group that is
/// null, not an empty record. Version 1 IS the current version, so the gate in
/// <see cref="JsonSettingsStore.Load"/> passed the document and returned it
/// as-is, against the promise in its own summary that there is always a valid
/// set of preferences.
///
/// It did not crash startup, and that is the interesting part: <c>AppSettings
/// .Apply</c> repaired what it published, so the launch path was covered and
/// nothing else was. The record handed to Apply stayed exactly as deserialized,
/// so the settings dialog's Closed handler — which reads `model.Result`, not
/// `AppSettings.Current` — threw a NullReferenceException on
/// `Result.General.ProtonDriveFolder` after Replace-from-a-copy was pointed at
/// such a file. The repair now runs where the file becomes a record, so Load
/// and Import both keep the promise.
///
/// This is NOT about a scalar arriving as its zero — that is documented at each
/// property, named for its zero value, and pinned elsewhere (see
/// <c>InterfaceTextSizeTests</c>). It is about the reference-typed groups, for
/// which the zero is null and nothing guarded them.
/// </summary>
public sealed class AbsentSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-absent-settings-" + Guid.NewGuid().ToString("N")[..8]);

    // Apply publishes to a static that the whole assembly reads, and these
    // classes run in sequence — so a leak here lands on somebody else's
    // assertion rather than on this file's.
    private readonly SettingsState _settingsBefore = AppSettings.Current;

    public AbsentSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        AppSettings.Apply(_settingsBefore);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>A v1 document that names no section at all.</summary>
    private const string NamesNothing = "{\"version\":1}";

    /// <summary>One that opens `general` and then names nothing inside it.</summary>
    private const string NamesNoGeneralKey = "{\"version\":1,\"general\":{}}";

    /// <summary>
    /// And one that opens `views` and names nothing inside it.
    ///
    /// **Not the same document as <see cref="NamesNothing"/>, and the difference
    /// is the whole test.** With `views` absent the repair hands back a freshly
    /// constructed <see cref="ViewSettings"/> — whose initializers DO run,
    /// because that is a constructor — so the three layouts underneath it are
    /// already present and the lines that put them there are never reached. Only
    /// a `views` object that IS in the file, holding none of them, exercises
    /// them; on this document all three arrive null.
    /// </summary>
    private const string NamesNoViewsKey = "{\"version\":1,\"views\":{}}";

    /// <summary>Writes a settings.json holding exactly the given JSON, and loads it.</summary>
    private SettingsState Load(string json)
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), json);
        return new JsonSettingsStore(_root).Load();
    }

    /// <summary>The same, as a file somebody points Replace-from-a-copy at.</summary>
    private string Copy(string json)
    {
        var path = Path.Combine(_root, "copy.json");
        File.WriteAllText(path, json);
        return path;
    }

    // ---- the mechanism itself -----------------------------------------------

    /// <summary>
    /// **A GUARD, and it says so.** Nothing in this repository can be mutated to
    /// redden it: what it pins is System.Text.Json's behaviour, which is the
    /// reason the rest of this file exists. If it ever fails, the source
    /// generator has started running initializers and
    /// <see cref="SettingsRepair"/> can be deleted rather than extended.
    /// </summary>
    [Fact]
    public void The_serializer_still_skips_the_property_initializers()
    {
        var raw = JsonSerializer.Deserialize(
            NamesNothing, SettingsJsonContext.Default.SettingsState);

        Assert.NotNull(raw);
        Assert.Null(raw!.Views);
        Assert.Null(raw.General);
        Assert.Equal(SettingsState.CurrentVersion, raw.Version);
    }

    // ---- the groups ---------------------------------------------------------

    private static object? Group(SettingsState state, string name) => name switch
    {
        nameof(SettingsState.General) => state.General,
        nameof(SettingsState.Startup) => state.Startup,
        nameof(SettingsState.Views) => state.Views,
        nameof(SettingsState.Vcs) => state.Vcs,
        nameof(SettingsState.Navigation) => state.Navigation,
        nameof(SettingsState.ContextMenu) => state.ContextMenu,
        nameof(SettingsState.Trash) => state.Trash,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a group"),
    };

    /// <summary>
    /// Every group, one case each, because the repair is a list and a list is
    /// exactly the shape that loses an entry when somebody adds a group and
    /// forgets this file. `Vcs` was added once already and arrived null.
    /// </summary>
    [Theory]
    [InlineData(nameof(SettingsState.General))]
    [InlineData(nameof(SettingsState.Startup))]
    [InlineData(nameof(SettingsState.Views))]
    [InlineData(nameof(SettingsState.Vcs))]
    [InlineData(nameof(SettingsState.Navigation))]
    [InlineData(nameof(SettingsState.ContextMenu))]
    [InlineData(nameof(SettingsState.Trash))]
    public void A_file_that_names_no_section_still_loads_every_one(string group)
        => Assert.NotNull(Group(Load(NamesNothing), group));

    private static object? Layout(ViewSettings views, string name) => name switch
    {
        nameof(ViewSettings.Icons) => views.Icons,
        nameof(ViewSettings.Compact) => views.Compact,
        nameof(ViewSettings.Details) => views.Details,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a layout"),
    };

    /// <summary>
    /// And the three layouts one level down. Repairing the outer group and
    /// stopping there would look handled while `Views.Icons.Spacing` still threw.
    /// </summary>
    [Theory]
    [InlineData(nameof(ViewSettings.Icons))]
    [InlineData(nameof(ViewSettings.Compact))]
    [InlineData(nameof(ViewSettings.Details))]
    public void And_the_three_layouts_underneath_the_views(string layout)
        => Assert.NotNull(Layout(Load(NamesNoViewsKey).Views, layout));

    /// <summary>
    /// A group the file DOES name keeps what it says, so the repair cannot be a
    /// blanket reset of anything it does not recognise.
    /// </summary>
    [Fact]
    public void A_section_the_file_does_name_is_left_alone()
        => Assert.False(
            Load("{\"version\":1,\"general\":{\"naturalSorting\":false}}").General.NaturalSorting);

    // ---- the strings ---------------------------------------------------------

    private static string? Text(GeneralSettings general, string name) => name switch
    {
        nameof(GeneralSettings.IconThemeFolder) => general.IconThemeFolder,
        nameof(GeneralSettings.PreferredTerminal) => general.PreferredTerminal,
        nameof(GeneralSettings.ProtonDriveFolder) => general.ProtonDriveFolder,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a string setting"),
    };

    /// <summary>
    /// The same hazard on strings, where the zero is null rather than "".
    ///
    /// **`ProtonDriveFolder` was the one still missing**, and it is the property
    /// the settings dialog calls `.Trim()` on when it collects a save — so an
    /// install whose settings.json predated that property crashed on Save, while
    /// the two beside it had been coerced for a release already.
    /// </summary>
    [Theory]
    [InlineData(nameof(GeneralSettings.IconThemeFolder))]
    [InlineData(nameof(GeneralSettings.PreferredTerminal))]
    [InlineData(nameof(GeneralSettings.ProtonDriveFolder))]
    public void A_general_that_names_no_string_key_gets_empty_ones(string key)
        => Assert.Equal("", Text(Load(NamesNoGeneralKey).General, key));

    /// <summary>
    /// The two declared `string?` are left as null, because null is what they
    /// mean — "no folder chosen", "no font chosen" — and coercing them to ""
    /// would make an unset startup folder look like a chosen empty one.
    /// </summary>
    [Fact]
    public void And_the_ones_that_are_allowed_to_be_null_stay_null()
    {
        var state = Load(NamesNothing);

        Assert.Null(state.Startup.StartupFolder);
        Assert.Null(state.Views.CustomFontFamily);
    }

    // ---- the version gate, which still runs ---------------------------------

    /// <summary>
    /// Completing a state does not make it readable. A file from a later version
    /// is still answered with defaults rather than with its own contents tidied
    /// up — half the settings somebody chose is worse than none of them, which
    /// is what <see cref="JsonSettingsStore.Load"/> already said.
    /// </summary>
    [Fact]
    public void A_file_from_a_later_version_is_still_ignored()
    {
        var state = Load("{\"version\":9999,\"general\":{\"naturalSorting\":false}}");

        Assert.Equal(SettingsState.CurrentVersion, state.Version);
        Assert.True(state.General.NaturalSorting);
    }

    // ---- the door that was not covered --------------------------------------

    /// <summary>
    /// Import as well as Load, and pinned separately from it: these are two
    /// methods over one <c>TryLoad</c>, and the repair belongs to the reading
    /// rather than to either caller. Import is the door that was crashing.
    /// </summary>
    [Fact]
    public void A_copy_that_names_no_section_is_imported_complete()
    {
        var state = JsonSettingsStore.Import(Copy(NamesNothing));

        Assert.NotNull(state);
        Assert.NotNull(state!.General);
        Assert.NotNull(state.Views);
        Assert.NotNull(state.Vcs);
    }

    /// <summary>
    /// **The exact expression that threw.** MainWindow's Closed handler reads
    /// `model.Result.General.ProtonDriveFolder` to point the Proton mapping at
    /// the saved folder, and `model.Result` is whatever Import returned —
    /// applying the state to <c>AppSettings</c> a few lines above does not
    /// repair the record itself, because records are values and Apply keeps its
    /// repair.
    /// </summary>
    [AvaloniaFact]
    public void The_dialog_hands_out_an_imported_state_the_window_can_read()
    {
        var vm = new SettingsViewModel(
            new SettingsState(), settingsFile: Path.Combine(_root, "settings.json"));

        Assert.True(vm.ImportFrom(Copy(NamesNothing)));
        Assert.Equal("", vm.Result.General.ProtonDriveFolder);
        Assert.NotNull(vm.Result.Views.Details);
    }

    /// <summary>
    /// And one it can save. <c>Collect</c> carries the opened state forward with
    /// `with`, which throws on a null group, and trims the Proton folder, which
    /// throws on a null string — so the dialog had two ways to crash on a file
    /// that merely predated a property.
    /// </summary>
    [AvaloniaFact]
    public void And_one_it_can_save()
    {
        var vm = new SettingsViewModel(
            Load(NamesNoGeneralKey), settingsFile: Path.Combine(_root, "settings.json"));

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Saved);
        Assert.Equal("", vm.Result.General.ProtonDriveFolder);
        Assert.NotNull(vm.Result.Vcs);
    }

    // ---- the other door ------------------------------------------------------

    /// <summary>
    /// <c>AppSettings.Apply</c> completes too, and that is not redundant with the
    /// store: Apply is reached by the settings dialog's own result, by
    /// <c>DefaultViewChanged</c>, and by tests, none of which came through a
    /// file. It was the only guard there was, and nothing pinned it.
    /// </summary>
    [Fact]
    public void A_state_handed_straight_to_the_settings_is_completed_too()
    {
        var raw = JsonSerializer.Deserialize(
            NamesNothing, SettingsJsonContext.Default.SettingsState)!;

        AppSettings.Apply(raw);

        Assert.NotNull(AppSettings.Current.General);
        Assert.NotNull(AppSettings.Current.Views.Icons);
        Assert.Equal("", AppSettings.Current.General.ProtonDriveFolder);
    }
}
