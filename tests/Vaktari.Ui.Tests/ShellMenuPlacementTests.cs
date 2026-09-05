using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where the hosted "Windows menu" sits in the listing's right-click menu, and
/// what the rules around it do when it is not there.
///
/// **It was three groups too high: above Open file location and above
/// Properties.** Every other row in that menu is one of ours, with a command
/// and a gate written down in the markup; this one row hands the click to
/// whatever COM handlers the machine has registered, and it sat in the middle
/// of ours. Windows 11 puts the same row — "Show more options" — at the very
/// bottom, under Properties and behind a rule, and the reason is the reason it
/// is behind a hover at all: the last thing on the list is the thing that is
/// not the application's.
///
/// **Read off a built window, not out of the markup, because the order in the
/// file is not the order a person sees.** A row is in the ContextMenu whether
/// or not its gate lets it draw, so a markup test asserting that Properties is
/// the element before the hosted row would stay green in exactly the listings
/// where Properties is hidden and something else is above it. It also cannot
/// see a rule that has nothing between it and the next rule, which is what
/// moving the row down here first produced. Measured on Avalonia 12.1: with the
/// menu OPEN the direct children report the visibility their bindings gave
/// them — with it closed every one of them reads true, so these tests open it.
///
/// A shell menu provider is a static seam, set here to an inert one; nothing in
/// this file opens the submenu, so it is never asked for anything.
///
/// The assertions are on DIRECT children throughout. A row nested inside
/// another MenuItem is a hover away and would satisfy any test that merely
/// looked for the header somewhere in the menu.
/// </summary>
public sealed class ShellMenuPlacementTests : OwnedViewModels
{
    /// <summary>Enough of a provider to make HasShellMenu true. It is never
    /// asked: building happens on SubmenuOpened, which no test here raises.</summary>
    private sealed class Inert : IShellMenuProvider
    {
        public Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths)
            => Task.FromResult<IShellMenu?>(null);

        public Task<IShellMenu?> BuildBackgroundAsync(string folder)
            => Task.FromResult<IShellMenu?>(null);
    }

    private readonly IShellMenuProvider? _shellMenuBefore = PaneViewModel.ShellMenu;

    public ShellMenuPlacementTests() => PaneViewModel.ShellMenu = new Inert();

    /// <summary>**Chained rather than overriding the teardown away**, which is
    /// the one way to keep none of what OwnedViewModels does.</summary>
    public override void Dispose()
    {
        PaneViewModel.ShellMenu = _shellMenuBefore;
        base.Dispose();
    }

    /// <summary>Pumps the dispatcher and lays the window out, the way the other
    /// window tests in this assembly do.</summary>
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

    /// <summary>The listing's own menu: the only one in the window holding a
    /// direct child named ShellMenu.</summary>
    private static ContextMenu ListingMenu(MainWindow window)
        => window.GetVisualDescendants()
            .OfType<Control>()
            .Select(c => c.ContextMenu)
            .OfType<ContextMenu>()
            .Single(m => m.Items.OfType<MenuItem>().Any(i => i.Name == "ShellMenu"));

    /// <summary>The hosted row itself.</summary>
    private static MenuItem ShellRow(ContextMenu menu)
        => menu.Items.OfType<MenuItem>().Single(i => i.Name == "ShellMenu");

    /// <summary>The rule that introduces it: the child declared before it,
    /// whatever its own visibility.</summary>
    private static Control RuleAbove(ContextMenu menu)
    {
        var items = menu.Items.OfType<Control>().ToList();

        return items[items.IndexOf(ShellRow(menu)) - 1];
    }

    /// <summary>What a person actually sees when the menu opens, in order.</summary>
    private static List<Control> Seen(ContextMenu menu)
        => [.. menu.Items.OfType<Control>().Where(c => c.IsVisible)];

    /// <summary>The words on a row, with the access key marker taken out.</summary>
    private static string Words(Control row)
        => MenuLabels.Plain((row as MenuItem)?.Header as string);

    private static string TempFolder()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-shellplace-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");

        return root;
    }

    /// <summary>
    /// Runs a body against the listing menu of a real window, opened on the
    /// given listing, and puts the tab back where it was — this window flushes
    /// the session it was built from when it closes.
    /// </summary>
    private async Task InTheMenu(
        string? virtualPath, Func<ShellViewModel, ContextMenu, Task> body)
    {
        var root = TempFolder();
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // After the window, which assigns the platform's own search provider:
        // a search listing here must not walk the machine.
        UseSearch(null);

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var was = shell.ActiveTab?.CurrentPath;

        try
        {
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            await Layout(window);

            var menu = ListingMenu(window);

            // **Opened in an ordinary folder first, and that is what makes
            // these tests able to fail.** A gate is read when the binding is
            // established, and every row of this menu lives in the same
            // ContextMenu for the life of the window — so a gate nothing
            // announces keeps the answer the FIRST open got, and a menu whose
            // first open is on the listing under test never shows a stale one.
            // Measured: with HasShellMenu's notification removed, opening
            // straight onto the bin passed and this order failed.
            menu.Open();
            await Layout(window);
            menu.Close();
            await Layout(window);

            if (virtualPath is not null)
            {
                await pane.NavigateAsync(virtualPath);
                await Layout(window);
            }

            menu.Open();
            await Layout(window);

            Assert.True(menu.IsOpen, "the listing's menu did not open, so this proves nothing");

            await body(shell, menu);

            menu.Close();
            await Layout(window);
        }
        finally
        {
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

    // ---- where it sits ------------------------------------------------------

    /// <summary>
    /// The finding: the machine's own menu is the last thing on the list.
    /// </summary>
    [AvaloniaFact]
    public async Task The_windows_menu_is_the_last_row_of_the_listing_menu()
        => await InTheMenu(null, (shell, menu) =>
        {
            Assert.True(shell.ActiveTab!.HasShellMenu,
                        "the row is not offered here, so this proves nothing");

            var seen = Seen(menu);

            Assert.Same(ShellRow(menu), seen[^1]);
            Assert.Equal("Windows menu", Words(seen[^1]));

            return Task.CompletedTask;
        });

    /// <summary>
    /// The half that says WHICH row it went below, and with what between them.
    /// "Last" alone would still be satisfied by a menu that ended with the
    /// hosted row and kept Properties nowhere near it; Properties, a rule, then
    /// the machine's own is the arrangement Explorer has.
    /// </summary>
    [AvaloniaFact]
    public async Task Properties_and_one_rule_are_what_sit_above_it()
        => await InTheMenu(null, (_, menu) =>
        {
            var seen = Seen(menu);

            Assert.Equal("Windows menu", Words(seen[^1]));
            Assert.IsType<Separator>(seen[^2]);
            Assert.Equal("Properties", Words(seen[^3]));

            return Task.CompletedTask;
        });

    // ---- and what the rules do when a row is not there -----------------------

    /// <summary>
    /// **The double rule the move made, in the listings where the two gates
    /// disagree.** Properties wants a selection or a real folder; the hosted
    /// row excludes only the bin and Recent. In This PC and in a search with
    /// nothing picked the first is hidden and the second is not — so the rule
    /// that introduces Properties and the rule that introduces the hosted row
    /// met, with nothing between them, and Avalonia collapses neither.
    ///
    /// Asserted over the whole menu rather than that one pair: any two rules
    /// touching is the same line drawn twice, wherever it happens.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(VirtualPaths.Computer)]
    [InlineData("vaktari:search:report::everywhere")]
    public async Task No_two_rules_meet_where_properties_is_hidden(string path)
        => await InTheMenu(path, (shell, menu) =>
        {
            // The disagreement itself, asserted rather than assumed: it is what
            // makes the rest of this test the case it is meant to be.
            Assert.False(shell.CanShowProperties);
            Assert.True(shell.ActiveTab!.HasShellMenu);

            var seen = Seen(menu);

            for (var i = 1; i < seen.Count; i++)
                Assert.False(seen[i] is Separator && seen[i - 1] is Separator,
                             $"two rules meet at visible row {i} of the menu in {path}");

            return Task.CompletedTask;
        });

    /// <summary>
    /// The other end of the same rule: where the hosted row is hidden, nothing
    /// is left hanging off the bottom of the menu. That is the bin and Recent
    /// on Windows, and every listing on Linux, where the provider is null.
    ///
    /// Both gates are checked, not just the drawn result — a menu whose last
    /// visible thing is a row rather than a rule would also be satisfied by the
    /// hosted row still being offered in the bin, which is the fault this pins
    /// the fix for.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(VirtualPaths.Trash)]
    [InlineData(VirtualPaths.Files)]
    public async Task Nothing_hangs_off_the_bottom_where_the_row_is_hidden(string path)
        => await InTheMenu(path, (shell, menu) =>
        {
            Assert.False(shell.ActiveTab!.HasShellMenu);

            Assert.False(ShellRow(menu).IsVisible,
                         "the listing offers a menu the pane refuses to build");
            Assert.False(RuleAbove(menu).IsVisible);

            var seen = Seen(menu);

            Assert.False(seen[^1] is Separator,
                         "a line under the final row of the menu in " + path);

            return Task.CompletedTask;
        });
}
