using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the Menu key's menu knows when it opens.
///
/// **The keyboard's menu was the last right-click's menu.** Everything the
/// listing's menu re-reads as it opens (the scripts and templates folders, the
/// Undo label, the Paste row, and which Proton rows apply) was re-read in
/// OnListingMenuOpening, the handler for the menu's Opening event. Avalonia
/// raises Opening on the right-click route and not on ContextMenu.Open(control),
/// which is how OpenListingMenu opens it for the Menu key and Shift+F10.
/// Measured on 12.1.2 in a headless probe: Open raised Opening zero times. So a
/// script dropped into the scripts folder, which the menu itself invites, showed
/// up on the next right-click and never on the next Menu key.
///
/// A script rather than any of the others because it is the one a test can add
/// without a clipboard or an operation behind it, and its folder is the
/// platform's own: on Windows under the per-class state directory TestState
/// points the stores at, on Linux the XDG data folder. The file is named to be
/// unmistakable and removed afterwards either way.
/// </summary>
public sealed class ContextMenuRefreshTests : OwnedViewModels
{
    /// <summary>The same pump ContextMenuPlacementTests uses, for the same
    /// reason given there.</summary>
    private static async Task Layout(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    private static ListBox Listing(MainWindow window, PaneViewModel pane)
        => window.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(list => list.IsVisible
                            && ReferenceEquals(list.DataContext, pane)
                            && list.SelectionMode.HasFlag(SelectionMode.Multiple));

    private static ContextMenu ListingMenu(ListBox list)
    {
        for (var visual = (Visual?)list; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { ContextMenu: { } menu }) return menu;

        throw new InvalidOperationException("the listing has no context menu above it");
    }

    /// <summary>A row by name, however deep in the submenus it sits.</summary>
    private static MenuItem? Find(IEnumerable<object?> items, string name)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Name == name) return item;
            if (Find(item.Items, name) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>
    /// Puts a runnable script into the platform's scripts folder: a .cmd on
    /// Windows, which WindowsScriptRunner lists by extension, and a file with
    /// its execute bit set on Linux, which is LinuxScriptRunner's opt-in.
    /// </summary>
    private static string AddScript(IScriptRunner runner, string stem)
    {
        var path = Path.Combine(runner.ScriptsDirectory,
                                OperatingSystem.IsWindows() ? stem + ".cmd" : stem);

        Directory.CreateDirectory(runner.ScriptsDirectory);
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo off\r\n" : "#!/bin/sh\n");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                             | UnixFileMode.UserExecute);

        return path;
    }

    /// <summary>
    /// The finding: a script added after the window was built reaches the menu
    /// the Menu key opens.
    ///
    /// Asserted absent before the press as well, so that a pane which had read
    /// the folder for some other reason in between cannot make this pass.
    /// </summary>
    [AvaloniaFact]
    public async Task A_script_added_after_the_window_opened_is_on_the_menu_key_menu()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-menurefresh-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(root, $"file-{i}.txt"), "x");

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var was = shell.ActiveTab?.CurrentPath;

        var stem = "vaktari_menukey_" + Guid.NewGuid().ToString("N")[..8];
        var label = stem.Replace('_', ' ');
        string? script = null;

        try
        {
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            await Layout(window);

            script = AddScript(window.Services.Platform.Scripts, stem);

            Assert.Contains(window.Services.Platform.Scripts.Discover(), s => s.Name == label);
            Assert.DoesNotContain(pane.Scripts, s => s.Name == label);

            var list = Listing(window, pane);

            Assert.True(list.ItemCount >= 1, "the listing did not load, so this proves nothing");

            list.SelectedIndex = 0;
            Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0)).Focus();

            await Layout(window);

            var menu = ListingMenu(list);

            window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            await Layout(window);

            Assert.True(menu.IsOpen, "the Menu key did not open the listing's menu");

            var scripts = Find(menu.Items, "ScriptsMenu");

            Assert.NotNull(scripts);
            Assert.True(scripts.IsVisible, "the Scripts row is hidden, so the new script is not on it");
            Assert.Contains(scripts.Items.OfType<ScriptCommand>(), s => s.Name == label);

            menu.Close();
            await Layout(window);
        }
        finally
        {
            if (script is not null)
            {
                try { File.Delete(script); }
                catch (Exception) { /* a temp script is not worth failing over */ }
            }

            if (was is { } back && shell.ActiveTab is { } tab)
            {
                await tab.NavigateAsync(back);
                Dispatcher.UIThread.RunJobs();
            }

            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }
}
