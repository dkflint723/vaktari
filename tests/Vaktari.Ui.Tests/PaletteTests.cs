using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Input;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Any command by name: the box Ctrl+Shift+P opens.
///
/// **A hundred and twenty-five menu rows and seventy-five keys, and no way
/// to run one by its name.** These pin what the palette lists and prints, the
/// matching, and the two ends: Enter runs the highlighted command once the box
/// has closed, and Escape runs nothing.
///
/// **And the safety net the palette went round.** It had a table of its own,
/// and its Delete for good went straight to the pane's delete — deleting the
/// selection for good without the question Shift+Delete asks. It runs the
/// same commands the keys run now; the test that says so is here.
/// </summary>
public sealed class PaletteTests : OwnedViewModels
{
    private static readonly XNamespace Ax = "https://github.com/avaloniaui";
    private static readonly XNamespace In = "clr-namespace:Vaktari.Ui.Input";

    private readonly List<Window> _windows = [];
    private readonly SettingsState _settingsBefore = AppSettings.Current;
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

    private T Shown<T>(T window) where T : Window
    {
        _windows.Add(window);
        window.Show();
        Pump();

        return window;
    }

    // ---- what it lists -----------------------------------------------------------

    /// <summary>
    /// Each row prints the keys its command has in the keymap it was built
    /// from — the shipped ones, and a key somebody chose once they have. The
    /// keys beside each name are how the key gets learnt, so a row printing a
    /// key the command no longer answers to would be teaching a dead key.
    /// </summary>
    [AvaloniaFact]
    public void Every_row_prints_the_keys_its_command_has()
    {
        var rows = Palette.Rows(Keymap.Default);

        Assert.All(rows, row => Assert.Equal(Keymap.Default.Readable(row.Command.Id), row.Keys));
        Assert.True(rows.Count(r => r.Keys.Length > 0) >= 30, "the rule is looking at too few keys");

        var chosen = Keymap.From(new KeyboardSettings
        {
            Bindings = new(StringComparer.Ordinal) { ["SortBySize"] = ["Ctrl+Alt+S"] },
        });

        Assert.Equal("Ctrl+Alt+S", Palette.Rows(chosen).Single(r => r.Command.Id == "SortBySize").Keys);
    }

    /// <summary>
    /// Every command but two: the palette itself, and moving the keyboard
    /// between regions, which the palette taking the keyboard makes
    /// meaningless. Names unique, or two rows would read alike.
    /// </summary>
    [AvaloniaFact]
    public void Every_command_is_offered_but_the_palette_and_the_region_key()
    {
        var rows = Palette.Rows(Keymap.Default);
        var ids = rows.Select(r => r.Command.Id).ToList();

        Assert.DoesNotContain("ShowPalette", ids);
        Assert.DoesNotContain("NextRegion", ids);
        Assert.Contains("DeletePermanently", ids);
        Assert.Contains("SelectAll", ids);
        Assert.Equal(Commands.All.Count - 2, ids.Count);

        Assert.Equal(rows.Count, rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
        Assert.Equal(Palette.Rows(Keymap.Default).Count, Palette.Match("", Keymap.Default).Count);
        Assert.Empty(Palette.Match("no such command", Keymap.Default));

        var up = Palette.Match("up", Keymap.Default);

        Assert.Equal("Up one folder", up[0].Name);
        Assert.Contains(up, e => e.Name == "Duplicate tab");

        // Words in any order, and case does not matter. "closed" contains
        // "close", so Reopen closed tab is a match too — after Close tab,
        // which the table lists first.
        var close = Palette.Match("TAB close", Keymap.Default);

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

        Assert.Equal("New tab", Assert.IsType<PaletteRow>(palette.Matches.SelectedItem).Name);

        palette.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Pump();

        Assert.Equal("New window", Assert.IsType<PaletteRow>(palette.Matches.SelectedItem).Name);

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

    // ---- the safety net ----------------------------------------------------------

    /// <summary>
    /// **Delete for good from the palette asks first, the way Shift+Delete
    /// does.** The palette's own table sent it straight to the pane's delete —
    /// the command whose summary says the view must confirm first — so with
    /// "ask before deleting for good" on, the key asked and the palette did
    /// not. Now both run the window's PermanentlyDelete.
    ///
    /// A real file in a folder this test makes, a real window, and the real
    /// pick: the file still there and the question on screen is the answer,
    /// and nothing short of both would be.
    /// </summary>
    [AvaloniaFact]
    public async Task Delete_for_good_from_the_palette_asks_first_the_way_shift_delete_does()
    {
        UseSearch(PaneViewModel.Search);

        var folder = _scratch = Directory.CreateTempSubdirectory("vaktari-palette").FullName;
        var file = Path.Combine(folder, "keep-me.txt");

        File.WriteAllText(file, "not yet");

        var window = Shown(new MainWindow());

        // After the window, which applies the settings on disk when it opens.
        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { ConfirmPermanentDelete = true },
        });

        var pane = window.Shell.ActiveTab!;

        await pane.NavigateAsync(folder);
        await Until(() => pane.Entries.Any(e => e.Name == "keep-me.txt"));

        pane.SelectedEntry = pane.Entries.Single(e => e.Name == "keep-me.txt");
        Pump();

        window.Shell.ShowPaletteCommand.Execute(null);
        Pump();

        var palette = Assert.Single(window.OwnedWindows.OfType<PaletteWindow>());

        palette.Query.Text = "delete for good";
        Pump();

        Assert.Equal("DeletePermanently", Assert.IsType<PaletteRow>(palette.Matches.SelectedItem).Command.Id);

        palette.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => !window.OwnedWindows.OfType<PaletteWindow>().Any());

        Assert.True(File.Exists(file), "the palette deleted the file for good without asking");
        Assert.True(window.FindControl<Control>("PromptBar")?.IsVisible == true,
                    "nothing asked before deleting for good");
    }

    // ---- the routes --------------------------------------------------------------

    /// <summary>On its key, and on the view menu beside the sheet and the
    /// tour, printed with whatever key it has.</summary>
    [AvaloniaFact]
    public void The_key_and_the_view_menu_both_open_it()
    {
        Assert.Equal("ShowPalette",
                     Keymap.Default.Owner(new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift))?.Id);

        var row = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Ax + "Button")
            .Single(b => (string?)b.Attribute(In + "KeyHint.Content") == "Commands…");

        Assert.Equal("ShowPalette", (string?)row.Attribute(In + "KeyHint.Command"));
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

    /// <summary>A bounded wait on the condition itself, the shape
    /// ExpandableFoldersTests.Until measures.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Pump();
            await Task.Delay(5);
        }

        Pump();
    }
}
