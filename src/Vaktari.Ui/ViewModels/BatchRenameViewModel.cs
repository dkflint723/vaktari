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
    /// <summary>
    /// The files being renamed, where each one is NOW — re-pointed after a run
    /// that stopped part-way (see <see cref="AfterAStopAsync"/>), so the next plan
    /// starts from the names the files actually have.
    /// </summary>
    private IReadOnlyList<FileEntry> _entries;

    private readonly Func<FileEntry, string, Task> _rename;

    /// <summary>The whole folder, so the preview can see the files that are
    /// NOT being renamed and would be collided with. Read again after a run
    /// that stopped, through <see cref="_reread"/> when there is one.</summary>
    private IReadOnlyList<FileEntry>? _folder;

    /// <summary>Reads the folder again from the disk, for the plan after a
    /// stop. Null keeps the folder as the dialog was given it, moved on by
    /// the renames this dialog itself made.</summary>
    private readonly Func<IReadOnlyList<FileEntry>>? _reread;

    /// <summary>
    /// The names a run that stopped part-way still owes, by where each file is
    /// now — or null when nothing is owed. While it is set, the preview is
    /// this rather than a fresh plan: a find-and-replace planned again from
    /// names it has already rewritten would rewrite them twice. Any change to
    /// the options drops it, and the plan is then made afresh from the files
    /// as they now are.
    /// </summary>
    private Dictionary<string, string>? _owed;

    /// <summary>Opens the engine's one-step-for-the-whole-batch undo group.
    /// Null in a test that is not exercising the history.</summary>
    private readonly Func<IUndoGroup?>? _group;

    public BatchRenameViewModel(
        IReadOnlyList<FileEntry> entries, Func<FileEntry, string, Task> rename,
        IReadOnlyList<FileEntry>? folder = null,
        Func<IUndoGroup?>? undoGroup = null,
        Func<IReadOnlyList<FileEntry>>? reread = null)
    {
        _entries = entries;
        _rename = rename;
        _folder = folder;
        _group = undoGroup;
        _reread = reread;

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

    /// <summary>
    /// Whether the run stopped because something has a file, or something
    /// inside a folder, open — which is what the Try again row is for.
    /// </summary>
    [ObservableProperty] private bool _isInUse;

    /// <summary>Where to look for what has it open, on Windows; empty
    /// elsewhere. Beside Try again, under the summary that says what
    /// stopped.</summary>
    public string InUseHint => InUseOffer.Hint is { } hint ? char.ToUpperInvariant(hint[0]) + hint[1..] : "";

    public string Title => $"Rename {_entries.Count} item(s)";

    /// <summary>Raised when the work is done, so the window can close itself.</summary>
    public event EventHandler? Finished;

    partial void OnIsNumberedChanged(bool value) => Replan();
    partial void OnPatternChanged(string value) => Replan();
    partial void OnFindChanged(string value) => Replan();
    partial void OnReplaceChanged(string value) => Replan();
    partial void OnUseRegexChanged(bool value) => Replan();
    partial void OnCaseSensitiveChanged(bool value) => Replan();
    partial void OnKeepExtensionChanged(bool value) => Replan();
    partial void OnStartAtChanged(int value) => Replan();

    /// <summary>The options changed: whatever a stopped run owed is
    /// forgotten, and the plan is made from the files as they are.</summary>
    private void Replan()
    {
        _owed = null;
        IsInUse = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_owed is { } owed)
        {
            ShowOwed(owed);
            return;
        }

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

    /// <summary>
    /// The rest of a run that stopped, as the preview: every file where it is
    /// now, and the name it was going to get.
    /// </summary>
    private void ShowOwed(Dictionary<string, string> owed)
    {
        Preview.Clear();

        foreach (var entry in _entries)
            Preview.Add(new RenamePreview(
                entry.FullPath, entry.Name,
                owed.TryGetValue(entry.FullPath, out var name) ? name : entry.Name,
                Problem: null));

        CanApply = Preview.Any(r => r.IsChanged);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply) return;

        CanApply = false;
        IsInUse = false;
        Summary = "renaming…";

        var done = 0;

        // Where each file is after every step that landed, by its row: a run
        // that stops is planned again from here, not from where it began.
        var now = _entries.ToDictionary(e => e.FullPath, e => e.FullPath, StringComparer.Ordinal);

        // Every row's target, for what a stopped run still owes.
        var wanted = Preview.Where(r => r.IsValid && r.IsChanged)
                            .ToDictionary(r => r.FullPath, r => r.NewName, StringComparer.Ordinal);

        Exception? stopped = null;

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

                    now[step.FullPath] = step.ToPath;

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
                    stopped = ex;

                    // **Out of the group before anything is shown** (review
                    // finding 9). The group is the engine's, shared by every
                    // window: held open while the person reads this, every
                    // rename made meanwhile anywhere would have joined this
                    // dialog's one Ctrl+Z. What landed is one step now, and
                    // what Try again lands is another.
                    break;
                }
            }
        }

        if (stopped is null)
        {
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        await AfterAStopAsync(now, wanted).ConfigureAwait(true);

        // Worded the way the status bar words a failure, rather than handing
        // back a .NET exception message.
        Summary = $"stopped after {done}: " + Core.FileSystem.Failures.Describe(stopped, "rename that");
        IsInUse = stopped is InUseException;
    }

    /// <summary>
    /// After a run stopped part-way: the files re-pointed at where they now
    /// are, the folder read again, and the preview showing exactly what is
    /// left to do — so Apply, or Try again, finishes the job rather than
    /// redoing it.
    ///
    /// **Apply after a stop renamed the wrong things** (review finding 9).
    /// It planned again from the paths the dialog opened with, and some of
    /// those had already been renamed — or were sitting under a staging name
    /// half-way through a swap — so the plan named files that were no longer
    /// there, or renumbered from names that had already moved. Every stop
    /// comes through here: in use, name taken, permission, anything.
    /// </summary>
    private async Task AfterAStopAsync(Dictionary<string, string> now, Dictionary<string, string> wanted)
    {
        var owed = new Dictionary<string, string>(StringComparer.Ordinal);

        _entries = [.. _entries.Select(entry =>
        {
            var at = now.TryGetValue(entry.FullPath, out var moved) ? moved : entry.FullPath;
            var here = entry with { FullPath = at, Name = Path.GetFileName(at) };

            if (wanted.TryGetValue(entry.FullPath, out var name) && !string.Equals(here.Name, name, StringComparison.Ordinal))
                owed[at] = name;

            return here;
        })];

        // **Read off the window's thread** (rename QA, note 3): the folder
        // can be on a share, and a listing of it is a wait on the network.
        _folder = _reread is { } reread
            ? await Task.Run(reread).ConfigureAwait(true)
            : Moved(_folder, now);
        _owed = owed;

        Refresh();
    }

    /// <summary>The folder as the dialog was given it, with this dialog's own
    /// renames applied — what is known without reading the disk.</summary>
    private static IReadOnlyList<FileEntry>? Moved(IReadOnlyList<FileEntry>? folder, Dictionary<string, string> now)
        => folder is null ? null
            : [.. folder.Select(entry => now.TryGetValue(entry.FullPath, out var at)
                ? entry with { FullPath = at, Name = Path.GetFileName(at) }
                : entry)];

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Everything now in the folders <paramref name="entries"/> live in, read
    /// from the disk: the bystanders a plan made after a stop checks against.
    /// By name only, which is all a plan reads of them; a folder that cannot
    /// be read adds nothing.
    /// </summary>
    public static IReadOnlyList<FileEntry> OnDisk(IReadOnlyList<FileEntry> entries)
    {
        var found = new List<FileEntry>();

        foreach (var folder in entries.Select(e => Path.GetDirectoryName(e.FullPath))
                                      .OfType<string>()
                                      .Distinct(PathRules.Comparer))
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                    found.Add(new FileEntry(Path.GetFileName(path), path, 0, default, EntryFlags.None));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Vaktari.Core.Quiet.Swallowed("batch-rename", e);
            }
        }

        return found;
    }
}
