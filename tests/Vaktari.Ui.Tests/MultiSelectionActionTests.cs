using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Verbs that act on the whole selection.
///
/// **Open, Open with and Run as administrator each acted on one of N.** They
/// were keyed on SelectedEntry while nine other call sites used SelectionPaths,
/// so picking five images and pressing Enter opened one — silently, with
/// nothing to say the other four had been dropped.
/// </summary>
public sealed class MultiSelectionActionTests : OwnedViewModels
{
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

    private sealed class RecordingLauncher : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];
        public List<string> Elevated { get; } = [];
        public List<string> OpenedWith { get; } = [];

        public bool CanElevate => true;

        public void Open(string path) => Opened.Add(path);
        public void OpenElevated(string path) => Elevated.Add(path);
        public void OpenWith(string path, LaunchOption option) => OpenedWith.Add(path);

        public void OpenTerminal(string directory) { }
        public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
    }

    private static FileEntry File(string name, bool directory = false)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1, DateTimeOffset.UnixEpoch,
            directory ? EntryFlags.Directory : EntryFlags.None);

    private PaneViewModel Pane(RecordingLauncher launcher, params FileEntry[] selected)
    {
        var pane = Own(new PaneViewModel(new Inert(), null, launcher)
        {
            CurrentPath = Path.GetTempPath(),
        });

        foreach (var entry in selected) pane.SelectedEntries.Add(entry);

        // The focused row, as a real selection always has.
        if (selected.Length > 0) pane.SelectedEntry = selected[0];

        return pane;
    }

    [AvaloniaFact]
    public void Opening_five_files_opens_five()
    {
        var launcher = new RecordingLauncher();
        var pane = Pane(launcher,
            File("a.txt"), File("b.txt"), File("c.txt"), File("d.txt"), File("e.txt"));

        _ = pane.OpenSelectedAsync();

        Assert.Equal(5, launcher.Opened.Count);
    }

    /// <summary>
    /// A folder among the selection is left alone rather than opened as a file:
    /// there is no sensible "navigate into all of these".
    /// </summary>
    [AvaloniaFact]
    public void A_folder_in_a_multi_selection_is_not_launched()
    {
        var launcher = new RecordingLauncher();
        var pane = Pane(launcher, File("a.txt"), File("pictures", directory: true), File("b.txt"));

        _ = pane.OpenSelectedAsync();

        Assert.Equal(2, launcher.Opened.Count);
        Assert.DoesNotContain(launcher.Opened, p => p.EndsWith("pictures"));
    }

    /// <summary>
    /// **A guard, because "open" on four hundred files launches four hundred
    /// processes** and the machine is gone. It says so rather than doing
    /// nothing, which would be indistinguishable from a dead key.
    /// </summary>
    [AvaloniaFact]
    public void Opening_far_too_many_refuses_and_says_why()
    {
        var launcher = new RecordingLauncher();

        var many = Enumerable.Range(0, 40).Select(i => File($"f{i}.txt")).ToArray();
        var pane = Pane(launcher, many);

        _ = pane.OpenSelectedAsync();

        Assert.Empty(launcher.Opened);
        Assert.Contains("select fewer", pane.Status);
    }

    [AvaloniaFact]
    public void Running_as_administrator_takes_every_executable_chosen()
    {
        var launcher = new RecordingLauncher();
        var pane = Pane(launcher, File("one.exe"), File("two.exe"), File("notes.txt"));

        pane.RunAsAdministrator();

        // The text file is not something Windows can start elevated, so it is
        // not sent — but both executables are.
        Assert.Equal(2, launcher.Elevated.Count);
    }

    [AvaloniaFact]
    public void Open_with_takes_every_file_chosen()
    {
        var launcher = new RecordingLauncher();
        var pane = Pane(launcher, File("a.png"), File("b.png"), File("c.png"));

        pane.OpenWithApp(new LaunchOption("Paint", "paint", null));

        Assert.Equal(3, launcher.OpenedWith.Count);
    }

    /// <summary>A single selection still behaves exactly as it did — a folder
    /// is navigated, not launched.</summary>
    [AvaloniaFact]
    public void One_folder_selected_is_still_navigated_rather_than_opened()
    {
        var launcher = new RecordingLauncher();
        var pane = Pane(launcher, File("pictures", directory: true));

        _ = pane.OpenSelectedAsync();

        Assert.Empty(launcher.Opened);
    }
}
