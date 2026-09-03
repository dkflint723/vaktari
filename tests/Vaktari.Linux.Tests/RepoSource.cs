namespace Vaktari.Linux.Tests;

/// <summary>
/// Reading the application's own source, for the handful of rules that live
/// somewhere no unit can be asked about — a call site, the order of two
/// statements, a table of per-platform flags.
///
/// **Every one of these walked up looking for "Vaktari.slnx", and the file is
/// called vaktari.slnx.** On Windows that matched anyway; on Linux it did not,
/// so the walk ran off the top of the filesystem and the null went straight
/// into Path.Combine. The tests passed on the machine they were written on and
/// crashed on the agent that gates the merge — the second time in this codebase
/// that a source-reading test has been weaker there than here.
///
/// One reader, one spelling, and a failure that says what it could not find.
/// </summary>
internal static class RepoSource
{
    internal static string Root
    {
        get
        {
            var here = AppContext.BaseDirectory;

            // Case-insensitively, so neither spelling of the name can break
            // this again on one platform and not the other.
            while (here is not null && !Directory.EnumerateFiles(here, "*.slnx").Any())
                here = Path.GetDirectoryName(here);

            return here ?? throw new InvalidOperationException(
                "could not find the repository root above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>One file under the repository root, with line endings
    /// normalised — the repository stores LF and git hands out CRLF.</summary>
    internal static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([Root, .. parts]))
               .Replace("\r\n", "\n", StringComparison.Ordinal);
}
