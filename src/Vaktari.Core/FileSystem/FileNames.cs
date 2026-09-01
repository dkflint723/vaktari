namespace Vaktari.Core.FileSystem;

/// <summary>
/// Tidies a name somebody typed, the way the desktop would.
///
/// **Explorer strips leading and trailing spaces silently**, and Windows itself
/// drops trailing spaces and dots at the API level — so a name typed with one
/// produces a file that does not match what was asked for, and on a bad day one
/// that other tools cannot open or delete at all.
///
/// Applied only to text a person typed. A name read from disk is used exactly as
/// it is: a file already called "report " exists, and quietly looking for
/// "report" instead would fail to find it.
/// </summary>
public static class FileNames
{
    /// <summary>
    /// The name as the filesystem would have it, or empty where nothing is
    /// left — which the caller should refuse rather than act on.
    /// </summary>
    public static string Clean(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return "";

        var name = typed.Trim();

        // **Only on Windows.** A trailing dot or space is legal on a
        // freedesktop filesystem and somebody may well have meant it; on
        // Windows the API discards them, so keeping them would ask for one name
        // and get another.
        if (OperatingSystem.IsWindows()) name = name.TrimEnd(' ', '.');

        return name;
    }

    /// <summary>
    /// Why this name cannot be used here, or null when it can.
    ///
    /// **The rename check was empty-or-separator and nothing else**, so on
    /// Windows a colon reached the filesystem and came back as the raw "The
    /// parameter is incorrect." — and worse, <c>d:notes</c> is drive-RELATIVE,
    /// so Path.Combine discarded the folder entirely and the file silently left
    /// the listing for the current directory of drive D:.
    ///
    /// A sentence, not a code: it is shown to the person typing the name, and
    /// an error number tells them nothing about which character to remove.
    /// Applied to <see cref="Clean"/>'s output, so a trailing space that would
    /// have been trimmed anyway is not reported as a fault.
    /// </summary>
    public static string? Refuse(string? typed)
    {
        var name = Clean(typed);

        if (name.Length == 0) return "a name cannot be empty";

        if (name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
            return "a name cannot contain a slash";

        if (name is "." or "..") return $"\"{name}\" is not a name";

        // **Windows only past here.** ext4 takes every one of these, and
        // refusing them everywhere would stop a Linux user renaming a file to
        // something their filesystem is perfectly happy with.
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            if (!name.Contains(bad)) continue;

            // The control characters have no printable form to quote back.
            return char.IsControl(bad)
                ? "a name cannot contain control characters"
                : $"a name cannot contain {bad}";
        }

        // Reserved with or without an extension: a file called CON.txt cannot
        // be created either.
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(name)))
            return $"\"{Path.GetFileNameWithoutExtension(name)}\" is a name Windows reserves for a device";

        return null;
    }

    private static readonly HashSet<string> Reserved =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
}
