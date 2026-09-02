using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Vaktari.Ui.Tests;

public sealed class ZzTmpRetryProbe(ITestOutputHelper output)
{
    private sealed class Flaky(string deadPath) : IFileSystemProvider
    {
        public Dictionary<string, int> Attempts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Attempts[path] = Attempts.TryGetValue(path, out var n) ? n + 1 : 1;
            await Task.Yield();

            if (string.Equals(path, deadPath, StringComparison.OrdinalIgnoreCase))
                throw new DirectoryNotFoundException("no such folder");

            yield return new[]
            {
                new FileEntry("a.txt", Path.Combine(path, "a.txt"), 1, DateTimeOffset.Now, EntryFlags.None),
            };
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

    private sealed class Store : IRecentStore
    {
        public List<string> Recorded { get; } = [];
        public void Record(string path, RecentKind kind) => Recorded.Add($"{kind}:{path}");
        public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count) => [];
        public void Forget(string path) { }
        public event EventHandler? Changed;
    }

    private static async Task Pump(Task work)
    {
        for (var i = 0; i < 400 && !work.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
        await work;
    }

    [AvaloniaFact]
    public async Task Probe()
    {
        var good = Path.Combine(Path.GetTempPath(), "vaktari-probe-good");
        var dead = Path.Combine(Path.GetTempPath(), "vaktari-probe-dead");

        var fs = new Flaky(dead);
        var store = new Store();
        PaneViewModel.Recents = store;

        var pane = new PaneViewModel(fs);

        await Pump(pane.NavigateAsync(good));
        output.WriteLine($"after good: IsLoaded={pane.IsLoaded} IsLoading={pane.IsLoading} " +
                         $"CurrentPath={pane.CurrentPath} attempts(good)={fs.Attempts.GetValueOrDefault(good)}");

        await Pump(pane.NavigateAsync(dead));
        output.WriteLine($"after dead#1: IsLoaded={pane.IsLoaded} IsLoading={pane.IsLoading} " +
                         $"LoadError='{pane.LoadError}' CurrentPath={pane.CurrentPath} " +
                         $"attempts(dead)={fs.Attempts.GetValueOrDefault(dead)}");
        output.WriteLine($"recorded after dead#1: {string.Join(" , ", store.Recorded)}");

        await Pump(pane.NavigateAsync(dead));
        output.WriteLine($"after dead#2 (retry): attempts(dead)={fs.Attempts.GetValueOrDefault(dead)} " +
                         $"IsLoaded={pane.IsLoaded} LoadError='{pane.LoadError}'");

        // F5 route, for comparison.
        await Pump(pane.RefreshAsync());
        output.WriteLine($"after F5: attempts(dead)={fs.Attempts.GetValueOrDefault(dead)}");

        output.WriteLine($"recorded final: {string.Join(" , ", store.Recorded)}");

        PaneViewModel.Recents = null;
    }
}
