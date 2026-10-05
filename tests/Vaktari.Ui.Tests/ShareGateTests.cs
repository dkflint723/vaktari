using Avalonia.Headless.XUnit;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The Share and Install-copyparty rows while copyparty is still being looked
/// for.
///
/// **The look moved off startup, so "not available" can now mean "not yet".**
/// It used to run inside the first window's constructor, on the UI thread, so
/// the answer was always in by the time a menu opened. Now it runs in the
/// background after the window is up, and for that moment the gates must
/// offer neither row: Share would fail, and Install — what the old gate said
/// for anything not available — would offer to install copyparty on a machine
/// that has it (review H3). A share asked for by keyboard before the look
/// lands waits for it rather than saying "not available".
/// </summary>
public sealed class ShareGateTests : OwnedViewModels
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

    /// <summary>A provider whose look the test finishes, with the answer it
    /// chooses.</summary>
    private sealed class Looking : IFileSharing
    {
        private readonly TaskCompletionSource _look = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _available;

        public int Asked;

        public void Finish(bool available)
        {
            _available = available;
            _look.TrySetResult();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool IsKnown => _look.Task.IsCompleted;
        public bool IsAvailable => IsKnown && _available;
        public string? UnavailableReason => IsAvailable ? null : "copyparty is not installed";
        public IReadOnlyList<ShareSession> Active => [];

        public event EventHandler? Changed;

        public Task EnsureKnownAsync()
        {
            Interlocked.Increment(ref Asked);
            return _look.Task;
        }

        public Task<ShareSession> StartAsync(string path, ShareOptions options, CancellationToken ct)
            => throw new InvalidOperationException("not in a test");

        public Task StopAsync(ShareSession session) => Task.CompletedTask;
        public Task StopAllAsync() => Task.CompletedTask;
        public Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct) => Task.FromResult(true);
    }

    [AvaloniaFact]
    public void While_the_look_runs_neither_share_nor_install_is_offered()
    {
        var sharing = new Looking();
        var shell = Own(new ShellViewModel(new Inert(), sharing: sharing));

        Assert.False(shell.CanShare, "Share was offered before anything was known");
        Assert.False(shell.CanInstallSharing, "Install copyparty was offered while the look for it was still running");
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Once_the_look_lands_exactly_one_of_them_is_offered(bool installed)
    {
        var sharing = new Looking();
        var shell = Own(new ShellViewModel(new Inert(), sharing: sharing));

        sharing.Finish(installed);

        Assert.Equal(installed, shell.CanShare);
        Assert.Equal(!installed, shell.CanInstallSharing);
    }

    /// <summary>
    /// The keyboard and palette route can arrive before the background look:
    /// the dialog opens once the look says copyparty is there, not "sharing is
    /// not available" before it has looked.
    /// </summary>
    [AvaloniaFact]
    public async Task Asking_to_share_before_the_look_lands_waits_for_it()
    {
        var sharing = new Looking();
        var shell = Own(new ShellViewModel(new Inert(), sharing: sharing));
        var opened = 0;
        shell.ShareDialogRequested += (_, _) => opened++;

        var asking = shell.RequestShareCommand.ExecuteAsync(null);

        Assert.Equal(1, sharing.Asked);
        Assert.Equal(0, opened);

        sharing.Finish(available: true);
        await asking.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, opened);
    }
}
