using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>A single access flag, made two-way bindable for a checkbox.</summary>
public sealed partial class AccessToggleViewModel : ObservableObject
{
    private readonly AccessToggle _model;

    public AccessToggleViewModel(AccessToggle model)
    {
        _model = model;
        _value = model.Value;
    }

    public string Key => _model.Key;
    public string Group => _model.Group;
    public string Label => _model.Label;

    [ObservableProperty] private bool _value;

    public AccessToggle ToModel() => _model with { Value = Value };
}

public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly IPropertiesProvider _provider;
    private readonly IReadOnlyList<string> _paths;
    private CancellationTokenSource? _measureCts;

    private readonly IAccessEditor? _access;

    public PropertiesViewModel(
        IPropertiesProvider provider, IReadOnlyList<string> paths, IAccessEditor? access = null)
    {
        _provider = provider;
        _paths = paths;
        _access = access;
    }

    // ---- permissions ---------------------------------------------------

    public ObservableCollection<AccessToggleViewModel> Access { get; } = new();

    [ObservableProperty] private bool _canEditAccess;
    [ObservableProperty] private bool _canRecurse;
    [ObservableProperty] private bool _applyRecursively;
    [ObservableProperty] private string _accessSummary = "";
    [ObservableProperty] private string _accessStatus = "";

    private async Task LoadAccessAsync(string path, bool isDirectory)
    {
        if (_access is not { CanEdit: true }) return;

        var state = await _access.GetAccessAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (state is null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Access.Clear();
            foreach (var toggle in state.Toggles) Access.Add(new AccessToggleViewModel(toggle));

            AccessSummary = state.Summary;
            CanEditAccess = true;
            CanRecurse = isDirectory;
        });
    }

    [RelayCommand]
    private async Task ApplyAccessAsync()
    {
        if (_access is null || _paths.Count == 0) return;

        AccessStatus = "applying…";

        var toggles = Access.Select(a => a.ToModel()).ToList();
        var progress = new Progress<int>(done => AccessStatus = $"{done:N0} entries…");

        try
        {
            var skipped = 0;
            Exception? first = null;

            foreach (var path in _paths)
            {
                var outcome = await _access.SetAccessAsync(
                    path, toggles, ApplyRecursively, progress, CancellationToken.None)
                    .ConfigureAwait(false);

                skipped += outcome.Skipped;
                first ??= outcome.FirstFailure;
            }

            // Read back rather than trusting what we sent — the filesystem may
            // have refused part of it, and showing the request as if it were
            // the result would be a lie.
            await LoadAccessAsync(_paths[0], CanRecurse).ConfigureAwait(false);

            // **"applied" only when it was.** A recursive apply skips whatever
            // it cannot write, and saying so is the whole point of a
            // permissions dialog: a tree where every child belongs to another
            // user used to look exactly like one where the change took.
            await Dispatcher.UIThread.InvokeAsync(() =>
                AccessStatus = skipped == 0
                    ? "applied"
                    : first is null
                        ? $"applied, but {skipped:N0} item(s) would not change"
                        : $"applied, but {skipped:N0} item(s) would not change — "
                          + Vaktari.Core.FileSystem.Failures.Describe(first, "change all of those"));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                AccessStatus = Vaktari.Core.FileSystem.Failures.Describe(ex, "read that"));
        }
    }

    public ObservableCollection<PropertyGroup> Groups { get; } = new();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _canMeasure;
    [ObservableProperty] private bool _isMeasuring;

    // ---- checksums -------------------------------------------------------
    //
    // A section rather than a tab: this window is a flat run of sections and
    // adding a TabControl for one more would restructure it for no gain.
    //
    // Opt-in, like measuring a folder. Hashing a multi-gigabyte file is real
    // work, and doing it automatically to every file whose properties you open
    // would make the window slow for the common case in service of the rare one.

    private CancellationTokenSource? _hashCts;

    /// <summary>
    /// Stops whatever is still running when the window goes.
    ///
    /// A folder measurement walks the whole tree and a checksum reads the whole
    /// file, and both carried on after the dialog closed — nothing lands
    /// anywhere wrong, but a gigabyte of disk reads for a window nobody can see
    /// is work with no beneficiary, and on a laptop it is battery.
    /// </summary>
    public void CancelBackgroundWork()
    {
        try { _measureCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _hashCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    [ObservableProperty] private bool _canChecksum;
    [ObservableProperty] private bool _isHashing;
    [ObservableProperty] private string _hashStatus = "";
    [ObservableProperty] private string _md5 = "";
    [ObservableProperty] private string _sha1 = "";
    [ObservableProperty] private string _sha256 = "";

    public bool HasChecksums => Md5.Length > 0;

    partial void OnMd5Changed(string value) => OnPropertyChanged(nameof(HasChecksums));

    /// <summary>A real bool: binding a string's Length straight to IsVisible
    /// does not convert under compiled bindings.</summary>
    public bool HasHashStatus => HashStatus.Length > 0;

    partial void OnHashStatusChanged(string value) => OnPropertyChanged(nameof(HasHashStatus));

    /// <summary>Label doubles as the cancel affordance, as Measure does.</summary>
    public string ChecksumButtonText => IsHashing ? "stop" : "compute";

    partial void OnIsHashingChanged(bool value)
        => OnPropertyChanged(nameof(ChecksumButtonText));

    [RelayCommand]
    private async Task ComputeChecksumsAsync()
    {
        if (IsHashing)
        {
            _hashCts?.Cancel();
            return;
        }

        if (_paths.Count != 1 || !File.Exists(_paths[0])) return;

        _hashCts?.Dispose();
        _hashCts = new CancellationTokenSource();
        var ct = _hashCts.Token;

        IsHashing = true;
        HashStatus = "reading…";
        Md5 = Sha1 = Sha256 = "";

        var progress = new Progress<double>(fraction =>
            HashStatus = $"{fraction:P0}");

        try
        {
            var result = await Checksums.ComputeAsync(_paths[0], progress, ct)
                                        .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Md5 = result.Md5;
                Sha1 = result.Sha1;
                Sha256 = result.Sha256;
                HashStatus = "";
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => HashStatus = "cancelled");
        }
        catch (Exception ex)
        {
            // Unreadable, vanished, or a permission problem — said out loud
            // rather than leaving three empty rows and no explanation.
            await Dispatcher.UIThread.InvokeAsync(() =>
                HashStatus = Vaktari.Core.FileSystem.Failures.Describe(ex, "read that"));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsHashing = false);
        }
    }

    public async Task LoadAsync()
    {
        if (_paths.Count == 0) return;

        if (_paths.Count > 1)
        {
            await LoadManyAsync().ConfigureAwait(false);
            return;
        }

        var details = await _provider.GetAsync(_paths[0], CancellationToken.None)
                                     .ConfigureAwait(false);

        await LoadAccessAsync(_paths[0], details.IsDirectory).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = details.Name;
            Location = Path.GetDirectoryName(details.FullPath) ?? details.FullPath;
            Kind = details.Kind;
            SizeText = details.IsDirectory ? "not measured" : ByteSize.Format(details.Size);
            CanMeasure = details.IsDirectory;

            // Only ever for one actual file: a folder has no digest, and
            // hashing a multi-selection would be three answers to a question
            // nobody asked in the singular.
            CanChecksum = !details.IsDirectory && _paths.Count == 1;

            var general = new List<PropertyRow>();

            if (details.SymlinkTarget is { } target)
                general.Add(new PropertyRow("symlink to", target));

            if (details.Modified is { } modified)
                general.Add(new PropertyRow("modified", modified.ToString("yyyy-MM-dd HH:mm:ss")));
            if (details.Accessed is { } accessed)
                general.Add(new PropertyRow("accessed", accessed.ToString("yyyy-MM-dd HH:mm:ss")));
            if (details.Created is { } created)
                general.Add(new PropertyRow("created", created.ToString("yyyy-MM-dd HH:mm:ss")));

            Groups.Clear();
            if (general.Count > 0) Groups.Add(new PropertyGroup("general", general));
            foreach (var group in details.Groups) Groups.Add(group);
        });
    }

    private async Task LoadManyAsync()
    {
        long total = 0;
        var files = 0;
        var folders = 0;

        foreach (var path in _paths)
        {
            if (Directory.Exists(path)) folders++;
            else if (File.Exists(path)) { files++; total += new FileInfo(path).Length; }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = $"{_paths.Count} items";
            Location = Path.GetDirectoryName(_paths[0]) ?? "";
            Kind = "multiple selection";

            // Folders are counted but not walked — measuring is opt-in here too.
            SizeText = folders > 0
                ? $"{ByteSize.Format(total)} in {files} file(s), plus {folders} folder(s) unmeasured"
                : $"{ByteSize.Format(total)} in {files} file(s)";

            CanMeasure = folders > 0;
        });
    }

    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            _measureCts?.Cancel();
            return;
        }

        _measureCts?.Dispose();
        _measureCts = new CancellationTokenSource();
        var ct = _measureCts.Token;

        IsMeasuring = true;

        var progress = new Progress<SizeProgress>(p =>
            SizeText = $"{ByteSize.Format(p.Bytes)} · {p.Files:N0} files · {p.Folders:N0} folders…");

        try
        {
            long bytes = 0;
            var files = 0;
            var folders = 0;

            foreach (var path in _paths.Where(Directory.Exists))
            {
                var result = await _provider.MeasureAsync(path, progress, ct).ConfigureAwait(false);
                bytes += result.Bytes;
                files += result.Files;
                folders += result.Folders;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                SizeText = $"{ByteSize.Format(bytes)} · {files:N0} files · {folders:N0} folders");
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SizeText += " (cancelled)");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsMeasuring = false);
        }
    }

}
