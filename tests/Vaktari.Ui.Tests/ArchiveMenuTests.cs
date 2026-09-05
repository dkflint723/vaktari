using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Compress to ZIP and Extract all, on the right-click menu.
///
/// **There was no compress and no extract anywhere in the application.** No
/// verb in MainWindow.axaml, none in ShellViewModel, and the only ZipArchive in
/// src was the icon-theme installer's — so every route to a zip went through
/// the hosted "Windows menu", whatever the machine happened to have registered
/// there.
///
/// The rows are pinned in the markup (they have to be at the top level, which
/// is the whole finding) and the verbs are driven against real files on disk,
/// because what they do is write one.
/// </summary>
public sealed class ArchiveMenuTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-zipmenu").FullName;

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    private string At(string name) => Path.Combine(_root, name);

    private string Write(string name, string content = "content")
    {
        var path = At(name);

        File.WriteAllText(path, content);

        return path;
    }

    // ---- the rows themselves -----------------------------------------------

    /// <summary>
    /// The listing's context menu, which is the only one in the file whose
    /// DataType is the pane group.
    /// </summary>
    private static XElement ListingMenu()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "ContextMenu")
            .Single(m => (string?)m.Attribute(
                XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "DataType")
                == "vm:PaneGroupViewModel");

    /// <summary>
    /// **Top level, which is the finding.** A row nested inside another
    /// MenuItem is a hover away and would satisfy any test that merely looked
    /// for the header somewhere in the file, so the assertion is on the menu's
    /// DIRECT children.
    /// </summary>
    [Theory]
    [InlineData("Compress to ZIP", "{Binding ActiveTab.CompressSelectionCommand}",
                "{Binding ActiveTab.CanCompressSelection}")]
    [InlineData("Extract all", "{Binding ActiveTab.ExtractSelectionCommand}",
                "{Binding ActiveTab.CanExtractSelection}")]
    public void The_archive_rows_sit_at_the_top_of_the_listing_menu(
        string header, string command, string gate)
    {
        var row = ListingMenu()
            .Elements(Avalonia + "MenuItem")
            .SingleOrDefault(m => MenuLabels.Plain((string?)m.Attribute("Header")) == header);

        Assert.True(row is not null,
            $"\"{header}\" is not a direct child of the listing's context menu");

        Assert.Equal(command, (string?)row!.Attribute("Command"));
        Assert.Equal(gate, (string?)row.Attribute("IsVisible"));
    }

    /// <summary>
    /// The rule that introduces the pair is gated on the compress row, and has
    /// to be — the reason is the one the menu's own comments give for gating
    /// every other separator in it: Avalonia draws every one it is given and
    /// collapses none, so an ungated rule here would be a stray line in every
    /// listing where neither row shows, which is the bin and every folder with
    /// nothing selected.
    /// </summary>
    [Fact]
    public void The_rule_above_the_pair_comes_and_goes_with_them()
    {
        var children = ListingMenu().Elements().ToList();

        var compress = children.FindIndex(
            e => MenuLabels.Plain((string?)e.Attribute("Header")) == "Compress to ZIP");

        Assert.True(compress > 0, "the compress row is not a direct child of the menu");

        var before = children[compress - 1];

        Assert.Equal("Separator", before.Name.LocalName);
        Assert.Equal("{Binding ActiveTab.CanCompressSelection}", (string?)before.Attribute("IsVisible"));
    }

    // ---- when the rows are offered -----------------------------------------

    private readonly Recording _ops = new();

    private PaneViewModel Pane(string? at = null)
        => Own(new PaneViewModel(new Listing(), _ops) { CurrentPath = at ?? _root });

    private static FileEntry Row(string path)
        => new(Path.GetFileName(path), path, 0, DateTimeOffset.Now,
               Directory.Exists(path) ? EntryFlags.Directory : EntryFlags.None);

    [AvaloniaFact]
    public void Compress_is_offered_for_a_selection_in_a_real_folder()
    {
        var pane = Pane();

        Assert.False(pane.CanCompressSelection);

        pane.SelectedEntry = Row(Write("notes.txt"));

        Assert.True(pane.CanCompressSelection);
    }

    /// <summary>
    /// **A Recent or a bin row names where a file WAS.** Compressing there
    /// would archive whatever occupies that path now, which is the fault
    /// Duplicate and Mount already refuse those listings for.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(VirtualPaths.Trash)]
    [InlineData(VirtualPaths.Files)]
    public void Neither_verb_is_offered_in_a_listing_that_is_not_a_folder(string listing)
    {
        var pane = Pane(listing);

        pane.SelectedEntry = Row(Write("holiday.zip"));

        Assert.False(pane.CanCompressSelection);
        Assert.False(pane.CanExtractSelection);
    }

    /// <summary>
    /// **A selection here can span folders**, because the details listing
    /// splices an expanded folder's rows in underneath it — and two rows with
    /// the same name would land on one entry in the archive, which is measured
    /// and refused in Archives.CanCompress. The row hides rather than offering
    /// an archive that would drop half of what went in.
    /// </summary>
    [AvaloniaFact]
    public void Compress_is_not_offered_for_a_selection_spread_across_folders()
    {
        var pane = Pane();
        var here = Row(Write("notes.txt"));

        var inner = Directory.CreateDirectory(At("inner")).FullName;

        File.WriteAllText(Path.Combine(inner, "notes.txt"), "the other one");

        pane.SelectedEntries.Add(here);
        pane.SelectedEntries.Add(Row(Write("beside.txt")));

        Assert.True(pane.CanCompressSelection);

        pane.SelectedEntries.Add(Row(Path.Combine(inner, "notes.txt")));

        Assert.False(pane.CanCompressSelection);
    }

    [AvaloniaFact]
    public void Extract_is_offered_for_one_zip_and_nothing_else()
    {
        var pane = Pane();

        pane.SelectedEntry = Row(Write("holiday.zip"));
        Assert.True(pane.CanExtractSelection);

        pane.SelectedEntry = Row(Write("holiday.7z"));
        Assert.False(pane.CanExtractSelection);

        Directory.CreateDirectory(At("folder.zip"));
        pane.SelectedEntry = Row(At("folder.zip"));
        Assert.False(pane.CanExtractSelection);
    }

    /// <summary>
    /// **"Extract all" means all of ONE archive.** With two rows picked the
    /// verb would have unpacked the focused one and silently ignored the other,
    /// which is the fault Open had before it learned to act on the whole
    /// selection.
    /// </summary>
    [AvaloniaFact]
    public void Extract_is_not_offered_when_more_than_one_thing_is_picked()
    {
        var pane = Pane();
        var zip = Row(Write("holiday.zip"));

        pane.SelectedEntry = zip;
        pane.SelectedEntries.Add(zip);

        Assert.True(pane.CanExtractSelection);

        pane.SelectedEntries.Add(Row(Write("other.zip")));

        Assert.False(pane.CanExtractSelection);
    }

    /// <summary>
    /// **A menu row is only as live as its notification.** The gates are plain
    /// computed properties, so without a raise on the selection change the row
    /// keeps whatever visibility the previous right-click left it with.
    /// </summary>
    [AvaloniaFact]
    public void Both_gates_are_announced_when_the_selection_changes()
    {
        var pane = Pane();
        var said = new List<string>();

        pane.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? "");

        pane.SelectedEntry = Row(Write("holiday.zip"));

        Assert.Contains(nameof(PaneViewModel.CanCompressSelection), said);
        Assert.Contains(nameof(PaneViewModel.CanExtractSelection), said);

        // The other route to a selection: a listing's own collection, which is
        // what a click or a rubber band writes to. It raises its own set of
        // notifications and the focused row's setter is not on that path.
        said.Clear();

        pane.SelectedEntries.Add(Row(Write("other.zip")));

        Assert.Contains(nameof(PaneViewModel.CanCompressSelection), said);
        Assert.Contains(nameof(PaneViewModel.CanExtractSelection), said);
    }

    /// <summary>
    /// The third route: both gates read IsRealFolder, so they change when the
    /// pane moves between a folder and one of the virtual listings even though
    /// the selection has not been touched.
    /// </summary>
    [AvaloniaFact]
    public void Both_gates_are_announced_when_the_pane_changes_listing()
    {
        var pane = Pane();
        var said = new List<string>();

        pane.SelectedEntry = Row(Write("holiday.zip"));

        pane.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? "");

        pane.CurrentPath = VirtualPaths.Trash;

        // The path handler posts its raises, because CurrentPath is assigned
        // from a pool thread after a listing loads.
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(PaneViewModel.CanCompressSelection), said);
        Assert.Contains(nameof(PaneViewModel.CanExtractSelection), said);
    }

    // ---- what the verbs do -------------------------------------------------

    [AvaloniaFact]
    public async Task Compress_writes_a_zip_beside_what_went_into_it()
    {
        var pane = Pane();

        pane.SelectedEntry = Row(Write("notes.txt", "hello"));

        await pane.CompressSelectionAsync();

        Assert.True(File.Exists(At("notes.zip")), pane.Status);

        // Undoable, the same way a new folder is.
        Assert.Equal([At("notes.zip")], _ops.Created);

        // And the listing shows it without waiting for the watcher.
        Assert.Contains(pane.Entries, e => e.Name == "notes.zip");

        // Named, so the answer to "where did it go" is on screen.
        Assert.Equal("made notes.zip", pane.Status);
    }

    /// <summary>
    /// **Beside the source, not in CurrentPath.** A details listing can have
    /// folders expanded in place, so a picked row need not live in the folder
    /// the pane is pointed at — and an archive of it that landed a level up
    /// would be somewhere the person was not looking.
    /// </summary>
    [AvaloniaFact]
    public async Task An_archive_of_a_row_in_an_expanded_folder_lands_in_that_folder()
    {
        var inner = Directory.CreateDirectory(At("inner")).FullName;

        File.WriteAllText(Path.Combine(inner, "deep.txt"), "hello");

        var pane = Pane();

        pane.SelectedEntry = Row(Path.Combine(inner, "deep.txt"));

        await pane.CompressSelectionAsync();

        Assert.True(File.Exists(Path.Combine(inner, "deep.zip")), pane.Status);
        Assert.False(File.Exists(At("deep.zip")));
    }

    /// <summary>
    /// A bin row carries a path that is no longer occupied, so the command
    /// refuses rather than acting on whatever is there now — the gate is not
    /// the only guard, because a command can be reached by other routes than
    /// its menu row.
    /// </summary>
    [AvaloniaFact]
    public async Task Compress_does_nothing_when_the_listing_is_not_a_folder()
    {
        var pane = Pane(VirtualPaths.Trash);

        pane.SelectedEntry = Row(Write("notes.txt"));

        await pane.CompressSelectionAsync();

        Assert.False(File.Exists(At("notes.zip")));
    }

    [AvaloniaFact]
    public async Task Extract_unpacks_into_a_folder_beside_the_archive()
    {
        var source = Write("notes.txt", "hello");
        var archive = Archives.Compress([source], _root);

        var pane = Pane();

        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        Assert.Equal("hello", File.ReadAllText(At(Path.Combine("notes", "notes.txt"))));
        Assert.Equal([At("notes")], _ops.Created);

        // And the listing shows the folder without waiting for the watcher —
        // the same rule as compress, which had the assertion and this did not.
        Assert.Contains(pane.Entries, e => e.Name == "notes");

        Assert.Equal("extracted 1 item(s) to notes", pane.Status);
    }

    /// <summary>
    /// **A file named .zip that is not one is the failure this verb meets
    /// most**, because the row decides by extension and never opens the file: a
    /// download that arrived as an error page, a renamed .rar, a file that
    /// stopped halfway. Measured before the sentence existed: the status bar
    /// read "End of Central Directory record could not be found." — the BCL's
    /// own words, which is the fault Failures was written to end.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_named_zip_that_is_not_one_is_refused_in_words()
    {
        var pane = Pane();

        pane.SelectedEntry = Row(Write("download.zip", "<html>404 not found</html>"));

        await pane.ExtractSelectionAsync();

        Assert.Equal("download.zip is not a zip file, or is damaged", pane.Status);

        Assert.False(Directory.Exists(At("download")));
    }

    /// <summary>The same rule as compress, from the other end: the folder goes
    /// where the archive is, not where the pane is pointed.</summary>
    [AvaloniaFact]
    public async Task An_archive_in_an_expanded_folder_unpacks_into_that_folder()
    {
        var inner = Directory.CreateDirectory(At("inner")).FullName;

        File.WriteAllText(Path.Combine(inner, "deep.txt"), "hello");

        var archive = Archives.Compress([Path.Combine(inner, "deep.txt")], inner);

        var pane = Pane();

        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        Assert.True(File.Exists(Path.Combine(inner, "deep", "deep.txt")), pane.Status);
        Assert.False(Directory.Exists(At("deep")));
    }

    /// <summary>
    /// **An archive quietly producing fewer files than it holds is exactly what
    /// somebody needs to be told.** An entry naming a path outside the folder
    /// is dropped rather than written, and the count of those reaches the
    /// status line rather than being swallowed with the entry.
    /// </summary>
    [AvaloniaFact]
    public async Task The_entries_an_extraction_refused_are_said_out_loud()
    {
        var archive = At("hostile.zip");

        using (var file = File.Create(archive))
        using (var zip = new System.IO.Compression.ZipArchive(
                   file, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("innocent.txt");
            zip.CreateEntry("../escaped.txt");
        }

        var pane = Pane();

        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        Assert.Contains("1 refused", pane.Status);
        Assert.False(File.Exists(At("escaped.txt")));
    }

    /// <summary>
    /// **Not merely harmless — silent.** Without the gate the command runs,
    /// makes a folder for the extraction, fails opening a text file as a zip
    /// and tidies the folder away again — so the only sign is a status line
    /// reporting a failure nobody asked for. The assertion is therefore on what
    /// was SAID as well as on what is on disk.
    /// </summary>
    [AvaloniaFact]
    public async Task Extract_does_nothing_when_the_selection_is_not_an_archive()
    {
        var pane = Pane();

        pane.SelectedEntry = Row(Write("notes.txt"));

        await pane.ExtractSelectionAsync();

        Assert.False(Directory.Exists(At("notes")));
        Assert.Equal("", pane.Status);
    }

    /// <summary>The other half of the gate: a bin or Recent row names a path
    /// that is no longer its own, so the verb refuses the listing as well as
    /// the file type.</summary>
    [AvaloniaFact]
    public async Task Extract_does_nothing_when_the_listing_is_not_a_folder()
    {
        var archive = Archives.Compress([Write("notes.txt")], _root);

        var pane = Pane(VirtualPaths.Trash);

        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        Assert.False(Directory.Exists(At("notes")));
    }

    // ---- the fakes ---------------------------------------------------------

    /// <summary>Lists the folder it is asked about, so a pane's rows are the
    /// files these tests actually wrote.</summary>
    private sealed class Listing : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            if (!Directory.Exists(path)) yield break;

            yield return [.. Directory.EnumerateFileSystemEntries(path).Select(Row)];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(File.Exists(path) || Directory.Exists(path)
                ? Row(path)
                : null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>Remembers what was made undoable and refuses everything else:
    /// neither verb has any business reaching the copy engine.</summary>
    private sealed class Recording : IFileOperations
    {
        public List<string> Created { get; } = [];

        private static IOperationHandle Refuse()
            => throw new InvalidOperationException("the archive verbs write for themselves");

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Refuse();

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Refuse();

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Refuse();
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Refuse();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) => Created.Add(path);

        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
