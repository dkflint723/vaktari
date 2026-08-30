using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The administrator entries, behind Shift+right-click.
///
/// **Elevating is how somebody gets past a permission set against them
/// deliberately.** It belongs where it can be reached and not where it can be
/// stumbled into, which is why it is behind a modifier — the same gesture
/// Explorer uses — and why the rules worth pinning here are about when it is
/// NOT offered.
///
/// Vaktari never holds administrator rights itself. These ask the system to
/// start a new process elevated; the system shows its own consent dialog and
/// makes the decision, and nothing here can or should bypass it.
/// </summary>
public sealed class AdminEntryTests
{
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

    private sealed class FakeLauncher(bool canElevate) : IApplicationLauncher
    {
        public bool CanElevate { get; } = canElevate;

        public string? Elevated { get; private set; }
        public string? ElevatedTerminalIn { get; private set; }

        public void OpenElevated(string path) => Elevated = path;

        public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null)
            => ElevatedTerminalIn = directory;

        public void Open(string path) { }
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    private static PaneViewModel Pane(FakeLauncher launcher, string? selected = null)
    {
        var pane = new PaneViewModel(new InertFileSystem(), null, launcher)
        {
            CurrentPath = Path.GetTempPath(),
        };

        if (selected is not null)
            pane.SelectedEntry = new FileEntry(
                Path.GetFileName(selected), selected, 0, DateTimeOffset.Now, EntryFlags.None);

        return pane;
    }

    /// <summary>
    /// **Run as administrator no longer hides behind Shift.** Explorer shows it
    /// for every executable on a plain right-click and reserves Shift for its
    /// EXTENDED verbs; copying the gate onto this entry meant an ordinary
    /// right-click on an .exe offered no elevation anywhere visible, and the
    /// person went hunting through submenus for something that was simply
    /// hidden. The admin TERMINAL keeps the gate — that one is an extended
    /// verb by the same convention.
    /// </summary>
    [Fact]
    public void An_ordinary_right_click_on_an_executable_offers_elevation()
    {
        var pane = Pane(new FakeLauncher(true), @"C:\tools\setup.exe");

        Assert.True(pane.CanRunSelectionAsAdministrator);

        // But not the extended section: the admin terminal still wants Shift.
        Assert.False(pane.ShowAdminEntries);
    }

    [Fact]
    public void Holding_shift_adds_the_extended_section()
    {
        var pane = Pane(new FakeLauncher(true), @"C:\tools\setup.exe");

        pane.AdminRequested = true;

        Assert.True(pane.ShowAdminEntries);
        Assert.True(pane.CanRunSelectionAsAdministrator);
    }

    /// <summary>
    /// **Only for things Windows can actually start elevated.** The runas verb
    /// on a .txt does nothing at all — no error, no elevation, no editor — so
    /// offering it for every file would be an entry that silently fails on most
    /// of them.
    /// </summary>
    [Theory]
    [InlineData(@"C:\tools\setup.exe", true)]
    [InlineData(@"C:\tools\install.msi", true)]
    [InlineData(@"C:\tools\go.bat", true)]
    [InlineData(@"C:\tools\task.ps1", true)]
    [InlineData(@"C:\notes.txt", false)]
    [InlineData(@"C:\photo.png", false)]
    [InlineData(@"C:\archive.zip", false)]
    public void Run_as_administrator_is_offered_only_where_it_would_work(string path, bool offered)
    {
        var pane = Pane(new FakeLauncher(true), path);
        pane.AdminRequested = true;

        Assert.Equal(offered, pane.CanRunSelectionAsAdministrator);
    }

    /// <summary>
    /// A desktop with no elevation route we should be using says so, and the
    /// section never appears — which is every desktop but Windows today.
    /// </summary>
    [Fact]
    public void A_platform_that_cannot_elevate_never_offers_it()
    {
        var pane = Pane(new FakeLauncher(false), @"C:\tools\setup.exe");

        pane.AdminRequested = true;

        Assert.False(pane.ShowAdminEntries);
        Assert.False(pane.CanRunSelectionAsAdministrator);
    }

    /// <summary>
    /// The bin and Recent hold rows naming where a file USED to be, so an
    /// elevated launch there would run whatever occupies that path now — with
    /// administrator rights, which makes it the worst place for that mistake.
    /// </summary>
    [Fact]
    public void The_bin_and_recent_never_offer_it()
    {
        foreach (var listing in new[] { VirtualPaths.Trash, VirtualPaths.Files })
        {
            var pane = new PaneViewModel(new InertFileSystem(), null, new FakeLauncher(true))
            {
                CurrentPath = listing,
                AdminRequested = true,
            };

            Assert.False(pane.ShowAdminEntries, listing);
        }
    }

    [Fact]
    public void Choosing_it_hands_the_file_to_the_system()
    {
        var launcher = new FakeLauncher(true);
        var pane = Pane(launcher, @"C:\tools\setup.exe");
        pane.AdminRequested = true;

        pane.RunAsAdministrator();

        Assert.Equal(@"C:\tools\setup.exe", launcher.Elevated);
    }

    /// <summary>
    /// The command refuses what the menu would not have shown. A command that
    /// trusts its own entry's visibility is one keyboard binding away from
    /// elevating a text file.
    /// </summary>
    [Fact]
    public void The_command_refuses_what_the_menu_would_not_offer()
    {
        var launcher = new FakeLauncher(true);
        var pane = Pane(launcher, @"C:\notes.txt");
        pane.AdminRequested = true;

        pane.RunAsAdministrator();

        Assert.Null(launcher.Elevated);
    }

    [Fact]
    public void An_admin_terminal_opens_in_this_folder()
    {
        var launcher = new FakeLauncher(true);
        var pane = Pane(launcher);
        pane.AdminRequested = true;

        pane.OpenAdminTerminalHere();

        Assert.Equal(Path.GetTempPath(), launcher.ElevatedTerminalIn);
    }

    [Fact]
    public void Without_shift_the_admin_terminal_command_does_nothing()
    {
        var launcher = new FakeLauncher(true);

        Pane(launcher).OpenAdminTerminalHere();

        Assert.Null(launcher.ElevatedTerminalIn);
    }
}
