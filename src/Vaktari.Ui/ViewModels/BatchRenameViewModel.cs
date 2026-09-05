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

    /// <summary>Opens the engine's one-step-for-the-whole-batch undo group.
    /// Null in a test that is not exercising the history.</summary>
    private readonly Func<IUndoGroup?>? _group;

    public BatchRenameViewModel(
        IReadOnlyList<FileEntry> entries, Func<FileEntry, string, Task> rename,
        IReadOnlyList<FileEntry>? folder = null,
        Func<IUndoGroup?>? undoGroup = null)
    {
        _entries = entries;
        _rename = rename;
        _folder = folder;
        _group = undoGroup;

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

        // What the whole batch cost, for the Undo row. Grown as the renames
        // land rather than taken from the plan, so a run that stops halfway is
        // offered back as the three files it managed and not as the forty it
        // set out to do.
        var renamed = new List<string>();

        // **One Ctrl+Z for the dialog, not one per file.** Every rename below
        // used to push its own undo entry, so taking back a renumbered folder
        // of forty photographs meant forty presses — and a swap pushed more
        // entries than there were files, because the staging move landed on the
        // stack too. The group closes on the way out of this block, including
        // the failure return below.
        using (var group = _group?.Invoke())
        {
            // **In an order the file system will accept, not the order shown.**
            // Renumbering asks for img001 to become img002 while img002 still
            // holds that name, and applying the rows top to bottom failed on
            // the first one — so the commonest batch rename there is reported
            // "stopped after 0". Sequence walks each chain from its far end and
            // pays for a staging move only where there is a genuine cycle.
            foreach (var step in Core.BatchRename.Sequence(Preview))
            {
                var entry = _entries.FirstOrDefault(e => e.FullPath == step.FullPath);
                if (entry.FullPath is null) continue;

                // Where the file is NOW, which a staging move has changed.
                var moving = entry with
                {
                    FullPath = step.FromPath,
                    Name = Path.GetFileName(step.FromPath),
                };

                try
                {
                    await _rename(moving, step.NewName).ConfigureAwait(true);

                    // A staging move is machinery, not a name anybody asked
                    // for: it is not counted.
                    //
                    // It is named all the same when it is the first thing to
                    // land, because the very next rename can refuse and leave
                    // the group holding nothing else — and then the Undo row
                    // was "rename of .vaktari-rename-0123456789abcdef", the
                    // name the file is parked under, rather than the name
                    // Ctrl+Z would bring back. Overwritten by every real rename
                    // after it.
                    if (step.IsTemporary)
                    {
                        if (group is not null && renamed.Count == 0)
                            group.Description = UndoNames.Of("rename", [step.FromPath]);

                        continue;
                    }

                    done++;
                    renamed.Add(step.ToPath);

                    if (group is not null)
                        group.Description = UndoNames.Of("rename", renamed);
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
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);
}
