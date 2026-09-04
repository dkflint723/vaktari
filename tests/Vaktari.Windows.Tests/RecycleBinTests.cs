using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Reading the Recycle Bin's metadata format.
///
/// **This parser decides where a restored file goes**, which is why the
/// rejection cases matter more than the happy one. A listing built from a
/// misread buffer shows a wrong path and looks odd; a RESTORE built from one
/// moves a real file to a path assembled out of unrelated bytes. So anything
/// unrecognised has to be refused rather than interpreted.
///
/// Round-tripped against the real bin at the time of writing: a file trashed
/// through WindowsFileOperations listed with its true size and deletion time,
/// restored to its exact original path with its bytes intact, and — with
/// something else holding the name — restored beside it as "notes (1).txt"
/// while the occupant was left alone.
/// </summary>
[SupportedOSPlatform("windows")]
public class RecycleBinTests
{
    /// <summary>A version 2 record, the shape Windows 10 and later write.</summary>
    private static byte[] Version2(string path, long size, DateTimeOffset deleted)
    {
        var chars = Encoding.Unicode.GetBytes(path);
        var bytes = new byte[28 + chars.Length + 2];

        BitConverter.TryWriteBytes(bytes.AsSpan(0), 2L);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), deleted.ToFileTime());
        BitConverter.TryWriteBytes(bytes.AsSpan(24), (path.Length + 1));
        chars.CopyTo(bytes, 28);

        return bytes;
    }

    /// <summary>A version 1 record: the same header, then a fixed 260-character
    /// field. Still written by anything older than Windows 10, and a bin can
    /// hold both at once.</summary>
    private static byte[] Version1(string path, long size, DateTimeOffset deleted)
    {
        var bytes = new byte[24 + 520];

        BitConverter.TryWriteBytes(bytes.AsSpan(0), 1L);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), deleted.ToFileTime());
        Encoding.Unicode.GetBytes(path).CopyTo(bytes, 24);

        return bytes;
    }

    [WindowsFact]
    public void A_version_2_record_is_read()
    {
        var when = DateTimeOffset.Now.AddDays(-3);

        Assert.True(RecycleBin.TryParse(
            Version2(@"C:\Users\someone\notes.txt", 4096, when), out var path, out var size, out var deleted));

        Assert.Equal(@"C:\Users\someone\notes.txt", path);
        Assert.Equal(4096, size);
        Assert.Equal(when.ToFileTime(), deleted.ToFileTime());
    }

    [WindowsFact]
    public void A_version_1_record_is_read()
    {
        Assert.True(RecycleBin.TryParse(
            Version1(@"D:\archive\old report.docx", 12, DateTimeOffset.Now), out var path, out var size, out _));

        Assert.Equal(@"D:\archive\old report.docx", path);
        Assert.Equal(12, size);
    }

    /// <summary>
    /// A version this does not know about is refused, not read hopefully. The
    /// field layout after the header is exactly what changed between versions 1
    /// and 2, so a third would put the path somewhere new — and reading it at
    /// the old offset yields a plausible-looking string rather than an obvious
    /// failure.
    /// </summary>
    [WindowsTheory]
    [InlineData(0L)]
    [InlineData(3L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void An_unknown_version_is_refused(long version)
    {
        var bytes = Version2(@"C:\x\y.txt", 1, DateTimeOffset.Now);
        BitConverter.TryWriteBytes(bytes.AsSpan(0), version);

        Assert.False(RecycleBin.TryParse(bytes, out _, out _, out _));
    }

    [WindowsTheory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(23)]
    [InlineData(27)]
    public void A_truncated_record_is_refused(int length)
        => Assert.False(RecycleBin.TryParse(new byte[length], out _, out _, out _));

    /// <summary>
    /// A declared path length longer than the buffer holding it. Trusting it
    /// would read past the end of the record.
    /// </summary>
    [WindowsFact]
    public void A_path_longer_than_its_buffer_is_refused()
    {
        var bytes = Version2(@"C:\x\y.txt", 1, DateTimeOffset.Now);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 5000);

        Assert.False(RecycleBin.TryParse(bytes, out _, out _, out _));
    }

    /// <summary>
    /// Relative or empty paths are refused. Restore joins this onto nothing and
    /// moves a file to it, so "where it came from" has to be somewhere absolute.
    /// </summary>
    [WindowsTheory]
    [InlineData("")]
    [InlineData("notes.txt")]
    [InlineData(@"..\..\notes.txt")]
    public void A_path_that_is_not_absolute_is_refused(string path)
        => Assert.False(RecycleBin.TryParse(
            Version2(path, 1, DateTimeOffset.Now), out _, out _, out _));

    [WindowsFact]
    public void A_nonsensical_deletion_time_is_refused()
    {
        var bytes = Version2(@"C:\x\y.txt", 1, DateTimeOffset.Now);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 0L);

        Assert.False(RecycleBin.TryParse(bytes, out _, out _, out _));
    }

    /// <summary>The payload sits beside its metadata, differing by one
    /// character. Getting this wrong would restore the wrong file.</summary>
    [WindowsTheory]
    [InlineData(@"C:\$Recycle.Bin\S-1-5\$IABC123.txt", @"C:\$Recycle.Bin\S-1-5\$RABC123.txt")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5\$IXY", @"C:\$Recycle.Bin\S-1-5\$RXY")]
    public void The_payload_is_found_beside_the_metadata(string info, string expected)
        => Assert.Equal(expected, RecycleBin.PayloadFor(info));

    [WindowsTheory]
    [InlineData(@"C:\$Recycle.Bin\S-1-5\$RABC123.txt")]  // the payload itself
    [InlineData(@"C:\$Recycle.Bin\S-1-5\desktop.ini")]
    [InlineData("x")]
    public void Something_that_is_not_a_metadata_file_has_no_payload(string path)
        => Assert.Null(RecycleBin.PayloadFor(path));

    /// <summary>
    /// The bin is per-volume, so a listing that reads only C: is silently
    /// partial. This asserts the shape rather than the contents, which depend
    /// on the machine.
    /// </summary>
    [WindowsFact]
    public void Bins_are_looked_for_on_every_ready_volume()
    {
        foreach (var directory in RecycleBin.Directories())
        {
            Assert.Contains("$Recycle.Bin", directory, StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathFullyQualified(directory));
        }
    }

    /// <summary>
    /// The cheap walk behind the undo after a recycle: the metadata files, and
    /// nothing else sharing the folder. The payloads sit right beside them and
    /// so does the desktop.ini that names the bin in Explorer, and a walk that
    /// swept those up would hand the undo keys that restore nothing.
    ///
    /// A made-up folder under the temp directory, as
    /// <see cref="RecycleDeleteOneTests"/> does — the walk does not care that it
    /// is not called $Recycle.Bin, and nothing here touches the bin of whoever
    /// is running it.
    /// </summary>
    [WindowsFact]
    public void Only_the_metadata_files_in_a_bin_are_walked()
    {
        using var tree = new TempTree();
        var bin = tree.Dir("bin");

        File.WriteAllText(Path.Combine(bin, "$IAAA111.txt"), "metadata");
        File.WriteAllText(Path.Combine(bin, "$RAAA111.txt"), "payload");
        File.WriteAllText(Path.Combine(bin, "$IBBB222"), "metadata");
        File.WriteAllText(Path.Combine(bin, "$RBBB222"), "payload");
        File.WriteAllText(Path.Combine(bin, "desktop.ini"), "[.ShellClassInfo]");

        Assert.Equal(
            ["$IAAA111.txt", "$IBBB222"],
            RecycleBin.InfoFiles(bin)
                .Select(path => Path.GetFileName(path))
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// **The walk answers wider than the listing does, on purpose.** A metadata
    /// file whose payload has gone is not an entry — <see cref="RecycleBin.Read"/>
    /// refuses it, so it lists as nothing — but it is still a name in the bin,
    /// and the undo's difference needs it on both sides to cancel. The bin this
    /// was measured against held 114 such names to 107 entries.
    /// </summary>
    [WindowsFact]
    public void A_metadata_file_whose_payload_has_gone_is_still_a_name()
    {
        using var tree = new TempTree();
        var bin = tree.Dir("bin");

        var orphan = Path.Combine(bin, "$ICCC333.txt");
        File.WriteAllBytes(orphan, Version2(@"C:\Users\someone\notes.txt", 12, DateTimeOffset.Now));

        Assert.Null(RecycleBin.Read(orphan));
        Assert.Equal([orphan], RecycleBin.InfoFiles(bin));
    }

    /// <summary>
    /// **A guard, and one whose teeth depend on the machine**: it says nothing
    /// at all on a runner whose Recycle Bin is empty. What it pins where the bin
    /// does hold something is that <see cref="WindowsTrashMaintenance.Keys"/>
    /// reaches the bin at all — replacing it with an empty answer was measured
    /// to redden this test and nothing else in the suite.
    ///
    /// **It does NOT pin that the walk reaches every volume, and cannot.**
    /// <see cref="RecycleBin.List"/> is built on the same
    /// <see cref="RecycleBin.Names"/> walk, so a walk narrowed to one drive
    /// narrows both sides of this comparison together. Measured: with Names
    /// taking one directory of the two on this machine, and again with it
    /// taking none — the whole bin reported empty — every test still passed.
    /// The test below covers that, by recomputing the answer independently.
    ///
    /// Keys on both sides of the listing narrows the window the shared bin
    /// opens: something recycled after the first walk is caught by the second,
    /// and something purged before the second was caught by the first. It does
    /// not close it — something both recycled and purged between the two walks
    /// is in neither, and could still be in the listing taken between them.
    /// </summary>
    [WindowsFact]
    public void Every_listed_entry_is_one_of_the_keys()
    {
        var bin = new WindowsTrashMaintenance();

        var keys = bin.Keys().ToHashSet(StringComparer.Ordinal);
        var listed = bin.List();
        keys.UnionWith(bin.Keys());

        foreach (var item in listed) Assert.Contains(item.TrashName, keys);
    }

    /// <summary>
    /// **The cheap walk reaches every volume's bin, not just the first one.**
    /// The test above cannot see this: <see cref="RecycleBin.List"/> is built on
    /// the same walk, so a narrowed walk narrows both sides of it at once and it
    /// stays green. This one rebuilds the answer from
    /// <see cref="RecycleBin.Directories"/> instead, so there is nothing for a
    /// narrowed <see cref="RecycleBin.Names"/> to hide behind.
    ///
    /// Machine-dependent in the same way, and for the same reason: it says
    /// nothing on a runner whose bins are all empty. It has teeth wherever more
    /// than one volume is holding something, which is where a walk that stopped
    /// at C: would cost somebody the undo of a delete on D:.
    ///
    /// Names on both sides of the per-volume read, so a recycle by another
    /// program between the two cannot flake it.
    /// </summary>
    [WindowsFact]
    public void The_walk_reaches_every_volume_that_has_a_bin()
    {
        var names = RecycleBin.Names().ToHashSet(StringComparer.Ordinal);
        var perVolume = RecycleBin.Directories().SelectMany(RecycleBin.InfoFiles).ToList();
        names.UnionWith(RecycleBin.Names());

        foreach (var info in perVolume) Assert.Contains(info, names);
    }
}
