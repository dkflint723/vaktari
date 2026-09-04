using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the bin, Recent and This PC are not.
///
/// **Everything that needed a path on disk asked CurrentPath and was handed
/// "vaktari:trash".** The listings that are views rather than folders already
/// gated the entries that act on a SELECTION, but the ones that act on the
/// FOLDER ITSELF had no gate at all: Ctrl+D pinned a place whose path was the
/// literal scheme and which could never be opened, F4 opened a terminal in it,
/// and Ctrl+L put it in the address bar to be read straight back as a path.
/// One mistake in three places, so one answer.
/// </summary>
public sealed class VirtualListingActionTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

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

    /// <summary>Counts what it was asked to open, and opens nothing.</summary>
    private sealed class CountingLauncher : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];

        public void OpenTerminal(string directory) => Opened.Add(directory);

        public void OpenTerminal(string directory, TerminalOption terminal)
            => Opened.Add(directory);

        public IReadOnlyList<TerminalOption> Terminals { get; } =
            [new TerminalOption("xterm", "Terminal", "xterm", [])];

        public Exception? Open(string path) => null;
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    private PaneViewModel Pane(string path)
    {
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });
        pane.CurrentPath = path;

        return pane;
    }

    [AvaloniaTheory]
    [InlineData(VirtualPaths.Trash)]
    [InlineData(VirtualPaths.Computer)]
    [InlineData(VirtualPaths.Files)]
    [InlineData(VirtualPaths.Locations)]
    public void None_of_the_virtual_listings_is_a_folder(string path)
        => Assert.False(Pane(path).IsRealFolder);

    [AvaloniaFact]
    public void An_ordinary_folder_is()
        => Assert.True(Pane(Path.GetTempPath()).IsRealFolder);

    /// <summary>
    /// The keystroke, not just the menu row: F4 is bound straight to the
    /// command, so hiding the row leaves the gesture wide open.
    /// </summary>
    [AvaloniaFact]
    public void F4_in_a_virtual_listing_opens_no_terminal()
    {
        var launcher = new CountingLauncher();
        var pane = Own(new PaneViewModel(new Inert(), launcher: launcher));

        pane.CurrentPath = VirtualPaths.Trash;
        pane.OpenTerminalHereCommand.Execute(null);
        pane.OpenTerminalInCommand.Execute(launcher.Terminals[0]);

        Assert.Empty(launcher.Opened);

        pane.CurrentPath = Path.GetTempPath();
        pane.OpenTerminalHereCommand.Execute(null);

        Assert.Equal([Path.GetTempPath()], launcher.Opened);
    }

    /// <summary>Neither shape of the entry is offered where it cannot
    /// work.</summary>
    [AvaloniaFact]
    public void Neither_terminal_row_is_offered_there()
    {
        var pane = Pane(VirtualPaths.Trash);

        Assert.False(pane.ShowOneTerminal);
        Assert.False(pane.ShowTerminalChoice);

        pane.CurrentPath = Path.GetTempPath();

        // Exactly one of them, whichever the machine's terminal count picks.
        Assert.True(pane.ShowOneTerminal ^ pane.ShowTerminalChoice);
    }

    /// <summary>
    /// **Ctrl+L filled the box with "vaktari:computer".** An internal name, in
    /// the one place whose whole contract is that what it holds is a path you
    /// can read, edit and press Enter on.
    /// </summary>
    [AvaloniaFact]
    public void The_address_bar_opens_empty_rather_than_holding_a_scheme()
    {
        var pane = Pane(VirtualPaths.Computer);

        pane.BeginEditPath();

        Assert.Equal("", pane.PathText);
        Assert.True(pane.IsPathEditing);
    }

    [AvaloniaFact]
    public void And_still_holds_a_real_folder()
    {
        var pane = Pane(Path.GetTempPath());

        pane.BeginEditPath();

        Assert.Equal(Path.GetTempPath(), pane.PathText);
    }

    /// <summary>
    /// The row that pins the current folder hides where there is no folder to
    /// pin — the convention the selection entries already follow.
    /// </summary>
    [AvaloniaFact]
    public void The_add_this_folder_row_is_not_offered_there()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var row = markup.Descendants(Avalonia + "MenuItem").Single(
            m => (string?)m.Attribute("Header") == "Add this folder to places");

        Assert.Contains("ShowAddCurrentToPlaces", (string?)row.Attribute("IsVisible"));

        Assert.Contains(
            "ActiveTab?.IsRealFolder == true",
            RepoSource.Ui("ViewModels", "ShellViewModel.cs"));
    }

    /// <summary>And the gesture behind it, which no markup gate reaches.</summary>
    [Fact]
    public void And_ctrl_d_pins_nothing_there()
        => Assert.Contains(
            "ActiveTab is { IsRealFolder: true, CurrentPath: { Length: > 0 } path }",
            RepoSource.Body(
                RepoSource.Ui("ViewModels", "ShellViewModel.cs"),
                "private void PinCurrent()"));

    /// <summary>
    /// A shell whose one tab is showing <paramref name="path"/>.
    ///
    /// The tab the shell opens for itself is navigated in the background and
    /// its notifications arrive a dispatcher turn later; they are drained here
    /// so what the tests below count belongs to the move they made.
    ///
    /// (`global::`, because this class already has a private static
    /// `Avalonia` XNamespace field that shadows the namespace — the same
    /// reason NavigationHistoryTests writes it that way.)
    /// </summary>
    private async Task<ShellViewModel> ShellOn(string path)
    {
        // A search path with no backend is an empty listing rather than a real
        // recursive walk of this machine.
        UseSearch(null);

        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        await shell.ActiveTab!.RefreshAsync();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        await shell.ActiveTab.NavigateAsync(path);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(path, shell.ActiveTab.CurrentPath);

        return shell;
    }

    /// <summary>A row of the kind This PC and a search hold: a real path,
    /// unlike the listing around it.</summary>
    private static FileEntry Row()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        return new FileEntry(root, root, 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);
    }

    /// <summary>
    /// **Properties stayed on the menu where there was nothing to describe.**
    /// With no row picked the sheet describes the FOLDER you are looking at,
    /// and This PC is not one — it was handed "vaktari:computer", found no such
    /// thing on disk, and printed the internal scheme straight back.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(VirtualPaths.Computer)]
    [InlineData("vaktari:search:report::everywhere")]
    public async Task Properties_is_not_offered_where_there_is_no_folder_and_no_row(string path)
    {
        var shell = await ShellOn(path);

        // Asserted, not assumed: it is what makes the next line mean anything.
        Assert.False(shell.ActiveTab!.IsRealFolder);
        Assert.False(shell.CanShowProperties);

        // A drive, or a result, is a real path — the row belongs there.
        shell.ActiveTab.SelectedEntry = Row();

        Assert.True(shell.CanShowProperties);
    }

    /// <summary>And the new clause is not over-broad: an ordinary folder with
    /// nothing selected has no selection either, and is exactly where the sheet
    /// describes the folder itself.</summary>
    [AvaloniaFact]
    public async Task Properties_is_still_offered_in_an_ordinary_folder()
    {
        // A real subdirectory rather than GetTempPath(), whose trailing
        // separator the pane normalises away.
        var folder = Directory.CreateTempSubdirectory("vaktari-properties").FullName;

        try
        {
            var shell = await ShellOn(folder);

            Assert.False(shell.ActiveTab!.HasSelection);
            Assert.True(shell.CanShowProperties);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// **Alt+Enter got round the row's visibility**, which is exactly how the
    /// bin and Recent were got round before it. The command is a plain void, so
    /// the negative is settled the instant Execute returns.
    /// </summary>
    [AvaloniaFact]
    public async Task Alt_enter_on_a_listing_that_is_not_a_folder_asks_for_no_sheet()
    {
        var shell = await ShellOn(VirtualPaths.Computer);
        var asked = 0;

        shell.PropertiesRequested += (_, _) => asked++;

        // The keystroke reaches the command, not the window's own method.
        shell.ShowPropertiesCommand.Execute(null);

        Assert.Equal(0, asked);

        // Not vacuous: the same command on the same listing asks once there is
        // a drive to ask about.
        shell.ActiveTab!.SelectedEntry = Row();
        shell.ShowPropertiesCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// **The row went on being offered after the tab moved into This PC.**
    /// These entries are refreshed when the SELECTION changes, and moving away
    /// from a folder where nothing was picked changes no selection at all.
    /// </summary>
    [AvaloniaFact]
    public async Task The_properties_row_is_re_examined_when_the_tab_navigates()
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-properties-nav").FullName;

        try
        {
            var shell = await ShellOn(folder);
            var raised = new List<string?>();

            shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            await shell.ActiveTab!.NavigateAsync(VirtualPaths.Computer);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(VirtualPaths.Computer, shell.ActiveTab.CurrentPath);
            Assert.Contains(nameof(ShellViewModel.CanShowProperties), raised);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>And when a row is focused, which is what a right-click does
    /// before the menu opens — and which touches no selection collection.</summary>
    [AvaloniaFact]
    public async Task The_properties_row_is_re_examined_when_a_row_is_focused()
    {
        var shell = await ShellOn(VirtualPaths.Computer);
        var raised = new List<string?>();

        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.ActiveTab!.SelectedEntry = Row();

        Assert.Contains(nameof(ShellViewModel.CanShowProperties), raised);
    }
}
