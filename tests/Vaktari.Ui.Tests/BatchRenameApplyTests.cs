using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Applying a batch rename.
///
/// **Renumbering stopped after zero files.** The preview correctly allowed
/// img001 to take img002's name, because the file holding it was also being
/// renamed — and then the rows were applied in the order they were shown, so
/// the first one asked the file system to rename img001 onto a name img002
/// still had. Both executors refuse that, and the dialog reported "stopped
/// after 0" for the commonest batch rename there is.
///
/// Driven through the real command against a fake renamer that behaves like a
/// file system: it refuses a move onto a name that is still taken. A fake that
/// accepted anything would pass with the bug in place.
/// </summary>
public sealed class BatchRenameApplyTests
{
    /// <summary>
    /// A folder of names that refuses a collision, which is the whole point:
    /// the ordering is only real if something can reject it.
    /// </summary>
    private sealed class Folder(params string[] names)
    {
        private readonly HashSet<string> _live = new(names, StringComparer.Ordinal);

        public List<string> Renames { get; } = [];

        public IReadOnlyList<FileEntry> Entries =>
            [.. names.Select(n => new FileEntry(
                n, Path.Combine(Path.GetTempPath(), n), 1,
                DateTimeOffset.UnixEpoch, EntryFlags.None))];

        public IReadOnlyCollection<string> Live => _live;

        public Task Rename(FileEntry entry, string newName)
        {
            if (!_live.Contains(entry.Name))
                throw new FileNotFoundException($"'{entry.Name}' is not there.");

            if (_live.Contains(newName) && newName != entry.Name)
                throw new IOException($"'{newName}' already exists here.");

            _live.Remove(entry.Name);
            _live.Add(newName);
            Renames.Add($"{entry.Name} -> {newName}");

            return Task.CompletedTask;
        }
    }

    private static BatchRenameViewModel Renaming(Folder folder, string pattern, int startAt = 1)
    {
        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries)
        {
            Pattern = pattern,
            StartAt = startAt,
        };

        return model;
    }

    /// <summary>The reported case, end to end.</summary>
    [AvaloniaFact]
    public async Task Renumbering_upwards_renames_every_file()
    {
        var folder = new Folder("img001.jpg", "img002.jpg", "img003.jpg");

        var model = Renaming(folder, "img###", startAt: 2);

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(
            ["img002.jpg", "img003.jpg", "img004.jpg"],
            folder.Live.Order(StringComparer.Ordinal));

        Assert.DoesNotContain("stopped after", model.Summary);
    }

    /// <summary>
    /// And it costs three renames, not six. A chain drains from its far end and
    /// needs no staging at all; parking every file first would triple the
    /// filesystem work for the case this fix is about.
    /// </summary>
    [AvaloniaFact]
    public async Task Renumbering_costs_one_rename_per_file()
    {
        var folder = new Folder("img001.jpg", "img002.jpg", "img003.jpg");

        await Renaming(folder, "img###", startAt: 2).ApplyCommand.ExecuteAsync(null);

        Assert.Equal(3, folder.Renames.Count);
        Assert.DoesNotContain(folder.Renames, r => r.Contains(".vaktari-rename-"));
    }

    /// <summary>
    /// Renumbering downwards is the other half of the same shape and fails the
    /// same way without an order — the chain simply drains from the other end.
    /// </summary>
    [AvaloniaFact]
    public async Task Renumbering_downwards_renames_every_file_too()
    {
        var folder = new Folder("img002.jpg", "img003.jpg", "img004.jpg");

        await Renaming(folder, "img###", startAt: 1).ApplyCommand.ExecuteAsync(null);

        Assert.Equal(
            ["img001.jpg", "img002.jpg", "img003.jpg"],
            folder.Live.Order(StringComparer.Ordinal));
    }

    /// <summary>A real collision — a name held by a file that is NOT in the
    /// selection — is still a refusal, and must stay one.</summary>
    [AvaloniaFact]
    public void A_name_already_taken_by_a_bystander_is_still_refused()
    {
        var folder = new Folder("a.txt", "b.txt");

        var bystander = new FileEntry(
            "one.txt", Path.Combine(Path.GetTempPath(), "one.txt"), 1,
            DateTimeOffset.UnixEpoch, EntryFlags.None);

        var model = new BatchRenameViewModel(
            folder.Entries, folder.Rename, [.. folder.Entries, bystander])
        {
            Pattern = "one",
        };

        Assert.Contains(model.Preview, r => !r.IsValid);
    }
}
