using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Sharing;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Machines other than this one: finding them, connecting to them, and
/// handing a folder back out to them.
///
/// **Every path in here belongs to somebody else's computer**, which is what
/// the whole file has in common and why its failures read differently from
/// the rest of the shell's. A local action fails or it does not; these time
/// out, answer slowly, or answer with a machine that has since gone — so the
/// browse has a guard against running twice, the connect reports what it
/// could not reach, and nothing assumes an address still resolves.
///
/// CopyTextRequested stays in ShellViewModel: four callers outside this file
/// use it, so it is the shell's clipboard route rather than this file's.
///
/// Split out of ShellViewModel under roadmap 22 — 4,388 lines, 278 members
/// and no partials at all before this. Nothing moved changed.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- network discovery -----------------------------------------------

    private INetworkDiscovery? _discovery;

    public ObservableCollection<DiscoveredService> Discovered { get; } = new();

    public bool HasDiscovered => Discovered.Count > 0;

    [ObservableProperty] private bool _isBrowsing;

    public bool CanBrowseNetwork => _discovery?.IsAvailable == true && !IsBrowsing;

    partial void OnIsBrowsingChanged(bool value) => OnPropertyChanged(nameof(CanBrowseNetwork));

    public void UseDiscovery(INetworkDiscovery? discovery)
    {
        _discovery = discovery;
        OnPropertyChanged(nameof(CanBrowseNetwork));
    }

    /// <summary>
    /// Sweeps the network on demand rather than continuously — it costs a
    /// couple of seconds and multicast traffic, and nobody wants either
    /// happening in the background forever.
    /// </summary>
    [RelayCommand]
    private async Task BrowseNetworkAsync()
    {
        if (_discovery is null || IsBrowsing) return;

        var pane = ActiveTab;

        if (!_discovery.IsAvailable)
        {
            if (pane is not null) pane.Status = _discovery.UnavailableReason ?? "discovery unavailable";
            return;
        }

        IsBrowsing = true;
        if (pane is not null) pane.Status = "looking for servers on the network…";

        try
        {
            var found = await _discovery.BrowseAsync(CancellationToken.None).ConfigureAwait(true);

            Discovered.Clear();
            foreach (var service in found) Discovered.Add(service);

            OnPropertyChanged(nameof(HasDiscovered));

            if (pane is not null)
            {
                pane.Status = found.Count == 0
                    ? "no servers announced themselves"
                    : $"found {found.Count} server(s)";
            }
        }
        catch (Exception ex)
        {
            if (pane is not null) pane.Status = $"discovery failed: {ex.Message}";
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    /// <summary>Asks the view to show connection details; the shell owns no windows.</summary>
    public event EventHandler<ConnectionInfoViewModel>? ConnectionInfoRequested;

    [RelayCommand]
    private async Task DisconnectRemoteAsync(RemoteMount? mount)
    {
        if (_remotes is null || mount is null) return;

        var pane = ActiveTab;

        try
        {
            var ok = await _remotes.UnmountAsync(mount, CancellationToken.None).ConfigureAwait(true);

            Sidebar.RefreshRemotes();

            if (pane is not null)
            {
                pane.Status = ok
                    ? $"disconnected {mount.Label}"
                    : $"could not disconnect {mount.Label} — something may still be using it";
            }
        }
        catch (Exception ex)
        {
            if (pane is not null) pane.Status = Vaktari.Core.FileSystem.Failures.Describe(ex);
        }
    }

    [RelayCommand]
    private void ShowRemoteInfo(RemoteMount? mount)
    {
        if (mount is null) return;

        var info = new ConnectionInfoViewModel(
            mount.Label,
            [
                new("Protocol", mount.Protocol),
                new("Status", mount.Reachable ? "connected" : "offline — the far end is not answering"),
                new("Local path", mount.Path),
            ],
            mount.Path,
            disconnect: () => DisconnectRemoteAsync(mount),
            copy: text => CopyTextRequested?.Invoke(this, text));

        ConnectionInfoRequested?.Invoke(this, info);
    }

    [RelayCommand]
    private void ShowServiceInfo(DiscoveredService? service)
    {
        if (service is null) return;

        var info = new ConnectionInfoViewModel(
            service.Name,
            [
                new("Service", service.Friendly),
                new("Announced as", service.ServiceType),
                new("Host", service.Host),
                new("Address", service.Address),
                new("Port", service.Port.ToString()),
                new("Connects as", service.MountUri),
            ],
            service.MountUri,

            // Nothing to disconnect: this has been seen, not mounted.
            disconnect: null,
            copy: text => CopyTextRequested?.Invoke(this, text));

        ConnectionInfoRequested?.Invoke(this, info);
    }

    [RelayCommand]
    private void CopyRemotePath(RemoteMount? mount)
    {
        if (mount is null) return;

        CopyTextRequested?.Invoke(this, mount.Path);
        if (ActiveTab is { } pane) pane.Status = $"copied {mount.Path}";
    }

    /// <summary>Mounts a discovered service and opens it.</summary>
    [RelayCommand]
    private async Task OpenDiscoveredAsync(DiscoveredService? service)
    {
        if (service is null) return;

        await ConnectToAsync(service.MountUri).ConfigureAwait(true);
    }

    /// <summary>Asks the view for a URI; the shell owns no dialogs.</summary>
    public event EventHandler? ConnectRequested;

    [RelayCommand]
    private void Connect() => ConnectRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Mounts a URI and navigates to wherever the desktop put it.
    /// </summary>
    public async Task ConnectToAsync(string uri)
    {
        if (_remotes is null || ActiveTab is not { } pane) return;

        uri = uri.Trim();
        if (uri.Length == 0) return;

        pane.Status = $"connecting to {uri}…";

        try
        {
            var mount = await _remotes.MountAsync(uri, CancellationToken.None).ConfigureAwait(true);

            Sidebar.RefreshRemotes();
            await pane.NavigateAsync(mount.Path).ConfigureAwait(true);

            pane.Status = $"connected to {mount.Label}";
        }
        catch (Exception ex)
        {
            pane.Status = ex.Message;
        }
    }

    /// <summary>Asks the view for the share dialog; the shell owns no windows.</summary>
    public event EventHandler<ShareRequestViewModel>? ShareDialogRequested;

    /// <summary>
    /// Sharing without a right-click: pick any folder, by typing or browsing.
    /// Starts at the folder currently open, which is usually the answer.
    /// </summary>
    [RelayCommand]
    private void RequestShare()
    {
        if (_sharing is null) return;

        if (!_sharing.IsAvailable)
        {
            if (ActiveTab is { } tab) tab.Status = _sharing.UnavailableReason ?? "sharing is not available";
            return;
        }

        var start = ActiveTab?.SelectedEntry is { IsDirectory: true } selected
            ? selected.FullPath
            : ActiveTab?.CurrentPath ?? "";

        var request = new ShareRequestViewModel(start, ShareFolderAsync);

        ShareDialogRequested?.Invoke(this, request);
    }

    /// <summary>Shared by the dialog and the context menu, so both behave alike.</summary>
    private async Task ShareFolderAsync(string path, ShareOptions options)
    {
        if (_sharing is null) return;

        var session = await _sharing.StartAsync(path, options, CancellationToken.None)
                                    .ConfigureAwait(true);

        if (ActiveTab is { } pane)
        {
            // The address carries the password, so the line is the whole
            // handover; what the platform had to say rides behind it.
            pane.Status = (options.Writable
                    ? $"sharing {session.Label} read-write at {session.Url}"
                    : $"sharing {session.Label} at {session.Url}")
                + (session.Warning is { } warning ? $" — {warning}" : "");
        }
    }

    /// <summary>Nothing served should outlive the window that started it.</summary>
    public Task StopAllSharesAsync() => _sharing?.StopAllAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void CopyShareUrl(ShareSession? session)
    {
        if (session is null) return;

        CopyTextRequested?.Invoke(this, session.Url);

        if (ActiveTab is { } pane) pane.Status = $"copied {session.Url}";
    }
}
