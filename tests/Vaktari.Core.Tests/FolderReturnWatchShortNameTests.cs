using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **A missing folder spelled with its 8.3 short name was never heard coming
/// back** (batch-0.11.2c QA, round 4; a regression from looking only at the
/// name on the way down). The watcher reports the long name the folder is
/// made with — TargetFolderName — and the wait compared it with TARGET~1.
/// Heard before the filter, and on every route QA tried: made, renamed in,
/// two levels made one after the other.
/// </summary>
public sealed class FolderReturnWatchShortNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-return-8dot3").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Provider : IFileSystemProvider
    {
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Given = [];

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            Given.Add((path, onChange));
            return new Off();
        }

        private sealed class Off : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;
    }

    [ShortNamesFact]
    public void A_missing_folder_spelled_with_its_short_name_is_heard_coming_back_under_its_long_one()
    {
        var spelled = Path.Combine(_root, "TARGET~1");
        var made = Path.Combine(_root, "TargetFolderName");

        // The short name is the volume's to give: check it gives this one.
        Directory.CreateDirectory(made);
        Assert.True(Directory.Exists(spelled), "the volume gave the folder another short name");
        Directory.Delete(made);

        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, spelled, () => told++);

        var (watched, heard) = Assert.Single(fs.Given);
        Assert.Equal(_root, watched);

        Directory.CreateDirectory(made);
        heard(new FileSystemChange(ChangeKind.Added, made));

        Assert.Equal(1, told);
    }

    /// <summary>The same with a folder of that name renamed into place.</summary>
    [ShortNamesFact]
    public void A_folder_renamed_in_under_its_long_name_is_heard_for_the_short_one()
    {
        var spelled = Path.Combine(_root, "TARGET~1");
        var made = Path.Combine(_root, "TargetFolderName");
        var incoming = Path.Combine(_root, "incoming");

        Directory.CreateDirectory(made);
        Assert.True(Directory.Exists(spelled), "the volume gave the folder another short name");
        Directory.Delete(made);

        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, spelled, () => told++);
        var (_, heard) = Assert.Single(fs.Given);

        Directory.CreateDirectory(incoming);
        Directory.Move(incoming, made);
        heard(new FileSystemChange(ChangeKind.Renamed, made, incoming));

        Assert.Equal(1, told);
    }
}
