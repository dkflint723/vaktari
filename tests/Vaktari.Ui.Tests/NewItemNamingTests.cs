using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the three "make me one" gestures call the thing they make.
///
/// **Each had its own numbering loop, and all three disagreed with the rest of
/// the application.** New folder, new file and new-from-template produced
/// "New folder 2" — a space and a bare digit — while every other name Vaktari
/// invents is parenthesised on both platforms, and Explorer's own answer to
/// this gesture is "New folder (2)". New folder had a second fault of its own:
/// it asked only whether a FOLDER was in the way, so a file of that name sent
/// it into a create it had just been told was safe, and the gesture made
/// nothing at all.
///
/// Driven through the real commands and asserted on disk, because the naming
/// arithmetic has its own tests in Core — what these say is that the call sites
/// actually reach it.
/// </summary>
public sealed class NewItemNamingTests : OwnedViewModels
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-newitem-" + Guid.NewGuid().ToString("N")[..8]);

    public NewItemNamingTests() => Directory.CreateDirectory(_root);

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string At(string name) => Path.Combine(_root, name);

    private PaneViewModel Pane()
        => Own(new PaneViewModel(new Inert(), new Quiet()) { CurrentPath = _root });

    [AvaloniaFact]
    public async Task A_second_new_folder_is_numbered_in_parentheses()
    {
        Directory.CreateDirectory(At("New folder"));

        await Pane().NewFolderAsync();

        Assert.True(Directory.Exists(At("New folder (2)")),
                    "made: " + string.Join(", ", Directory.GetDirectories(_root).Select(Path.GetFileName)));
    }

    /// <summary>
    /// **A file of that name used to stop it dead.** The check asked only
    /// whether a folder was there, so the create went ahead on a taken path,
    /// System.IO refused, and the gesture produced nothing but a status line.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_called_New_folder_does_not_stop_a_new_folder()
    {
        File.WriteAllText(At("New folder"), "not a folder");

        var pane = Pane();

        await pane.NewFolderAsync();

        Assert.True(Directory.Exists(At("New folder (2)")), pane.Status);
    }

    [AvaloniaFact]
    public async Task A_second_new_file_is_numbered_before_its_extension()
    {
        File.WriteAllText(At("New file.txt"), "first");

        await Pane().NewFileAsync(new NewFileKind("Text file", ".txt"));

        Assert.True(File.Exists(At("New file (2).txt")),
                    "made: " + string.Join(", ", Directory.GetFiles(_root).Select(Path.GetFileName)));

        // And the one that was already there is untouched: the naive path
        // truncates it instead of numbering around it.
        Assert.Equal("first", File.ReadAllText(At("New file.txt")));
    }

    /// <summary>
    /// **A dotfile is a name, not a bare extension.** Splitting on the last dot
    /// made a second .gitignore into " 2.gitignore", with nothing at all in
    /// front of the space.
    /// </summary>
    [AvaloniaFact]
    public async Task A_template_whose_name_starts_with_a_dot_keeps_it()
    {
        var templates = Directory.CreateDirectory(Path.Combine(_root, "templates")).FullName;
        var source = Path.Combine(templates, ".gitignore");

        File.WriteAllText(source, "bin/");
        File.WriteAllText(At(".gitignore"), "obj/");

        var pane = Pane();

        await pane.NewFromTemplateAsync(new FileTemplate("gitignore", source));

        Assert.True(File.Exists(At(".gitignore (2)")),
                    "made: " + string.Join(", ", Directory.GetFiles(_root).Select(Path.GetFileName)));
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>Accepts every operation and does none of it. Needed because
    /// the template create returns early with no operations at all.</summary>
    private sealed class Quiet : IFileOperations
    {
        private static IOperationHandle Done()
        {
            var handle = new OperationHandle();

            handle.Begin(0, 0);
            handle.Complete();

            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Done();
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
