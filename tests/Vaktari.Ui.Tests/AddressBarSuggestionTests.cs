using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the address bar offers while a path is being typed.
///
/// **The completer worked out every candidate and the box showed one.** Tab has
/// extended a typed path since it was written — first to the prefix every
/// candidate agrees on, then cycling through them — and the matches it built to
/// do that were private to it. Nothing was on screen to say there were
/// alternatives, which of them was next, or that the key had done anything at
/// all; the only notice anywhere was one line of a tooltip.
///
/// The list is the same candidates in the same order, so the dropdown and Tab
/// cannot disagree about what matches. Tab, Enter and Escape are untouched and
/// are pinned by their own files.
/// </summary>
public sealed class AddressBarSuggestionTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static char Slash => Path.DirectorySeparatorChar;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-suggest-" + Guid.NewGuid().ToString("N"));

    public AddressBarSuggestionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Galaxy", "Inner"));
        Directory.CreateDirectory(Path.Combine(_root, "Games"));
        Directory.CreateDirectory(Path.Combine(_root, "Music"));
    }

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); } catch { }

        GC.SuppressFinalize(this);
    }

    /// <summary>A pane sitting in the tree above, with its path box open.</summary>
    private async Task<PaneViewModel> Editing()
    {
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);

        pane.BeginEditPath();

        return pane;
    }

    /// <summary>
    /// The list is the candidates, in the order Tab hands them out.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_a_partial_path_offers_the_folders_it_could_be()
    {
        var pane = await Editing();

        pane.PathText = _root + Slash + "Ga";

        Assert.Equal(["Galaxy", "Games"], pane.PathSuggestions.Select(s => s.Name));
        Assert.True(pane.IsPathSuggestionsOpen);
    }

    /// <summary>
    /// **PathText is written on the way IN to editing as well as while typing**
    /// — BeginEditPath fills the box with the folder you are standing in — so a
    /// rebuild that did not ask whether the box was open dropped a list of the
    /// current folder's siblings over the window the instant Ctrl+L was
    /// pressed, before a single character had been typed.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_the_box_offers_nothing_until_something_is_typed()
    {
        var pane = await Editing();

        Assert.Equal(_root, pane.PathText);
        Assert.Empty(pane.PathSuggestions);
        Assert.False(pane.IsPathSuggestionsOpen);
    }

    /// <summary>
    /// Picking a row types it — the same thing Tab does — and the offer becomes
    /// the chosen folder's own children, so walking down a tree with the mouse
    /// is one gesture repeated.
    /// </summary>
    [AvaloniaFact]
    public async Task Picking_a_row_types_it_and_then_offers_what_is_inside_it()
    {
        var pane = await Editing();

        pane.PathText = _root + Slash + "Ga";

        pane.PathSuggestions.Single(s => s.Name == "Galaxy").Apply.Execute(null);

        Assert.Equal(Path.Combine(_root, "Galaxy") + Slash, pane.PathText);
        Assert.Equal(["Inner"], pane.PathSuggestions.Select(s => s.Name));
    }

    /// <summary>
    /// Tab still completes, and the list follows it rather than going stale.
    ///
    /// The completing write is the one PathText change that is NOT the user
    /// typing, and it is the change that most alters what should be on offer:
    /// the text now ends in a separator, so the answer is the contents of the
    /// folder Tab just landed in.
    /// </summary>
    [AvaloniaFact]
    public async Task Tab_still_completes_and_the_offer_follows_it()
    {
        var pane = await Editing();

        pane.PathText = _root + Slash + "Ga";
        pane.CompletePath();

        Assert.Equal(Path.Combine(_root, "Galaxy") + Slash, pane.PathText);
        Assert.Equal(["Inner"], pane.PathSuggestions.Select(s => s.Name));
    }

    /// <summary>
    /// Escape, or clicking away, takes the list with the box.
    ///
    /// Not implied by the text changing: the revert writes the CURRENT path
    /// back into the box, and that is a perfectly good prefix to offer
    /// completions for — so without an explicit close the field collapsed to
    /// crumbs and left a dropdown hanging under them.
    /// </summary>
    [AvaloniaFact]
    public async Task Cancelling_the_edit_puts_the_offer_away()
    {
        var pane = await Editing();

        pane.PathText = _root + Slash + "Ga";
        Assert.True(pane.IsPathSuggestionsOpen);

        pane.RevertPathText();

        Assert.Empty(pane.PathSuggestions);
        Assert.False(pane.IsPathSuggestionsOpen);
    }

    /// <summary>
    /// And so does Enter. This one cannot be implied by anything: Enter READS
    /// PathText and never writes it, so no rebuild is coming.
    /// </summary>
    [AvaloniaFact]
    public async Task Going_somewhere_puts_the_offer_away()
    {
        var pane = await Editing();

        pane.PathText = _root + Slash + "Ga";
        Assert.True(pane.IsPathSuggestionsOpen);

        await pane.NavigateToPathText();

        Assert.Empty(pane.PathSuggestions);
        Assert.False(pane.IsPathSuggestionsOpen);
    }

    // ---- and the markup that shows them -------------------------------------

    private static XElement Markup()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml")).Root!;

    private static XElement TheList()
        => Markup().Descendants(Avalonia + "Popup")
            .Single(p => (string?)p.Attribute(X + "Name") == "PathSuggestionList");

    /// <summary>
    /// The list exists, is driven by the view model, and its rows carry the
    /// command that types one.
    /// </summary>
    [Fact]
    public void The_box_has_a_list_under_it_bound_to_the_offer()
    {
        var list = TheList();

        Assert.Equal("{Binding ActiveTab.IsPathSuggestionsOpen, Mode=OneWay}",
                     (string?)list.Attribute("IsOpen"));

        var rows = list.Descendants(Avalonia + "ItemsControl").Single();

        Assert.Equal("{Binding ActiveTab.PathSuggestions}", (string?)rows.Attribute("ItemsSource"));

        var row = rows.Descendants(Avalonia + "Button").Single();

        Assert.Equal("{Binding Apply}", (string?)row.Attribute("Command"));
    }

    /// <summary>
    /// **A Button renders its Content through AccessText, which eats "_" and
    /// underlines the next letter**, so a folder called "git_projects" would
    /// draw as "gitprojects". The crumbs one screen up learned this the hard
    /// way; the same rule has to hold here, because these rows show the same
    /// kind of string.
    /// </summary>
    [Fact]
    public void A_suggested_name_is_not_parsed_for_mnemonics()
    {
        var row = TheList().Descendants(Avalonia + "Button").Single();

        Assert.Null(row.Attribute("Content"));

        Assert.Equal("{Binding Name}",
                     (string?)row.Elements(Avalonia + "TextBlock").Single().Attribute("Text"));
    }

    /// <summary>
    /// **The box would have destroyed itself when a row was clicked.** It
    /// reverts and hides on lost focus — that is how clicking away cancels —
    /// and a popup is its own focus root, so the field collapsed out from under
    /// the list being clicked. It names the list as its own so that opening it
    /// does not count as leaving.
    /// </summary>
    [Fact]
    public void The_box_owns_the_list_rather_than_being_left_by_it()
    {
        var box = Markup().Descendants(Avalonia + "TextBox")
            .Single(t => (string?)t.Attribute(X + "Name") == "PathBox");

        Assert.Equal("{Binding #PathSuggestionList}",
                     (string?)box.Attribute(Local + "FocusBehavior.CompanionPopup"));
    }

    /// <summary>The markup's `local:` prefix, which is where FocusBehavior
    /// lives.</summary>
    private static readonly XNamespace Local = "clr-namespace:Vaktari.Ui";

    /// <summary>
    /// **A dropdown nobody expects is still a surprise**, and the box's tooltip
    /// is the one place that says what the field can do — it already names Tab,
    /// Enter and Escape. The list has to be in there beside them, or the only
    /// way to learn it exists is to type into the bar and see something appear.
    /// </summary>
    [Fact]
    public void The_box_says_that_it_offers_a_list()
    {
        var box = Markup().Descendants(Avalonia + "TextBox")
            .Single(t => (string?)t.Attribute(X + "Name") == "PathBox");

        Assert.Contains("listed below as you type", (string?)box.Attribute("ToolTip.Tip"));
    }

    /// <summary>
    /// The list cannot dismiss itself, which is the fact the guard below has to
    /// be written around: nothing but the view model writing false ever closes
    /// it, and the routes that write false are the two ways out of the box.
    /// </summary>
    [Fact]
    public void The_list_is_only_ever_closed_by_the_view_model()
    {
        var list = TheList();

        Assert.Equal("False", (string?)list.Attribute("IsLightDismissEnabled"));

        Assert.Equal("{Binding ActiveTab.IsPathSuggestionsOpen, Mode=OneWay}",
                     (string?)list.Attribute("IsOpen"));
    }

    // ---- and what the keyboard does around it -------------------------------

    /// <summary>
    /// The address bar as MainWindow builds it, with the list the markup
    /// declares: light dismiss OFF, and <c>IsOpen</c> bound ONE WAY from
    /// <c>IsPathSuggestionsOpen</c>.
    ///
    /// **Both of those are exactly what a hand-driven `new Popup()` hides.** A
    /// rig that opens and closes the popup itself can always close it; the
    /// shipped one can be closed only by the view model, so a guard that waits
    /// for it to close waits for something that is not coming — which is the
    /// whole difficulty and was invisible to a bare popup.
    ///
    /// The list carries something that can hold the keyboard. The shipped rows
    /// are Focusable="False", so that taking one leaves the caret in the path;
    /// but the popup hosts its child in a root of its own, and
    /// <see cref="Reaching_into_the_list_is_not_leaving_the_box"/> measures
    /// that focus landing in there raises the box's LostFocus with
    /// <c>IsFocused</c> false. The hostile case is the one worth rigging.
    /// </summary>
    private sealed record Bar(
        Window Window, PaneViewModel Pane, TextBox Listing, TextBox Box,
        Popup List, TextBox InsideTheList);

    private Bar OpenBar()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        pane.CurrentPath = _root;
        group.Tabs.Add(pane);
        group.ActiveTab = pane;

        var listing = new TextBox();
        var box = new TextBox();
        var insideTheList = new TextBox();

        var list = new Popup
        {
            IsLightDismissEnabled = false,
            Child = new Border { Child = insideTheList },
        };

        box.Bind(TextBox.TextProperty, new Binding("ActiveTab.PathText"));
        box.Bind(Visual.IsVisibleProperty, new Binding("ActiveTab.IsPathEditing"));
        box.SetValue(FocusBehavior.FocusOnVisibleProperty, true);
        box.Bind(FocusBehavior.FocusWhenProperty, new Binding("ActiveTab.FocusPathBox"));
        box.Bind(FocusBehavior.LostFocusCommandProperty,
                 new Binding("ActiveTab.RevertPathTextCommand"));

        FocusBehavior.SetCompanionPopup(box, list);

        list.PlacementTarget = box;
        list.Bind(Popup.IsOpenProperty,
                  new Binding("ActiveTab.IsPathSuggestionsOpen") { Mode = BindingMode.OneWay });

        var panel = new StackPanel { DataContext = group };

        panel.Children.Add(listing);
        panel.Children.Add(box);
        panel.Children.Add(list);

        var window = new Window { Content = panel, Width = 600, Height = 400 };

        window.Show();
        window.Measure(new Size(600, 400));
        window.Arrange(new Rect(0, 0, 600, 400));

        return new Bar(window, pane, listing, box, list, insideTheList);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>An open box with two folders on offer under it.</summary>
    private Bar Offering()
    {
        var bar = OpenBar();

        bar.Pane.BeginEditPath();
        Settle();

        bar.Pane.PathText = _root + Slash + "Ga";
        Settle();

        Assert.Equal(["Galaxy", "Games"], bar.Pane.PathSuggestions.Select(s => s.Name));
        Assert.True(bar.List.IsOpen, "the rig's popup is not following the view model");

        return bar;
    }

    /// <summary>
    /// **Clicking away cancelled the edit, and then the list arrived and it
    /// stopped.** The box was left open, unfocused, under a floating list — and
    /// nothing was coming to close either: the list has light dismiss off, its
    /// IsOpen is one-way, and the only writer of false was the revert that had
    /// just been suppressed. Even Escape could not reach a box with no caret in
    /// it.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_away_while_the_list_is_showing_still_cancels()
    {
        var bar = Offering();

        try
        {
            bar.Listing.Focus();
            Settle();

            Assert.False(bar.Pane.IsPathEditing, "the box stayed open after the keyboard left it");
            Assert.False(bar.List.IsOpen);
            Assert.Equal(_root, bar.Pane.PathText);
        }
        finally
        {
            bar.Window.Close();
        }
    }

    /// <summary>
    /// And the half that keeps the list usable: while the keyboard is INSIDE
    /// the list, the gesture is not over.
    ///
    /// **Measured here: moving focus into the popup raises the box's LostFocus
    /// and leaves IsFocused false**, so without this the guard's own reason for
    /// existing — a row click must not collapse the field out from under the
    /// list being clicked — fails as soon as the pointer is held down long
    /// enough for the dispatcher to idle between press and release.
    /// </summary>
    [AvaloniaFact]
    public void Reaching_into_the_list_is_not_leaving_the_box()
    {
        var bar = Offering();

        try
        {
            bar.InsideTheList.Focus();
            Settle();

            Assert.False(bar.Box.IsFocused, "the rig is not modelling a list that takes the keyboard");

            Assert.True(bar.Pane.IsPathEditing, "the box closed under the list being used");
            Assert.Equal(_root + Slash + "Ga", bar.Pane.PathText);
        }
        finally
        {
            bar.Window.Close();
        }
    }

    /// <summary>
    /// Taking a folder with nothing under it empties the offer, which closes
    /// the list — the one gesture where the list goes away by itself while the
    /// box must stay.
    ///
    /// **Measured: closing the popup hands the keyboard back to whatever held
    /// it before the popup opened**, which is the box. So the question asked a
    /// turn after the focus left is answered by the box being focused again,
    /// and the typed path survives.
    /// </summary>
    [AvaloniaFact]
    public void Taking_the_last_folder_in_a_branch_keeps_the_box_and_the_path()
    {
        var bar = Offering();

        try
        {
            // No settling in between: this is one gesture, and the decision
            // about the focus is still outstanding when the row is taken.
            bar.InsideTheList.Focus();

            bar.Pane.PathSuggestions.Single(s => s.Name == "Games").Apply.Execute(null);
            Settle();

            Assert.False(bar.List.IsOpen, "Games has no children, so the offer should be empty");

            Assert.True(bar.Box.IsFocused, "closing the list did not give the keyboard back");
            Assert.True(bar.Pane.IsPathEditing, "taking a row threw the edit away");
            Assert.Equal(Path.Combine(_root, "Games") + Slash, bar.Pane.PathText);
        }
        finally
        {
            bar.Window.Close();
        }
    }

    // ---- and which thread builds it -----------------------------------------

    /// <summary>
    /// **PathText is written from a pool thread and this collection is bound.**
    /// LoadListingAsync writes it in its synchronous prologue, and undo and redo
    /// reach that prologue off the UI thread — they await the refresh with
    /// ConfigureAwait(false). So an undo performed while the box was open
    /// rebuilt an ItemsSource, and read a directory to do it, on whichever pool
    /// thread was carrying the operation. Measured at two notifications per
    /// undo before the hop.
    ///
    /// Onto a DIFFERENT path, because that is what makes the setter fire: a
    /// refresh that writes back the path already in the box changes nothing and
    /// notifies nobody.
    /// </summary>
    [AvaloniaFact]
    public async Task The_offer_is_never_rebuilt_off_the_ui_thread()
    {
        var pane = await Editing();

        pane.PathText = _root + Slash + "Ga";

        Assert.NotEmpty(pane.PathSuggestions);

        var offThread = 0;

        pane.PathSuggestions.CollectionChanged += (_, _) =>
        {
            if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref offThread);
        };

        await Task.Run(async () => await pane.RefreshAsync().ConfigureAwait(false));

        Settle();

        Assert.Equal(0, offThread);
    }

    private sealed class Inert : IFileSystemProvider
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
