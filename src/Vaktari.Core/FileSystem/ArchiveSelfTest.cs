using System.Security.Cryptography;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Extracts every committed fixture with the SHIPPED binary and compares
/// the result with <c>expected.tsv</c>: <c>Vaktari --self-test-archives
/// tests/Fixtures/Archives</c>.
///
/// **A NativeAOT publish with full trimming is a different program from the
/// one the tests ran.** A decoder that is only reached through a registry
/// the trimmer cannot see — PPMd, BCJ2, Deflate64, the RAR unpackers — is
/// removed without a warning and fails only when somebody opens such an
/// archive. The unit tests run on the JIT and would never know (R1-16). So CI
/// runs this against the published binary on both platforms, and the Fedora
/// package runs it against the installed one.
///
/// **Exact, not approximate**: the complete set of files each fixture lands
/// as — landing folder included, which pins the no-double-wrap rule — the
/// SHA-256 of each, the summary counts, or the exact sentence a refusal gives.
/// An extra file is a failure as surely as a missing one.
/// </summary>
public static class ArchiveSelfTest
{
    private sealed record Expectation(
        List<(string Path, long Size, string Sha)> Files,
        int[]? Counts,
        string? Refusal);

    /// <returns>0 when every fixture matched, 1 otherwise.</returns>
    public static int Run(string fixturesDir, TextWriter output)
    {
        Dictionary<string, Expectation> expected;

        try
        {
            expected = Parse(File.ReadAllLines(Path.Combine(fixturesDir, "expected.tsv")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            output.WriteLine($"FAIL expected.tsv: {e.Message}");
            return 1;
        }

        var failed = 0;

        foreach (var (fixture, expect) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var problem = Check(Path.Combine(fixturesDir, fixture), expect);

            if (problem is null)
            {
                output.WriteLine($"ok {fixture}");
            }
            else
            {
                output.WriteLine($"FAIL {fixture}: {problem}");
                failed++;
            }
        }

        output.WriteLine(failed == 0
            ? $"all {expected.Count} archive fixtures extracted as expected"
            : $"{failed} of {expected.Count} archive fixtures did not extract as expected");

        return failed == 0 ? 0 : 1;
    }

    private static string? Check(string archive, Expectation expect)
    {
        var temp = Directory.CreateTempSubdirectory("vaktari-selftest").FullName;

        try
        {
            Archives.Extraction done;

            try
            {
                done = Archives.Extract(archive, temp);
            }
            catch (Exception e) when (expect.Refusal is not null)
            {
                return e.Message == expect.Refusal ? null : $"refused with \"{e.Message}\", expected \"{expect.Refusal}\"";
            }
            catch (Exception e)
            {
                return $"{e.GetType().Name}: {e.Message}";
            }

            if (expect.Refusal is not null) return $"extracted, expected the refusal \"{expect.Refusal}\"";

            var actual = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(temp, f).Replace('\\', '/'), StringComparer.Ordinal);

            foreach (var (path, size, sha) in expect.Files)
            {
                if (!actual.Remove(path, out var file)) return $"{path} is missing";

                var info = new FileInfo(file);

                if (info.Length != size) return $"{path} is {info.Length} bytes, expected {size}";

                using var read = File.OpenRead(file);

                var hash = Convert.ToHexStringLower(SHA256.HashData(read));

                if (hash != sha) return $"{path} has SHA-256 {hash}, expected {sha}";
            }

            if (actual.Count > 0) return $"unexpected {actual.Keys.OrderBy(k => k, StringComparer.Ordinal).First()}";

            if (expect.Counts is { } c)
            {
                int[] got =
                [
                    done.Files, done.Folders, done.Renamed, done.LeftOut.Unsafe, done.LeftOut.Links,
                    done.LeftOut.Special, done.LeftOut.MacMetadata, done.LeftOut.Unwritable,
                ];

                if (!got.SequenceEqual(c))
                    return $"counts {string.Join(',', got)}, expected {string.Join(',', c)} (files,folders,renamed,unsafe,links,special,mac,unwritable)";
            }

            return null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Quiet.Swallowed("selftest", e); }
        }
    }

    /// <summary>
    /// Columns: fixture, kind (file | summary | refused), path, size, sha256,
    /// files, folders, renamed, unsafe, links, special, mac, unwritable,
    /// sentence. Blank lines and lines starting with # are skipped, as is the
    /// header.
    /// </summary>
    private static Dictionary<string, Expectation> Parse(string[] lines)
    {
        var all = new Dictionary<string, Expectation>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("fixture\t", StringComparison.Ordinal)) continue;

            var f = line.Split('\t');

            if (f.Length < 14) throw new FormatException($"expected.tsv: a line with {f.Length} columns: {line}");

            if (!all.TryGetValue(f[0], out var e)) all[f[0]] = e = new Expectation([], null, null);

            switch (f[1])
            {
                case "file":
                    e.Files.Add((f[2], long.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture), f[4]));
                    break;

                case "summary":
                    all[f[0]] = e with { Counts = [.. f[5..13].Select(n => int.Parse(n, System.Globalization.CultureInfo.InvariantCulture))] };
                    break;

                case "refused":
                    all[f[0]] = e with { Refusal = f[13] };
                    break;

                default:
                    throw new FormatException($"expected.tsv: unknown kind '{f[1]}'");
            }
        }

        return all;
    }
}
