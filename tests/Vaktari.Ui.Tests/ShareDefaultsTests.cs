using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the share dialog hands a share, and what the shell says once one is
/// running. The defaults themselves — a password, one address, no
/// announcement — are pinned in Core; these pin that the choice made in the
/// dialog reaches the server and that the answer reaches the person.
/// </summary>
public sealed class ShareDefaultsTests : OwnedViewModels
{
    [AvaloniaFact]
    public async Task The_dialog_starts_a_share_unannounced_unless_the_box_is_ticked()
    {
        var asked = new List<ShareOptions>();

        var model = new ShareRequestViewModel(
            Path.GetTempPath(), (_, options) => { asked.Add(options); return Task.CompletedTask; });

        await model.ShareCommand.ExecuteAsync(null);

        model.Announce = true;
        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal(
            [new ShareOptions(Writable: false), new ShareOptions(Writable: false, Announce: true)],
            asked);
    }

    /// <summary>The address is the whole handover — password included — and
    /// what the platform had to say rides behind it.</summary>
    [AvaloniaFact]
    public async Task The_status_line_carries_the_address_and_what_the_platform_said()
    {
        var shell = Own(new ShellViewModel(new Inert(), sharing: new Served("this machine is on a public network")));
        shell.Start(null, Path.GetTempPath());

        await shell.ShareFolderCommand.ExecuteAsync(null);

        Assert.Contains("http://192.0.2.7:8080/?pw=abcdefghjkmnpqrs", shell.ActiveTab!.Status);
        Assert.Contains("public network", shell.ActiveTab.Status);
    }

    [AvaloniaFact]
    public async Task With_nothing_to_say_the_status_line_ends_at_the_address()
    {
        var shell = Own(new ShellViewModel(new Inert(), sharing: new Served(null)));
        shell.Start(null, Path.GetTempPath());

        await shell.ShareFolderCommand.ExecuteAsync(null);

        Assert.EndsWith("http://192.0.2.7:8080/?pw=abcdefghjkmnpqrs", shell.ActiveTab!.Status);
    }

    /// <summary>A server that answers any start with one session.</summary>
    private sealed class Served(string? warning) : IFileSharing
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<ShareSession> Active => [];

        public Task<ShareSession> StartAsync(string path, ShareOptions options, CancellationToken ct)
            => Task.FromResult(new ShareSession
            {
                Path = path,
                Url = "http://192.0.2.7:8080/?pw=abcdefghjkmnpqrs",
                Port = 8080,
                Writable = options.Writable,
                Password = "abcdefghjkmnpqrs",
                Warning = warning,
                Handle = new object(),
            });

        public Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct) => Task.FromResult(true);
        public Task StopAsync(ShareSession session) => Task.CompletedTask;
        public Task StopAllAsync() => Task.CompletedTask;

        public event EventHandler? Changed { add { } remove { } }
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
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
