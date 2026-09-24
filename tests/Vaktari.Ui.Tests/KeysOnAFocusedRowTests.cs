using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The keys a listing row takes for itself, and the ones the window answers.
///
/// **Enter on a clicked row did nothing.** Avalonia 12's ListBoxItem has an
/// OnKeyDown of its own: Enter and Space select the row, and with Ctrl they
/// toggle it — and the item marks the key handled either way. One click puts
/// the keyboard on the ROW rather than on the ListBox, so the window's bubble
/// handler, which is where Enter opens the selection and Space opens the
/// preview, never saw either key. Enter worked only when the ListBox itself had
/// the keyboard, which a click never leaves it with.
///
/// Every test here clicks a row once and proves the row, not the ListBox, holds
/// the keyboard before a key goes in: the ListBox-focused case passed all
/// along, so a test that focused the ListBox would pass with the bug in.
/// </summary>
public sealed class KeysOnAFocusedRowTests : OwnedViewModels
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>Waits on the assertion's own subject under a wall-clock
    /// ceiling rather than on a count of dispatcher turns.</summary>
    private static async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            Settle();

            if (done()) return;

            await Task.Delay(5);
        }

        Settle();
        Assert.True(done(), what);
    }

    /// <summary>
    /// A real window on a temp folder holding one folder and one file, with the
    /// pane put back and the window closed on the way out — see
    /// SingleClickAffordanceTests' rig for why both halves matter.
    /// </summary>
    private sealed record Rig(
        MainWindow Window, ShellViewModel Shell, PaneViewModel Pane, string Root, string? Was,
        SettingsState SettingsBefore)
        : IAsyncDisposable
    {
        public string Folder => Path.Combine(Root, "adir");

        public string File => Path.Combine(Root, "afile.txt");

        public async ValueTask DisposeAsync()
        {
            if (Pane.IsPreviewVisible) Pane.TogglePreview();

            if (Was is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Pane.NavigateAsync(Was));
                Settle();
            }

            Pane.View = ViewMode.Details;

            AppSettings.Apply(SettingsBefore);

            var closed = false;

            Window.Closed += (_, _) => closed = true;
            Window.Close();

            await Until(() => closed, "the window never finished closing");

            try { Directory.Delete(Root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    private async Task<Rig> BuildAsync()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-rowkeys-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "adir"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "afile.txt"), "text");

        var settingsBefore = AppSettings.Current;
        var window = new MainWindow();

        window.Show();
        Settle();

        // **A click that only selects.** Pinned after construction, because the
        // window's startup re-reads the settings store over anything set before
        // it; and double rather than "whatever the desktop says", so the click
        // below cannot be the thing that opened the folder.
        var before = AppSettings.Current;

        AppSettings.Apply(before with
        {
            Navigation = before.Navigation with { OpenItemsWith = ActivationClick.Double },
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var was = shell.ActiveTab?.CurrentPath;

        shell.RefreshActivation();

        await shell.ActiveTab!.NavigateAsync(root);
        shell.ActiveTab.View = ViewMode.Details;

        Settle();
        window.UpdateLayout();
        Settle();

        Assert.Equal(2, shell.ActiveTab.Entries.Count);
        Assert.False(shell.ActiveTab.OpensOnSingleClick);

        return new Rig(window, shell, shell.ActiveTab, root, was, settingsBefore);
    }

    /// <summary>The realized row showing <paramref name="path"/> in the
    /// pane's own visible listing.</summary>
    private static ListBoxItem RowFor(Rig rig, string path)
        => Assert.Single(
            rig.Window.GetVisualDescendants().OfType<ListBox>()
               .Where(l => l.IsVisible && ReferenceEquals(l.DataContext, rig.Pane))
               .SelectMany(l => l.GetVisualDescendants().OfType<ListBoxItem>()),
            r => r.DataContext is FileEntry entry && entry.FullPath == path);

    /// <summary>
    /// Clicks a row once and proves the click left the keyboard on that row —
    /// the state the bug lived in, and the one a test that focused the ListBox
    /// by hand would never reach.
    /// </summary>
    private static async Task<ListBoxItem> ClickAsync(Rig rig, string path)
    {
        var row = RowFor(rig, path);
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), rig.Window);

        Assert.NotNull(at);

        rig.Window.MouseDown(at!.Value, MouseButton.Left);
        rig.Window.MouseUp(at.Value, MouseButton.Left);

        await Until(() => rig.Pane.SelectedEntry?.FullPath == path,
                    "the click never selected the row");

        Assert.Same(row, rig.Window.FocusManager?.GetFocusedElement());

        return row;
    }

    // ---- the keys the window answers -----------------------------------------

    [AvaloniaFact]
    public async Task Enter_on_a_clicked_folder_row_opens_the_folder()
    {
        await using var rig = await BuildAsync();

        await ClickAsync(rig, rig.Folder);

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await Until(() => rig.Pane.CurrentPath == rig.Folder,
                    "Enter on the focused row did not open the folder");
    }

    /// <summary>
    /// **Arrowing is the other way the keyboard lands on a row.** The ListBox
    /// moves focus to the row it selects, so a row reached by Down is in the
    /// same state as a clicked one.
    /// </summary>
    [AvaloniaFact]
    public async Task Enter_on_a_row_reached_by_arrow_opens_the_folder()
    {
        await using var rig = await BuildAsync();

        // The file sorts after the folder, so Up from the file lands on it.
        await ClickAsync(rig, rig.File);

        rig.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);

        await Until(() => rig.Pane.SelectedEntry?.FullPath == rig.Folder,
                    "Up never moved the selection to the folder");

        Assert.Same(RowFor(rig, rig.Folder), rig.Window.FocusManager?.GetFocusedElement());

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await Until(() => rig.Pane.CurrentPath == rig.Folder,
                    "Enter on the row Up reached did not open the folder");
    }

    /// <summary>
    /// Space is the preview's key, and the row claimed it exactly as it claimed
    /// Enter. Pressed on a file, so what Space would open is something a
    /// preview can show.
    /// </summary>
    [AvaloniaFact]
    public async Task Space_on_a_clicked_row_opens_the_preview()
    {
        await using var rig = await BuildAsync();

        Assert.False(rig.Pane.IsPreviewVisible, "the preview was already open");

        await ClickAsync(rig, rig.File);

        rig.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Settle();

        Assert.True(rig.Pane.IsPreviewVisible, "Space on the focused row did not open the preview");
    }

    // ---- and the keys the row keeps -------------------------------------------

    /// <summary>
    /// **Ctrl+Space is the row's, and stays the row's.** It toggles the focused
    /// row in and out of a multiple selection, which is how a keyboard picks
    /// files that are not next to each other; nothing in the window answers it,
    /// so a fix that took every Space away from the row would cost that.
    /// </summary>
    [AvaloniaFact]
    public async Task Ctrl_space_still_toggles_the_focused_row()
    {
        await using var rig = await BuildAsync();

        var row = await ClickAsync(rig, rig.Folder);

        Assert.True(row.IsSelected);

        rig.Window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        Settle();

        Assert.False(row.IsSelected, "Ctrl+Space no longer reaches the row it toggles");
        Assert.False(rig.Pane.IsPreviewVisible, "Ctrl+Space opened the preview as well");
    }
}
