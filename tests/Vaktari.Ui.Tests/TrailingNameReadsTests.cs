using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Vcs;
using Vaktari.Ui.Input;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **What the pane reads, opens in place or moves for a name Win32 folds.**
/// From the seventh review round's hunt: expanding "album " in place spliced
/// in "album"'s children, and Shift+Delete on one deleted the neighbour's file
/// (H3); the preview showed "report"'s text as "report "'s (H4); a drop out of
/// an archiver's temp folder moved the neighbour into staging (H7); the
/// properties window hashed and sized the neighbour; and, found beside them,
/// the crumb menu and the folder tree listed a folded folder's neighbour, a
/// recent file took its neighbour's size, a thumbnail would have been drawn
/// from the neighbour, and git was run in the neighbour's folder.
///
/// A read goes through the spelling that reaches the row itself — "\\?\" — or
/// shows nothing; an expansion, a crumb or a tree node of such a folder is
/// refused the way the listing is, in the same words. The neighbour is always
/// there, different, and untouched at the end. All in a temporary folder, the
/// trailing names made through "\\?\"; the Windows listing and engine are the
/// real ones, by name, since this assembly also builds on Linux.
/// </summary>
public sealed class TrailingNameReadsTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-readsfold").FullName;

    private readonly IThumbnailProvider? _thumbnailsBefore = ThumbnailLoader.Provider;
    private readonly IVersionControl? _vcsBefore = PaneViewModel.Vcs;

    public override void Dispose()
    {
        base.Dispose();

        ThumbnailLoader.Provider = _thumbnailsBefore;
        PaneViewModel.Vcs = _vcsBefore;

        try
        {
            var root = Raw(_root);

            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(f);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    private static string Raw(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;

    private string File_(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Raw(Path.GetDirectoryName(path)!));
        File.WriteAllText(Raw(path), content);
        return path;
    }

    private string Folder(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Raw(path));
        return path;
    }

    private static T Windows<T>(string type)
        => (T)Activator.CreateInstance(Type.GetType($"Vaktari.Windows.{type}, Vaktari.Windows", throwOnError: true)!)!;

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), what);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(2);
        }
    }

    private async Task<PaneViewModel> Pane(string folder, int rows)
    {
        var pane = Own(new PaneViewModel(
            Windows<IFileSystemProvider>("WindowsFileSystemProvider"),
            Windows<IFileOperations>("WindowsFileOperations")) { ViewportWidth = 1400 });

        await pane.NavigateAsync(folder);
        await Until(() => pane.Entries.Count == rows, $"{folder} did not list its {rows} rows");

        return pane;
    }

    // ---- expanding a folder in place (H3) ---------------------------------------

    /// <summary>
    /// The hunt's steps: "album" holding a file, "album " beside it, expand
    /// "album ". Its children were album's, with album's paths, and deleting
    /// the one shown under "album " deleted album's file. Refused now, in the
    /// listing's own words, and nothing is spliced in.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folded_folder_is_not_opened_in_place_as_its_neighbour()
    {
        File_(Path.Combine("album", "neighbours-child.txt"), "N");
        File_(Path.Combine("album ", "own-child.txt"), "T");

        var pane = await Pane(_root, 2);
        var row = pane.Entries.Single(e => e.Name == "album ");

        Assert.True(pane.CanExpandRows);

        await pane.ToggleExpandAsync(row);
        await Settle();

        Assert.Equal(["album", "album "], pane.DetailsEntries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Contains("\"album \" ends with a space", pane.Status, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "album", "neighbours-child.txt")));
    }

    /// <summary>The control, and the rest of the route: an ordinary folder
    /// still opens, and a child in it whose own name folds is refused by the
    /// engine exactly as a listed row is.</summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folded_child_of_an_ordinary_folder_is_refused_like_any_row()
    {
        File_(Path.Combine("album", "x"), "NEIGHBOUR");
        File_(Path.Combine("album", "x..."), "TRAILING");

        var pane = await Pane(_root, 1);

        await pane.ToggleExpandAsync(pane.Entries.Single());
        await Until(() => pane.DetailsEntries.Count() == 3, "album did not open in place");

        var started = new List<IOperationHandle>();
        pane.OperationStarted += (_, h) => started.Add(h);

        pane.SelectedEntry = pane.DetailsEntries.Single(e => e.Name == "x...");
        pane.DeleteSelected();

        var delete = Assert.Single(started);
        await delete.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Windows cannot open it by name", Assert.Single(delete.Problems).Error.Message, StringComparison.Ordinal);
        Assert.Equal("NEIGHBOUR", File.ReadAllText(Path.Combine(_root, "album", "x")));
    }

    // ---- the preview (H4) --------------------------------------------------------------

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("report ", "report")]
    [InlineData("notes.", "notes")]
    public async Task The_preview_shows_a_folded_row_its_own_text(string name, string neighbour)
    {
        File_(neighbour, "NEIGHBOUR-SECRET-TEXT");
        File_(name, "its own text");

        var pane = await Pane(_root, 2);

        pane.SelectedEntry = pane.Entries.Single(e => e.Name == name);
        pane.TogglePreview();

        await Until(() => pane.PreviewText.Length > 0, "nothing was previewed");

        Assert.Equal("its own text", pane.PreviewText);
    }

    // ---- a drop out of an archiver's temp folder (H7) ---------------------------------

    [WindowsFact]
    public void A_folded_drop_is_not_rescued_as_its_neighbour()
    {
        var temp = Folder("temp");
        var neighbour = File_(Path.Combine("temp", "7zO1", "report"), "NEIGHBOUR");
        var dropped = File_(Path.Combine("temp", "7zO1", "report "), "TRAILING");
        var staging = Path.Combine(temp, "staging");

        var staged = DropStaging.Rescue([dropped], temp, staging);

        Assert.Equal([dropped], staged.Paths);
        Assert.False(staged.Rescued);
        Assert.Equal("NEIGHBOUR", File.ReadAllText(neighbour));
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    /// <summary>The copy fallback, for a dropped folder that cannot be renamed
    /// away: a name inside it that folds — a file or a folder — abandons the
    /// rescue of that folder rather than copying the neighbour in its place.</summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("x...")]
    [InlineData("sub.")]
    public void A_drop_holding_a_folded_name_is_not_copied_as_its_neighbour(string name)
    {
        var temp = Folder("temp");
        var dropped = Folder(Path.Combine("temp", "7zO2", "album"));
        var folder = !name.StartsWith('x');

        File_(Path.Combine(dropped, "keep.txt"), "held");

        // An empty one: a folder with files in it is also refused through
        // its children's paths, which carry its name; an empty one has none,
        // and was made in staging as "sub".
        if (folder)
        {
            Folder(Path.Combine(dropped, name));
            Folder(Path.Combine(dropped, name.TrimEnd('.')));
        }
        else
        {
            File_(Path.Combine(dropped, "x"), "NEIGHBOUR");
            File_(Path.Combine(dropped, name), "TRAILING");
        }

        // Held open, so the rename refuses and the copy is what is tried.
        using var held = new FileStream(Path.Combine(dropped, "keep.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var staged = DropStaging.Rescue([dropped], temp, Path.Combine(temp, "staging"));

        Assert.Equal([dropped], staged.Paths);
    }

    // ---- a folder row's papers --------------------------------------------------------------

    /// <summary>
    /// A drawn folder shows papers when there is something in it, and "album "
    /// was asked as "album": an empty folder beside a full one of that name was
    /// drawn full. Asked of the folder itself now.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folded_folder_row_is_not_drawn_with_its_neighbours_papers()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var full = File_(Path.Combine("album" + suffix, "n.txt"), "N");
        var empty = Folder("album" + suffix + " ");
        var painted = new List<string>();

        FileEntry Row(string path) => new(Path.GetFileName(path), path, 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

        await RowIcon.ShowContentsIfAnyAsync(Row(empty), _ => painted.Add(empty), CancellationToken.None);
        await RowIcon.ShowContentsIfAnyAsync(Row(Path.GetDirectoryName(full)!), _ => painted.Add(full), CancellationToken.None);

        Assert.Equal([full], painted);
    }

    // ---- the properties window ------------------------------------------------------------

    private sealed class Details : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                Kind = "File",
                IsDirectory = false,
            });

        public ValueTask<SizeProgress> MeasureAsync(string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(0, 0, 1));

        public bool ShowSystemDialog(string path) => false;
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task The_properties_window_hashes_a_folded_row_itself()
    {
        File_("report", "a much longer neighbour, forty bytes long");
        var row = File_("report ", "12345");

        var model = new PropertiesViewModel(new Details(), [row]);

        await model.ComputeChecksumsCommand.ExecuteAsync(null);
        await Until(() => model.Sha256.Length > 0, "nothing was hashed");

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("12345"))), model.Sha256.ToLowerInvariant());
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task The_properties_window_sizes_a_folded_row_itself()
    {
        File_("report", "a much longer neighbour, forty bytes long");
        var row = File_("report ", "12345");
        var other = File_("other.txt", "abc");

        var model = new PropertiesViewModel(new Details(), [row, other]);

        await model.LoadAsync();
        await Until(() => model.SizeText.Length > 0, "no size was shown");

        Assert.Equal($"{ByteSize.Format(8)} in 2 file(s)", model.SizeText);
    }

    /// <summary>The measure a selection holding a folder starts by itself
    /// counts the loose files too — each by its own name.</summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task The_properties_window_measures_a_folded_row_itself()
    {
        File_("report", "a much longer neighbour, forty bytes long");
        var row = File_("report ", "12345");
        var folder = Folder("album");

        var model = new PropertiesViewModel(new Details(), [row, folder]);

        await model.LoadAsync();
        await Until(() => !model.IsMeasuring && model.SizeText.Contains(" folders", StringComparison.Ordinal),
                    "the measure did not finish");

        Assert.Equal($"{ByteSize.Format(5)} · 1 files · 1 folders", model.SizeText);
    }

    // ---- the crumb menu, and the folder tree ------------------------------------------------

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folded_crumb_does_not_list_its_neighbours_folders()
    {
        Folder(Path.Combine("album", "neighbours-folder"));
        Folder(Path.Combine("album ", "inner"));

        var pane = Own(new PaneViewModel(Windows<IFileSystemProvider>("WindowsFileSystemProvider")) { ViewportWidth = 1400 });
        await pane.NavigateAsync(Path.Combine(_root, "album ", "inner"));
        await Settle();

        var crumb = Assert.Single(pane.Breadcrumbs, c => c.FullPath == Path.Combine(_root, "album "));

        crumb.Menu!.Execute(null);
        await Settle();

        Assert.Equal(["could not read this folder"], crumb.Children.Select(c => c.Name));
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folded_tree_node_does_not_open_as_its_neighbour()
    {
        Folder(Path.Combine("album", "neighbours-folder"));
        Folder(Path.Combine("album ", "own-folder"));

        var tree = new FolderTreeViewModel(Windows<IFileSystemProvider>("WindowsFileSystemProvider"));
        tree.SetRoots([(Path.Combine(_root, "album "), "album ")]);

        await tree.Roots[0].EnsureOpenAsync();

        Assert.Empty(tree.Roots[0].Children);
        Assert.True(tree.Roots[0].IsUnreadable);
    }

    // ---- the recent listing -------------------------------------------------------------------

    private sealed class Remembered(params string[] paths) : IRecentStore
    {
        public void Record(string path, RecentKind kind) { }

        public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count)
            => kind == RecentKind.File ? [.. paths.Select(p => new RecentEntry(p, kind, DateTimeOffset.Now))] : [];

        public void Forget(string path) { }
        public int Count => paths.Length;
        public int ForgetAll() => 0;
        public event EventHandler? Changed { add { } remove { } }
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_remembered_folded_file_is_listed_with_its_own_size_or_not_at_all()
    {
        File_("report", "a much longer neighbour, forty bytes long");
        File_("notes", "the neighbour of a file that has gone");
        Folder("album");
        var kept = File_("report ", "12345");
        var gone = Path.Combine(_root, "notes ");
        var goneFolder = Path.Combine(_root, "album ");

        var rows = new List<FileEntry>();

        await foreach (var batch in RecentListing.EnumerateAsync(new Remembered(kept, gone, goneFolder), VirtualPaths.Files))
            rows.AddRange(batch);

        var row = Assert.Single(rows);
        Assert.Equal(kept, row.FullPath);
        Assert.Equal(5, row.Length);
    }

    // ---- a thumbnail ------------------------------------------------------------------------------

    private sealed class Pictures : IThumbnailProvider
    {
        public List<string> Asked { get; } = [];

        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
        {
            Asked.Add(path);
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
        {
            Asked.Add(path);
            return ValueTask.FromResult<IconPixels?>(new IconPixels(size, size, new byte[size * size * 4]));
        }
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task No_thumbnail_is_drawn_from_a_folded_name()
    {
        var pictures = new Pictures();
        ThumbnailLoader.Provider = pictures;

        var name = "photo-" + Guid.NewGuid().ToString("N")[..8] + ".heic";
        var folded = File_(Path.Combine("sub ", name), "its own");
        var plain = File_(Path.Combine("sub", name), "the neighbour");

        Assert.False(ThumbnailLoader.CanThumbnail(folded));
        Assert.Null(await ThumbnailLoader.LoadAsync(folded, 64, CancellationToken.None));
        Assert.Empty(pictures.Asked);

        // The control: the same question of an ordinary path is asked.
        Assert.True(ThumbnailLoader.CanThumbnail(plain));
        Assert.NotNull(await ThumbnailLoader.LoadAsync(plain, 64, CancellationToken.None));
        Assert.NotEmpty(pictures.Asked);
    }

    // ---- git ------------------------------------------------------------------------------------------

    private sealed class Repository : IVersionControl
    {
        public List<string> Asked { get; } = [];

        public string Name => "recording";
        public bool IsAvailable => true;

        public string? FindRoot(string folder)
        {
            lock (Asked) Asked.Add(folder);
            return null;
        }

        public Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
        {
            lock (Asked) Asked.Add(folder);
            return Task.FromResult<VcsSnapshot?>(null);
        }
    }

    /// <summary>
    /// A pin rather than a fix: a refused listing never reaches the git
    /// refresh, which runs from a finished load and the repository's own
    /// watch. Measured — a guard added in RefreshVcsAsync reddened nothing
    /// when removed, so it was taken out again.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Git_is_not_asked_about_a_folded_folder()
    {
        Folder("album");
        var folded = Folder("album ");
        var repository = new Repository();
        PaneViewModel.Vcs = repository;

        var pane = Own(new PaneViewModel(Windows<IFileSystemProvider>("WindowsFileSystemProvider")) { ViewportWidth = 1400 });

        await pane.NavigateAsync(folded);
        await Settle();
        await Task.Delay(200);

        lock (repository.Asked) Assert.Empty(repository.Asked);

        // The control: the folder beside it is asked about.
        await pane.NavigateAsync(Path.Combine(_root, "album"));
        await Until(() => { lock (repository.Asked) return repository.Asked.Count > 0; }, "git was never asked about the ordinary folder");
    }
}
