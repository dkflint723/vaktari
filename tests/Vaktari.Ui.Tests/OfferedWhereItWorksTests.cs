using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
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
/// its own full path — and then into the listing itself, where the sidebar is
/// beside the results rather than replaced by them.
///
/// **And "Use my desktop's icons" was on screen on Linux, where nothing could
/// act on it.** The setting is honoured through IPlatform.FileIcons alone —
/// Windows composes an icon per file and has such a provider, freedesktop
/// answers by icon name and has none — so the box could be ticked, saved, and
/// found still ticked, while every row drew exactly what it drew before.
///
/// The box is a row of the File icons chooser now, so the gate is on the row:
/// the list offers "Your desktop's icons" exactly where the box was shown, and
/// the chooser itself — live on both platforms — is never gated.
/// </summary>
public sealed class OfferedWhereItWorksTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ---- searching leaves the sidebar alone --------------------------------

    /// <summary>
    /// The whole finding: a rail deliberately hidden stays hidden. Checked at
    /// each of the three states, because the old line set Full unconditionally
    /// and would pass a test that only tried one.
    ///
    /// Asked through the shell rather than of the pane, because the pane is
    /// where the command lives now and the sidebar is what must not move — a
    /// test of the pane alone could not tell the two apart.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Vaktari.Core.Session.RailState.Hidden)]
    [InlineData(Vaktari.Core.Session.RailState.RailOnly)]
    [InlineData(Vaktari.Core.Session.RailState.Full)]
    public void Searching_leaves_the_sidebar_however_you_left_it(
        Vaktari.Core.Session.RailState rail)
    {
        var shell = Own(new ShellViewModel(new Empty()));

        shell.Start(null, Path.GetTempPath());
        shell.Sidebar.Rail = rail;

        shell.ActiveTab!.BeginSearchCommand.Execute(null);

        Assert.Equal(rail, shell.Sidebar.Rail);
    }

    /// <summary>And still does what it is for.</summary>
    [AvaloniaFact]
    public void But_still_puts_the_caret_in_the_box()
    {
        var shell = Own(new ShellViewModel(new Empty()));

        shell.Start(null, Path.GetTempPath());
        shell.Sidebar.Rail = Vaktari.Core.Session.RailState.Hidden;

        shell.ActiveTab!.BeginSearchCommand.Execute(null);

        Assert.True(shell.ActiveTab.IsSearchOpen);
        Assert.True(shell.ActiveTab.IsSearchFocused);
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
            // The provider itself, not the whole argument list: pinning the
            // list makes this fail every time a fourth thing is handed over,
            // which says nothing about whether the icons still are.
            "_platform.FileIcons",
            RepoSource.Body(
                RepoSource.UiClass("", "MainWindow"), "internal void ShowSettings(SettingsPage? page = null)"));

    /// <summary>
    /// The chooser half. Hiding the desktop's row in the view model buys
    /// nothing unless the row is really absent from the list on screen — and
    /// with a provider, really there.
    /// </summary>
    [AvaloniaFact]
    public void The_desktop_s_row_is_offered_only_with_a_provider()
    {
        Assert.DoesNotContain(
            new SettingsViewModel(Vaktari.Ui.Settings.AppSettings.Current).IconThemeChoices,
            c => c.DesktopIcons);

        Assert.Single(
            new SettingsViewModel(Vaktari.Ui.Settings.AppSettings.Current, desktopIcons: new NoIcons())
                .IconThemeChoices,
            c => c.DesktopIcons);
    }

    /// <summary>
    /// And the chooser that holds the row must NOT be gated: it is live on
    /// both platforms, and hiding it on Linux would take away the only icon
    /// control that works there.
    /// </summary>
    [Fact]
    public void The_icon_chooser_itself_is_not_hidden()
    {
        var chooser = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "ComboBox")
            .Single(e => (string?)e.Attribute(X + "Name") == "IconChoice");

        Assert.Equal("{Binding IconThemeChoices}", (string?)chooser.Attribute("ItemsSource"));
        Assert.DoesNotContain(chooser.AncestorsAndSelf(), e => e.Attribute("IsVisible") is not null);
    }

    /// <summary>
    /// The paragraph named Windows on both platforms, promising something a
    /// Linux reader could not have. It is the chooser's help text now, and
    /// the row's own words name the desktop too.
    /// </summary>
    [AvaloniaFact]
    public void And_the_words_name_the_desktop_rather_than_one_of_them()
    {
        var text = (string?)XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "ComboBox")
            .Single(e => (string?)e.Attribute(X + "Name") == "IconChoice")
            .Attribute("AutomationProperties.HelpText") ?? "";

        Assert.DoesNotContain("Windows", text);
        Assert.Contains("desktop", text);

        var row = new SettingsViewModel(Vaktari.Ui.Settings.AppSettings.Current, desktopIcons: new NoIcons())
            .IconThemeChoices.Single(c => c.DesktopIcons);

        Assert.DoesNotContain("Windows", row.Label);
        Assert.Contains("desktop", row.Label);
    }

    private sealed class Empty : IFileSystemProvider
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

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
