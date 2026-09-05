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

    // ---- putting everything back --------------------------------------------

    /// <summary>
    /// **There was no way back to the defaults.** Nine sections on one page and
    /// five more pages, each remembering what it was last set to, and the only
    /// route out was to close Vaktari, find settings.json, delete it and start
    /// again.
    /// </summary>
    [AvaloniaFact]
    public void Restoring_defaults_puts_a_changed_setting_back()
    {
        var vm = Model(new SettingsState
        {
            General = new GeneralSettings { NaturalSorting = false, ShowStatusBar = false },
        });

        vm.RestoreDefaultsCommand.Execute(null);

        var fresh = new SettingsState();

        Assert.Equal(fresh.General.NaturalSorting, vm.NaturalSorting);
        Assert.Equal(fresh.General.ShowStatusBar, vm.ShowStatusBar);
    }

    /// <summary>
    /// **And a page this dialog never built.** Collect carries the opened state
    /// forward with `with`, so a reset that only touched the controls on screen
    /// would leave every setting on an unopened page exactly as it was — which
    /// is not what the button says.
    /// </summary>
    [AvaloniaFact]
    public void Restoring_defaults_reaches_settings_no_control_shows()
    {
        var vm = Model(new SettingsState
        {
            Views = new ViewSettings
            {
                Details = new DetailsViewSettings { FolderSize = FolderSizeMode.ContentSize },
            },
        });

        vm.RestoreDefaultsCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(new SettingsState().Views.Details.FolderSize, vm.Result.Views.Details.FolderSize);
    }

    /// <summary>
    /// **Nothing reaches disk until Save**, which is why there is no
    /// confirmation: Cancel discards a restore exactly as it discards any other
    /// change, so the defaults are on screen to be looked at first.
    /// </summary>
    [AvaloniaFact]
    public void Restoring_defaults_does_not_save_anything()
    {
        var vm = Model();
        var closed = 0;

        vm.CloseRequested += (_, _) => closed++;
        vm.RestoreDefaultsCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.Equal(0, closed);
        Assert.NotEqual("", vm.SettingsFileStatus);
    }

    /// <summary>
    /// The controls have to be told, or the boxes go on showing the old values
    /// over the new ones and Save writes those old values straight back.
    /// </summary>
    [AvaloniaFact]
    public void Restoring_defaults_tells_the_controls()
    {
        var vm = Model(new SettingsState
        {
            General = new GeneralSettings { NaturalSorting = false },
        });

        var told = new List<string?>();

        vm.PropertyChanged += (_, e) => told.Add(e.PropertyName);
        vm.RestoreDefaultsCommand.Execute(null);

        Assert.Contains(nameof(vm.NaturalSorting), told);
    }

    // ---- the startup folder box ---------------------------------------------

    /// <summary>
    /// **The placeholder was "/home/…" on Windows** — a hardcoded Linux path in
    /// the one control whose whole job is to be given a path on THIS machine.
    /// </summary>
    [AvaloniaFact]
    public void The_startup_box_suggests_a_folder_on_this_machine()
    {
        var hint = SettingsViewModel.StartupFolderHint;

        Assert.NotEqual("/home/\u2026", hint);
        Assert.True(Directory.Exists(hint), $"the hint {hint} is not a folder on this machine");
    }

    /// <summary>
    /// **A folder that is not there is said, not refused.** The dialog saved
    /// whatever was typed and the next launch opened home instead, in silence —
    /// so a typo looked exactly like the setting not working. Saying it beats
    /// refusing it: the folder may be on a stick that is out.
    /// </summary>
    [AvaloniaFact]
    public void A_startup_folder_that_is_not_there_is_said()
    {
        var vm = Model();

        vm.StartInSpecificFolder = true;
        vm.StartupFolder = Path.Combine(_root, "went-away");

        Assert.True(vm.HasStartupFolderProblem);
        Assert.NotEqual("", vm.StartupFolderProblem);

        // And still saved, which is the half a refusal would have got wrong.
        vm.SaveCommand.Execute(null);

        Assert.Equal(Path.Combine(_root, "went-away"), vm.Result.Startup.StartupFolder);
    }

    /// <summary>A folder that IS there says nothing.</summary>
    [AvaloniaFact]
    public void A_startup_folder_that_is_there_says_nothing()
    {
        var vm = Model();

        vm.StartInSpecificFolder = true;
        vm.StartupFolder = _root;

        Assert.False(vm.HasStartupFolderProblem);
    }

    /// <summary>
    /// And the warning is about a folder only one choice consults, so choosing
    /// something else takes it away rather than leaving a complaint on screen
    /// about a box that no longer matters.
    ///
    /// **Asserted on the NOTIFICATION as well as the value, and that cost a
    /// revert-check.** StartupFolderProblem is computed, so reading it always
    /// gives the fresh answer whether or not anything was announced — a test
    /// that only read it passed with the announcement deleted, while the
    /// warning stayed on screen under a radio button nobody had chosen.
    /// </summary>
    [AvaloniaFact]
    public void And_it_goes_away_with_the_choice_that_uses_it()
    {
        var vm = Model();

        vm.StartInSpecificFolder = true;
        vm.StartupFolder = Path.Combine(_root, "went-away");

        Assert.True(vm.HasStartupFolderProblem);

        var told = new List<string?>();

        vm.PropertyChanged += (_, e) => told.Add(e.PropertyName);
        vm.StartInSpecificFolder = false;

        Assert.False(vm.HasStartupFolderProblem);
        Assert.Contains(nameof(vm.HasStartupFolderProblem), told);
    }

    /// <summary>
    /// And typing announces it too, for the same reason: the box is what is
    /// being typed into, so that is when the warning has to appear.
    /// </summary>
    [AvaloniaFact]
    public void Typing_a_folder_that_is_not_there_announces_the_warning()
    {
        var vm = Model();

        vm.StartInSpecificFolder = true;

        var told = new List<string?>();

        vm.PropertyChanged += (_, e) => told.Add(e.PropertyName);
        vm.StartupFolder = Path.Combine(_root, "went-away");

        Assert.Contains(nameof(vm.StartupFolderProblem), told);
        Assert.Contains(nameof(vm.HasStartupFolderProblem), told);
    }

    /// <summary>An empty box is not a complaint; it is a box nobody has filled in.</summary>
    [AvaloniaFact]
    public void An_empty_startup_box_says_nothing()
    {
        var vm = Model();

        vm.StartInSpecificFolder = true;
        vm.StartupFolder = "";

        Assert.False(vm.HasStartupFolderProblem);
    }

    /// <summary>
    /// The Browse button asks the window for a picker, since a view model that
    /// opened one could not be built in a test — the same shape as the icon
    /// theme browse beside it.
    /// </summary>
    [AvaloniaFact]
    public void Browse_asks_the_window_for_a_picker()
    {
        var vm = Model();
        var asked = 0;

        vm.StartupFolderBrowseRequested += (_, _) => asked++;
        vm.BrowseForStartupFolderCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    // ---- the folder views nothing could see ---------------------------------

    /// <summary>A store that reports a count and remembers being cleared.</summary>
    private sealed class Views(int remembered) : Vaktari.Core.FileSystem.IFolderViewStore
    {
        internal int Cleared;

        public Vaktari.Core.FileSystem.FolderViewState? Read(string path) => null;
        public void Write(string path, Vaktari.Core.FileSystem.FolderViewState state) { }
        public void Forget(string path) { }

        public int Remembered => remembered;

        public int ForgetAll()
        {
            Cleared++;
            return remembered;
        }
    }

    private SettingsViewModel WithViews(int remembered)
        => new(new SettingsState(), settingsFile: File("settings.json"),
               folderViews: new Views(remembered));

    /// <summary>
    /// **Turning the setting off left every folder already recorded exactly as
    /// it was.** The store fills up on its own — merely looking at a folder
    /// writes to it — Forget(path) had never been called by anything, and the
    /// file was invisible from the application, so a listing that had once been
    /// given a layout kept it with the feature switched off.
    /// </summary>
    [AvaloniaFact]
    public void Forgetting_the_remembered_views_is_offered_when_there_are_some()
    {
        var vm = WithViews(4);

        Assert.True(vm.HasRememberedViews);
        Assert.True(vm.ForgetRememberedViewsCommand.CanExecute(null));
        Assert.Equal("4 folders are remembered", vm.RememberedViewsLabel);
    }

    /// <summary>
    /// And not offered when there are none: a button that clears an empty list
    /// is a button that does nothing.
    /// </summary>
    [AvaloniaFact]
    public void And_is_not_offered_when_there_are_none()
    {
        var vm = WithViews(0);

        Assert.False(vm.HasRememberedViews);
        Assert.False(vm.ForgetRememberedViewsCommand.CanExecute(null));
    }

    /// <summary>One is one, not "1 folders".</summary>
    [AvaloniaFact]
    public void One_folder_is_said_in_the_singular()
        => Assert.Equal("One folder is remembered", WithViews(1).RememberedViewsLabel);

    /// <summary>
    /// **Armed, not done.** Pressing it must behave like every other control on
    /// these six pages: nothing has happened until Save, so Cancel throws it
    /// away. Pressing it and cancelling would otherwise clear a list the person
    /// then chose not to clear.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_it_does_not_clear_anything_yet()
    {
        var store = new Views(4);

        var vm = new SettingsViewModel(
            new SettingsState(), settingsFile: File("settings.json"), folderViews: store);

        vm.ForgetRememberedViewsCommand.Execute(null);

        Assert.True(vm.ForgetViewsOnSave);
        Assert.Equal(0, store.Cleared);
        Assert.NotEqual("", vm.SettingsFileStatus);
    }

    /// <summary>Until it is pressed, the save has nothing to do.</summary>
    [AvaloniaFact]
    public void And_a_save_that_was_not_asked_forgets_nothing()
        => Assert.False(WithViews(4).ForgetViewsOnSave);

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
