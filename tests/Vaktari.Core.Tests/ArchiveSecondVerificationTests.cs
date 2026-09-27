using System.Formats.Tar;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The second independent verification of Stage A (after b995890): shapes
/// the fixes were probed with that no test yet held. Each was shown red
/// under a one-line mutation of the rule it pins and green without it.
/// </summary>
public sealed class ArchiveSecondVerificationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-verify2").FullName;

    public void Dispose()
    {
        try { Archives.DeleteTree(_root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Dir(string name) => Directory.CreateDirectory(At(name)).FullName;

    private sealed class Creates : IExtractionObserver
    {
        public int Count;
        public void BeforeCreate(string path) => Count++;
        public void WhileWriting(string temporary, string final) { }
        public void BeforeLanding(string target) { }
    }

    /// <summary>Everything a sweep must not change about a file it can
    /// reach through a link: the name, the content, the time, and the
    /// read-only bit (Windows) or the mode (Linux).</summary>
    private static string Snapshot(string folder)
        => string.Join('\n', Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => $"{Path.GetRelativePath(folder, p)} {File.GetAttributes(p) & (FileAttributes.ReadOnly | FileAttributes.Hidden)}"
                         + $" {File.GetLastWriteTimeUtc(p):O}"
                         + (OperatingSystem.IsWindows() ? "" : " " + File.GetUnixFileMode(p))
                         + (File.Exists(p) ? " " + File.ReadAllText(p) : "")));

    // ---- 1. a sweep never reaches through a link ------------------------------

    /// <summary>
    /// **A folder link inside an abandoned working folder** — a junction on
    /// Windows, a symbolic link elsewhere — pointing at a folder of files
    /// that are read-only: the sweep removes the link, and the folder behind
    /// it keeps every file, with its attributes, mode and time.
    /// </summary>
    [Fact]
    public void Sweeping_a_folder_link_leaves_the_folder_behind_it_untouched()
    {
        var outside = Dir("outside");
        var deeper = Directory.CreateDirectory(Path.Combine(outside, "deeper")).FullName;

        foreach (var (path, text) in new[] { (Path.Combine(outside, "a.txt"), "a"), (Path.Combine(deeper, "b.txt"), "b") })
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        var before = Snapshot(outside);

        var into = Dir("out");
        var abandoned = Directory.CreateDirectory(Path.Combine(into, ".vaktari-extracting-0000feedbeef", "sub")).FullName;

        TestLinks.FolderLink(Path.Combine(abandoned, "link"), outside);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(abandoned)!, DateTime.UtcNow.AddHours(-2));

        Assert.Equal(1, Archives.Sweep(into));

        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
        Assert.Equal(before, Snapshot(outside));
    }

    // ---- 2. declared sizes that pass the largest long ---------------------------

    /// <summary>
    /// **Three entries each declaring a third of long.MaxValue, plus one.**
    /// Their sum passes the largest long; summed without a check it wraps to
    /// a need every disk has room for, and a zip's declared total also turns
    /// the running floor off. Refused before anything is created.
    /// </summary>
    [Fact]
    public void Declared_sizes_whose_sum_passes_the_largest_long_create_nothing()
    {
        var each = long.MaxValue / 3 + 1;

        File.WriteAllBytes(At("sum.zip"), ZipBytes.Build(
            new ZipBytes.Entry("a.bin") { Data = "a"u8.ToArray(), Size64 = each },
            new ZipBytes.Entry("b.bin") { Data = "b"u8.ToArray(), Size64 = each },
            new ZipBytes.Entry("c.bin") { Data = "c"u8.ToArray(), Size64 = each }));

        var creates = new Creates();
        var into = Dir("out");

        Assert.ThrowsAny<Exception>(() => Archives.Extract(
            At("sum.zip"), into, null, default, new ArchiveRoom(_ => 10L << 30, _ => null), creates));

        Assert.Equal(0, creates.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    // ---- the depth cap, on its own -----------------------------------------------

    /// <summary>
    /// **600 one-letter folders: past the depth cap, well inside any path
    /// limit.** The ten-thousand-deep test is also far past Linux's 4,000-byte
    /// path limit, so on Linux the length rule caught it with the cap
    /// switched off (a revert-check stayed green in Fedora). This one is
    /// 1,200 characters, so only the cap can leave it out.
    /// </summary>
    [Fact]
    public void An_entry_six_hundred_short_folders_deep_is_left_out_on_every_system()
    {
        var deep = string.Concat(Enumerable.Repeat("d/", 600)) + "deep.txt";
        var archive = ArchiveTestData.Zip(At("deep.zip"), ("top/a.txt", "a"), ("top/" + deep, "deep"));

        var done = Archives.Extract(archive, Dir("out"));

        Assert.Equal(1, done.LeftOut.Unwritable);
        Assert.Equal(["a.txt"], Directory.EnumerateFileSystemEntries(done.Landed).Select(Path.GetFileName));
    }

    // ---- 5. both readers read the same bytes -------------------------------------

    /// <summary>
    /// **Two whole zips back to back**, each with offsets counted from its
    /// own start: the directory found is the second's, so the shift is the
    /// first zip's length, and what lands must be the second zip's bytes —
    /// read by the decoder from the same place the directory points.
    /// </summary>
    [Fact]
    public void Of_two_zips_back_to_back_the_one_whose_directory_is_read_is_the_one_extracted()
    {
        byte[] bytes =
        [
            .. ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "first zip"u8.ToArray() }),
            .. ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "second zip"u8.ToArray() }),
        ];

        File.WriteAllBytes(At("two.zip"), bytes);

        var done = Archives.Extract(At("two.zip"), Dir("out"));

        Assert.Equal("second zip", File.ReadAllText(Path.Combine(done.Landed, "a.txt")));
    }

    /// <summary>
    /// **A whole zip hidden in another's end-record comment**, its offsets
    /// counted from where the comment starts. The last end record in the
    /// file is the hidden zip's, so that is the directory read, with a shift
    /// of everything in front of it; the decoder must read that same
    /// directory's entry, not the outer zip's at the same offset.
    /// </summary>
    [Fact]
    public void A_zip_hidden_in_an_end_record_comment_is_read_by_both_readers_alike()
    {
        var outer = ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "outer bytes"u8.ToArray() });
        var inner = ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "inner bytes"u8.ToArray() });

        BitConverter.GetBytes((ushort)inner.Length).CopyTo(outer, outer.Length - 2);

        File.WriteAllBytes(At("comment.zip"), [.. outer, .. inner]);

        var directory = ZipDirectory.Read(new MemoryStream([.. outer, .. inner]));
        var done = Archives.Extract(At("comment.zip"), Dir("out"));

        Assert.Equal(outer.Length, directory.Shift);
        Assert.Equal("inner bytes", File.ReadAllText(Path.Combine(done.Landed, "a.txt")));
    }

    // ---- 6. nothing written lands nothing ------------------------------------------

    /// <summary>
    /// What each archive that holds only one thing comes to: a folder lands
    /// as that folder with nothing left out; a device, a link, or a link in a
    /// folder only its own path implies lands nothing and says what was left
    /// out — no empty folder named after the archive or after the implied one.
    /// </summary>
    [Fact]
    public void An_archive_holding_one_thing_lands_it_or_says_why_nothing_landed()
    {
        var folder = At("folder.zip");
        File.WriteAllBytes(folder, ZipBytes.Build(new ZipBytes.Entry("d/")));

        var done = Archives.Extract(folder, Dir("out-folder"));

        Assert.Equal(At("out-folder", "d"), done.Landed);
        Assert.Equal(0, done.LeftOut.Total);

        var device = ArchiveTestData.Tar(At("device.tar"), t =>
            t.WriteEntry(new PaxTarEntry(TarEntryType.CharacterDevice, "top/null") { DeviceMajor = 1, DeviceMinor = 3 }));

        var link = At("link.zip");
        File.WriteAllBytes(link, ZipBytes.Build(new ZipBytes.Entry("d/link")
        {
            Data = "../../etc/passwd"u8.ToArray(),
            MadeBy = 3 << 8 | 20,
            External = 0xA1FFu << 16,
        }));

        foreach (var (archive, why) in new[] { (device, "1 special file"), (link, "1 link") })
        {
            var into = Dir("out-" + Path.GetFileNameWithoutExtension(archive));

            var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(archive, into));

            Assert.Equal($"nothing in {Path.GetFileName(archive)} could be written: 1 left out ({why})", refused.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(into));
        }
    }
}
