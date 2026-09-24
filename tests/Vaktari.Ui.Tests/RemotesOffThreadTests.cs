using Avalonia.Headless.XUnit;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The sidebar's network connections are asked for off the window's thread.
///
/// **Asking was done on it**, at startup and after every connect — and asking
/// includes whether each share answers, a directory read per share. One whose
/// server had gone froze the next launch before the window drew.
/// </summary>
public sealed class RemotesOffThreadTests
{
    /// <summary>Connections that are not listed until they are let go.</summary>
    private sealed class Stuck : IRemoteMounts, IDisposable
    {
        public ManualResetEventSlim Release { get; } = new();

        public bool IsAvailable => true;
        public string AddressPrefill => "";
        public string AddressHint => "";

        public IReadOnlyList<RemoteMount> Discover()
        {
            Release.Wait(TimeSpan.FromSeconds(10));

            return [new RemoteMount { Path = @"\\nas\media", Label = "media on nas", Protocol = "smb", Reachable = false }];
        }

        public Task<bool> UnmountAsync(RemoteMount mount, CancellationToken ct) => Task.FromResult(true);

        public Task<RemoteMount> MountAsync(string address, CancellationToken ct)
            => throw new NotSupportedException("nothing here mounts");

        public void Dispose() => Release.Dispose();
    }

    [AvaloniaFact]
    public async Task Listing_the_connections_does_not_hold_the_window()
    {
        using var stuck = new Stuck();
        var sidebar = new SidebarViewModel(places: null);

        sidebar.UseRemotes(stuck);

        // Back already, with nothing yet to show.
        Assert.Empty(sidebar.Remotes);

        stuck.Release.Set();

        for (var i = 0; i < 500 && sidebar.Remotes.Count == 0; i++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(@"\\nas\media", Assert.Single(sidebar.Remotes).Path);
    }
}
