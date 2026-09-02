using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Owns one or two pane groups. Deliberately thin — it decides which side is
/// active and nothing else; all the behaviour lives in PaneViewModel, which is
/// what made split view an addition rather than a rewrite.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IFileSystemProvider _fs;
    private readonly IFileOperations? _ops;
    private readonly IApplicationLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly IScriptRunner? _scripts;
    private readonly ITemplateProvider? _templates;
    private readonly IFileSharing? _sharing;
    private readonly ISessionStore? _store;
    private bool _restoring;
    private bool _started;

    /// <summary>
    /// The right side as it was when last closed. Reopening restores it, so
    /// toggling the split off is not a way to silently lose where you were.
    /// </summary>
    private PaneState? _rememberedRight;

    public ShellViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        ISessionStore? store = null,
        IPlacesProvider? places = null,
        IApplicationLauncher? launcher = null,
        IClipboardService? clipboard = null,
        ISearchProvider? search = null,
        IScriptRunner? scripts = null,
        ITemplateProvider? templates = null,
        IFileSharing? sharing = null)
    {
        _sharing = sharing;

        // Marks are raised by a pane and shown by every listing, so the shell
        // mirrors them rather than owning them.
        CutMarks.Changed += (_, _) =>
            Dispatcher.UIThread.Post(() => CutPaths = CutMarks.Paths);

        if (sharing is not null)
        {
            sharing.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                RefreshShares();

                // Discovery re-runs after an install, so availability can change
                // while the app is open.
                OnPropertyChanged(nameof(CanShare));
                OnPropertyChanged(nameof(CanInstallSharing));
            });
        }

        _scripts = scripts;
        _templates = templates;
        _fs = fs;
        _ops = ops;
        _store = store;
        _launcher = launcher;
        _clipboard = clipboard;

        Sidebar = new SidebarViewModel(places, search, () => ActiveTab?.CurrentPath);

        // A chosen result navigates the active tab to its folder and selects it,
        // rather than opening the file — search is for finding, not launching.
        // Attached here rather than in MainWindow, because this is the only
        // place that knows which pane is active.
        Sidebar.AttachNavigation(path => _ = ActiveTab?.NavigateAsync(path));

        Sidebar.Search.ResultChosen += (_, entry) =>
        {
            var folder = entry.IsDirectory
                ? entry.FullPath
                : Path.GetDirectoryName(entry.FullPath);

            if (folder is null || ActiveTab is not { } pane) return;

            _ = pane.NavigateAsync(folder).ContinueWith(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => pane.SelectedEntry = entry),
                TaskScheduler.Default);
        };

        Left = CreateGroup();
        ActiveGroup = Left;

        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.Rail)
                               or nameof(SidebarViewModel.Width))
                MarkDirty();
        };
    }

    public SidebarViewModel Sidebar { get; }

    public PaneGroupViewModel Left { get; private set; }

    /// <summary>Null unless split. The XAML binds its column's visibility to this.</summary>
    [ObservableProperty] private PaneGroupViewModel? _right;


    [ObservableProperty] private PaneGroupViewModel _activeGroup = null!;

    [ObservableProperty] private double _splitRatio = 0.5;

    /// <summary>
    /// Multiplies the whole type scale and every metric derived from it. Exists
    /// as a user control rather than a constant because "the text is too small"
    /// is an accessibility problem, and the right size depends on the display
    /// and the person, not on a value picked at build time.
    /// </summary>
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private double _iconScale = 1.0;

    /// <summary>Set by the view so scale changes can re-write the resources.</summary>
    public Action<double, double>? ScaleApplier { get; set; }

    partial void OnFontScaleChanged(double value) => ApplyScales();
    partial void OnIconScaleChanged(double value) => ApplyScales();

    private void ApplyScales()
    {
        // Application-level defaults only: the sidebar, status bar and
        // properties window. Panes carry their own scale and set their own
        // TextScale, so nothing here reaches into them.
        ScaleApplier?.Invoke(FontScale, IconScale);
        MarkDirty();
    }

    private static double Step(double value, double delta)
        => Math.Round(Math.Clamp(value + delta, 0.7, 2.5), 2);

    /// <summary>
    /// Scaling applies to ONE pane — whichever is active, or whichever the
    /// pointer is over for the wheel. A reference listing beside a working one
    /// wants different sizes, which is the point of having two.
    /// </summary>
    public void ScalePane(PaneViewModel? pane, double fontDelta, double iconDelta)
    {
        if (pane is null) return;

        if (fontDelta != 0) pane.FontScale = Step(pane.FontScale, fontDelta);
        if (iconDelta != 0) pane.IconScale = Step(pane.IconScale, iconDelta);

        MarkDirty();
    }

    // ---- which pane the menu's size controls act on ------------------------

    /// <summary>
    /// **The menu lives on the rightmost pane, and the sizes it changed were
    /// always that pane's.** Opening the flyout makes its own side active, so
    /// "this pane" could only ever mean the right one — leaving the left half
    /// of a split with no way to be sized at all, by any route but the wheel.
    ///
    /// 0 left, 1 right, 2 both. Only meaningful while split; with one pane
    /// there is nothing to choose between and the chooser is hidden.
    /// </summary>
    [ObservableProperty] private int _scaleTargetIndex = 1;

    partial void OnScaleTargetIndexChanged(int value) => NotifyTargetSizes();

    /// <summary>The panes the menu's controls act on.</summary>
    private IEnumerable<PaneViewModel> ScaleTargets
    {
        get
        {
            if (!IsSplit)
            {
                // Nothing to choose between: whatever is showing.
                if (ActiveTab is { } only) yield return only;

                yield break;
            }

            if (ScaleTargetIndex is 0 or 2 && Left.ActiveTab is { } left) yield return left;
            if (ScaleTargetIndex is 1 or 2 && Right?.ActiveTab is { } right) yield return right;
        }
    }

    /// <summary>What the boxes show: the first target's size, since with both
    /// selected there is no single answer and the left is the one read first.</summary>
    private PaneViewModel? PrimaryTarget => ScaleTargets.FirstOrDefault();

    public double TargetFontPoints
    {
        get => PrimaryTarget?.FontPoints ?? 14;
        set
        {
            foreach (var pane in ScaleTargets.ToList()) pane.FontPoints = value;

            NotifyTargetSizes();
        }
    }

    public double TargetIconPixels
    {
        get => PrimaryTarget?.IconPixels ?? 16;
        set
        {
            foreach (var pane in ScaleTargets.ToList()) pane.IconPixels = value;

            NotifyTargetSizes();
        }
    }

    /// <summary>The boxes follow the wheel and the buttons as well as their own
    /// typing, or they would sit showing a size that is no longer true.</summary>
    public void NotifyTargetSizes()
    {
        OnPropertyChanged(nameof(TargetFontPoints));
        OnPropertyChanged(nameof(TargetIconPixels));
    }

    private void ScaleTargeted(double fontDelta, double iconDelta)
    {
        foreach (var pane in ScaleTargets.ToList()) ScalePane(pane, fontDelta, iconDelta);

        NotifyTargetSizes();
    }

    [RelayCommand] private void FontLarger()  => ScaleTargeted(0.1, 0);
    [RelayCommand] private void FontSmaller() => ScaleTargeted(-0.1, 0);
    [RelayCommand] private void IconsLarger()  => ScaleTargeted(0, 0.15);
    [RelayCommand] private void IconsSmaller() => ScaleTargeted(0, -0.15);

    /// <summary>
    /// The menu's reset, which follows the chooser. Ctrl+0 keeps its own
    /// meaning — the pane being worked in — because a keystroke should not
    /// depend on a menu setting somebody left on "both" an hour ago.
    /// </summary>
    [RelayCommand]
    private void ResetTargetedScale()
    {
        foreach (var pane in ScaleTargets.ToList()) ResetPaneScale(pane);

        NotifyTargetSizes();
    }

    /// <summary>Ctrl+0 puts both back, since one control resetting only half of
    /// the sizing would be a puzzle rather than a reset.</summary>
    [RelayCommand]
    private void ZoomReset() => ResetPaneScale(ActiveTab);

    /// <summary>
    /// Back to default for one pane. Separate from the command so the wheel
    /// click can reset whichever pane the pointer is over, matching how
    /// Ctrl+wheel already targets by position rather than by focus.
    /// </summary>
    public void ResetPaneScale(PaneViewModel? pane)
    {
        if (pane is null) return;

        pane.FontScale = 1.0;
        pane.IconScale = 1.0;
        MarkDirty();
    }

    /// <summary>
    /// Combined zoom moves BOTH axes — it was stepping only the font, which
    /// made it identical to FontLarger and meant icons never grew with it.
    /// Icons step further per notch because their range is wider.
    /// </summary>
    [RelayCommand] private void ZoomIn()  => ScalePane(ActiveTab, 0.1, 0.15);
    [RelayCommand] private void ZoomOut() => ScalePane(ActiveTab, -0.1, -0.15);

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
            await ShareFolderAsync(target, writable).ConfigureAwait(true);
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

    // ---- remote mounts ---------------------------------------------------

    private IRemoteMounts? _remotes;

    public void UseRemotes(IRemoteMounts? remotes)
    {
        _remotes = remotes;
        Sidebar.UseRemotes(remotes);
    }

    public bool CanConnect => _remotes?.IsAvailable == true;

    /// <summary>
    /// What the connect prompt should offer, from whatever is doing the
    /// mounting. Hard-coding "smb://" here put a Linux URI in front of Windows
    /// users, whose redirector wants `\\server\share` and cannot mount sftp at
    /// all.
    /// </summary>
    public string ConnectPrefill => _remotes?.AddressPrefill ?? "";

    public string ConnectHint => _remotes?.AddressHint ?? "";

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
    private async Task ShareFolderAsync(string path, bool writable)
    {
        if (_sharing is null) return;

        var session = await _sharing.StartAsync(path, writable, CancellationToken.None)
                                    .ConfigureAwait(true);

        if (ActiveTab is { } pane)
        {
            pane.Status = writable
                ? $"sharing {session.Label} read-write at {session.Url}"
                : $"sharing {session.Label} at {session.Url}";
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

    /// <summary>The view owns the clipboard, so commands just ask.</summary>
    public event EventHandler<string>? CopyTextRequested;

    /// <summary>
    /// F11 toggles the panel on the side that has focus, so the shortcut and
    /// the per-side buttons do the same thing to the same place.
    /// </summary>
    [RelayCommand]
    private void ToggleInfo() => ActiveGroup?.ToggleInfoCommand.Execute(null);

    /// <summary>Hands the provider to both sides; each builds its own panel.</summary>
    public void UseProperties(IPropertiesProvider? properties)
    {
        Left?.UseProperties(properties);
        Right?.UseProperties(properties);
        _properties = properties;
    }

    private IPropertiesProvider? _properties;

    public bool IsSplit => Right is not null;

    /// <summary>The other-pane transfers need both a second pane and something
    /// to send. They were gated on the split alone, so an empty-space right-click
    /// in a split window offered them and they returned on the empty selection.</summary>
    /// <summary>
    /// A second pane, and something real to send. **CanActOnSelection rather
    /// than HasSelection**: a bin row names where a file USED to be, so copying
    /// from one copies whatever occupies that path now — the same hazard the
    /// delete and rename guards exist for, reached through the transfers.
    /// </summary>
    public bool CanTransferToOtherPane => IsSplit && ActiveTab?.CanActOnSelection == true;

    /// <summary>
    /// What this desktop calls the bin, for labels that name it.
    ///
    /// Exposed on the view model rather than reached from markup with x:Static
    /// because <c>InitializeComponent</c> runs before the platform is chosen —
    /// an x:Static reference is resolved as the XAML is parsed and would bake in
    /// the default. A binding is evaluated when the DataContext arrives, which
    /// is after.
    /// </summary>
    public string BinName => Core.Naming.BinName;

    /// <summary>The same inside a sentence: "the Recycle Bin", "the trash".</summary>
    public string TheBin => Core.Naming.TheBin;

    /// <summary>The same starting a label: "Recycle Bin", "Trash".</summary>
    public string BinTitle => Core.Naming.BinTitle;

    /// <summary>
    /// "Empty Recycle Bin…" / "Empty trash…". The ellipsis is a promise that it
    /// asks first, and it stays on both platforms.
    /// </summary>
    public string BinEmptyLabel => $"Empty {BinName}…";

    /// <summary>
    /// "Move to the Recycle Bin" / "Move to the trash".
    ///
    /// The one bin label that was still hardcoded, so the context menu said
    /// "trash" on Windows while the prompt beside it, the settings page and the
    /// sidebar all said Recycle Bin.
    /// </summary>
    public string BinMoveLabel => $"Move to {TheBin}";

    public string BinEmptyHint => $"permanently delete everything in {TheBin}";



    // Hiding a control does not give its column back — an invisible pane in a
    // "*" column still reserves half the window. The definitions themselves
    // have to collapse, so they are driven from here.
    public GridLength LeftColumnWidth
        => new(IsSplit ? Math.Clamp(SplitRatio, 0.1, 0.9) : 1, GridUnitType.Star);

    public GridLength RightColumnWidth
        => IsSplit ? new GridLength(1 - Math.Clamp(SplitRatio, 0.1, 0.9), GridUnitType.Star)
                   : new GridLength(0);

    /// <summary>
    /// The fraction of the content width this group receives. 1 when not split.
    ///
    /// Clamped exactly as the column definitions clamp, so the answer matches
    /// what the layout will actually do rather than what SplitRatio says.
    /// </summary>
    private double ShareOf(object? group)
    {
        if (!IsSplit) return 1.0;

        var ratio = Math.Clamp(SplitRatio, 0.1, 0.9);

        return ReferenceEquals(group, Left) ? ratio : 1 - ratio;
    }

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(LeftColumnWidth));
        OnPropertyChanged(nameof(RightColumnWidth));
    }

    /// <summary>
    /// The active tab of the active side. Everything outside this class —
    /// toolbar, key bindings, context menu — binds through here and never needs
    /// to know whether the window is split.
    /// </summary>
    public PaneViewModel? ActiveTab => ActiveGroup?.ActiveTab;

    /// <summary>
    /// The status line, named with the folder it describes. In a split, a bare
    /// "21 items" does not say which of two identical listings it counted.
    /// </summary>
    /// <summary>Item and selection counts, separate from the transient status
    /// so a passing message never hides them.</summary>
    public string ActiveSummary => ActiveTab?.Summary ?? "";

    /// <summary>
    /// The status line, prefixed in split view with the side it belongs to.
    ///
    /// **Only when there IS a status.** Status is empty almost all the time —
    /// it carries transient messages and is cleared the moment a listing
    /// finishes — so the split branch printed the folder name, an em dash and
    /// nothing after it, permanently, on every split window. The bar read
    /// "8 items · qa —" and had done since split view was built.
    /// </summary>
    public string ActiveStatus
    {
        get
        {
            if (ActiveTab is not { } pane || pane.Status.Length == 0) return "";

            return IsSplit ? $"{pane.Title} — {pane.Status}" : pane.Status;
        }
    }

    /// <summary>The other side, when split. Where "copy to other pane" sends things.</summary>
    public PaneGroupViewModel? OtherGroup
        => Right is null ? null : ReferenceEquals(ActiveGroup, Left) ? Right : Left;

    public event EventHandler<PaneViewModel>? PaneCreated;

    /// <summary>The view owns window creation, so the command just asks.</summary>
    public event EventHandler? PropertiesRequested;

    public event EventHandler? BatchRenameRequested;

    [RelayCommand]
    private void BatchRename() => BatchRenameRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowProperties()
    {
        // **Not in the bin or in Recent.** Both hold rows naming where a file
        // USED to be, so the sheet describes a path that is not there: on
        // Windows that reads as "modified 1601-01-01", size 0. Every neighbouring
        // entry carries this gate; this one was written two hundred lines from
        // the comment explaining why.
        if (ActiveTab is { IsTrashListing: true } or { IsRecentListing: true }) return;

        PropertiesRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether the Properties entry applies here at all.</summary>
    public bool CanShowProperties
        => ActiveTab is not null
           && !ActiveTab.IsTrashListing
           && !ActiveTab.IsRecentListing;

    /// <summary>
    /// Emptying the trash goes through the window, not straight to the store,
    /// because it needs the confirm bar — and the prompt lives in the window,
    /// which is the only thing that owns real buttons. Same arrangement as
    /// properties and settings.
    /// </summary>
    /// <summary>Widen the window by this many pixels, to make room for a panel
    /// that would not otherwise fit.</summary>
    public event EventHandler<double>? GrowRequested;

    /// <summary>Put the window back to the width it had before it was grown.</summary>
    public event EventHandler? ReleaseRequested;

    /// <summary>
    /// Asks the window to bin the selection, so the confirmation setting is
    /// honoured.
    ///
    /// **The context menu used to call TrashSelectedCommand directly**, which
    /// skipped the prompt entirely: with "ask before moving files to the bin"
    /// turned on, the Delete key asked and the identical menu entry did not.
    /// The one path where somebody has explicitly requested a safety net is the
    /// worst place to have two routes with different behaviour.
    ///
    /// An event rather than a command that prompts, because the prompt bar
    /// belongs to the window — the same shape EmptyTrashRequested already uses.
    /// </summary>
    public event EventHandler? TrashSelectionRequested;

    [RelayCommand]
    private void TrashSelection() => TrashSelectionRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? EmptyTrashRequested;

    [RelayCommand]
    private void EmptyTrash() => EmptyTrashRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Status bar visibility, straight off the preferences. Re-raised rather
    /// than stored, so there is one source of truth and no copy to fall out of
    /// step with the file.
    /// </summary>
    public bool ShowStatusBar => Settings.AppSettings.Current.General.ShowStatusBar;

    public bool ShowFreeSpace => Settings.AppSettings.Current.General.ShowFreeSpace;

    // ---- context menu visibility ------------------------------------------
    //
    // Straight off the preferences, like the status bar above. Bound with
    // IsVisible on the MenuItems, which is how Dolphin's Services page works:
    // the commands all still exist and keep their shortcuts, the menu just
    // stops listing them.

    private static Core.Settings.ContextMenuSettings Menu => Settings.AppSettings.Current.ContextMenu;

    // The four that act on a selection carry the preference AND the selection.
    // One menu serves a row and the empty space below it, so without the second
    // half they were listed on an empty-space click and did nothing when picked.
    //
    // ShowAddToPlaces and ShowCopyLocation are deliberately NOT gated: their
    // commands retarget the current folder when nothing is selected, which is a
    // real answer rather than a silent no-op. See AddSelectionToPlaces below.
    public bool ShowCopyToInMenu => Menu.ShowCopyTo && ActiveTab?.CanActOnSelection == true;
    public bool ShowMoveToInMenu => Menu.ShowMoveTo && ActiveTab?.CanActOnSelection == true;
    public bool ShowSortByInMenu => Menu.ShowSortBy;

    public bool ShowDuplicateInMenu => Menu.ShowDuplicate && ActiveTab?.CanActOnSelection == true;
    /// <summary>
    /// Only for a FOLDER. OpenInNewTab opens a directory and quietly does
    /// nothing for anything else, so offering it on a text file was a row that
    /// could only disappoint — and in the bin it named a path the folder no
    /// longer occupies.
    /// </summary>
    public bool ShowOpenInNewTabInMenu =>
        Menu.ShowOpenInNewTab && ActiveTab is { HasDirectorySelected: true, IsTrashListing: false };
    /// <summary>
    /// The files waiting to be moved by a paste, which every row binds to so a
    /// cut one can be greyed the way Explorer greys it.
    ///
    /// Mirrored from <see cref="CutMarks"/> rather than owned here: cut is
    /// raised by a pane, which has no reference to the shell, and the marks
    /// apply to every listing rather than to the one that was cut from.
    /// </summary>
    [ObservableProperty] private IReadOnlySet<string> _cutPaths = CutMarks.Paths;

    public bool ShowAddToPlacesInMenu => Menu.ShowAddToPlaces;

    /// <summary>
    /// The selection row appears only when a FOLDER is selected.
    ///
    /// **Two entries were doing one thing.** Splitting "Add to places" into a
    /// selection row and a current-folder row read well until you noticed that
    /// the selection command falls back to the current folder for anything that
    /// is not a directory — so with nothing selected, or a file selected, both
    /// rows pinned the same path under two different labels, and the one naming
    /// a selection was not acting on it.
    /// </summary>
    public bool ShowAddSelectionToPlaces
        => Menu.ShowAddToPlaces && ActiveTab?.HasDirectorySelected == true;

    /// <summary>
    /// The current-folder row shows only when the selection row does not —
    /// they are one "Add to places" slot in the menu, and which command fills
    /// it depends on whether a folder is selected. Both visible at once was
    /// the old layout's homework: two adjacent rows whose difference the
    /// reader had to work out.
    /// </summary>
    public bool ShowAddCurrentToPlaces
        => Menu.ShowAddToPlaces && ActiveTab?.HasDirectorySelected != true;
    public bool ShowCopyLocationInMenu => Menu.ShowCopyLocation;

    /// <summary>
    /// The selection's path on the clipboard. Reuses CopyTextRequested, which
    /// already exists for share URLs and mount paths — the view owns the
    /// clipboard, so the shell asks rather than reaches.
    /// </summary>
    [RelayCommand]
    private void CopyLocation()
    {
        var path = ActiveTab?.SelectedEntry?.FullPath ?? ActiveTab?.CurrentPath;

        if (!string.IsNullOrEmpty(path)) CopyTextRequested?.Invoke(this, path);
    }

    /// <summary>
    /// Pins the selected folder rather than the current one, which is what a
    /// context menu on a row should mean. Falls back to the current folder when
    /// the click was on empty space.
    /// </summary>
    [RelayCommand]
    private void AddSelectionToPlaces()
    {
        var path = ActiveTab?.SelectedEntry is { IsDirectory: true } entry
            ? entry.FullPath
            : ActiveTab?.CurrentPath;

        if (path is { Length: > 0 }) _ = Sidebar.PinAsync(path);
    }

    /// <summary>
    /// Called when preferences change. Most settings are read at the moment
    /// they matter and so need nothing; sorting is the exception, because a
    /// listing already on screen was ordered under the old rule.
    /// </summary>
    /// <summary>
    /// Keeps the sidebar's highlight on the place the active pane is showing.
    /// The shell is the only thing that knows which pane that is, which is the
    /// same reason it owns the navigation callback.
    /// </summary>
    public void SyncSidebarLocation() => Sidebar.SetCurrentPath(ActiveTab?.CurrentPath);

    public void OnSettingsChanged()
    {
        // The tile and cell metrics are computed from the pane's scale AND the
        // global spacing settings, but only the scale raises a notification.
        // Without this, a spacing change would reach only the panes that
        // happened to rescale afterwards — which is the trap the old
        // application-level filter was trying to avoid, solved at the right end.
        foreach (var group in new[] { Left, Right })
            if (group is not null)
                foreach (var tab in group.Tabs)
                {
                    tab.RefreshScale();
                    tab.RefreshDecorations();
                }

        // The narrow-panel behaviour changes whether the toggle may be pressed,
        // and that is computed rather than stored — so it has to be re-raised or
        // a greyed button stays greyed until the next resize.
        foreach (var group in new[] { Left, Right })
            group?.RefreshInfoFit();

        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(ShowFreeSpace));

        // Free space now prints on the drive rows rather than in the status bar,
        // and those rows are separate objects — raising it here does not reach
        // them, so each one is told directly.
        foreach (var group in Sidebar.Groups)
            foreach (var item in group.Places)
                item.RaiseCapacityVisibilityChanged();

        OnPropertyChanged(nameof(ShowCopyToInMenu));
        OnPropertyChanged(nameof(CanShowProperties));
        OnPropertyChanged(nameof(ShowMoveToInMenu));
        OnPropertyChanged(nameof(ShowSortByInMenu));
        OnPropertyChanged(nameof(ShowDuplicateInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewTabInMenu));
        OnPropertyChanged(nameof(ShowAddToPlacesInMenu));
        OnPropertyChanged(nameof(ShowAddSelectionToPlaces));
        OnPropertyChanged(nameof(ShowAddCurrentToPlaces));
        OnPropertyChanged(nameof(ShowCopyLocationInMenu));

        // Left and Right, not a Groups collection — this view model has no such
        // thing, and inventing one for a loop would be the tail wagging the dog.
        foreach (var group in new[] { Left, Right })
        {
            if (group is null) continue;

            foreach (var tab in group.Tabs)
                tab.RefreshCommand.Execute(null);
        }
    }

    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised so the window can show the shortcut list; a view model
    /// has no business owning a window.</summary>
    public event EventHandler? ShortcutsRequested;

    [RelayCommand]
    private void ShowShortcuts() => ShortcutsRequested?.Invoke(this, EventArgs.Empty);

    public Func<WindowSession>? GeometryProvider { get; set; }

    [ObservableProperty] private string _operationStatus = "";
    [ObservableProperty] private IOperationHandle? _activeOperation;

    /// <summary>
    /// Whether the transfer bar is on screen.
    ///
    /// **The bar used to follow ActiveOperation alone, so every failure was
    /// written and hidden in the same instant.** The completion handler sets
    /// the message — "failed: …", or which files were left behind — and then,
    /// six lines later, clears ActiveOperation because nothing is running any
    /// more. The bar went with it. The comment above that message says "a
    /// failure stays on screen"; it could not.
    ///
    /// So: visible while something is running, and visible while there is
    /// something to say.
    /// </summary>
    public bool ShowOperationBar => ActiveOperation is not null || OperationStatus.Length > 0;

    /// <summary>True only in the after-the-fact state, where there is a message
    /// but nothing left to pause or cancel.</summary>
    public bool OperationFinished => ActiveOperation is null && OperationStatus.Length > 0;

    partial void OnActiveOperationChanged(IOperationHandle? value) => NotifyOperationBar();
    partial void OnOperationStatusChanged(string value) => NotifyOperationBar();

    private void NotifyOperationBar()
    {
        OnPropertyChanged(nameof(ShowOperationBar));
        OnPropertyChanged(nameof(OperationFinished));
    }

    /// <summary>
    /// Puts the last message away. Needed because the bar now outlives the
    /// operation: without it a failure would sit there until the next copy.
    /// </summary>
    [RelayCommand]
    private void DismissOperationStatus() => OperationStatus = "";

    // ---- construction --------------------------------------------------

    private PaneGroupViewModel CreateGroup()
    {
        var group = new PaneGroupViewModel(NewPane);

        group.LocationChanged += (_, _) => SyncSidebarLocation();

        // The details panel's width is persisted, and until it had a working
        // handle nothing could ever change it — so nothing marked the session
        // dirty for it either, and a drag would have been forgotten by the next
        // launch.
        group.LayoutChanged += (_, _) => MarkDirty();

        // Forwarded rather than handled: only the window can change its own
        // width, and the group has no business knowing a window exists.
        //
        // But the ARITHMETIC belongs here, because only the shell knows the
        // window is split. The columns are STAR lengths driven by SplitRatio, so
        // growing the window by the group's shortfall hands that side only its
        // SHARE of the extra — which is why the window grew and the panel still
        // did not appear. Dividing by the share makes one resize enough.
        group.GrowRequested += (sender, needed) =>
            GrowRequested?.Invoke(this, needed / ShareOf(sender));

        // Only give the width back when NEITHER side still needs it. In a split
        // both panels can have grown the window, and restoring on the first close
        // would pull the room out from under the one still open.
        group.ReleaseRequested += (_, _) =>
        {
            if (Left.GrewForPanel || Right is { GrewForPanel: true })
            {
                PaneGroupViewModel.PanelDebug("[vaktari] panel: holding the width — the other side's panel "
                    + "is still open");
                return;
            }

            ReleaseRequested?.Invoke(this, EventArgs.Empty);
        };

        // A split created later must get the provider too, or its panel would
        // silently have nothing to show.
        group.UseProperties(_properties);
        group.PropertyChanged += OnGroupChanged;
        return group;
    }

    private PaneViewModel NewPane()
    {
        // A new tab inherits the sizes of the one it was opened from, rather
        // than snapping back to default mid-session.
        var pane = new PaneViewModel(_fs, _ops, _launcher, _clipboard, _scripts, _templates)
        {
            FontScale = ActiveTab?.FontScale ?? FontScale,
            IconScale = ActiveTab?.IconScale ?? IconScale,
        };

        pane.ScaleChanged += (_, _) =>
        {
            MarkDirty();

            // The boxes in the menu follow the wheel as well as their own
            // typing, or they sit showing a size that is no longer true.
            NotifyTargetSizes();
        };
        pane.OperationStarted += OnOperationStarted;
        pane.PropertyChanged += OnPaneChanged;
        PaneCreated?.Invoke(this, pane);
        return pane;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneGroupViewModel.ActiveTab)) return;

        if (ReferenceEquals(sender, ActiveGroup))
        {
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(ActiveStatus));
            OnPropertyChanged(nameof(ActiveSummary));
            NotifySelectionMenu();
        }

        MarkDirty();
    }

    partial void OnActiveGroupChanged(PaneGroupViewModel? oldValue, PaneGroupViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActiveGroup = false;
        newValue.IsActiveGroup = true;

        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveStatus));
        OnPropertyChanged(nameof(OtherGroup));
        NotifySelectionMenu();

        MarkDirty();
    }

    partial void OnRightChanged(PaneGroupViewModel? value)
    {
        // Closing the split leaves the size chooser pointing at a pane that is
        // no longer there, showing a number that belongs to nothing.
        NotifyTargetSizes();

        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(OtherGroup));
        NotifySelectionMenu();
        SyncWindowControls();
        NotifyColumns();
        MarkDirty();
    }

    /// <summary>
    /// The window's own controls live on one side only: the right, when split,
    /// and the only side otherwise.
    ///
    /// **Three controls, and the plural is deliberate**: the split toggle, the
    /// details-panel toggle and the view-options menu. All three sit on one
    /// side because that is what was asked for — "only show the settings, split
    /// view, and detail panel buttons on the right most window when in split
    /// mode. They are not needed on the left split at all."
    ///
    /// An audit once read the panel toggle's own tooltip — "for this side" —
    /// and concluded the left half was being cut off from the feature, so the
    /// gate came off. It was wrong: F11 toggles whichever side is active, so
    /// the left half keeps the panel and simply does not carry a second copy of
    /// the button. Restored 14 August 2026.
    ///
    /// Driven from here rather than computed in each group, because a group has
    /// no idea whether it is the left or the right of anything — that is the
    /// shell's knowledge, and OnRightChanged is the single point every split
    /// change passes through, including a session restored at startup.
    /// </summary>
    private void SyncWindowControls()
    {
        Left.ShowsWindowControls = Right is null;
        if (Right is { } right) right.ShowsWindowControls = true;
    }

    partial void OnSplitRatioChanged(double value)
    {
        NotifyColumns();
        MarkDirty();
    }

    // ---- split ---------------------------------------------------------

    /// <summary>
    /// F3, matching Dolphin. Opening a split clones the current location so the
    /// second side starts somewhere useful rather than at home.
    /// </summary>
    [RelayCommand]
    public void ToggleSplit()
    {
        if (Right is null)
        {
            var right = CreateGroup();

            // Populated before being assigned, so the column never flashes empty.
            if (_rememberedRight is { Tabs.Count: > 0 } remembered)
                Restore(right, remembered);
            else
                right.AddTab(ActiveTab?.CurrentPath
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            Right = right;
            ActiveGroup = right;
        }
        else
        {
            // Closing the split always keeps the left side, so which half
            // survives is predictable rather than depending on focus.
            var closing = Right;

            // Vaktari's default keeps what the closing side was showing, so
            // reopening the split lands back in place — closing a split should
            // not be a quiet way to lose a location. Dolphin discards it, and
            // people used to that can have it.
            _rememberedRight = Settings.AppSettings.Current.General.ClosingSplitDiscardsOtherPane
                ? null
                : closing.ToPaneState();

            Right = null;
            ActiveGroup = Left;
            closing.DisposeAll();
        }
    }

    /// <summary>Tab, matching Dolphin.</summary>
    [RelayCommand]
    public void FocusOtherPane()
    {
        if (OtherGroup is { } other) ActiveGroup = other;
    }

    [RelayCommand]
    public void CopyToOtherPane() => TransferToOther(move: false);

    [RelayCommand]
    public void MoveToOtherPane() => TransferToOther(move: true);

    /// <summary>
    /// Somewhere to send files that is not the other pane. Built from the same
    /// Places the sidebar shows, so the destinations offered are the ones the
    /// user already keeps — no separate list to maintain, and pinning a folder
    /// makes it a transfer target for free.
    /// </summary>
    /// <summary>
    /// The destination the other half of a split IS, as the first row of the
    /// transfer submenus. It was two more top-level entries — four transfer
    /// rows in a flat run — and folding it here is what let the pair collapse
    /// to two. Routed by its id in TransferTo, since it has no path of its own
    /// until the moment it is used.
    /// </summary>
    internal const string OtherPaneTargetId = "vaktari:other-pane";

    private static readonly PlaceItemViewModel OtherPaneTarget = new(new Place
    {
        Id = OtherPaneTargetId,
        Label = "The other pane",
        Path = "",
        Kind = PlaceKind.Virtual,
        Icon = "",
    });

    public IReadOnlyList<PlaceItemViewModel> TransferTargets
    {
        get
        {
            var targets = Sidebar.Groups
                .SelectMany(g => g.Places)

                // An unmounted volume or unreachable share would look like a valid
                // destination and fail on use.
                .Where(p => p.IsAvailable && !string.IsNullOrEmpty(p.Path))

                // Sending a folder into itself is the one destination that is
                // never meaningful — and neither is sending it into something
                // inside itself, which equality alone allowed. Contains also
                // picks the platform's case rules: two paths differing only in
                // case are one folder on NTFS and two on ext4.
                .Where(p => !PathRules.Contains(SelectedFolderOf(ActiveTab), p.Path)
                            && !PathRules.Same(p.Path, ActiveTab?.CurrentPath))
                .ToList();

            if (CanTransferToOtherPane) targets.Insert(0, OtherPaneTarget);

            return targets;
        }
    }

    private void NotifyTransferTargets() => OnPropertyChanged(nameof(TransferTargets));

    /// <summary>
    /// The menu entries that need something to act on. Computed here rather
    /// than on the pane because the preference and the selection live on
    /// opposite sides, so nothing raises them on its own — and they go stale in
    /// three ways, not one: the selection changes, the active tab changes
    /// underneath them, and the split appears or goes away.
    /// </summary>
    private void NotifySelectionMenu()
    {
        OnPropertyChanged(nameof(ShowCopyToInMenu));
        OnPropertyChanged(nameof(CanShowProperties));
        OnPropertyChanged(nameof(ShowMoveToInMenu));
        OnPropertyChanged(nameof(ShowDuplicateInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewTabInMenu));
        OnPropertyChanged(nameof(ShowAddSelectionToPlaces));
        OnPropertyChanged(nameof(ShowAddCurrentToPlaces));
        OnPropertyChanged(nameof(CanTransferToOtherPane));
    }

    [RelayCommand]
    private void CopySelectionTo(PlaceItemViewModel? place) => TransferTo(place, move: false);

    [RelayCommand]
    private void MoveSelectionTo(PlaceItemViewModel? place) => TransferTo(place, move: true);

    private void TransferTo(PlaceItemViewModel? place, bool move)
    {
        if (place is null || ActiveTab is not { } source) return;

        // The first row of the submenu is not a folder at all.
        if (place.Id == OtherPaneTargetId)
        {
            TransferToOther(move);
            return;
        }

        var paths = SelectionOf(source);
        if (paths.Count == 0) { source.Status = "nothing selected"; return; }

        if (!Directory.Exists(place.Path))
        {
            source.Status = $"{place.Label} is not reachable";
            return;
        }

        // Routed through a pane already showing the destination when there is
        // one, so its listing refreshes itself; otherwise through the same
        // helper, which keeps the conflict policy in exactly one place.
        var open = new[] { Left, Right }
            .Where(g => g is not null)
            .SelectMany(g => g!.Tabs)
            .FirstOrDefault(t => PathRules.Same(t.CurrentPath, place.Path));

        if (open is not null) open.PasteInto(paths, move);
        else source.PasteIntoFolder(place.Path, paths, move);

        source.Status = move
            ? $"moving {paths.Count} item(s) to {place.Label}"
            : $"copying {paths.Count} item(s) to {place.Label}";
    }

    private static List<string> SelectionOf(PaneViewModel pane)
        => pane.SelectionPaths().ToList();

    private void TransferToOther(bool move)
    {
        if (_ops is null || OtherGroup?.ActiveTab is not { } target) return;
        if (ActiveTab is not { } source) return;

        var paths = SelectionOf(source);
        if (paths.Count == 0) return;

        // **The one route with no containment check at all.** Sending a folder
        // to the other pane while that pane is showing somewhere inside it
        // copies the folder into its own subtree.
        if (paths.Any(p => PathRules.Contains(p, target.CurrentPath)))
        {
            source.Status = "that folder cannot be sent into itself";
            return;
        }

        target.PasteInto(paths, move);
    }

    // ---- tabs ----------------------------------------------------------

    [RelayCommand]
    private void NewTab()
        => ActiveGroup.AddTab(ActiveTab?.CurrentPath
                              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    private void OpenInNewTab(FileEntry? entry)
    {
        // Nullable because the CommandParameter binds to the selected entry and
        // one menu serves both a row and the empty space below it — on an
        // empty-space click it resolves to null. FileEntry is a struct, so a
        // RelayCommand<FileEntry> could not accept that and threw
        // ArgumentException from the menu rather than doing nothing; taking
        // FileEntry? is what lets the command be handed the empty case at all.
        if (entry is { IsDirectory: true } folder) ActiveGroup.AddTab(folder.FullPath);
    }

    /// <summary>
    /// Opens a folder by path — used when the desktop hands one over, either on
    /// the command line or from a later launch forwarded to this instance.
    ///
    /// Reuses the current tab when it is already showing that folder, so
    /// repeatedly opening the same place from elsewhere does not stack up
    /// identical tabs.
    /// </summary>
    public void OpenInNewTab(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // PathRules.Same, like the two other places in this file that ask the
        // same question. An ordinal compare opens a second tab on the same
        // folder for a difference of case or a trailing separator - and its
        // doc comment names duplicate-tab detection as the reason it exists.
        var existing = ActiveGroup.Tabs.FirstOrDefault(
            t => Core.FileSystem.PathRules.Same(t.CurrentPath, path));

        if (existing is not null)
        {
            ActiveGroup.ActiveTab = existing;
            return;
        }

        ActiveGroup.AddTab(path);
    }

    [RelayCommand]
    private void CloseTab(PaneViewModel? pane)
    {
        // Closing the last tab of the right side collapses the split rather
        // than refusing, which is what the user actually means.
        if (Right is not null && ActiveGroup.Tabs.Count <= 1 &&
            (pane is null || ActiveGroup.Tabs.Contains(pane)))
        {
            if (ReferenceEquals(ActiveGroup, Right)) { ToggleSplit(); return; }

            // Closing the last left tab: promote the right side to be the only one.
            var survivor = Right;
            Left.DisposeAll();
            Left = survivor;
            Right = null;
            ActiveGroup = Left;
            OnPropertyChanged(nameof(Left));
            return;
        }

        // **The last tab closes the window**, which is what Ctrl+W does in
        // Explorer and in every browser. It used to do nothing at all: the
        // group refuses to leave a side with no tabs, so with one tab and no
        // split both Ctrl+W and the tab's × were drawn, clickable, tooltipped
        // and inert — and there was no Ctrl+Q either, so the keyboard could not
        // close the window at all.
        if (Right is null && ActiveGroup.Tabs.Count <= 1
            && (pane is null || ActiveGroup.Tabs.Contains(pane)))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ActiveGroup.CloseTab(pane);
    }

    /// <summary>
    /// Asks the window to close. Raised rather than acted on: the shell owns no
    /// window, and the session is saved by whoever does.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>Ctrl+Q, which had no binding anywhere.</summary>
    [RelayCommand]
    private void Quit() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand] private void NextTab() => ActiveGroup.Cycle(1);
    [RelayCommand] private void PreviousTab() => ActiveGroup.Cycle(-1);
    [RelayCommand] private void CancelOperation() => ActiveOperation?.Cancel();

    /// <summary>
    /// Pauses or resumes the running operation.
    ///
    /// **Pause was fully implemented and unreachable.** OperationHandle has a
    /// real gate, and BOTH engines await it between items and inside the byte
    /// loop — so the machinery for stopping a large copy mid-flight has always
    /// worked and nothing in the application could ask for it. The interface's
    /// own comment justifies handles existing on the grounds that "pause and
    /// reorder cannot be retrofitted onto a Task", which was true and was the
    /// reason a feature nobody could use had been paid for in full.
    /// </summary>
    [RelayCommand]
    private void PauseOperation()
    {
        if (ActiveOperation is not { } operation) return;

        if (operation.State == OperationState.Paused) operation.Resume();
        else operation.Pause();

        OnPropertyChanged(nameof(PauseLabel));
    }

    /// <summary>One button, two words — the state it is in decides which.</summary>
    public string PauseLabel
        => ActiveOperation?.State == OperationState.Paused ? "resume" : "pause";

    public void SelectTabByIndex(int index) => ActiveGroup.SelectTabByIndex(index);

    public void ActivateGroup(PaneGroupViewModel group)
    {
        if (!ReferenceEquals(ActiveGroup, group)) ActiveGroup = group;
    }

    // ---- places --------------------------------------------------------

    [RelayCommand]
    private void GoToPlace(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _ = ActiveTab?.NavigateAsync(path);
    }

    [RelayCommand]
    private void PinCurrent()
    {
        if (ActiveTab?.CurrentPath is { Length: > 0 } path) _ = Sidebar.PinAsync(path);
    }

    /// <summary>
    /// The way back out, which did not exist: places could be added from two
    /// places and removed from none.
    ///
    /// No confirmation. This removes a shortcut and never a folder — the same
    /// bargain as Recent's "Forget (keeps the file)", which also asks nothing —
    /// and putting it back is the same Ctrl+D that created it.
    /// </summary>
    [RelayCommand]
    private void RemovePlace(PlaceItemViewModel? place)
    {
        if (place is { IsUserPinned: true }) _ = Sidebar.UnpinAsync(place.Id);
    }

    // ---- what a sidebar row's menu offers everybody ------------------------
    //
    // **Right-clicking Home, Documents, a drive, a mapped drive or the bin
    // opened nothing at all.** The menu held two entries, both of which apply
    // to almost no rows — Remove to the ones the user pinned, Eject to the ones
    // that can be ejected — and the Opening handler cancelled the popup for
    // everything else rather than show a sliver of empty menu. Correct for a
    // menu with nothing in it, and the wrong fix: both references put Open in
    // new tab and Properties on every node of the navigation pane.

    [RelayCommand]
    private void OpenPlaceInNewTab(PlaceItemViewModel? place)
    {
        if (place is { Path.Length: > 0 }) OpenInNewTab(place.Path);
    }

    [RelayCommand]
    private void CopyPlacePath(PlaceItemViewModel? place)
    {
        if (place is { HasRealPath: true }) CopyTextRequested?.Invoke(this, place.Path);
    }

    /// <summary>The desktop's own properties dialog, the same one the listing
    /// menu opens for a row.</summary>
    [RelayCommand]
    private void ShowPlaceProperties(PlaceItemViewModel? place)
    {
        if (place is { HasRealPath: true }) ShowPropertiesRequested?.Invoke(this, place.Path);
    }

    /// <summary>Raised so the window can put up the platform properties dialog;
    /// a view model has no business owning one.</summary>
    public event EventHandler<string>? ShowPropertiesRequested;

    /// <summary>
    /// Safely removes a drive, after getting out of its way.
    ///
    /// **Step one is the difference between this working and never working.**
    /// Vaktari holds the volume open itself: a pane showing a folder on the
    /// drive keeps a live directory watch on it, which is an outstanding handle
    /// like any other. Ejecting the drive somebody is looking at — overwhelmingly
    /// the common case, since looking at it is why they want it back — would
    /// fail every single time, and the veto would blame a program the user
    /// cannot find, because the program is us.
    /// </summary>
    [RelayCommand]
    private async Task EjectPlaceAsync(PlaceItemViewModel? place)
    {
        if (place is not { CanEject: true, IsEjecting: false }) return;

        // Captured before the first await: an eject takes seconds, a tab can be
        // switched inside them, and the answer must land where it was asked for
        // — the same reason DisconnectRemoteAsync captures it.
        var pane = ActiveTab;

        // Every tab in both panes, not just the active one: a background tab
        // holds its directory watch open exactly like a visible one does, and
        // an unseen tab vetoing the eject is the least explicable failure of
        // the lot. Right is null when the window is not split.
        foreach (var group in new[] { Left, Right }.OfType<PaneGroupViewModel>())
        {
            foreach (var tab in group.Tabs.ToList())
            {
                if (!IsUnder(tab.CurrentPath, place.Path)) continue;

                await tab.NavigateAsync(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    .ConfigureAwait(true);
            }
        }

        // After the navigation, never before: a finished listing clears the
        // status line, so a message set first would be erased by the very
        // navigation this just triggered.
        if (pane is not null) pane.Status = $"ejecting {place.Label}…";

        try
        {
            var result = await Sidebar.EjectAsync(place.Id).ConfigureAwait(true);

            if (pane is not null) pane.Status = result.Message;
        }
        catch (Exception ex)
        {
            if (pane is not null)
                pane.Status = Core.FileSystem.Failures.Describe(ex, $"eject {place.Label}");
        }
    }

    /// <summary>
    /// What to say when a batch finished with items left behind.
    ///
    /// **Names the file when there is one to name.** "3 items could not be
    /// copied" sends someone hunting; the first name plus a count tells them
    /// where to start. The reason comes from the same register every other
    /// failure in this window uses, so "the file is open in another program"
    /// rather than an exception type.
    /// </summary>
    internal static string DescribeProblems(IReadOnlyList<Core.FileSystem.ItemProblem> problems)
    {
        var first = problems[0];
        var name = Path.GetFileName(first.Path.TrimEnd(Path.DirectorySeparatorChar));
        var why = Core.FileSystem.Failures.Describe(first.Error, "copy that");

        return problems.Count == 1
            ? $"{name} was left behind — {why}"
            : $"{name} and {problems.Count - 1} more were left behind — {why}";
    }

    /// <summary>
    /// The folder currently selected in a pane, when one is — which is what a
    /// transfer destination must not be inside.
    /// </summary>
    private static string? SelectedFolderOf(PaneViewModel? pane)
        => pane?.SelectedEntry is { IsDirectory: true } folder ? folder.FullPath : null;

    /// <summary>Whether a pane is looking at the drive, or anywhere inside it.</summary>
    private static bool IsUnder(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (Core.FileSystem.PathRules.Same(path, root)) return true;

        var prefix = Core.FileSystem.PathRules.Normalise(root);
        var full = Core.FileSystem.PathRules.Normalise(path);

        if (!full.StartsWith(prefix, Core.FileSystem.PathRules.Comparison)) return false;

        // The prefix has to end at a separator, or "E:\" would claim "E:\..."
        // correctly but "/media/one" would also claim "/media/onetwo".
        return prefix.EndsWith(Path.DirectorySeparatorChar)
               || (full.Length > prefix.Length && full[prefix.Length] == Path.DirectorySeparatorChar);
    }

    // ---- operations ----------------------------------------------------

    /// <summary>
    /// Everything currently running, newest last.
    ///
    /// **One slot was not enough, and the second operation took it.** Nothing
    /// serialises these: each Copy, Move or Trash starts its own handle
    /// immediately, the conflict prompt is an inline bar rather than a modal so
    /// the window stays usable, and every pane and tab reports into the same
    /// shell. Paste a large folder and then press Delete on something else: the
    /// trash finishes in milliseconds, its completion cleared ActiveOperation
    /// without checking whether it still owned it, and the transfer bar vanished
    /// while the copy was still running - taking Cancel with it, since
    /// CancelOperation only ever reached ActiveOperation.
    /// </summary>
    private readonly List<IOperationHandle> _running = [];

    private void OnOperationStarted(object? sender, IOperationHandle handle)
    {
        _running.Add(handle);
        ActiveOperation = handle;

        handle.Progressed += (_, p) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Only the operation the bar is showing may write to it, or a
                // quick background one overwrites the foreground one's numbers.
                if (!ReferenceEquals(ActiveOperation, handle)) return;

                OperationStatus = p.ItemsTotal <= 1 && p.BytesTotal == 0
                    ? p.CurrentItem ?? ""
                    : $"{p.ItemsDone}/{p.ItemsTotal}  {ByteSize.Format(p.BytesDone)}/{ByteSize.Format(p.BytesTotal)}  {p.CurrentItem}";
            });

        _ = handle.Completion.ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _running.Remove(handle);

                // A failure stays on screen; only success clears silently.
                if (handle.State == OperationState.Failed && handle.Error is { } error)
                {
                    // Described the way the rest of the application describes a
                    // failure, rather than handing back a .NET exception message.
                    OperationStatus = "failed: " + Core.FileSystem.Failures.Describe(error, "finish that");
                }
                else if (handle.Problems.Count > 0)
                {
                    // **A batch that finished with items left behind must say
                    // so.** The rest of the files really did arrive, so this is
                    // not a failure — but clearing the line would report a clean
                    // run, and the whole point of carrying on past a locked file
                    // is that the person learns which ones were skipped.
                    OperationStatus = DescribeProblems(handle.Problems);
                }
                else if (ReferenceEquals(ActiveOperation, handle))
                {
                    OperationStatus = "";
                }

                // The bar follows whatever is still going, and only empties when
                // nothing is.
                if (ReferenceEquals(ActiveOperation, handle))
                    ActiveOperation = _running.Count > 0 ? _running[^1] : null;
            }), TaskScheduler.Default);
    }

    // ---- session -------------------------------------------------------

    /// <summary>
    /// <paramref name="state"/> null means "do not restore" — the caller has
    /// already applied the startup setting and decided the session should be
    /// ignored. <paramref name="openFolder"/> is where to start instead; null
    /// means home, which is what this always did.
    ///
    /// The decision lives in the caller rather than here because the caller is
    /// the only place that holds both stores, and because a view model that
    /// reaches for preferences to decide whether to use its own argument is
    /// harder to reason about than one that is simply told.
    /// </summary>
    public void Start(SessionState? state, string? openFolder = null)
    {
        if (_started) return;
        _started = true;

        var home = string.IsNullOrWhiteSpace(openFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : openFolder;
        var window = state?.Windows.FirstOrDefault();

        if (window is not null)
        {
            Sidebar.Width = window.SidebarWidth;
            Sidebar.Rail = window.Rail;
            SplitRatio = window.SplitRatio;
            FontScale = window.FontScale <= 0 ? 1.0 : window.FontScale;
            IconScale = window.IconScale <= 0 ? 1.0 : window.IconScale;
        }

        _ = Sidebar.InitializeAsync();

        var panes = window?.Panes;

        _restoring = true;
        try
        {
            if (panes is null || panes.Count == 0 || panes[0].Tabs.Count == 0)
            {
                Left.AddTab(home);
            }
            else
            {
                Restore(Left, panes[0]);

                if (panes.Count > 1 && panes[1].Tabs.Count > 0)
                {
                    var right = CreateGroup();
                    Restore(right, panes[1]);
                    Right = right;
                }
                else
                {
                    // Split was closed at save time; keep what it was showing
                    // so reopening after a restart lands back in place.
                    _rememberedRight = window?.RememberedRightPane;
                }
            }

            var activeIndex = window?.ActivePaneIndex ?? 0;
            ActiveGroup = activeIndex == 1 && Right is not null ? Right : Left;
        }
        finally
        {
            _restoring = false;
        }

        // Assigned while suppressed, so it never triggered its own load.
        ActiveGroup.ActiveTab?.RefreshIfUnloaded();
    }

    private static void Restore(PaneGroupViewModel group, PaneState state)
    {
        group.RestoreFrom(state);

        foreach (var tab in state.Tabs) group.AddRestoredTab(tab);
        group.ActiveTab = group.Tabs[Math.Clamp(state.ActiveTabIndex, 0, group.Tabs.Count - 1)];
    }

    public SessionState ToSessionState()
    {
        var geometry = GeometryProvider?.Invoke() ?? new WindowSession();

        var panes = Right is null
            ? new List<PaneState> { Left.ToPaneState() }
            : [Left.ToPaneState(), Right.ToPaneState()];

        return new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows =
            [
                geometry with
                {
                    SidebarWidth = Sidebar.Width,
                    Rail = Sidebar.Rail,
                    SplitRatio = SplitRatio,
                    FontScale = FontScale,
                    IconScale = IconScale,
                    RememberedRightPane = Right is null ? _rememberedRight : null,
                    Panes = panes,
                    ActivePaneIndex = ReferenceEquals(ActiveGroup, Right) ? 1 : 0,
                }
            ],
        };
    }

    public void NotifyWindowChanged() => MarkDirty();

    private void MarkDirty()
    {
        // Nothing before Start() is worth saving, and property setters fire
        // during construction while Sidebar and the groups are still null.
        if (!_started || _restoring || _store is null) return;
        _store.NotifyChanged(ToSessionState());
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.CurrentPath):
                // The destination list excludes the folder you are already in,
                // so it changes whenever the pane navigates.
                NotifyTransferTargets();
                MarkDirty();
                break;

            case nameof(PaneViewModel.Selection):
                NotifySelectionMenu();
                break;

            case nameof(PaneViewModel.Sort):
            case nameof(PaneViewModel.SortDescending):
            case nameof(PaneViewModel.ShowHidden):
            // The column choice is per tab and lives in the session. A property
            // that ToTabState writes but this switch does not list only
            // persists when something else happens to change first.
            case nameof(PaneViewModel.HideSizeColumn):
            case nameof(PaneViewModel.HideModifiedColumn):
            case nameof(PaneViewModel.ShowTypeColumn):
                MarkDirty();
                break;

            case nameof(PaneViewModel.Status):
            case nameof(PaneViewModel.Title):
                OnPropertyChanged(nameof(ActiveStatus));
                break;


            case nameof(PaneViewModel.Summary):
                OnPropertyChanged(nameof(ActiveSummary));

                break;
        }
    }
}
