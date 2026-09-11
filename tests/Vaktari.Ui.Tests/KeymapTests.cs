using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Input;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Keys somebody can change: the command table, the keymap laid over it, and
/// everything that answers or prints a key following it.
///
/// **Every key the window answered was written into the markup and the key
/// handler, and nobody could change one.** A key that collides with a habit —
/// Ctrl+D pins here and deletes in Explorer, Ctrl+B folds the sidebar here
/// and adds a place in Dolphin — could be explained on the F1 sheet and no
/// more. These pin the table's own promises, the rules a chosen key is held
/// to, and that a moved key really moves: in the window, on the sheet, in the
/// tour, on the labels and in the file.
///
/// Every test that changes the settings in force puts them back — the keymap
/// in force is read from them, and this assembly runs its classes in
/// sequence, so a leak lands on somebody else's assertion.
/// </summary>
public sealed class KeymapTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly List<Window> _windows = [];
    private string? _scratch;

    public override void Dispose()
    {
        foreach (var window in _windows) window.Close();

        AppSettings.Apply(_settingsBefore);

        if (_scratch is not null)
        {
            try { Directory.Delete(_scratch, recursive: true); }
            catch (Exception) { /* a temp folder left behind is not worth failing over */ }
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static KeyboardSettings Choose(params (string Id, string[] Keys)[] choices)
        => new()
        {
            Bindings = choices.ToDictionary(c => c.Id, c => c.Keys.ToList(), StringComparer.Ordinal),
        };

    /// <summary>Puts these choices in force, the way a settings save does.</summary>
    private static void Use(params (string Id, string[] Keys)[] choices)
        => AppSettings.Apply(AppSettings.Current with { Keyboard = Choose(choices) });

    private static KeyGesture G(string text) => KeyChords.Parse(text) ?? throw new ArgumentException(text);

    // ---- the table -----------------------------------------------------------------

    /// <summary>Ids are what settings.json stores, so two alike would make one
    /// command's keys land on the other; and every default has to read as a
    /// key, or it silently answers nothing.</summary>
    [Fact]
    public void Ids_are_unique_and_every_default_is_a_key()
    {
        Assert.Equal(Commands.All.Count, Commands.All.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());

        var unread = Commands.All
            .SelectMany(c => c.Defaults.Where(d => KeyChords.Parse(d) is null).Select(d => $"{c.Id}: {d}"))
            .ToList();

        Assert.True(unread.Count == 0, "these defaults are not keys: " + string.Join(", ", unread));
    }

    /// <summary>
    /// No two commands ship on one key — counted with the pad folded onto the
    /// top row, which is how Avalonia matches — and nothing about the shipped
    /// keys needs the conflict rules at all.
    /// </summary>
    [Fact]
    public void No_two_commands_ship_with_the_same_key()
    {
        var shared = Commands.All
            .SelectMany(c => c.DefaultKeys.Select(g => (Key: KeyChords.Readable(g), c.Id)))
            .GroupBy(x => x.Key)
            .Where(g => g.Select(x => x.Id).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" and ", g.Select(x => x.Id).Distinct())}")
            .ToList();

        Assert.True(shared.Count == 0, "these keys ship on two commands: " + string.Join("; ", shared));
        Assert.Empty(Keymap.Default.Dropped);
    }

    /// <summary>The shipped keys obey the same rule a chosen key does.</summary>
    [Fact]
    public void No_shipped_key_is_one_a_command_may_not_have()
    {
        var refused = Commands.All
            .SelectMany(c => c.DefaultKeys
                .Where(g => KeyChords.Refusal(g, c.Tier) is not null)
                .Select(g => $"{c.Id}: {KeyChords.Readable(g)}"))
            .ToList();

        Assert.True(refused.Count == 0, "these shipped keys break the rules: " + string.Join(", ", refused));
    }

    /// <summary>
    /// **A key that answers anywhere and that a text box edits with would take
    /// it from the address bar** — the fault every clipboard and undo key was
    /// once lifted out of the markup for. The shipped keys include none.
    /// </summary>
    [Fact]
    public void No_key_that_answers_anywhere_is_one_a_text_box_edits_with()
    {
        var editing = Commands.All
            .Where(c => c.Tier == KeyTier.Anywhere)
            .SelectMany(c => c.DefaultKeys.Where(KeyChords.IsTextEditing).Select(g => $"{c.Id}: {KeyChords.Readable(g)}"))
            .ToList();

        Assert.True(editing.Count == 0, "these answer anywhere and edit text: " + string.Join(", ", editing));
    }

    /// <summary>What each command runs, by the name of the view-model command
    /// — shell, sidebar or pane — it hands back.</summary>
    public static TheoryData<string, string> Runs => new()
    {
        { "GoBack", "Pane.GoBackCommand" },
        { "GoForward", "Pane.GoForwardCommand" },
        { "GoUp", "Pane.GoUpCommand" },
        { "GoHome", "Pane.GoHomeCommand" },
        { "Refresh", "Pane.RefreshCommand" },
        { "EditPath", "Pane.BeginEditPathCommand" },
        { "NewTab", "Shell.NewTabCommand" },
        { "NewWindow", "Shell.NewWindowCommand" },
        { "CloseTab", "Shell.CloseTabCommand" },
        { "ReopenClosedTab", "Shell.ReopenClosedTabCommand" },
        { "DuplicateTab", "Shell.DuplicateTabCommand" },
        { "Quit", "Shell.QuitCommand" },
        { "NextTab", "Shell.NextTabCommand" },
        { "PreviousTab", "Shell.PreviousTabCommand" },
        { "ToggleSplit", "Shell.ToggleSplitCommand" },
        { "ToggleInfo", "Shell.ToggleInfoCommand" },
        { "Sidebar", "Sidebar.CycleRailCommand" },
        { "Search", "Pane.BeginSearchCommand" },
        { "ToggleFilter", "Pane.ToggleFilterCommand" },
        { "Copy", "Pane.CopySelectionToClipboardCommand" },
        { "Cut", "Pane.CutSelectionToClipboardCommand" },
        { "Paste", "Pane.PasteCommand" },
        { "Undo", "Pane.UndoCommand" },
        { "Redo", "Pane.RedoCommand" },
        { "Rename", "Pane.BeginRenameCommand" },
        { "BatchRename", "Shell.BatchRenameCommand" },
        { "Trash", "Shell.TrashSelectionCommand" },
        { "NewFolder", "Pane.NewFolderCommand" },
        { "NewFile", "Pane.NewFileCommand" },
        { "Duplicate", "Pane.DuplicateSelectedCommand" },
        { "CreateShortcut", "Pane.CreateShortcutCommand" },
        { "Compress", "Pane.CompressSelectionCommand" },
        { "Extract", "Pane.ExtractSelectionCommand" },
        { "CopyLocation", "Shell.CopyLocationCommand" },
        { "Properties", "Shell.ShowPropertiesCommand" },
        { "Preview", "Pane.TogglePreviewCommand" },
        { "OpenTerminalHere", "Pane.OpenTerminalHereCommand" },
        { "PinCurrent", "Shell.PinCurrentCommand" },
        { "EmptyTrash", "Shell.EmptyTrashCommand" },
        { "ToggleView", "Pane.ToggleViewCommand" },
        { "ShowAsDetails", "Pane.ShowAsDetailsCommand" },
        { "ShowAsCompact", "Pane.ShowAsCompactCommand" },
        { "ShowAsGrid", "Pane.ShowAsGridCommand" },
        { "ToggleHidden", "Pane.ToggleHiddenCommand" },
        { "SortByName", "Pane.SortByNameCommand" },
        { "SortBySize", "Pane.SortBySizeCommand" },
        { "SortByModified", "Pane.SortByModifiedCommand" },
        { "SortByCreated", "Pane.SortByCreatedCommand" },
        { "SortByType", "Pane.SortByCommand" },
        { "GroupByNone", "Pane.GroupByNoneCommand" },
        { "GroupByName", "Pane.GroupByNameCommand" },
        { "GroupBySize", "Pane.GroupBySizeCommand" },
        { "GroupByModified", "Pane.GroupByModifiedCommand" },
        { "GroupByType", "Pane.GroupByKindCommand" },
        { "ZoomIn", "Shell.ZoomInCommand" },
        { "ZoomOut", "Shell.ZoomOutCommand" },
        { "ZoomReset", "Shell.ZoomResetCommand" },
        { "UseThisViewEverywhere", "Shell.UseThisViewEverywhereCommand" },
        { "ResetColumnWidths", "Shell.ResetColumnWidthsCommand" },
        { "ShowShortcuts", "Shell.ShowShortcutsCommand" },
        { "ShowPalette", "Shell.ShowPaletteCommand" },
        { "OpenSettings", "Shell.OpenSettingsCommand" },
        { "ShowTour", "Shell.ShowTourCommand" },
    };

    /// <summary>
    /// **A command that runs the wrong command is the table's one silent
    /// failure**: Back pointed at Forward compiles, prints the right name on
    /// the palette and the sheet, and does the other thing. Compared by
    /// reference with what the shell, its sidebar or a started pane hands
    /// back, so the lambda has to reach the very object the menus bind to.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Runs))]
    public void Every_command_runs_the_view_model_command_it_is_named_for(string id, string where)
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        var (owner, property) = (where.Split('.')[0], where.Split('.')[1]);

        object host = owner switch
        {
            "Shell" => shell,
            "Sidebar" => shell.Sidebar,
            _ => shell.ActiveTab!,
        };

        var expected = host.GetType().GetProperty(property)?.GetValue(host);
        var command = Commands.Get(id);

        Assert.NotNull(expected);
        Assert.Null(command.Window);
        Assert.Same(expected, command.Command!(shell));
    }

    /// <summary>
    /// And the table above is the whole table: a command added without a row
    /// there would be the one whose target nothing checks. The five that are
    /// the window's own are checked below instead.
    /// </summary>
    [Fact]
    public void Every_command_is_checked_by_one_of_the_two_tests()
    {
        var checkedHere = Runs.Select(row => row.Data.Item1).ToHashSet(StringComparer.Ordinal);
        var windows = new[] { "NextRegion", "DeletePermanently", "SelectAll", "SelectNone", "InvertSelection" };

        var missed = Commands.All
            .Select(c => c.Id)
            .Where(id => !checkedHere.Contains(id) && !windows.Contains(id))
            .ToList();

        Assert.True(missed.Count == 0, "no test says what these run: " + string.Join(", ", missed));
        Assert.All(windows, id => Assert.NotNull(Commands.Get(id).Window));
    }

    /// <summary>The window's own commands run the window's own methods — and
    /// Delete for good is one of them, which is what keeps its question.</summary>
    [AvaloniaFact]
    public void The_windows_own_commands_run_the_window()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        var host = new Host(shell);

        foreach (var id in new[] { "NextRegion", "DeletePermanently", "SelectAll", "SelectNone", "InvertSelection" })
            Assert.True(Commands.Get(id).Run(host));

        Assert.Equal(["NextRegion", "DeletePermanently", "SelectAll", "SelectNone", "InvertSelection"], host.Ran);
    }

    // ---- reading and printing keys -----------------------------------------------

    /// <summary>
    /// **Avalonia reads a bare digit as a number**, so "Ctrl+Shift+1" became
    /// Ctrl+Shift+Cancel — measured once already on a menu row that drew
    /// Ctrl+Shift+Tab. A number longer than one digit is not a key at all.
    /// </summary>
    [Fact]
    public void A_digit_is_read_as_the_digit_key_not_as_a_number()
    {
        Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift), G("Ctrl+Shift+1"));
        Assert.Equal(new KeyGesture(Key.D0, KeyModifiers.Control), G("Ctrl+0"));
        Assert.Null(KeyChords.Parse("Ctrl+10"));
    }

    /// <summary>The three spellings of a gesture — the file's, the sheet's and
    /// a label's — read back to one key, so a file edited by hand can say
    /// whichever somebody knows.</summary>
    [Theory]
    [InlineData("Ctrl+Shift+OemComma", "Ctrl+Shift+,")]
    [InlineData("Ctrl+OemPlus", "Ctrl++")]
    [InlineData("Ctrl+OemMinus", "ctrl+-")]
    [InlineData("Alt+Left", "Alt+←")]
    [InlineData("Alt+Left", "alt+left")]
    [InlineData("Ctrl+PageDown", "Ctrl+Page Down")]
    [InlineData("Back", "Backspace")]
    [InlineData("Apps", "Menu")]
    [InlineData("Alt+Enter", "alt+return")]
    public void Every_spelling_reads_back_to_the_same_key(string one, string other)
        => Assert.Equal(G(one), G(other));

    /// <summary>What is written to settings.json reads back as what was
    /// written, and in names a person would write rather than the enum's
    /// other names for the same keys.</summary>
    [Fact]
    public void Every_shipped_key_round_trips_through_the_file()
    {
        foreach (var gesture in Commands.All.SelectMany(c => c.DefaultKeys))
        {
            var stored = KeyChords.Store(gesture);

            Assert.Equal(gesture, KeyChords.Parse(stored));
            Assert.DoesNotContain("Return", stored, StringComparison.Ordinal);
        }

        Assert.Equal("Ctrl+PageDown", KeyChords.Store(G("Ctrl+Page Down")));
        Assert.Equal("Alt+Enter", KeyChords.Store(G("Alt+Enter")));
    }

    /// <summary>The sheet's spelling and a label's, which every printed key in
    /// the window uses.</summary>
    [Fact]
    public void Keys_print_the_way_the_sheet_and_the_labels_spell_them()
    {
        Assert.Equal("Ctrl+Shift+,", KeyChords.Readable(G("Ctrl+Shift+OemComma")));
        Assert.Equal("ctrl+shift+,", KeyChords.Hint(G("Ctrl+Shift+OemComma")));
        Assert.Equal("Alt+←", KeyChords.Readable(G("Alt+Left")));
        Assert.Equal("alt+left", KeyChords.Hint(G("Alt+Left")));
        Assert.Equal("Ctrl+Page Down", KeyChords.Readable(G("Ctrl+PageDown")));
        Assert.Equal("Ctrl+0", KeyChords.Readable(G("Ctrl+NumPad0")));
        Assert.Equal("Ctrl+Shift+1", KeyChords.Readable(G("Ctrl+Shift+D1")));
    }

    /// <summary>
    /// **Keys that type, and the keys that are every list's grammar, belong to
    /// no command.** A letter with nothing held, or with Shift, is type-ahead
    /// and every box's text; Enter, Tab, the arrows, Backspace, the menu key
    /// and Ctrl+1…9 are answered with rules a keymap cannot express. Space is
    /// the one typing key a command may have, and only one that acts on the
    /// listing. Ctrl+Alt with a letter is allowed — see the test below for
    /// why that is safe.
    /// </summary>
    [Theory]
    [InlineData("K", KeyTier.Anywhere, false)]
    [InlineData("Shift+K", KeyTier.Listing, false)]
    [InlineData("Ctrl+Alt+T", KeyTier.Anywhere, true)]
    [InlineData("Enter", KeyTier.Listing, false)]
    [InlineData("Tab", KeyTier.Anywhere, false)]
    [InlineData("Left", KeyTier.Listing, false)]
    [InlineData("Ctrl+Left", KeyTier.Anywhere, false)]
    [InlineData("Backspace", KeyTier.Listing, false)]
    [InlineData("Ctrl+3", KeyTier.Anywhere, false)]
    [InlineData("Menu", KeyTier.Listing, false)]
    [InlineData("Shift+F10", KeyTier.Anywhere, false)]
    [InlineData("Alt+F4", KeyTier.Anywhere, false)]
    [InlineData("Space", KeyTier.Anywhere, false)]
    [InlineData("Space", KeyTier.Guarded, false)]
    [InlineData("Space", KeyTier.Listing, true)]
    [InlineData("Ctrl+K", KeyTier.Anywhere, true)]
    [InlineData("Alt+K", KeyTier.Listing, true)]
    [InlineData("F12", KeyTier.Guarded, true)]
    [InlineData("Ctrl+Enter", KeyTier.Listing, true)]
    [InlineData("Alt+Left", KeyTier.Anywhere, true)]
    [InlineData("Ctrl+Tab", KeyTier.Anywhere, true)]
    public void Keys_that_type_and_the_grammar_are_refused(string key, KeyTier tier, bool allowed)
        => Assert.Equal(allowed, KeyChords.Refusal(G(key), tier) is null);

    /// <summary>
    /// **Ctrl+Alt with a letter is AltGr on many keyboards** — how € and @ are
    /// typed — and it is still a key a command may have, because a box that
    /// has the keyboard is handed it back: it counts as a key a box edits
    /// with, so the character arrives in the address bar and the rename box,
    /// and the command runs only from the listing. Refusing it would cost the
    /// Ctrl+Alt+T habit to guard against a collision this already answers.
    /// </summary>
    [Fact]
    public void An_altgr_key_is_allowed_and_left_to_a_box()
    {
        var altGr = G("Ctrl+Alt+E");

        Assert.Null(KeyChords.Refusal(altGr, KeyTier.Anywhere));
        Assert.True(KeyChords.IsTextEditing(altGr));

        // And a plain Ctrl letter is neither typing nor editing.
        Assert.False(KeyChords.IsTextEditing(G("Ctrl+K")));
    }

    // ---- laying choices over the defaults ------------------------------------------

    [Fact]
    public void A_chosen_key_replaces_the_defaults()
    {
        var keys = Keymap.From(Choose(("NewTab", ["Ctrl+K"])));

        Assert.Equal([G("Ctrl+K")], keys.KeysOf("NewTab"));
        Assert.Null(keys.Owner(G("Ctrl+T")));
        Assert.Equal("NewTab", keys.Owner(G("Ctrl+K"))?.Id);
    }

    [Fact]
    public void An_empty_list_is_a_command_with_no_key()
    {
        var keys = Keymap.From(Choose(("CloseTab", [])));

        Assert.Empty(keys.KeysOf("CloseTab"));
        Assert.Null(keys.Owner(G("Ctrl+W")));
        Assert.Empty(keys.Dropped);
    }

    /// <summary>
    /// **A file that gives Ctrl+B to Add this folder to places and never took
    /// it from the sidebar meant what it said.** The chosen key wins; the
    /// sidebar keeps the key nobody asked for, and the loss is recorded.
    /// </summary>
    [Fact]
    public void A_chosen_key_beats_a_default_that_still_names_it()
    {
        var keys = Keymap.From(Choose(("PinCurrent", ["Ctrl+B"])));

        Assert.Equal("PinCurrent", keys.Owner(G("Ctrl+B"))?.Id);
        Assert.Equal([G("F9")], keys.KeysOf("Sidebar"));
        Assert.Contains(keys.Dropped, line => line.StartsWith("Sidebar:", StringComparison.Ordinal));
    }

    /// <summary>Between two chosen keys, the command listed first keeps it —
    /// deterministic, and said.</summary>
    [Fact]
    public void Between_two_chosen_keys_the_first_command_keeps_it()
    {
        var keys = Keymap.From(Choose(("CloseTab", ["Ctrl+K"]), ("NewTab", ["Ctrl+K"])));

        Assert.Equal("NewTab", keys.Owner(G("Ctrl+K"))?.Id);
        Assert.Empty(keys.KeysOf("CloseTab"));
        Assert.Single(keys.Dropped);
    }

    /// <summary>
    /// **A settings file must never stop the window opening**, so what cannot
    /// be honoured is dropped and said, not thrown: a word that is not a key,
    /// a key that keeps its own job, and a command this build does not have —
    /// whose keys belong to the Vaktari that wrote them.
    /// </summary>
    [Fact]
    public void What_cannot_be_honoured_is_dropped_not_thrown()
    {
        var keys = Keymap.From(Choose(
            ("NewTab", ["Ctrl+", "Enter", "nonsense", "Ctrl+K"]),
            ("FromANewerVaktari", ["Ctrl+J"])));

        Assert.Equal([G("Ctrl+K")], keys.KeysOf("NewTab"));
        Assert.Equal(4, keys.Dropped.Count);
        Assert.Null(keys.Owner(G("Ctrl+J")));
    }

    /// <summary>Avalonia matches the pad's plus as the top row's, so the two
    /// are one key here: given to one command, taken from the other.</summary>
    [Fact]
    public void The_pad_and_the_top_row_are_one_key()
    {
        var keys = Keymap.From(Choose(("ZoomOut", ["Ctrl+Add"])));

        Assert.Equal("ZoomOut", keys.Owner(G("Ctrl++"))?.Id);
        Assert.Empty(keys.KeysOf("ZoomIn"));
    }

    // ---- the keymap in force -------------------------------------------------------

    [AvaloniaFact]
    public void The_keymap_in_force_follows_the_settings()
    {
        Use(("NewTab", ["Ctrl+K"]));

        Assert.Equal([G("Ctrl+K")], Keymap.Current.KeysOf("NewTab"));

        AppSettings.Apply(AppSettings.Current with { Keyboard = new KeyboardSettings() });

        Assert.Equal([G("Ctrl+T")], Keymap.Current.KeysOf("NewTab"));
    }

    /// <summary>
    /// Told once when a save moves a key, and not at all when it does not —
    /// most saves change something else, and every window reinstalls its keys
    /// on the event.
    /// </summary>
    [AvaloniaFact]
    public void A_save_that_moves_no_key_says_nothing()
    {
        var heard = 0;
        EventHandler count = (_, _) => heard++;

        _ = Keymap.Current;
        Keymap.Changed += count;

        try
        {
            AppSettings.Apply(AppSettings.Current with
            {
                General = AppSettings.Current.General with { ShowTooltips = !AppSettings.Current.General.ShowTooltips },
            });

            Assert.Equal(0, heard);

            Use(("NewTab", ["Ctrl+K"]));
            Assert.Equal(1, heard);

            // A new record saying the same thing, which is what the dialog's
            // save writes whether or not a key moved.
            Use(("NewTab", ["Ctrl+K"]));
            Assert.Equal(1, heard);
        }
        finally
        {
            Keymap.Changed -= count;
        }
    }

    // ---- what prints a key ---------------------------------------------------------

    /// <summary>
    /// The sheet prints the keys in force: a moved key where it went, a
    /// cleared command nowhere, and a command somebody gave its first key
    /// under its own heading — "every key Vaktari answers" is the promise. And
    /// the F3 line names wherever Search went.
    /// </summary>
    [Fact]
    public void The_sheet_prints_the_keys_in_force()
    {
        var keys = Keymap.From(Choose(
            ("Search", ["Ctrl+K"]),
            ("CloseTab", []),
            ("SortBySize", ["Ctrl+Alt+S"])));

        var sheet = Shortcuts.For(false, keys);
        var lines = sheet.SelectMany(g => g.Keys).ToList();

        Assert.Contains(lines, l => l.Keys == "Ctrl+K" && l.Does == "Search");
        Assert.Contains(lines, l => l.Keys == "F3" && l.Does == "Split the window — search is Ctrl+K");
        Assert.DoesNotContain(lines, l => l.Keys.Contains("Ctrl+W", StringComparison.Ordinal));
        Assert.Contains(sheet.Single(g => g.Name == "Looking at things").Keys,
                        l => l.Keys == "Ctrl+Alt+S" && l.Does == "Sort by size");
    }

    /// <summary>A line explaining another program's habit goes when the key
    /// the habit reaches for does: moved off F3, the split has no collision
    /// left to explain.</summary>
    [Fact]
    public void A_habit_note_goes_when_its_key_does()
    {
        var line = Shortcuts.For(false, Keymap.From(Choose(("ToggleSplit", ["Ctrl+J"]))))
            .SelectMany(g => g.Keys)
            .Single(l => l.Keys == "Ctrl+J");

        Assert.Equal("Split the window", line.Does);
    }

    /// <summary>The tour reads the keymap when a card is shown, so it teaches
    /// the key a command has now.</summary>
    [AvaloniaFact]
    public void The_tour_teaches_the_key_in_force()
    {
        var search = Tour.Cards.SelectMany(c => c.Lines).Single(l => l.Command == "Search");

        Use(("Search", ["Ctrl+K"]));
        Assert.Equal("Ctrl+K", search.Keys);

        Use(("Search", []));
        Assert.Equal("no key yet", search.Keys);
    }

    /// <summary>The magnifier's tooltip names the key in force, and is told
    /// when a save moves it.</summary>
    [AvaloniaFact]
    public void The_search_tip_names_the_key_in_force()
    {
        var pane = Own(new PaneViewModel(new Inert()));
        var told = 0;

        pane.PropertyChanged += (_, e) => told += e.PropertyName == nameof(PaneViewModel.SearchTip) ? 1 : 0;

        Use(("Search", ["Ctrl+K"]));

        Assert.Equal("Search files  (ctrl+k)", pane.SearchTip);
        Assert.True(told > 0, "the pane was not told the tooltip changed");
    }

    /// <summary>A menu row shows its command's first key, written when the row
    /// is given its command.</summary>
    [AvaloniaFact]
    public void A_menu_row_shows_its_commands_key()
    {
        Use(("Copy", ["Ctrl+K", "Ctrl+Insert"]));

        var row = new MenuItem();

        KeyHint.SetCommand(row, "Copy");

        Assert.Equal(G("Ctrl+K"), row.InputGesture);

        Use(("Copy", []));
        KeyHint.Refresh(row);

        Assert.Null(row.InputGesture);
    }

    // ---- the window ----------------------------------------------------------------

    /// <summary>
    /// **A moved key answers, and the key it moved from does not** — in a real
    /// window, whose keys that answer anywhere are KeyBindings it made when it
    /// opened and makes again when a save moves one.
    /// </summary>
    [AvaloniaFact]
    public void A_moved_key_answers_in_the_window_and_the_old_one_does_not()
    {
        var window = Shown();
        var tabs = window.Shell.Left.Tabs.Count;

        Use(("NewTab", ["Ctrl+K"]));
        Pump();

        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
        Pump();

        Assert.Equal(tabs + 1, window.Shell.Left.Tabs.Count);

        window.KeyPress(Key.T, RawInputModifiers.Control, PhysicalKey.T, null);
        Pump();

        Assert.Equal(tabs + 1, window.Shell.Left.Tabs.Count);
    }

    /// <summary>
    /// **A key a text box edits with is left to the box.** New tab on Ctrl+V
    /// opens a tab from the listing — and from the address bar pastes, as
    /// Ctrl+V always did there. Avalonia's KeyBinding takes a keystroke only
    /// when its command says it can run, and the window's says no while a box
    /// has the keyboard and the key is one the box uses.
    /// </summary>
    [AvaloniaFact]
    public void A_key_a_box_edits_with_is_left_to_the_box()
    {
        var window = Shown();
        var pane = window.Shell.ActiveTab!;
        var tabs = window.Shell.Left.Tabs.Count;

        Use(("NewTab", ["Ctrl+V"]));
        Pump();

        pane.BeginEditPathCommand.Execute(null);
        Pump();

        Assert.True(window.FocusManager?.GetFocusedElement() is TextBox,
                    "the path box does not have the keyboard, so this proves nothing");

        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        Pump();

        Assert.Equal(tabs, window.Shell.Left.Tabs.Count);

        Listing(window, pane).Focus();
        Pump();

        Assert.False(window.FocusManager?.GetFocusedElement() is TextBox);

        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        Pump();

        Assert.Equal(tabs + 1, window.Shell.Left.Tabs.Count);
    }

    /// <summary>
    /// **A guarded command's key is given back to a box when it is one the box
    /// types with.** The guarded commands are answered above the text-box
    /// guard on purpose, so a focused box does not stop them — and Ctrl+Alt
    /// with a letter is AltGr, which a box does not claim on key-down: the
    /// character arrives as text, and the key-down comes on to the window as
    /// well. Without the check in DispatchKeymap, typing € in the address bar
    /// would have toggled the hidden files too.
    ///
    /// From the listing, the same key does toggle them — or the first half
    /// would pass on a key that answers nowhere.
    /// </summary>
    [AvaloniaFact]
    public void A_guarded_key_a_box_types_with_is_left_to_the_box()
    {
        var window = Shown();
        var pane = window.Shell.ActiveTab!;

        Use(("ToggleHidden", ["Ctrl+Alt+E"]));
        Pump();

        var before = pane.ShowHidden;

        pane.BeginEditPathCommand.Execute(null);
        Pump();

        Assert.True(window.FocusManager?.GetFocusedElement() is TextBox,
                    "the path box does not have the keyboard, so this proves nothing");

        window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.E, null);
        Pump();

        Assert.Equal(before, pane.ShowHidden);

        Listing(window, pane).Focus();
        Pump();

        window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.E, null);
        Pump();

        Assert.NotEqual(before, pane.ShowHidden);
    }

    /// <summary>
    /// A listing command on a moved key answers behind the guards, and the key
    /// it moved from does nothing: Invert the selection on Ctrl+K selects all
    /// three files from none, and Ctrl+Shift+A — which the listing control
    /// does not answer itself — leaves them selected.
    /// </summary>
    [AvaloniaFact]
    public async Task A_moved_listing_key_answers_and_the_old_one_does_not()
    {
        var folder = _scratch = Directory.CreateTempSubdirectory("vaktari-keymap").FullName;

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(folder, name), name);

        var window = Shown();
        var pane = window.Shell.ActiveTab!;

        Use(("InvertSelection", ["Ctrl+K"]));

        await pane.NavigateAsync(folder);
        await Until(() => pane.Entries.Count == 3);

        var list = Listing(window, pane);

        list.SelectedItems?.Clear();
        list.Focus();
        Pump();

        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
        Pump();

        Assert.Equal(3, list.SelectedItems?.Count);

        window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.A, null);
        Pump();

        Assert.Equal(3, list.SelectedItems?.Count);
    }

    /// <summary>
    /// **A space typed into a name belongs to the name.** Typing "new folder"
    /// into the listing toggled the preview on the fourth keystroke and threw
    /// the prefix away, and the only test of that asked the pane whether a
    /// word was being typed, never the window what it did with the key. So the
    /// window's half, the listing's keys passing on a key that types while a
    /// name is being typed, could be removed with nothing going red: measured,
    /// it was, and nothing did. Space on its own opens and closes the preview
    /// first, which is what makes the rest mean anything.
    ///
    /// Headless sends a key and the text it types separately, so the space's
    /// text is sent here after its key. The two names are chosen so the
    /// selection says whether the space joined the name: without it, "newf"
    /// lands on newfile.txt.
    /// </summary>
    [AvaloniaFact]
    public async Task A_space_typed_into_a_name_joins_the_name()
    {
        var folder = _scratch = Directory.CreateTempSubdirectory("vaktari-keymap").FullName;

        foreach (var name in new[] { "new folder.txt", "newfile.txt" })
            File.WriteAllText(Path.Combine(folder, name), name);

        var window = Shown();
        var pane = window.Shell.ActiveTab!;

        await pane.NavigateAsync(folder);
        await Until(() => pane.Entries.Count == 2);

        Listing(window, pane).Focus();
        Pump();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();

        Assert.True(pane.IsPreviewVisible, "Space on its own did not open the preview, so the rest proves nothing");

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();

        Assert.False(pane.IsPreviewVisible);

        foreach (var letter in new[] { "n", "e", "w" }) window.KeyTextInput(letter);
        Pump();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyTextInput(" ");
        window.KeyTextInput("f");
        Pump();

        Assert.False(pane.IsPreviewVisible, "the space opened the preview instead of joining the name");
        Assert.Equal("new folder.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// **The sidebar refuses what the keymap has the key running**, not a list
    /// of keys: Move to the bin on Ctrl+K is refused from a place row, and
    /// Delete — now nobody's — is not.
    /// </summary>
    [Fact]
    public void A_moved_key_is_refused_from_the_sidebar()
    {
        var keys = Keymap.From(Choose(("Trash", ["Ctrl+K"])));

        Assert.True(SidebarWalk.ActsOnTheListing(Key.K, KeyModifiers.Control, keys));
        Assert.False(SidebarWalk.ActsOnTheListing(Key.Delete, KeyModifiers.None, keys));
        Assert.True(SidebarWalk.ActsOnTheListing(Key.Delete, KeyModifiers.None, Keymap.Default));
    }

    /// <summary>
    /// The labels in the window print the key in force: the close-tab button
    /// says its new key to the pointer and to a screen reader, and says none
    /// once it has none.
    /// </summary>
    [AvaloniaFact]
    public void The_labels_print_the_key_in_force()
    {
        var window = Shown();

        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => KeyHint.GetCommand(b) == "CloseTab");

        Assert.Equal("Close tab  (ctrl+w)", ToolTip.GetTip(button));

        Use(("CloseTab", ["Ctrl+K"]));
        Pump();

        Assert.Equal("Close tab  (ctrl+k)", ToolTip.GetTip(button));
        Assert.Equal("Close tab  (ctrl+k)", AutomationProperties.GetName(button));

        Use(("CloseTab", []));
        Pump();

        Assert.Equal("Close tab", ToolTip.GetTip(button));
    }

    /// <summary>
    /// A control given its command after it is already on screen follows the
    /// keys too. It hears about a moved key only while attached, and it
    /// subscribes when it attaches — which, for this one, has been and gone.
    /// </summary>
    [AvaloniaFact]
    public void A_label_given_its_command_on_screen_still_follows_the_keys()
    {
        var button = new Button();
        var window = new Window { Content = button };

        _windows.Add(window);
        window.Show();
        Pump();

        KeyHint.SetCommand(button, "CloseTab");
        KeyHint.SetTip(button, "Close tab");

        Assert.Equal("Close tab  (ctrl+w)", ToolTip.GetTip(button));

        Use(("CloseTab", ["Ctrl+K"]));
        Pump();

        Assert.Equal("Close tab  (ctrl+k)", ToolTip.GetTip(button));
    }

    // ---- the file ------------------------------------------------------------------

    /// <summary>What is chosen is what comes back — a moved key and a cleared
    /// command both — through the real store.</summary>
    [Fact]
    public void The_keys_survive_the_settings_file()
    {
        var folder = _scratch = Directory.CreateTempSubdirectory("vaktari-keymap-file").FullName;
        var store = new JsonSettingsStore(folder);

        store.Save(new SettingsState { Keyboard = Choose(("NewTab", ["Ctrl+K"]), ("CloseTab", [])) });

        var keys = Keymap.From(new JsonSettingsStore(folder).Load().Keyboard);

        Assert.Equal([G("Ctrl+K")], keys.KeysOf("NewTab"));
        Assert.Empty(keys.KeysOf("CloseTab"));
    }

    // ---- helpers -------------------------------------------------------------------

    private MainWindow Shown()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 800 };

        _windows.Add(window);
        window.Show();
        Pump();

        return window;
    }

    /// <summary>The listing the keys act on, found the way ColumnChooserTests
    /// finds it — only one of a pane's three layouts is drawn.</summary>
    private static ListBox Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
                 .OfType<ListBox>()
                 .Single(l => l.IsVisible
                              && ReferenceEquals(l.DataContext, pane)
                              && l.SelectionMode.HasFlag(SelectionMode.Multiple));

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Pump();
            await Task.Delay(5);
        }

        Pump();
    }

    /// <summary>The window's side, recorded rather than performed.</summary>
    private sealed class Host(ShellViewModel shell) : ICommandHost
    {
        public List<string> Ran { get; } = [];

        public ShellViewModel Shell => shell;
        public bool TypingInABox => false;

        public void SelectAll() => Ran.Add(nameof(SelectAll));
        public void SelectNone() => Ran.Add(nameof(SelectNone));
        public void InvertSelection() => Ran.Add(nameof(InvertSelection));
        public void DeletePermanently() => Ran.Add(nameof(DeletePermanently));
        public void NextRegion() => Ran.Add(nameof(NextRegion));
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
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
}
