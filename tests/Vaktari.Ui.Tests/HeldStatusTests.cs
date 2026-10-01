using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A held status line outlasts a reload of a large folder** (batch-0.11.2c
/// QA, round 3). The load writes a running count — "1,500 items…" — each
/// time it hands a batch to the listing, and that was not held off: a large
/// folder reloaded while the copy-across walk went wrote its count over
/// "looking inside the folders to copy…". Here the listing arrives in slow
/// batches, so the running count is written several times, and the line held
/// before the reload is the line on screen throughout and after it.
/// </summary>
public sealed class HeldStatusTests : OwnedViewModels
{
    private const string Held = "looking inside the folders to copy…";

    /// <summary>A folder of 6,000 rows that arrives in batches 150 ms apart.</summary>
    private sealed class Slow : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var batch = 0; batch < 6; batch++)
            {
                await Task.Delay(150, ct);

                yield return Enumerable.Range(batch * 1000, 1000)
                    .Select(i => new FileEntry($"f{i:D5}", Path.Combine(path, $"f{i:D5}"), 1, DateTimeOffset.UnixEpoch, EntryFlags.None))
                    .ToList();
            }
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => ValueTask.FromResult<FileEntry?>(null);
        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => ValueTask.FromResult(true);
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    [AvaloniaFact]
    public async Task A_reload_of_a_large_folder_leaves_a_held_line_alone()
    {
        var pane = Own(new PaneViewModel(new Slow(), null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        var owner = new object();
        pane.HoldStatus(owner, Held);

        var seen = new HashSet<string>();
        var reload = pane.RefreshAsync();
        var clock = Stopwatch.StartNew();

        while (!reload.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            Dispatcher.UIThread.RunJobs();
            seen.Add(pane.Status);
            await Task.Delay(5);
        }

        Dispatcher.UIThread.RunJobs();
        seen.Add(pane.Status);

        Assert.True(reload.IsCompleted, "the reload did not finish");
        Assert.Equal(6000, pane.Entries.Count);
        Assert.Equal([Held], seen);

        // Let go of, the next line is the pane's own again.
        pane.ReleaseStatus(owner);
    }
}
