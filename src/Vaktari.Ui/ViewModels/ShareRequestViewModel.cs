using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Choosing a folder to serve, when you did not get here by right-clicking one.
///
/// Both ways in matter: typing is faster when you know the path, and browsing
/// is the only option when you do not. Neither is a good enough default to be
/// the only one.
/// </summary>
public sealed partial class ShareRequestViewModel : ObservableObject
{
    private readonly Func<string, ShareOptions, Task> _share;

    public ShareRequestViewModel(string startingPath, Func<string, ShareOptions, Task> share)
    {
        _share = share;
        _path = startingPath;

        Refresh();
    }

    /// <summary>
    /// Subfolders of the current path.
    ///
    /// Browsing is done here rather than through the platform folder picker for
    /// two reasons. Avalonia's picker is documented to fail or hang when opened
    /// from a window shown with ShowDialog on Linux (AvaloniaUI/Avalonia#10998
    /// and #6589) — which is exactly this situation. And more to the point,
    /// this is a file manager: listing folders is the one thing it definitely
    /// knows how to do, so borrowing someone else's browser for it was the
    /// wrong instinct even before it broke.
    /// </summary>
    public ObservableCollection<string> Folders { get; } = new();

    public bool CanGoUp => PathRules.Parent(Path.Trim()) is not null;

    private void Refresh()
    {
        Folders.Clear();

        var current = Path.Trim();
        if (!Directory.Exists(current)) return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(current)
                         .Select(System.IO.Path.GetFileName)
                         .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith('.'))
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Folders.Add(directory!);
        }
        catch (Exception ex)
        {
            Status = ex is UnauthorizedAccessException
                ? "no permission to list that folder"
                : ex.Message;
        }

        OnPropertyChanged(nameof(CanGoUp));
    }

    [RelayCommand]
    private void Enter(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;

        Path = System.IO.Path.Combine(Path.Trim(), name);
    }

    [RelayCommand]
    private void GoUp()
    {
        // PathRules.Parent, not Directory.GetParent: the latter returns a
        // DirectoryInfo whose FullName touches the current working directory for
        // a relative path, and this dialog only ever asks a question about the
        // shape of the string it holds.
        if (PathRules.Parent(Path.Trim()) is { } parent) Path = parent;
    }

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _writable;

    /// <summary>Off unless ticked: announcing puts the share by name into
    /// every file manager on the network, which is the opposite of handing
    /// one person an address.</summary>
    [ObservableProperty] private bool _announce;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanShare));
        Status = "";
        Refresh();
    }

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanShare));

    /// <summary>
    /// Checked here rather than on submit, so a wrong path is visible while
    /// typing instead of becoming an error afterwards.
    /// </summary>
    public bool CanShare => !Busy
                            && !string.IsNullOrWhiteSpace(Path)
                            && Directory.Exists(Path.Trim());

    public event EventHandler? Finished;

    [RelayCommand]
    private async Task ShareAsync()
    {
        if (!CanShare) return;

        Busy = true;
        Status = "starting…";

        try
        {
            await _share(Path.Trim(), new ShareOptions(Writable, Announce)).ConfigureAwait(true);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);
}
