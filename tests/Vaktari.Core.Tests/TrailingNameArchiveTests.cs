using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **Compress read the neighbour's bytes and said it had made the archive.**
/// From the seventh review round's hunt (H5): "report " compressed to an entry
/// "report " holding report's bytes; a folder holding "x..." beside "x" put "x"
/// in the zip twice and "x..." not at all; "report " beside a FOLDER "report"
/// wrote nothing but an empty "report /" entry. All three reported success.
///
/// Read now through "\\?\" — each entry holds its own name's bytes, or the
/// compress refuses — and a destination that folds is refused rather than
/// written as its neighbour. Extract reads the archive the row names.
///
/// On Linux these are ordinary names and every one of them is archived as
/// itself with nothing refused. All in a temporary folder; the Windows
/// trailing names are made through "\\?\".
/// </summary>
public sealed class TrailingNameArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-zipfold").FullName;

    public void Dispose()
    {
        try
        {
            var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;

            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(f);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    /// <summary>The spelling this test writes a name through: "\\?\" on
    /// Windows, where nothing else keeps it, and the path itself on Linux.</summary>
    private static string Raw(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;

    private string File_(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Raw(Path.GetDirectoryName(path)!));
        File.WriteAllText(Raw(path), content);
        return path;
    }

    private static SortedDictionary<string, string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(Raw(zip));
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) { entries[entry.FullName] = "<folder>"; continue; }

            using var reader = new StreamReader(entry.Open());
            entries.Add(entry.FullName, reader.ReadToEnd());
        }

        return entries;
    }

    [WindowsTheory]
    [InlineData("report ")]
    [InlineData("report.")]
    [InlineData("report...")]
    public void A_folded_file_is_archived_with_its_own_bytes(string name)
    {
        File_("report", "NEIGHBOUR-SECRET");
        var row = File_(name, "OWN");

        var zip = Archives.Compress([row], _root);

        Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal) { [name] = "OWN" }, Entries(zip));
    }

    [WindowsTheory]
    [InlineData("x...")]
    [InlineData("notes ")]
    public void A_folder_holding_a_folded_name_is_archived_name_for_name(string name)
    {
        File_(Path.Combine("album", name), "OWN");
        File_(Path.Combine("album", name.TrimEnd(' ', '.')), "NEIGHBOUR");
        File_(Path.Combine("album", "sub.", "inner.txt"), "INNER");

        var zip = Archives.Compress([Path.Combine(_root, "album")], _root);

        Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["album/"] = "<folder>",
            ["album/" + name] = "OWN",
            ["album/" + name.TrimEnd(' ', '.')] = "NEIGHBOUR",
            ["album/sub./"] = "<folder>",
            ["album/sub./inner.txt"] = "INNER",
        }, Entries(zip));
    }

    [WindowsFact]
    public void A_folded_file_beside_a_folder_of_that_name_is_archived_as_the_file()
    {
        File_(Path.Combine("report", "inside.txt"), "NEIGHBOUR-FOLDER");
        var row = File_("report ", "OWN");

        var zip = Archives.Compress([row], _root);

        Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["report "] = "OWN" }, Entries(zip));
    }

    [WindowsFact]
    public void An_archive_is_not_written_into_a_folded_folder()
    {
        var row = File_("notes.txt", "OWN");
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        Directory.CreateDirectory(Raw(Path.Combine(_root, "out ")));

        var refused = Assert.IsType<IOException>(
            Record.Exception(() => Archives.Compress([row], Path.Combine(_root, "out "))));

        Assert.Contains("\"out \" ends with a space", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "out")));
    }

    [WindowsFact]
    public void Extract_reads_the_archive_the_row_names()
    {
        Zip(Path.Combine(_root, "src", "report.zip"), "neighbour.txt", "NEIGHBOUR");
        Zip(Path.Combine(_root, "src", "report.zip."), "own.txt", "OWN");
        var into = Directory.CreateDirectory(Path.Combine(_root, "into")).FullName;

        var done = Archives.Extract(Path.Combine(_root, "src", "report.zip."), into);

        Assert.Equal("OWN", File.ReadAllText(Raw(Path.Combine(done.Landed, "own.txt"))));
        Assert.False(File.Exists(Path.Combine(done.Landed, "neighbour.txt")));
    }

    [WindowsFact]
    public void Nothing_is_extracted_into_a_folded_folder()
    {
        var archive = Path.Combine(_root, "src", "a.zip");
        Zip(archive, "a.txt", "A");
        Directory.CreateDirectory(Path.Combine(_root, "into"));
        Directory.CreateDirectory(Raw(Path.Combine(_root, "into ")));

        var refused = Assert.IsType<ArchiveRefusedException>(
            Record.Exception(() => Archives.Extract(archive, Path.Combine(_root, "into "))));

        Assert.Contains("\"into \" ends with a space", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "into")));
    }

    [PosixFact]
    public void On_linux_the_same_names_are_archived_as_themselves()
    {
        File_("report", "NEIGHBOUR");
        var row = File_("report ", "OWN");
        File_(Path.Combine("album", "x..."), "OWN-X");
        File_(Path.Combine("album", "x"), "NEIGHBOUR-X");

        Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["report "] = "OWN" },
                     Entries(Archives.Compress([row], _root)));

        Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["album/"] = "<folder>",
            ["album/x"] = "NEIGHBOUR-X",
            ["album/x..."] = "OWN-X",
        }, Entries(Archives.Compress([Path.Combine(_root, "album")], _root)));
    }

    private static void Zip(string path, string entry, string content)
    {
        Directory.CreateDirectory(Raw(Path.GetDirectoryName(path)!));

        using var file = File.Create(Raw(path));
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());

        writer.Write(content);
    }
}
