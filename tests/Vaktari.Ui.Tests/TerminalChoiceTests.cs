using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which terminal "open terminal here" opens.
///
/// **There was no choice.** The launcher tried Windows Terminal, then
/// PowerShell, then cmd, and took whichever started — so on a machine where
/// somebody lives in Warp, or WSL, or the Git Bash their toolchain needs, F4
/// opened the wrong program and there was nowhere to say otherwise.
///
/// The rules worth pinning are the ones that fail quietly: a preference naming
/// something since uninstalled must not break F4, and the menu's first entry
/// and F4 must never disagree about which terminal is the default — two routes
/// to one action that pick differently is the fault this project keeps finding.
/// </summary>
public sealed class TerminalChoiceTests
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

    /// <summary>Reports a fixed set, and records what it was asked to open.</summary>
    private sealed class FakeLauncher : IApplicationLauncher
    {
        public FakeLauncher(params string[] ids) =>
            Terminals = ids.Select(id => new TerminalOption(id, id, id + ".exe", [])).ToList();

        public IReadOnlyList<TerminalOption> Terminals { get; }

        public TerminalOption? Opened { get; private set; }
        public bool OpenedWithoutChoosing { get; private set; }

        public void OpenTerminal(string directory) => OpenedWithoutChoosing = true;
        public void OpenTerminal(string directory, TerminalOption terminal) => Opened = terminal;

        public Exception? Open(string path) => null;
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    private static PaneViewModel Pane(FakeLauncher launcher) =>
        new(new InertFileSystem(), null, launcher) { CurrentPath = Path.GetTempPath() };

    private static void Prefer(string id)
    {
        var current = Vaktari.Ui.Settings.AppSettings.Current;
        Vaktari.Ui.Settings.AppSettings.Apply(current with
        {
            General = current.General with { PreferredTerminal = id },
        });
    }

    public TerminalChoiceTests() => Prefer("");

    [Fact]
    public void With_no_preference_the_first_one_found_is_used()
    {
        var launcher = new FakeLauncher("windows-terminal", "warp", "cmd");

        Pane(launcher).OpenTerminalHere();

        Assert.Equal("windows-terminal", launcher.Opened?.Id);
    }

    [Fact]
    public void The_chosen_terminal_is_the_one_that_opens()
    {
        Prefer("warp");
        var launcher = new FakeLauncher("windows-terminal", "warp", "cmd");

        Pane(launcher).OpenTerminalHere();

        Assert.Equal("warp", launcher.Opened?.Id);
    }

    /// <summary>
    /// The menu lists the chosen one first and marks it, because the row at the
    /// top of that submenu and F4 have to be the same terminal — two routes to
    /// one action that disagree is the fault this codebase keeps finding.
    /// </summary>
    [Fact]
    public void The_chosen_terminal_leads_the_menu_and_is_marked()
    {
        Prefer("warp");
        var pane = Pane(new FakeLauncher("windows-terminal", "warp", "cmd"));

        Assert.Equal("warp", pane.Terminals[0].Id);
        Assert.True(pane.Terminals[0].IsPreferred);
        Assert.All(pane.Terminals.Skip(1), t => Assert.False(t.IsPreferred));
    }

    /// <summary>Everything installed stays reachable — choosing one is not
    /// hiding the others.</summary>
    [Fact]
    public void Choosing_one_does_not_drop_the_rest()
    {
        Prefer("warp");
        var pane = Pane(new FakeLauncher("windows-terminal", "warp", "cmd"));

        Assert.Equal(3, pane.Terminals.Count);
        Assert.Equal(["warp", "windows-terminal", "cmd"], pane.Terminals.Select(t => t.Id));
    }

    /// <summary>
    /// **Uninstalling the chosen terminal must not break F4.** The preference
    /// is a stored id, so it outlives the program it names — and honouring a
    /// dead one would turn "open a terminal" into nothing at all.
    /// </summary>
    [Fact]
    public void A_preference_for_something_uninstalled_is_ignored()
    {
        Prefer("warp");
        var launcher = new FakeLauncher("windows-terminal", "cmd");

        Pane(launcher).OpenTerminalHere();

        Assert.Equal("windows-terminal", launcher.Opened?.Id);
    }

    /// <summary>
    /// Detecting nothing is not the same fact as nothing being installed, so
    /// the launcher's own fall-through still gets its turn.
    /// </summary>
    [Fact]
    public void With_nothing_detected_the_launcher_still_gets_asked()
    {
        var launcher = new FakeLauncher();

        Pane(launcher).OpenTerminalHere();

        Assert.True(launcher.OpenedWithoutChoosing);
    }

    /// <summary>One terminal gets the plain entry; a submenu holding a single
    /// row is a hover that buys nothing.</summary>
    [Fact]
    public void A_choice_is_only_offered_when_there_is_one()
    {
        Assert.False(Pane(new FakeLauncher("cmd")).HasSeveralTerminals);
        Assert.True(Pane(new FakeLauncher("cmd", "warp")).HasSeveralTerminals);
    }
}
