using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Session;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// How large the interface's text is drawn, and who gets to say.
///
/// **Nothing in Vaktari could make the interface bigger and nothing looked at
/// the setting where the answer already was.** The one application-level type
/// scale was written from the restored window geometry and read straight back
/// into it — no control in Settings, no menu item, no key touched it — so the
/// sidebar, the tab strip, the toolbar and the status bar stayed at the size
/// chosen at build time however the panes were zoomed. And the desktop's own
/// text size was ignored on both platforms: Plasma's point size was parsed into
/// <see cref="ThemePalette.FontSize"/> and never read, and Windows' "Make text
/// bigger" percentage was not read at all.
///
/// These go through the surfaces the window really uses — the metric funnel
/// both the application defaults and each pane are computed through, the
/// dictionary <c>ThemeApplier</c> writes, and the settings view model the
/// dialog binds to — because a size control fails in the way the font setting
/// once did: it renders, it saves, and the value never reaches the screen.
/// </summary>
public sealed class InterfaceTextSizeTests : OwnedViewModels
{
    // Both are statics the metric pipeline reads, so every test here has to put
    // them back — Vaktari.Ui.Tests runs its classes in sequence, which makes a
    // leak land on somebody else's assertion rather than on this file's.
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly double? _systemBefore = InterfaceText.SystemScale;

    /// <summary>
    /// **Stated, not inherited.** The published value is the machine's own
    /// desktop setting once a window has read a palette, so a test that assumed
    /// 1.0 would pass here and fail on a machine whose text size is not 100% —
    /// exactly the class of machine this feature exists for.
    /// </summary>
    public InterfaceTextSizeTests() => InterfaceText.SystemScale = null;

    public override void Dispose()
    {
        AppSettings.Apply(_settingsBefore);
        InterfaceText.SystemScale = _systemBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Configure(double scale)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { InterfaceTextScale = scale },
        });

    private static double Metric(string key, double fontScale = 1.0, double iconScale = 1.0)
        => PaneScale.Compute(fontScale, iconScale).Single(m => m.Key == key).Value;

    // ---- the funnel -------------------------------------------------------

    /// <summary>
    /// The whole point: one factor, applied where the chrome and the listings
    /// are computed by the same arithmetic.
    /// </summary>
    [AvaloniaFact]
    public void The_interface_size_multiplies_every_type_metric()
    {
        Configure(1.5);

        Assert.Equal(21, Metric("FontSizeBase"));

        // The metadata columns hold text, so they are on the same axis: 150 at
        // 100%, and a pane still at its own 100% asks for 225.
        Assert.Equal(225, Metric("ColModified"));
    }

    /// <summary>
    /// **The font axis only.** Windows' slider and Plasma's general font are
    /// settings about text; growing the icons with them would be this window
    /// answering a question nobody asked, and the icons have their own control.
    /// </summary>
    [AvaloniaFact]
    public void And_leaves_the_icons_where_they_were()
    {
        Configure(2.0);

        Assert.Equal(PaneScale.BaseIcon(Vaktari.Core.Session.ViewMode.Details), Metric("ThumbSize"));
        Assert.Equal(PaneScale.RowIcon, Metric("IconSize"));
    }

    /// <summary>
    /// A row still has to fit the taller of its label and its icon, so the rows
    /// grow even though the icons did not. Asserted because it is the one
    /// consequence of the previous test that would look like a bug on screen if
    /// it were missing: 200% text inside a 30px row is clipped text.
    /// </summary>
    [AvaloniaFact]
    public void So_the_rows_grow_with_the_text_they_hold()
    {
        var before = Metric("RowHeight");

        Configure(2.0);

        Assert.True(Metric("RowHeight") > before,
            $"a row of 28px type must be taller than {before}");
    }

    // ---- the rows that are not listings -----------------------------------

    /// <summary>
    /// **The chrome stated its heights in pixels, and the text inside it is
    /// what this change grows.** Measured before these metrics existed, with
    /// the headless font: at 200% the status line wanted 28px inside the 25 a
    /// 26px bar leaves under its border, and the search field wanted 28 inside
    /// 18. Segoe UI's line box is taller again, so the shipped window clipped
    /// its own status line from about 150% — while the dialog's own blurb
    /// promised "every label, row, tab and status line".
    /// </summary>
    [AvaloniaTheory]
    [InlineData("ChromeRowHeight")]
    [InlineData("ChromeToolbarHeight")]
    [InlineData("ChromeStepWidth")]
    [InlineData("ChromeBoxWidth")]
    [InlineData("ChromeSearchWidth")]
    [InlineData("ChromeFilterWidth")]
    public void The_chrome_rows_follow_the_interface_size(string metric)
    {
        var before = Metric(metric);

        Configure(2.0);

        Assert.Equal(before * 2, Metric(metric), 1);
    }

    /// <summary>
    /// And they fit the text they hold, at every size the combo offers.
    ///
    /// **Measured rather than asserted as a ratio**: the fault was a container
    /// shorter than a line of text, so the only honest check lays out the same
    /// text at the same size and asks how tall it came out. The headless font
    /// is not Segoe UI and its line box is shorter, so the margin here is
    /// narrower on a real machine than it reads — which is the safe direction
    /// for a fit test to be wrong in, and the reason the room left over is
    /// asserted rather than the exact numbers.
    ///
    /// The status bar keeps 1px of top border; the search field keeps 1px of
    /// border and 3px of padding at each edge, which is what its own markup
    /// sets.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void And_still_fit_the_text_they_hold(double scale)
    {
        Configure(scale);

        var line = new Avalonia.Controls.TextBlock
        {
            Text = "36 items · 1.4 GB",
            FontSize = Metric("FontSizeSmall"),
        };

        line.Measure(Avalonia.Size.Infinity);

        var text = line.DesiredSize.Height;
        var bar = Metric("ChromeRowHeight");

        Assert.True(bar - 1 >= text,
            $"the status bar leaves {bar - 1}px for a line {text}px tall");

        Assert.True(bar - 2 - 6 >= text,
            $"the search field leaves {bar - 8}px for a line {text}px tall");
    }

    /// <summary>
    /// And the markup takes those numbers rather than restating them. Every
    /// height this asserts was a literal, and a literal is how the status bar
    /// came to be 26px tall around 28px text.
    /// </summary>
    [Fact]
    public void And_the_chrome_reads_them_instead_of_stating_pixels()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));
        var ns = markup.Root!.GetDefaultNamespace();

        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var sized = markup.Descendants()
            .Where(e => (string?)e.Attribute(x + "Name") is "StatusBar" or "SearchBox")
            .ToList();

        Assert.Equal(2, sized.Count);

        foreach (var element in sized)
            Assert.Equal("{DynamicResource ChromeRowHeight}", (string?)element.Attribute("Height"));

        // The toolbar and the two toolbar fields, found by the metric they are
        // supposed to be using — so a height put back as a number takes its
        // element out of this list rather than merely changing a value in it.
        var heights = markup.Descendants()
            .Select(e => (string?)e.Attribute("Height"))
            .Where(h => h is not null && h.Contains("Chrome", StringComparison.Ordinal))
            .ToList();

        Assert.Contains("{DynamicResource ChromeToolbarHeight}", heights);

        // Four flyout buttons, two flyout boxes, the search button, the search
        // field, the filter field and the status bar.
        Assert.Equal(10, heights.Count(h => h == "{DynamicResource ChromeRowHeight}"));
    }

    // ---- who decides ------------------------------------------------------

    /// <summary>Somebody who opened Settings and chose 150% has said something
    /// about Vaktari, not about their desktop.</summary>
    [AvaloniaFact]
    public void A_chosen_size_wins_over_the_desktops()
    {
        InterfaceText.SystemScale = 2.0;
        Configure(1.25);

        Assert.Equal(1.25, InterfaceText.Scale);
    }

    /// <summary>
    /// **Zero is the value every upgrading install has**, because
    /// deserialization does not run property initializers — so zero has to mean
    /// the wanted behaviour, and the wanted behaviour is to follow the desktop
    /// that has already been told how big this person needs text.
    ///
    /// The desktop's answer is clamped to the same range as everything else:
    /// Windows' own slider stops at 225%, which is inside it, but a Plasma
    /// general font is a point size and can say anything at all.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.4, 1.4)]
    [InlineData(2.25, 2.25)]
    [InlineData(3.0, InterfaceText.Maximum)]
    [InlineData(0.2, InterfaceText.Minimum)]
    public void And_nothing_chosen_follows_the_desktop(double desktop, double expected)
    {
        InterfaceText.SystemScale = desktop;
        Configure(0);

        Assert.Equal(expected, InterfaceText.Scale);
    }

    /// <summary>A desktop that says nothing leaves the window exactly as it
    /// shipped, which is what every machine at its default gets.</summary>
    [AvaloniaFact]
    public void A_silent_desktop_changes_nothing()
    {
        InterfaceText.SystemScale = null;
        Configure(0);

        Assert.Equal(1.0, InterfaceText.Scale);
        Assert.Equal(14, Metric("FontSizeBase"));
    }

    /// <summary>
    /// A settings.json edited by hand can say 40 as easily as 1.4, and Windows'
    /// own slider reaches 225%. Both ends are clamped to the range the per-pane
    /// zoom already uses, since the two multiply each other.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(40.0, InterfaceText.Maximum)]
    [InlineData(0.01, InterfaceText.Minimum)]
    public void An_impossible_size_is_clamped(double configured, double expected)
    {
        Configure(configured);

        Assert.Equal(expected, InterfaceText.Scale);
    }

    /// <summary>
    /// **The numbers it is clamped to are the pane zoom's own**, stated here
    /// because the two multiply each other: a ceiling of 2.5 on each is 6.25x
    /// type between them, and a third number would make the product something
    /// nobody had chosen. The pane end is asserted through the box that edits
    /// it, since its own constants are private to it.
    /// </summary>
    [AvaloniaFact]
    public void And_to_the_range_the_pane_zoom_already_uses()
    {
        Assert.Equal(0.7, InterfaceText.Minimum);
        Assert.Equal(2.5, InterfaceText.Maximum);

        var pane = Pane();

        pane.FontPoints = 999;
        Assert.Equal(InterfaceText.Maximum, pane.FontScale, 3);

        pane.FontPoints = 1;
        Assert.Equal(InterfaceText.Minimum, pane.FontScale, 3);
    }

    // ---- what the desktop said --------------------------------------------

    /// <summary>
    /// **A Plasma point size only means something as a ratio to Plasma's own
    /// default.** 12.5pt is not 12.5/14ths of Vaktari's body text — it is the
    /// desktop's 10 raised by a quarter. Read the other way, a stock Plasma
    /// would have shrunk the window by 5% the first time it started.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(10.0, 1.0)]
    [InlineData(12.5, 1.25)]
    [InlineData(null, null)]
    [InlineData(0.0, null)]
    public void A_desktop_font_size_is_read_against_that_desktops_default(
        double? points, double? expected)
        => Assert.Equal(expected, InterfaceText.FromDesktopFontSize(points));

    /// <summary>
    /// Windows states a percentage and no point size; Plasma states a point
    /// size and no percentage. A palette carrying both is a desktop this does
    /// not know, and the percentage is the one a person moved on purpose.
    /// </summary>
    [AvaloniaFact]
    public void A_stated_percentage_beats_a_font_size()
    {
        var palette = new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            FontSize = 20,
            TextScale = 1.25,
        };

        Assert.Equal(1.25, InterfaceText.FromPalette(palette));
    }

    /// <summary>
    /// The palette is where both facts arrive, and <c>ThemeApplier.Apply</c> is
    /// the one place every read of it funnels through — startup, a desktop
    /// scheme change and a settings save all reach it. Publishing anywhere else
    /// is how the value would come to describe a palette that is no longer on
    /// screen.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(12.5, null, 1.25)]
    [InlineData(null, 1.5, 1.5)]
    public void Applying_a_palette_publishes_the_text_size_it_carried(
        double? points, double? stated, double expected)
    {
        InterfaceText.SystemScale = 99;

        ThemeApplier.Apply(new Avalonia.Controls.Window(), new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            FontSize = points,
            TextScale = stated,
        });

        Assert.Equal(expected, InterfaceText.SystemScale);
    }

    // ---- the pane's own readouts and thresholds ---------------------------

    private sealed class InertFileSystem : IFileSystemProvider
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

    private PaneViewModel Pane() =>
        Own(new PaneViewModel(new InertFileSystem()) { ViewportWidth = 600 });

    /// <summary>
    /// **The size box has to quote what is on screen**, which is the rule the
    /// icon box beside it was fixed under after it read 26 next to an 18px
    /// icon. At 150% a pane at its own 100% draws 21px type.
    /// </summary>
    [AvaloniaFact]
    public void The_size_box_quotes_the_size_that_is_drawn()
    {
        var pane = Pane();

        Configure(1.5);

        Assert.Equal(21, pane.FontPoints);
    }

    /// <summary>And typing into it still lands on the size typed, by moving the
    /// only thing that control owns — this pane's zoom.</summary>
    [AvaloniaFact]
    public void And_typing_a_size_into_it_still_lands_there()
    {
        var pane = Pane();

        Configure(1.5);
        pane.FontPoints = 28;

        Assert.Equal(28, pane.FontPoints);
        Assert.NotEqual(2.0, pane.FontScale);
    }

    /// <summary>
    /// **Until the size typed is one this pane cannot be**, which the comment
    /// on the setter used to deny. The pane's zoom is a multiplier ON the
    /// interface size and clamps to 0.7–2.5 of it, so the range the box can
    /// express moves with that size: measured, at 250% the smallest is 0.7 x 14
    /// x 2.5, and typing 21 lands on 24. At the combo's own top row, 200%, the
    /// box bottoms out at 20.
    ///
    /// Asserted rather than fixed: a pane that could be zoomed back to 14px
    /// inside a 200% window would be a second control undoing the first.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.5, 28, 28)]
    [InlineData(2.5, 21, 24)]
    [InlineData(2.0, 14, 20)]
    [InlineData(1.0, 99, 35)]
    public void Unless_it_is_a_size_this_pane_cannot_be(
        double interfaceSize, double typed, double lands)
    {
        var pane = Pane();

        Configure(interfaceSize);
        pane.FontPoints = typed;

        Assert.Equal(lands, pane.FontPoints);
    }

    /// <summary>
    /// **Column thresholds are measured in text width, and the interface size
    /// is half of how wide the text is.** Left following the pane's zoom alone,
    /// a 600px pane at 200% would go on claiming every column fits while each
    /// one overflowed — the fault fixed thresholds had at 2x, reintroduced by
    /// the global factor.
    /// </summary>
    [AvaloniaFact]
    public void Column_thresholds_follow_the_interface_size()
    {
        var pane = Pane();

        Assert.True(pane.ShowModified, "a 600px pane shows Modified at 100%");

        Configure(2.0);
        pane.FontScale = 1.1;

        Assert.Equal(2.2, pane.TextScale, 3);
        Assert.False(pane.ShowModified, "at 200% the same 600px pane cannot fit it");
    }

    /// <summary>
    /// And they move when the interface size changes on its own, which is the
    /// case the pane's own scale cannot report: nothing about this pane moved,
    /// so nothing it raises would have said so.
    /// </summary>
    [AvaloniaFact]
    public void Even_when_the_panes_own_zoom_never_moves()
    {
        var pane = Pane();

        Configure(2.0);
        pane.RefreshScale();

        Assert.Equal(2.0, pane.TextScale, 3);
        Assert.False(pane.ShowModified);
    }

    /// <summary>
    /// **And a pane BUILT at a larger interface size starts there**, which the
    /// first cut of this got wrong: the threshold multiplier was only ever
    /// written from the pane's own zoom changing, so a pane nobody had zoomed
    /// held 1.0 while its text was drawn at 200%. Measured, in a 600px pane at
    /// 200%: Modified and Size both still "fitted", which reserved 500px of a
    /// 600px viewport for two metadata columns and left 100px for the name.
    ///
    /// Startup is that case, and so is every Ctrl+T and every new window —
    /// the paths where the pane's zoom is assigned the value it already holds,
    /// so nothing changes and nothing is raised.
    /// </summary>
    [AvaloniaFact]
    public void A_pane_built_at_that_size_starts_there()
    {
        Configure(2.0);

        var pane = Pane();

        Assert.Equal(2.0, pane.TextScale, 3);
        Assert.False(pane.ShowModified, "a 600px pane at 200% cannot fit Modified");
    }

    /// <summary>The same pane, reached the way startup reaches it: a shell that
    /// restores its tabs rather than a constructor a test called.</summary>
    [AvaloniaFact]
    public void And_so_does_every_pane_a_shell_starts_with()
    {
        Configure(2.0);

        var shell = Started();

        Assert.All(shell.Left.Tabs, tab => Assert.Equal(2.0, tab.TextScale, 3));
    }

    /// <summary>
    /// And a tab opened beside one that is already right. It copies the other
    /// pane's zoom, which is the value it already has — so the copy raises
    /// nothing, and only what the pane was built with can be correct here.
    /// </summary>
    [AvaloniaFact]
    public void And_a_tab_opened_beside_one_that_never_moved()
    {
        var shell = Started();

        Configure(2.0);
        shell.OnSettingsChanged();

        var opened = shell.ActiveGroup!.AddTab(Path.GetTempPath(), like: shell.ActiveTab);

        Assert.Equal(2.0, opened.TextScale, 3);
    }

    /// <summary>The size box is republished by the same refresh, or it would go
    /// on quoting a size nothing on screen is any more.</summary>
    [AvaloniaFact]
    public void And_the_size_box_is_told_as_well()
    {
        var pane = Pane();
        var raised = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.FontPoints)) raised++;
        };

        Configure(1.5);
        pane.RefreshScale();

        Assert.Equal(1, raised);
    }

    // ---- reaching every pane after a change -------------------------------

    private ShellViewModel Started()
    {
        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Path.GetTempPath());

        return shell;
    }

    /// <summary>
    /// A desktop scheme change does not go through Save, so none of the routes
    /// that already re-apply the metrics run. This is the one that does.
    /// </summary>
    [AvaloniaFact]
    public void A_desktop_change_reaches_every_open_pane()
    {
        var shell = Started();
        var boxes = 0;

        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.TargetFontPoints)) boxes++;
        };

        Configure(1.75);
        shell.RefreshPaneScales();

        Assert.All(shell.Left.Tabs, tab => Assert.Equal(1.75, tab.TextScale, 3));

        // The flyout's boxes hang off the SHELL, so the panes being told is not
        // the same as the numbers beside them being told — and a desktop change
        // moves the size a pane draws at without touching the pane's own zoom.
        Assert.True(boxes > 0, "the size boxes went on quoting the old size");
    }

    /// <summary>
    /// The flyout's boxes read the pane's sizes THROUGH the shell, so a pane
    /// notification does not reach them.
    /// </summary>
    [AvaloniaFact]
    public void And_the_menus_size_boxes_are_told_after_a_save()
    {
        var shell = Started();
        var raised = 0;

        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.TargetFontPoints)) raised++;
        };

        Configure(1.5);
        shell.OnSettingsChanged();

        Assert.True(raised > 0, "the boxes were never told the interface size moved");
        Assert.Equal(21, shell.TargetFontPoints);
    }

    // ---- and every open WINDOW ---------------------------------------------

    /// <summary>
    /// **A save reached only the window whose dialog it was.** The metrics the
    /// chrome reads are one application-level dictionary, so a second window's
    /// sidebar, toolbar, tab strip and status bar took the new size the instant
    /// it was written — while every pane keeps its own dictionary, which
    /// shadows that one and is rewritten only when the pane is told. So the
    /// peer window drew 28px chrome around 14px rows, and its columns went on
    /// being measured against a text size that had been replaced.
    ///
    /// Two real windows, because the fault is in what a window's own shell is
    /// told: a second ShellViewModel built by hand would be a peer this code
    /// path has no way of finding.
    /// </summary>
    [AvaloniaFact]
    public async Task A_save_reaches_every_open_window()
    {
        await EmptySessionAsync();

        var searchBefore = PaneViewModel.Search;
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Dispatcher.UIThread.RunJobs();

            founder.Shell.NewWindowCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var peer = founder.Services.Windows.First(w => !ReferenceEquals(w, founder));

            Configure(2.0);
            founder.SettingsChangedEverywhere();

            Assert.All(peer.Shell.Left.Tabs, tab => Assert.Equal(2.0, tab.TextScale, 3));
        }
        finally
        {
            foreach (var window in founder.Services.Windows.ToList().AsEnumerable().Reverse())
            {
                try { window.Close(); }
                catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
            }

            Dispatcher.UIThread.RunJobs();
            PaneViewModel.Search = searchBefore;
        }
    }

    /// <summary>
    /// The session this test's window starts from, so it does not restore
    /// whatever an earlier test in this class left behind — the state directory
    /// is per test class and a closing window writes its own session into it.
    /// </summary>
    private static async Task EmptySessionAsync()
    {
        var directory = TestState.Current();

        Directory.CreateDirectory(directory);

        var store = new JsonSessionStore(directory);

        store.NotifyChanged(new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows = [],
        });

        await store.FlushAsync(CancellationToken.None);
        await store.DisposeAsync();
    }

    /// <summary>
    /// And the save handler is what calls it.
    ///
    /// **A source assertion for the reason the desktop-change one above gives**:
    /// the handler runs when the settings dialog closes, and a test that drove
    /// a modal dialog to prove one line would be waiting on the dialog rather
    /// than on what it asserts. What is checkable is that the save applies to
    /// the family rather than to <c>_shell</c> alone.
    /// </summary>
    [Fact]
    public void And_the_save_handler_is_what_calls_it()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        var save = source.IndexOf("if (!model.Saved) return;", StringComparison.Ordinal);

        Assert.True(save >= 0, "the settings-save handler is not written the way this looks for it");

        var broadcast = source.IndexOf(
            nameof(MainWindow.SettingsChangedEverywhere) + "();", save, StringComparison.Ordinal);

        Assert.True(broadcast >= 0, "the save tells only its own window");
    }

    // ---- the control in Settings ------------------------------------------

    private static SettingsState Stored(double scale) => new()
    {
        Views = new ViewSettings { InterfaceTextScale = scale },
    };

    [AvaloniaTheory]
    [InlineData(0.0, 0)]
    [InlineData(1.25, 3)]
    [InlineData(2.0, 6)]
    // A size no row offers — hand-written, or left by a version that offered a
    // different list. It must not silently become the row that happens to sit
    // at its index.
    [InlineData(1.37, 0)]
    public void The_stored_size_selects_the_matching_row(double stored, int expected)
    {
        var vm = new SettingsViewModel(Stored(stored));

        Assert.Equal(expected, vm.InterfaceTextIndex);
    }

    [AvaloniaTheory]
    [InlineData(0, 0.0)]
    [InlineData(3, 1.25)]
    [InlineData(6, 2.0)]
    // A row nobody offered — an empty ComboBox reports -1, and a list that lost
    // a row would report an index past its end. Neither can become a text size;
    // both mean the first row, which is the one that changes nothing.
    [InlineData(-1, 0.0)]
    [InlineData(99, 0.0)]
    public void The_selected_row_is_what_gets_saved(int row, double expected)
    {
        var vm = new SettingsViewModel(Stored(0)) { InterfaceTextIndex = row };

        vm.SaveCommand.Execute(null);

        Assert.Equal(expected, vm.Result.Views.InterfaceTextScale);
    }

    /// <summary>
    /// Opening the dialog and pressing Save without touching anything gives
    /// back what was there — the regression that matters most for a setting
    /// that has just acquired its first control.
    /// </summary>
    [AvaloniaFact]
    public void Saving_without_touching_it_preserves_it()
    {
        var vm = new SettingsViewModel(Stored(1.5));

        vm.SaveCommand.Execute(null);

        Assert.Equal(1.5, vm.Result.Views.InterfaceTextScale);
    }

    /// <summary>
    /// The palette read that repaints on a desktop change also re-runs the
    /// metrics, because the text size arrives on that same palette.
    ///
    /// **A source assertion, and the reason is worth stating**: the handler
    /// posts through the dispatcher twice with a 150ms settle in between, and a
    /// test that waited for it would be waiting on a delay rather than on what
    /// it asserts — the mistake behind three flakes in this suite. What is
    /// checkable without inventing a clock is that the rescale is in the
    /// handler, ahead of the icon reload it shares the block with — and that
    /// what it calls does both halves of the job, since the chrome reads the
    /// application dictionary and every pane reads its own.
    /// </summary>
    [Fact]
    public void A_desktop_scheme_change_re_runs_the_metrics()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        var handler = source.IndexOf("var palette = _theme.Read();", StringComparison.Ordinal);

        Assert.True(handler >= 0, "the theme-changed handler is not written the way this looks for it");

        var rescale = source.IndexOf("RescaleForDesktop();", handler, StringComparison.Ordinal);
        var icons = source.IndexOf("platformIcons?.Reload", handler, StringComparison.Ordinal);

        Assert.True(rescale >= 0 && rescale < icons,
            "the handler repaints the colours and never re-runs the sizes");

        var method = source.IndexOf(
            "private void RescaleForDesktop()", StringComparison.Ordinal);

        Assert.True(method >= 0, "RescaleForDesktop is not written the way this looks for it");

        var body = source[method..source.IndexOf('}', method)];

        Assert.Contains("ApplyScales(", body, StringComparison.Ordinal);
        Assert.Contains("RefreshPaneScales();", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The rows are decoded by index**, so the markup's list and
    /// <see cref="InterfaceText.Steps"/> are two halves of one table. A row
    /// added to one and not the other stores the size of whichever row happens
    /// to share its position — a silent wrong answer, and the reason this is
    /// checked against the markup rather than trusted.
    ///
    /// **The LABELS, not the count.** Counting was the first cut of this, and
    /// it was measured toothless: relabelling the third row "115%" while it
    /// went on storing 1.1 left the whole class green. A row that says one size
    /// and stores another is exactly the wrong answer this exists to catch, and
    /// it is the one a count cannot see.
    /// </summary>
    [Fact]
    public void The_dialog_offers_one_row_per_size_it_can_store()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));
        var ns = markup.Root!.GetDefaultNamespace();

        var combo = markup.Descendants(ns + "ComboBox").Single(c =>
            (string?)c.Attribute("SelectedIndex")
                == "{Binding " + nameof(SettingsViewModel.InterfaceTextIndex) + "}");

        var labels = combo.Elements(ns + "ComboBoxItem")
            .Select(item => (string?)item.Attribute("Content"))
            .ToList();

        Assert.Equal(InterfaceText.Steps.Length, labels.Count);

        // Row zero stores the setting's own zero, which means "follow the
        // desktop" — so it is the one row that cannot be a percentage.
        Assert.Equal(0, InterfaceText.Steps[0]);
        Assert.Equal("Follow the desktop text size", labels[0]);

        for (var row = 1; row < labels.Count; row++)
            Assert.Equal(
                $"{InterfaceText.Steps[row] * 100:0.##}%",
                labels[row]);
    }
}
