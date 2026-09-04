using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Double-clicking a file the desktop will not open.
///
/// **It did nothing at all.** IApplicationLauncher.Open returned void, so both
/// launchers caught whatever went wrong and dropped it — the Windows one into
/// Quiet.Swallowed, which prints only under VAKTARI_QUIET_DEBUG, the Linux one
/// into a bare catch — and OpenAsync fired it with nothing afterwards. Delete a
/// file from another window, double-click its row before the listing catches
/// up, and the pane was indistinguishable from one where the click had missed:
/// no window, no message, no status.
///
/// The launcher now hands the failure back and this is where it is said. Both
/// routes to a file arrive here — the pointer and Enter through OpenAsync, and
/// a path typed into the location bar, which opens through this method rather
/// than through the launcher — so one line covers both.
/// </summary>
public sealed class OpenFailureTests : OwnedViewModels
{
    /// <summary>Answers structurally and reads nothing: these tests are about
    /// what OpenAsync does with the launcher's answer, not about a listing.
    /// </summary>
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
    /// A desktop that refuses, on request.
    ///
    /// It still RECORDS the path, so a test that expects silence has something
    /// that could have contradicted it: "nothing was said" is only worth
    /// asserting where the launch is known to have been attempted.
    /// </summary>
    private sealed class RefusingLauncher(Exception? failure) : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];

        public Exception? Open(string path)
        {
            Opened.Add(path);
            return failure;
        }

        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    private (PaneViewModel Pane, RefusingLauncher Launcher) Pane(Exception? failure)
    {
        var launcher = new RefusingLauncher(failure);

        var pane = Own(new PaneViewModel(new InertFileSystem(), null, launcher)
        {
            CurrentPath = Path.GetTempPath(),
        });

        return (pane, launcher);
    }

    private static FileEntry Row(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 0, DateTimeOffset.Now,
               EntryFlags.None);

    /// <summary>
    /// The file is gone. The wording is Failures.Describe's, which is the same
    /// sentence a copy or a listing produces for the same fact — the point of
    /// handing back the exception rather than a bool.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_that_will_not_open_says_so()
    {
        var (pane, launcher) = Pane(new FileNotFoundException());

        await pane.OpenAsync(Row("notes.txt"));

        Assert.Single(launcher.Opened);
        Assert.Equal("that file is not there any more", pane.Status);
    }

    /// <summary>
    /// A refusal that is not about the file being missing still reaches the
    /// user, in the words the rest of the application uses for it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refusal_is_described_rather_than_named()
    {
        var (pane, _) = Pane(new UnauthorizedAccessException());

        await pane.OpenAsync(Row("payroll.xlsx"));

        // Not "UnauthorizedAccessException", which is what a status bar built
        // from ex.ToString would have said.
        Assert.Equal("you do not have permission to open that file", pane.Status);
    }

    /// <summary>
    /// GUARD. A launch the desktop accepted must say nothing: the status bar is
    /// shared, and an "opened it" line would push off whatever the pane was
    /// actually reporting for a fact the user can see for themselves.
    /// </summary>
    [AvaloniaFact]
    public async Task A_launch_that_is_accepted_says_nothing()
    {
        var (pane, launcher) = Pane(failure: null);

        pane.Status = "12 items";

        await pane.OpenAsync(Row("notes.txt"));

        Assert.Single(launcher.Opened);
        Assert.Equal("12 items", pane.Status);
    }
}
