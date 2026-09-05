using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui;
using Vaktari.Ui.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The right-click menu on the address bar: copy the address, edit the address.
///
/// **The bar answered a double-click and nothing else.** Right-clicking it —
/// on a crumb, or on the empty strip past them — reached no menu at all, so
/// getting the path out of the window meant opening the editor, selecting the
/// text and pressing Ctrl+C, and the only notice anywhere that the editor
/// existed was a tooltip that appears after 1200ms. Explorer and Dolphin both
/// hang Copy address and Edit address off the bar's own context menu.
/// </summary>
public sealed class AddressBarMenuTests : OwnedViewModels
{
    private readonly List<Action> _restore = [];

    public override void Dispose()
    {
        foreach (var undo in _restore) undo();

        _restore.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the fakes ---------------------------------------------------------

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

    /// <summary>Keeps whatever text it was handed, and can refuse or throw.</summary>
    private sealed class Pocket : IClipboardService
    {
        public string? Text { get; private set; }
        public bool Refuses { get; init; }
        public bool Throws { get; init; }

        public Task<bool> SetTextAsync(string text)
        {
            if (Throws) throw new InvalidOperationException("the clipboard is busy");

            Text = text;

            return Task.FromResult(!Refuses);
        }

        public Task<bool> HasFilesAsync() => Task.FromResult(false);

        public Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
            => Task.FromResult(true);

        public Task<ClipboardPayload?> GetFilesAsync()
            => Task.FromResult<ClipboardPayload?>(null);
    }

    private PaneViewModel Pane(IClipboardService? clipboard, string at)
    {
        var pane = Own(new PaneViewModel(new Inert(), clipboard: clipboard) { ViewportWidth = 1400 });

        pane.CurrentPath = at;

        return pane;
    }

    private static string Folder => Path.Combine(Path.GetTempPath(), "vaktari-address-menu");

    // ---- what the row copies -----------------------------------------------

    /// <summary>
    /// The whole finding, from the view model's side: the address the bar is
    /// showing, on the clipboard, in one gesture.
    /// </summary>
    [AvaloniaFact]
    public async Task Copy_address_puts_the_folder_on_the_clipboard()
    {
        var pocket = new Pocket();

        await Pane(pocket, Folder).CopyAddressAsync();

        Assert.Equal(Folder, pocket.Text);
    }

    /// <summary>
    /// **A virtual listing's CurrentPath is an internal scheme.** The tooltip
    /// on this very bar already refuses to show it — showing "vaktari:computer"
    /// to anyone who hovered was its own finding — and a menu row that put it
    /// on the CLIPBOARD would be the same leak with somewhere to go afterwards.
    /// </summary>
    [AvaloniaFact]
    public async Task This_PC_copies_its_name_rather_than_its_scheme()
    {
        var pocket = new Pocket();

        await Pane(pocket, VirtualPaths.Computer).CopyAddressAsync();

        Assert.Equal(Vaktari.Core.Naming.ComputerTitle, pocket.Text);
        Assert.DoesNotContain("vaktari:", pocket.Text!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the four labels that are NOT addresses, of which the bin is one.
    ///
    /// **Only This PC's name pastes back**, because NavigateToPathText matches
    /// the machine by its title and matches nothing else: the bin, both Recent
    /// listings and a search all copy the name the bar is showing, and typing
    /// that name back into the box goes looking for a folder called "Recycle
    /// Bin". That is the deliberate half of the trade — the alternative on the
    /// clipboard is "vaktari:trash", which is neither readable nor an address
    /// either, and is a leak of an internal scheme on top.
    ///
    /// Here rather than left to the This PC case alone, so the class covers a
    /// listing where the round trip does not work and says out loud what it
    /// does instead.
    /// </summary>
    [AvaloniaFact]
    public async Task The_bin_copies_the_name_the_bar_is_showing()
    {
        var pocket = new Pocket();

        await Pane(pocket, VirtualPaths.Trash).CopyAddressAsync();

        Assert.Equal(Vaktari.Core.Naming.BinTitle, pocket.Text);
        Assert.DoesNotContain("vaktari:", pocket.Text!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the name is not merely readable, it is the string the box it came
    /// from understands: NavigateToPathText matches the machine by its title,
    /// so This PC's address pastes back into the address bar and goes there.
    ///
    /// The pane is moved away first. Navigating to where the pane already is
    /// returns early, so a round trip that never left would pass with the
    /// clipboard empty.
    /// </summary>
    [AvaloniaFact]
    public async Task What_This_PC_copies_pastes_back_into_the_box()
    {
        var pocket = new Pocket();
        var pane = Pane(pocket, VirtualPaths.Computer);

        await pane.CopyAddressAsync();

        pane.CurrentPath = Folder;

        pane.BeginEditPath();
        pane.PathText = pocket.Text!;

        await pane.NavigateToPathText();

        Assert.Equal(VirtualPaths.Computer, pane.CurrentPath);
    }

    /// <summary>
    /// A copy that worked says what it copied, on the line that reports
    /// everything else this pane does.
    /// </summary>
    [AvaloniaFact]
    public async Task A_copy_that_worked_says_so()
    {
        var pane = Pane(new Pocket(), Folder);

        await pane.CopyAddressAsync();

        Assert.Equal($"copied {Folder}", pane.Status);
    }

    /// <summary>
    /// **A menu row that quietly does nothing is the failure this application
    /// keeps finding.** A clipboard that refused the write, one that was never
    /// injected, and one that threw are three ways to end up with an unchanged
    /// clipboard and no explanation; each of them reaches the status line.
    /// </summary>
    [AvaloniaFact]
    public async Task A_clipboard_that_refuses_the_write_says_so()
    {
        var pane = Pane(new Pocket { Refuses = true }, Folder);

        await pane.CopyAddressAsync();

        Assert.Equal("clipboard unavailable", pane.Status);
    }

    [AvaloniaFact]
    public async Task A_pane_with_no_clipboard_says_so()
    {
        var pane = Pane(null, Folder);

        await pane.CopyAddressAsync();

        Assert.Equal("clipboard unavailable", pane.Status);
    }

    /// <summary>
    /// A pane that has not been anywhere yet has no address. CurrentPath starts
    /// as the empty string, so this is the state a second pane is in between
    /// being constructed and being navigated.
    ///
    /// It gets its own answer rather than falling through to the write: the
    /// service refuses empty text, so without the guard the status line would
    /// have said "clipboard unavailable" about a clipboard that was working
    /// perfectly — the wrong thing to go and check.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pane_that_has_been_nowhere_has_no_address_to_copy()
    {
        var pocket = new Pocket();
        var pane = Own(new PaneViewModel(new Inert(), clipboard: pocket) { ViewportWidth = 1400 });

        await pane.CopyAddressAsync();

        Assert.Equal("there is no address to copy", pane.Status);
        Assert.Null(pocket.Text);
    }

    [AvaloniaFact]
    public async Task A_clipboard_that_throws_reaches_the_status_line()
    {
        var pane = Pane(new Pocket { Throws = true }, Folder);

        await pane.CopyAddressAsync();

        Assert.StartsWith("copy failed:", pane.Status, StringComparison.Ordinal);
    }

    // ---- the service the pane writes through -------------------------------

    /// <summary>
    /// The pane had no route to the text clipboard at all before this: the
    /// service it owns wrote file lists only, and every text copy in the
    /// application went out through the shell's CopyTextRequested to the
    /// window. The bar belongs to a pane — one per split half, bound to its
    /// group's own ActiveTab — so it writes through the clipboard that pane
    /// already owns rather than asking the window which pane is current.
    /// </summary>
    [AvaloniaFact]
    public async Task The_service_really_writes_to_the_windows_clipboard()
    {
        var window = Shown();

        Assert.True(await ClipboardService.ForWindow(window).SetTextAsync(Folder));

        Assert.Equal(Folder, await window.Clipboard!.TryGetTextAsync());
    }

    /// <summary>
    /// Nothing to write to is reported rather than thrown: a window being torn
    /// down has no TopLevel, and a menu row is not a place for an exception.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_window_the_write_reports_that_it_failed()
        => Assert.False(await new ClipboardService(() => null).SetTextAsync(Folder));

    /// <summary>
    /// Empty text leaves the clipboard alone. The caller with nothing to say
    /// wants "nothing was copied" reported; emptying the clipboard instead
    /// would throw away whatever was in it and say the copy worked.
    /// </summary>
    [AvaloniaFact]
    public async Task Empty_text_is_refused_rather_than_written()
    {
        var window = Shown();
        var service = ClipboardService.ForWindow(window);

        await service.SetTextAsync("kept");

        Assert.False(await service.SetTextAsync(""));
        Assert.Equal("kept", await window.Clipboard!.TryGetTextAsync());
    }

    private Window Shown()
    {
        var window = new Window();

        window.Show();
        _restore.Add(window.Close);

        return window;
    }

    // ---- the real window ---------------------------------------------------

    /// <summary>
    /// **The rows and the wiring only exist inside a flyout nobody opens until
    /// somebody right-clicks.** A Command binding that resolves to nothing is a
    /// logged warning in Avalonia rather than an exception, so the menu would
    /// draw, the rows would read correctly, and picking one would do nothing at
    /// all — which is the exact failure this finding is about.
    ///
    /// So: the real window, the real bar, the menu really opened by a
    /// right-click on a crumb, and the commands the realized rows really hold.
    /// </summary>
    [AvaloniaFact]
    public async Task Right_clicking_a_crumb_opens_the_bars_own_menu()
    {
        var (window, _) = await RealWindow();
        var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

        var menu = BarMenu(window);

        Crumb(window).RaiseEvent(new ContextRequestedEventArgs());

        var rows = await Rows(window, menu);

        Assert.Equal(
            ["Copy address", "Edit address"],
            rows.Select(r => MenuLabels.Plain(r.Header?.ToString())));

        // The rows carry the pane's own commands. Which pane that is, when
        // there are two, is what
        // The_quiet_half_of_a_split_answers_for_itself measures — this window
        // has one group, so the shell's ActiveTab and the group's are the same
        // object and it cannot tell the two bindings apart.
        Assert.Same(pane.CopyAddressCommand, rows[0].Command);
        Assert.Same(pane.BeginEditPathCommand, rows[1].Command);
    }

    /// <summary>
    /// WHERE the menu hangs, in both directions.
    ///
    /// <see cref="BarMenu"/> walks up from a crumb and takes the first ancestor
    /// carrying one, so every right-click test above passes just as well with
    /// the flyout moved anywhere above the bar — and one step above the bar is
    /// the 40px toolbar Border it shares with Back, Forward, the search field
    /// and the filter chip, where "Copy address" would answer a right-click on
    /// all four. One step below it is the crumbs' own DockPanel, where the
    /// empty strip past the crumbs reaches nothing.
    ///
    /// So this names the owner rather than accepting any ancestor: the control
    /// carrying the menu is the one the double-click target and the crumbs both
    /// hang directly off, which is the only control that is the whole bar and
    /// nothing else.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_hangs_on_the_one_control_that_is_the_whole_bar()
    {
        var (window, _) = await RealWindow();

        Visual owner = MenuOwner(window);

        Assert.Same(owner, DoubleClickTarget(window).GetVisualParent());
        Assert.Same(owner, Crumb(window).FindAncestorOfType<DockPanel>()!.GetVisualParent());
    }

    /// <summary>
    /// **The bar being right-clicked belongs to a pane, and on a split there
    /// are two.** The rows bind to the GROUP's ActiveTab — this template's own
    /// DataContext — rather than reaching out through $parent[Window] to the
    /// shell's, so the menu acts on the bar it was opened over.
    ///
    /// Measured on the half that is NOT active, because that is the only place
    /// the two bindings give different answers: with the left group activated,
    /// a reach-out for the shell's ActiveTab would copy the LEFT pane's
    /// address out of the right pane's menu.
    /// </summary>
    [AvaloniaFact]
    public async Task The_quiet_half_of_a_split_answers_for_itself()
    {
        var (window, root) = await RealWindow();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        shell.ToggleSplit();

        await shell.Right!.ActiveTab!.NavigateAsync(Path.Combine(root, "there"));

        // The LEFT half is current; the right one is the quiet half whose bar
        // is about to be right-clicked.
        shell.ActivateGroup(shell.Left);

        await Settled(window);

        var quiet = shell.Right!.ActiveTab!;

        Assert.NotSame(quiet, shell.ActiveTab);

        var crumb = Crumb(window, shell.Right!);
        var menu = MenuAbove(crumb);

        crumb.RaiseEvent(new ContextRequestedEventArgs());

        var rows = await Rows(window, menu);

        Assert.Same(quiet.CopyAddressCommand, rows[0].Command);
        Assert.Same(quiet.BeginEditPathCommand, rows[1].Command);
    }

    /// <summary>
    /// The other half of the bar. The crumbs are an ItemsControl and the strip
    /// past them is the double-click Border behind them, so a menu on either
    /// one alone answers a right-click on half the bar — which is the half a
    /// person aims at when the path is short.
    /// </summary>
    [AvaloniaFact]
    public async Task Right_clicking_past_the_crumbs_opens_the_same_menu()
    {
        var (window, _) = await RealWindow();

        var menu = BarMenu(window);

        DoubleClickTarget(window).RaiseEvent(new ContextRequestedEventArgs());

        Assert.Equal(2, (await Rows(window, menu)).Count);
    }

    /// <summary>
    /// GUARD. **Measured, and the reason the menu can sit on the whole bar:** a
    /// TextBox carries a ContextFlyout from the theme, and a flyout that opens
    /// marks ContextRequested handled — so the editor's own Cut/Copy/Paste
    /// answers the right-click while the box is open, and the bar's menu never
    /// fires over it. Without that, "Copy address" would have covered the one
    /// control in the window where copying means copying the selected text.
    ///
    /// A guard because no mutation in this repository turns it red: the box's
    /// flyout comes from the theme, and moving the bar's menu — up onto the
    /// toolbar, down into the crumbs' DockPanel — leaves both assertions green,
    /// measured, because the box is not under the menu either way. What it
    /// would catch is an Avalonia upgrade in which the theme's flyout stops
    /// claiming the event, which is the sentence above ceasing to be true.
    /// </summary>
    [AvaloniaFact]
    public async Task The_open_editor_keeps_its_own_menu()
    {
        var (window, _) = await RealWindow();
        var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

        pane.BeginEditPath();

        await Settled(window);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PathBox");

        Assert.True(box.IsVisible, "the editor is not the thing being right-clicked");

        var menu = BarMenu(window);

        box.RaiseEvent(new ContextRequestedEventArgs());

        await Settled(window);

        Assert.True(box.ContextFlyout?.IsOpen,
                    "the box's own Cut/Copy/Paste did not answer the right-click");

        Assert.False(menu.IsOpen, "the bar's menu opened over the text being edited");
    }

    /// <summary>
    /// End to end, through everything the finding touches: the row in the real
    /// menu, the pane's command, the service the window built, and the
    /// clipboard the desktop hands back.
    /// </summary>
    [AvaloniaFact]
    public async Task Picking_copy_address_puts_the_folder_on_the_real_clipboard()
    {
        var (window, root) = await RealWindow();

        var menu = BarMenu(window);

        Crumb(window).RaiseEvent(new ContextRequestedEventArgs());

        var rows = await Rows(window, menu);
        var copy = rows[0];

        copy.Command!.Execute(copy.CommandParameter);

        Assert.Equal(Path.Combine(root, "here"), await Copied(window));
    }

    /// <summary>
    /// The other row, picked the way a person picks it — through the menu,
    /// which CLOSES on the way.
    ///
    /// **Edit address is the one row whose effect has to survive the flyout
    /// closing.** It makes the path box visible, and the box reverts its edit
    /// and hides itself the moment focus leaves it; the crumb held the keyboard
    /// before the menu opened, and a flyout hands the keyboard back to whatever
    /// held it. If that landed after the box was focused, the editor would
    /// collapse back to breadcrumbs the instant it appeared — and nothing else
    /// in this class closes a menu at all, so nothing else could see it.
    ///
    /// Measured on Avalonia 12.1, and it does not: the crumb goes invisible
    /// with the rest of the breadcrumb bar as soon as IsPathEditing goes true,
    /// so the close has nothing to hand the keyboard back to, and
    /// FocusBehavior.FocusOnVisible has the box.
    /// </summary>
    [AvaloniaFact]
    public async Task Picking_edit_address_leaves_the_editor_open_and_focused()
    {
        var (window, _) = await RealWindow();
        var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

        var crumb = Crumb(window);
        var menu = MenuAbove(crumb);

        // The crumb really holds the keyboard first. Without this the flyout
        // has nothing to restore focus to and the hazard is not present.
        crumb.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(crumb, window.FocusManager?.GetFocusedElement());

        crumb.RaiseEvent(new ContextRequestedEventArgs());

        var rows = await Rows(window, menu);

        Assert.Equal("Edit address", MenuLabels.Plain(rows[1].Header?.ToString()));

        Pick(window, "E");

        Assert.False(menu.IsOpen,
                     "the menu did not close, so the focus restore never happened");

        var box = await Editor(window);

        // And it is still there once everything the close posted has run. The
        // revert is posted at Background priority from FocusBehavior, so the
        // damage — if there were any — lands after the box first has focus.
        await Settled(window);
        await Settled(window);

        Assert.True(pane.IsPathEditing, "the editor closed again on its own");
        Assert.True(box.IsFocused, "the editor is open but the keyboard is elsewhere");
    }

    /// <summary>
    /// The keyboard route to the same menu.
    ///
    /// **A crumb is an ordinary focusable Button, and Menu / Shift+F10 opened
    /// the LISTING's menu from it** — Cut, Copy, Delete and the rest, offered
    /// for files that are not what the keyboard is pointing at. The two rows
    /// this finding adds carry access keys, and without this they were
    /// reachable only after a mouse right-click.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_key_on_a_crumb_opens_the_bars_menu()
    {
        var (window, _) = await RealWindow();

        var crumb = Crumb(window);
        var menu = MenuAbove(crumb);

        crumb.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(crumb, window.FocusManager?.GetFocusedElement());

        window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);

        var rows = await Rows(window, menu);

        Assert.Equal(
            ["Copy address", "Edit address"],
            rows.Select(r => MenuLabels.Plain(r.Header?.ToString())));

        Assert.Null(ListingMenu(window));
    }

    // ---- driving the real window -------------------------------------------

    /// <summary>
    /// A real MainWindow showing &lt;root&gt;/here, with a second folder
    /// &lt;root&gt;/there for the split to show on its other side.
    ///
    /// The window and the folder are torn down by <see cref="Dispose"/>, and
    /// the search backend is borrowed and given back: the constructor assigns
    /// the platform's own, and a later test that loads a search listing would
    /// otherwise walk the machine for real.
    /// </summary>
    private async Task<(MainWindow Window, string Root)> RealWindow()
    {
        UseSearch(PaneViewModel.Search);

        var places = PaneViewModel.Places;

        _restore.Add(() => PaneViewModel.Places = places);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-address-menu-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "here"));
        Directory.CreateDirectory(Path.Combine(root, "there"));

        await NoSessionAsync();

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        _restore.Add(() =>
        {
            var services = window.Services;

            window.Close();

            // **Close() only STARTS the teardown**, and the release it runs
            // writes session.json — which the NEXT window in this class then
            // restores. NewWindowTests measured that write landing a whole test
            // late; here it would mean a test opening the split window the test
            // above it left behind, with two address bars and the wrong one
            // first in the tree. Waited on the list of live windows emptying,
            // which is what the release does last, under a wall-clock ceiling.
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (services.Windows.Count > 0 && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            try { Directory.Delete(root, true); }
            catch (IOException ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        await shell.ActiveTab!.NavigateAsync(Path.Combine(root, "here"));
        await Settled(window);

        return (window, root);
    }

    /// <summary>
    /// An empty session for the window about to be built.
    ///
    /// **A MainWindow restores whatever the last one wrote**, and one test in
    /// this class splits the window. Without this, the test after it opens a
    /// SPLIT window: two address bars, two sets of crumbs, and the first crumb
    /// in the tree belonging to a half that is not the shell's active tab —
    /// which is what half the assertions here compare against. The state
    /// directory is per test class, so one write reaches every test below it.
    /// </summary>
    private static async Task NoSessionAsync()
    {
        var directory = TestState.Current();

        Directory.CreateDirectory(directory);

        var store = new JsonSessionStore(directory);

        store.NotifyChanged(new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows = [],
        });

        await store.FlushAsync(CancellationToken.None);
        await store.DisposeAsync();
    }

    /// <summary>
    /// Runs the queue, then measures and arranges. A realized popup has to be
    /// laid out before anything in it exists to be read.
    /// </summary>
    private static async Task Settled(Window window)
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Waits for the assertion's own subject under a wall-clock ceiling — the
    /// rows of the menu that was asked to open — rather than for a fixed number
    /// of dispatcher turns, which is a proxy for it and the shape three CI
    /// flakes in this repository have taken.
    /// </summary>
    private static async Task<List<MenuItem>> Rows(Window window, MenuFlyout menu)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await Settled(window);

            if (Realized(menu) is { Count: > 0 } rows) return rows;
        }

        Assert.Fail(menu.IsOpen
            ? "the bar's menu opened and realized no rows"
            : "right-clicking the bar opened no menu at all");

        return [];
    }

    private static List<MenuItem> Realized(MenuFlyout menu)
        => menu.IsOpen && menu.Popup.Child is Visual popup
            ? [.. popup.GetVisualDescendants().OfType<MenuItem>()]
            : [];

    /// <summary>
    /// Picks the row whose access key this is, which is the one gesture
    /// reachable from a headless test that raises the row's Click AND closes
    /// the menu — the two halves the row above needs.
    ///
    /// WHY IT REACHES FOR A PRIVATE METHOD: the keystroke cannot be delivered
    /// from outside. An open flyout's visual root is a popup host, not an
    /// IInputRoot, so window.KeyPress never reaches the rows —
    /// ContextMenuKeysTests measured that and says so at length. What is
    /// reachable is Avalonia's own AccessKeyHandler.ProcessKey, given the key
    /// and the element the press came from, and picking a row is what it does:
    /// measured there, one press of a key exactly one row answers to raises
    /// that row's Click and closes the menu.
    ///
    /// Raising MenuItem.ClickEvent directly instead would NOT do: measured
    /// here, it runs the command and leaves the flyout open, so the focus
    /// restore this class needs to see never happens at all.
    /// </summary>
    private static void Pick(Window window, string key)
    {
        var handler = typeof(Window)
            .GetProperty("AccessKeyHandler",
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            !.GetValue(window)!;

        var processKey = handler.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "ProcessKey"
                         && m.GetParameters() is [{ ParameterType.Name: "String" },
                                                  { ParameterType: var second }]
                         && second == typeof(IInputElement));

        processKey.Invoke(handler, [key, window.FocusManager?.GetFocusedElement() ?? window]);

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The open, focused path box — waited for as itself under a wall-clock
    /// ceiling rather than for a count of dispatcher turns.
    /// </summary>
    private static async Task<TextBox> Editor(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await Settled(window);

            if (window.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(t => t.Name == "PathBox")
                is { IsVisible: true, IsFocused: true } box) return box;
        }

        Assert.Fail("the path box never opened with the keyboard in it");

        return new TextBox();
    }

    /// <summary>Whichever ContextMenu in the window is open, if any. The
    /// listing's is the only one these keys ever reached.</summary>
    private static ContextMenu? ListingMenu(Window window)
        => window.GetVisualDescendants()
            .OfType<Control>()
            .Select(c => c.ContextMenu)
            .FirstOrDefault(m => m is { IsOpen: true });

    /// <summary>What the clipboard holds, waited for as itself.</summary>
    private static async Task<string?> Copied(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();

            if (await window.Clipboard!.TryGetTextAsync() is { Length: > 0 } text) return text;

            await Task.Delay(1);
        }

        return await window.Clipboard!.TryGetTextAsync();
    }

    /// <summary>
    /// The menu the bar carries: found by walking UP from a crumb, because
    /// where it hangs is the point — ContextRequested bubbles, so only an
    /// ancestor of the crumbs answers a right-click on one.
    ///
    /// Which ancestor is deliberately NOT asked here, so that every test using
    /// this one still opens whatever menu the markup really hangs above the
    /// crumbs. <see cref="The_menu_hangs_on_the_one_control_that_is_the_whole_bar"/>
    /// is where that question is put, once, on its own.
    /// </summary>
    private static MenuFlyout BarMenu(Window window) => MenuAbove(Crumb(window));

    private static MenuFlyout MenuAbove(Visual from)
    {
        if (OwnerAbove(from) is { ContextFlyout: MenuFlyout menu }) return menu;

        Assert.Fail("nothing above the crumbs carries a context menu");

        return new MenuFlyout();
    }

    /// <summary>The control the bar's menu is hung on.</summary>
    private static Control MenuOwner(Window window)
        => OwnerAbove(Crumb(window)) ?? throw new InvalidOperationException("no menu above the crumbs");

    private static Control? OwnerAbove(Visual from)
    {
        for (Visual? visual = from; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { ContextFlyout: MenuFlyout } control)
                return control;

        return null;
    }

    /// <summary>One crumb's name button — the thing a person right-clicks.</summary>
    private static Button Crumb(Window window)
        => window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.DataContext is PathSegment && b.Flyout is null);

    /// <summary>
    /// A crumb on one half of a split, told apart by the group whose template
    /// it was realized inside — the DataContext every control in that half
    /// inherits.
    /// </summary>
    private static Button Crumb(Window window, PaneGroupViewModel group)
        => window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.DataContext is PathSegment
                        && b.Flyout is null
                        && Owns(group, b));

    private static bool Owns(PaneGroupViewModel group, Visual control)
    {
        for (Visual? visual = control; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: PaneGroupViewModel found })
                return ReferenceEquals(found, group);

        return false;
    }

    /// <summary>The transparent target that fills the bar behind the crumbs.
    /// It is what a right-click past the end of the path lands on.</summary>
    private static Border DoubleClickTarget(Window window)
        => window.GetVisualDescendants()
            .OfType<Border>()
            .First(b => DoubleClick.GetCommand(b) is not null);
}
