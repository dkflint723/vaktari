using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A batch rename that stops part-way, and what Apply — or Try again — does
/// next (review finding 9).
///
/// **Apply after a stop planned from where the files had been.** Some had
/// already been renamed, and in a swap one sat under a staging name, so the
/// plan named files that were no longer there or renamed them twice. And a
/// resume inside the same undo group would have held the engine's one group
/// open while the person read the message, catching every rename made
/// meanwhile in any window.
/// </summary>
public sealed class BatchRenameStopTests
{
    /// <summary>
    /// A folder of names that refuses a collision, and refuses one name while
    /// something "has it open" — until the test lets go.
    /// </summary>
    private sealed class Folder(params string[] names)
    {
        private readonly HashSet<string> _live = new(names, StringComparer.Ordinal);

        public string? HeldOpen { get; set; }

        public string? Taken { get; set; }

        public IReadOnlyCollection<string> Live => _live;

        public IReadOnlyList<FileEntry> Entries =>
            [.. names.Select(n => new FileEntry(
                n, Path.Combine(Path.GetTempPath(), n), 1,
                DateTimeOffset.UnixEpoch, EntryFlags.None))];

        public Task Rename(FileEntry entry, string newName)
        {
            if (entry.Name == HeldOpen)
                throw new InUseException(entry.FullPath, isDirectory: false, unchecked((int)0x80070020));

            if (newName == Taken) throw new IOException($"'{newName}' already exists here.");

            if (!_live.Contains(entry.Name))
                throw new FileNotFoundException($"'{entry.Name}' is not there.");

            if (_live.Contains(newName) && newName != entry.Name)
                throw new IOException($"'{newName}' already exists here.");

            _live.Remove(entry.Name);
            _live.Add(newName);

            return Task.CompletedTask;
        }
    }

    private sealed class Group : IUndoGroup
    {
        public string Description { get; set; } = "";
        public bool Closed { get; private set; }
        public void Dispose() => Closed = true;
    }

    /// <summary>
    /// **Try again finishes the run, and is a step of its own.** The stop
    /// closes the group before anything is shown; Try again opens another.
    /// The resume renames only what is left, under the names first planned.
    /// </summary>
    [AvaloniaFact]
    public async Task A_run_stopped_by_a_file_in_use_resumes_where_it_stopped()
    {
        var folder = new Folder("a.txt", "b.txt", "c.txt") { HeldOpen = "b.txt" };
        var groups = new List<Group>();

        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries,
            () => { var g = new Group(); groups.Add(g); return g; })
        {
            Pattern = "x###",
        };

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.True(model.IsInUse);
        Assert.Contains("something else has that file open", model.Summary);
        Assert.True(model.CanApply);
        Assert.True(Assert.Single(groups).Closed, "the undo group was held open across the stop");

        // What is left, read from where the files are now.
        Assert.Equal(
            ["x001.txt→x001.txt", "b.txt→x002.txt", "c.txt→x003.txt"],
            model.Preview.Select(r => $"{r.OldName}→{r.NewName}"));

        folder.HeldOpen = null;

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["x001.txt", "x002.txt", "x003.txt"], folder.Live.Order(StringComparer.Ordinal));
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.True(g.Closed));
        Assert.False(model.IsInUse);
    }

    /// <summary>
    /// **Find and replace must not be applied twice.** Planned again from
    /// names it has already rewritten, "a" → "aa" would make "aaaa" of the
    /// file it had finished. The rest of the run keeps the names first
    /// planned.
    /// </summary>
    [AvaloniaFact]
    public async Task A_replace_is_not_applied_twice_to_a_file_it_already_renamed()
    {
        var folder = new Folder("a1.txt", "a2.txt") { HeldOpen = "a2.txt" };

        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries)
        {
            IsNumbered = false,
            Find = "a",
            Replace = "aa",
        };

        await model.ApplyCommand.ExecuteAsync(null);

        folder.HeldOpen = null;

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["aa1.txt", "aa2.txt"], folder.Live.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every stop is planned again, not only one for a file in use — a name
    /// taken is the commonest. No Try again row for it, which is about
    /// something having a file open.
    /// </summary>
    [AvaloniaFact]
    public async Task Any_stop_is_planned_again_from_where_the_files_are()
    {
        var folder = new Folder("a.txt", "b.txt") { Taken = "x002.txt" };

        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries)
        {
            Pattern = "x###",
        };

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.False(model.IsInUse);
        Assert.Contains("already exists", model.Summary);
        Assert.Contains(model.Preview, r => r.OldName == "x001.txt" && !r.IsChanged);

        folder.Taken = null;
        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["x001.txt", "x002.txt"], folder.Live.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// **The folder is read again off the window's thread** (rename QA): on a
    /// share that read is a wait on the network, and the dialog is drawn on
    /// the thread it would have waited on.
    /// </summary>
    [AvaloniaFact]
    public async Task The_folder_is_read_again_off_the_window_s_thread()
    {
        var folder = new Folder("a.txt", "b.txt") { HeldOpen = "b.txt" };
        bool? onTheWindowsThread = null;

        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries, reread: () =>
        {
            onTheWindowsThread = Dispatcher.UIThread.CheckAccess();
            return folder.Entries;
        })
        {
            Pattern = "x###",
        };

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.True(model.IsInUse);
        Assert.False(onTheWindowsThread ?? true, "the folder was read on the window's thread, or not at all");
    }

    /// <summary>Changing the options after a stop plans afresh from the
    /// names the files have now.</summary>
    [AvaloniaFact]
    public async Task Changing_the_pattern_after_a_stop_plans_from_the_names_now()
    {
        var folder = new Folder("a.txt", "b.txt") { HeldOpen = "b.txt" };

        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries)
        {
            Pattern = "x###",
        };

        await model.ApplyCommand.ExecuteAsync(null);

        model.Pattern = "y###";

        Assert.False(model.IsInUse);
        Assert.Equal(["x001.txt→y001.txt", "b.txt→y002.txt"], model.Preview.Select(r => $"{r.OldName}→{r.NewName}"));
    }

    /// <summary>
    /// The row is there only for a file in use, and hidden otherwise — asserted
    /// as hidden, since a binding that does not resolve leaves a row SHOWN.
    /// </summary>
    [AvaloniaFact]
    public async Task The_Try_again_row_shows_only_for_a_file_in_use()
    {
        var folder = new Folder("a.txt", "b.txt") { HeldOpen = "b.txt" };

        var model = new BatchRenameViewModel(folder.Entries, folder.Rename, folder.Entries) { Pattern = "x###" };
        var window = new BatchRenameWindow(model);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var row = window.FindControl<Border>("InUseRow")!;
            var button = window.FindControl<Button>("TryAgainButton")!;

            Assert.False(row.IsVisible, "the Try again row showed before anything stopped");

            await model.ApplyCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(row.IsVisible);
            Assert.Same(model.ApplyCommand, button.Command);

            folder.HeldOpen = null;
            await model.ApplyCommand.ExecuteAsync(null);
        }
        finally
        {
            window.Close();
        }
    }
}
