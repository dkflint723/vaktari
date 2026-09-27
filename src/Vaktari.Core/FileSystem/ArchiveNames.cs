using System.Text;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// The name one path segment of an archive entry lands under.
///
/// **An archive's names are somebody else's, and several kinds of them cannot
/// or should not be written.** Windows refuses <c>a:b</c> and quietly drops a
/// trailing dot (measured on Windows 11 26200: <c>trail.</c> becomes
/// <c>trail</c>), a name like <c>inv\u202Egpj.exe</c> displays as
/// <c>invexe.jpg</c> in every file manager, and a control character is a
/// name nobody can type. The maintainer's decision, following 7-Zip: replace
/// the offending characters with <c>_</c> rather than leave the entry out —
/// <c>inv_gpj.exe</c>, <c>_CON.txt</c> — and count the renaming on the
/// result.
///
/// **Replaced rather than escaped**, so what lands on disk is exactly what a
/// listing shows and what the address bar will accept back: there is one
/// spelling of every landed name, not a display form and a disk form.
///
/// Only <c>""</c>, <c>.</c> and <c>..</c> are refused outright. They are not
/// names at all, and the caller counts them as unsafe.
/// </summary>
public static class ArchiveNames
{
    /// <summary>A landed segment, and whether it differs from what the
    /// archive said.</summary>
    public readonly record struct Landed(string Name, bool Changed);

    private const int MaxUnits = 255;

    /// <summary>
    /// The segment as it may be written, or null when it is not a name.
    /// </summary>
    /// <param name="windowsRules">Windows' own rules as well as the
    /// universal ones: on Windows, and on a Linux destination formatted as
    /// FAT, exFAT or NTFS (see <see cref="WindowsRulesFor"/>).</param>
    public static Landed? Land(string segment, bool windowsRules)
    {
        if (segment is "" or "." or "..") return null;

        var sb = new StringBuilder(segment.Length + 1);

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];

            // A surrogate is kept only as half of a pair.
            if (char.IsHighSurrogate(c) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1]))
            {
                sb.Append(c).Append(segment[++i]);
                continue;
            }

            sb.Append(Unwritable(c, windowsRules) ? '_' : c);
        }

        if (windowsRules)
        {
            // **Every trailing dot and space**, one underscore each: Windows
            // strips them when it creates the file, so "notes." would land as
            // "notes" and collide with a sibling the planner never knew about.
            for (var i = sb.Length - 1; i >= 0 && sb[i] is '.' or ' '; i--) sb[i] = '_';

            if (IsReservedDevice(sb.ToString())) sb.Insert(0, '_');
        }

        var landed = Fit(sb.ToString());

        // **Cutting to fit can expose a space or a dot at the new end** —
        // 254 a's, a space and twenty b's was planned as "a…a " and Windows
        // created "a…a", a name the planner had never checked (review of
        // Stage A). So the trailing rule runs again on what Fit left.
        if (windowsRules) landed = Trailing(landed);

        // **A replacement character means a name already lost something**
        // upstream — the tar reader hands over U+FFFD for bytes that were not
        // UTF-8 and keeps no raw bytes — so it counts as renamed even though
        // nothing here changed it.
        var changed = !string.Equals(landed, segment, StringComparison.Ordinal) || segment.Contains('\uFFFD');

        return new Landed(landed, changed);
    }

    private static string Trailing(string name)
    {
        var chars = name.ToCharArray();

        for (var i = chars.Length - 1; i >= 0 && chars[i] is '.' or ' '; i--) chars[i] = '_';

        return new string(chars);
    }

    /// <summary>
    /// What two names are compared by when deciding whether they collide.
    /// Folded under Windows rules, because <c>Docs</c> and <c>docs</c> are one
    /// folder there and two on ext4.
    /// </summary>
    public static string CollisionKey(string name, bool windowsRules)
        => windowsRules ? name.ToUpperInvariant() : name;

    /// <summary>
    /// The <paramref name="n"/>th copy of a name — <c>notes (2).txt</c>, or
    /// <c>docs (2)</c> for a folder — the house numbering that
    /// <see cref="NewItemName.Free"/> uses, kept inside the length limit by
    /// shortening the stem rather than by losing the number.
    /// </summary>
    public static string Numbered(string name, int n, bool isFolder)
    {
        var (stem, extension) = PathRules.SplitLeaf(name, isFolder);
        var suffix = $" ({n})";

        var candidate = stem + suffix + extension;

        while (Units(candidate) > MaxUnits && stem.Length > 1)
        {
            stem = TrimOne(stem);
            candidate = stem + suffix + extension;
        }

        return candidate;
    }

    /// <summary>
    /// Whether a destination filesystem needs Windows' rules on a machine that
    /// is not Windows.
    ///
    /// **Linux enforces the filesystem's rules, not its own.** A FAT or exFAT
    /// stick refuses <c>: * ? " &lt; &gt; |</c> with EINVAL and folds case, so
    /// an archive holding <c>a:b</c> or both <c>Docs</c> and <c>docs</c> fails
    /// part-way there even though the same archive extracts to ext4. Knowing it
    /// up front turns those failures into names. <c>fuseblk</c> is how
    /// ntfs-3g reports itself; it may also be another FUSE filesystem, and
    /// Windows rules on those only rename a few more names.
    /// </summary>
    internal static bool WindowsRulesFor(string? driveFormat)
        => OperatingSystem.IsWindows()
           || driveFormat?.ToLowerInvariant() is "vfat" or "msdos" or "exfat" or "ntfs" or "ntfs3" or "fuseblk";

    private static bool Unwritable(char c, bool windowsRules)
    {
        // C0, DEL and C1 controls.
        if (c < 0x20 || c is >= '\u007F' and <= '\u009F') return true;

        // Bidirectional controls: each one makes a name display in an order
        // other than the one it has.
        if (c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'))
            return true;

        // Invisible characters, which make two names look identical.
        if (c is (>= '\u200B' and <= '\u200D') or '\u2060' or '\uFEFF') return true;

        // A surrogate reaching here is one without its partner.
        if (char.IsSurrogate(c)) return true;

        return windowsRules && c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/';
    }

    /// <summary>
    /// The DOS device names, which Windows resolves to the device whatever
    /// extension follows — <c>CON.txt</c> is the console to anything that
    /// asks by the old rules. Measured on Windows 11 26200 that .NET creates
    /// them as ordinary files; older Windows and plenty of programs do not
    /// agree, so they are prefixed the way 7-Zip prefixes them.
    /// </summary>
    private static bool IsReservedDevice(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ').ToUpperInvariant();

        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return true;

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
               && stem[3] is (>= '0' and <= '9') or '¹' or '²' or '³';
    }

    /// <summary>
    /// **A name over 255 units cannot be created at all**, on either system —
    /// 255 UTF-16 units on Windows, 255 UTF-8 bytes on Linux. The stem is
    /// shortened and the extension kept, so the file still opens with what it
    /// opened with, and a surrogate pair is never split in two.
    /// </summary>
    private static string Fit(string name)
    {
        if (Units(name) <= MaxUnits) return name;

        var (stem, extension) = PathRules.SplitLeaf(name, isDirectory: false);

        // An extension that is itself too long is part of the stem for this.
        if (Units(extension) > MaxUnits / 2) (stem, extension) = (name, "");

        while (Units(stem + extension) > MaxUnits && stem.Length > 1) stem = TrimOne(stem);

        return stem + extension;
    }

    private static string TrimOne(string s)
        => s.Length >= 2 && char.IsLowSurrogate(s[^1]) && char.IsHighSurrogate(s[^2]) ? s[..^2] : s[..^1];

    /// <summary>UTF-16 units on Windows; UTF-8 bytes elsewhere, which is never
    /// fewer, so a FAT stick under Linux is covered by the stricter count.</summary>
    private static int Units(string s)
        => OperatingSystem.IsWindows() ? s.Length : Encoding.UTF8.GetByteCount(s);
}
