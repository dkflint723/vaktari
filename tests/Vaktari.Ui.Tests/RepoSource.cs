namespace Vaktari.Ui.Tests;

/// <summary>
/// Reading the application's own source, for the handful of rules that live in
/// a place no view model can be asked about — a markup attribute, a call site,
/// the order of two statements inside an event handler.
///
/// **Every one of these read the file raw, and the file is not raw.** The
/// repository stores LF and git hands out CRLF on checkout, so a scan for
/// "\n    }\n" — the end of a method — matched locally and returned -1 on a
/// Windows agent. One test crashed on that outright, which is how it was
/// found; the rest had a `less than zero` fallback and quietly widened from
/// "inside this method" to "anywhere in the file", so they went on passing
/// while asserting something much weaker than they claimed.
///
/// A test that is weaker on the machine that gates the merge than on the one it
/// was written on is worse than no test, because nothing says so.
/// </summary>
internal static class RepoSource
{
    private static string Root
    {
        get
        {
            var here = AppContext.BaseDirectory;

            // By extension rather than by name. **This looked for
            // "Vaktari.slnx" and the file is vaktari.slnx** — which matched on
            // Windows and did not on Linux, so the walk ran off the top of the
            // filesystem and the null went into Path.Combine. Nothing here is
            // allowed to depend on how a filesystem feels about case.
            while (here is not null && !Directory.EnumerateFiles(here, "*.slnx").Any())
                here = Path.GetDirectoryName(here);

            return here ?? throw new InvalidOperationException(
                "could not find the repository root above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>One file under src/Vaktari.Ui, with line endings normalised so
    /// the same scan means the same thing on either platform.</summary>
    internal static string Ui(params string[] parts)
        => File.ReadAllText(Path.Combine([Root, "src", "Vaktari.Ui", .. parts]))
               .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Every markup file in the application, by name, for the rules
    /// that have to hold across all of them rather than in one.</summary>
    internal static IEnumerable<string> UiMarkup()
        => Directory.EnumerateFiles(Path.Combine(Root, "src", "Vaktari.Ui"), "*.axaml")
                    .Select(Path.GetFileName)
                    .OfType<string>();

    /// <summary>
    /// The text of one method, from its declaration to the closing brace at
    /// class indentation.
    ///
    /// Throws rather than falling back to the whole file: a declaration that
    /// cannot be found means the test is looking for something that has been
    /// renamed, and answering that with "here is everything" turns a broken
    /// test into a passing one.
    /// </summary>
    internal static string Body(string source, string declaration)
    {
        var at = source.IndexOf(declaration, StringComparison.Ordinal);

        if (at < 0)
            throw new InvalidOperationException(
                $"'{declaration}' is not declared the way this test looks for it");

        var end = source.IndexOf("\n    }\n", at, StringComparison.Ordinal);

        if (end < 0)
            throw new InvalidOperationException(
                $"could not find the end of '{declaration}'");

        return source[at..end];
    }
}
