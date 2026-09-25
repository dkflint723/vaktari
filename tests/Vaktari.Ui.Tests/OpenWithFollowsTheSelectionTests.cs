using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The "Open with" list belongs to the file that is selected when it arrives.
///
/// **The list could be the previous file's.** Each selection starts its own
/// lookup off the UI thread, and nothing ordered their answers: a slow one for
/// photo.png landing after notes.txt's filled the submenu with image viewers,
/// and OpenWithApp acts on the LIVE selection — so choosing one opened
/// notes.txt in the image viewer. A folder selected after a file was refilled
/// the same way, with rows for a file that was no longer selected.
///
/// The launcher here holds each lookup until the test lets it go, so the order
/// the answers land in is the test's choice and not the scheduler's.
/// </summary>
public sealed class OpenWithFollowsTheSelectionTests : OwnedViewModels
{
    private static readonly LaunchOption Viewer = new("Image viewer", "viewer.desktop");
    private static readonly LaunchOption Editor = new("Text editor", "editor.desktop");

    [AvaloniaFact]
    public void A_late_answer_for_the_previous_file_does_not_replace_the_list()
    {
        var launcher = new Gated(("photo.png", Viewer), ("notes.txt", Editor));
        var (pane, folder) = Pane(launcher);

        pane.SelectedEntry = File(folder, "photo.png");
        pane.SelectedEntry = File(folder, "notes.txt");

        launcher.Release("notes.txt");
        Settle(() => pane.OpenWithOptions.Count > 0);

        Assert.Equal([Editor], pane.OpenWithOptions);

        // Now the stale one lands.
        launcher.Release("photo.png");
        SettleAfter(launcher, answers: 2);

        Assert.Equal([Editor], pane.OpenWithOptions);
    }

    [AvaloniaFact]
    public void A_folder_selected_after_a_file_is_not_given_the_files_list()
    {
        var launcher = new Gated(("photo.png", Viewer));
        var (pane, folder) = Pane(launcher);

        pane.SelectedEntry = File(folder, "photo.png");
        pane.SelectedEntry = new FileEntry(
            "Pictures", Path.Combine(folder, "Pictures"), 0, DateTimeOffset.Now, EntryFlags.Directory);

        launcher.Release("photo.png");
        SettleAfter(launcher, answers: 1);

        Assert.Empty(pane.OpenWithOptions);
        Assert.False(pane.HasOpenWithOptions);
    }

    // ---- plumbing -----------------------------------------------------------

    private (PaneViewModel Pane, string Folder) Pane(Gated launcher)
    {
        var folder = Path.GetTempPath();

        var pane = Own(new PaneViewModel(new InertFileSystem(), null, launcher)
        {
            CurrentPath = folder,
        });

        return (pane, folder);
    }

    private static FileEntry File(string folder, string name)
        => new(name, Path.Combine(folder, name), 0, DateTimeOffset.Now, EntryFlags.None);

    /// <summary>
    /// Pumped rather than slept on: the Post that fills the list cannot run
    /// without a dispatcher turn.
    /// </summary>
    private static void Settle(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Waits until the launcher has answered this many lookups, then gives the
    /// Post that follows each answer a few turns to land. The Post comes a
    /// statement after the last thing the launcher can see, so there is nothing
    /// closer to wait on.
    /// </summary>
    private static void SettleAfter(Gated launcher, int answers)
    {
        Settle(() => launcher.Answered >= answers);

        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A launcher whose lookups each wait for the test to release their file,
    /// so two selections' answers can be made to arrive in either order.
    /// </summary>
    private sealed class Gated(params (string Name, LaunchOption Option)[] answers) : IApplicationLauncher
    {
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new();
        private int _answered;

        public int Answered => Volatile.Read(ref _answered);

        public void Release(string name) => Gate(name).Set();

        private ManualResetEventSlim Gate(string name) => _gates.GetOrAdd(name, _ => new ManualResetEventSlim());

        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path)
        {
            var name = Path.GetFileName(path);

            Gate(name).Wait(TimeSpan.FromSeconds(10));

            return answers.Where(a => a.Name == name).Select(a => a.Option).ToList();
        }

        // Read straight after the lookup, and so the last thing the test can
        // see before the Post: counted here rather than in the lookup.
        public bool CanChooseApplication
        {
            get
            {
                Interlocked.Increment(ref _answered);
                return false;
            }
        }

        public Exception? Open(string path) => null;
        public void OpenTerminal(string directory) { }
        public void OpenWith(string path, LaunchOption option) { }
    }

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
}
