using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Handing a folder out to somebody else, by either of the two routes there
/// are: a link to a folder already in cloud storage, or a server this machine
/// runs for as long as the share lasts.
///
/// **They are one concern because they end in the same place** — a URL on the
/// clipboard and a row that has to be stoppable — and they fail the same way: a
/// share outlives the click that made it, so every one of them has a stop, and
/// nothing here assumes a share it started is still running.
///
/// Gathered rather than lifted. The link half sat under a "drive links"
/// heading in ShellViewModel; the server half went into ShellViewModel.Network
/// in an earlier slice of roadmap 22, which left that file holding sharing as
/// well as network and slightly misnamed. This is the correction as much as it
/// is a new file.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- network sharing -------------------------------------------------

    public ObservableCollection<ShareSession> Shares { get; } = new();

    public bool HasShares => Shares.Count > 0;

    // ---- drive links -------------------------------------------------------

    private ILinkSharing? _links;
    private Action<IReadOnlyList<DriveLink>>? _saveLinks;
    private Action<string>? _openUrl;

    /// <summary>
    /// Links Vaktari has created, oldest first. In the sidebar beside the
    /// copyparty shares and for the same reason: something you are sharing
    /// must never be something you have to remember.
    /// </summary>
    public ObservableCollection<DriveLink> DriveLinks { get; } = new();

    public bool HasDriveLinks => DriveLinks.Count > 0;

    /// <summary>The SHARING sidebar section shows when either kind exists.</summary>
    public bool HasAnySharing => HasShares || HasDriveLinks;

    /// <summary>
    /// Wires the link provider and what it remembers. The saver is handed in
    /// rather than the store, so the shell stays ignorant of files — the same
    /// arrangement the session has.
    /// </summary>
    public void UseDriveLinks(
        ILinkSharing? links,
        IReadOnlyList<DriveLink> remembered,
        Action<IReadOnlyList<DriveLink>> save,
        Action<string>? openUrl = null)
    {
        _links = links;
        _saveLinks = save;
        _openUrl = openUrl;

        DriveLinks.Clear();
        foreach (var link in remembered) DriveLinks.Add(link);

        DriveLinks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDriveLinks));
            OnPropertyChanged(nameof(HasAnySharing));
        };

        OnPropertyChanged(nameof(HasDriveLinks));
        OnPropertyChanged(nameof(HasAnySharing));
    }

    /// <summary>
    /// Whether the menu may offer a link for this path at all — a question
    /// about WHERE the item is, deliberately not about whether the tool is
    /// installed yet. The row IS the first step: clicking it installs what
    /// is missing, signs in, and shares, in that order, so the person meets
    /// one entry that says what they want instead of a setup errand.
    /// </summary>
    public bool CanLinkShare(string path)
        => _links is { } links && links.MapToRemote(path) is not null;

    /// <summary>The link already covering this path, if Vaktari made one.</summary>
    public DriveLink? LinkFor(string path)
        => DriveLinks.FirstOrDefault(l =>
            Vaktari.Core.FileSystem.PathRules.Same(l.LocalPath, path));

    /// <summary>The disabled "installing…" row, shown in the share row's
    /// place while the download runs — the state that must never look like
    /// the feature left.</summary>
    public bool ShowDriveInstallBusy(string path)
        => IsInstallingDriveLinks
           && _links is { } links
           && links.MapToRemote(path) is not null;

    [ObservableProperty] private bool _isInstallingDriveLinks;

    /// <summary>
    /// The install half of the share click. True when the tool is ready to
    /// use — already present, or fetched just now; false when it could not be
    /// had, with the status line already saying why.
    ///
    /// Downloading on the click is a deliberate departure from the copyparty
    /// entry's explicit install row: this row says "Share via Proton Drive",
    /// and fetching the vendor's own tool is part of doing exactly that — the
    /// click is the consent. What stays non-automatic is everything else:
    /// nothing downloads before a person asks to share.
    /// </summary>
    private async Task<bool> EnsureDriveToolAsync(ILinkSharing links, PaneViewModel pane)
    {
        if (links.IsAvailable) return true;

        IsInstallingDriveLinks = true;

        var progress = new Progress<string>(line => pane.Status = line);

        try
        {
            return await links.InstallAsync(progress, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            pane.Status = $"could not install the Proton Drive CLI: {ex.Message}";
            return false;
        }
        finally
        {
            IsInstallingDriveLinks = false;
        }
    }

    /// <summary>
    /// Creates the link, remembers it, and puts the URL straight on the
    /// clipboard — the click means "get me the thing I send to a friend", and
    /// making them find a copy button afterwards would be a second errand.
    /// </summary>
    public async Task CreateDriveLinkAsync(string path)
    {
        if (_links is not { } links || ActiveTab is not { } pane) return;

        if (IsInstallingDriveLinks)
        {
            pane.Status = "still downloading the Proton Drive CLI…";
            return;
        }

        // One click, whole flow: fetch the tool if it is missing, then the
        // sign-in retry inside the create opens the browser if needed. The
        // person asked to share; the steps between are Vaktari's errand.
        if (!await EnsureDriveToolAsync(links, pane).ConfigureAwait(true)) return;

        pane.Status = "creating the Proton Drive link…";

        try
        {
            var link = await WithSignInRetryAsync(
                pane, () => links.CreateLinkAsync(path, CancellationToken.None))
                .ConfigureAwait(true);

            // Re-sharing replaces the remembered row rather than stacking a
            // duplicate: one item, one row, one kill switch.
            if (LinkFor(path) is { } previous) DriveLinks.Remove(previous);

            DriveLinks.Add(link);
            _saveLinks?.Invoke(DriveLinks.ToList());

            CopyTextRequested?.Invoke(this, link.Url);
            pane.Status = "link copied — anyone with it can open the file";
        }
        catch (Exception ex)
        {
            pane.Status = Vaktari.Core.FileSystem.Failures.Describe(ex, "create that link");
        }
    }

    /// <summary>
    /// Runs a link operation, treating "you are not signed in" as a step
    /// rather than a failure: the tool's own browser sign-in is started, any
    /// link it prints is opened, and the operation is retried once.
    ///
    /// **This is the whole sign-in story on purpose.** No credentials dialog,
    /// no token field, no probe before every call — the person authenticates
    /// in their browser exactly once, the session lives in the operating
    /// system's credential store, and from then on the retry never fires.
    /// </summary>
    private async Task<T> WithSignInRetryAsync<T>(PaneViewModel pane, Func<Task<T>> operation)
    {
        try
        {
            return await Task.Run(operation).ConfigureAwait(true);
        }
        catch (IOException first) when (
            _links is { } links
            && Vaktari.Core.Sharing.ProtonDriveLinks.LooksSignedOut(first.Message))
        {
            pane.Status = "signing in to Proton Drive — finish in your browser…";

            var openUrl = _openUrl;

            var signedIn = await Task.Run(() => links.SignInAsync(
                url => openUrl?.Invoke(url), CancellationToken.None)).ConfigureAwait(true);

            if (!signedIn)
            {
                pane.Status = "the sign-in did not complete";
                throw;
            }

            return await Task.Run(operation).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task StopDriveLinkAsync(DriveLink? link)
    {
        if (link is null || _links is not { } links) return;

        var pane = ActiveTab;

        try
        {
            if (pane is null)
            {
                await Task.Run(
                    () => links.RevokeAsync(link, CancellationToken.None)).ConfigureAwait(true);
            }
            else
            {
                await WithSignInRetryAsync<object?>(pane, async () =>
                {
                    await links.RevokeAsync(link, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }).ConfigureAwait(true);
            }

            DriveLinks.Remove(link);
            _saveLinks?.Invoke(DriveLinks.ToList());

            if (pane is not null) pane.Status = $"no longer sharing {link.Label}";
        }
        catch (Exception ex)
        {
            // The row STAYS on failure: a link that might still work must keep
            // its kill switch visible.
            if (pane is not null)
                pane.Status = Vaktari.Core.FileSystem.Failures.Describe(ex, "remove that link");
        }
    }

    [RelayCommand]
    private void CopyDriveLink(DriveLink? link)
    {
        if (link is not null) CopyTextRequested?.Invoke(this, link.Url);
    }

    public bool CanShare => _sharing?.IsAvailable == true;

    /// <summary>A backend exists for this platform, but is not installed yet.</summary>
    public bool CanInstallSharing => _sharing is { IsAvailable: false } && !IsInstalling;

    /// <summary>
    /// Whether sharing has any presence in the menu at all.
    ///
    /// **Sharing used to disappear from the menu while it was being
    /// installed.** It was two sibling entries — share, and install — gated on
    /// two flags that are both false during the download, so clicking "install
    /// copyparty" closed the menu and the next right-click showed nothing at
    /// all where the feature had been. Nothing was broken and nothing said so.
    ///
    /// One submenu now, gated on there being a backend for this platform, with
    /// the three states inside it: share it, install it, or installing. That
    /// also settles the older complaint that one feature had two top-level
    /// entries whose labels shared no words.
    /// </summary>
    public bool HasSharingEntry => _sharing is not null;

    [ObservableProperty] private bool _isInstalling;

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallSharing));
    }

    /// <summary>
    /// Installs the sharing backend on request. Not automatic on first share:
    /// putting software on someone's machine should be something they chose,
    /// and a half-finished install in the middle of sharing a folder is a
    /// confusing place to discover a network problem.
    /// </summary>
    [RelayCommand]
    private async Task InstallSharingAsync()
    {
        if (_sharing is null || _sharing.IsAvailable || IsInstalling) return;

        IsInstalling = true;

        var pane = ActiveTab;
        var progress = new Progress<string>(line =>
        {
            if (pane is not null) pane.Status = line;
        });

        try
        {
            await _sharing.InstallAsync(progress, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (pane is not null) pane.Status = $"install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            OnPropertyChanged(nameof(CanShare));
            OnPropertyChanged(nameof(CanInstallSharing));
        }
    }

    private void RefreshShares()
    {
        Shares.Clear();
        foreach (var share in _sharing?.Active ?? []) Shares.Add(share);

        OnPropertyChanged(nameof(HasShares));
        OnPropertyChanged(nameof(HasAnySharing));
    }

    /// <summary>
    /// Serves the current folder read-only. Read-only is not a setting here on
    /// purpose — writable sharing is a separate, explicit command, because the
    /// difference is "people can look" versus "people can overwrite".
    /// </summary>
    [RelayCommand]
    private Task ShareFolderAsync() => ShareAsync(writable: false);

    [RelayCommand]
    private Task ShareFolderWritableAsync() => ShareAsync(writable: true);

    private async Task ShareAsync(bool writable)
    {
        if (ActiveTab is not { } pane) return;

        if (_sharing is not { IsAvailable: true })
        {
            pane.Status = _sharing?.UnavailableReason ?? "sharing is not available";
            return;
        }

        // The folder that was right-clicked, not the one being listed. Sharing
        // the parent when a subfolder was selected exposes every sibling too,
        // which is both surprising and a much larger surface than intended.
        var target = pane.SelectedEntry is { IsDirectory: true } selected
            ? selected.FullPath
            : pane.CurrentPath;

        try
        {
            await ShareFolderAsync(target, new ShareOptions(writable)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            pane.Status = $"could not share: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopShareAsync(ShareSession? session)
    {
        if (_sharing is null || session is null) return;

        await _sharing.StopAsync(session).ConfigureAwait(true);

        if (ActiveTab is { } pane) pane.Status = $"stopped sharing {session.Label}";
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
