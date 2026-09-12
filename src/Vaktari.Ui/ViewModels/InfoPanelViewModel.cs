using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The docked details view: a preview and the facts about whatever is selected.
///
/// Reuses <see cref="IPropertiesProvider"/> rather than gathering its own
/// details, so the panel and the properties window can never disagree about the
/// same file. The difference between them is presentation and lifetime, not
/// content — this one follows the selection, that one is a snapshot you opened.
/// </summary>
public sealed partial class InfoPanelViewModel : ObservableObject
{
    private readonly IPropertiesProvider? _properties;

    /// <summary>
    /// Rises with each request. A slow stat for a file you have already
    /// scrolled past must not overwrite the details of the one now selected —
    /// and selection changes far faster than the filesystem answers.
    /// </summary>
    private int _generation;

    public InfoPanelViewModel(IPropertiesProvider? properties)
    {
        _properties = properties;
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _previewPath = "";

    /// <summary>
    /// True for a folder, which has no thumbnail. The panel shows its icon
    /// instead of an empty frame — a blank box reads as "still loading".
    /// </summary>
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _summary = "nothing selected";

    public ObservableCollection<PropertyRow> Rows { get; } = new();

    /// <summary>
    /// Several files selected: report the aggregate rather than one of them.
    /// Picking an arbitrary member would look like a bug.
    /// </summary>
    public void ShowMany(int count, long bytes)
    {
        _generation++;

        HasSelection = false;
        IsFolder = false;
        PreviewPath = "";
        Name = $"{count:N0} items selected";
        Kind = "";
        Summary = bytes > 0 ? ByteSize.Format(bytes) : "";
        Rows.Clear();
    }

    public void ShowNothing()
    {
        _generation++;

        HasSelection = false;
        IsFolder = false;
        PreviewPath = "";
        Name = "";
        Kind = "";
        Summary = "nothing selected";
        Rows.Clear();
    }

    public async Task ShowAsync(FileEntry entry)
    {
        var token = ++_generation;

        // Fill from what the listing already knows, immediately. The panel
        // should never be blank while a stat is in flight.
        HasSelection = true;
        IsFolder = entry.IsDirectory;
        Name = entry.Name;
        Kind = entry.IsDirectory ? "Folder" : "";
        PreviewPath = entry.IsDirectory ? "" : entry.FullPath;
        // A folder has no size to show until something measures it; a measured
        // row arrives with one, and blanking it here left the panel emptier
        // than the listing it was describing.
        Summary = entry.IsDirectory && !entry.IsMeasured ? "" : ByteSize.Format(entry.Length);
        Rows.Clear();

        if (_properties is null) return;

        try
        {
            var details = await _properties.GetAsync(entry.FullPath, CancellationToken.None)
                                           .ConfigureAwait(true);

            // Checked after the await, not before: the selection may have moved
            // on while this was running.
            if (token != _generation) return;

            Kind = details.Kind;
            if (!details.IsDirectory) Summary = ByteSize.Format(details.Size);

            Add("Modified", details.Modified?.LocalDateTime.ToString("dd MMM yyyy  HH:mm"));
            Add("Created", details.Created?.LocalDateTime.ToString("dd MMM yyyy  HH:mm"));
            Add("Links to", details.SymlinkTarget);

            // Only the first group: the panel is a glance, and the properties
            // window is where the whole story lives.
            foreach (var row in details.Groups.FirstOrDefault()?.Rows ?? [])
            {
                if (token != _generation) return;
                Rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            if (token == _generation) Summary = ex.Message;
        }
    }

    private void Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Rows.Add(new PropertyRow(label, value));
    }

}
