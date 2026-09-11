using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Settings ▸ Keyboard: every command, and the keys that run it, to change.
///
/// These pin the page's own rules — a free key is given, a key another
/// command has is offered rather than taken, a key no command may have is
/// refused with the reason and listening goes on, only what differs is
/// written and what a newer Vaktari wrote is carried through — and the two
/// ends: the dialog saves what the page holds, and in the real window every
/// keystroke goes to the listening row, Enter and Escape included, and none
/// does once the page is left.
/// </summary>
public sealed class KeyboardPageTests
{
    private static KeyboardSettings Choose(params (string Id, string[] Keys)[] choices)
        => new()
        {
            Bindings = choices.ToDictionary(c => c.Id, c => c.Keys.ToList(), StringComparer.Ordinal),
        };

    private static KeyboardPage Page(params (string Id, string[] Keys)[] choices)
    {
        var page = new KeyboardPage();

        page.Load(Choose(choices));

        return page;
    }

    private static KeyRow Row(KeyboardPage page, string id) => page.Rows.Single(r => r.Command.Id == id);

    private static KeyGesture G(string text) => KeyChords.Parse(text) ?? throw new ArgumentException(text);

    // ---- what the page shows -----------------------------------------------------

    /// <summary>Every command, under the heading the F1 sheet gives it, with
    /// the keys the settings give it.</summary>
    [Fact]
    public void Every_command_is_listed_under_its_heading_with_its_keys()
    {
        var page = Page();

        Assert.Equal(Commands.All.Count, page.Rows.Count());
        Assert.Equal(Commands.All.Select(c => c.Group).Distinct(), page.Groups.Select(g => g.Name));
        Assert.Equal([G("Ctrl+T")], Row(page, "NewTab").Gestures);
        Assert.Empty(Row(page, "SortBySize").Gestures);
    }

    [Fact]
    public void A_chosen_key_is_what_the_row_shows()
    {
        var page = Page(("NewTab", ["Ctrl+K"]));

        Assert.Equal([G("Ctrl+K")], Row(page, "NewTab").Gestures);
        Assert.True(Row(page, "NewTab").IsChanged);
        Assert.False(Row(page, "CloseTab").IsChanged);
    }

    /// <summary>
    /// **What the file asked for and could not have is said on the page.** The
    /// keymap records a key it could not give rather than throwing, so a
    /// hand-edited file never stops the window opening; this is where that
    /// record is read. A file nobody edited says nothing.
    /// </summary>
    [Fact]
    public void What_the_file_asked_for_and_could_not_have_is_said()
    {
        var page = Page(("NewTab", ["K"]));

        Assert.True(page.HasUnused);
        Assert.Contains("types a character", page.Unused, StringComparison.Ordinal);

        Assert.False(Page().HasUnused);
    }

    // ---- giving a key --------------------------------------------------------------

    /// <summary>A key nobody has goes to the listening row, listening stops,
    /// and it is written in the file's spelling.</summary>
    [Fact]
    public void A_free_key_is_given_to_the_listening_row_and_written()
    {
        var page = Page();
        var row = Row(page, "SortBySize");

        page.Listen(row);

        Assert.True(row.IsListening);
        Assert.True(page.Offer(Key.S, KeyModifiers.Control | KeyModifiers.Alt));

        Assert.Equal([G("Ctrl+Alt+S")], row.Gestures);
        Assert.Null(page.Listening);
        Assert.False(row.IsListening);

        var written = page.Collect();

        Assert.Equal(["Ctrl+Alt+S"], written["SortBySize"]);
        Assert.Single(written);
    }

    /// <summary>
    /// **Only what differs is written.** A row that answers to exactly its
    /// shipped keys writes nothing, so a key a later release adds to it
    /// reaches this person too.
    /// </summary>
    [Fact]
    public void Only_rows_that_differ_are_written()
    {
        Assert.Empty(Page().Collect());

        var page = Page(("NewTab", ["Ctrl+K"]));

        Assert.Equal(["NewTab"], page.Collect().Keys);
    }

    /// <summary>
    /// **A key another command has is offered, not taken.** Moving it without
    /// asking would take a key from a command somebody may not be looking at.
    /// Take it moves it, and writes both rows — the one that gained it and the
    /// one that lost it.
    /// </summary>
    [Fact]
    public void A_key_another_command_has_is_offered_and_take_it_moves_it()
    {
        var page = Page();
        var pin = Row(page, "PinCurrent");
        var sidebar = Row(page, "Sidebar");

        page.Listen(pin);
        page.Offer(Key.B, KeyModifiers.Control);

        Assert.True(page.IsOffering);
        Assert.Same(sidebar, page.OfferedFrom);
        Assert.Equal([G("Ctrl+D")], pin.Gestures);
        Assert.Equal([G("Ctrl+B"), G("F9")], sidebar.Gestures);

        page.TakeItCommand.Execute(null);

        Assert.Equal([G("Ctrl+D"), G("Ctrl+B")], pin.Gestures);
        Assert.Equal([G("F9")], sidebar.Gestures);
        Assert.False(page.IsOffering);

        var written = page.Collect();

        Assert.Equal(["Ctrl+D", "Ctrl+B"], written["PinCurrent"]);
        Assert.Equal(["F9"], written["Sidebar"]);
    }

    [Fact]
    public void Keep_it_there_leaves_both_alone()
    {
        var page = Page();

        page.Listen(Row(page, "PinCurrent"));
        page.Offer(Key.B, KeyModifiers.Control);
        page.KeepItCommand.Execute(null);

        Assert.Empty(page.Collect());
        Assert.Null(page.Listening);
        Assert.False(page.IsOffering);
    }

    /// <summary>A key pressed instead of answering an offer withdraws it: the
    /// two buttons must not go on answering for a key nobody is being asked
    /// about any more.</summary>
    [Fact]
    public void A_key_pressed_instead_of_an_answer_withdraws_the_offer()
    {
        var page = Page();
        var pin = Row(page, "PinCurrent");

        page.Listen(pin);
        page.Offer(Key.B, KeyModifiers.Control);

        Assert.True(page.IsOffering);

        page.Offer(Key.J, KeyModifiers.Control);

        Assert.False(page.IsOffering);
        Assert.Equal([G("Ctrl+D"), G("Ctrl+J")], pin.Gestures);
        Assert.Equal([G("Ctrl+B"), G("F9")], Row(page, "Sidebar").Gestures);
    }

    /// <summary>
    /// **The pad's plus is the top row's plus here, as it is to a keystroke.**
    /// Told apart, the page would have given Ctrl and the pad's plus to Sort
    /// by size while Zoom in went on showing Ctrl++, and the keymap — which
    /// cannot tell them apart — would then have given the key to one of them
    /// and dropped it from the other. Offered instead, and once taken the file
    /// the page writes builds a keymap with nothing dropped.
    /// </summary>
    [Fact]
    public void The_pad_plus_is_the_top_row_plus_here_as_in_the_keymap()
    {
        var page = Page();
        var sort = Row(page, "SortBySize");
        var zoom = Row(page, "ZoomIn");

        page.Listen(sort);
        page.Offer(Key.Add, KeyModifiers.Control);

        Assert.Same(zoom, page.OfferedFrom);

        page.TakeItCommand.Execute(null);

        Assert.DoesNotContain(zoom.Gestures, g => KeyChords.Same(g, G("Ctrl++")));

        var keymap = Keymap.From(new KeyboardSettings { Bindings = page.Collect() });

        Assert.Empty(keymap.Dropped);
        Assert.Equal("SortBySize", keymap.Owner(G("Ctrl++"))?.Id);
    }

    /// <summary>
    /// A key no command may have is refused with the reason, and listening
    /// goes on so another can be tried: a letter on its own types, Enter
    /// keeps its job.
    /// </summary>
    [Fact]
    public void A_key_no_command_may_have_is_refused_and_listening_goes_on()
    {
        var page = Page();
        var row = Row(page, "NewTab");

        page.Listen(row);

        Assert.True(page.Offer(Key.K, KeyModifiers.None));
        Assert.Contains("types a character", page.Status, StringComparison.Ordinal);

        Assert.True(page.Offer(Key.Enter, KeyModifiers.None));
        Assert.Contains("keeps its own job", page.Status, StringComparison.Ordinal);

        Assert.Same(row, page.Listening);
        Assert.Equal([G("Ctrl+T")], row.Gestures);
    }

    /// <summary>
    /// Ctrl on its own is half a key: waited past, not taken — and not refused
    /// either, which is what the refusal rules would do with it, so pressing
    /// Ctrl on the way to Ctrl+K leaves the line saying what to press rather
    /// than flashing a refusal nobody asked for.
    /// </summary>
    [Fact]
    public void Half_a_key_is_waited_past()
    {
        var page = Page();
        var row = Row(page, "NewTab");

        page.Listen(row);

        var asked = page.Status;

        Assert.True(page.Offer(Key.LeftCtrl, KeyModifiers.Control));
        Assert.Same(row, page.Listening);
        Assert.Equal([G("Ctrl+T")], row.Gestures);
        Assert.Equal(asked, page.Status);
    }

    [Fact]
    public void Escape_stops_listening()
    {
        var page = Page();

        page.Listen(Row(page, "NewTab"));

        Assert.True(page.Offer(Key.Escape, KeyModifiers.None));
        Assert.Null(page.Listening);
        Assert.Equal("", page.Status);
    }

    /// <summary>The add button, clicked again while its row listens, stops it —
    /// the button says "Press a key…" by then, and clicking it is the
    /// plainest way to say never mind.</summary>
    [Fact]
    public void Add_clicked_again_stops_listening()
    {
        var page = Page();
        var row = Row(page, "NewTab");

        row.AddCommand.Execute(null);

        Assert.Same(row, page.Listening);
        Assert.Equal("Press a key…", row.AddLabel);

        row.AddCommand.Execute(null);

        Assert.Null(page.Listening);
        Assert.Equal("Add key", row.AddLabel);
    }

    /// <summary>Space is the one typing key a command may have, and only one
    /// that acts on the listing: refused for New tab, offered for Duplicate
    /// — where it is Quick preview's, so it is offered rather than given.</summary>
    [Fact]
    public void Space_is_only_for_a_command_that_acts_on_the_listing()
    {
        var page = Page();

        page.Listen(Row(page, "NewTab"));
        page.Offer(Key.Space, KeyModifiers.None);

        Assert.Contains("Space", page.Status, StringComparison.Ordinal);
        Assert.False(page.IsOffering);

        page.Listen(Row(page, "Duplicate"));
        page.Offer(Key.Space, KeyModifiers.None);

        Assert.True(page.IsOffering);
        Assert.Equal("Preview", page.OfferedFrom?.Command.Id);
    }

    /// <summary>Not listening, a key is the dialog's: the page does not take it.</summary>
    [Fact]
    public void Not_listening_the_page_takes_no_key()
        => Assert.False(Page().Offer(Key.K, KeyModifiers.Control));

    /// <summary>Leaving the page stops its row listening, whichever way the
    /// page was left.</summary>
    [Fact]
    public void Leaving_the_page_stops_the_row_listening()
    {
        var page = Page();

        page.IsOpen = true;
        page.Listen(Row(page, "NewTab"));
        page.IsOpen = false;

        Assert.Null(page.Listening);
        Assert.False(page.Offer(Key.K, KeyModifiers.Control));
    }

    // ---- taking keys off, and putting them back ------------------------------------

    /// <summary>A command with its only key taken off has no key, and that is
    /// written as an empty list — which is how "none" differs from "the
    /// shipped ones".</summary>
    [Fact]
    public void Taking_the_last_key_off_writes_a_command_with_none()
    {
        var page = Page();
        var close = Row(page, "CloseTab");

        close.Keys.Single().Remove.Execute(null);

        Assert.Empty(close.Gestures);
        Assert.Empty(page.Collect()["CloseTab"]);
    }

    /// <summary>
    /// Reset puts a row back — except a key another row has been given since,
    /// which stays there and is named: Reset on this row does not take keys
    /// from a command somebody is not looking at.
    /// </summary>
    [Fact]
    public void Reset_puts_a_row_back_except_keys_another_row_has_now()
    {
        var page = Page(("PinCurrent", ["Ctrl+D", "Ctrl+B"]), ("Sidebar", ["F9"]), ("NewTab", ["Ctrl+K"]));

        Row(page, "Sidebar").ResetCommand.Execute(null);

        Assert.Equal([G("F9")], Row(page, "Sidebar").Gestures);
        Assert.Contains("Ctrl+B", page.Status, StringComparison.Ordinal);

        Row(page, "NewTab").ResetCommand.Execute(null);

        Assert.Equal([G("Ctrl+T")], Row(page, "NewTab").Gestures);
    }

    [Fact]
    public void Reset_all_puts_every_row_back()
    {
        var page = Page(("NewTab", ["Ctrl+K"]), ("CloseTab", []));

        page.ResetAllCommand.Execute(null);

        Assert.Empty(page.Collect());
    }

    /// <summary>
    /// **Keys a newer Vaktari wrote are carried through.** An id this build
    /// has no command for belongs to the version that wrote it; saving from
    /// this one must not erase it.
    /// </summary>
    [Fact]
    public void Keys_a_newer_vaktari_wrote_are_carried_through()
    {
        var page = Page(("FromANewerVaktari", ["Ctrl+J"]));

        Assert.Equal(["Ctrl+J"], page.Collect()["FromANewerVaktari"]);
        Assert.DoesNotContain(page.Rows, r => r.Command.Id == "FromANewerVaktari");
    }

    // ---- finding -------------------------------------------------------------------

    /// <summary>By name, words in any order, or by a key as the sheet or a
    /// label spells it — "ctrl+b" finds whatever Ctrl+B runs, which is the
    /// question somebody arriving from another program asks.</summary>
    [Fact]
    public void The_filter_finds_a_command_by_name_or_by_key()
    {
        var page = Page();

        page.Filter = "tab close";

        Assert.True(Row(page, "CloseTab").IsShown);
        Assert.False(Row(page, "NewTab").IsShown);

        page.Filter = "ctrl+b";

        Assert.Equal(["Sidebar"], page.Rows.Where(r => r.IsShown).Select(r => r.Command.Id));
        Assert.Equal(["Tabs and panes"], page.Groups.Where(g => g.IsShown).Select(g => g.Name));

        page.Filter = "";

        Assert.All(page.Rows, r => Assert.True(r.IsShown));
    }

    /// <summary>
    /// **The keys sheet says where its keys are changed.** F1 is where somebody
    /// goes to find a key, so it is where the one that collides with a habit is
    /// found — and a sheet that only listed keys left the page that moves them
    /// to be found some other way.
    /// </summary>
    [Fact]
    public void The_keys_sheet_says_where_keys_are_changed()
    {
        var sheet = System.Xml.Linq.XDocument.Parse(RepoSource.Ui("ShortcutsWindow.axaml"));
        System.Xml.Linq.XNamespace avalonia = "https://github.com/avaloniaui";

        Assert.Contains(sheet.Descendants(avalonia + "TextBlock"),
                        t => ((string?)t.Attribute("Text") ?? "").Contains("on the Keyboard page", StringComparison.Ordinal));
    }

    // ---- the dialog ----------------------------------------------------------------

    /// <summary>The dialog opens the page on the keys in the file, saves what
    /// the page holds — a key moved on it, not the one it opened with — and
    /// Restore defaults puts the keys back with everything else.</summary>
    [AvaloniaFact]
    public void The_dialog_saves_the_page_and_restore_resets_it()
    {
        var vm = new SettingsViewModel(new SettingsState { Keyboard = Choose(("NewTab", ["Ctrl+K"])) });
        var row = Row(vm.Keyboard, "NewTab");

        Assert.Equal([G("Ctrl+K")], row.Gestures);

        row.Keys.Single().Remove.Execute(null);
        vm.Keyboard.Listen(row);
        vm.Keyboard.Offer(Key.J, KeyModifiers.Control);

        vm.SaveCommand.Execute(null);

        Assert.Equal(["Ctrl+J"], vm.Result.Keyboard.Bindings["NewTab"]);

        var restored = new SettingsViewModel(new SettingsState { Keyboard = Choose(("NewTab", ["Ctrl+K"])) });

        restored.RestoreDefaultsCommand.Execute(null);
        restored.SaveCommand.Execute(null);

        Assert.Equal([G("Ctrl+T")], Row(restored.Keyboard, "NewTab").Gestures);
        Assert.Empty(restored.Result.Keyboard.Bindings);
    }

    /// <summary>
    /// **In the real window every keystroke goes to the listening row** — Enter
    /// and Escape included, which the dialog's own Save and Cancel would
    /// otherwise have: Enter is refused with its reason and saves nothing,
    /// Escape stops listening and closes nothing, and an Alt key reaches the
    /// row rather than the dialog's access keys.
    /// </summary>
    [AvaloniaFact]
    public void In_the_window_every_key_goes_to_the_listening_row()
    {
        var vm = new SettingsViewModel(new SettingsState());
        var window = new SettingsWindow(vm);
        var closed = false;

        window.Closed += (_, _) => closed = true;
        window.Show();
        Pump();

        try
        {
            OpenKeyboardPage(window);

            Assert.True(vm.Keyboard.IsOpen, "the page's tab is not bound to it, so leaving it would not stop a row");

            var sort = Row(vm.Keyboard, "SortBySize");

            sort.AddCommand.Execute(null);
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.S, null);
            Pump();

            Assert.Equal([G("Ctrl+Alt+S")], sort.Gestures);

            var group = Row(vm.Keyboard, "GroupBySize");

            group.AddCommand.Execute(null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();

            Assert.False(vm.Saved, "Enter saved the dialog out from under the listening row");
            Assert.False(closed);
            Assert.Same(group, vm.Keyboard.Listening);

            window.KeyPress(Key.K, RawInputModifiers.Alt, PhysicalKey.K, null);
            Pump();

            Assert.Equal([G("Alt+K")], group.Gestures);

            group.AddCommand.Execute(null);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();

            Assert.Null(vm.Keyboard.Listening);
            Assert.False(closed, "Escape closed the dialog instead of stopping the row listening");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A key meant for the row is not the focused button's.** A keyboard user
    /// reaches Add key and presses Enter, and the button keeps the keyboard;
    /// a focused button answers Enter itself before the key bubbles up to the
    /// window, so the window has to take the next key on the tunnel or that
    /// Enter presses Add key again and the row stops listening. Here the
    /// second Enter is refused with its reason and the row goes on listening,
    /// and Ctrl+Enter — which a focused button would also have taken — is
    /// given.
    /// </summary>
    [AvaloniaFact]
    public void A_key_for_the_row_is_not_the_focused_buttons()
    {
        var vm = new SettingsViewModel(new SettingsState());
        var window = new SettingsWindow(vm);

        window.Show();
        Pump();

        try
        {
            OpenKeyboardPage(window);

            var row = Row(vm.Keyboard, "SortBySize");
            var add = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.DataContext, row) && ReferenceEquals(b.Command, row.AddCommand));

            add.Focus();
            Pump();

            Assert.Same(add, window.FocusManager?.GetFocusedElement());

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();

            Assert.Same(row, vm.Keyboard.Listening);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();

            Assert.Same(row, vm.Keyboard.Listening);
            Assert.Contains("keeps its own job", vm.Keyboard.Status, StringComparison.Ordinal);

            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            Pump();

            Assert.Equal([G("Ctrl+Enter")], row.Gestures);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A row left listening takes no keys once its page is left.** The
    /// window hands every key to a listening row, so one left listening on a
    /// page nobody can see would take what is typed into the next page's
    /// boxes. Going to General stops it, and Ctrl+K then reaches nothing on the
    /// Keyboard page.
    /// </summary>
    [AvaloniaFact]
    public void A_row_takes_no_keys_once_its_page_is_left()
    {
        var vm = new SettingsViewModel(new SettingsState());
        var window = new SettingsWindow(vm);

        window.Show();
        Pump();

        try
        {
            var tabs = OpenKeyboardPage(window);
            var row = Row(vm.Keyboard, "SortBySize");

            row.AddCommand.Execute(null);

            Assert.Same(row, vm.Keyboard.Listening);

            tabs.SelectedIndex = 0;
            Pump();

            Assert.Null(vm.Keyboard.Listening);

            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            Pump();

            Assert.Empty(row.Gestures);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **Enter on this page does what it does on the others.** The dialog's
    /// default button is safe because it is handed an unhandled Enter only,
    /// and that was pressed on every page before this one existed. Here: on a
    /// focused button Enter presses it — Add key, which then listens — and
    /// saves nothing; from the search box, a single-line box like the rest,
    /// it saves.
    /// </summary>
    [AvaloniaFact]
    public void Enter_presses_a_focused_button_and_saves_from_the_box()
    {
        var vm = new SettingsViewModel(new SettingsState());
        var window = new SettingsWindow(vm);

        window.Show();
        Pump();

        try
        {
            OpenKeyboardPage(window);

            var row = Row(vm.Keyboard, "NewTab");
            var add = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.DataContext, row) && ReferenceEquals(b.Command, row.AddCommand));

            add.Focus();
            Pump();

            Assert.Same(add, window.FocusManager?.GetFocusedElement());

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();

            Assert.False(vm.Saved, "Enter on Add key saved the dialog");
            Assert.Same(row, vm.Keyboard.Listening);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();

            Assert.Null(vm.Keyboard.Listening);

            var box = window.GetVisualDescendants().OfType<TextBox>()
                .Single(t => AutomationProperties.GetName(t) == "Find a command or a key");

            box.Focus();
            Pump();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();

            Assert.True(vm.Saved, "Enter in the search box did not save, as it does from every other box");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **At the dialog's floor every command's name stays clear of its
    /// buttons.** The page is about 300px wide there, and a name that ran on
    /// under Add key and Reset would read as one line with them. Every row is
    /// given a changed key list first, so each row with shipped keys shows
    /// both buttons — the narrowest its name ever gets.
    /// </summary>
    [AvaloniaFact]
    public void At_the_floor_every_name_stays_clear_of_its_buttons()
    {
        var vm = new SettingsViewModel(new SettingsState
        {
            Keyboard = new KeyboardSettings
            {
                Bindings = Commands.All.ToDictionary(c => c.Id, _ => new List<string>(), StringComparer.Ordinal),
            },
        });

        var window = new SettingsWindow(vm) { Width = 520, Height = 450 };

        window.Show();
        Pump();

        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();

            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "Keyboard");
            Pump();

            window.Measure(new Avalonia.Size(520, 450));
            window.Arrange(new Avalonia.Rect(0, 0, 520, 450));
            Pump();

            var rows = window.GetVisualDescendants().OfType<DockPanel>()
                .Where(d => d.DataContext is KeyRow && d.Children.Count == 2)
                .ToList();

            Assert.Equal(Commands.All.Count, rows.Count);

            foreach (var row in rows)
            {
                // The buttons are docked right and declared first; the name
                // is the first line of the column that fills the rest, so its
                // right edge in the row is the column's offset plus its own.
                var buttons = row.Children[0];
                var column = (Panel)row.Children[1];
                var name = column.Children.OfType<TextBlock>().First();
                var right = column.Bounds.X + name.Bounds.Right;
                var label = ((KeyRow)row.DataContext!).Name;

                Assert.True(name.TextLayout.Width <= name.Bounds.Width + 0.5,
                            $"“{label}” is {name.TextLayout.Width:0}px of text in {name.Bounds.Width:0}px");
                Assert.True(right <= buttons.Bounds.Left + 0.5,
                            $"“{label}” ends {right - buttons.Bounds.Left:0}px under its buttons");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Selects the Keyboard page and lays the window out, so the
    /// page's rows exist to be found and focused.</summary>
    private static TabControl OpenKeyboardPage(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();

        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "Keyboard");
        Pump();

        window.Measure(new Avalonia.Size(700, 560));
        window.Arrange(new Avalonia.Rect(0, 0, 700, 560));
        Pump();

        return tabs;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }
}
