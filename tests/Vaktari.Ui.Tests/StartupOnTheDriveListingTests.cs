using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The drive listing — "This PC" on Windows, "This computer" on Linux — as
/// somewhere a launch can begin.
///
/// **It was reachable from everywhere except the setting that decides where a
/// launch begins.** The sidebar row opens it, Up from a drive root arrives at
/// it, its breadcrumb is one press away and the path bar accepts its name; the
/// Startup page offered a session, the home folder, or a directory. And the
/// directory arm could not stand in for it: the listing lives at the virtual
/// path <c>vaktari:computer</c>, MainWindow gates that arm on
/// <c>Directory.Exists</c>, and the dialog warns under the box when the text
/// typed there is not a directory — so somebody who found the internal name
/// and typed it got a warning, saved anyway, and launched into their home
/// folder. Explorer's own startup choice is exactly This PC or Home.
///
/// Both directions of the dialog are covered rather than one, because the
/// failure that matters for a setting the file can already hold is the
/// silent-drop: seeding the wrong radio makes opening the dialog and pressing
/// Save rewrite a choice the person made.
///
/// The last test here is about disk rather than the dialog, and it belongs
/// beside them: the choice is a name in settings.json, and a name is the one
/// part of a persisted enum that appending a member cannot make safe.
/// </summary>
public class StartupOnTheDriveListingTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static SettingsState With(StartupLocation location, string? folder = null) => new()
    {
        Startup = new StartupSettings { ShowOnStartup = location, StartupFolder = folder },
    };

    /// <summary>
    /// Every startup choice seeds its own radio and no other, so a saved
    /// preference cannot arrive on screen as somebody else's.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(StartupLocation.Computer, false, false, true, false)]
    [InlineData(StartupLocation.RestoreSession, true, false, false, false)]
    [InlineData(StartupLocation.HomeFolder, false, true, false, false)]
    [InlineData(StartupLocation.SpecificFolder, false, false, false, true)]
    public void The_saved_choice_checks_its_own_radio(
        StartupLocation saved, bool restore, bool home, bool computer, bool folder)
    {
        var vm = new SettingsViewModel(With(saved));

        Assert.Equal(restore, vm.RestoreLastSession);
        Assert.Equal(home, vm.StartInHome);
        Assert.Equal(computer, vm.StartInComputer);
        Assert.Equal(folder, vm.StartInSpecificFolder);
    }

    /// <summary>
    /// And the picked radio is what reaches the record the dialog hands back.
    /// </summary>
    [AvaloniaFact]
    public void The_drive_listing_radio_is_what_gets_saved()
    {
        var vm = new SettingsViewModel(With(StartupLocation.RestoreSession))
        {
            RestoreLastSession = false,
            StartInComputer = true,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(StartupLocation.Computer, vm.Result.Startup.ShowOnStartup);
    }

    /// <summary>
    /// Opening the dialog and pressing Save without touching anything gives the
    /// choice back unchanged.
    ///
    /// **This is the regression that matters most for a value the file could
    /// hold before a control existed for it.** A hand-edited settings.json
    /// naming a choice the dialog cannot seed is silently rewritten by the
    /// first person who opens Settings and presses Save.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(StartupLocation.Computer)]
    [InlineData(StartupLocation.HomeFolder)]
    [InlineData(StartupLocation.RestoreSession)]
    public void Saving_without_changing_anything_preserves_the_choice(StartupLocation saved)
    {
        var vm = new SettingsViewModel(With(saved));

        vm.SaveCommand.Execute(null);

        Assert.Equal(saved, vm.Result.Startup.ShowOnStartup);
    }

    /// <summary>
    /// The radio says the platform's own word for the listing, whole.
    ///
    /// **Asserted as the entire label rather than as "contains This PC"**: the
    /// failure guarded against here is that one platform's noun is baked in and
    /// shown on the other, and "Open This PC" on Linux contains the right words
    /// for Windows while naming a thing that desktop has never called that.
    ///
    /// The Linux case is lowercase, and that is the assertion doing the second
    /// job here. The noun sits mid-sentence, "This computer" is a description
    /// rather than a proper noun there, and sentence case is the house rule for
    /// every label — the two platform-noun labels already on this page,
    /// ConfirmTrashLabel and LimitBinLabel, both read the lowercase form for the
    /// same reason. Windows keeps its capitals in both forms because Explorer
    /// does.
    ///
    /// Naming is a process-wide static, so each case sets it and restores it —
    /// otherwise whichever test ran first would decide the words for every test
    /// after it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("windows", "Open This PC")]
    [InlineData("linux", "Open this computer")]
    public void The_radio_uses_the_platform_word_for_the_listing(string platform, string label)
    {
        var previousBin = Core.Naming.BinName;
        var previousPlatform = Core.Naming.Platform;

        try
        {
            Core.Naming.Adopt(previousBin, platform);

            var vm = new SettingsViewModel(With(StartupLocation.Computer));

            Assert.Equal(label, vm.StartInComputerLabel);
        }
        finally
        {
            Core.Naming.Adopt(previousBin, previousPlatform);
        }
    }

    /// <summary>
    /// The folder box below stays out of it.
    ///
    /// **The box and its warning belong to one of the four choices only.** The
    /// listing is not a directory, so a box offering to name one — and a line
    /// under it saying that name is not there — would be the dialog asking for
    /// something the choice does not take. A stale path already in the file
    /// must not raise the warning either, which is why a folder is seeded here
    /// rather than left empty.
    ///
    /// The stale path carries a fresh GUID rather than a fixed name. "Not
    /// there" is the premise the assertions rest on, so the test has to
    /// ESTABLISH it: a constant under %TEMP% is a machine fact borrowed from
    /// whatever else has run on the box, and one directory of that name makes
    /// the warning empty for a reason that has nothing to do with the choice.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_box_is_not_offered_for_the_drive_listing()
    {
        var gone = Path.Combine(
            Path.GetTempPath(), "vaktari-gone-" + Guid.NewGuid().ToString("N")[..12]);

        Assert.False(Directory.Exists(gone));

        var vm = new SettingsViewModel(With(StartupLocation.Computer, gone));

        Assert.False(vm.CanEditStartupFolder);
        Assert.False(vm.HasStartupFolderProblem);
        Assert.Equal("", vm.StartupFolderProblem);
    }

    /// <summary>
    /// And the page really offers it.
    ///
    /// **A view-model property with nothing bound to it is a preference nobody
    /// can set**, which is the state this finding started in — and compiled
    /// bindings would let <c>StartInComputer</c> go on existing, and go on
    /// passing every test above, with no control on the page touching it.
    ///
    /// The GroupName is asserted as well as the binding: a radio in its own
    /// group stays checked while another choice is picked beside it, so the
    /// dialog would offer two answers to one question.
    /// </summary>
    [Fact]
    public void The_startup_page_offers_the_drive_listing()
    {
        var page = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "TabItem")
            .Single(t => (string?)t.Attribute("Header") == "Startup");

        var radio = page.Descendants(Avalonia + "RadioButton").Single(
            r => (string?)r.Attribute("IsChecked") == "{Binding StartInComputer}");

        Assert.Equal("{Binding StartInComputerLabel}", (string?)radio.Attribute("Content"));
        Assert.Equal("startup", (string?)radio.Attribute("GroupName"));
    }

    /// <summary>
    /// What the new NAME costs on disk, which is not what the new number costs.
    ///
    /// **Appending the member protected every number already in a
    /// settings.json; nothing could protect the name, and the price is the
    /// whole file.** Measured through the real store: a settings.json whose
    /// showOnStartup is a choice this build does not have comes back with every
    /// preference on every page at its default — not merely the startup one.
    /// JsonSettingsStore.TryLoad catches, returns null, and Load answers with a
    /// fresh SettingsState; and the next Save writes those defaults over the
    /// file. So a person who ran a newer Vaktari, chose the drive listing, then
    /// opened an older one loses their sorting, their status bar and their bin
    /// limits along with the choice.
    ///
    /// The same file with a name this build DOES have is loaded here as the
    /// control, because "everything came back default" proves nothing unless
    /// the fixture would otherwise have carried non-defaults through.
    ///
    /// Not a guard. It fails the moment the store learns to keep the settings
    /// it could read — which is the fix this test exists to make visible, and
    /// which is deliberately not part of this change.
    /// </summary>
    [Fact]
    public void A_startup_name_this_build_does_not_have_costs_the_whole_file()
    {
        static SettingsState Load(string choice)
        {
            var directory = Directory.CreateTempSubdirectory("vaktari-older-").FullName;

            File.WriteAllText(Path.Combine(directory, "settings.json"), $$"""
                {
                  "version": 1,
                  "startup": { "showOnStartup": "{{choice}}" },
                  "general": { "naturalSorting": false, "showStatusBar": false },
                  "trash": { "deleteAfterDays": 7 }
                }
                """);

            try { return new JsonSettingsStore(directory).Load(); }
            finally { Directory.Delete(directory, recursive: true); }
        }

        var control = Load("Computer");

        Assert.Equal(StartupLocation.Computer, control.Startup.ShowOnStartup);
        Assert.False(control.General.NaturalSorting);
        Assert.False(control.General.ShowStatusBar);
        Assert.Equal(7, control.Trash.DeleteAfterDays);

        var older = Load("SomethingThisBuildHasNeverHeardOf");
        var defaults = new SettingsState();

        Assert.Equal(defaults.Startup.ShowOnStartup, older.Startup.ShowOnStartup);
        Assert.Equal(defaults.General.NaturalSorting, older.General.NaturalSorting);
        Assert.Equal(defaults.General.ShowStatusBar, older.General.ShowStatusBar);
        Assert.Equal(defaults.Trash.DeleteAfterDays, older.Trash.DeleteAfterDays);
    }

    /// <summary>
    /// **And the three radios beside it still bind their own properties.**
    ///
    /// Not paranoia: BackspacePreferenceTests records a control beside a newly
    /// added one silently becoming a second switch for it, on this very page,
    /// with the whole suite still green. Four radios in one group is exactly
    /// the shape that happens in.
    /// </summary>
    [Theory]
    [InlineData("Restore folders, tabs and window from last time", "RestoreLastSession")]
    [InlineData("Open the home folder", "StartInHome")]
    [InlineData("Open a specific folder", "StartInSpecificFolder")]
    public void The_radios_beside_it_are_untouched(string label, string property)
    {
        var page = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "TabItem")
            .Single(t => (string?)t.Attribute("Header") == "Startup");

        var radio = page.Descendants(Avalonia + "RadioButton")
            .Single(r => (string?)r.Attribute("Content") == label
                         || r.Descendants(Avalonia + "TextBlock")
                             .Any(t => (string?)t.Attribute("Text") == label));

        Assert.Equal("{Binding " + property + "}", (string?)radio.Attribute("IsChecked"));
    }
}
