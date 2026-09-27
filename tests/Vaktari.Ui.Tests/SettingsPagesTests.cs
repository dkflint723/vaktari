using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The settings window as seven pages, each holding what its name says.
///
/// **General was four screens at the default size and Navigation held two
/// settings.** Twenty-two settings in ten sections on the first page — the
/// icon-theme catalogue, the terminal, Proton Drive and the update check among
/// them — with some four hundred words of notes always on screen, while the
/// page named for what a key does held the click choice and Backspace. The
/// window always opened on General, had no key for the next page and no
/// letters on its page names, put its footer ahead of every page in tab order,
/// and could only show a change by closing.
///
/// What is pinned here is what the maintainer approved: every setting on the
/// page its name says, one line of note at most with the rest as the control's
/// tooltip and help text, Apply, Ctrl+Tab and Ctrl+PageDown, access keys, tab
/// order, reopening where it was left, opening at a named page, the merged
/// choosers, and two columns that fall back to one at the floor — measured,
/// not assumed.
/// </summary>
public sealed class SettingsPagesTests : OwnedViewModels
{
    private static readonly XNamespace Ax = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly List<Window> _windows = [];
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly SettingsPage _pageBefore = MainWindow.LastSettingsPage;

    public override void Dispose()
    {
        // **A window that asks before it closes never closed, and stayed open
        // for the rest of the run.** Three tests here Apply through a real
        // window, which writes settings.json in this class's state directory,
        // and one of them turns on BeginInSplitView — so every later window
        // of the class restored itself split, counted two tabs, and answered
        // Close() with "2 tabs are open. Close anyway?". Nobody answers in a
        // test. MEASURED: one MainWindow and its prompt open from here to the
        // end of the suite, and every window built after it paying to repaint
        // it. The prompt is not what these tests are about, so it is not
        // asked.
        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { ConfirmClosingMultipleTabs = false },
        });

        foreach (var window in _windows) window.Close();

        AppSettings.Apply(_settingsBefore);
        MainWindow.LastSettingsPage = _pageBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static XDocument Markup() => XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

    /// <summary>The words a page's name shows, without its access-key marker.</summary>
    internal static string PageName(XElement tab) => MenuLabels.Plain((string?)tab.Attribute("Header"));

    private static List<XElement> Pages(XDocument markup) => [.. markup.Descendants(Ax + "TabItem")];

    private static XElement Page(XDocument markup, SettingsPage page) => Pages(markup)[(int)page];

    private T Shown<T>(T window) where T : Window
    {
        _windows.Add(window);
        window.Show();
        Pump();

        return window;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private (SettingsWindow Window, SettingsViewModel Model) Dialog(
        double width = 700, double height = 560, SettingsState? state = null)
    {
        var model = new SettingsViewModel(state ?? new SettingsState());
        var window = Shown(new SettingsWindow(model) { Width = width, Height = height });

        window.UpdateLayout();
        Pump();

        return (window, model);
    }

    private static void Show(SettingsWindow window, SettingsViewModel model, SettingsPage page)
    {
        model.Page = page;
        Pump();
        window.UpdateLayout();
        Pump();
    }

    // ---- the pages ---------------------------------------------------------------

    /// <summary>
    /// The strip lists the pages in <see cref="SettingsPage"/>'s order, under
    /// the names approved for them — the enum IS the strip's index, so the two
    /// drifting apart would open every caller's page one away from the one it
    /// named.
    /// </summary>
    [Fact]
    public void The_strip_lists_the_seven_pages_in_the_enum_s_order()
    {
        var names = Pages(Markup()).Select(PageName).ToList();

        Assert.Equal(
            ["General", "Appearance", "Folders and lists", "Privacy and system", "Keyboard", "Context menu", "{Binding BinPageHeader}"],
            names);

        Assert.Equal(Enum.GetValues<SettingsPage>().Length, names.Count);

        for (var i = 0; i < names.Count - 1; i++)
            Assert.Equal(((SettingsPage)i).ToString(), names[i].Replace(" ", "", StringComparison.Ordinal), ignoreCase: true);
    }

    /// <summary>Where every setting lives. The Keyboard page is its own list,
    /// and the Context menu page's one-per-flag rule is NewWindowTests'.</summary>
    private static readonly Dictionary<SettingsPage, string[]> Placed = new()
    {
        [SettingsPage.General] =
        [
            "StartupIndex", "StartupFolder", "BeginInSplitView", "ShowFilterBar", "LocationBarEditable",
            "ShowFullPathInTitleBar", "OpenWithSystem", "OpenWithSingle", "OpenWithDouble", "BackspaceGoesUp",
            "TabSwitchesSplitPanes", "ClosingSplitDiscardsOtherPane", "ConfirmMoveToTrash",
            "ConfirmPermanentDelete", "ConfirmClosingMultipleTabs",
        ],
        [SettingsPage.Appearance] =
        [
            "ColourIndex", "SelectedFont", "InterfaceTextIndex", "ShowFileExtensions", "ShowSelectionBoxes",
            "ShowVcsDecorations", "ShowStatusBar", "ShowFreeSpace", "ShowFolderTree", "DetailsPanelIndex",
            "SelectedIconTheme",
        ],
        [SettingsPage.FoldersAndLists] =
        [
            "SortOrderIndex", "FoldersFirst", "RememberViewPerFolder", "FolderSizeIndex", "AbsoluteDates",
            "ShowTooltips", "ShowPreviews", "MaxLocalPreviewMegabytes", "MaxRemotePreviewMegabytes",
            "IconSpacing", "CompactSpacing",
        ],
        [SettingsPage.PrivacyAndSystem] =
        [
            "RememberRecent", "RememberSearches", "SelectedTerminal", "ProtonDriveFolder", "CheckForUpdates",
        ],
        [SettingsPage.Bin] =
        [
            "DeleteOldTrash", "DeleteAfterDays", "LimitTrashSize", "MaxPercentOfDisk", "LimitActionWarn",
            "LimitActionOldest", "LimitActionLargest",
        ],
    };

    /// <summary>What a setting control on a page binds, read from the markup.</summary>
    private static List<string> SettingsBoundOn(XElement page)
    {
        string[] controls = ["CheckBox", "RadioButton", "ComboBox", "TextBox"];
        string[] values = ["IsChecked", "SelectedIndex", "SelectedItem", "Text"];

        return [.. page.Descendants()
            .Where(e => controls.Contains(e.Name.LocalName))
            .SelectMany(e => values.Select(v => (string?)e.Attribute(v)))
            .OfType<string>()
            .Select(v => Regex.Match(v, @"^\{Binding (\w+)\}$"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)];
    }

    /// <summary>
    /// **Every setting on the page its name says, and nothing else there.**
    /// Both directions: a setting moved off its page fails, and so does one
    /// added to a page without being placed here — a list somebody must
    /// remember to add to is a list that goes stale, so this one is checked
    /// against the markup rather than trusted.
    /// </summary>
    [Fact]
    public void Every_setting_is_on_the_page_its_name_says()
    {
        var markup = Markup();

        foreach (var (page, expected) in Placed)
        {
            var bound = SettingsBoundOn(Page(markup, page));

            Assert.True(
                bound.Order().SequenceEqual(expected.Order()),
                $"{page} binds [{string.Join(", ", bound.Order())}], expected [{string.Join(", ", expected.Order())}]");
        }
    }

    /// <summary>And the buttons that act rather than choose, beside what they act on.</summary>
    [Theory]
    [InlineData("ForgetRememberedViewsCommand", SettingsPage.FoldersAndLists)]
    [InlineData("ForgetRecentCommand", SettingsPage.PrivacyAndSystem)]
    [InlineData("ForgetSearchHistoryCommand", SettingsPage.PrivacyAndSystem)]
    [InlineData("MakeDefaultCommand", SettingsPage.PrivacyAndSystem)]
    [InlineData("BrowseForIconThemeCommand", SettingsPage.Appearance)]
    [InlineData("BrowseForStartupFolderCommand", SettingsPage.General)]
    public void Each_action_is_on_the_page_of_what_it_acts_on(string command, SettingsPage page)
        => Assert.Contains(
            Page(Markup(), page).Descendants(Ax + "Button"),
            b => (string?)b.Attribute("Command") == "{Binding " + command + "}");

    // ---- access keys ----------------------------------------------------------------

    /// <summary>
    /// **Every page answers to Alt and a letter, and no two to the same one.**
    /// The bin's name is the platform's word, so both spellings are checked
    /// against the six fixed names: "Recycle _Bin" and "_Trash".
    /// </summary>
    [Fact]
    public void Every_page_name_marks_its_own_letter()
    {
        var headers = Pages(Markup())
            .Select(t => (string?)t.Attribute("Header") ?? "")
            .Where(h => !h.StartsWith('{'))
            .ToList();

        Assert.Equal(6, headers.Count);
        Assert.All(headers, h => Assert.Single(h, '_'));

        foreach (var bin in new[] { "Recycle Bin", "Trash" })
        {
            var all = headers.Append(SettingsViewModel.BinPageHeaderFor(bin)).ToList();
            var letters = all.Select(h => char.ToLowerInvariant(h[h.IndexOf('_') + 1])).ToList();

            Assert.Equal(letters.Count, letters.Distinct().Count());
        }

        Assert.Equal("Recycle _Bin", SettingsViewModel.BinPageHeaderFor("Recycle Bin"));
        Assert.Equal("_Trash", SettingsViewModel.BinPageHeaderFor("Trash"));
    }

    /// <summary>
    /// And the letters work. **The strip's template turned access keys off** —
    /// RecognizesAccessKey="False" — so a marked name would have drawn its
    /// underscore and answered to nothing. Each page is pressed for.
    /// </summary>
    [AvaloniaFact]
    public void Alt_and_a_page_s_letter_opens_that_page()
    {
        var (window, model) = Dialog();

        var letters = new (char Letter, SettingsPage Page)[]
        {
            ('k', SettingsPage.Keyboard), ('a', SettingsPage.Appearance), ('p', SettingsPage.PrivacyAndSystem),
            ('m', SettingsPage.ContextMenu), ('f', SettingsPage.FoldersAndLists), ('g', SettingsPage.General),
            (char.ToLowerInvariant(model.BinPageHeader[model.BinPageHeader.IndexOf('_') + 1]), SettingsPage.Bin),
        };

        foreach (var (letter, page) in letters)
        {
            var key = Enum.Parse<Key>(letter.ToString(), ignoreCase: true);
            var physical = Enum.Parse<PhysicalKey>(letter.ToString(), ignoreCase: true);

            window.KeyPress(key, RawInputModifiers.Alt, physical, letter.ToString());
            window.KeyRelease(key, RawInputModifiers.Alt, physical, letter.ToString());
            Pump();

            Assert.Equal(page, model.Page);
        }
    }

    // ---- turning pages ----------------------------------------------------------------

    private static void Press(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physical)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Pump();
    }

    /// <summary>
    /// **Ctrl+Tab moved focus like Tab, and nothing turned the page.** Both
    /// pairs, both directions, wrapping at both ends — and the page's name in
    /// the strip takes the keyboard, because the page that had it is gone.
    /// </summary>
    [AvaloniaFact]
    public void Ctrl_tab_and_ctrl_page_down_turn_the_page()
    {
        var (window, model) = Dialog();

        Press(window, Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab);
        Assert.Equal(SettingsPage.Appearance, model.Page);

        Press(window, Key.PageDown, RawInputModifiers.Control, PhysicalKey.PageDown);
        Assert.Equal(SettingsPage.FoldersAndLists, model.Page);

        Press(window, Key.PageUp, RawInputModifiers.Control, PhysicalKey.PageUp);
        Assert.Equal(SettingsPage.Appearance, model.Page);

        Press(window, Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab);
        Assert.Equal(SettingsPage.General, model.Page);

        // Round the ends.
        Press(window, Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab);
        Assert.Equal(SettingsPage.Bin, model.Page);

        Press(window, Key.PageDown, RawInputModifiers.Control, PhysicalKey.PageDown);
        Assert.Equal(SettingsPage.General, model.Page);

        var focused = Assert.IsType<TabItem>(window.FocusManager?.GetFocusedElement());
        Assert.True(focused.IsSelected, "the keyboard is not on the page just turned to");
    }

    /// <summary>
    /// And not while a row on the Keyboard page is listening: then Ctrl+Tab is
    /// a key somebody may be trying to give a command, and the row has it.
    /// </summary>
    [AvaloniaFact]
    public void A_listening_row_is_given_ctrl_tab_rather_than_the_page_turning()
    {
        var (window, model) = Dialog();

        Show(window, model, SettingsPage.Keyboard);

        var row = model.Keyboard.Rows.First(r => r.Command.Id == "SortBySize");

        row.AddCommand.Execute(null);

        var asking = model.Keyboard.Status;

        Press(window, Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab);

        Assert.Equal(SettingsPage.Keyboard, model.Page);

        // The row answered it — taken, refused with its reason, or offered —
        // which is the line under the search box changing.
        Assert.NotEqual(asking, model.Keyboard.Status);
    }

    // ---- tab order ------------------------------------------------------------------

    private static bool InFooter(Avalonia.Visual control)
        => control.GetVisualAncestors().OfType<Border>().Any(b => Grid.GetRow(b) == 1 && b.Parent is Grid { Parent: SettingsWindow });

    /// <summary>
    /// **Tab reached the footer before any page.** It was the shell's first
    /// child, so from a window nobody had touched Tab went to Settings file,
    /// Restore defaults and the version first. Now: the page strip, then the
    /// page, then the footer.
    /// </summary>
    [AvaloniaFact]
    public void Tab_reaches_the_pages_before_the_footer()
    {
        var (window, _) = Dialog();

        var order = new List<Control>();

        for (var i = 0; i < 40; i++)
        {
            Press(window, Key.Tab, RawInputModifiers.None, PhysicalKey.Tab);

            if (window.FocusManager?.GetFocusedElement() is Control focused && !order.Contains(focused))
                order.Add(focused);
        }

        Assert.IsType<TabItem>(order[0]);

        var firstSetting = order.FindIndex(c => c is CheckBox or ComboBox or RadioButton or TextBox);
        var firstFooter = order.FindIndex(InFooter);

        Assert.True(firstSetting > 0, "Tab never reached a setting");
        Assert.True(firstFooter > firstSetting,
            $"the footer ({firstFooter}) came before the first setting ({firstSetting}): "
            + string.Join(" > ", order.Select(c => c.GetType().Name)));
    }

    // ---- opening at a page, and where it was left ---------------------------------------

    /// <summary>
    /// **The window always opened on General**, whatever had been in use last
    /// and whatever the caller wanted. Through the real main window: a caller
    /// names a page and it opens there; closed on another page, the next
    /// unnamed opening comes back to that one.
    /// </summary>
    [AvaloniaFact]
    public void It_opens_where_asked_and_reopens_where_it_was_left()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());

        MainWindow.LastSettingsPage = SettingsPage.General;

        main.ShowSettings(SettingsPage.Keyboard);
        Pump();

        var first = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
        var model = Assert.IsType<SettingsViewModel>(first.DataContext);

        Assert.Equal(SettingsPage.Keyboard, model.Page);
        Assert.True(model.Keyboard.IsOpen, "the Keyboard page was named but not shown");

        model.Page = SettingsPage.Appearance;
        model.CancelCommand.Execute(null);
        Pump();

        Assert.Empty(main.OwnedWindows.OfType<SettingsWindow>());

        main.ShowSettings();
        Pump();

        var second = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
        _windows.Add(second);

        Assert.Equal(SettingsPage.Appearance, Assert.IsType<SettingsViewModel>(second.DataContext).Page);
    }

    /// <summary>
    /// And the tour's line about keys is the caller that names one: pressing it
    /// closes the tour and opens Settings on the Keyboard page.
    /// </summary>
    [AvaloniaFact]
    public void The_tour_s_keys_line_opens_the_keyboard_page()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());

        MainWindow.LastSettingsPage = SettingsPage.General;

        main.Shell.ShowTourCommand.Execute(null);
        Pump();

        var tour = Assert.Single(main.OwnedWindows.OfType<TourWindow>());

        while (tour.Index < Tour.Cards.Count - 1) tour.Next();
        Pump();
        tour.UpdateLayout();

        var link = tour.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && b.DataContext is TourLine { OpensSettings: true });

        Assert.Equal(SettingsPage.Keyboard, ((TourLine)link.DataContext!).OpensSettingsAt);

        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        Assert.Empty(main.OwnedWindows.OfType<TourWindow>());

        var settings = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
        _windows.Add(settings);

        Assert.Equal(SettingsPage.Keyboard, Assert.IsType<SettingsViewModel>(settings.DataContext).Page);
    }

    // ---- Apply ----------------------------------------------------------------------

    /// <summary>
    /// **A change could only be seen by closing the dialog.** Apply hands over
    /// what Save would, and leaves the dialog open: no close request, and not
    /// Saved — the window's Closed handler must not commit it a second time.
    /// </summary>
    [AvaloniaFact]
    public void Apply_hands_over_the_edited_copy_without_closing()
    {
        var model = new SettingsViewModel(new SettingsState());
        SettingsState? applied = null;
        var closed = false;

        model.ApplyRequested += (_, state) => applied = state;
        model.CloseRequested += (_, _) => closed = true;

        model.ShowStatusBar = false;
        model.ApplyCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.False(applied!.General.ShowStatusBar);
        Assert.False(closed);
        Assert.False(model.Saved);
        Assert.Equal(1, model.AppliedCount);

        // And says what Cancel means now, where Cancel is.
        Assert.Contains("Cancel", model.SettingsFileStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// An armed Forget is carried out by Apply — the handler sees it armed —
    /// and then disarmed and its row withdrawn, so a second Apply does not
    /// offer to empty a list that is already empty.
    /// </summary>
    [AvaloniaFact]
    public void Apply_carries_out_an_armed_forget_once()
    {
        var model = new SettingsViewModel(new SettingsState(), recents: new Recents(3));
        var armedWhenApplied = false;

        model.ApplyRequested += (_, _) => armedWhenApplied = model.ForgetRecentOnSave;

        model.ForgetRecentCommand.Execute(null);
        Assert.True(model.HasRecent);

        model.ApplyCommand.Execute(null);

        Assert.True(armedWhenApplied);
        Assert.False(model.ForgetRecentOnSave);
        Assert.False(model.HasRecent);
        Assert.False(model.ForgetRecentCommand.CanExecute(null));
    }

    private sealed class Recents(int count) : Vaktari.Core.FileSystem.IRecentStore
    {
        public int Count => count;
        public void Record(string path, Vaktari.Core.FileSystem.RecentKind kind) { }
        public IReadOnlyList<Vaktari.Core.FileSystem.RecentEntry> Recent(Vaktari.Core.FileSystem.RecentKind kind, int count) => [];
        public void Forget(string path) { }
        public int ForgetAll() => 0;
        public event EventHandler? Changed { add { } remove { } }
    }

    /// <summary>
    /// **Through the real window: applied without closing, and Cancel keeps
    /// it.** Cancel after an Apply drops only what changed since — the
    /// property-sheet contract — so the applied value is still the live one
    /// after the dialog is gone, while a change made after the Apply is not.
    /// </summary>
    [AvaloniaFact]
    public void Apply_lands_now_and_cancel_afterwards_keeps_it()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());
        var before = AppSettings.Current.General.ShowStatusBar;
        var tooltipsBefore = AppSettings.Current.General.ShowTooltips;

        main.ShowSettings(SettingsPage.Appearance);
        Pump();

        var dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
        var model = Assert.IsType<SettingsViewModel>(dialog.DataContext);

        model.ShowStatusBar = !before;
        model.ApplyCommand.Execute(null);
        Pump();

        Assert.Equal(!before, AppSettings.Current.General.ShowStatusBar);
        Assert.Equal(!before, main.Shell.ShowStatusBar);
        Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());

        model.ShowTooltips = !tooltipsBefore;
        model.CancelCommand.Execute(null);
        Pump();

        Assert.Empty(main.OwnedWindows.OfType<SettingsWindow>());
        Assert.Equal(!before, AppSettings.Current.General.ShowStatusBar);
        Assert.Equal(tooltipsBefore, AppSettings.Current.General.ShowTooltips);
    }

    // ---- help: one line on the page, the rest on the control --------------------------

    private static IEnumerable<XElement> SettingsPagesMarkup(XDocument markup)
        => Pages(markup).Where((_, i) => (SettingsPage)i != SettingsPage.Keyboard);

    /// <summary>
    /// **No check box or radio carries its explanation as a second line any
    /// more.** That shape — a StackPanel of label and paragraph as the
    /// control's content — was how four hundred words came to be always on
    /// screen. A label is the Content now, and the paragraph is the control's
    /// help.
    /// </summary>
    [Fact]
    public void Every_choice_is_a_one_line_label()
    {
        var nested = SettingsPagesMarkup(Markup())
            .SelectMany(p => p.Descendants())
            .Where(e => e.Name.LocalName is "CheckBox" or "RadioButton")
            .Where(e => e.Attribute("Content") is null || e.Elements().Any())
            .Select(e => (string?)e.Attribute("IsChecked"))
            .ToList();

        Assert.True(nested.Count == 0, "still carrying a paragraph: " + string.Join(", ", nested));
    }

    /// <summary>
    /// A note is one sentence of one line, at most. Ninety characters is what
    /// a note's size holds across the page at the default width.
    /// </summary>
    [Fact]
    public void Every_note_is_one_short_sentence()
    {
        var notes = Markup().Descendants(Ax + "TextBlock")
            .Where(t => ((string?)t.Attribute("Classes"))?.Split(' ').Contains("note") == true)
            .Select(t => (string?)t.Attribute("Text") ?? "")
            .Where(t => !t.StartsWith('{'))
            .ToList();

        // Four written in the markup; the fetch line is bound, because it
        // differs per platform, and is held to the same rule below.
        Assert.True(notes.Count >= 4, $"only {notes.Count} notes found");

        notes.Add(SettingsViewModel.FetchNote);

        foreach (var note in notes)
        {
            Assert.True(note.Length <= 90, $"{note.Length} characters: {note}");
            Assert.DoesNotContain(". ", note.TrimEnd('.'), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// **The notes that were false are gone**, and so is the one only its
    /// author could read.
    /// </summary>
    [Theory]
    [InlineData("Changes to how it opens apply on the next launch")]
    [InlineData("Takes effect immediately")]
    [InlineData("Forgetting these is the thing this replaces")]
    [InlineData(@"D:\Proton-Drive")]
    public void A_false_note_is_not_on_the_page(string words)
    {
        // What a person reads or hears — attribute values — rather than the
        // file, whose comments record these notes as the defects they were.
        var shown = Markup().Descendants().SelectMany(e => e.Attributes()).Select(a => a.Value).ToList();

        Assert.True(shown.Count > 100, "the walk is not reaching the markup");
        Assert.DoesNotContain(shown, value => value.Contains(words, StringComparison.Ordinal));
    }

    /// <summary>
    /// And the title-bar box, which was under the "next launch" note while it
    /// lands at once, says what it does.
    /// </summary>
    [Fact]
    public void The_title_bar_box_says_it_changes_at_once()
    {
        var box = Page(Markup(), SettingsPage.General).Descendants(Ax + "CheckBox")
            .Single(c => (string?)c.Attribute("IsChecked") == "{Binding ShowFullPathInTitleBar}");

        Assert.Contains("as soon as you apply or save", (string?)box.Attribute("AutomationProperties.HelpText") ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **The Proton Drive box had no label and a D: drive for an example on
    /// Linux.** A label beside it now, and an example built from this
    /// machine's own profile folder.
    /// </summary>
    [Fact]
    public void The_proton_drive_box_is_labelled_and_suggests_a_path_here()
    {
        var box = Page(Markup(), SettingsPage.PrivacyAndSystem).Descendants(Ax + "TextBox")
            .Single(t => (string?)t.Attribute("Text") == "{Binding ProtonDriveFolder}");

        Assert.Equal("{Binding ProtonDriveHint}", (string?)box.Attribute("PlaceholderText"));
        Assert.Contains(box.Parent!.Elements(Ax + "TextBlock"), t => (string?)t.Attribute("Text") == "Proton Drive folder");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("e.g. " + Path.Combine(home, "Proton Drive"), SettingsViewModel.ProtonDriveHint);
    }

    /// <summary>
    /// **Every help text is the control's tooltip too — on screen, not only in
    /// the markup.** One style reads AutomationProperties.HelpText into
    /// ToolTip.Tip; a style that stopped matching would leave screen readers
    /// with the text and the mouse with nothing, and no markup test could see
    /// that. So every page is shown and every control asked.
    /// </summary>
    [AvaloniaFact]
    public void Every_help_text_is_also_the_tooltip()
    {
        var (window, model) = Dialog();
        var helped = 0;

        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            Show(window, model, page);

            foreach (var control in window.GetVisualDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && c is not TabItem))
            {
                if (AutomationProperties.GetHelpText(control) is not { Length: > 0 } help) continue;

                helped++;
                Assert.Equal(help, ToolTip.GetTip(control) as string);
            }
        }

        // Counted today at thirty-odd across the pages; written under that so
        // a new setting is not a failure, and over what a broken walk would see.
        Assert.True(helped >= 25, $"only {helped} controls carry help");
    }

    // ---- two columns, one at the floor ----------------------------------------------------

    /// <summary>
    /// **Measured, both widths.** At the default size every short group is two
    /// columns; at the 520px floor every one of them is one — and at both,
    /// each check box sits inside its column with its words inside the box,
    /// and no two boxes on a row overlap.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(700, 560, 2)]
    [InlineData(520, 450, 1)]
    public void Short_groups_are_two_columns_that_fall_back_to_one(double width, double height, int columns)
    {
        var (window, model) = Dialog(width, height);
        var groups = 0;

        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            Show(window, model, page);

            foreach (var panel in window.GetVisualDescendants().OfType<SettingsColumns>().Where(p => p.IsEffectivelyVisible))
            {
                groups++;

                Assert.Equal(columns, panel.Columns);

                var boxes = panel.Children.OfType<CheckBox>().Where(c => c.IsVisible).ToList();

                foreach (var box in boxes)
                {
                    Assert.True(box.Bounds.Right <= panel.Bounds.Width + 0.5,
                                $"{box.Content} ends {box.Bounds.Right - panel.Bounds.Width:0}px past its panel");

                    foreach (var text in box.GetVisualDescendants().OfType<TextBlock>())
                        Assert.True(text.TextLayout.Width <= text.Bounds.Width + 0.5,
                                    $"{box.Content}: {text.TextLayout.Width:0}px of words in {text.Bounds.Width:0}px");
                }

                foreach (var a in boxes)
                    foreach (var b in boxes.Where(b => !ReferenceEquals(a, b)))
                        Assert.False(a.Bounds.Intersects(b.Bounds), $"{a.Content} overlaps {b.Content}");
            }
        }

        // General (2), Appearance (2), Folders and lists, Context menu.
        Assert.True(groups >= 6, $"only {groups} column groups were shown");
    }

    /// <summary>
    /// **General was 2,040px tall at the default size — four screens.** It is
    /// under a screen and a half now (718px in 513, measured), which is what
    /// the changelog says and what this pins.
    /// </summary>
    [AvaloniaFact]
    public void The_first_page_is_under_a_screen_and_a_half_at_the_default_size()
    {
        var (window, _) = Dialog();

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);

        Assert.True(scroller.Extent.Height < 1.5 * scroller.Viewport.Height,
                    $"General is {scroller.Extent.Height:0}px in a {scroller.Viewport.Height:0}px window");
    }

    // ---- review fixes ---------------------------------------------------------------

    /// <summary>
    /// **A stored forced lightness beside the desktop's colours drew dark text
    /// on dark surfaces**, and the Colour chooser showed that pairing as "the
    /// desktop's own colours". ThemeApplier took light or dark from ThemeMode
    /// whatever the flag said, while the flag brought the desktop's own
    /// backgrounds. With the flag on, the desktop decides — the control case,
    /// flag off, still honours the forced lightness.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_desktop_s_colours_bring_the_desktop_s_lightness(bool desktopColours, bool expectDark)
    {
        AppSettings.Apply(new SettingsState
        {
            Views = new ViewSettings { FollowDesktopColours = desktopColours, ThemeMode = ThemeMode.Light },
        });

        var window = Shown(new Window());

        ThemeApplier.Apply(window, new Vaktari.Core.ThemePalette
        {
            IsDark = true,
            Colours = new Dictionary<string, string>(),
        });

        Assert.Equal(
            expectDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            Avalonia.Application.Current!.RequestedThemeVariant);
    }

    /// <summary>
    /// **Apply said "Applied" over the reason nothing is written.** With a
    /// settings.json from a newer Vaktari the store writes nothing; the
    /// footer had said so, Apply replaced it with success, and its tooltip
    /// promised a save. Both now say the change lasts until Vaktari closes.
    /// </summary>
    [AvaloniaFact]
    public void Apply_over_a_file_it_may_not_write_says_so()
    {
        const string why = "settings.json was written by a newer Vaktari";

        var model = new SettingsViewModel(new SettingsState(), settingsFileNote: why);

        Assert.Contains("not saved", model.ApplyTip, StringComparison.Ordinal);

        model.ShowStatusBar = false;
        model.ApplyCommand.Execute(null);

        // Read from what the footer shows, which carries the reason whatever
        // the latest status is (see the test below).
        Assert.Contains("not saved", model.ShownStatus, StringComparison.Ordinal);
        Assert.Contains(why, model.ShownStatus, StringComparison.Ordinal);

        // And an ordinary file still hears that it was saved.
        Assert.StartsWith("Saves", new SettingsViewModel(new SettingsState()).ApplyTip, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A screen reader heard every page as "Avalonia.Controls.ScrollViewer"
    /// with no access key** — measured on the pages' automation peers, which
    /// fall back to the content's ToString. Each page's peer now gives its
    /// words and its Alt letter, the bin's from the platform's word.
    /// </summary>
    [AvaloniaFact]
    public void Each_page_is_announced_by_name_with_its_key()
    {
        var (window, model) = Dialog();

        var pages = window.GetVisualDescendants().OfType<TabItem>().ToList();

        Assert.Equal(7, pages.Count);

        foreach (var tab in pages)
        {
            var header = (tab.Header as string)!;
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(tab);

            Assert.Equal(MenuLabels.Plain(header), peer.GetName());
            Assert.Equal("Alt+" + char.ToUpperInvariant(header[header.IndexOf('_') + 1]), peer.GetAccessKey());
        }

        Assert.Equal(model.BinTitle, Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(pages[^1]).GetName());
    }

    /// <summary>
    /// **Apply and then an untouched Save committed twice**, and a commit
    /// relists every loaded pane in every window. Nothing new since the last
    /// Apply is nothing to commit — for a second Apply as for the Save —
    /// unless a Forget has been armed since.
    /// </summary>
    [AvaloniaFact]
    public void Nothing_changed_since_an_apply_is_not_committed_again()
    {
        var model = new SettingsViewModel(new SettingsState(), recents: new Recents(2));
        var applied = 0;

        model.ApplyRequested += (_, _) => applied++;

        model.ShowStatusBar = false;
        model.ApplyCommand.Execute(null);
        model.ApplyCommand.Execute(null);

        Assert.Equal(1, applied);
        Assert.Equal("Nothing has changed since the last Apply.", model.SettingsFileStatus);

        model.SaveCommand.Execute(null);
        Assert.False(model.CommitNeeded);

        // A Forget armed after the Apply is something to commit.
        var armed = new SettingsViewModel(new SettingsState(), recents: new Recents(2));

        armed.ApplyCommand.Execute(null);
        armed.ForgetRecentCommand.Execute(null);
        armed.SaveCommand.Execute(null);
        Assert.True(armed.CommitNeeded);

        // And so is a change; and a Save with no Apply before it always is.
        var changed = new SettingsViewModel(new SettingsState());

        changed.ApplyCommand.Execute(null);
        changed.ShowTooltips = false;
        changed.SaveCommand.Execute(null);
        Assert.True(changed.CommitNeeded);

        var plain = new SettingsViewModel(new SettingsState());

        plain.SaveCommand.Execute(null);
        Assert.True(plain.CommitNeeded);
    }

    /// <summary>
    /// And through the real window: the live settings change once for the
    /// Apply and not again for the Save that follows it unchanged.
    /// </summary>
    [AvaloniaFact]
    public void Through_the_window_an_untouched_save_after_apply_commits_nothing()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());

        main.ShowSettings(SettingsPage.General);
        Pump();

        var model = Assert.IsType<SettingsViewModel>(
            Assert.Single(main.OwnedWindows.OfType<SettingsWindow>()).DataContext);

        var commits = 0;
        void Count(object? sender, EventArgs e) => commits++;

        AppSettings.Changed += Count;

        try
        {
            model.ConfirmClosingMultipleTabs = !model.ConfirmClosingMultipleTabs;
            model.ApplyCommand.Execute(null);
            Pump();

            Assert.Equal(1, commits);

            model.SaveCommand.Execute(null);
            Pump();

            Assert.Equal(1, commits);
        }
        finally
        {
            AppSettings.Changed -= Count;
        }
    }

    /// <summary>
    /// **What the dialog does not show was written back as it stood when the
    /// dialog opened**, undoing a column dragged or a default view set in
    /// another window meanwhile. It is taken from the live settings now — and
    /// after Restore defaults, from the defaults, which is what that button
    /// says.
    /// </summary>
    [AvaloniaFact]
    public void What_the_dialog_does_not_show_comes_from_the_live_settings()
    {
        var live = new SettingsState
        {
            Views = new ViewSettings
            {
                DefaultView = Vaktari.Core.Session.ViewMode.Grid,
                Details = new DetailsViewSettings { TypeColumn = 321 },
            },
        };

        var model = new SettingsViewModel(new SettingsState()) { Live = () => live };

        model.SaveCommand.Execute(null);

        Assert.Equal(Vaktari.Core.Session.ViewMode.Grid, model.Result.Views.DefaultView);
        Assert.Equal(321, model.Result.Views.Details.TypeColumn);

        var restored = new SettingsViewModel(new SettingsState()) { Live = () => live };

        restored.RestoreDefaultsCommand.Execute(null);
        restored.SaveCommand.Execute(null);

        Assert.Equal(default, restored.Result.Views.DefaultView);
        Assert.Equal(0, restored.Result.Views.Details.TypeColumn);
    }

    /// <summary>And the real window hands the live settings over: a width
    /// changed after the dialog opened survives an Apply.</summary>
    [AvaloniaFact]
    public void Through_the_window_a_change_made_elsewhere_survives_apply()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());

        main.ShowSettings(SettingsPage.General);
        Pump();

        var model = Assert.IsType<SettingsViewModel>(
            Assert.Single(main.OwnedWindows.OfType<SettingsWindow>()).DataContext);

        var now = AppSettings.Current;

        AppSettings.Apply(now with
        {
            Views = now.Views with { Details = now.Views.Details with { TypeColumn = 321 } },
        });

        model.ShowTooltips = !model.ShowTooltips;
        model.ApplyCommand.Execute(null);
        Pump();

        Assert.Equal(321, AppSettings.Current.Views.Details.TypeColumn);
    }

    /// <summary>
    /// **The help of a greyed-out control could not be reached**, and none of
    /// it from the keyboard. A disabled control shows its tooltip, and one
    /// reached with Tab opens it — and closes it when the keyboard moves on.
    /// </summary>
    [AvaloniaFact]
    public void Help_reaches_disabled_controls_and_the_keyboard()
    {
        var (window, model) = Dialog();

        model.ShowPreviews = false;
        Show(window, model, SettingsPage.FoldersAndLists);

        var limit = window.GetVisualDescendants().OfType<TextBox>()
            .Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b)!.StartsWith("Skip network", StringComparison.Ordinal));

        Assert.False(limit.IsEffectivelyEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(limit));

        var boxes = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.IsEffectivelyVisible && AutomationProperties.GetHelpText(c) is { Length: > 0 })
            .Take(2).ToList();

        boxes[0].Focus(NavigationMethod.Tab);
        Pump();
        Assert.True(ToolTip.GetIsOpen(boxes[0]), "Tab onto a control with help did not show it");

        boxes[1].Focus(NavigationMethod.Tab);
        Pump();
        Assert.False(ToolTip.GetIsOpen(boxes[0]), "the help stayed open after the keyboard moved on");
        Assert.True(ToolTip.GetIsOpen(boxes[1]));

        ToolTip.SetIsOpen(boxes[1], false);
    }

    /// <summary>
    /// **Three places said Windows' or the bundled set's words on Linux**: the
    /// line above the fetchable themes, the message when a chosen theme has
    /// gone, and the icons line of the diagnostics. Each follows the platform.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("windows", true, "Vaktari's own icons", "bundled")]
    [InlineData("linux", false, "your desktop's icon theme", "desktop theme")]
    public void Icon_words_follow_the_platform(string platform, bool namesWindows, string noTheme, string diagnostics)
    {
        var previousBin = Core.Naming.BinName;
        var previousPlatform = Core.Naming.Platform;

        try
        {
            Core.Naming.Adopt(previousBin, platform);

            Assert.Equal(namesWindows, SettingsViewModel.FetchNote.Contains("Windows", StringComparison.Ordinal));

            var gone = Path.Combine(Path.GetTempPath(), "vaktari-gone-theme-" + Guid.NewGuid().ToString("N")[..8]);
            var model = Icons(useDesktop: false, gone, perFile: false);

            Assert.EndsWith(noTheme + ".", model.IconThemeProblem, StringComparison.Ordinal);

            Assert.Equal(diagnostics, Icons(useDesktop: false, "", perFile: false).IconsForDiagnostics);
        }
        finally
        {
            Core.Naming.Adopt(previousBin, previousPlatform);
        }
    }

    /// <summary>
    /// **A browsed-to theme's full path showed nowhere** once the chooser's
    /// tooltip became its help. It is a line under the chooser while a theme
    /// is in use, and each theme row's own tooltip.
    /// </summary>
    [Fact]
    public void The_theme_in_use_shows_its_whole_path()
    {
        var page = Page(Markup(), SettingsPage.Appearance);

        var line = page.Descendants(Ax + "SelectableTextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding IconThemeFolder}");

        Assert.Equal("{Binding HasIconTheme}", (string?)line.Attribute("IsVisible"));

        Assert.Contains(page.Descendants(Ax + "TextBlock"),
                        t => (string?)t.Attribute("Text") == "{Binding Label}"
                             && (string?)t.Attribute("ToolTip.Tip") == "{Binding Path}");

        Assert.Null(new SettingsViewModel.IconThemeChoice("x", "").Path);
        Assert.Equal("/t", new SettingsViewModel.IconThemeChoice("x", "/t").Path);
    }

    /// <summary>
    /// **Two columns stayed two at 200%**, because the minimum column was a
    /// fixed 200px while the labels doubled, and every label wrapped onto
    /// two or three lines. The minimum grows with the text: at 150% and 200%
    /// the default window is one column throughout.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(18)]
    [InlineData(24)]
    public void Larger_text_falls_back_to_one_column(double small)
    {
        var model = new SettingsViewModel(new SettingsState());
        var window = new SettingsWindow(model) { Width = 700, Height = 560 };

        window.Resources["FontSizeSmall"] = small;
        Shown(window);

        var groups = 0;

        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            Show(window, model, page);

            foreach (var panel in window.GetVisualDescendants().OfType<SettingsColumns>().Where(p => p.IsEffectivelyVisible))
            {
                groups++;
                Assert.Equal(1, panel.Columns);
            }
        }

        Assert.True(groups >= 6, $"only {groups} column groups were shown");
    }

    // ---- verification fixes -----------------------------------------------------------

    /// <summary>
    /// **A new window ignored the startup choices Apply had just made.** The
    /// constructor read the launch snapshot WindowServices took, which nothing
    /// updated, so Ctrl+N after applying split view, the filter bar and the
    /// full-path title opened unsplit, with no filter bar and a short title —
    /// while the dialog said they apply to new windows. Through a real second
    /// window, after a real Apply.
    /// </summary>
    [AvaloniaFact]
    public void A_window_opened_after_apply_starts_the_way_it_says()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());

        main.ShowSettings(SettingsPage.General);
        Pump();

        var dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
        var model = Assert.IsType<SettingsViewModel>(dialog.DataContext);

        model.BeginInSplitView = true;
        model.ShowFilterBar = true;
        model.ShowFullPathInTitleBar = true;
        model.ApplyCommand.Execute(null);
        model.CancelCommand.Execute(null);
        Pump();

        main.Shell.NewWindowCommand.Execute(null);
        Pump();

        var peer = main.Services.Windows.Single(w => !ReferenceEquals(w, main));
        _windows.Add(peer);

        Assert.True(peer.Shell.IsSplit, "the new window is not split");
        Assert.True(peer.Shell.ActiveTab!.IsFilterVisible, "the new window has no filter bar");
        Assert.Contains(Path.DirectorySeparatorChar.ToString(), peer.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Help opened from the keyboard was drawn at the pointer**, wherever
    /// the mouse was parked — measured at (5,25) for a box at (196,481). It
    /// is placed under the control it explains, and hover goes back to the
    /// pointer once the keyboard moves on.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_opened_help_sits_under_its_control()
    {
        var (window, model) = Dialog();

        window.MouseMove(new Avalonia.Point(5, 5));
        Pump();

        var boxes = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.IsEffectivelyVisible && AutomationProperties.GetHelpText(c) is { Length: > 0 })
            .ToList();

        var box = boxes[^1];

        box.Focus(NavigationMethod.Tab);
        Pump();

        Assert.True(ToolTip.GetIsOpen(box));
        Assert.Equal(PlacementMode.Bottom, ToolTip.GetPlacement(box));

        boxes[0].Focus(NavigationMethod.Tab);
        Pump();

        Assert.Equal(PlacementMode.Pointer, ToolTip.GetPlacement(box));

        ToolTip.SetIsOpen(boxes[0], false);
    }

    /// <summary>
    /// **A second, unchanged Apply over a read-only file said only "Nothing
    /// has changed"** — the reason nothing is saved was gone again. The
    /// footer shows the reason with whatever it says, a Forget included.
    /// </summary>
    [AvaloniaFact]
    public void The_reason_nothing_is_saved_stays_with_every_status()
    {
        const string why = "settings.json was written by a newer Vaktari";

        var model = new SettingsViewModel(new SettingsState(), recents: new Recents(2), settingsFileNote: why);

        Assert.Equal(why, model.ShownStatus);

        model.ShowStatusBar = false;
        model.ApplyCommand.Execute(null);
        model.ApplyCommand.Execute(null);

        Assert.StartsWith("Nothing has changed", model.ShownStatus, StringComparison.Ordinal);
        Assert.Contains(why, model.ShownStatus, StringComparison.Ordinal);

        model.ForgetRecentCommand.Execute(null);
        Assert.Contains(why, model.ShownStatus, StringComparison.Ordinal);

        // And the footer shows this, not the bare status.
        Assert.Contains(Page(Markup(), SettingsPage.General).Document!.Descendants(Ax + "SelectableTextBlock"),
                        t => (string?)t.Attribute("Text") == "{Binding ShownStatus}");
    }

    /// <summary>
    /// **An untouched Save lost to another window's dialog.** This dialog
    /// applied tooltips off; another applied them on; this one's Save,
    /// compared only with its own last Apply, committed nothing and left them
    /// on while it showed off. Compared with what is live, it commits.
    /// </summary>
    [AvaloniaFact]
    public void An_untouched_save_is_measured_against_what_is_live()
    {
        var live = new SettingsState();

        var model = new SettingsViewModel(new SettingsState()) { Live = () => live };

        model.ApplyRequested += (_, state) => live = state;

        model.ShowTooltips = false;
        model.ApplyCommand.Execute(null);

        // Another window's dialog puts them back on.
        live = live with { General = live.General with { ShowTooltips = true } };

        model.SaveCommand.Execute(null);

        Assert.True(model.CommitNeeded, "the Save thought the live settings still matched");
        Assert.False(model.Result.General.ShowTooltips);
    }

    // ---- the merged choosers, both ways ------------------------------------------------

    /// <summary>
    /// **One Colour chooser, on the two keys it replaced.** File to row:
    /// Vaktari's colours in each lightness are rows 0–2, and the desktop's own
    /// colours are row 3 whatever lightness the file pairs them with.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ThemeMode.FollowDesktop, false, 0)]
    [InlineData(ThemeMode.Light, false, 1)]
    [InlineData(ThemeMode.Dark, false, 2)]
    [InlineData(ThemeMode.FollowDesktop, true, 3)]
    [InlineData(ThemeMode.Light, true, 3)]
    [InlineData(ThemeMode.Dark, true, 3)]
    public void The_colour_row_follows_both_keys(ThemeMode mode, bool desktop, int row)
        => Assert.Equal(row, new SettingsViewModel(new SettingsState
        {
            Views = new ViewSettings { ThemeMode = mode, FollowDesktopColours = desktop },
        }).ColourIndex);

    /// <summary>
    /// Row to file. Row 3 brings the desktop's lightness with its colours —
    /// the combination "light, with a dark desktop's backgrounds" is the one
    /// the merge exists to stop offering.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0, ThemeMode.FollowDesktop, false)]
    [InlineData(1, ThemeMode.Light, false)]
    [InlineData(2, ThemeMode.Dark, false)]
    [InlineData(3, ThemeMode.FollowDesktop, true)]
    public void The_colour_row_is_saved_as_both_keys(int row, ThemeMode mode, bool desktop)
    {
        // From a state that is none of the four, so every row is a change.
        var model = new SettingsViewModel(new SettingsState
        {
            Views = new ViewSettings { ThemeMode = row == 2 ? ThemeMode.Light : ThemeMode.Dark },
        })
        { ColourIndex = row };

        model.SaveCommand.Execute(null);

        Assert.Equal(mode, model.Result.Views.ThemeMode);
        Assert.Equal(desktop, model.Result.Views.FollowDesktopColours);
    }

    [AvaloniaTheory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 2)]
    public void The_sort_row_follows_both_keys(bool natural, bool caseSensitive, int row)
        => Assert.Equal(row, new SettingsViewModel(new SettingsState
        {
            General = new GeneralSettings { NaturalSorting = natural, CaseSensitiveSorting = caseSensitive },
        }).SortOrderIndex);

    /// <summary>
    /// **"Case sensitive" is no longer a box that would not tick.** Row to
    /// file, from a state with case sensitivity on: the natural row leaves it
    /// as the file had it, because natural order never reads it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0, true, true)]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    public void The_sort_row_is_saved_as_both_keys(int row, bool natural, bool caseSensitive)
    {
        var model = new SettingsViewModel(new SettingsState
        {
            General = new GeneralSettings { NaturalSorting = row != 0, CaseSensitiveSorting = true },
        })
        { SortOrderIndex = row };

        model.SaveCommand.Execute(null);

        Assert.Equal(natural, model.Result.General.NaturalSorting);
        Assert.Equal(caseSensitive, model.Result.General.CaseSensitiveSorting);
    }

    [AvaloniaTheory]
    [InlineData(NarrowPanelBehaviour.DisableToggle, false, 0)]
    [InlineData(NarrowPanelBehaviour.DisableToggle, true, 0)]
    [InlineData(NarrowPanelBehaviour.GrowWindow, false, 1)]
    [InlineData(NarrowPanelBehaviour.GrowWindow, true, 2)]
    public void The_details_panel_row_follows_both_keys(NarrowPanelBehaviour grow, bool keepWide, int row)
        => Assert.Equal(row, new SettingsViewModel(new SettingsState
        {
            Views = new ViewSettings { NarrowDetailsPanel = grow, KeepWidthAfterPanelClose = keepWide },
        }).DetailsPanelIndex);

    [AvaloniaTheory]
    [InlineData(0, NarrowPanelBehaviour.DisableToggle, false)]
    [InlineData(1, NarrowPanelBehaviour.GrowWindow, false)]
    [InlineData(2, NarrowPanelBehaviour.GrowWindow, true)]
    public void The_details_panel_row_is_saved_as_both_keys(int row, NarrowPanelBehaviour grow, bool keepWide)
    {
        // Growing unless row 0 is the change. For row 0 the file says "shrink
        // back" (keep wide false) — the first row must leave the width key as
        // it was, and a file saying "keep wide" could not tell leaving it from
        // rewriting it, which the revert-check measured: the first cut of this
        // stayed green with the guard deleted. The other rows start from the
        // opposite of what they must store.
        var model = new SettingsViewModel(new SettingsState
        {
            Views = new ViewSettings
            {
                NarrowDetailsPanel = row == 0 ? NarrowPanelBehaviour.GrowWindow : NarrowPanelBehaviour.DisableToggle,
                KeepWidthAfterPanelClose = row == 1,
            },
        })
        { DetailsPanelIndex = row };

        model.SaveCommand.Execute(null);

        Assert.Equal(grow, model.Result.Views.NarrowDetailsPanel);
        Assert.Equal(keepWide, model.Result.Views.KeepWidthAfterPanelClose);
    }

    [AvaloniaTheory]
    [InlineData(StartupLocation.RestoreSession, 0)]
    [InlineData(StartupLocation.HomeFolder, 1)]
    [InlineData(StartupLocation.Computer, 2)]
    [InlineData(StartupLocation.SpecificFolder, 3)]
    public void The_startup_row_and_the_stored_choice_agree_both_ways(StartupLocation location, int row)
    {
        Assert.Equal(row, new SettingsViewModel(new SettingsState
        {
            Startup = new StartupSettings { ShowOnStartup = location },
        }).StartupIndex);

        var model = new SettingsViewModel(new SettingsState
        {
            Startup = new StartupSettings
            {
                ShowOnStartup = location == StartupLocation.HomeFolder ? StartupLocation.Computer : StartupLocation.HomeFolder,
            },
        })
        { StartupIndex = row };

        model.SaveCommand.Execute(null);

        Assert.Equal(location, model.Result.Startup.ShowOnStartup);

        // One answer, never two: exactly the flag for this row is set.
        Assert.Equal(1, new[] { model.RestoreLastSession, model.StartInHome, model.StartInComputer, model.StartInSpecificFolder }.Count(b => b));
    }

    [AvaloniaTheory]
    [InlineData(FolderSizeMode.ItemCount, 0)]
    [InlineData(FolderSizeMode.ContentSize, 1)]
    [InlineData(FolderSizeMode.None, 2)]
    public void The_folder_size_row_and_the_stored_mode_agree_both_ways(FolderSizeMode mode, int row)
    {
        Assert.Equal(row, new SettingsViewModel(new SettingsState
        {
            Views = new ViewSettings { Details = new DetailsViewSettings { FolderSize = mode } },
        }).FolderSizeIndex);

        var model = new SettingsViewModel(new SettingsState
        {
            Views = new ViewSettings
            {
                Details = new DetailsViewSettings
                {
                    FolderSize = mode == FolderSizeMode.None ? FolderSizeMode.ContentSize : FolderSizeMode.None,
                },
            },
        })
        { FolderSizeIndex = row };

        model.SaveCommand.Execute(null);

        Assert.Equal(mode, model.Result.Views.Details.FolderSize);
    }

    // ---- the File icons chooser -----------------------------------------------------------

    private sealed class PerFileIcons : Vaktari.Core.FileSystem.IFileIconProvider
    {
        public Vaktari.Core.FileSystem.IconPixels? IconFor(string path, bool isDirectory, int size) => null;
    }

    private static SettingsViewModel Icons(bool useDesktop, string folder, bool perFile)
        => new(new SettingsState { General = new GeneralSettings { UseSystemIcons = useDesktop, IconThemeFolder = folder } },
               desktopIcons: perFile ? new PerFileIcons() : null);

    /// <summary>
    /// **File to row, by the precedence the listing applies.** A theme wins;
    /// then the desktop's own icons where the platform has them; then the row
    /// with no theme. What the chooser shows is what is drawn.
    /// </summary>
    [AvaloniaFact]
    public void The_icon_row_is_what_the_listing_draws()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vaktari-no-such-theme-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.True(Icons(useDesktop: true, "", perFile: true).SelectedIconTheme!.DesktopIcons);
        Assert.False(Icons(useDesktop: false, "", perFile: true).SelectedIconTheme!.DesktopIcons);
        Assert.Equal("", Icons(useDesktop: false, "", perFile: true).SelectedIconTheme!.Folder);

        // Asked for, but the platform cannot: the row with no theme, which is
        // what is drawn — and there is no desktop row to pick at all.
        var linux = Icons(useDesktop: true, "", perFile: false);
        Assert.False(linux.SelectedIconTheme!.DesktopIcons);
        Assert.DoesNotContain(linux.IconThemeChoices, c => c.DesktopIcons);

        // A theme outranks the tick.
        Assert.Equal(folder, Icons(useDesktop: true, folder, perFile: true).SelectedIconTheme!.Folder);
    }

    /// <summary>
    /// Row to file. The two rows with no theme each say which they are; a
    /// theme row sets the folder and leaves the tick as it was, because the
    /// theme outranks it either way.
    /// </summary>
    [AvaloniaFact]
    public void The_icon_row_is_saved_as_both_keys()
    {
        var model = Icons(useDesktop: false, "", perFile: true);

        model.SelectedIconTheme = model.IconThemeChoices.Single(c => c.DesktopIcons);
        model.SaveCommand.Execute(null);
        Assert.True(model.Result.General.UseSystemIcons);
        Assert.Equal("", model.Result.General.IconThemeFolder);

        model = Icons(useDesktop: true, "", perFile: true);
        model.SelectedIconTheme = model.IconThemeChoices.First(c => !c.DesktopIcons && c.Folder.Length == 0);
        model.SaveCommand.Execute(null);
        Assert.False(model.Result.General.UseSystemIcons);

        var folder = Path.Combine(Path.GetTempPath(), "vaktari-no-such-theme-" + Guid.NewGuid().ToString("N")[..8]);

        model = Icons(useDesktop: true, folder, perFile: true);
        model.SelectedIconTheme = model.IconThemeChoices.First(c => c.DesktopIcons);
        model.SelectedIconTheme = model.IconThemeChoices.Single(c => c.Folder == folder);
        model.SaveCommand.Execute(null);
        Assert.Equal(folder, model.Result.General.IconThemeFolder);
        Assert.True(model.Result.General.UseSystemIcons);
    }

    /// <summary>
    /// **"Vaktari's own icons" was on Linux too, where the row draws the
    /// desktop's icon theme.** The platform's icon provider answers when no
    /// theme is chosen, and on Linux that is Plasma's theme.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("windows", "Vaktari's own icons")]
    [InlineData("linux", "Your desktop's icon theme")]
    public void The_row_with_no_theme_says_what_it_draws_here(string platform, string label)
    {
        var previousBin = Core.Naming.BinName;
        var previousPlatform = Core.Naming.Platform;

        try
        {
            Core.Naming.Adopt(previousBin, platform);

            Assert.Equal(label, new SettingsViewModel(new SettingsState()).IconThemeChoices[0].Label);
        }
        finally
        {
            Core.Naming.Adopt(previousBin, previousPlatform);
        }
    }

    // ---- the review fixes, from the corners their own tests did not reach ----------------

    /// <summary>
    /// **The desktop decides only with the flag, and only when there is a
    /// desktop to ask.** The lightness fix was pinned in one direction — a
    /// forced Light under a DARK desktop — which a rule reading "dark whenever
    /// either says dark" passes too. These are the other corners: a forced
    /// Dark under a LIGHT desktop palette (a light Plasma scheme, Windows in
    /// light mode) is drawn light; with no palette at all — a Linux desktop
    /// with no kdeglobals — the stored lightness still holds rather than
    /// falling to dark; and without the flag the forced lightness wins over
    /// the desktop as it always did.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true, ThemeMode.Dark, 0, false)]
    [InlineData(true, ThemeMode.FollowDesktop, 0, false)]
    [InlineData(true, ThemeMode.Light, -1, false)]
    [InlineData(true, ThemeMode.Dark, -1, true)]
    [InlineData(false, ThemeMode.Dark, 0, true)]
    [InlineData(false, ThemeMode.FollowDesktop, 0, false)]
    [InlineData(false, ThemeMode.FollowDesktop, -1, true)]
    public void Lightness_with_and_without_a_desktop_palette(bool desktopColours, ThemeMode mode, int paletteDark, bool expectDark)
    {
        AppSettings.Apply(new SettingsState
        {
            Views = new ViewSettings { FollowDesktopColours = desktopColours, ThemeMode = mode },
        });

        var window = Shown(new Window());

        ThemeApplier.Apply(window, paletteDark < 0
            ? null
            : new Vaktari.Core.ThemePalette { IsDark = paletteDark == 1, Colours = new Dictionary<string, string>() });

        Assert.Equal(
            expectDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            Avalonia.Application.Current!.RequestedThemeVariant);
    }

    /// <summary>
    /// **A key assigned after an Apply is a change.** What an unchanged Save
    /// is compared against is the bytes the store would write, and the
    /// Keyboard page's bindings are part of them: a comparison that looked
    /// only at what the other six pages show would drop a key given to a
    /// command between the Apply and the Save.
    /// </summary>
    [AvaloniaFact]
    public void A_key_assigned_after_apply_is_committed_by_the_save()
    {
        var model = new SettingsViewModel(new SettingsState());

        model.ApplyCommand.Execute(null);

        var row = model.Keyboard.Rows.First(r => r.Command.Id == "SortBySize");

        row.AddCommand.Execute(null);
        Assert.True(model.Keyboard.Offer(Key.F7, KeyModifiers.Control | KeyModifiers.Shift));

        model.SaveCommand.Execute(null);

        Assert.True(model.CommitNeeded);
        Assert.True(model.Result.Keyboard.Bindings.ContainsKey("SortBySize"));
    }

    /// <summary>
    /// And through the real window, the half of the Apply-then-Save rule the
    /// untouched case cannot see: a change made after the Apply is committed
    /// by the Save — once for the Apply, once more for the Save.
    /// </summary>
    [AvaloniaFact]
    public void Through_the_window_a_change_after_apply_is_saved()
    {
        UseSearch(PaneViewModel.Search);

        var main = Shown(new MainWindow());

        main.ShowSettings(SettingsPage.General);
        Pump();

        var model = Assert.IsType<SettingsViewModel>(
            Assert.Single(main.OwnedWindows.OfType<SettingsWindow>()).DataContext);

        var commits = 0;
        void Count(object? sender, EventArgs e) => commits++;

        AppSettings.Changed += Count;

        try
        {
            model.ConfirmClosingMultipleTabs = !model.ConfirmClosingMultipleTabs;
            model.ApplyCommand.Execute(null);
            Pump();

            var tooltips = !model.ShowTooltips;

            model.ShowTooltips = tooltips;
            model.SaveCommand.Execute(null);
            Pump();

            Assert.Equal(2, commits);
            Assert.Equal(tooltips, AppSettings.Current.General.ShowTooltips);
        }
        finally
        {
            AppSettings.Changed -= Count;
        }
    }

    /// <summary>A state each merged chooser can show but not write back as
    /// itself, if it rewrote what it was handed.</summary>
    private static SettingsState Unusual(string name) => name switch
    {
        "desktop colours, forced light" => new() { Views = new ViewSettings { FollowDesktopColours = true, ThemeMode = ThemeMode.Light } },
        "desktop colours, forced dark" => new() { Views = new ViewSettings { FollowDesktopColours = true, ThemeMode = ThemeMode.Dark } },
        "natural, case-sensitive kept" => new() { General = new GeneralSettings { NaturalSorting = true, CaseSensitiveSorting = true } },
        "panel greyed, keep-wide kept" => new() { Views = new ViewSettings { NarrowDetailsPanel = NarrowPanelBehaviour.DisableToggle, KeepWidthAfterPanelClose = true } },
        "folder size as contents" => new() { Views = new ViewSettings { Details = new DetailsViewSettings { FolderSize = FolderSizeMode.ContentSize } } },
        "the drive listing, a folder kept" => new() { Startup = new StartupSettings { ShowOnStartup = StartupLocation.Computer, StartupFolder = Path.Combine(Path.GetTempPath(), "vaktari-kept-startup") } },
        "desktop icons asked for, none here" => new() { General = new GeneralSettings { UseSystemIcons = true } },
        "a theme that has gone" => new() { General = new GeneralSettings { IconThemeFolder = Path.Combine(Path.GetTempPath(), "vaktari-no-such-theme-kept") } },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// **An untouched Save through every page writes back what it was given.**
    /// The merged choosers each read two keys and promise not to rewrite a
    /// pairing nobody changed; their tests drive the view model, where no
    /// ComboBox ever binds. Here the real window is shown, every page is
    /// visited so every binding is live, and the saved state must be the
    /// state handed in, byte for byte.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("desktop colours, forced light")]
    [InlineData("desktop colours, forced dark")]
    [InlineData("natural, case-sensitive kept")]
    [InlineData("panel greyed, keep-wide kept")]
    [InlineData("folder size as contents")]
    [InlineData("the drive listing, a folder kept")]
    [InlineData("desktop icons asked for, none here")]
    [InlineData("a theme that has gone")]
    public void An_untouched_save_through_every_page_writes_back_what_it_was_given(string name)
    {
        var state = Unusual(name);
        var (window, model) = Dialog(state: state);

        foreach (var page in Enum.GetValues<SettingsPage>()) Show(window, model, page);

        model.SaveCommand.Execute(null);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(SettingsRepair.Complete(state), SettingsJsonContext.Default.SettingsState),
            System.Text.Json.JsonSerializer.Serialize(model.Result, SettingsJsonContext.Default.SettingsState));
    }
}
