using Avalonia.Headless.XUnit;
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
public sealed class AdminEntryTests : OwnedViewModels
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

    /// <summary>
    /// **Which files can be started elevated is the launcher's answer now**,
    /// and this one is told rather than working it out. The real rules live
    /// beside the launchers that own them — the Windows extension list in
    /// Vaktari.Windows.Tests, the execute bit in Vaktari.Linux.Tests — because
    /// a fake that reimplemented either would pin the fake.
    /// </summary>
    private sealed class FakeLauncher(bool canElevate, params string[] startable)
        : IApplicationLauncher
    {
        public bool CanElevate => canElevate;

        /// <summary>
        /// Independent of <see cref="CanElevate"/> on purpose, so that a
        /// launcher careless about one and not the other is a state this suite
        /// can actually produce — it is the state the pane's own check exists
        /// to survive.
        /// </summary>
        public bool CanElevateFile(string path) => startable.Contains(path);

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

    private PaneViewModel Pane(FakeLauncher launcher, string? selected = null)
    {
        var pane = Own(new PaneViewModel(new InertFileSystem(), null, launcher)
        {
            CurrentPath = Path.GetTempPath(),
        });

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
    [AvaloniaFact]
    public void An_ordinary_right_click_on_an_executable_offers_elevation()
    {
        var pane = Pane(new FakeLauncher(true, @"C:\tools\setup.exe"), @"C:\tools\setup.exe");

        Assert.True(pane.CanRunSelectionAsAdministrator);

        // But not the extended section: the admin terminal still wants Shift.
        Assert.False(pane.ShowAdminEntries);
    }

    [AvaloniaFact]
    public void Holding_shift_adds_the_extended_section()
    {
        var pane = Pane(new FakeLauncher(true, @"C:\tools\setup.exe"), @"C:\tools\setup.exe");

        pane.AdminRequested = true;

        Assert.True(pane.ShowAdminEntries);
        Assert.True(pane.CanRunSelectionAsAdministrator);
    }

    /// <summary>
    /// **Only for what the platform can actually start elevated, and the
    /// platform is asked.** This used to be a list of Windows file extensions
    /// held here, which was the right answer on Windows and the only answer
    /// anywhere — on a desktop where an executable usually has no extension it
    /// could only say no, so the entry could never appear on Linux however
    /// loudly the launcher said it could elevate. What the pane owes is to ask
    /// and to obey the answer; the rules themselves are pinned beside their
    /// launchers.
    /// </summary>
    [AvaloniaFact]
    public void Only_a_file_the_launcher_will_start_is_offered()
    {
        // **Deliberately the wrong way round for an extension list.** The
        // launcher says yes to a .txt and no to an .exe, so a pane that kept
        // the old rule instead of asking gets both answers backwards. Written
        // with setup.exe and notes.txt the old rule agreed with the fake on
        // every case, and a pane that had quietly stopped asking passed.
        var launcher = new FakeLauncher(true, @"C:\tools\payload.txt");

        Assert.True(Pane(launcher, @"C:\tools\payload.txt").CanRunSelectionAsAdministrator);
        Assert.False(Pane(launcher, @"C:\tools\setup.exe").CanRunSelectionAsAdministrator);
    }

    /// <summary>
    /// A desktop with no elevation route says so, and the section never
    /// appears.
    ///
    /// **This once read "which is every desktop but Windows today", and that
    /// stopped being true.** Linux has pkexec, which does not decide anything —
    /// it hands the request to polkit, and polkit shows the system's own
    /// authentication dialog, exactly as the runas verb hands a request to
    /// Windows' consent dialog. What is left of the old claim is a machine with
    /// no pkexec installed, which is still an ordinary machine and still gets
    /// no rows.
    /// </summary>
    [AvaloniaFact]
    public void A_platform_that_cannot_elevate_never_offers_it()
    {
        var pane = Pane(new FakeLauncher(false, @"C:\tools\setup.exe"), @"C:\tools\setup.exe");

        pane.AdminRequested = true;

        Assert.False(pane.ShowAdminEntries);
        Assert.False(pane.CanRunSelectionAsAdministrator);
    }

    /// <summary>
    /// The bin and Recent hold rows naming where a file USED to be, so an
    /// elevated launch there would run whatever occupies that path now — with
    /// administrator rights, which makes it the worst place for that mistake.
    /// </summary>
    [AvaloniaFact]
    public void The_bin_and_recent_never_offer_it()
    {
        foreach (var listing in new[] { VirtualPaths.Trash, VirtualPaths.Files })
        {
            var pane = Own(new PaneViewModel(new InertFileSystem(), null, new FakeLauncher(true))
            {
                CurrentPath = listing,
                AdminRequested = true,
            });

            Assert.False(pane.ShowAdminEntries, listing);
        }
    }

    [AvaloniaFact]
    public void Choosing_it_hands_the_file_to_the_system()
    {
        var launcher = new FakeLauncher(true, @"C:\tools\setup.exe");
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
    [AvaloniaFact]
    public void The_command_refuses_what_the_menu_would_not_offer()
    {
        var launcher = new FakeLauncher(true);
        var pane = Pane(launcher, @"C:\notes.txt");
        pane.AdminRequested = true;

        pane.RunAsAdministrator();

        Assert.Null(launcher.Elevated);
    }

    [AvaloniaFact]
    public void An_admin_terminal_opens_in_this_folder()
    {
        var launcher = new FakeLauncher(true);
        var pane = Pane(launcher);
        pane.AdminRequested = true;

        pane.OpenAdminTerminalHere();

        Assert.Equal(Path.GetTempPath(), launcher.ElevatedTerminalIn);
    }

    [AvaloniaFact]
    public void Without_shift_the_admin_terminal_command_does_nothing()
    {
        var launcher = new FakeLauncher(true);

        Pane(launcher).OpenAdminTerminalHere();

        Assert.Null(launcher.ElevatedTerminalIn);
    }
}
