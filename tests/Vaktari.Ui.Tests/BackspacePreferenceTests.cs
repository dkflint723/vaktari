using System.Xml.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What Backspace does, and who decides.
///
/// **It answered Explorer's habit and nobody else's.** Explorer's Backspace
/// goes Back through history; Dolphin's goes up to the parent folder. Vaktari
/// hard-coded Back, so somebody who learned the key on the other desktop
/// pressed it at the bottom of a deep tree and was thrown to wherever they had
/// been ten minutes earlier. A key that does nothing is a puzzle you solve in a
/// second; a key that confidently does the other thing is a wrong turn you then
/// have to notice and undo.
///
/// So it is a preference, defaulting to Back — what shipped, and what the
/// larger audience expects. These pin all four halves of that: the default, the
/// override, the sheet that has to say which one is in force, and the dialog
/// that has to load and save the choice.
/// </summary>
public sealed class BackspacePreferenceTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private MainWindow? _window;

    public override void Dispose()
    {
        // **Closing is not tidiness.** A shown window flushes the session on
        // close, and CaptureGeometry writes its size — TestState points both at
        // this run's directory, but a window left open is torn down later on
        // whatever thread xunit is on and surfaces as a threading failure in
        // some unrelated test that merely ran afterwards.
        _window?.Close();

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// Three real folders arranged so that Back and Up cannot give the same
    /// answer: the pane walks to <c>far</c> and then to <c>near/deep</c>, so
    /// Back is <c>far</c> and Up is <c>near</c>. A tree where the two coincide
    /// would pass whichever branch the handler took.
    /// </summary>
    private sealed record Tree(string Root, string Far, string Near, string Deep) : IDisposable
    {
        public static Tree Make()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "vaktari-backspace-" + Guid.NewGuid().ToString("N")[..8]);

            var tree = new Tree(root,
                                Path.Combine(root, "far"),
                                Path.Combine(root, "near"),
                                Path.Combine(root, "near", "deep"));

            Directory.CreateDirectory(tree.Far);
            Directory.CreateDirectory(tree.Deep);

            return tree;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// A real window walked to <see cref="Tree.Deep"/> with <see cref="Tree.Far"/>
    /// behind it, with the preference applied AFTER construction — the
    /// constructor applies whatever is on disk over anything a test has set.
    /// </summary>
    private async Task<(MainWindow Window, PaneViewModel Pane)> Walked(Tree tree, bool goesUp)
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow();

        window.Show();
        Settle();

        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(before with
        {
            Navigation = before.Navigation with { BackspaceGoesUp = goesUp },
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        await pane.NavigateAsync(tree.Far);
        await pane.NavigateAsync(tree.Deep);
        Settle();

        // **Nothing is pressed until the pane is provably where this test put
        // it.** A real window opens on whatever folders the session restored,
        // and both assertions below would otherwise be about that folder's
        // history rather than about this one's.
        Assert.Equal(tree.Deep, pane.CurrentPath);
        Assert.True(pane.CanGoBack, "there is no history for Back to walk");
        Assert.True(pane.CanGoUp, "there is no parent for Up to reach");

        return (window, pane);
    }

    // ---- the key ------------------------------------------------------------

    /// <summary>
    /// The shipped behaviour, out of the box.
    ///
    /// It asks the RECORD what its default is rather than writing false here,
    /// so the default is under test too: give the property an initializer and
    /// this fails.
    /// </summary>
    [AvaloniaFact]
    public async Task Backspace_goes_back_out_of_the_box()
    {
        using var tree = Tree.Make();

        var (window, pane) = await Walked(tree, goesUp: new NavigationSettings().BackspaceGoesUp);

        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Settle();

        Assert.Equal(tree.Far, pane.CurrentPath);
    }

    /// <summary>And the other habit, once asked for.</summary>
    [AvaloniaFact]
    public async Task Backspace_goes_up_when_the_setting_says_so()
    {
        using var tree = Tree.Make();

        var (window, pane) = await Walked(tree, goesUp: true);

        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Settle();

        Assert.Equal(tree.Near, pane.CurrentPath);
    }

    /// <summary>
    /// **Neither setting may cost a route.** The whole case for making this a
    /// preference rather than a choice is that Alt+← and Alt+↑ go on doing
    /// their own jobs either way — so somebody who flips it loses nothing, and
    /// somebody who leaves it alone gains the same.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alt_left_and_alt_up_keep_working_either_way(bool goesUp)
    {
        using var tree = Tree.Make();

        var (window, pane) = await Walked(tree, goesUp);

        // **Pressed, not called.** This asserted by invoking GoUpAsync and
        // GoBackAsync directly, which is not what its name promises and not
        // what the decision promises: deleting the whole Backspace body left
        // it green, and so did repointing the Alt+Up KeyBinding at GoBack.
        // A route is a KEY reaching a command, so the key is what goes in.
        window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
        Settle();
        Assert.Equal(tree.Near, pane.CurrentPath);

        window.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        Settle();
        Assert.Equal(tree.Deep, pane.CurrentPath);
    }

    /// <summary>
    /// **Turning it on makes Backspace inert wherever there is no parent**, and
    /// that is a real cost the changelog does not mention: This PC, the bin,
    /// Recent and a search all report CanGoUp false, so the key that used to
    /// walk the history from them now does nothing at all.
    ///
    /// Pinned rather than argued about: it is Dolphin's own behaviour and the
    /// maintainer chose Dolphin, but it must be a decision somebody made rather
    /// than a crash waiting in GoUpAsync.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_has_nowhere_to_go_from_a_virtual_listing(bool goesUp)
    {
        using var tree = Tree.Make();

        var (window, pane) = await Walked(tree, goesUp);

        await pane.NavigateAsync(VirtualPaths.Computer);
        Settle();

        Assert.Equal(VirtualPaths.Computer, pane.CurrentPath);
        Assert.False(pane.CanGoUp, "This PC has no parent, so Up cannot mean anything");
        Assert.True(pane.CanGoBack, "and there is history behind it for Back to walk");

        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Settle();

        Assert.Equal(goesUp ? VirtualPaths.Computer : tree.Deep, pane.CurrentPath);
    }

    // ---- the sheet ----------------------------------------------------------

    /// <summary>
    /// **A sheet that names the wrong behaviour is worse than no sheet**, and
    /// this is the one line in it whose meaning is a preference. Read through
    /// <c>Shortcuts.All</c> rather than the <c>For</c> overload, so the wiring
    /// between the sheet and the live setting is what is under test.
    /// </summary>
    [Theory]
    [InlineData(false, "Back")]
    [InlineData(true, "Up one folder")]
    public void The_sheet_says_what_backspace_currently_does(bool goesUp, string expected)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(before with
        {
            Navigation = before.Navigation with { BackspaceGoesUp = goesUp },
        });

        var line = Vaktari.Ui.ViewModels.Shortcuts.All
            .SelectMany(g => g.Keys)
            .Single(k => k.Keys == "Backspace");

        Assert.StartsWith(expected, line.Does, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it says where the other half lives. Somebody reading this line
    /// pressed the key and got the answer they did not want; the sheet is the
    /// only thing in front of them that can say the answer is theirs to change.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void And_says_the_page_that_changes_it(bool goesUp)
    {
        var line = Vaktari.Ui.ViewModels.Shortcuts.For(goesUp)
            .SelectMany(g => g.Keys)
            .Single(k => k.Keys == "Backspace");

        Assert.Contains("Navigation", line.Does, StringComparison.Ordinal);

        // Both behaviours named on the one line, whichever way it is set:
        // "makes it Back" is useless to a reader who does not know what the key
        // is doing now.
        Assert.Contains("Back", line.Does, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("up one folder", line.Does, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the dialog ---------------------------------------------------------

    /// <summary>
    /// Opening the dialog shows the choice already made. Loading the wrong
    /// value is the failure that quietly rewrites somebody's preference the
    /// moment they open the dialog and press Save.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_dialog_opens_showing_the_saved_choice(bool goesUp)
    {
        var model = new SettingsViewModel(new SettingsState
        {
            Navigation = new NavigationSettings { BackspaceGoesUp = goesUp },
        });

        Assert.Equal(goesUp, model.BackspaceGoesUp);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_ticked_box_is_what_gets_saved(bool goesUp)
    {
        var model = new SettingsViewModel(new SettingsState())
        {
            BackspaceGoesUp = goesUp,
        };

        model.SaveCommand.Execute(null);

        Assert.Equal(goesUp, model.Result.Navigation.BackspaceGoesUp);
    }

    /// <summary>
    /// And Save without touching anything gives back what was there — the
    /// round-trip that matters most for a value only reachable by hand-editing
    /// the file.
    /// </summary>
    [AvaloniaFact]
    public void Saving_without_touching_it_preserves_it()
    {
        var model = new SettingsViewModel(new SettingsState
        {
            Navigation = new NavigationSettings { BackspaceGoesUp = true },
        });

        model.SaveCommand.Execute(null);

        Assert.True(model.Result.Navigation.BackspaceGoesUp);
    }

    /// <summary>
    /// **The control has to be on the page, not merely on the model.** A
    /// preference with no checkbox is one nobody can set, which is the state
    /// this finding started in — and compiled bindings would let the property
    /// go on existing with nothing bound to it.
    /// </summary>
    [Fact]
    public void The_navigation_page_offers_it()
    {
        var box = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "CheckBox")
            .Single(c => (string?)c.Attribute(X + "Name") == "BackspaceChoice");

        Assert.Equal("{Binding BackspaceGoesUp}", (string?)box.Attribute("IsChecked"));

        // On the Navigation page rather than wherever it happened to land: the
        // page is the one somebody looks on for what a navigation key does.
        Assert.Equal(
            "Navigation",
            box.Ancestors(Avalonia + "TabItem")
               .Select(t => (string?)t.Attribute("Header"))
               .First());
    }

    /// <summary>
    /// **And the click radios above it still bind their own properties.**
    ///
    /// Not paranoia. Adding the checkbox to this page really did repoint "A
    /// single click" at the new property once, while this was being written,
    /// and every test in the suite went on passing: the page had nothing
    /// asserting what its own three radios bind, so a control beside the new
    /// one silently became a second switch for it.
    /// </summary>
    [Theory]
    [InlineData("Whatever the desktop is set to", "OpenWithSystem")]
    [InlineData("A single click", "OpenWithSingle")]
    [InlineData("A double click", "OpenWithDouble")]
    public void The_click_choice_above_it_is_untouched(string label, string property)
    {
        var page = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "TabItem")
            .Single(t => (string?)t.Attribute("Header") == "Navigation");

        var radio = page.Descendants(Avalonia + "RadioButton")
            .Single(r => (string?)r.Attribute("Content") == label
                         || r.Descendants(Avalonia + "TextBlock")
                             .Any(t => (string?)t.Attribute("Text") == label));

        Assert.Equal("{Binding " + property + "}", (string?)radio.Attribute("IsChecked"));
    }
}
