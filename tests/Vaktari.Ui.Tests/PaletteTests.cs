using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Any command by name: the box Ctrl+Shift+P opens.
///
/// **A hundred and twenty-five menu rows and seventy-five keys, and no way
/// to run one by its name.** These pin the table — every key on it is a key
/// the F1 sheet lists, and every entry reaches a real command on a started
/// shell — the matching, and the two ends: Enter runs the highlighted entry
/// once the box has closed, and Escape runs nothing.
/// </summary>
public sealed class PaletteTests : OwnedViewModels
{
    private static readonly XNamespace Ax = "https://github.com/avaloniaui";

    private readonly List<Window> _windows = [];

    public override void Dispose()
    {
        foreach (var window in _windows) window.Close();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private T Shown<T>(T window) where T : Window
    {
        _windows.Add(window);
        window.Show();
        Pump();

        return window;
    }

    // ---- the table ---------------------------------------------------------------

    /// <summary>
    /// **A key printed beside a command that the sheet does not list is a
    /// key that has been renamed or removed.** The sheet is held to the
    /// markup by ShortcutListTests; this holds the palette to the sheet, by
    /// exact spelling. An empty key is an entry no key runs, which is
    /// allowed — it is what the palette is FOR.
    /// </summary>
    [AvaloniaFact]
    public void Every_key_the_palette_prints_is_a_key_on_the_sheet()
    {
        // A sheet line can carry two keys — "Ctrl++ / Ctrl+-" — and the
        // palette prints one per entry, so the sheet is read a key at a
        // time, the way ShortcutListTests reads it.
        var sheet = Shortcuts.All
            .SelectMany(g => g.Keys)
            .SelectMany(k => k.Keys.Split(" / ", StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

        var strangers = Palette.Entries
            .Where(e => e.Keys.Length > 0 && !sheet.Contains(e.Keys))
            .Select(e => $"{e.Name} ({e.Keys})")
            .ToList();

        Assert.True(strangers.Count == 0,
            "the palette prints keys the F1 sheet does not list: " + string.Join(", ", strangers));

        Assert.True(Palette.Entries.Count(e => e.Keys.Length > 0) >= 30, "the rule is looking at too few keys");
    }

    /// <summary>
    /// Every entry reaches a command on a shell with a tab open — a lambda
    /// that reaches for a command the pane does not have, or the shell's
    /// where the pane's was meant, answers null here and a dead row in the
    /// box. Names are unique too, or two rows would read alike.
    /// </summary>
    [AvaloniaFact]
    public void Every_entry_reaches_a_command_on_a_started_shell()
    {
        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Path.GetTempPath());

        var dead = Palette.Entries
            .Where(e => e.Command(shell) is null)
            .Select(e => e.Name)
            .ToList();

        Assert.True(dead.Count == 0, "these entries reach no command: " + string.Join(", ", dead));

        Assert.Equal(Palette.Entries.Count, Palette.Entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---- matching ----------------------------------------------------------------

    /// <summary>
    /// Every word of the query, in any order; a name that begins with the
    /// query first — "up" is Up one folder before it is Duplicate tab, since
    /// somebody typing a command's name types it from the front — and the
    /// table's own order within a tier, so the empty query reads as the
    /// menus do.
    /// </summary>
    [AvaloniaFact]
    public void Typing_narrows_the_list_and_a_name_that_starts_with_it_comes_first()
    {
        Assert.Equal(Palette.Entries.Count, Palette.Match("").Count);
        Assert.Empty(Palette.Match("no such command"));

        var up = Palette.Match("up");

        Assert.Equal("Up one folder", up[0].Name);
        Assert.Contains(up, e => e.Name == "Duplicate tab");

        // Words in any order, and case does not matter. "closed" contains
        // "close", so Reopen closed tab is a match too — after Close tab,
        // which the table lists first.
        var close = Palette.Match("TAB close");

        Assert.Equal("Close tab", close[0].Name);
        Assert.Contains(close, e => e.Name == "Reopen closed tab");
        Assert.DoesNotContain(close, e => e.Name == "New tab");
    }

    // ---- the box -----------------------------------------------------------------

    /// <summary>
    /// The box highlights the first match as the query is typed, so Enter
    /// straight after typing runs the best one; the arrows move the
    /// highlight and stop at the ends.
    /// </summary>
    [AvaloniaFact]
    public void The_first_match_is_highlighted_and_the_arrows_move_it()
    {
        var palette = Shown(new PaletteWindow());

        palette.Query.Text = "new";
        Pump();

        Assert.Equal("New tab", Assert.IsType<PaletteEntry>(palette.Matches.SelectedItem).Name);

        palette.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Pump();

        Assert.Equal("New window", Assert.IsType<PaletteEntry>(palette.Matches.SelectedItem).Name);

        palette.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        palette.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Pump();

        Assert.Equal(0, palette.Matches.SelectedIndex);
    }

    /// <summary>
    /// **Run after the box has closed, not from inside it.** Half the
    /// commands move the keyboard — into the address bar, a rename — and one
    /// run while the palette is still the active window puts the caret in a
    /// box the palette then takes the focus back from on its way out. The
    /// window reads the pick once the dialog has returned: here, Enter on
    /// "New tab" is a second tab in the window the box came from.
    /// </summary>
    [AvaloniaFact]
    public void Enter_runs_the_highlighted_command_in_the_window_the_box_came_from()
    {
        UseSearch(PaneViewModel.Search);

        var window = Shown(new MainWindow());
        var tabs = window.Shell.Left.Tabs.Count;

        window.Shell.ShowPaletteCommand.Execute(null);
        Pump();

        var palette = Assert.Single(window.OwnedWindows.OfType<PaletteWindow>());

        palette.Query.Text = "new tab";
        Pump();

        palette.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Pump();

        Assert.Empty(window.OwnedWindows.OfType<PaletteWindow>());
        Assert.Equal(tabs + 1, window.Shell.Left.Tabs.Count);
    }

    /// <summary>Escape closes the box and runs nothing — not even the
    /// highlighted entry, which is exactly what Enter would have run.</summary>
    [AvaloniaFact]
    public void Escape_closes_the_box_and_runs_nothing()
    {
        UseSearch(PaneViewModel.Search);

        var window = Shown(new MainWindow());
        var tabs = window.Shell.Left.Tabs.Count;

        window.Shell.ShowPaletteCommand.Execute(null);
        Pump();

        var palette = Assert.Single(window.OwnedWindows.OfType<PaletteWindow>());

        palette.Query.Text = "new tab";
        Pump();

        palette.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();

        Assert.Empty(window.OwnedWindows.OfType<PaletteWindow>());
        Assert.Null(palette.Chosen);
        Assert.Equal(tabs, window.Shell.Left.Tabs.Count);
    }

    // ---- the routes --------------------------------------------------------------

    /// <summary>On the key, and on the view menu beside the sheet and the
    /// tour, printed with its key the way the rows around it are.</summary>
    [AvaloniaFact]
    public void The_key_and_the_view_menu_both_open_it()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var binding = doc.Descendants(Ax + "KeyBinding")
                         .Single(k => (string?)k.Attribute("Gesture") == "Ctrl+Shift+P");

        Assert.Equal("{Binding ShowPaletteCommand}", (string?)binding.Attribute("Command"));

        var row = doc.Descendants(Ax + "Button")
                     .Single(b => ((string?)b.Attribute("Content") ?? "").StartsWith("Commands…", StringComparison.Ordinal));

        Assert.Contains("ctrl+shift+p", (string?)row.Attribute("Content"), StringComparison.Ordinal);
        Assert.Equal(
            "{Binding $parent[Window].((vm:ShellViewModel)DataContext).ShowPaletteCommand}",
            (string?)row.Attribute("Command"));
    }

    // ---- helpers -----------------------------------------------------------------

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private sealed class InertFileSystem : IFileSystemProvider
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
