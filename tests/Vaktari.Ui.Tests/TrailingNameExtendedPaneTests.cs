using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A pane opened through "\\?\" hands no folded name on either.** Such a
/// pane lists "t.cmd." and "report " as themselves, which is the point of
/// opening it that way — and every row then carries the prefix. Vaktari's own
/// reads and acts may use that spelling (<see cref="ReachablePath.Refuse"/>
/// passes it), but the shell and another program may not be trusted with it:
/// Explorer and anything else that strips the prefix open the neighbour. So
/// every hand-off in the pane asks <see cref="ReachablePath.RefuseHandedOut"/>,
/// which takes the prefix off first.
///
/// TrailingNameHandOffTests spells every row plainly, so a pane that asked
/// Refuse instead passed all of it (fix8's verification: the mutation stayed
/// green). These are the same routes with the prefix on, and a control: an
/// ordinary row in the same pane is handed over exactly as spelled. Every
/// platform call is a recording; the rows are strings.
/// </summary>
public sealed class TrailingNameExtendedPaneTests : OwnedViewModels
{
    private readonly string _root = @"\\?\" + Path.Combine(Path.GetTempPath(), "vaktari-extpane-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly IShellMenuProvider? _shellMenuBefore = PaneViewModel.ShellMenu;

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.ShellMenu = _shellMenuBefore;

        GC.SuppressFinalize(this);
    }

    private sealed class RecordingLauncher : IApplicationLauncher
    {
        public List<string> Asked { get; } = [];

        public Exception? Open(string path) { Asked.Add("open " + path); return null; }
        public bool CanRunFile(string path) => true;
        public Exception? Run(string path) { Asked.Add("run " + path); return null; }
        public void OpenTerminal(string directory) => Asked.Add("terminal " + directory);
        public void OpenTerminal(string directory, TerminalOption terminal) => Asked.Add("terminal " + directory);
        public bool CanElevate => true;
        public bool CanElevateFile(string path) => true;
        public void OpenElevated(string path) => Asked.Add("elevated " + path);
        public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null) => Asked.Add("elevated terminal " + directory);

        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path)
        {
            Asked.Add("options " + path);
            return [new LaunchOption("Editor", "editor")];
        }

        public void OpenWith(string path, LaunchOption option) => Asked.Add("with " + path);
        public bool CanChooseApplication => true;
        public bool ChooseApplication(string path) { Asked.Add("choose " + path); return true; }
    }

    private sealed class RecordingMenus : IShellMenuProvider
    {
        public List<string> Asked { get; } = [];

        public Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths)
        {
            Asked.AddRange(paths);
            return Task.FromResult<IShellMenu?>(null);
        }

        public Task<IShellMenu?> BuildBackgroundAsync(string folder)
        {
            Asked.Add(folder);
            return Task.FromResult<IShellMenu?>(null);
        }
    }

    private sealed class RecordingScripts : IScriptRunner
    {
        public List<string> Asked { get; } = [];

        public string ScriptsDirectory => Path.GetTempPath();
        public IReadOnlyList<ScriptCommand> Discover() => [];

        public ValueTask<string> RunAsync(
            ScriptCommand script, string workingDirectory, IReadOnlyList<string> paths, CancellationToken ct)
        {
            Asked.Add(workingDirectory);
            Asked.AddRange(paths);
            return ValueTask.FromResult("ran");
        }
    }

    /// <summary>Lists nothing and reads nothing: the rule is about the path.</summary>
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

    private FileEntry Row(string name)
        => new(name, Path.Combine(_root, name), 5, DateTimeOffset.Now, EntryFlags.None);

    private (PaneViewModel Pane, RecordingLauncher Launcher, RecordingScripts Scripts) Pane(string? folder = null)
    {
        var launcher = new RecordingLauncher();
        var scripts = new RecordingScripts();
        var pane = Own(new PaneViewModel(new InertFileSystem(), null, launcher, scripts: scripts)
        {
            CurrentPath = folder ?? _root,
        });

        return (pane, launcher, scripts);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 30; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("t.cmd.")]
    [InlineData("report ")]
    public async Task A_prefixed_folded_row_is_handed_to_nothing_and_says_why(string name)
    {
        var menus = new RecordingMenus();
        PaneViewModel.ShellMenu = menus;

        var (pane, launcher, scripts) = Pane();
        var row = Row(name);

        await pane.OpenAsync(row);

        pane.SelectedEntry = row;
        await Settle();

        pane.OpenWithApp(new LaunchOption("Editor", "editor"));
        pane.OpenWithApp(new LaunchOption("Choose another app…", "") { IsChooser = true });
        pane.RunSelection();
        pane.RunAsAdministrator();
        await pane.OpenShellMenuAsync(background: false);
        Dispatcher.UIThread.RunJobs();
        await pane.RunScriptAsync(new ScriptCommand("tidy", Path.Combine(Path.GetTempPath(), "tidy.cmd")));

        Assert.Empty(launcher.Asked);
        Assert.Empty(menus.Asked);
        Assert.Empty(scripts.Asked);
        Assert.False(pane.HasOpenWithOptions);
        Assert.Contains($"\"{name}\" cannot be handed to another program", pane.Status, StringComparison.Ordinal);

        var disabled = Assert.IsType<ShellMenuEntry>(Assert.Single(pane.ShellMenuItems));
        Assert.False(disabled.IsEnabled);
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public void No_terminal_opens_in_a_prefixed_folded_folder()
    {
        var (pane, launcher, _) = Pane(Path.Combine(_root, "album "));

        pane.OpenTerminalHere();
        pane.OpenTerminalIn(new TerminalOption("cmd", "Command Prompt", "cmd.exe", []));
        pane.AdminRequested = true;
        pane.OpenAdminTerminalHere();

        Assert.Empty(launcher.Asked);
        Assert.Contains("\"album \" cannot be handed to another program", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>The control: an ordinary row in the same prefixed pane goes to
    /// every one of those, spelled exactly as the row is.</summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_prefixed_ordinary_row_is_handed_over_as_spelled()
    {
        var menus = new RecordingMenus();
        PaneViewModel.ShellMenu = menus;

        var (pane, launcher, scripts) = Pane();
        var row = Row("t.cmd");

        await pane.OpenAsync(row);

        pane.SelectedEntry = row;
        await Settle();

        pane.OpenWithApp(new LaunchOption("Editor", "editor"));
        pane.RunSelection();
        pane.OpenTerminalHere();
        await pane.OpenShellMenuAsync(background: false);
        await pane.RunScriptAsync(new ScriptCommand("tidy", Path.Combine(Path.GetTempPath(), "tidy.cmd")));

        Assert.Equal(
            ["open " + row.FullPath, "options " + row.FullPath, "with " + row.FullPath, "run " + row.FullPath, "terminal " + _root],
            launcher.Asked);
        Assert.Equal([row.FullPath], menus.Asked);
        Assert.Equal([_root, row.FullPath], scripts.Asked);
    }
}
