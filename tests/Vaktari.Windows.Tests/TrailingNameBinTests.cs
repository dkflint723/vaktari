using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The bin and a name that ends in a space or a dot.** From the seventh
/// review round's hunt: an item binned as "report " — by WSL, by a Linux share,
/// by anything that reaches such a name — came back as "report" or "report (1)",
/// because the recorded path had its whitespace trimmed and the move folded
/// the target; a binned folder holding "x..." could never be purged, because
/// the tree delete refuses a tree it would read by the wrong names; and
/// "Delete for good" of one item ignored the purge failing and said it had
/// deleted it.
///
/// None of this touches the real Recycle Bin. A bin entry is a "$I" metadata
/// file and a "$R" payload beside it, and both are made here in a temporary
/// folder: WindowsTrashMaintenance.Delete and Restore take the metadata path
/// as the item's key, and read from wherever it is.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingNameBinTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-binfold").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }

            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    /// <summary>A version 2 record, the shape Windows 10 and later write.</summary>
    private static byte[] Version2(string path, long size, DateTimeOffset deleted)
    {
        var chars = Encoding.Unicode.GetBytes(path);
        var bytes = new byte[28 + chars.Length + 2];

        BitConverter.TryWriteBytes(bytes.AsSpan(0), 2L);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), deleted.ToFileTime());
        BitConverter.TryWriteBytes(bytes.AsSpan(24), path.Length + 1);
        chars.CopyTo(bytes, 28);

        return bytes;
    }

    /// <summary>A bin entry for <paramref name="original"/> in a folder of this
    /// test's own: the "$I" file, and the "$R" payload beside it.</summary>
    private (string Info, string Payload) Binned(string original, bool directory)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var id = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var info = Path.Combine(bin, "$I" + id);
        var payload = Path.Combine(bin, "$R" + id);

        File.WriteAllBytes(info, Version2(original, 5, DateTimeOffset.Now));

        if (directory) Directory.CreateDirectory(payload);
        else File.WriteAllText(payload, "OWN-BYTES");

        return (info, payload);
    }

    [WindowsTheory]
    [InlineData(@"C:\work\report ")]
    [InlineData(@"C:\work\report.")]
    public void The_recorded_path_is_read_exactly_as_recorded(string recorded)
    {
        Assert.True(RecycleBin.TryParse(Version2(recorded, 5, DateTimeOffset.Now), out var original, out _, out _));

        Assert.Equal(recorded, original);
    }

    [WindowsFact]
    public void An_item_binned_as_report_space_comes_back_as_itself_beside_report()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        File.WriteAllText(Path.Combine(work, "report"), "NEIGHBOUR");

        var original = Path.Combine(work, "report ");
        var (info, _) = Binned(original, directory: false);

        var landed = new WindowsTrashMaintenance().Restore(info);

        Assert.Equal(original, landed);
        Assert.Equal("OWN-BYTES", File.ReadAllText(@"\\?\" + original));
        Assert.Equal("NEIGHBOUR", File.ReadAllText(Path.Combine(work, "report")));
        Assert.Equal(["report", "report "], Directory.GetFiles(@"\\?\" + work).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// **A binned FOLDER "album " stayed in the bin.** The landing is spelled
    /// "\\?\" and the payload was not, and Directory.Move refused the pair —
    /// "Source and destination path must have identical roots" (fix-8
    /// verification). It comes back as itself, contents and all, beside an
    /// "album" that is left exactly as it was.
    /// </summary>
    [WindowsTheory]
    [InlineData("album ")]
    [InlineData("album.")]
    public void A_folder_binned_as_album_space_comes_back_as_itself_beside_album(string name)
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, "album"));
        File.WriteAllText(Path.Combine(work, "album", "neighbours.txt"), "NEIGHBOUR");

        var original = Path.Combine(work, name);
        var (info, payload) = Binned(original, directory: true);
        File.WriteAllText(Path.Combine(payload, "own.txt"), "OWN");
        Directory.CreateDirectory(Path.Combine(payload, "inner"));

        var landed = new WindowsTrashMaintenance().Restore(info);

        Assert.Equal(original, landed);
        Assert.Equal("OWN", File.ReadAllText(@"\\?\" + Path.Combine(original, "own.txt")));
        Assert.True(Directory.Exists(@"\\?\" + Path.Combine(original, "inner")));
        Assert.Equal(["neighbours.txt"], Directory.GetFileSystemEntries(Path.Combine(work, "album")).Select(Path.GetFileName));
        Assert.Equal("NEIGHBOUR", File.ReadAllText(Path.Combine(work, "album", "neighbours.txt")));
        Assert.False(Directory.Exists(payload));
        Assert.False(File.Exists(info));
    }

    /// <summary>
    /// **The bin's own names were folded too.** A pair named "$I…txt " and
    /// "$R…txt " beside "$I…txt" and "$R…txt" was read as its neighbour, so
    /// one item listed twice and the other not at all (fix-8 verification).
    /// Read through "\\?\", each pair is its own item; restored, it comes back
    /// with its own bytes and leaves the other pair in the bin.
    /// </summary>
    [WindowsFact]
    public void A_bin_pair_whose_own_name_folds_is_read_as_itself()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;

        File.WriteAllBytes(Path.Combine(bin, "$IABC123.txt"), Version2(Path.Combine(work, "neighbour.txt"), 9, DateTimeOffset.Now));
        File.WriteAllText(Path.Combine(bin, "$RABC123.txt"), "NEIGHBOUR");
        File.WriteAllBytes(@"\\?\" + Path.Combine(bin, "$IABC123.txt "), Version2(Path.Combine(work, "own.txt"), 3, DateTimeOffset.Now));
        File.WriteAllText(@"\\?\" + Path.Combine(bin, "$RABC123.txt "), "OWN");

        var entries = RecycleBin.InfoFiles(bin).Select(RecycleBin.Read).OfType<RecycleEntry>().ToList();

        Assert.Equal(
            [Path.Combine(work, "neighbour.txt"), Path.Combine(work, "own.txt")],
            entries.Select(e => e.OriginalPath).Order(StringComparer.Ordinal));

        var own = entries.Single(e => e.OriginalPath.EndsWith("own.txt", StringComparison.Ordinal));

        Assert.Equal(Path.Combine(work, "own.txt"), new WindowsTrashMaintenance().Restore(own.InfoPath));
        Assert.Equal("OWN", File.ReadAllText(Path.Combine(work, "own.txt")));
        Assert.Equal("NEIGHBOUR", File.ReadAllText(Path.Combine(bin, "$RABC123.txt")));
        Assert.True(File.Exists(Path.Combine(bin, "$IABC123.txt")));
    }

    /// <summary>
    /// The same pair as a folder, going back to an ordinary name: the payload
    /// is read through "\\?\" and the landing is not, and Directory.Move wants
    /// the two in one spelling.
    /// </summary>
    [WindowsFact]
    public void A_folder_pair_whose_own_name_folds_is_restored_to_an_ordinary_name()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;

        File.WriteAllBytes(Path.Combine(bin, "$IDEF456"), Version2(Path.Combine(work, "neighbour"), 0, DateTimeOffset.Now));
        Directory.CreateDirectory(Path.Combine(bin, "$RDEF456"));
        File.WriteAllBytes(@"\\?\" + Path.Combine(bin, "$IDEF456."), Version2(Path.Combine(work, "photos"), 0, DateTimeOffset.Now));
        Directory.CreateDirectory(@"\\?\" + Path.Combine(bin, "$RDEF456."));
        File.WriteAllText(@"\\?\" + Path.Combine(bin, "$RDEF456.", "own.txt"), "OWN");

        var landed = new WindowsTrashMaintenance().Restore(Path.Combine(bin, "$IDEF456."));

        Assert.Equal(Path.Combine(work, "photos"), landed);
        Assert.Equal("OWN", File.ReadAllText(Path.Combine(work, "photos", "own.txt")));
        Assert.True(Directory.Exists(Path.Combine(bin, "$RDEF456")));
        Assert.True(File.Exists(Path.Combine(bin, "$IDEF456")));
    }

    /// <summary>
    /// **A second "album " is numbered beside the first, and keeps what is in
    /// it.** The landing is taken, so the name gains a number and no longer
    /// ends in a space, while the payload is still moved through "\\?\" — the
    /// inner "x..." and "inner " arrive as themselves (fix-9 verification).
    /// </summary>
    [WindowsFact]
    public void A_second_folder_binned_as_album_space_is_numbered_beside_the_first_with_its_contents()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, "album"));
        var original = Path.Combine(work, "album ");

        var restore = new WindowsTrashMaintenance();

        var (first, _) = Binned(original, directory: true);
        Assert.Equal(original, restore.Restore(first));

        var (second, payload) = Binned(original, directory: true);
        File.WriteAllText(@"\\?\" + Path.Combine(payload, "x..."), "TRAIL");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(payload, "inner "));
        File.WriteAllText(@"\\?\" + Path.Combine(payload, "inner ", "deep."), "DEEP");

        var landed = restore.Restore(second);

        Assert.Equal(Path.Combine(work, "album  (1)"), landed);
        Assert.Equal("TRAIL", File.ReadAllText(@"\\?\" + Path.Combine(landed, "x...")));
        Assert.Equal("DEEP", File.ReadAllText(@"\\?\" + Path.Combine(landed, "inner ", "deep.")));
        Assert.Empty(Directory.GetFileSystemEntries(@"\\?\" + original));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(work, "album")));
    }

    /// <summary>
    /// **A folder whose recorded path is itself spelled "\\?\" stayed in the
    /// bin**, an ordinary name included: the landing was extended and the "$R"
    /// payload was not, and Directory.Move refused the pair for having two
    /// roots (fix-9 verification; main threw for both names below).
    /// </summary>
    [WindowsTheory]
    [InlineData("plain")]
    [InlineData("album ")]
    public void A_folder_recorded_through_the_extended_prefix_comes_back(string name)
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, "album"));

        var recorded = @"\\?\" + Path.Combine(work, name);
        var (info, payload) = Binned(recorded, directory: true);
        File.WriteAllText(Path.Combine(payload, "own.txt"), "OWN");

        var landed = new WindowsTrashMaintenance().Restore(info);

        Assert.Equal(recorded, landed);
        Assert.Equal("OWN", File.ReadAllText(Path.Combine(recorded, "own.txt")));
        Assert.False(Directory.Exists(payload));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(work, "album")));
    }

    /// <summary>
    /// A folder whose PARENT's name folds lands in that parent, made through
    /// "\\?\" when it is not there, and not in the neighbour "dir" beside it.
    /// </summary>
    [WindowsFact]
    public void A_folder_under_a_folded_parent_lands_there_and_not_in_its_neighbour()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, "dir"));
        File.WriteAllText(Path.Combine(work, "dir", "neighbour.txt"), "NEIGHBOUR");

        var original = Path.Combine(work, "dir ", "album");
        var (info, payload) = Binned(original, directory: true);
        File.WriteAllText(Path.Combine(payload, "own.txt"), "OWN");

        Assert.Equal(original, new WindowsTrashMaintenance().Restore(info));

        Assert.Equal("OWN", File.ReadAllText(@"\\?\" + Path.Combine(original, "own.txt")));
        Assert.Equal(["neighbour.txt"], Directory.GetFileSystemEntries(Path.Combine(work, "dir")).Select(Path.GetFileName));
    }

    /// <summary>
    /// **Delete for good of a pair whose own names fold took its neighbour's
    /// metadata.** "$I…." was read as "$I…" and deleted as it too, so the
    /// neighbour item lost its record and the pair asked for stayed half there
    /// (fix-9 verification, measured on main). Now exactly that pair goes, by
    /// the key the bin's walk hands out, and the neighbour pair is untouched.
    /// </summary>
    [WindowsTheory]
    [InlineData(".", true)]
    [InlineData(".txt ", false)]
    public void Delete_for_good_of_a_pair_whose_own_name_folds_leaves_the_neighbour_pair(string suffix, bool directory)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var plainSuffix = suffix.TrimEnd(' ', '.');

        var neighbourInfo = Path.Combine(bin, "$IDEL001" + plainSuffix);
        var neighbourPayload = Path.Combine(bin, "$RDEL001" + plainSuffix);
        File.WriteAllBytes(neighbourInfo, Version2(Path.Combine(_root, "neighbour"), 9, DateTimeOffset.Now));
        if (directory) File.WriteAllText(Path.Combine(Directory.CreateDirectory(neighbourPayload).FullName, "n.txt"), "NEIGHBOUR");
        else File.WriteAllText(neighbourPayload, "NEIGHBOUR");

        var ownInfo = Path.Combine(bin, "$IDEL001" + suffix);
        var ownPayload = @"\\?\" + Path.Combine(bin, "$RDEL001" + suffix);
        File.WriteAllBytes(@"\\?\" + ownInfo, Version2(Path.Combine(_root, "own "), 3, DateTimeOffset.Now));
        if (directory)
        {
            Directory.CreateDirectory(ownPayload);
            File.WriteAllText(Path.Combine(ownPayload, "x..."), "TRAIL");
            File.WriteAllText(Path.Combine(ownPayload, "x"), "PLAIN");
            File.SetAttributes(Path.Combine(ownPayload, "x"), FileAttributes.ReadOnly);
        }
        else File.WriteAllText(ownPayload, "OWN");

        new WindowsTrashMaintenance().Delete(ownInfo);

        Assert.False(File.Exists(@"\\?\" + ownInfo));
        Assert.False(File.Exists(ownPayload) || Directory.Exists(ownPayload));
        Assert.True(File.Exists(neighbourInfo));
        Assert.Equal(Path.Combine(_root, "neighbour"), RecycleBin.Read(neighbourInfo)?.OriginalPath);
    }

    /// <summary>
    /// The tree delete refuses a tree it would read by the wrong names, so a
    /// binned folder holding "x..." beside a read-only "x" stayed in the bin
    /// for ever — Empty, the sweep and Delete for good alike. Purged through
    /// "\\?\", every name is reached as itself and the whole payload goes.
    /// </summary>
    [WindowsFact]
    public void A_binned_folder_holding_a_folded_name_is_purged_whole()
    {
        var (info, payload) = Binned(Path.Combine(_root, "album"), directory: true);

        File.WriteAllText(@"\\?\" + Path.Combine(payload, "x..."), "trailing");
        File.WriteAllText(Path.Combine(payload, "x"), "neighbour");
        File.SetAttributes(Path.Combine(payload, "x"), FileAttributes.ReadOnly);

        var entry = RecycleBin.Read(info);
        Assert.NotNull(entry);

        Assert.True(WindowsTrashMaintenance.Purge(entry), "the purge refused the tree");
        Assert.False(Directory.Exists(@"\\?\" + payload));
        Assert.False(File.Exists(info));
    }

    /// <summary>
    /// **"Deleted 1 item(s) for good" over an item still in the bin.** Delete
    /// dropped Purge's answer. A payload held open by another handle cannot go,
    /// and now says so, naming it, so the pane counts it as failed.
    /// </summary>
    [WindowsFact]
    public void Delete_for_good_that_could_not_delete_says_so()
    {
        var (info, payload) = Binned(Path.Combine(_root, "notes.txt"), directory: false);

        using (new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var refused = Assert.IsType<IOException>(Record.Exception(() => new WindowsTrashMaintenance().Delete(info)));

            Assert.Contains("\"notes.txt\" could not be deleted for good", refused.Message, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(info));
    }
}
