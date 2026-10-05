using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **→ and ← never opened a folder in place.** The README's key table promised
/// them, and the window's bubble handler answered them — but the ListBox marks
/// both keys handled on the way up, so a focused folder row ignored them. The
/// existing tests called TurnExpansion directly, or read the handler's source,
/// and none pressed the key.
///
/// Every test here presses the real key into a real MainWindow. The guards
/// matter as much as the opening: a text box sitting over a selected folder
/// must keep its caret, which is what a naive tunnel arm would take away.
/// </summary>
public sealed class ArrowKeysOpenFoldersTests : OwnedViewModels
{
    private MainWindow? _window;
    private string? _root;

    public override void Dispose()
    {
        if (_window?.DataContext is ShellViewModel shell && shell.ActiveTab is { } tab)
        {
            tab.View = ViewMode.Details;
            if (tab.IsPathEditing) tab.IsPathEditing = false;
            if (tab.IsFilterVisible) tab.IsFilterVisible = false;
            Dispatcher.UIThread.RunJobs();
        }

        _window?.Close();

        if (_root is not null)
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir is not worth failing over */ }

        base.Dispose();
    }

    private async Task<(MainWindow Window, PaneViewModel Pane, FileEntry Folder)> Open()
    {
        UseSearch(PaneViewModel.Search);

        _root = Directory.CreateTempSubdirectory("vaktari-arrows-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "kid.txt"), "kid");
        File.WriteAllText(Path.Combine(_root, "a-file.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b-file.txt"), "b");

        var window = _window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        if (shell.IsSplit) shell.ToggleSplit();

        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Details;

        await pane.NavigateAsync(_root);
        await Settle(window);

        var folder = pane.DetailsEntries.Single(e => e.Name == "docs");

        Assert.True(pane.CanExpandRows, "this listing draws no triangles, so nothing here could open");

        return (window, pane, folder);
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Waits a little for an expansion to land, so a test that says
    /// "it did not open" is not reading the state before the read finished.</summary>
    private static async Task Wait(Window window, Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        await Settle(window);
    }

    private static ListBox Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants().OfType<ListBox>()
                 .First(l => ReferenceEquals(l.DataContext, pane)
                             && l.IsEffectivelyVisible
                             && l.ItemsSource is not null
                             && l.GetVisualDescendants().OfType<ListBoxItem>().Any());

    private static async Task FocusRow(Window window, PaneViewModel pane, FileEntry entry)
    {
        pane.SelectedEntry = entry;
        await Settle(window);

        var row = Listing(window, pane).ContainerFromItem(entry);

        Assert.NotNull(row);

        row!.Focus(NavigationMethod.Directional);
        await Settle(window);

        Assert.Same(row, window.FocusManager?.GetFocusedElement());
    }

    private static void Press(Window window, Key key)
        => window.KeyPress(key, RawInputModifiers.None,
                           key == Key.Right ? PhysicalKey.ArrowRight : PhysicalKey.ArrowLeft, null);

    [AvaloniaFact]
    public async Task Right_and_Left_on_a_folder_row_open_and_close_it()
    {
        var (window, pane, folder) = await Open();

        await FocusRow(window, pane, folder);

        Press(window, Key.Right);
        await Wait(window, () => pane.IsExpanded(folder.FullPath));

        Assert.True(pane.IsExpanded(folder.FullPath), "→ on a focused folder row did not open it");
        Assert.Contains(pane.DetailsEntries, e => e.Name == "kid.txt");

        // Straight on, with nothing done in between: the keyboard has to be
        // back on the row by itself, or ← moves it to the sidebar instead.
        Assert.Equal(folder.FullPath, (window.FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext is FileEntry on ? on.FullPath : null);

        Press(window, Key.Left);
        await Wait(window, () => !pane.IsExpanded(folder.FullPath));

        Assert.False(pane.IsExpanded(folder.FullPath), "← on an open folder row did not close it");
    }

    /// <summary>A file row turns nothing, so the key goes on to the ListBox as
    /// it did before — nothing here claims a press that did nothing.</summary>
    [AvaloniaFact]
    public async Task Right_on_a_file_row_is_left_to_the_listing()
    {
        var (window, pane, _) = await Open();

        var file = pane.DetailsEntries.Single(e => e.Name == "a-file.txt");

        await FocusRow(window, pane, file);

        Press(window, Key.Right);
        await Settle(window);

        Assert.Equal(file, pane.SelectedEntry);
        Assert.False(pane.IsExpanded(Path.Combine(_root!, "docs")));
    }

    /// <summary>
    /// Twice: with the caret at the start, where the box moves it and claims
    /// the key, and at the END, where the box has nowhere to go and leaves the
    /// key unhandled — so it reaches the window's own handler, and only the
    /// rename guard there keeps it from the folder under the box.
    ///
    /// ← at the start is the same case from the other side: the box has
    /// nowhere to go, and the row used to take the keyboard and cancel.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Right, false)]
    [InlineData(Key.Right, true)]
    [InlineData(Key.Left, false)]
    public async Task Right_in_the_rename_box_moves_the_caret_and_opens_nothing(Key key, bool atEnd)
    {
        var (window, pane, folder) = await Open();

        await FocusRow(window, pane, folder);

        pane.BeginRenameCommand.Execute(null);
        await Settle(window);

        var box = window.GetVisualDescendants().OfType<TextBox>()
                        .Single(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);

        Assert.True(box.IsFocused, "the name is not being typed anywhere, so this proves nothing");

        var caret = atEnd ? box.Text!.Length : 0;

        box.SelectionStart = box.SelectionEnd = caret;
        box.CaretIndex = caret;

        Press(window, key);
        await Wait(window, () => pane.IsExpanded(folder.FullPath));

        Assert.False(pane.IsExpanded(folder.FullPath), "→ in the rename box opened the folder");
        Assert.Equal(key == Key.Left || atEnd ? caret : 1, box.CaretIndex);
        Assert.True(box.IsFocused, "the press took the keyboard out of the rename box");
        Assert.Equal(folder.FullPath, pane.RenamingPath);
    }

    public enum Box { Path, Filter, Search, Prompt }

    /// <summary>
    /// The boxes above the listing, each with a folder selected below it. A
    /// press in any of them belongs to its caret.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Box.Path, false)]
    [InlineData(Box.Filter, false)]
    [InlineData(Box.Search, false)]
    [InlineData(Box.Prompt, false)]
    [InlineData(Box.Path, true)]
    [InlineData(Box.Filter, true)]
    [InlineData(Box.Search, true)]
    [InlineData(Box.Prompt, true)]
    public async Task Right_in_a_text_box_over_a_selected_folder_opens_nothing(Box which, bool atEnd)
    {
        var (window, pane, folder) = await Open();

        pane.SelectedEntry = folder;

        switch (which)
        {
            case Box.Path: pane.IsPathEditing = true; break;
            case Box.Filter: pane.IsFilterVisible = true; break;
            case Box.Search: pane.IsSearchOpen = true; break;
            case Box.Prompt: ((ShellViewModel)window.DataContext!).ConnectCommand.Execute(null); break;
        }

        await Settle(window);

        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();

        var box = which switch
        {
            Box.Path => boxes.Single(t => t.Name == "PathBox" && t.IsEffectivelyVisible),
            Box.Filter => boxes.Single(t => t.PlaceholderText == "Filter — esc to clear" && t.IsEffectivelyVisible),
            Box.Search => boxes.Single(t => t.PlaceholderText == "Search files" && t.IsEffectivelyVisible),
            _ => window.FindControl<TextBox>("PromptInput")!,
        };

        box.Text = "abcdef";
        box.Focus(NavigationMethod.Tab);
        await Settle(window);

        Assert.Same(box, window.FocusManager?.GetFocusedElement());

        // At the end the box has nowhere to move the caret and leaves the key
        // unhandled, so it reaches the window: the text-box guard's case.
        var caret = atEnd ? 6 : 0;

        box.SelectionStart = box.SelectionEnd = caret;
        box.CaretIndex = caret;

        Assert.Equal(folder, pane.SelectedEntry);

        Press(window, Key.Right);
        await Wait(window, () => pane.IsExpanded(folder.FullPath));

        Assert.False(pane.IsExpanded(folder.FullPath), $"→ in the {which} box opened the selected folder");
        Assert.Equal(atEnd ? 6 : 1, box.CaretIndex);
    }

    [AvaloniaFact]
    public async Task Right_on_a_focused_heading_opens_nothing()
    {
        var (window, pane, folder) = await Open();

        pane.SelectedEntry = folder;
        await Settle(window);

        var heading = window.GetVisualDescendants().OfType<Button>()
                            .First(b => b.CommandParameter as string == "name"
                                        && ReferenceEquals(b.DataContext, pane)
                                        && b.IsEffectivelyVisible);

        heading.Focus(NavigationMethod.Tab);
        await Settle(window);

        Assert.Same(heading, window.FocusManager?.GetFocusedElement());

        Press(window, Key.Right);
        await Wait(window, () => pane.IsExpanded(folder.FullPath));

        Assert.False(pane.IsExpanded(folder.FullPath), "→ on a sort heading opened the selected folder");
    }

    /// <summary>The grid has no triangles, and its arrows move sideways.</summary>
    [AvaloniaFact]
    public async Task Right_in_the_grid_still_moves_the_selection()
    {
        var (window, pane, folder) = await Open();

        pane.View = ViewMode.Grid;
        await Settle(window);

        await FocusRow(window, pane, folder);

        Press(window, Key.Right);
        await Settle(window);

        Assert.False(pane.IsExpanded(folder.FullPath));
        Assert.NotNull(pane.SelectedEntry);
        Assert.NotEqual(folder, pane.SelectedEntry);
    }

    /// <summary>
    /// **↑ and ↓ in the rename box keep the name being typed**, in every
    /// layout: a one-line box has no use for them, and the listing took them
    /// to move the selection, which took the keyboard out of the box and
    /// cancelled the rename.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details, Key.Up)]
    [InlineData(ViewMode.Details, Key.Down)]
    [InlineData(ViewMode.Grid, Key.Up)]
    [InlineData(ViewMode.Grid, Key.Down)]
    [InlineData(ViewMode.Compact, Key.Up)]
    [InlineData(ViewMode.Compact, Key.Down)]
    public async Task Up_and_Down_in_the_rename_box_keep_the_name(ViewMode view, Key key)
    {
        var (window, pane, _) = await Open();

        pane.View = view;
        await Settle(window);

        var file = pane.Entries.Single(e => e.Name == "a-file.txt");

        await FocusRow(window, pane, file);

        pane.BeginRenameCommand.Execute(null);
        await Settle(window);

        var box = window.GetVisualDescendants().OfType<TextBox>()
                        .Single(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);

        Assert.True(box.IsFocused, "the name is not being typed anywhere, so this proves nothing");

        window.KeyPress(key, RawInputModifiers.None, key == Key.Up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown, null);
        await Settle(window);

        Assert.True(box.IsFocused, $"{key} took the keyboard out of the rename box");
        Assert.Equal(file.FullPath, pane.RenamingPath);
    }

    /// <summary>
    /// **A row chosen while the folder was being read keeps the keyboard.**
    /// The keyboard goes back to the opened folder's row only if the selection
    /// is still there: moved on meanwhile — by an arrow pressed straight after
    /// →, or as here from code before the read lands — the keyboard stays
    /// with the selection, or the two are left apart (QA: selection on a
    /// file, keyboard on the folder). Deterministic where a second key press
    /// is a race with the disk.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_chosen_while_the_folder_opens_keeps_the_keyboard()
    {
        var (window, pane, folder) = await Open();

        await FocusRow(window, pane, folder);

        Press(window, Key.Right);

        // Before the dispatcher runs again, so before the toggle's own finish.
        var file = pane.DetailsEntries.Single(e => e.Name == "b-file.txt");

        pane.SelectedEntry = file;
        Listing(window, pane).ContainerFromItem(file)!.Focus(NavigationMethod.Directional);

        await Wait(window, () => pane.IsExpanded(folder.FullPath));
        await Settle(window);

        var focused = window.FocusManager?.GetFocusedElement() as ListBoxItem;

        Assert.Equal(file, pane.SelectedEntry);
        Assert.Equal(file.FullPath, (focused?.DataContext as FileEntry?)?.FullPath);
    }
}