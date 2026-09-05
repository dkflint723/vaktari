using System.Reflection;
using System.Windows.Input;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The View submenu on the listing's own context menu.
///
/// **How a listing is drawn had no menu anywhere in the application.** The
/// three layouts were the chip at the top right of the pane and
/// Ctrl+Shift+1..3; hidden files were Ctrl+H and a checkbox inside the view
/// options flyout. Nothing in the right-click menu said either existed — its
/// Arrange submenu held Sort by and Group by and stopped there.
///
/// The flyout is the part that made it a hole rather than an inconvenience:
/// its button carries <c>IsVisible="{Binding ShowsWindowControls}"</c>, and
/// <c>SyncWindowControls</c> sets that false on the LEFT half of a split. So on
/// that side hidden files had no pointer route at all, and the only way to it
/// was a key nobody is told about.
///
/// Two halves are checked here, and both are needed. The markup half pins the
/// rows, their headers and — through <see cref="Gesture"/> — the KEY EACH ROW
/// ENDS UP CARRYING rather than the characters somebody typed into the
/// attribute; the behaviour half runs whatever command each row actually names
/// against a real pane, because a binding path that names nothing is not a
/// build error — it is a silent row that does nothing when picked.
///
/// **Asserting the attribute text was not enough, and the first draft of this
/// file did exactly that.** InputGesture is a KeyGesture, and Avalonia parses a
/// bare digit through Enum.TryParse&lt;Key&gt;, which reads it NUMERICALLY: the
/// honest-looking "Ctrl+Shift+1" became Key.Cancel, "Ctrl+Shift+2" Key.Back and
/// "Ctrl+Shift+3" Key.Tab — so the third row drew "Ctrl+Shift+Tab", which is a
/// LIVE binding that switches tab. Every assertion below goes through the
/// parser for that reason.
/// </summary>
public sealed class ViewMenuTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ---- statics this file borrows -----------------------------------------

    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private readonly IFolderViewStore? _viewsBefore = PaneViewModel.FolderViews;
    private readonly Scratch _views = new();

    /// <summary>
    /// **Setting <c>pane.View</c> ends in RememberFolderView, which writes
    /// through whichever store the static happens to hold.** This assembly does
    /// not run in parallel, and a real MainWindow built by an earlier class
    /// points that static at a JsonFolderViewStore over the user's own config
    /// directory — so a layout picked from the menu here would be recorded
    /// against the machine's real settings for whatever folder the pane was
    /// showing. Both statics are taken for the run and given back in
    /// <see cref="Dispose"/>.
    ///
    /// Remembering is switched ON rather than left at its shipped default of
    /// off, so that the write really happens and
    /// <see cref="Picking_a_layout_is_recorded_against_the_folder_and_goes_no_further"/>
    /// can watch where it lands. Borrowing a static and never exercising it
    /// proves nothing about the borrow.
    /// </summary>
    public ViewMenuTests()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(new SettingsState
        {
            General = new GeneralSettings { RememberViewPerFolder = true },
        });

        PaneViewModel.FolderViews = _views;
    }

    /// <summary>
    /// Gives both statics back. **This restore cannot be reddened by a test in
    /// this class** — a leak is only visible to whatever class runs next, and
    /// xUnit promises no order — so it is a guard, kept because the assembly
    /// has been bitten by exactly this before.
    /// </summary>
    public override void Dispose()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        PaneViewModel.FolderViews = _viewsBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A folder-view store that goes no further than this test class.</summary>
    private sealed class Scratch : IFolderViewStore
    {
        private readonly Dictionary<string, FolderViewState> _views = new(StringComparer.Ordinal);

        public FolderViewState? Read(string path)
            => _views.TryGetValue(PathRules.Normalise(path), out var view) ? view : null;

        public void Write(string path, FolderViewState state)
            => _views[PathRules.Normalise(path)] = state;

        public void Forget(string path) => _views.Remove(PathRules.Normalise(path));

        public int Remembered => _views.Count;

        public int ForgetAll()
        {
            var had = _views.Count;
            _views.Clear();
            return had;
        }
    }

    // ---- reading the markup -------------------------------------------------

    /// <summary>
    /// The listing's context menu, which is the only one in the file whose
    /// DataType is the pane group. The same anchor ArchiveMenuTests and
    /// CreateShortcutTests use for the same menu.
    /// </summary>
    private static XElement ListingMenu()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "ContextMenu")
            .Single(m => (string?)m.Attribute(Xaml + "DataType") == "vm:PaneGroupViewModel");

    /// <summary>
    /// A DIRECT child of the listing menu, not a descendant. A "View" nested
    /// two hovers deep would satisfy a scan of the whole file and would not be
    /// the thing this exists to add.
    /// </summary>
    private static XElement ViewSubmenu()
        => ListingMenu().Elements(Avalonia + "MenuItem")
            .Single(m => MenuLabels.Plain((string?)m.Attribute("Header")) == "View");

    private static XElement Row(string header)
        => ViewSubmenu().Elements(Avalonia + "MenuItem")
            .Single(m => MenuLabels.Plain((string?)m.Attribute("Header")) == header);

    /// <summary>
    /// The key a row really ends up advertising, as Avalonia reads it — not the
    /// characters in the attribute. The whole point of the indirection: see the
    /// class comment for the three gestures this caught.
    /// </summary>
    private static KeyGesture Gesture(XElement row)
        => KeyGesture.Parse((string?)row.Attribute("InputGesture")
            ?? throw new InvalidOperationException(
                $"the '{MenuLabels.Plain((string?)row.Attribute("Header"))}' row prints no gesture"));

    /// <summary>
    /// The pane member a <c>{Binding ActiveTab.Something}</c> attribute names.
    /// Throws on an attribute shaped any other way rather than returning null,
    /// so a row rewritten to bind somewhere else fails loudly instead of being
    /// skipped.
    /// </summary>
    private static string Member(XElement row, string attribute)
    {
        var value = (string?)row.Attribute(attribute)
            ?? throw new InvalidOperationException(
                $"the '{MenuLabels.Plain((string?)row.Attribute("Header"))}' row has no {attribute}");

        const string prefix = "{Binding ActiveTab.";

        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{value}' does not bind to a member of the active tab");

        // "IsChecked" carries a mode after the path: "...IsDetailsView, Mode=OneWay}".
        return value[prefix.Length..].TrimEnd('}').Split(',')[0].Trim();
    }

    private static object Read(PaneViewModel pane, string member)
        => typeof(PaneViewModel).GetProperty(member, BindingFlags.Public | BindingFlags.Instance)
               ?.GetValue(pane)
           ?? throw new InvalidOperationException(
               $"PaneViewModel has no readable '{member}', so the row that binds it is inert");

    private static ICommand Run(PaneViewModel pane, XElement row)
        => (ICommand)Read(pane, Member(row, "Command"));

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

    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    // ---- the rows themselves ------------------------------------------------

    /// <summary>
    /// **The finding.** Grep for a View header in the markup returned nothing:
    /// the menu offered New, Refresh, Select, Arrange and the rest, and no way
    /// to say how the listing should be drawn.
    /// </summary>
    [Fact]
    public void The_listing_menu_carries_a_view_submenu()
    {
        Assert.Single(ListingMenu().Elements(Avalonia + "MenuItem"),
                      m => MenuLabels.Plain((string?)m.Attribute("Header")) == "View");
    }

    /// <summary>
    /// All three layouts, each printing the key that reaches it. The chip on
    /// screen shows the same three under the same names, so the menu and the
    /// toolbar cannot be read as offering different sets.
    ///
    /// The expected gesture is built from <see cref="Key"/> values rather than
    /// parsed from a string, so this test cannot be fooled by the same parse it
    /// exists to check: <c>Key.D1</c> is the number-row 1, and a row that
    /// advertised the bare "Ctrl+Shift+1" would arrive here as Key.Cancel.
    /// </summary>
    [Theory]
    [InlineData("List", Key.D1)]
    [InlineData("Small grid", Key.D2)]
    [InlineData("Large grid", Key.D3)]
    public void The_view_submenu_names_every_layout_and_the_key_that_reaches_it(
        string header, Key key)
    {
        var row = Row(header);

        Assert.Equal("Radio", (string?)row.Attribute("ToggleType"));
        Assert.Equal(new KeyGesture(key, KeyModifiers.Control | KeyModifiers.Shift), Gesture(row));
    }

    /// <summary>
    /// The row the left half of a split had no pointer route to.
    ///
    /// A CheckBox rather than a Radio, and it prints Ctrl+H — which is the
    /// gesture that already existed and that nothing in the menu mentioned.
    /// </summary>
    [Fact]
    public void The_view_submenu_carries_hidden_files_and_says_which_key_flips_them()
    {
        var row = Row("Show hidden files");

        Assert.Equal("CheckBox", (string?)row.Attribute("ToggleType"));
        Assert.Equal(new KeyGesture(Key.H, KeyModifiers.Control), Gesture(row));
    }

    /// <summary>
    /// **The rule between the layouts and hidden files.** They are two
    /// different questions — which of three, and yes or no — and the three
    /// radios read as a set only while nothing else is inside their run. The
    /// rule is ungated because the whole submenu is: unlike the separators
    /// higher in this menu there is no selection state that can empty either
    /// side of it.
    /// </summary>
    [Fact]
    public void A_rule_separates_the_layouts_from_hidden_files()
    {
        var children = ViewSubmenu().Elements().ToList();

        var hidden = children.FindIndex(
            e => MenuLabels.Plain((string?)e.Attribute("Header")) == "Show hidden files");

        Assert.True(hidden > 0, "the hidden-files row is not a direct child of the View submenu");
        Assert.Equal("Separator", children[hidden - 1].Name.LocalName);
    }

    /// <summary>
    /// **A tick that writes back would fight the command beside it.**
    /// IsChecked is TwoWay by default on a MenuItem, so a row carrying both a
    /// two-way tick and ToggleHiddenCommand would flip the property and then
    /// flip it straight back — and the layout rows would additionally write
    /// into get-only properties. Every row here is OneWay for that reason.
    /// </summary>
    [Theory]
    [InlineData("List")]
    [InlineData("Small grid")]
    [InlineData("Large grid")]
    [InlineData("Show hidden files")]
    public void Every_row_reports_its_state_without_writing_it_back(string header)
    {
        Assert.Contains("Mode=OneWay", (string?)Row(header).Attribute("IsChecked") ?? "");
    }

    // ---- what the rows do ---------------------------------------------------

    /// <summary>
    /// Reads the command out of the markup and runs it, so a binding path that
    /// names nothing — which the build accepts in silence — is caught here
    /// rather than by somebody picking a row and watching it do nothing.
    ///
    /// The pane is put into a DIFFERENT layout first: a row wired to the wrong
    /// command, or to none, would otherwise pass by starting where it was
    /// meant to arrive.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("List", ViewMode.Details)]
    [InlineData("Small grid", ViewMode.Compact)]
    [InlineData("Large grid", ViewMode.Grid)]
    public void Every_layout_row_runs_the_command_that_reaches_that_layout(
        string header, ViewMode expected)
    {
        var row = Row(header);
        var pane = Shell().Left.Tabs[0];

        pane.View = expected == ViewMode.Details ? ViewMode.Grid : ViewMode.Details;

        // The tick the row shows is the pane's own report, and it must not
        // already be claiming the layout we are about to ask for.
        var tick = Member(row, "IsChecked");
        Assert.False((bool)Read(pane, tick));

        Run(pane, row).Execute(null);

        Assert.Equal(expected, pane.View);
        Assert.True((bool)Read(pane, tick));
    }

    /// <summary>
    /// **A layout picked from this menu is a layout the folder keeps**, when
    /// remember-per-folder is on: the row runs the same command the chip runs,
    /// the command sets View, and OnViewChanged ends in RememberFolderView.
    ///
    /// Here as much for where the write GOES as for the fact that it happens.
    /// The store is a process-wide static, and a real MainWindow built by
    /// another class in this non-parallel assembly points it at the user's own
    /// config directory — so a test that drives View without taking the static
    /// first records a layout for the temp folder in the machine's real
    /// settings. Reading it back out of this class's own store is what says the
    /// borrow held.
    /// </summary>
    [AvaloniaFact]
    public void Picking_a_layout_is_recorded_against_the_folder_and_goes_no_further()
    {
        var pane = Shell().Left.Tabs[0];

        pane.View = ViewMode.Details;

        Run(pane, Row("Large grid")).Execute(null);

        Assert.False(string.IsNullOrEmpty(pane.CurrentPath));
        Assert.Equal(ViewMode.Grid, _views.Read(pane.CurrentPath)?.View);
    }

    /// <summary>
    /// The same for hidden files, and it is the one row whose command is not
    /// also on a toolbar button — ToggleHidden existed for Ctrl+H alone.
    /// </summary>
    [AvaloniaFact]
    public void The_hidden_files_row_runs_the_command_that_flips_them()
    {
        var row = Row("Show hidden files");
        var pane = Shell().Left.Tabs[0];

        pane.ShowHidden = false;

        Run(pane, row).Execute(null);

        Assert.True(pane.ShowHidden);
        Assert.True((bool)Read(pane, Member(row, "IsChecked")));

        Run(pane, row).Execute(null);

        Assert.False(pane.ShowHidden);
    }

    // ---- and why it had to be this menu ------------------------------------

    /// <summary>
    /// **The half of the window the flyout does not serve.** Splitting hands
    /// the window controls to the right group only, which is what was asked
    /// for — so the left group's view options button, and the hidden-files
    /// checkbox behind it, are not drawn.
    ///
    /// The listing menu belongs to the group rather than to the window, so
    /// every pane has one. Nothing may gate this submenu on the same flag, or
    /// the left half is back where it started.
    ///
    /// The gate on the button is asserted here rather than taken on trust from
    /// a comment: the file's own note beside that button read "Ungated" for
    /// three weeks after the gate went back on it (c99da00 took the gate off on
    /// 11 August 2026, bf9ce4b put it back on the 14th and updated only the
    /// panel toggle's note), and this whole submenu is justified by which of
    /// the two is true.
    /// </summary>
    [AvaloniaFact]
    public void The_left_half_of_a_split_loses_the_flyout_and_keeps_the_menu()
    {
        var shell = Shell();

        shell.ToggleSplitCommand.Execute(null);

        Assert.NotNull(shell.Right);
        Assert.False(shell.Left.ShowsWindowControls);
        Assert.True(shell.Right!.ShowsWindowControls);

        var flyoutButton = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "Button")
            .Single(b => (string?)b.Attribute("ToolTip.Tip") == "View options");

        Assert.Equal("{Binding ShowsWindowControls}", (string?)flyoutButton.Attribute("IsVisible"));

        Assert.Null(ViewSubmenu().Attribute("IsVisible"));
    }
}
