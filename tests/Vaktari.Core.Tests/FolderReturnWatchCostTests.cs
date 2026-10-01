using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What an arrival in the folder watched costs a wait, and the second look's
/// last edge (batch-0.11.2e QA, round 6).
///
/// Every arrival and every change in the folder watched reaches every wait
/// there, on the watcher's one shared thread. Hearing Changed doubled them,
/// and a full path compare on each — two normalised copies — let the reader
/// fall behind a busy folder again on Linux. The busy-folder test over the
/// real watcher only caught that by timing, and only when run alone; these
/// count the work instead, so they hold wherever they run.
/// </summary>
public sealed class FolderReturnWatchCostTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-cost").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Provider : IFileSystemProvider
    {
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Given = [];

        /// <summary>Run as a watch is asked for; true refuses it.</summary>
        public Func<string, bool>? Refuse { get; set; }

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (Refuse?.Invoke(path) == true) throw new DirectoryNotFoundException(path);

            lock (Given) Given.Add((path, onChange));
            return new Off();
        }

        public Action<FileSystemChange> Last
        {
            get { lock (Given) return Given[^1].Heard; }
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
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    /// <summary>
    /// **An arrival of another name is a name compared in place, and nothing
    /// more.** Added, Changed and Renamed of names of every length — longer,
    /// shorter, the same length, one letter off — never reach the full path
    /// compare; the next step's own name does.
    /// </summary>
    [Fact]
    public void Other_names_never_reach_the_full_compare()
    {
        var target = Path.Combine(_root, "missing0", "x");
        var fs = new Provider();

        using var wait = new FolderReturnWatch(fs, target, () => { }, null, Directory.Exists, TimeSpan.FromHours(1));
        Assert.Equal(_root, wait.Watching);

        var heard = fs.Last;

        foreach (var name in new[] { "f00000", "missing1", "missing01", "missing", "issing0", "x", "missing0.tmp" })
        {
            var other = Path.Combine(_root, name);
            heard(new FileSystemChange(ChangeKind.Added, other));
            heard(new FileSystemChange(ChangeKind.Changed, other));
            heard(new FileSystemChange(ChangeKind.Renamed, other, Path.Combine(_root, "was")));
        }

        Assert.Equal(0, wait.FullCompares);
        Assert.Equal(_root, wait.Watching);

        var step = Directory.CreateDirectory(Path.Combine(_root, "missing0")).FullName;
        heard(new FileSystemChange(ChangeKind.Changed, step));

        Assert.Equal(1, wait.FullCompares);
        Assert.Equal(step, wait.Watching);
    }

    /// <summary>
    /// **A folder first found, refused because it went, and made again before
    /// the watch above was set up, is looked at again** (batch-0.11.2e QA,
    /// round 6, traced on Linux). The second look compared only with the
    /// folder first found — the same path — and the wait sat at the root
    /// hearing nothing made inside a until the slow look, 30 s later.
    /// </summary>
    [Fact]
    public void A_folder_refused_as_it_went_and_made_again_at_once_is_watched()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var target = Path.Combine(a, "b", "c");
        var refused = false;

        var fs = new Provider
        {
            Refuse = path =>
            {
                if (refused || path != a) return false;

                // a goes as its watch is asked for, and is refused.
                refused = true;
                Directory.Delete(a);
                return true;
            },
        };

        var made = false;

        using var wait = new FolderReturnWatch(fs, target, () => { }, TimeSpan.FromHours(1),
            path =>
            {
                // a is made again while the wait, refused there, is finding
                // where to watch instead: the first time the root is asked
                // about after the refusal, so after the walk has passed a.
                if (refused && !made && path == _root)
                {
                    made = true;
                    Directory.CreateDirectory(a);
                }

                return Directory.Exists(path);
            }, TimeSpan.FromHours(1));

        Assert.True(refused, "a was never refused");
        Assert.True(made, "a was never made again");
        Assert.Equal(a, wait.Watching);
    }

    [Theory]
    [InlineData("TargetFolderName", "TARGET~1", true)]
    [InlineData("Target Folder", "TARGET~1", true)]
    [InlineData("target.folder.name", "TARGET~1", true)]
    [InlineData(".config", "CONFIG~1", true)]
    [InlineData("download.part", "TARGET~1", false)]
    [InlineData("tar", "TARGET~1", false)]
    [InlineData("TargetFolderName", "TARGET~12", true)]
    // Hashed: only the first two characters are the name's.
    [InlineData("TaxReturns2024", "TAEAE4~1", true)]
    [InlineData("Other", "TAEAE4~1", false)]
    // A stem with a character the volume could not keep: trusted up to it.
    [InlineData("Ab+cdefgh", "AB_CDE~1", true)]
    [InlineData("Xb+cdefgh", "AB_CDE~1", false)]
    // No tilde: nothing to go by.
    [InlineData("anything", "PLAIN", true)]
    public void A_name_could_carry_an_alias_only_if_it_begins_with_its_safe_stem(string name, string alias, bool could)
    {
        Assert.Equal(could, FolderReturnWatch.MayBeAliasOf(name, alias));
    }

    /// <summary>
    /// **With a short next step, other arrivals do not ask the volume**
    /// (batch-0.11.2e QA, round 6: one GetShortPathNameW per arrival per wait
    /// still flooded a busy folder). Only a name that could carry the alias is
    /// asked for its short name, and the folder's own arrival is still heard.
    /// </summary>
    [ShortNamesFact]
    public void With_a_short_next_step_other_names_do_not_ask_the_volume()
    {
        var spelled = Path.Combine(_root, "TARGET~1");
        var made = Path.Combine(_root, "TargetFolderName");

        Directory.CreateDirectory(made);
        Assert.True(Directory.Exists(spelled), "the volume gave the folder another short name");
        Directory.Delete(made);

        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, spelled, () => told++, null, Directory.Exists, TimeSpan.FromHours(1));
        var heard = fs.Last;

        for (var i = 0; i < 50; i++)
        {
            var other = Path.Combine(_root, $"f{i:D5}");
            File.WriteAllText(other, "x");
            heard(new FileSystemChange(ChangeKind.Added, other));
            heard(new FileSystemChange(ChangeKind.Changed, other));
        }

        Assert.Equal(0, wait.ShortLookups);
        Assert.Equal(0, told);

        Directory.CreateDirectory(made);
        heard(new FileSystemChange(ChangeKind.Added, made));

        Assert.Equal(1, wait.ShortLookups);
        Assert.Equal(1, told);
    }
}
