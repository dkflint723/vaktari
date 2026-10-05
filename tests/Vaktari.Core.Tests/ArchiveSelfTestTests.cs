using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The self-test the shipped binary runs in CI. It is only worth running if
/// it can fail, so two of these break a copy of expected.tsv and require the
/// failure to name the fixture.
/// </summary>
public sealed class ArchiveSelfTestTests : IDisposable
{
    private readonly string _copy = Directory.CreateTempSubdirectory("vaktari-selftest-copy").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_copy, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    [Fact]
    public void Every_committed_fixture_extracts_as_expected()
    {
        var output = new StringWriter();

        var code = ArchiveSelfTest.Run(ArchiveTestData.FixturesDir, output);

        Assert.True(code == 0, output.ToString());
        Assert.DoesNotContain("FAIL", output.ToString());
        Assert.Contains("ok zip-ppmd.zip", output.ToString());
    }

    [Fact]
    public void A_wrong_hash_fails_and_names_the_fixture()
    {
        var lines = CopyFixtures();
        var at = lines.FindIndex(l => l.StartsWith("zip-ppmd.zip\tfile\tzip-ppmd/docs/c.txt\t", StringComparison.Ordinal));
        var cells = lines[at].Split('\t');

        cells[4] = new string('0', 64);
        lines[at] = string.Join('\t', cells);

        File.WriteAllLines(Path.Combine(_copy, "expected.tsv"), lines);

        var output = new StringWriter();

        Assert.Equal(1, ArchiveSelfTest.Run(_copy, output));
        Assert.Contains("FAIL zip-ppmd.zip: zip-ppmd/docs/c.txt has SHA-256", output.ToString());
    }

    [Fact]
    public void A_file_nobody_expected_fails()
    {
        var lines = CopyFixtures();

        lines.RemoveAt(lines.FindIndex(l => l.StartsWith("zip-zstd.zip\tfile\tzip-zstd/jpg/test.jpg\t", StringComparison.Ordinal)));

        File.WriteAllLines(Path.Combine(_copy, "expected.tsv"), lines);

        var output = new StringWriter();

        Assert.Equal(1, ArchiveSelfTest.Run(_copy, output));
        Assert.Contains("FAIL zip-zstd.zip: unexpected zip-zstd/jpg/test.jpg", output.ToString());
    }

    [Fact]
    public void A_refusal_in_other_words_fails()
    {
        var lines = CopyFixtures();
        var at = lines.FindIndex(l => l.StartsWith("zip-aes256.zip\trefused\t", StringComparison.Ordinal));

        lines[at] = lines[at].Replace("password-protected", "locked", StringComparison.Ordinal);

        File.WriteAllLines(Path.Combine(_copy, "expected.tsv"), lines);

        var output = new StringWriter();

        Assert.Equal(1, ArchiveSelfTest.Run(_copy, output));
        Assert.Contains("FAIL zip-aes256.zip: refused with", output.ToString());
    }

    private List<string> CopyFixtures()
    {
        foreach (var file in Directory.EnumerateFiles(ArchiveTestData.FixturesDir))
            File.Copy(file, Path.Combine(_copy, Path.GetFileName(file)));

        return [.. File.ReadAllLines(Path.Combine(_copy, "expected.tsv"))];
    }
}
