using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where a name is typed.
///
/// **F2 renamed in a 320px box at the bottom of the window.** The request went
/// to the shared <c>PromptBar</c> — a bottom-docked Border whose TextBox is a
/// fixed <c>Width="320"</c> — so the row being renamed and the box renaming it
/// were as far apart as the window is tall, with nothing at either end naming
/// the file the other meant. None of the three row templates held a TextBox at
/// all, and none of them held a flag saying a row was being edited.
///
/// The bar is still there, and still right, for the things it was built for:
/// the delete and trash confirmations, a pinned place's caption and a server
/// address. It is the FILE rename that has moved onto the row.
/// </summary>
public sealed class RenameOnTheRowTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    // ---- the markup carries an editor in every layout -----------------------

    private static List<XElement> Listings()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ListBox")
            .Where(l => (string?)l.Attribute("ItemsSource")
                        is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                           or "{Binding GridEntries}")
            .ToList();

    /// <summary>
    /// Discovered from the markup rather than listed here, so a fourth layout
    /// added later is held to this without anybody remembering — the same way
    /// the ghosting, the row name and the selection box are.
    /// </summary>
    [Fact]
    public void Every_row_layout_has_a_box_to_type_the_name_in()
    {
        var listings = Listings();

        // A guard, not decoration: a renamed listing must fail here rather than
        // silently drop out of the check below.
        Assert.Equal(3, listings.Count);

        foreach (var listing in listings)
        {
            var box = Assert.Single(
                listing.Descendants(Xaml + "TextBox"),
                t => (string?)t.Attribute("Classes") == MainWindow.RenameBoxClass);

            // The box appears on the ONE row being renamed, and the pane holds
            // which that is.
            Assert.Contains(
                "RenamingPath",
                box.Element(Xaml + "TextBox.IsVisible")?.ToString() ?? "");

            // And it is the name that is in it. The DIRECTION is not asserted:
            // measured by dropping `Mode=TwoWay` from the details template and
            // watching Enter still rename the file, TextBox.Text is registered
            // two-way already — the markup states it rather than inherits it,
            // which is a reading aid and not a behaviour this can catch.
            Assert.Contains("RenameText", (string?)box.Attribute("Text") ?? "");

            // **And the box takes the keyboard in every layout.** Before this
            // line, the compact and grid hooks could both be deleted with the
            // whole suite green — F2 there put a box on the tile that never
            // took the keyboard, so every keystroke went to the listing's
            // type-ahead. The hook is in the `in:` namespace, so it is matched
            // on its local name.
            var hook = Assert.Single(
                box.Elements(), x => x.Name.LocalName == "RenameBox.Editing");

            Assert.Contains("RenamingPath", hook.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other half of the swap. Leaving the drawn name in place would put
    /// the label and the box in the same cell at once, and the label is the
    /// taller of the two in a tile.
    /// </summary>
    [Fact]
    public void The_drawn_name_steps_aside_while_the_box_is_up()
    {
        foreach (var listing in Listings())
        {
            var name = Assert.Single(
                listing.Descendants(Xaml + "TextBlock"),
                t => ((string?)t.Attribute("Text"))
                     ?.Contains("FileConverters.DisplayName", StringComparison.Ordinal) == true);

            Assert.Contains(
                "NotRenaming",
                name.Element(Xaml + "TextBlock.IsVisible")?.ToString() ?? "");
        }
    }

    // ---- and it is not the bottom bar any more ------------------------------

    private sealed record Rig(Window Window, ShellViewModel Shell, PaneViewModel Pane, string Root)
        : IDisposable
    {
        public void Dispose()
        {
            // Put the layout back before the flush. The view a new tab opens in
            // is remembered, so a test that ended in Grid would hand Grid to
            // every window the rest of the run builds.
            Pane.View = ViewMode.Details;

            // Closing flushes the session; TestState points every store this
            // window builds at a directory belonging to the run, so there is
            // nothing of the developer's own to put back.
            Window.Close();

            try { Directory.Delete(Root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// Real files, because a rename that has to land has to have something to
    /// land on.
    ///
    /// <paramref name="filler"/> is for the one test that needs the listing to
    /// be longer than the pane: the rows are named so they sort AFTER the two
    /// the tests act on, which is what puts "report.txt" at the top and off the
    /// end of a scroll.
    /// </summary>
    private async Task<Rig> BuildAsync(int filler = 0)
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-renamerow-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        foreach (var name in new[] { "report.txt", "second.txt" })
            File.WriteAllText(Path.Combine(root, name), name);

        // A FOLDER for the pointer tests. Opening one navigates, which a test
        // can see and undo; opening a file hands it to the desktop's own
        // handler, which a test must never do.
        Directory.CreateDirectory(Path.Combine(root, "adir"));

        for (var i = 0; i < filler; i++)
            File.WriteAllText(Path.Combine(root, $"zz{i:D4}.txt"), "x");

        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        // Awaited, never blocked on: a headless test runs ON the dispatcher, so
        // a GetResult here would wait for a callback that cannot run until the
        // wait ends.
        await shell.ActiveTab!.NavigateAsync(root);
        Settle();
        window.UpdateLayout();
        Settle();

        Assert.Equal(3 + filler, shell.ActiveTab.Entries.Count);

        // Details, explicitly: the view a new tab opens in is remembered across
        // windows, so a layout test that ran earlier in this assembly would
        // otherwise decide what this one measures.
        shell.ActiveTab.View = ViewMode.Details;

        Settle();
        window.UpdateLayout();
        Settle();

        return new Rig(window, shell, shell.ActiveTab, root);
    }

    /// <summary>Every rename box the window has realized and is showing.</summary>
    private static List<TextBox> Boxes(Window window)
        => window.GetVisualDescendants().OfType<TextBox>()
                 .Where(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible)
                 .ToList();

    /// <summary>Every rename box the window has realized, SHOWN OR NOT — the
    /// item template stamps one onto every row it builds.</summary>
    private static List<TextBox> AllBoxes(Window window)
        => window.GetVisualDescendants().OfType<TextBox>()
                 .Where(t => t.Classes.Contains(MainWindow.RenameBoxClass))
                 .ToList();

    /// <summary>The middle of a control, in the window's own coordinates.</summary>
    private static Point Centre(Visual control, Window window)
    {
        var at = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);

        Assert.NotNull(at);

        return at!.Value;
    }

    private static void Begin(Rig rig, string name)
    {
        rig.Pane.SelectedEntry = rig.Pane.Entries.Single(e => e.Name == name);
        rig.Pane.BeginRenameCommand.Execute(null);

        Settle();
        rig.Window.UpdateLayout();
        Settle();
    }

    [AvaloniaFact]
    public async Task The_box_opens_on_the_row_and_not_in_the_bar()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        Assert.Equal("report.txt", box.Text);

        // On the row it names, rather than anywhere a bar could be: the box's
        // own DataContext is the entry, which is only true inside the item
        // template.
        Assert.Equal(
            rig.Pane.Entries.Single(e => e.Name == "report.txt"),
            Assert.IsType<FileEntry>(box.DataContext));

        Assert.True(box.IsFocused, "the box on the row does not have the keyboard");

        var bar = rig.Window.FindControl<Border>("PromptBar");

        Assert.NotNull(bar);
        Assert.False(bar!.IsVisible, "the bottom bar opened for a file rename again");
    }

    /// <summary>
    /// **The theme's own TextBox does not fit a listing row**, and a details
    /// row is a fixed height that clips rather than growing — so the overflow
    /// is silent: the box is drawn with its top and bottom edges cut off.
    ///
    /// Measured on this window with the sizing style's MinHeight setter taken
    /// out: the Fluent theme gives the box MinHeight 32 and it renders 32 tall
    /// inside a row that is 30. With the setter it renders 20.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_fits_inside_the_row_that_holds_it()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));
        var row = box.GetVisualAncestors().OfType<ListBoxItem>().First();

        Assert.True(box.Bounds.Height <= row.Bounds.Height,
                    $"the box is {box.Bounds.Height} tall in a {row.Bounds.Height} row, "
                    + "so the row clips it");
    }

    /// <summary>
    /// **And it is wide enough to type a name into.** The details row docks its
    /// name cell, so the cell is arranged at what it wants rather than at the
    /// column around it: measured with the MinWidth taken off, renaming
    /// "report.txt" opened a 150-wide box — the theme's own floor — with
    /// hundreds of pixels of column free beside it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_is_wider_than_the_name_it_replaces()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        Assert.True(box.Bounds.Width >= 200,
                    $"the box is only {box.Bounds.Width} wide");
    }

    /// <summary>
    /// **Explorer selects the name and not the extension**, and the box that
    /// does it is built by a DataTemplate — so nothing in the code behind can
    /// reach it to set the selection, which is what <c>RenameBox</c> is for.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_offers_the_name_without_its_extension()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        Assert.Equal(0, box.SelectionStart);
        Assert.Equal("report".Length, box.SelectionEnd);
    }

    [AvaloniaFact]
    public async Task Enter_in_the_box_renames_the_file()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        Boxes(rig.Window).Single().Text = "summary.txt";
        Settle();

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Drain();

        Assert.True(File.Exists(Path.Combine(rig.Root, "summary.txt")));
        Assert.False(File.Exists(Path.Combine(rig.Root, "report.txt")));
        Assert.Empty(Boxes(rig.Window));
    }

    [AvaloniaFact]
    public async Task Escape_in_the_box_leaves_the_name_alone()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        Boxes(rig.Window).Single().Text = "summary.txt";
        Settle();

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Drain();

        Assert.True(File.Exists(Path.Combine(rig.Root, "report.txt")));
        Assert.False(File.Exists(Path.Combine(rig.Root, "summary.txt")));
        Assert.Empty(Boxes(rig.Window));
    }

    /// <summary>
    /// **A refused name used to close the editor and report afterwards**, and
    /// the reason it can stay is that the box is still open — see
    /// <see cref="RefusedRenameTests"/>. The reason has to be readable from
    /// where the box is, so it is held open under the box rather than written
    /// into a hint line at the far end of the window.
    /// </summary>
    [AvaloniaFact]
    public async Task A_name_that_cannot_be_used_keeps_the_box_and_says_why()
    {
        // Enough rows for the listing to realize more than one editor, which is
        // what the popup count below is about.
        using var rig = await BuildAsync(filler: 40);

        Begin(rig, "report.txt");

        var box = Boxes(rig.Window).Single();

        // ".." is refused on both platforms; a colon is a Windows rule only.
        box.Text = "..";
        Settle();
        rig.Window.UpdateLayout();
        Settle();

        Assert.False(string.IsNullOrWhiteSpace(rig.Pane.RenameRefusal),
                     "refused without saying why, which is what it did before");

        Assert.Equal(rig.Pane.RenameRefusal, ToolTip.GetTip(box));
        Assert.True(ToolTip.GetIsOpen(box),
                    "the reason is there and nothing is showing it");

        // **On the ONE row it belongs to.** The reason is a pane-level flag and
        // the editor is stamped once per row, so holding the tip open off that
        // flag alone opened a popup on every realized box: measured on a
        // listing this long, 19 of them stacked at the top with 18 over boxes
        // that are not on screen at all.
        Assert.Single(AllBoxes(rig.Window), ToolTip.GetIsOpen);

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Drain();

        Assert.Single(Boxes(rig.Window));
        Assert.Equal("..", Boxes(rig.Window).Single().Text);
        Assert.True(File.Exists(Path.Combine(rig.Root, "report.txt")));

        // And the reason goes away with the box rather than waiting for the
        // next name to overwrite it.
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Drain();

        Assert.Null(rig.Pane.RenameRefusal);
        Assert.DoesNotContain(AllBoxes(rig.Window), ToolTip.GetIsOpen);
    }

    /// <summary>And it goes away again the moment the name is usable, rather
    /// than leaving a stale complaint over a name that is now fine.</summary>
    [AvaloniaFact]
    public async Task The_reason_clears_when_the_name_becomes_usable()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Boxes(rig.Window).Single();

        box.Text = "..";
        Settle();

        Assert.NotNull(rig.Pane.RenameRefusal);

        box.Text = "summary.txt";
        Settle();

        Assert.Null(rig.Pane.RenameRefusal);
        Assert.False(ToolTip.GetIsOpen(box));
    }

    /// <summary>
    /// **An inline editor that outlives its focus is litter.** The bar it
    /// replaces was docked at the window's edge and obviously a prompt; a box
    /// left on a row halfway down the listing after you have clicked elsewhere
    /// reads as part of the listing.
    /// </summary>
    [AvaloniaFact]
    public async Task Clicking_away_puts_the_box_away()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        Boxes(rig.Window).Single().Text = "summary.txt";
        Settle();

        Listing(rig).Focus();
        await Drain();

        Assert.Empty(Boxes(rig.Window));

        // Cancelled rather than committed: a name nobody confirmed must not be
        // applied by a click aimed at something else.
        Assert.True(File.Exists(Path.Combine(rig.Root, "report.txt")));
        Assert.False(File.Exists(Path.Combine(rig.Root, "summary.txt")));
    }

    /// <summary>
    /// **The window can be left with no way out.** Every shortcut is refused
    /// while a rename is open, which was safe when the editor was a docked bar
    /// that could not go anywhere — and one drawn by a virtualizing item
    /// template can.
    ///
    /// Measured on this window: with the row being renamed scrolled off the
    /// end of a 400-row listing, the box is gone, GetFocusedElement answers
    /// null, and NO lost-focus event was raised — the close that a click
    /// elsewhere triggers never runs, so the rename stays open with nothing
    /// holding the keyboard and every shortcut in the window dead. A key
    /// pressed in that state ends the rename and is then handled as it always
    /// was.
    /// </summary>
    [AvaloniaFact]
    public async Task A_key_with_the_row_scrolled_away_is_not_swallowed()
    {
        using var rig = await BuildAsync(filler: 400);

        Assert.False(rig.Pane.IsFilterVisible, "the filter is already open, so this proves nothing");

        Begin(rig, "report.txt");

        Assert.Single(Boxes(rig.Window));

        var scroll = Listing(rig).GetVisualDescendants().OfType<ScrollViewer>().First();

        scroll.Offset = new Vector(0, scroll.Extent.Height);
        rig.Window.UpdateLayout();
        Settle();

        // The premise, and the measurement: the row is unrealized, so its box
        // is not merely unfocused but gone, and the rename is still open.
        Assert.Empty(Boxes(rig.Window));
        Assert.Null(rig.Window.FocusManager?.GetFocusedElement());
        Assert.NotEqual("", rig.Pane.RenamingPath);

        rig.Window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, null);
        await Drain();

        Assert.True(rig.Pane.IsFilterVisible,
                    "Ctrl+I was swallowed by a rename whose box had been scrolled away");

        // And the rename really ended rather than being stepped over: left
        // open, the very next key would have to talk its way past this again.
        Assert.Equal("", rig.Pane.RenamingPath);
    }

    /// <summary>
    /// Enter asks for the reason itself rather than trusting that the live
    /// check has already run.
    ///
    /// **A CONSTRUCTION, and named as one.** Every ordinary route to a refused
    /// name goes through the box's own text and has therefore been answered
    /// already, so the reason is on screen before Enter is pressed and this
    /// line cannot be seen to do anything. Clearing the reason first is what
    /// separates "Enter reports the refusal" from "something else reported it
    /// earlier" — without it the assignment in ConfirmPrompt could be deleted
    /// with the suite still green.
    /// </summary>
    [AvaloniaFact]
    public async Task Enter_asks_again_even_when_nothing_was_typed()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        Boxes(rig.Window).Single().Text = "..";
        Settle();

        rig.Pane.RenameRefusal = null;

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Drain();

        Assert.False(string.IsNullOrWhiteSpace(rig.Pane.RenameRefusal),
                     "Enter refused the name and said nothing");
        Assert.Single(Boxes(rig.Window));
    }

    // ---- a press inside the box is not a press on the row -------------------

    /// <summary>
    /// Pins the activation preference for one test and puts it back.
    ///
    /// It is a static, and the shipped default is "whatever the desktop says" —
    /// which is a second static that the platform theme writes on startup. A
    /// pointer test that did not say which rule it wanted would measure
    /// whichever one an earlier test in this assembly happened to leave.
    /// </summary>
    private sealed class Activation : IDisposable
    {
        private readonly SettingsState _before = AppSettings.Current;

        /// <summary>AFTER the window is built, always: MainWindow's own startup
        /// re-reads the settings store, so a preference set before it exists is
        /// gone by the time the first click arrives — measured by watching a
        /// press in the box take the double-click path with Single asked
        /// for.</summary>
        public Activation(ActivationClick how)
        {
            AppSettings.Apply(
                _before with { Navigation = _before.Navigation with { OpenItemsWith = how } });

            Assert.Equal(how, AppSettings.Current.Navigation.OpenItemsWith);
        }

        public void Dispose() => AppSettings.Apply(_before);
    }

    /// <summary>
    /// **Two clicks in the editor opened the file.** The box carries the row's
    /// own entry — the first runtime test above asserts exactly that — and the
    /// window finds the row a gesture landed on by walking up to the first
    /// entry it meets, so double-clicking to select a word in the name was an
    /// activation gesture. Measured before the guard: two clicks in the box
    /// renaming the folder "adir" navigated into adir, and left the rename
    /// pointing at what had just become the current folder with no box on
    /// screen.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_clicks_in_the_box_do_not_open_the_row()
    {
        using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Double);

        Begin(rig, "adir");

        var at = Centre(Assert.Single(Boxes(rig.Window)), rig.Window);
        var before = rig.Pane.CurrentPath;

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);
        Settle();

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);
        await Drain();

        Assert.Equal(before, rig.Pane.CurrentPath);
        Assert.Single(Boxes(rig.Window));
    }

    /// <summary>
    /// And with the single-click preference on, ONE click to put the caret
    /// where the typo is was enough to open the thing being renamed.
    /// </summary>
    [AvaloniaFact]
    public async Task One_click_in_the_box_does_not_open_the_row()
    {
        using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Single);

        Begin(rig, "adir");

        var at = Centre(Assert.Single(Boxes(rig.Window)), rig.Window);
        var before = rig.Pane.CurrentPath;

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);
        await Drain();

        Assert.Equal(before, rig.Pane.CurrentPath);
        Assert.Single(Boxes(rig.Window));
    }

    /// <summary>
    /// **Dragging across the box to select the name dragged the FILE.** The
    /// press arms a file drag from any row, and the box is on one. Measured
    /// before the guard: a press at one end of the box and a move to the other
    /// left the window with a real drag in flight and the text untouched —
    /// selection 0-0 where it should be the whole name.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_across_the_box_selects_the_name()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        var from = box.TranslatePoint(new Point(6, box.Bounds.Height / 2), rig.Window)!.Value;
        var to = box.TranslatePoint(
            new Point(box.Bounds.Width - 6, box.Bounds.Height / 2), rig.Window)!.Value;

        rig.Window.MouseDown(from, MouseButton.Left);
        Settle();

        rig.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        await Drain();

        Assert.True(box.SelectionEnd - box.SelectionStart >= "report".Length,
                    $"the drag selected {box.SelectionStart}-{box.SelectionEnd} of the name, "
                    + "so it went to the file rather than the text");

        rig.Window.MouseUp(to, MouseButton.Left);
        Settle();
    }

    /// <summary>
    /// **A box's own context menu is not somewhere else.** A TextBox carries a
    /// Cut/Copy/Paste flyout, and opening it takes the keyboard — so the
    /// close-on-lost-focus above tore the rename down under the menu that had
    /// just opened, on the gesture most likely to be wanted: pasting a name in.
    /// Measured before the guard: the right press left the flyout standing over
    /// a row with no box under it.
    /// </summary>
    [AvaloniaFact]
    public async Task Right_clicking_the_box_keeps_it_open()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));
        var at = Centre(box, rig.Window);

        rig.Window.MouseDown(at, MouseButton.Right);
        rig.Window.MouseUp(at, MouseButton.Right);
        await Drain();

        // The premise: the menu really did open, so "the box survived" is not
        // just "nothing happened".
        Assert.True(box.ContextFlyout?.IsOpen,
                    "no menu opened, so this proves nothing about surviving one");

        Assert.Single(Boxes(rig.Window));
        Assert.Equal(
            rig.Pane.Entries.Single(e => e.Name == "report.txt").FullPath,
            rig.Pane.RenamingPath);

        box.ContextFlyout!.Hide();
        await Drain();
    }

    // ---- the other two layouts ----------------------------------------------

    /// <summary>
    /// The runtime tests above all measure the details row. These are the other
    /// two, and they are not decoration: measured by pointing either template's
    /// RenameBox hook at the opposite comparison, F2 on a compact row or a grid
    /// tile then draws a box that never takes the keyboard, so every keystroke
    /// goes to the listing's type-ahead instead.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Compact)]
    [InlineData(ViewMode.Grid)]
    public async Task The_box_takes_the_keyboard_in_every_layout(ViewMode layout)
    {
        using var rig = await BuildAsync();

        rig.Pane.View = layout;
        Settle();
        rig.Window.UpdateLayout();
        Settle();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        Assert.True(box.IsFocused, $"the box on a {layout} row does not have the keyboard");
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal("report".Length, box.SelectionEnd);
    }

    /// <summary>
    /// **The box is the size of the NAME, not the size of the cell.** Measured
    /// with the sizing style's setters taken out one at a time: the theme's own
    /// padding renders the box 29 tall against a 16px line, and a TextBox
    /// stretches by default, which fills 36 of a compact row's 48. Both read as
    /// a field dropped into the listing rather than a name being typed.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details)]
    [InlineData(ViewMode.Compact)]
    [InlineData(ViewMode.Grid)]
    public async Task The_box_is_the_size_of_the_name_it_replaces(ViewMode layout)
    {
        using var rig = await BuildAsync();

        rig.Pane.View = layout;
        Settle();
        rig.Window.UpdateLayout();
        Settle();

        var label = rig.Window.GetVisualDescendants().OfType<TextBlock>()
                       .First(t => t.Text == "report.txt" && t.IsVisible);

        var line = label.Bounds.Height;

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        // The allowance is the box's own chrome: a 1px border and 1px of
        // padding at each edge, measured as 4 here, so six is slack and not a
        // second line's worth.
        Assert.True(box.Bounds.Height <= line + 6,
                    $"the box is {box.Bounds.Height} tall against a {line} line in {layout}");
    }

    /// <summary>
    /// **A pane carries its own type scale** — Ctrl+scroll — and the theme's
    /// default font size does not follow it. Measured at a pane scale of 1.6
    /// with the style's FontSize setter removed: the row drew the name at 22.4
    /// and the box typed it at 14, five pixels BELOW where it had been drawn,
    /// so pressing F2 shrank the name and dropped it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_types_at_the_size_the_row_draws()
    {
        using var rig = await BuildAsync();

        rig.Pane.FontScale = 1.6;
        Settle();
        rig.Window.UpdateLayout();
        Settle();

        var label = rig.Window.GetVisualDescendants().OfType<TextBlock>()
                       .First(t => t.Text == "report.txt" && t.IsVisible);

        var drawn = label.FontSize;
        var drawnAt = label.TranslatePoint(new Point(0, 0), rig.Window)!.Value;

        Assert.True(drawn > 14, $"the pane did not scale, so this proves nothing ({drawn})");

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        Assert.Equal(drawn, box.FontSize);

        // And in the same place: the typed name sits on the line the drawn one
        // sat on, rather than floating above it.
        var text = box.GetVisualDescendants().OfType<TextPresenter>().First();
        var typedAt = text.TranslatePoint(new Point(0, 0), rig.Window)!.Value;

        Assert.True(Math.Abs(typedAt.Y - drawnAt.Y) <= 1,
                    $"the name moved from y={drawnAt.Y} to y={typedAt.Y} when the box replaced it");
    }

    /// <summary>
    /// **The whole name is offered again on the way back in.** Press F2, click
    /// into the middle of the name, press Escape, press F2 again: the row keeps
    /// the same container, so the box is the same control with the caret still
    /// where it was left — and the text it is bound to has not changed, so
    /// nothing resets it. Without the line that puts the anchor back to zero,
    /// the second F2 offers a selection running from wherever the caret had
    /// been.
    /// </summary>
    [AvaloniaFact]
    public async Task Reopening_the_box_offers_the_whole_name_again()
    {
        using var rig = await BuildAsync();

        Begin(rig, "report.txt");

        var box = Assert.Single(Boxes(rig.Window));

        box.SelectionStart = 4;
        box.SelectionEnd = 4;
        Settle();

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Drain();

        Begin(rig, "report.txt");

        var again = Assert.Single(Boxes(rig.Window));

        Assert.Same(box, again);
        Assert.Equal(0, again.SelectionStart);
        Assert.Equal("report".Length, again.SelectionEnd);
    }

    /// <summary>
    /// The live check answers for the pane whose box is open and no other.
    ///
    /// **A CONSTRUCTION, and named as one.** Only the open box writes a pane's
    /// RenameText in the ordinary course of things, so the other side's is
    /// written here by hand — which is what makes the difference between
    /// "answered for the renaming pane" and "answered for whichever pane
    /// spoke" visible at all.
    /// </summary>
    [AvaloniaFact]
    public async Task The_other_tab_is_not_told_why_this_name_was_refused()
    {
        using var rig = await BuildAsync();

        // Not activated: the editor stays on the pane it opened on, and this
        // one is wired to the same handler the moment it is created.
        var other = Own(rig.Shell.Left.AddTab(rig.Root, activate: false));

        Assert.NotSame(rig.Pane, other);

        Begin(rig, "report.txt");

        Assert.Single(Boxes(rig.Window));

        other.RenameText = "..";
        Settle();

        Assert.Null(other.RenameRefusal);

        // The premise: the SAME text in the pane that is renaming does get an
        // answer, so this is the guard talking rather than a handler that
        // never ran.
        rig.Pane.RenameText = "..";
        Settle();

        Assert.NotNull(rig.Pane.RenameRefusal);
    }

    /// <summary>The visible listing for the active pane.</summary>
    private static ListBox Listing(Rig rig)
        => rig.Window.GetVisualDescendants().OfType<ListBox>()
              .First(l => l.IsVisible && ReferenceEquals(l.DataContext, rig.Pane));

    /// <summary>The reload a rename starts is not awaited, so a test has to
    /// wait the way the window does.</summary>
    private static async Task Drain()
    {
        for (var i = 0; i < 60; i++)
        {
            Settle();
            await Task.Delay(5);
        }

        Settle();
    }

    // ---- the comparison behind the box's visibility --------------------------

    private static bool Renaming(string path, string target)
        => (bool)FileConverters.Renaming.Convert(
               [path, target], typeof(bool), null,
               System.Globalization.CultureInfo.InvariantCulture)!;

    [Fact]
    public void The_row_being_renamed_is_the_one_whose_path_matches()
        => Assert.True(Renaming("/a/report.txt", "/a/report.txt"));

    [Fact]
    public void Any_other_row_is_not()
        => Assert.False(Renaming("/a/second.txt", "/a/report.txt"));

    /// <summary>
    /// **Nobody is not everybody.** <c>PathRules.Same("", "")</c> is true and an
    /// unrealized row carries an empty path, so without the length test every
    /// row in the listing would open a box the moment one was asked for — and
    /// the pane holds "" for "no rename in progress".
    /// </summary>
    [Fact]
    public void No_rename_in_progress_opens_no_box_anywhere()
    {
        Assert.False(Renaming("/a/report.txt", ""));
        Assert.False(Renaming("", ""));
    }

    [Fact]
    public void The_label_is_shown_exactly_when_the_box_is_not()
    {
        foreach (var (path, target) in new[]
                 {
                     ("/a/report.txt", "/a/report.txt"),
                     ("/a/second.txt", "/a/report.txt"),
                     ("/a/report.txt", ""),
                 })
        {
            var shown = (bool)FileConverters.NotRenaming.Convert(
                [path, target], typeof(bool), null,
                System.Globalization.CultureInfo.InvariantCulture)!;

            Assert.Equal(!Renaming(path, target), shown);
        }
    }
}
