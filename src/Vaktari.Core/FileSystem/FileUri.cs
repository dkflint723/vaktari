namespace Vaktari.Core.FileSystem;

/// <summary>
/// Turning what another application sends into a path this machine can open.
///
/// **Everything that asks a file manager to show something sends a URI.** The
/// freedesktop FileManager1 methods are declared to take an array of them, a
/// desktop entry's %U expands to them, and a drop carries text/uri-list. The
/// window already had a private version of this and its doc comment records
/// what it cost: the installed desktop entry said %U, nothing decoded one, and
/// on every desktop that honours %U literally "open containing folder" arrived
/// as "file:///home/me/Documents", failed Directory.Exists, and was dropped
/// without a word. That was the primary Linux install route.
///
/// It lives here rather than in the window because there are now two callers —
/// the command line and the bus — and two copies of a decoder is how one of
/// them keeps a bug the other has already fixed.
/// </summary>
public static class FileUri
{
    /// <summary>
    /// The local path a URI names, or null when it names nothing this process
    /// can open. A plain path is handed straight back: the same argument list
    /// carries both, and always has.
    /// </summary>
    public static string? ToLocalPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();

        // **Tested by looking for a scheme rather than by asking Uri.**
        // Uri.TryCreate accepts "C:\Users\me" as an absolute file URI, so
        // asking it first sends every Windows path down the URI branch — while
        // a Linux path is rejected by it, so the two platforms would take
        // different routes through the same function for the same input shape.
        if (!HasScheme(value)) return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;

        // trash://, recent://, sftp://, mtp:// — all real things a desktop
        // sends. Null rather than the raw string: handing "trash:///x" on to
        // Directory.Exists is how a URI we cannot open becomes a silent no-op
        // instead of a sentence.
        if (!uri.IsFile) return null;

        // A file: URI may name a host, and RFC 8089 says the empty host and
        // "localhost" both mean this machine. Anything else is another
        // machine's filesystem and not ours to open.
        if (uri.Host.Length > 0
            && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return null;

        // **Uri.LocalPath cannot be used once a host is present.** System.Uri
        // sets its UNC flag for ANY non-empty host on a file: URI — including
        // the "localhost" that RFC 8089 says means this machine — and hands
        // back \\localhost\tmp\x, a backslash path no POSIX lookup will ever
        // match, which then fails Directory.Exists and is dropped without a
        // word. That is the exact failure this class exists to end, arriving by
        // a different door. Not Windows-only behaviour: it is the same on both.
        //
        // For the empty host, LocalPath is still the right answer — it is the
        // one that decodes %20 and the UTF-8 escapes, which is the difference
        // between opening "My Documents" and opening nothing. AbsolutePath
        // leaves them escaped, so the localhost branch decodes them itself.
        var path = uri.Host.Length > 0
            ? Uri.UnescapeDataString(uri.AbsolutePath)
            : uri.LocalPath;

        // **'?' and '#' are ordinary characters in a POSIX file name.** Uri is
        // not: it splits them off as query and fragment, so an unescaped
        // file:///tmp/notes#2.txt yields "/tmp/notes" — a real file, the wrong
        // one, opened confidently. A caller that escapes them properly leaves
        // both of these empty and nothing happens here, so putting them back is
        // correct either way.
        if (uri.Query.Length > 0) path += Uri.UnescapeDataString(uri.Query);
        if (uri.Fragment.Length > 0) path += Uri.UnescapeDataString(uri.Fragment);

        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// RFC 3986's scheme rule, with one deliberate exception: a single letter
    /// before the colon is a Windows drive here, not a scheme. One-letter
    /// schemes are legal and essentially nonexistent; drive letters are every
    /// path on the disk.
    /// </summary>
    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');

        if (colon <= 1) return false;
        if (!char.IsAsciiLetter(value[0])) return false;

        for (var i = 1; i < colon; i++)
        {
            var c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        }

        return true;
    }
}
