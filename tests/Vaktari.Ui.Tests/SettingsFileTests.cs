using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Finding settings.json, keeping a copy of it, and putting one back.
///
/// **The dialog could not say where any of it goes.** Six pages of choices, and
/// the only path on screen was the version line's tooltip — which is where the
/// BINARY is, a different place on every platform and on none of them this one.
/// So the file could be found by knowing where a platform keeps config, or by
/// searching the disk, and no other way. Which also meant a set-up could not be
/// kept, moved to a second machine, or put back after a bad afternoon.
/// </summary>
public class SettingsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-settings-file-" + Guid.NewGuid().ToString("N")[..8]);

    public SettingsFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string File(string name) => Path.Combine(_root, name);

    private SettingsViewModel Model(SettingsState? state = null)
        => new(state ?? new SettingsState(), settingsFile: File("settings.json"));

    // ---- where it is ---------------------------------------------------------

    /// <summary>
    /// **The folder, not the file.** Opening settings.json itself hands it to
    /// whatever the desktop registered for .json, which on a machine with a
    /// development environment installed is an IDE taking twenty seconds to
    /// start. "Where is it?" is a question about the folder.
    /// </summary>
    [AvaloniaFact]
    public void Show_settings_file_opens_the_folder_it_is_in()
    {
        var vm = Model();
        string? opened = null;

        vm.OpenUrlRequested += (_, url) => opened = url;
        vm.ShowSettingsFileCommand.Execute(null);

        Assert.Equal(_root, opened);
    }

    /// <summary>
    /// A view model with no file behind it does nothing rather than something
    /// wrong. The unit tests build dozens of those.
    /// </summary>
    [AvaloniaFact]
    public void Without_a_file_there_is_nothing_to_show()
    {
        var vm = new SettingsViewModel(new SettingsState());

        Assert.False(vm.HasSettingsFile);
        Assert.False(vm.ShowSettingsFileCommand.CanExecute(null));
        Assert.False(vm.ExportSettingsCommand.CanExecute(null));
        Assert.False(vm.ImportSettingsCommand.CanExecute(null));
    }

    // ---- keeping a copy ------------------------------------------------------

    /// <summary>
    /// **What is on screen, not what is on disk.** Copying the file would have
    /// been simpler and wrong: the dialog is where choices are made, so the
    /// moment anyone reaches for "keep a copy of this" is after changing
    /// something and before pressing Save — and a file copy there exports the
    /// state they are in the middle of replacing.
    /// </summary>
    [AvaloniaFact]
    public void A_copy_holds_the_unsaved_change_on_screen()
    {
        var vm = Model(new SettingsState
        {
            General = new GeneralSettings { NaturalSorting = true },
        });

        vm.NaturalSorting = false;

        var copy = File("copy.json");

        Assert.True(vm.ExportTo(copy));

        Assert.False(JsonSettingsStore.Import(copy)!.General.NaturalSorting);
    }

    /// <summary>A path that cannot be written is said out loud, not swallowed.</summary>
    [AvaloniaFact]
    public void A_copy_that_cannot_be_written_says_so()
    {
        var vm = Model();

        Assert.False(vm.ExportTo(Path.Combine(_root, "no-such-folder", "copy.json")));
        Assert.NotEqual("", vm.SettingsFileStatus);
    }

    // ---- putting one back ----------------------------------------------------

    /// <summary>
    /// The imported state is what the window is handed, and it leaves through
    /// the same door Save uses — so it is applied and written by the one
    /// handler that already does both.
    /// </summary>
    [AvaloniaFact]
    public void Replacing_from_a_copy_hands_that_state_out_and_closes()
    {
        var copy = File("copy.json");

        Assert.True(JsonSettingsStore.Export(
            copy,
            new SettingsState { General = new GeneralSettings { ShowStatusBar = false } }));

        var vm = Model();
        var closed = 0;

        vm.CloseRequested += (_, _) => closed++;

        Assert.True(vm.ImportFrom(copy));
        Assert.True(vm.Saved);
        Assert.False(vm.Result.General.ShowStatusBar);
        Assert.Equal(1, closed);
    }

    /// <summary>
    /// **A file this version cannot read is refused, not defaulted.** Startup
    /// answers an unreadable file with defaults because there is nothing else
    /// it can do. Here somebody is pointing at a file they believe holds their
    /// settings, and answering with defaults would silently reset every choice
    /// they have — the exact opposite of what they asked for.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("{ \"version\": 9999 }")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void A_file_this_version_cannot_read_is_refused(string contents)
    {
        var bad = File("bad.json");
        System.IO.File.WriteAllText(bad, contents);

        var vm = Model();
        var closed = 0;

        vm.CloseRequested += (_, _) => closed++;

        Assert.False(vm.ImportFrom(bad));
        Assert.False(vm.Saved);
        Assert.Equal(0, closed);
        Assert.NotEqual("", vm.SettingsFileStatus);
    }

    /// <summary>And a file that is not there at all is the same refusal.</summary>
    [AvaloniaFact]
    public void A_file_that_is_not_there_is_refused_too()
    {
        var vm = Model();

        Assert.False(vm.ImportFrom(File("absent.json")));
        Assert.False(vm.Saved);
    }

    /// <summary>
    /// The round trip, so the two halves are pinned against each other rather
    /// than each against its own idea of the format.
    /// </summary>
    [AvaloniaFact]
    public void A_copy_read_back_is_the_settings_that_were_written()
    {
        var vm = Model(new SettingsState());

        vm.ShowStatusBar = false;
        vm.NaturalSorting = false;
        vm.BackspaceGoesUp = true;

        var copy = File("round-trip.json");

        Assert.True(vm.ExportTo(copy));

        var back = Model();

        Assert.True(back.ImportFrom(copy));
        Assert.False(back.Result.General.ShowStatusBar);
        Assert.False(back.Result.General.NaturalSorting);
        Assert.True(back.Result.Navigation.BackspaceGoesUp);
    }
}
