using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Batch rename. The preview is the plan — what the list shows is exactly what
/// Apply performs, because both come from the same <see cref="BatchRename.Plan"/>
/// call rather than being computed twice.
/// </summary>
public sealed partial class BatchRenameViewModel : ObservableObject
{
    private readonly IReadOnlyList<FileEntry> _entries;
    private readonly Func<FileEntry, string, Task> _rename;

    /// <summary>The whole folder, so the preview can see the files that are
    /// NOT being renamed and would be collided with.</summary>
    private readonly IReadOnlyList<FileEntry>? _folder;

    public BatchRenameViewModel(
        IReadOnlyList<FileEntry> entries, Func<FileEntry, string, Task> rename,
        IReadOnlyList<FileEntry>? folder = null)
    {
        _entries = entries;
        _rename = rename;
        _folder = folder;

        Pattern = entries.Count > 0
            ? Path.GetFileNameWithoutExtension(entries[0].Name) + " ###"
            : "file ###";

        Refresh();
    }

    public ObservableCollection<RenamePreview> Preview { get; } = new();

    [ObservableProperty] private bool _isNumbered = true;
    [ObservableProperty] private string _pattern = "";
    [ObservableProperty] private string _find = "";
    [ObservableProperty] private string _replace = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _keepExtension = true;
    [ObservableProperty] private int _startAt = 1;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canApply;

    public string Title => $"Rename {_entries.Count} item(s)";

    /// <summary>Raised when the work is done, so the window can close itself.</summary>
    public event EventHandler? Finished;

    partial void OnIsNumberedChanged(bool value) => Refresh();
    partial void OnPatternChanged(string value) => Refresh();
    partial void OnFindChanged(string value) => Refresh();
    partial void OnReplaceChanged(string value) => Refresh();
    partial void OnUseRegexChanged(bool value) => Refresh();
    partial void OnCaseSensitiveChanged(bool value) => Refresh();
    partial void OnKeepExtensionChanged(bool value) => Refresh();
    partial void OnStartAtChanged(int value) => Refresh();

    private void Refresh()
    {
        var plan = BatchRename.Plan(_entries, new BatchRenameOptions
        {
            Mode = IsNumbered ? RenameMode.Numbered : RenameMode.Replace,
            Pattern = Pattern,
            Find = Find,
            Replace = Replace,
            UseRegex = UseRegex,
            CaseSensitive = CaseSensitive,
            KeepExtension = KeepExtension,
            StartAt = StartAt,
        }, _folder);

        Preview.Clear();
        foreach (var row in plan) Preview.Add(row);

        var problems = plan.Count(r => !r.IsValid);
        var changes = plan.Count(r => r.IsValid && r.IsChanged);

        Summary = problems > 0
            ? $"{problems} problem(s) — nothing will be renamed until they are fixed"
            : changes == 0
                ? "no changes"
                : $"{changes} of {plan.Count} will be renamed";

        // All or nothing: a partial rename halfway through a numbered sequence
        // is worse than no rename, because the numbering is then wrong and the
        // originals are gone.
        CanApply = problems == 0 && changes > 0;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply) return;

        CanApply = false;
        Summary = "renaming…";

        var done = 0;

        foreach (var row in Preview.Where(r => r.IsValid && r.IsChanged).ToList())
        {
            var entry = _entries.FirstOrDefault(e => e.FullPath == row.FullPath);
            if (entry.FullPath is null) continue;

            try
            {
                await _rename(entry, row.NewName).ConfigureAwait(true);
                done++;
            }
            catch (Exception ex)
            {
                // Worded the way the status bar words a failure, rather than
                // handing back a .NET exception message.
                Summary = $"stopped after {done}: "
                    + Core.FileSystem.Failures.Describe(ex, "rename that");
                CanApply = true;
                return;
            }
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);
}
