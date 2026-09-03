using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Two gestures that changed something they were never asked to.
///
/// **Ctrl+F, Ctrl+E and the magnifier all forced the sidebar back open.**
/// FocusSearch set the rail to Full, so a search silently reversed a Ctrl+B or
/// an F9 — and because the rail is written into the session, the sidebar you
/// hid was back again on the next launch. The line was kept on the grounds that
/// a result's place context is read off the rail; that stopped being true when
/// the results moved into a popup under the path bar, where every row carries
/// its own full path.
///
/// **And "Use my desktop's icons" was on screen on Linux, where nothing could
/// act on it.** The setting is honoured through IPlatform.FileIcons alone —
/// Windows composes an icon per file and has such a provider, freedesktop
/// answers by icon name and has none — so the box could be ticked, saved, and
/// found still ticked, while every row drew exactly what it drew before.
/// </summary>
public sealed class OfferedWhereItWorksTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ---- searching leaves the sidebar alone --------------------------------

    /// <summary>
    /// The whole finding: a rail deliberately hidden stays hidden. Checked at
    /// each of the three states, because the old line set Full unconditionally
    /// and would pass a test that only tried one.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Vaktari.Core.Session.RailState.Hidden)]
    [InlineData(Vaktari.Core.Session.RailState.RailOnly)]
    [InlineData(Vaktari.Core.Session.RailState.Full)]
    public void Searching_leaves_the_sidebar_however_you_left_it(
        Vaktari.Core.Session.RailState rail)
    {
        var sidebar = new SidebarViewModel(places: null, search: null) { Rail = rail };

        sidebar.FocusSearchCommand.Execute(null);

        Assert.Equal(rail, sidebar.Rail);
    }

    /// <summary>And still does what it is for.</summary>
    [AvaloniaFact]
    public void But_still_puts_the_caret_in_the_box()
    {
        var sidebar = new SidebarViewModel(places: null, search: null)
        {
            Rail = Vaktari.Core.Session.RailState.Hidden,
        };

        sidebar.FocusSearchCommand.Execute(null);

        Assert.True(sidebar.IsSearchOpen);
        Assert.True(sidebar.IsSearching);
    }

    // ---- the icons choice is offered only where it works -------------------

    /// <summary>
    /// Null provider, no control — the bargain CanBeDefault already makes.
    /// </summary>
    [AvaloniaFact]
    public void Without_a_per_file_icon_provider_the_choice_is_not_offered()
        => Assert.False(
            new SettingsViewModel(Vaktari.Ui.Settings.AppSettings.Current).CanUseDesktopIcons);

    [AvaloniaFact]
    public void And_with_one_it_is()
        => Assert.True(
            new SettingsViewModel(
                Vaktari.Ui.Settings.AppSettings.Current,
                defaults: null,
                desktopIcons: new NoIcons()).CanUseDesktopIcons);

    private sealed class NoIcons : Vaktari.Core.FileSystem.IFileIconProvider
    {
        public Vaktari.Core.FileSystem.IconPixels? IconFor(
            string path, bool isDirectory, int size) => null;
    }

    /// <summary>
    /// And the window hands over the real one. The gate answers whatever it is
    /// given, so a settings window built with null would hide the control on
    /// Windows too — where it is the only thing that works.
    /// </summary>
    [Fact]
    public void The_settings_window_is_handed_the_platform_s_own_provider()
        => Assert.Contains(
            "AppSettings.Current, _defaultFileManager, _platform.FileIcons)",
            RepoSource.Body(
                RepoSource.Ui("MainWindow.axaml.cs"), "private void ShowSettings()"));

    /// <summary>
    /// The markup half. Hiding the checkbox in the view model buys nothing if
    /// the control on screen is not the one that asks.
    /// </summary>
    [Fact]
    public void The_checkbox_asks_before_it_shows()
    {
        var panel = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "StackPanel")
            .Single(e => (string?)e.Attribute(X + "Name") == "DesktopIconsChoice");

        Assert.Equal("{Binding CanUseDesktopIcons}", (string?)panel.Attribute("IsVisible"));

        // It wraps the checkbox itself, not something near it.
        Assert.Contains(panel.Descendants(Avalonia + "CheckBox"),
                        c => (string?)c.Attribute("IsChecked") == "{Binding UseSystemIcons}");
    }

    /// <summary>
    /// And the icon-theme chooser below it must NOT have been swept into the
    /// gate: that one is live on both platforms, and hiding it on Linux would
    /// take away the only icon control that works there.
    /// </summary>
    [Fact]
    public void The_theme_chooser_below_it_is_not_hidden_too()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var gated = markup.Descendants(Avalonia + "StackPanel")
            .Single(e => (string?)e.Attribute(X + "Name") == "DesktopIconsChoice");

        Assert.DoesNotContain(gated.Descendants(Avalonia + "ComboBox"),
                              _ => true);
    }

    /// <summary>
    /// The paragraph named Windows on both platforms, promising something a
    /// Linux reader could not have.
    /// </summary>
    [Fact]
    public void And_the_paragraph_names_the_desktop_rather_than_one_of_them()
    {
        var text = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "StackPanel")
            .Single(e => (string?)e.Attribute(X + "Name") == "DesktopIconsChoice")
            .Descendants(Avalonia + "TextBlock")
            .Select(t => (string?)t.Attribute("Text") ?? "")
            .Single(t => t.Contains("they use the ones", StringComparison.Ordinal));

        Assert.DoesNotContain("Windows", text);
        Assert.Contains("your desktop", text);
    }
}
