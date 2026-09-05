using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a listing looks like when one click opens.
///
/// **The preference changed what a tap DID and nothing about what a row looked
/// like.** Set to single click, a row under the pointer wore the plain arrow
/// and an unadorned name — exactly what it wears when it takes two clicks — so
/// the only way to find out whether the next click was going to launch
/// something was to click it. That is the wrong way round on the one setting
/// where a misjudged click cannot be taken back.
///
/// Measured before this went in: the only <c>Cursor="Hand"</c> anywhere in
/// MainWindow.axaml was the sidebar's section fold, and the decision itself was
/// a private static read by two click handlers, so there was nothing a style
/// could be keyed off at all.
/// </summary>
public sealed class SingleClickAffordanceTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    /// <summary>A filesystem that answers nothing, for the tests that only ask
    /// a pane what it thinks rather than what is in a folder.</summary>
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

    private PaneViewModel Pane() => Own(new PaneViewModel(new Inert()));

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// Waits on the assertion's own subject under a wall-clock ceiling, rather
    /// than on a fixed number of dispatcher turns — a count is a proxy for the
    /// work finishing and has been the cause of flakes here before.
    /// </summary>
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
    /// Pins the activation preference AND the desktop's answer for one test,
    /// and puts both back.
    ///
    /// Both are statics, and the shipped default for the preference is
    /// "whatever the desktop says" — so a test that did not say which rule it
    /// wanted would measure whichever one an earlier test in this assembly
    /// happened to leave. <see cref="PaneViewModel.SystemSingleClick"/> in
    /// particular is written by ThemeApplier on every palette read, which
    /// includes building a MainWindow.
    /// </summary>
    private sealed class Activation : IDisposable
    {
        private readonly SettingsState _before = AppSettings.Current;
        private readonly bool? _desktopBefore = PaneViewModel.SystemSingleClick;

        public Activation(ActivationClick how, bool? desktop = null)
        {
            AppSettings.Apply(
                _before with { Navigation = _before.Navigation with { OpenItemsWith = how } });

            PaneViewModel.SystemSingleClick = desktop;

            Assert.Equal(how, AppSettings.Current.Navigation.OpenItemsWith);
        }

        public void Dispose()
        {
            AppSettings.Apply(_before);
            PaneViewModel.SystemSingleClick = _desktopBefore;
        }
    }

    // ---- the rule itself ----------------------------------------------------

    [AvaloniaFact]
    public void The_preference_decides_when_it_has_an_opinion()
    {
        using (new Activation(ActivationClick.Single, desktop: false))
            Assert.True(Pane().OpensOnSingleClick);

        using (new Activation(ActivationClick.Double, desktop: true))
            Assert.False(Pane().OpensOnSingleClick);
    }

    /// <summary>
    /// And when it defers, the desktop decides. Null is not "double" — it is
    /// "the desktop never said", which falls back to this application's own
    /// default; the two are asserted separately so a fallback that stopped
    /// distinguishing them cannot pass.
    /// </summary>
    [AvaloniaFact]
    public void The_desktop_decides_when_the_preference_defers()
    {
        using (new Activation(ActivationClick.System, desktop: true))
            Assert.True(Pane().OpensOnSingleClick);

        using (new Activation(ActivationClick.System, desktop: false))
            Assert.False(Pane().OpensOnSingleClick);

        using (new Activation(ActivationClick.System, desktop: null))
            Assert.False(Pane().OpensOnSingleClick);
    }

    /// <summary>
    /// The desktop's answer arrives on the palette and nowhere else, so the one
    /// line that unpacks it is what everything above depends on. Asserted
    /// through a pane rather than by reading the static back, because the pane
    /// is what the markup binds to.
    /// </summary>
    [AvaloniaFact]
    public void A_palette_hands_the_desktop_answer_to_the_panes()
    {
        using var activation = new Activation(ActivationClick.System, desktop: false);

        ThemeApplier.Apply(new Window(), new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            SingleClick = true,
        });

        Assert.True(Pane().OpensOnSingleClick,
                    "the palette said single click and no pane heard about it");
    }

    /// <summary>
    /// The desktop's answer moved off the window and onto the pane, and one
    /// other file documents itself by pointing at it.
    ///
    /// **A comment that names a member which no longer exists is worse than no
    /// comment**, because it reads as a fact and cannot be compiled against:
    /// InterfaceText explains where the desktop's text SIZE is published by
    /// saying it follows this one, and the <c>&lt;c&gt;</c> around the name
    /// means the build has nothing to say when the name goes stale. Measured
    /// across src/Vaktari.Ui, that file is the only place outside this rule's
    /// own two files that names it.
    /// </summary>
    [Fact]
    public void The_file_that_documents_itself_against_this_rule_says_where_it_lives()
    {
        var text = RepoSource.Ui("InterfaceText.cs");

        Assert.DoesNotContain("MainWindow.SystemSingleClick", text, StringComparison.Ordinal);
        Assert.Contains("PaneViewModel.SystemSingleClick", text, StringComparison.Ordinal);
    }

    // ---- and a pane already on screen is told when it changes ---------------

    /// <summary>
    /// Nothing is stored, so nothing raises on its own: a listing already
    /// realized keeps the pointer it was built with until it is told. That is
    /// the trap the font setting fell into for weeks — saved, recorded, and
    /// invisible until the next launch.
    /// </summary>
    [AvaloniaFact]
    public void A_pane_raises_the_question_again_when_asked()
    {
        using var activation = new Activation(ActivationClick.Double);

        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        pane.RefreshActivation();

        Assert.Contains(nameof(PaneViewModel.OpensOnSingleClick), raised);
    }

    /// <summary>
    /// A SAVE reaches every open pane, which is the path a person actually
    /// takes: the setting is on the Navigation page, and the listing behind the
    /// dialog is already realized.
    /// </summary>
    [AvaloniaFact]
    public void Saving_the_preference_reaches_a_pane_already_on_screen()
    {
        using var activation = new Activation(ActivationClick.Double);

        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        var pane = shell.ActiveTab!;
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        AppSettings.Apply(AppSettings.Current with
        {
            Navigation = AppSettings.Current.Navigation with
            {
                OpenItemsWith = ActivationClick.Single,
            },
        });

        shell.OnSettingsChanged();

        Assert.Contains(nameof(PaneViewModel.OpensOnSingleClick), raised);
        Assert.True(pane.OpensOnSingleClick);
    }

    /// <summary>
    /// The other route in, and the one with no save behind it: Plasma can be
    /// switched from double to single while the window is open, and the palette
    /// re-read that follows rewrites the static every pane reads.
    ///
    /// Read from the source because the handler is a lambda built in the
    /// window's constructor, behind a 150ms settle and a platform theme
    /// provider — there is no seam to drive it through. The ORDER is the part
    /// that matters: Apply is what writes the new answer, so a refresh in front
    /// of it would re-raise the old one.
    /// </summary>
    [Fact]
    public void A_desktop_that_changes_its_mind_reaches_the_panes()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        var opens = source.IndexOf("_onThemeChanged = (_, _) =>", StringComparison.Ordinal);
        var closes = source.IndexOf("_theme.Changed += _onThemeChanged;", StringComparison.Ordinal);

        Assert.True(opens > 0, "the theme-changed handler has moved or gone");
        Assert.True(closes > opens, "the theme-changed handler is not subscribed after it is built");

        var handler = source[opens..closes];

        var applied = handler.IndexOf("ThemeApplier.Apply(this, palette)", StringComparison.Ordinal);
        var refreshed = handler.IndexOf("RefreshActivation()", StringComparison.Ordinal);

        Assert.True(applied >= 0, "the handler no longer re-reads the palette");
        Assert.True(refreshed >= 0,
                    "a desktop that switches to single click leaves the rows wearing the old pointer");
        Assert.True(applied < refreshed,
                    "the panes are refreshed before the palette is applied, so they re-read the old answer");
    }

    // ---- what the markup does with it --------------------------------------

    private static List<XElement> Listings()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ListBox")
            .Where(l => (string?)l.Attribute("ItemsSource")
                        is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                           or "{Binding GridEntries}")
            .ToList();

    /// <summary>
    /// Discovered from the markup rather than listed here, so a fourth layout
    /// added later is held to this without anybody remembering — the same way
    /// the row name, the rename box and the selection box are.
    /// </summary>
    [Fact]
    public void Every_listing_says_when_one_click_opens()
    {
        var listings = Listings();

        // A guard, not decoration: a renamed listing must fail here rather than
        // silently drop out of the check below.
        Assert.Equal(3, listings.Count);

        var silent = listings
            .Where(l => (string?)l.Attribute("Classes." + MainWindow.SingleClickClass)
                        != "{Binding OpensOnSingleClick}")
            .Select(l => (string)l.Attribute("ItemsSource")!)
            .ToList();

        Assert.True(silent.Count == 0,
            "these listings look the same whether one click opens or two: "
            + string.Join(", ", silent));
    }

    /// <summary>
    /// The cell an underline goes under. It is the same TextBlock the rename
    /// box replaces, found the same way that test finds it.
    /// </summary>
    [Fact]
    public void Every_layout_names_the_cell_that_draws_the_name()
    {
        var listings = Listings();

        Assert.Equal(3, listings.Count);

        foreach (var listing in listings)
        {
            var name = Assert.Single(
                listing.Descendants(Xaml + "TextBlock"),
                t => ((string?)t.Attribute("Text"))
                     ?.Contains("FileConverters.DisplayName", StringComparison.Ordinal) == true);

            Assert.Equal(MainWindow.RowNameClass, (string?)name.Attribute("Classes"));
        }
    }

    /// <summary>
    /// A class with no style behind it is inert, and would leave every test
    /// above passing over a window that still looks identical either way. All
    /// three parts are asserted: the hand is what the pointer says before it
    /// reaches a row, the underline is what the row itself says, and the I-beam
    /// is the one place the hand is taken back off.
    /// </summary>
    [Fact]
    public void The_window_draws_every_part_of_the_affordance()
    {
        var styles = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "Style")
            .Where(s => ((string?)s.Attribute("Selector"))
                        ?.Contains("ListBox." + MainWindow.SingleClickClass, StringComparison.Ordinal)
                        == true)
            .ToList();

        var hand = Assert.Single(
            styles, s => ((string)s.Attribute("Selector")!).EndsWith("ListBoxItem", StringComparison.Ordinal));

        Assert.Equal(
            "Hand",
            (string?)Assert.Single(hand.Descendants(Xaml + "Setter"),
                                   x => (string?)x.Attribute("Property") == "Cursor")
                           .Attribute("Value"));

        // And the one cell the hand must not reach. It is here rather than in a
        // test of its own because a hand over an open text field is the same
        // rule read from the other side.
        var typing = Assert.Single(
            styles, s => ((string)s.Attribute("Selector")!)
                         .Contains("TextBox." + MainWindow.RenameBoxClass, StringComparison.Ordinal));

        Assert.Equal(
            "Ibeam",
            (string?)Assert.Single(typing.Descendants(Xaml + "Setter"),
                                   x => (string?)x.Attribute("Property") == "Cursor")
                           .Attribute("Value"));

        var underline = Assert.Single(
            styles, s => ((string)s.Attribute("Selector")!).Contains(":pointerover", StringComparison.Ordinal));

        Assert.Contains("TextBlock." + MainWindow.RowNameClass,
                        (string)underline.Attribute("Selector")!);

        Assert.Equal(
            "Underline",
            (string?)Assert.Single(underline.Descendants(Xaml + "Setter"),
                                   x => (string?)x.Attribute("Property") == "TextDecorations")
                           .Attribute("Value"));
    }

    // ---- and in the real window, where the bindings are compiled -----------

    /// <summary>
    /// Asynchronous only so the pane can be put BACK before the window closes.
    ///
    /// **Closing a MainWindow flushes the real session store**, and the store is
    /// redirected per test CLASS rather than per test — so a rig that closed
    /// with the pane parked in a temp folder, and then deleted that folder,
    /// handed the next <see cref="BuildAsync"/> in this class a session pointing
    /// at a path that is gone. Same trap and same fix as ListingRowNameTests,
    /// which documents it.
    /// </summary>
    private sealed record Rig(
        MainWindow Window, ShellViewModel Shell, PaneViewModel Pane, string Root, string? Was)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (Was is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Pane.NavigateAsync(Was));
                Settle();
            }

            // After the navigation, not before it: arriving somewhere can
            // restore that folder's remembered layout. The view a new tab opens
            // in is remembered too, so a test that ended in Grid would
            // otherwise hand Grid to every window the rest of the run builds.
            Pane.View = ViewMode.Details;

            // **Close returns before the window has closed.** OnClosing cancels
            // the first attempt, awaits the session flush and only then closes
            // for real — so a rig that returned here was handing the next test
            // a write still in flight, aimed at a session store shared by the
            // whole class. Waited on the window's own Closed event rather than
            // on a count of dispatcher turns, which is the proxy that caused
            // flakes here before.
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
            Path.GetTempPath(), "vaktari-singleclick-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        // A FOLDER, because opening one navigates — which a test can see and
        // undo. Opening a file hands it to the desktop's own handler, which a
        // test must never do.
        Directory.CreateDirectory(Path.Combine(root, "adir"));

        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        // Captured before the pane moves, and navigated back to in DisposeAsync
        // — see the record above for what closing on a deleted path costs.
        var was = shell.ActiveTab?.CurrentPath;

        await shell.ActiveTab!.NavigateAsync(root);
        Settle();
        window.UpdateLayout();
        Settle();

        Assert.Single(shell.ActiveTab.Entries);

        shell.ActiveTab.View = ViewMode.Details;

        Settle();
        window.UpdateLayout();
        Settle();

        return new Rig(window, shell, shell.ActiveTab, root, was);
    }

    /// <summary>
    /// The three layouts a listing can be in, each realized in turn.
    ///
    /// **Two of the three were held by markup shape only.** <see cref="Rows"/>
    /// filters on <c>IsVisible</c> and the compact and grid ListBoxes are
    /// hidden while the pane is in Details, so a class that bound correctly and
    /// a style that failed to resolve into either of those two templates would
    /// have left every runtime test passing. The grid is the one whose tree
    /// really differs — the name sits in a tile under the custom wrap panel,
    /// wrapped to two lines.
    /// </summary>
    private static readonly ViewMode[] EveryLayout =
        [ViewMode.Details, ViewMode.Compact, ViewMode.Grid];

    /// <summary>
    /// Switches the pane to one layout and hands back the ListBox now on show,
    /// so a caller looping over all three can prove it measured three
    /// different ones rather than Details three times.
    /// </summary>
    private static ListBox Show(Rig rig, ViewMode layout)
    {
        rig.Pane.View = layout;

        Settle();
        rig.Window.UpdateLayout();
        Settle();

        // Not decoration: the layout is chosen by three IsVisible bindings, and
        // if two of them agreed the rows below would be a mix of two templates.
        return Assert.Single(Listings(rig));
    }

    /// <summary>The pane's own listing, whichever of the three is on show.</summary>
    private static List<ListBox> Listings(Rig rig)
        => rig.Window.GetVisualDescendants().OfType<ListBox>()
              .Where(l => l.IsVisible && ReferenceEquals(l.DataContext, rig.Pane))
              .ToList();

    /// <summary>Every realized row of the pane's own visible listing.</summary>
    private static List<ListBoxItem> Rows(Rig rig)
        => Listings(rig)
              .SelectMany(l => l.GetVisualDescendants().OfType<ListBoxItem>())
              .ToList();

    /// <summary>The middle of a control, in the window's own coordinates.</summary>
    private static Point Centre(Visual control, Visual window)
    {
        var at = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);

        Assert.NotNull(at);

        return at!.Value;
    }

    /// <summary>
    /// Says the preference AFTER the window is built, always: MainWindow's own
    /// startup re-reads the settings store, so a preference set before it
    /// exists is gone by the time the first binding is evaluated.
    /// </summary>
    private static void Choose(Rig rig, ActivationClick how)
    {
        var before = AppSettings.Current;

        AppSettings.Apply(
            before with { Navigation = before.Navigation with { OpenItemsWith = how } });

        rig.Shell.RefreshActivation();

        Settle();
        rig.Window.UpdateLayout();
        Settle();
    }

    /// <summary>The cell the underline goes under, in a realized row.</summary>
    private static TextBlock NameIn(ListBoxItem row)
        => Assert.Single(
            row.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Classes.Contains(MainWindow.RowNameClass));

    /// <summary>The cursor the markup's own <c>Hand</c> resolves to.</summary>
    private static string Hand => new Cursor(StandardCursorType.Hand).ToString();

    /// <summary>
    /// The shipped listings, because the class is a COMPILED binding there and
    /// the style is a descendant selector into a DataTemplate — neither of
    /// which a hand-written window in a string would exercise.
    ///
    /// All three layouts, not just the one a new tab opens in: the two hidden
    /// ListBoxes realize nothing while Details is on show, so a style that
    /// failed to reach the compact or grid template would have passed here
    /// unnoticed.
    ///
    /// Cursor is an inherited property in Avalonia, so the setter on the
    /// container is what every cell in the row picks up.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rows_wear_a_hand_only_when_one_click_opens()
    {
        await using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Double);

        // The premise: a Cursor really does say WHICH cursor it is, so the
        // comparisons below are not two arrows agreeing with each other.
        Assert.NotEqual(Hand, new Cursor(StandardCursorType.Arrow).ToString());

        var seen = new List<ListBox>();

        foreach (var layout in EveryLayout)
        {
            seen.Add(Show(rig, layout));

            Choose(rig, ActivationClick.Double);

            var plain = Assert.Single(Rows(rig)).Cursor?.ToString();

            // Named rather than Assert.Equal, because the layout is the thing
            // worth knowing when this fails and Assert.Equal takes no message.
            Assert.True(plain is null,
                        $"{layout}: two clicks are needed and the row wears {plain}");

            Choose(rig, ActivationClick.Single);

            var opening = Assert.Single(Rows(rig)).Cursor?.ToString();

            Assert.True(Hand == opening,
                        $"{layout}: one click opens and the row wears {opening ?? "no cursor of its own"}");
        }

        // The loop's own premise: three layouts means three ListBoxes, not the
        // details one measured three times because the view never changed.
        Assert.Equal(EveryLayout.Length, seen.Distinct().Count());
    }

    /// <summary>
    /// The one cell in the row the hand must not reach, and the measurement
    /// behind the note in the markup.
    ///
    /// **The hand reached the open rename box, and a text field answering the
    /// pointer with a hand is a text field that looks like a link.** Written
    /// first as "a TextBox sets its own I-beam locally, so it is already the
    /// exception", which is the sort of claim that reads obvious; measured on a
    /// realized row, the box's own Cursor came back as Hand. Nothing sets a
    /// cursor on it — not this window, not the Fluent TextBox theme — so an
    /// inherited value with no competition simply won. The third style is the
    /// competition, and this is what says so.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rename_box_keeps_the_pointer_for_typing()
    {
        await using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Single);

        Choose(rig, ActivationClick.Single);

        var row = Assert.Single(Rows(rig));

        // The premise: the row really does carry the hand, so the box below is
        // refusing an inherited value rather than never being offered one.
        Assert.Equal(Hand, row.Cursor?.ToString());

        var box = Assert.Single(
            row.GetVisualDescendants().OfType<TextBox>(),
            t => t.Classes.Contains(MainWindow.RenameBoxClass));

        Assert.Equal(new Cursor(StandardCursorType.Ibeam).ToString(), box.Cursor?.ToString());
    }

    /// <summary>
    /// The other half, and the one that needs a pointer: the name is underlined
    /// while it is the row being pointed at, the way a target that opens on one
    /// click is underlined in a browser. All three layouts, for the reason on
    /// <see cref="EveryLayout"/> — the grid's name sits in a tile rather than a
    /// row, which is where a descendant selector is most likely to miss.
    /// </summary>
    [AvaloniaFact]
    public async Task The_name_under_the_pointer_is_underlined_when_one_click_opens()
    {
        await using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Single);

        foreach (var layout in EveryLayout)
        {
            Show(rig, layout);
            Choose(rig, ActivationClick.Single);

            var row = Assert.Single(Rows(rig));
            var name = NameIn(row);

            // Off the row first, and waited on the row's own answer rather than
            // on a number of turns: the pointer is still where the previous
            // layout left it, which may already be over this one.
            rig.Window.MouseMove(new Point(0, 0), RawInputModifiers.None);

            await Until(() => !row.IsPointerOver, "the pointer would not leave the row");

            Assert.Null(name.TextDecorations);

            rig.Window.MouseMove(Centre(row, rig.Window), RawInputModifiers.None);

            await Until(() => row.IsPointerOver, "the pointer never landed on the row");

            rig.Window.UpdateLayout();
            Settle();

            Assert.Contains(
                TextDecorationLocation.Underline,
                (name.TextDecorations ?? []).Select(d => d.Location));

            // And it is the SETTING that put it there, not the hover alone:
            // with two clicks needed, the same pointer on the same row draws
            // nothing.
            Choose(rig, ActivationClick.Double);

            Assert.True(row.IsPointerOver, "the pointer left the row, so this proves nothing");
            Assert.Null(name.TextDecorations);
        }
    }

    /// <summary>
    /// The rig's own housekeeping, asserted rather than trusted.
    ///
    /// **A test that closes a window with the pane parked in a folder it then
    /// deletes hands that path to the next test in the class.** Closing a
    /// MainWindow flushes the real session store and the store is redirected
    /// per test class, not per test — so the four rigs here pass each other a
    /// session, and one pointing at a deleted temp folder is a failure with
    /// somebody else's name on it. The remembered layout is the same shape of
    /// problem: a rig that ended in Grid would open every later window in Grid.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rig_puts_the_pane_back_before_it_closes()
    {
        var rig = await BuildAsync();

        Assert.NotNull(rig.Was);
        Assert.NotEqual(rig.Was, rig.Pane.CurrentPath);

        Show(rig, ViewMode.Grid);

        var closed = false;

        rig.Window.Closed += (_, _) => closed = true;

        await rig.DisposeAsync();

        Assert.Equal(rig.Was, rig.Pane.CurrentPath);
        Assert.Equal(ViewMode.Details, rig.Pane.View);

        // Closing is deferred — OnClosing cancels the first attempt and flushes
        // the session before closing for real — so returning from Close is not
        // the same as being closed.
        Assert.True(closed, "the rig returned while the window was still closing");
    }

    /// <summary>
    /// **In the bin the affordance was a lie.** Every row wore the hand and
    /// underlined its name under the pointer — one click opens this — and the
    /// click reached <c>OpenAsync</c>, whose first act on a binned row is to
    /// refuse: the path on that row is the one the item USED to occupy, so
    /// opening it either launches whatever now sits there or navigates
    /// nowhere. Proven already, from the other end, by
    /// <c>BinIsNotAFolderTests.Opening_a_bin_row_directly_reaches_no_launcher</c>.
    ///
    /// The pane is walked into the bin and back out again, because the answer
    /// is computed from the path and nothing raises for a computed property on
    /// its own — the hand followed the pane in, and then stayed behind.
    /// </summary>
    [AvaloniaFact]
    public async Task No_hand_in_the_bin_where_one_click_opens_nothing()
    {
        await using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Single);

        Choose(rig, ActivationClick.Single);

        var here = rig.Pane.CurrentPath;

        Assert.Equal(Hand, Assert.Single(Rows(rig)).Cursor?.ToString());

        // The path alone, rather than a real trash listing: the rows on show
        // are beside the point — what the listing binds to is the pane saying
        // where it is, and this is the same assignment LoadListingAsync makes.
        rig.Pane.CurrentPath = VirtualPaths.Trash;

        await Until(() => !rig.Pane.OpensOnSingleClick,
                    "the pane still says one click opens while it is in the bin");

        rig.Window.UpdateLayout();
        Settle();

        Assert.Null(Assert.Single(Rows(rig)).Cursor);

        Assert.Null(NameIn(Assert.Single(Rows(rig))).TextDecorations);

        // And back out: a hand that never returns is the same bug wearing the
        // other face.
        rig.Pane.CurrentPath = here;

        await Until(() => rig.Pane.OpensOnSingleClick,
                    "the hand never came back after leaving the bin");

        rig.Window.UpdateLayout();
        Settle();

        Assert.Equal(Hand, Assert.Single(Rows(rig)).Cursor?.ToString());
    }

    /// <summary>
    /// And the preference still OPENS on one click, which is the behaviour the
    /// affordance is advertising. The window's own <c>OpensOnSingleClick</c>
    /// now reads the same rule the listings bind to, so this is what stops the
    /// two answers drifting apart.
    /// </summary>
    [AvaloniaFact]
    public async Task One_click_opens_a_folder_when_the_preference_says_so()
    {
        await using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Single);

        Choose(rig, ActivationClick.Single);

        var target = Path.Combine(rig.Root, "adir");
        var at = Centre(Assert.Single(Rows(rig)), rig.Window);

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);

        await Until(() => rig.Pane.CurrentPath == target,
                    "one click did not open the folder with single click set");
    }

    /// <summary>
    /// The other side of the same rule, so "it opened" is not just "a click
    /// always opens".
    /// </summary>
    [AvaloniaFact]
    public async Task One_click_does_not_open_a_folder_otherwise()
    {
        await using var rig = await BuildAsync();
        using var activation = new Activation(ActivationClick.Double);

        Choose(rig, ActivationClick.Double);

        var before = rig.Pane.CurrentPath;
        var at = Centre(Assert.Single(Rows(rig)), rig.Window);

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);

        await Until(() => rig.Pane.SelectedEntry is not null,
                    "the click never even selected the row");

        Assert.Equal(before, rig.Pane.CurrentPath);
    }
}
