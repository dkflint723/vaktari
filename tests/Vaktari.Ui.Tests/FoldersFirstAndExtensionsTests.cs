using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The two switches every file manager has and this one did not.
///
/// **Folders were banded above the files by a comparer that said "always".**
/// <c>PaneViewModel.CompareWithin</c> returned on <c>IsDirectory</c> before it
/// looked at the sort field, consulting nothing — so sorting a folder by
/// modified could not answer "what changed here most recently" if the answer
/// was a folder, and settings.json had no key to say otherwise.
///
/// **And the name column always drew the whole file name.**
/// <c>FileKind.DisplayName</c> hid .lnk on Windows and .desktop on Linux,
/// because each platform's own shell hides them, and nothing else ever —
/// Explorer's "File name extensions" had no counterpart here at all.
///
/// Both settings are named for their ZERO value in the record and asked
/// positively in the dialog, because deserialization does not run property
/// initializers: an absent key arrives as <c>default(bool)</c>, so false has to
/// be the behaviour every upgrading install already has.
/// </summary>
public sealed class FoldersFirstAndExtensionsTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private readonly Func<string, string?>? _launcherName = FileKind.LauncherName;

    /// <summary>
    /// Puts the live preferences back, which also puts
    /// <see cref="FileKind.HideExtensions"/> back — Apply pushes it, so the one
    /// restore covers both. This assembly disables parallelisation, so the
    /// window between them cannot be observed by another class.
    ///
    /// <see cref="FileKind.LauncherName"/> is the other per-process static this
    /// class writes, and is put back the way
    /// <see cref="LauncherRowNameTests"/> does it.
    /// </summary>
    public override void Dispose()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        FileKind.LauncherName = _launcherName;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void MixFolders(bool on)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(
            before with { General = before.General with { MixFoldersWithFiles = on } });
    }

    private static void HideExtensions(bool on)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(
            before with { Views = before.Views with { HideFileExtensions = on } });
    }

    // ---- the fake ------------------------------------------------------------

    private static string Folder => Path.Combine(Path.GetTempPath(), "vaktari-folders-first");

    private static FileEntry Row(string name, bool directory = false)
        => new(name, Path.Combine(Folder, name), 1, DateTimeOffset.UnixEpoch,
               directory ? EntryFlags.Directory : EntryFlags.None);

    /// <summary>One folder, yielding exactly the rows it was handed.</summary>
    private sealed class Canned(params FileEntry[] entries) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static List<string> Names(PaneViewModel pane)
        => pane.DetailsEntries.Select(e => e.Name).ToList();

    private async Task<PaneViewModel> Listing(params FileEntry[] rows)
    {
        var pane = Own(new PaneViewModel(new Canned(rows), null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Folder);

        // The listing lands on the dispatcher; wait on the rows themselves
        // rather than on a count of turns, under a wall-clock ceiling so a hang
        // fails instead of spinning.
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (pane.Entries.Count < rows.Length && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal(rows.Length, pane.Entries.Count);

        return pane;
    }

    // ---- what an absent key means -------------------------------------------

    /// <summary>
    /// **Both are named for their zero value, and this is the half that is easy
    /// to get wrong.** Deserialization here does not run property initializers —
    /// a key absent from settings.json arrives as default(T) — so a `= true`
    /// would be a lie for every file written before the key existed, and the
    /// first launch after an upgrade would reorder every listing or strip every
    /// extension without anybody asking. Both halves are asserted: a fresh
    /// record, and a file that has never heard of either key.
    /// </summary>
    [Fact]
    public void Folders_lead_and_extensions_show_until_somebody_says_otherwise()
    {
        Assert.False(new SettingsState().General.MixFoldersWithFiles);
        Assert.False(new SettingsState().Views.HideFileExtensions);

        var older = JsonSerializer.Deserialize(
            "{\"version\":1,\"general\":{\"naturalSorting\":true},"
            + "\"views\":{\"themeMode\":\"FollowDesktop\"}}",
            SettingsJsonContext.Default.SettingsState);

        Assert.NotNull(older);
        Assert.False(older!.General.MixFoldersWithFiles);
        Assert.False(older.Views.HideFileExtensions);
    }

    // ---- the comparer --------------------------------------------------------

    /// <summary>
    /// The whole of the first finding. Sorted by name ascending — the default —
    /// a folder named last in the alphabet still leads while the band is on,
    /// and falls into place when it is off.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_leads_the_listing_only_while_the_band_is_on()
    {
        MixFolders(false);

        var banded = await Listing(Row("zulu", directory: true), Row("alpha.txt"));

        Assert.Equal(["zulu", "alpha.txt"], Names(banded));

        MixFolders(true);

        var mixed = await Listing(Row("zulu", directory: true), Row("alpha.txt"));

        Assert.Equal(["alpha.txt", "zulu"], Names(mixed));
    }

    /// <summary>
    /// **The dialog's own description of this switch was measurably wrong**, and
    /// this is the sentence that replaced it. It said "Grouping still gives
    /// folders a band of their own", which is true of two of the four modes:
    /// <c>Grouping.CompareGroups</c> has a directory term for Size and Kind and
    /// none for Name or Modified, so with the band off a folder mixes into the
    /// date band with the files it was modified beside. The comment above
    /// <c>PaneViewModel.Compare</c> already said so in the same words the help
    /// text contradicted.
    ///
    /// Both rows carry the same timestamp, so grouping by date puts them in one
    /// band and the switch is the only thing left deciding their order.
    /// </summary>
    [AvaloniaFact]
    public async Task Only_the_size_and_type_bands_keep_folders_together()
    {
        MixFolders(true);

        var pane = await Listing(Row("zulu", directory: true), Row("alpha.txt"));

        Assert.Equal(["alpha.txt", "zulu"], Names(pane));

        pane.GroupBy = GroupMode.Kind;
        await Ordered(pane, ["zulu", "alpha.txt"]);

        pane.GroupBy = GroupMode.Size;
        await Ordered(pane, ["zulu", "alpha.txt"]);

        // And no band of its own here, so the folder falls where the switch
        // says — which is what the help text now tells the person reading it.
        pane.GroupBy = GroupMode.Modified;
        await Ordered(pane, ["alpha.txt", "zulu"]);

        pane.GroupBy = GroupMode.Name;
        await Ordered(pane, ["alpha.txt", "zulu"]);
    }

    /// <summary>
    /// Waits on the ORDER, which is what is asserted, rather than on a count of
    /// dispatcher turns — and on an order of exactly two named rows, which a
    /// half-rebuilt listing cannot pass through on its way anywhere.
    /// </summary>
    private static async Task Ordered(PaneViewModel pane, string[] expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!Names(pane).SequenceEqual(expected) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal(expected, Names(pane));
    }

    /// <summary>
    /// **A setting that reaches nothing until the next launch is the trap the
    /// font setting fell into for weeks.** The listing on screen was ordered
    /// under the old rule, so the save has to say so — which is what the
    /// refresh at the end of <c>OnSettingsChanged</c> is for.
    /// </summary>
    [AvaloniaFact]
    public async Task Saving_the_band_reorders_a_listing_already_on_screen()
    {
        MixFolders(false);

        var shell = Own(new ShellViewModel(
            new Canned(Row("zulu", directory: true), Row("alpha.txt"))));

        shell.Start(null, Folder);

        var pane = shell.ActiveTab!;
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (pane.Entries.Count < 2 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal(["zulu", "alpha.txt"], Names(pane));

        MixFolders(true);
        shell.OnSettingsChanged();

        // Waits on the ORDER, which is what is asserted, rather than on a count
        // of dispatcher turns — and the order it waits for is one the listing
        // cannot pass through on its way to somewhere else.
        deadline = DateTime.UtcNow.AddSeconds(10);

        while (Names(pane) is not ["alpha.txt", "zulu"] && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal(["alpha.txt", "zulu"], Names(pane));
    }

    // ---- the name column -----------------------------------------------------

    /// <summary>
    /// Through the converter the three layouts actually bind, not through
    /// FileKind directly: the preference has to travel Core-ward across an
    /// assembly boundary that does not allow a reference the other way, and the
    /// push in <c>AppSettings.Apply</c> is the only thing carrying it.
    /// </summary>
    private static string Drawn(FileEntry entry)
        => (string)FileConverters.DisplayName
            .Convert(entry, typeof(string), null, CultureInfo.InvariantCulture)!;

    [AvaloniaFact]
    public void The_name_a_row_draws_follows_the_preference()
    {
        var entry = Row("notes.txt");

        HideExtensions(false);
        Assert.Equal("notes.txt", Drawn(entry));

        HideExtensions(true);
        Assert.Equal("notes", Drawn(entry));

        HideExtensions(false);
        Assert.Equal("notes.txt", Drawn(entry));
    }

    /// <summary>
    /// **A launcher could go back to its reverse-DNS id.** Two arms of
    /// <c>DisplayName</c> answer for a .desktop file, and the trimming one's
    /// answer is the file name minus its suffix — "org.kde.dolphin" where the
    /// launcher says "Dolphin". Measured, hoisted above the launcher arm with
    /// "desktop" out of the started set, this drew "org.kde.dolphin"; with
    /// "desktop" in that set it drew "Dolphin" whichever order the arms sat in.
    /// So the ordering is not what is being relied on here — the started set
    /// is, and that is what this pins.
    ///
    /// The reader is a stub because what is asked about is the arms, not the
    /// freedesktop parse — this assembly's build references the Windows
    /// platform, where <c>LauncherName</c> is null.
    /// </summary>
    [AvaloniaFact]
    public void A_launcher_keeps_its_own_name_while_extensions_are_hidden()
    {
        FileKind.LauncherName = _ => "Dolphin";
        HideExtensions(true);

        Assert.Equal("Dolphin", Drawn(Row("org.kde.dolphin.desktop")));

        // And with nothing to read it stays the file name, whole: a .desktop
        // file is a thing that runs, so the trimming arm passes it by rather
        // than drawing it as "org.kde.dolphin".
        FileKind.LauncherName = null;

        Assert.Equal("org.kde.dolphin.desktop", Drawn(Row("org.kde.dolphin.desktop")));
    }

    /// <summary>
    /// **A program could be drawn as the document beside it.** With extensions
    /// hidden, report.exe and report.pdf both drew the single word "report",
    /// and the look-alike mark — the one thing here built for a difference the
    /// eye cannot catch — is deliberately blind to trimmed names, so neither
    /// row was chipped. Nothing else in the row closed the gap either: the Type
    /// column has no initialiser behind it and lives in one layout of three,
    /// and the name tooltip is gated on "Show tooltips on rows".
    ///
    /// Through the converter the layouts bind, so what is asserted is the text
    /// on screen rather than a Core answer that might not reach it.
    /// </summary>
    [AvaloniaFact]
    public void A_program_is_never_drawn_as_the_document_beside_it()
    {
        HideExtensions(true);

        Assert.Equal("report", Drawn(Row("report.pdf")));
        Assert.Equal("report.exe", Drawn(Row("report.exe")));

        Assert.NotEqual(Drawn(Row("report.pdf")), Drawn(Row("report.exe")));
    }

    /// <summary>
    /// **The look-alike mark keys on the name a row DRAWS**, which is right —
    /// two launchers can draw the same word — and would have turned every
    /// main.c beside its main.h into a pair of "Look-alike" chips the moment
    /// extensions were hidden. A folder of C sources, or of .tex beside .pdf,
    /// would light up end to end, which is noise in the one mark whose value is
    /// that it is rare. So the set asks for the name WITH its extension.
    ///
    /// The genuinely invisible pair is still marked, because that is the
    /// difference no column, icon or tooltip can show.
    /// </summary>
    [AvaloniaFact]
    public async Task Hiding_extensions_does_not_make_every_pair_a_lookalike()
    {
        HideExtensions(true);

        var pane = await Listing(
            Row("main.c"),
            Row("main.h"),
            Row("Ember Setup 0.1.0.exe"),
            Row("Ember Setup 0.1.0 .exe"));

        Assert.DoesNotContain(Path.Combine(Folder, "main.c"), pane.Confusable);
        Assert.DoesNotContain(Path.Combine(Folder, "main.h"), pane.Confusable);

        Assert.Contains(Path.Combine(Folder, "Ember Setup 0.1.0.exe"), pane.Confusable);
        Assert.Contains(Path.Combine(Folder, "Ember Setup 0.1.0 .exe"), pane.Confusable);
    }

    // ---- the dialog ----------------------------------------------------------

    /// <summary>
    /// The dialog asks the positive question and the record stores the negative
    /// one, so the inversion has to survive both directions. Seeding it wrong is
    /// the worse of the two failures: it silently rewrites a choice the moment
    /// somebody opens the dialog and presses Save.
    /// </summary>
    [AvaloniaFact]
    public void The_dialog_shows_the_positive_question_and_saves_the_negative_one()
    {
        var vm = new SettingsViewModel(new SettingsState
        {
            General = new GeneralSettings { MixFoldersWithFiles = true },
            Views = new ViewSettings { HideFileExtensions = true },
        });

        Assert.False(vm.FoldersFirst);
        Assert.False(vm.ShowFileExtensions);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result.General.MixFoldersWithFiles);
        Assert.True(vm.Result.Views.HideFileExtensions);
    }

    /// <summary>And the defaults arrive ticked, which is the state every
    /// install is already in.</summary>
    [AvaloniaFact]
    public void A_first_run_shows_both_ticked()
    {
        var vm = new SettingsViewModel(new SettingsState());

        Assert.True(vm.FoldersFirst);
        Assert.True(vm.ShowFileExtensions);

        vm.FoldersFirst = false;
        vm.ShowFileExtensions = false;
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result.General.MixFoldersWithFiles);
        Assert.True(vm.Result.Views.HideFileExtensions);
    }

    // ---- and both are reachable ----------------------------------------------

    private static XElement Box(string binding)
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        return Assert.Single(
            markup.Descendants(Xaml + "CheckBox"),
            c => (string?)c.Attribute("IsChecked") == "{Binding " + binding + "}");
    }

    /// <summary>
    /// **A preference nothing binds is a preference nobody can reach**, and
    /// both view-model properties and every test above them would pass with no
    /// control anywhere on the window. Read out of the markup for that reason,
    /// the way the Restore defaults button is.
    /// </summary>
    [Fact]
    public void The_dialog_carries_a_control_for_each()
    {
        Assert.Equal(
            "Sort folders before files",
            (string?)Box("FoldersFirst").Descendants(Xaml + "TextBlock").First().Attribute("Text"));

        Assert.Equal(
            "Show file name extensions",
            (string?)Box("ShowFileExtensions").Descendants(Xaml + "TextBlock").First().Attribute("Text"));
    }
}
