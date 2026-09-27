using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **What the pane hands to the shell or another program, for a name Win32
/// folds.** The seventh review round's hunt found each of these reaching the
/// neighbour: Open (a double-click, Enter, a path typed or pasted into the
/// bar) ran "t.cmd" for "t.cmd." and ran nothing, silently, for "t.cmd ";
/// Open with, the Windows menu, Run, a terminal and a script were each handed
/// the neighbour by the shell's parser (H1, H2, H6, and the routes found beside
/// them). Every one now refuses, all or nothing, with the sentence that names
/// the row and the file it would have reached.
///
/// Every platform call here is a recording: nothing is started, no menu is
/// built. The rows are strings — the rule is about the path — except where the
/// typed route needs File.Exists to answer, which it does in a temporary folder
/// through "\\?\". On Linux the same rows are ordinary, and are handed over.
/// </summary>
public sealed class TrailingNameHandOffTests : OwnedViewModels
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-handoff-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly IShellMenuProvider? _shellMenuBefore = PaneViewModel.ShellMenu;

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.ShellMenu = _shellMenuBefore;

        try
        {
            var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;

            if (Directory.Exists(root))
            {
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Remembers every path it is handed, by verb, and starts nothing.</summary>
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

    private FileEntry Row(string name, bool directory = false)
        => new(name, Path.Combine(_root, name), 5, DateTimeOffset.Now,
               directory ? EntryFlags.Directory : EntryFlags.None);

    private (PaneViewModel Pane, RecordingLauncher Launcher) Pane(string? folder = null, IScriptRunner? scripts = null)
    {
        var launcher = new RecordingLauncher();
        var pane = Own(new PaneViewModel(new InertFileSystem(), null, launcher, scripts: scripts)
        {
            CurrentPath = folder ?? _root,
        });

        return (pane, launcher);
    }

    private static void Refused(PaneViewModel pane, string name)
    {
        Assert.Contains($"\"{name}\" cannot be handed to another program", pane.Status, StringComparison.Ordinal);
    }

    // ---- open: a double-click, Enter (H1) ----------------------------------

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("t.cmd.")]
    [InlineData("t.cmd..")]
    [InlineData("t.cmd ")]
    public async Task Opening_a_folded_row_hands_the_shell_nothing_and_says_why(string name)
    {
        var (pane, launcher) = Pane();

        await pane.OpenAsync(Row(name));

        Assert.Empty(launcher.Asked);
        Refused(pane, name);
        Assert.Contains("\"t.cmd\" instead", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>A folder on the way that folds is the same hazard: every name
    /// below it is opened inside the neighbour.</summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_row_under_a_folded_folder_is_refused_the_same_way()
    {
        var (pane, launcher) = Pane();

        await pane.OpenAsync(Row(Path.Combine("album ", "notes.txt")));

        Assert.Empty(launcher.Asked);
        Refused(pane, "album ");
    }

    /// <summary>The control: an ordinary row still opens, so the silence above
    /// is the refusal and not a launcher that was never reached.</summary>
    [AvaloniaFact]
    public async Task An_ordinary_row_still_opens()
    {
        var (pane, launcher) = Pane();

        await pane.OpenAsync(Row("t.cmd"));

        Assert.Equal(["open " + Path.Combine(_root, "t.cmd")], launcher.Asked);
    }

    // ---- the address bar, and a pasted "Copy as path" (H2) -------------------

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("t.cmd.", false)]
    [InlineData("t.cmd.", true)]
    [InlineData("report ", true)]
    public async Task A_typed_path_to_a_folded_file_hands_the_shell_nothing(string name, bool quoted)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, name.TrimEnd(' ', '.')), "the neighbour");
        File.WriteAllText(@"\\?\" + Path.Combine(_root, name), "the one typed");

        var (pane, launcher) = Pane();
        var typed = Path.Combine(_root, name);

        pane.PathText = quoted ? $"\"{typed}\"" : typed;
        await pane.NavigateToPathText();

        Assert.Empty(launcher.Asked);
        Refused(pane, name);
    }

    // ---- Open with (H6) -------------------------------------------------------

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Open_with_offers_nothing_for_a_folded_row()
    {
        var (pane, launcher) = Pane();

        pane.SelectedEntry = Row("report ");

        // The enumeration runs on the pool and posts back; long enough for
        // one that was started to have been asked.
        for (var i = 0; i < 30; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }

        Assert.Empty(launcher.Asked);
        Assert.False(pane.HasOpenWithOptions);
    }

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(false)]
    [InlineData(true)]
    public void Choosing_an_application_for_a_folded_row_is_refused(bool chooser)
    {
        var (pane, launcher) = Pane();

        pane.SelectedEntry = Row("report ");
        launcher.Asked.Clear();

        pane.OpenWithApp(chooser
            ? new LaunchOption("Choose another app…", "") { IsChooser = true }
            : new LaunchOption("Editor", "editor"));

        Assert.Empty(launcher.Asked);
        Refused(pane, "report ");
    }

    // ---- Run, and Run as administrator -----------------------------------------

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(false)]
    [InlineData(true)]
    public void Running_a_folded_row_is_refused(bool elevated)
    {
        var (pane, launcher) = Pane();

        pane.SelectedEntry = Row("setup.exe.");

        if (elevated) pane.RunAsAdministrator();
        else pane.RunSelection();

        Assert.DoesNotContain(launcher.Asked, a => a.StartsWith(elevated ? "elevated " : "run ", StringComparison.Ordinal));
        Refused(pane, "setup.exe.");
    }

    // ---- the Windows menu (H6) --------------------------------------------------

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_windows_menu_is_not_built_for_a_folded_name_and_says_why(bool background)
    {
        var menus = new RecordingMenus();
        PaneViewModel.ShellMenu = menus;

        var (pane, _) = Pane(background ? Path.Combine(_root, "album ") : null);

        pane.SelectedEntry = Row("report ");

        await pane.OpenShellMenuAsync(background);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(menus.Asked);

        var row = Assert.IsType<ShellMenuEntry>(Assert.Single(pane.ShellMenuItems));
        Assert.False(row.IsEnabled);
        Assert.Contains(background ? "\"album \"" : "\"report \"", row.Label, StringComparison.Ordinal);
    }

    // ---- a terminal, and a script -------------------------------------------------

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public void No_terminal_opens_in_a_folded_folder()
    {
        var (pane, launcher) = Pane(Path.Combine(_root, "album "));

        pane.OpenTerminalHere();
        pane.OpenTerminalIn(new TerminalOption("cmd", "Command Prompt", "cmd.exe", []));

        pane.AdminRequested = true;
        pane.OpenAdminTerminalHere();

        Assert.Empty(launcher.Asked);
        Refused(pane, "album ");
    }

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_script_is_handed_no_folded_name(bool inFoldedFolder)
    {
        var scripts = new RecordingScripts();
        var (pane, _) = Pane(inFoldedFolder ? Path.Combine(_root, "album ") : null, scripts);

        pane.SelectedEntry = Row(inFoldedFolder ? "notes.txt" : "report ");

        await pane.RunScriptAsync(new ScriptCommand("tidy", Path.Combine(_root, "tidy.cmd")));

        Assert.Empty(scripts.Asked);
        Refused(pane, inFoldedFolder ? "album " : "report ");
    }

    // ---- Linux: the same names are ordinary -----------------------------------

    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task On_linux_every_one_of_these_is_handed_over()
    {
        var menus = new RecordingMenus();
        PaneViewModel.ShellMenu = menus;

        var scripts = new RecordingScripts();
        var (pane, launcher) = Pane(Path.Combine(_root, "album "), scripts);

        await pane.OpenAsync(Row("t.cmd."));

        pane.SelectedEntry = Row("report ");
        pane.OpenWithApp(new LaunchOption("Editor", "editor"));
        pane.RunSelection();
        pane.OpenTerminalHere();
        await pane.OpenShellMenuAsync(background: false);
        await pane.RunScriptAsync(new ScriptCommand("tidy", Path.Combine(_root, "tidy.sh")));

        Assert.Contains("open " + Path.Combine(_root, "t.cmd."), launcher.Asked);
        Assert.Contains("with " + Path.Combine(_root, "report "), launcher.Asked);
        Assert.Contains("run " + Path.Combine(_root, "report "), launcher.Asked);
        Assert.Contains("terminal " + Path.Combine(_root, "album "), launcher.Asked);
        Assert.Equal([Path.Combine(_root, "report ")], menus.Asked);
        Assert.Contains(Path.Combine(_root, "report "), scripts.Asked);
    }
}
