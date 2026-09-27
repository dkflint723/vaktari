using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **The folder an archive lands as is named by the entries' own rule.**
/// Extract all of "report .zip" or "report..zip" into a plainly spelled folder
/// landed — under Windows' rules — as "report", because Win32 takes a
/// trailing space or dot off as the folder is made, while the result, the
/// selection, the status and the undo all said "report "; the undo was then
/// refused as a name Windows cannot open (fix-7 round-2 verification). Now
/// the name is escaped the way an entry's is, "report_", and what lands is
/// what is reported, next to a "report" that is left alone. On Linux, where a
/// trailing space or dot is an ordinary character, the name is kept.
///
/// Two top-level entries, so the working folder itself becomes the result and
/// takes the archive's name. In a temporary folder.
/// </summary>
public sealed class ExtractionLandingNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-extractname").FullName;

    public void Dispose()
    {
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;

        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("report .zip", "report ")]
    [InlineData("report..zip", "report.")]
    public void The_landed_folder_is_the_one_reported_and_it_can_be_taken_back(string archiveName, string stem)
    {
        var archive = Path.Combine(_root, archiveName);

        using (var file = File.Create(archive))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "a.txt", "b.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(entry);
            }
        }

        var into = Directory.CreateDirectory(Path.Combine(_root, "into")).FullName;
        var neighbour = Path.Combine(into, "report");
        File.WriteAllText(neighbour, "a file already called report");

        var done = Archives.Extract(archive, into);

        var expected = OperatingSystem.IsWindows() ? stem.TrimEnd(' ', '.') + "_" : stem;

        Assert.Equal(Path.Combine(into, expected), done.Landed);
        Assert.False(done.IsFile);
        Assert.Null(ReachablePath.Refuse(done.Landed));
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(done.Landed).Select(Path.GetFileName).Order());
        Assert.Equal("a file already called report", File.ReadAllText(neighbour));
    }
}
