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

    /// <summary>
    /// **A template with no file behind it had nowhere to come from.** This
    /// route was a bare File.Copy, and Explorer's New menu is the ShellNew
    /// registry keys — where the one row Windows itself ships, "Compressed
    /// (zipped) Folder", carries its 22 bytes inline. That is the
    /// end-of-central-directory record of an empty archive, and no file on the
    /// machine holds it: measured, the six rows this provider offers are five
    /// Office seed files and that one, so a copy-only route lost exactly the
    /// row nobody had to install anything to get.
    ///
    /// The name still comes from Path, which for these is a leaf and not a
    /// place on disk.
    /// </summary>
    [AvaloniaFact]
    public async Task A_template_that_carries_its_bytes_is_written_rather_than_copied()
    {
        byte[] emptyZip =
            [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        var pane = Pane();

        await pane.NewFromTemplateAsync(
            new FileTemplate("Compressed (zipped) Folder", "Compressed (zipped) Folder.zip")
            {
                Content = emptyZip,
            });

        var made = At("Compressed (zipped) Folder.zip");

        Assert.True(File.Exists(made), pane.Status);
        Assert.Equal(emptyZip, File.ReadAllBytes(made));
    }

    /// <summary>
    /// **The copy used to be called whatever the seed was called.** Measured on
    /// Windows 11 26200: the Access row's ShellNew key points at
    /// <c>…\Office16\1033\ACCESS12.ACC</c>, so "New &gt; Microsoft Access
    /// Database" made a file called ACCESS12.ACC — not the row's name, and not
    /// even the .accdb the row is for. Word, Excel, PowerPoint and Publisher
    /// were the same, which is five of the six rows the Windows provider
    /// offers. Explorer's answer to that key is
    /// "New Microsoft Access Database.accdb".
    ///
    /// Leaf is the row's answer to "what is it called"; Path stays the file to
    /// copy the bytes out of. A Linux template sets no Leaf, because the user
    /// named the file themselves — which is what
    /// <see cref="A_template_whose_name_starts_with_a_dot_keeps_it"/> holds.
    /// </summary>
    [AvaloniaFact]
    public async Task A_copied_template_is_called_what_the_row_says_not_what_the_seed_is_called()
    {
        var seeds = Directory.CreateDirectory(Path.Combine(_root, "office")).FullName;
        var seed = Path.Combine(seeds, "ACCESS12.ACC");

        File.WriteAllText(seed, "an Access seed file");

        var pane = Pane();

        await pane.NewFromTemplateAsync(
            new FileTemplate("Microsoft Access Database", seed)
            {
                Leaf = "Microsoft Access Database.accdb",
            });

        var made = At("Microsoft Access Database.accdb");

        Assert.True(File.Exists(made),
                    "made: " + string.Join(", ", Directory.GetFiles(_root).Select(Path.GetFileName)));

        // Path is still where the bytes come from — renaming the destination
        // must not turn the copy into an empty file.
        Assert.Equal("an Access seed file", File.ReadAllText(made));
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

        /// <summary>No history, so nothing to gather into a step.</summary>
        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
