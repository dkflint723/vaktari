using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the status bar says when a create is refused.
///
/// **It spoke .NET for a file and English for a folder.** New folder went
/// through <c>Failures.Describe</c> and said "that folder is not there any
/// more"; new file, one menu row away and the same gesture, printed
/// <c>ex.Message</c> — "Could not find a part of the path
/// 'D:\gone\New file.txt'." So the same refusal read as a plain sentence or as
/// a stack-trace fragment depending on which of two adjacent commands you
/// happened to pick, and the fragment made the reader parse a path to learn
/// what the sentence would have told them.
///
/// Driven through the real commands rather than asserting on Failures.Describe,
/// which has its own tests: what was wrong here was that these two catch blocks
/// never asked it.
/// </summary>
public sealed class NewItemFailureTests : OwnedViewModels
{
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

    /// <summary>Accepts every operation and does none of it.</summary>
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
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>A folder that is not there, so the create genuinely throws
    /// rather than being refused by a guard before it tries.</summary>
    private PaneViewModel InAFolderThatIsGone()
        => Own(new PaneViewModel(new Inert(), new Quiet())
        {
            CurrentPath = Path.Combine(
                Path.GetTempPath(), "vaktari-gone-" + Guid.NewGuid().ToString("N")),
        });

    [AvaloniaFact]
    public async Task Creating_a_file_where_the_folder_has_gone_says_so_in_words()
    {
        var pane = InAFolderThatIsGone();

        await pane.NewFileAsync(new NewFileKind("Text file", ".txt"));

        Assert.Equal("that folder is not there any more", pane.Status);
    }

    /// <summary>The half that names the bug: no path, no exception type, no
    /// full stop lifted out of System.IO.</summary>
    [AvaloniaFact]
    public async Task The_message_is_a_sentence_rather_than_an_exception()
    {
        var pane = InAFolderThatIsGone();

        await pane.NewFileAsync(new NewFileKind("Text file", ".txt"));

        Assert.DoesNotContain(pane.CurrentPath, pane.Status);
        Assert.DoesNotContain("Could not find", pane.Status);
        Assert.DoesNotContain("could not create file:", pane.Status);
    }

    /// <summary>
    /// And the same sentence from the template route, which had its own copy of
    /// the raw message. A template deleted out from under the menu is the
    /// common case here.
    /// </summary>
    [AvaloniaFact]
    public async Task Creating_from_a_missing_template_says_the_same_thing()
    {
        var pane = InAFolderThatIsGone();

        var template = new FileTemplate(
            "Notes",
            Path.Combine(Path.GetTempPath(),
                         "vaktari-no-template-" + Guid.NewGuid().ToString("N") + ".txt"));

        await pane.NewFromTemplateAsync(template);

        Assert.DoesNotContain("could not create from template:", pane.Status);
        Assert.DoesNotContain("Could not find", pane.Status);
        Assert.False(string.IsNullOrWhiteSpace(pane.Status), "the refusal said nothing at all");
    }
}
