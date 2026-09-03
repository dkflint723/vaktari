namespace Vaktari.Linux;

/// <summary>
/// Reads the shared-mime-info glob database directly.
///
/// This is the same data <c>xdg-mime query filetype</c> consults, but that is a
/// shell script which spawns several processes per call — and it was being
/// called once per file to pick an icon name. A listing of a few thousand files
/// meant a few thousand process trees. The database is one file, parsed once.
///
/// Format is documented by shared-mime-info: <c>weight:mimetype:glob[:flags]</c>,
/// one per line, weight defaulting to 50 and higher winning.
/// </summary>
public static class SharedMimeInfo
{
    private static readonly Lazy<Database> Loaded = new(Load, isThreadSafe: true);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        Descriptions = new(StringComparer.Ordinal);

    private sealed record Database(
        Dictionary<string, string> ByExtension,
        Dictionary<string, string> ByName);

    /// <summary>
    /// The mime directories, in the spec's precedence order — later overrides
    /// earlier.
    /// </summary>
    private static IEnumerable<string> MimeRoots()
    {
        yield return "/usr/share/mime";
        yield return "/usr/local/share/mime";

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        yield return Path.Combine(dataHome, "mime");
    }

    /// <summary>
    /// Where to look for the per-type DESCRIPTIONS, for a test.
    ///
    /// **Descriptions only, and that is not tidiness — it is the bug this
    /// caused.** An override that also moved the glob database repointed it for
    /// the whole PROCESS: the database is a Lazy, loaded once and never again,
    /// so a test that pointed it at an empty directory left every later test in
    /// the assembly with no types at all. The Linux suite runs its classes in
    /// parallel, so which tests those were varied by run.
    ///
    /// A seam rather than XDG_DATA_HOME for the same family of reasons — that
    /// variable is process-global too — and because the suite also runs on
    /// Windows agents, where there is no /usr/share/mime to describe anything
    /// with.
    /// </summary>
    internal static IReadOnlyList<string>? DescriptionRootsOverride
    {
        get;
        set
        {
            field = value;

            // Every remembered description came from the old roots, so keeping
            // them would answer about files that are no longer being read.
            Descriptions.Clear();
        }
    }

    private static IEnumerable<string> DescriptionRoots()
        => DescriptionRootsOverride ?? MimeRoots().ToList();

    private static IEnumerable<string> Roots()
        => MimeRoots().Select(root => Path.Combine(root, "globs2"));

    private static Database Load()
    {
        var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Roots())
        {
            if (!File.Exists(file)) continue;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0 || line[0] == '#') continue;

                    var parts = line.Split(':', 3);
                    if (parts.Length < 3) continue;

                    if (!int.TryParse(parts[0], out var weight)) weight = 50;

                    var mime = parts[1];
                    var glob = parts[2];

                    // "*.tar.gz" style — the common case by a wide margin.
                    if (glob.StartsWith("*.", StringComparison.Ordinal)
                        && !glob.AsSpan(2).ContainsAny('*', '?', '['))
                    {
                        var extension = glob[2..];

                        if (weights.TryGetValue(extension, out var existing) && existing > weight)
                            continue;

                        extensions[extension] = mime;
                        weights[extension] = weight;
                    }
                    // A literal filename, like "Makefile" or ".bashrc".
                    else if (!glob.AsSpan().ContainsAny('*', '?', '['))
                    {
                        names[glob] = mime;
                    }

                    // Anything with a wildcard in the middle is rare enough that
                    // it falls through to xdg-mime rather than being reimplemented.
                }
            }
            catch
            {
                // An unreadable database just means fewer known types.
            }
        }

        return new Database(extensions, names);
    }

    /// <summary>
    /// The mime type for a filename, or empty when the database has no answer
    /// and the caller should fall back to a content sniff.
    /// </summary>
    public static string ForPath(string path)
    {
        var database = Loaded.Value;
        var name = Path.GetFileName(path);

        if (name.Length == 0) return "";
        if (database.ByName.TryGetValue(name, out var exact)) return exact;

        // Longest suffix first, so "archive.tar.gz" resolves as tar.gz rather
        // than gz — which is the difference between an archive icon and a
        // generic compressed-file one.
        var start = 0;

        while (true)
        {
            var dot = name.IndexOf('.', start);
            if (dot < 0 || dot == name.Length - 1) return "";

            var suffix = name[(dot + 1)..];
            if (database.ByExtension.TryGetValue(suffix, out var mime)) return mime;

            start = dot + 1;
        }
    }

    /// <summary>
    /// What a mime type is CALLED, for somebody to read.
    ///
    /// **Properties printed the type itself.** "application/vnd.oasis.
    /// opendocument.text" is an identifier for programs, and it was the whole
    /// answer to "what is this file" — where Dolphin says "ODT document" and
    /// Explorer says "OpenDocument Text". The description has been sitting in
    /// the same database the glob table comes from all along, one XML file per
    /// type, which is exactly what every other file manager reads.
    ///
    /// Falls back to the type itself, which is worse than a description and far
    /// better than nothing: a machine with no shared-mime-info installed, or a
    /// type too new for it, still says something true.
    /// </summary>
    public static string Describe(string mime)
    {
        if (string.IsNullOrEmpty(mime)) return "";

        return Descriptions.GetOrAdd(mime, static type =>
        {
            var found = "";

            // Every root, keeping the LAST answer: the precedence is the
            // database's own, where a type described locally overrides the
            // system's wording for it.
            foreach (var root in DescriptionRoots())
            {
                var file = Path.Combine(root, type + ".xml");

                if (!File.Exists(file)) continue;

                try
                {
                    if (CommentIn(File.ReadAllText(file)) is { Length: > 0 } comment)
                        found = comment;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // **Narrow, so it cannot hide the parse.** A bare catch here
                    // swallows a malformed file as well as an unreadable one,
                    // which makes the XML handling below unreachable and
                    // untestable — code that looks like a guard and is not one.
                    // An unreadable description is one fewer description.
                }
            }

            return found.Length > 0 ? found : type;
        });
    }

    /// <summary>
    /// The untranslated comment out of one shared-mime-info type file.
    ///
    /// **The bare element, never a translated one.** These files carry a
    /// comment per locale — dozens of them, all named "comment" and separated
    /// only by an xml:lang attribute — so taking the first match hands back
    /// whichever language happens to be sorted first in that file. The one
    /// without the attribute is the original.
    ///
    /// Read with XDocument rather than by hand: the text is real XML and holds
    /// entities that a substring scan would return raw.
    /// </summary>
    private static string CommentIn(string xml)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xml);
            var lang = System.Xml.Linq.XNamespace.Xml + "lang";

            return document.Root?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "comment" && e.Attribute(lang) is null)?
                .Value.Trim() ?? "";
        }
        catch (System.Xml.XmlException)
        {
            return "";
        }
    }
}
