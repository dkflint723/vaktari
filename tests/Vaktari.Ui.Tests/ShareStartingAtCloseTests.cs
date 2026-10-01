using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A window closing while one of its shares is still starting, through the
/// shell and the real provider. The provider's backend holds its start at the
/// platform's warning — the one wait inside a start — until the test lets it
/// go, and its launch starts a process that only waits, in place of a server:
/// no copyparty is run and nothing listens.
///
/// What a closing window does is MainWindow.OnClosing's: the last one out
/// stops every share (<see cref="ShellViewModel.StopAllSharesAsync"/>) and
/// then disposes its shell; any other only disposes its shell. A real window
/// cannot be given this provider — it is the platform's — so the two steps
/// are taken here in the same order.
/// </summary>
public sealed class ShareStartingAtCloseTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-share-close").FullName;
    private readonly string _photos;
    private readonly List<Process> _launched = [];

    public ShareStartingAtCloseTests()
    {
        _photos = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;
        CopypartyShare.ConfigDirectoryOverride = Path.Combine(_root, "configs");
    }

    public override void Dispose()
    {
        base.Dispose();

        CopypartyShare.ConfigDirectoryOverride = null;

        lock (_launched)
        {
            foreach (var server in _launched)
            {
                try { if (!server.HasExited) server.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Holds a start at the platform's warning until released.</summary>
    private sealed class Held : CopypartyBackend
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();

        public override (string? Command, string[] Prefix) Locate() => ("copyparty-stub", []);
        public override IReadOnlyList<InstallAttempt> InstallAttempts() => [];
        public override string NotInstalledHint => "";
        public override string NoInstallerHint => "";
        public override string InstalledButNotFoundHint => "";
        public override string InstallFailedHint => "";

        public override string? StartWarning()
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return null;
        }
    }

    /// <summary>The provider, over a held backend; every server it launches
    /// is a process that waits a minute, killed on the way out.</summary>
    private (CopypartyShare Share, Held Backend) Provider()
    {
        var backend = new Held();

        var share = new CopypartyShare(backend)
        {
            LaunchOverride = _ =>
            {
                var info = OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("ping") { ArgumentList = { "-n", "60", "127.0.0.1" } }
                    : new ProcessStartInfo("sleep") { ArgumentList = { "60" } };

                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                var server = Process.Start(info)!;
                lock (_launched) _launched.Add(server);
                return server;
            },
        };

        return (share, backend);
    }

    private ShellViewModel Shell(IFileSharing sharing)
    {
        var shell = Own(new ShellViewModel(new Inert(), sharing: sharing));
        shell.Start(null, _photos);
        Dispatcher.UIThread.RunJobs();
        return shell;
    }

    private static async Task<bool> Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();

        while (!done())
        {
            if (clock.Elapsed > Ceiling) return false;

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        return true;
    }

    private int Launched
    {
        get { lock (_launched) return _launched.Count; }
    }

    /// <summary>
    /// **The last window closing while its share was still starting left the
    /// server serving** (batch-0.11.2g, reproduced in Core before it was
    /// fixed). The stop swept a list the start was not in yet, and the start
    /// launched its server when the platform's warning came back. Now the
    /// start sees it was overtaken and launches nothing; the shell's status
    /// line, on a window already gone, is all that hears of it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_last_window_closing_while_a_share_starts_leaves_nothing_serving()
    {
        var (share, backend) = Provider();
        var shell = Shell(share);

        var sharing = shell.ShareCurrentFolderCommand.ExecuteAsync(null);

        Assert.True(await Until(() => backend.Entered.IsSet), "the share never began to start");

        // OnClosing, for the last window: stop everything, then let go.
        await shell.StopAllSharesAsync();
        shell.Dispose();

        backend.Release.Set();

        Assert.True(await Until(() => sharing.IsCompleted), "the share's start never finished");
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Launched == 0, $"{Launched} server(s) launched after the last window closed");
        Assert.Empty(share.Active);
    }

    /// <summary>
    /// **Another window closing while its share starts leaves the share to
    /// start**, and the windows still open list it. Shares belong to the
    /// process, not to a window — the last window out stops them all, and
    /// until then a share started anywhere outlives every window but the last
    /// (MainWindow.OnClosing) — so a share asked for and still on its way when
    /// its window went is as wanted as one that arrived a moment sooner. Its
    /// address is said on the status line of the window that asked, which has
    /// gone; the row in the windows still open copies it.
    /// </summary>
    [AvaloniaFact]
    public async Task Another_window_closing_while_its_share_starts_leaves_the_share_listed_elsewhere()
    {
        var (share, backend) = Provider();
        var leaving = Shell(share);
        var staying = Shell(share);

        try
        {
            var sharing = leaving.ShareCurrentFolderCommand.ExecuteAsync(null);

            Assert.True(await Until(() => backend.Entered.IsSet), "the share never began to start");

            // OnClosing, for a window that is not the last: let go, stop nothing.
            leaving.Dispose();

            backend.Release.Set();

            Assert.True(await Until(() => sharing.IsCompleted), "the share's start never finished");

            Assert.Equal(1, Launched);
            Assert.Single(share.Active);
            Assert.True(await Until(() => staying.Shares.Count == 1), "the window still open does not list the share");
            Assert.True(PathRules.Same(_photos, staying.Shares[0].Path));
        }
        finally
        {
            await share.StopAllAsync();
        }
    }

    /// <summary>Lists nothing; the share is about the folder's name, not its contents.</summary>
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
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
