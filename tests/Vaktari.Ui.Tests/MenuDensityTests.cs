using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Menu rows at 24 pixels, measured.
///
/// **Every menu row was Fluent's 30 pixels and every rule a 9-pixel band.**
/// Fluent pads a menu row 11,4,11,7 around its line of text; with the listing
/// menu at twenty-eight rows and six rules for one file on Windows, that came
/// to 904 pixels — the "very large and clunky" the menus were redesigned for.
/// Two styles in each window with menus now make a row 24 and a rule 7.
///
/// **Measured on the realized controls rather than asserted as numbers in the
/// markup.** A padding is only a claim about a row's height: the text inside
/// decides the rest, the theme's own template decides how they combine, and a
/// style that does not reach a submenu's rows — or a popup's — would leave the
/// markup green and the menu tall. So each test opens a real menu and reads the
/// Bounds its rows were arranged in: a top-level row, a row in a submenu, a
/// rule, and a flyout, in both windows that carry the styles.
///
/// The headless font is not the desktop's — its line is sixteen pixels where
/// Segoe UI's is nineteen — which is why the style carries a MinHeight of 24
/// rather than trusting the padding alone to add up: measured here, a row is
/// 24 on either, and would have been 21 headless on padding alone.
/// </summary>
public sealed class MenuDensityTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;

    public override void Dispose()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        base.Dispose();
    }

    private static void Lay(Window window, double width = 1400, double height = 900)
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The height a rule takes in its menu: its own line and the
    /// margin above and below it.</summary>
    private static double Taken(Separator rule) => rule.Bounds.Height + rule.Margin.Top + rule.Margin.Bottom;

    [AvaloniaFact]
    public void The_listing_menus_rows_are_24_pixels_and_their_rules_7()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        try
        {
            window.Show();
            Lay(window);

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var list = window.GetVisualDescendants()
                .OfType<ListBox>()
                .Single(l => l.IsVisible && ReferenceEquals(l.DataContext, shell.ActiveTab)
                             && l.SelectionMode.HasFlag(SelectionMode.Multiple));

            // The background menu: it has rows and rules wherever the window
            // happens to be, which the item menu, needing a selection, does not.
            var menu = ListingMenus.Above(list, ListingMenus.Background);

            menu.Open();
            Lay(window);

            try
            {
                var seen = ListingMenus.Seen(menu);

                var rows = seen.OfType<MenuItem>().ToList();
                var rules = seen.OfType<Separator>().ToList();

                Assert.NotEmpty(rows);
                Assert.NotEmpty(rules);

                Assert.All(rows, row => Assert.Equal(24, row.Bounds.Height));
                Assert.All(rules, rule => Assert.Equal(7, Taken(rule)));

                // A submenu's rows are the same size: the style is anchored on
                // the menu and reaches down through the rows' own logical
                // children, which is how a submenu hangs off its row.
                var view = ListingMenus.Row(menu, "View");

                view.Open();
                Lay(window);

                var layout = view.Items.OfType<MenuItem>().First();

                Assert.Equal(24, layout.Bounds.Height);
            }
            finally
            {
                menu.Close();
                Lay(window);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A MenuFlyout's rows, in the other window that has menus: the settings
    /// dialog's "Settings file" button. The same two styles are declared there
    /// — a style in one window does not reach another — and this is what says
    /// the copy is right.
    /// </summary>
    [AvaloniaFact]
    public void The_settings_windows_flyout_rows_are_24_pixels_too()
    {
        var window = new SettingsWindow { DataContext = new SettingsViewModel(new SettingsState()) };

        try
        {
            window.Show();
            Lay(window, 700, 560);

            var button = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(b => b.Content as string == "Settings file");

            var flyout = Assert.IsType<MenuFlyout>(button.Flyout);

            flyout.ShowAt(button);
            Lay(window, 700, 560);

            try
            {
                var rows = flyout.Items.OfType<MenuItem>().ToList();

                Assert.NotEmpty(rows);
                Assert.All(rows, row => Assert.Equal(24, row.Bounds.Height));
                Assert.Equal(7, Taken(flyout.Items.OfType<Separator>().Single()));
            }
            finally
            {
                flyout.Hide();
                Lay(window, 700, 560);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
