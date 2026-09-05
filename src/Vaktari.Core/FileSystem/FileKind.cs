using System.Collections.Concurrent;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// What kind of thing a row is, in one short phrase, computed synchronously.
///
/// **The platforms already have a Kind, and neither can be a column.** Windows
/// asks the shell and Linux asks shared-mime-info; both are per-file and async,
/// so a listing of two hundred thousand rows would be two hundred thousand
/// round trips to fill a column that scrolls past in a second. They also
/// disagree in shape — "PNG file" against "image/png" — so a column fed by
/// whichever platform is running would say something different on each.
///
/// This is the cheap answer instead: the extension, spelled the way Explorer
/// spells it when it has nothing better. It agrees with the Kind sort and the
/// Kind grouping beside it, because all three key on the same extension.
/// </summary>
public static class FileKind
{
    /// <summary>
    /// One string per extension, shared by every row that has it. A listing is
    /// mostly a handful of extensions repeated, so the alternative is tens of
    /// thousands of identical strings — allocated while scrolling, which is the
    /// one place this must not do that.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Phrases = new(StringComparer.OrdinalIgnoreCase);

    public static string Describe(FileEntry entry)
    {
        // **A link was drawn as the thing it points at, everywhere.** The
        // Symlink flag has been set correctly by both providers since they were
        // written and read by nothing at all — no binding, no converter, no
        // view-model property — so a symlinked folder, a junction and a mount
        // point were indistinguishable from the real thing in every layout, and
        // deleting one is a very different act from deleting the other.
        //
        // A FOLDER only, here. A symlink to a file already carries its own
        // type — "PNG file" says more than "Link" would — and losing that to
        // say "this is a link" trades one fact for another. A folder has no
        // extension to lose, so this is free. The emblem is what marks the
        // rest, and that is a drawing rather than a word.
        if (entry.IsDirectory) return entry.IsSymlink ? "Folder link" : "Folder";

        var extension = entry.Extension;

        if (extension.Length == 0) return "File";

        // **Explorer never says "LNK file", and never shows the extension
        // either.** lnkfile carries NeverShowExt in the registry, so Desktop and
        // the Start Menu — folders that are nothing but shortcuts — listed here
        // as a wall of "Chrome.lnk / LNK file" rows and everywhere else on the
        // machine as "Chrome / Shortcut". The sidebar already agreed with
        // Explorer, pinning "Sync.lnk" as "Sync", and the shortcut writer's own
        // doc comment claimed the listing hid the extension. Only the listing
        // disagreed.
        //
        // Windows only. On Linux a .lnk is an opaque file from another
        // operating system that nothing here can follow, so calling it a
        // shortcut would promise a hop that does not exist.
        if (OperatingSystem.IsWindows() && IsShortcut(extension)) return "Shortcut";

        // Bounded on purpose. A name may legally end in a dot and a hundred
        // characters, and caching one entry per such name would make the cache
        // grow with the listing rather than with the kinds in it.
        if (extension.Length > 12) return "File";

        return Phrases.GetOrAdd(
            extension.ToString(),
            ext => Named.TryGetValue(ext, out var phrase) ? phrase : ext.ToUpperInvariant() + " file");
    }

    /// <summary>
    /// The kinds worth naming, and the reason this is a table rather than a
    /// lookup.
    ///
    /// **Every file read "&lt;EXT&gt; file".** Explorer says "Application" and
    /// "Text Document"; this said "EXE file" and "TXT file" — the extension the
    /// column sits beside, spelled louder. A Type column whose every value can
    /// be read off the Name column is a column of nothing, and it sorted that
    /// way too: .exe filed under E, between .dll and .gif, rather than with the
    /// programs.
    ///
    /// A table rather than the platform's own answer, for the reason the class
    /// comment gives at length: both platforms answer per file and
    /// asynchronously, and a listing of two hundred thousand rows cannot make
    /// two hundred thousand round trips to fill a column that scrolls past in a
    /// second. This is the same trade the fallback already made, made better.
    ///
    /// Short of a hundred entries on purpose. The tail of any such list is
    /// guesswork about which of two names a person would rather read, and an
    /// unfamiliar extension falling through to "XYZ file" says exactly as much
    /// as it did before.
    /// </summary>
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        // Programs and the things that behave like them.
        ["exe"] = "Application",
        ["msi"] = "Installer",
        ["appimage"] = "Application",
        ["deb"] = "Package",
        ["rpm"] = "Package",
        ["flatpak"] = "Package",
        ["dll"] = "Library",
        ["so"] = "Library",
        ["dylib"] = "Library",
        ["bat"] = "Batch file",
        ["cmd"] = "Batch file",
        ["ps1"] = "PowerShell script",
        ["sh"] = "Shell script",
        ["desktop"] = "Launcher",

        // Text and documents.
        ["txt"] = "Text document",
        ["md"] = "Markdown document",
        ["rtf"] = "Rich text document",
        ["pdf"] = "PDF document",
        ["doc"] = "Word document",
        ["docx"] = "Word document",
        ["odt"] = "OpenDocument text",
        ["xls"] = "Excel workbook",
        ["xlsx"] = "Excel workbook",
        ["ods"] = "OpenDocument spreadsheet",
        ["ppt"] = "PowerPoint presentation",
        ["pptx"] = "PowerPoint presentation",
        ["odp"] = "OpenDocument presentation",
        ["csv"] = "Comma-separated values",
        ["epub"] = "E-book",

        // Pictures.
        ["png"] = "PNG image",
        ["jpg"] = "JPEG image",
        ["jpeg"] = "JPEG image",
        ["gif"] = "GIF image",
        ["bmp"] = "Bitmap image",
        ["webp"] = "WebP image",
        ["tif"] = "TIFF image",
        ["tiff"] = "TIFF image",
        ["heic"] = "HEIF image",
        ["svg"] = "Vector image",
        ["ico"] = "Icon",
        ["psd"] = "Photoshop image",
        ["raw"] = "Camera raw image",
        ["cr2"] = "Camera raw image",
        ["nef"] = "Camera raw image",

        // Sound and moving pictures.
        ["mp3"] = "MP3 audio",
        ["flac"] = "FLAC audio",
        ["wav"] = "WAV audio",
        ["ogg"] = "Ogg audio",
        ["opus"] = "Opus audio",
        ["m4a"] = "AAC audio",
        ["aac"] = "AAC audio",
        ["mp4"] = "MP4 video",
        ["mkv"] = "Matroska video",
        ["mov"] = "QuickTime video",
        ["avi"] = "AVI video",
        ["webm"] = "WebM video",
        ["wmv"] = "Windows Media video",

        // Archives.
        ["zip"] = "Zip archive",
        ["7z"] = "7-Zip archive",
        ["rar"] = "RAR archive",
        ["tar"] = "Tar archive",
        ["gz"] = "Gzip archive",
        ["bz2"] = "Bzip2 archive",
        ["xz"] = "XZ archive",
        ["zst"] = "Zstandard archive",
        ["iso"] = "Disc image",
        ["img"] = "Disc image",
        ["vhd"] = "Virtual disk",
        ["vhdx"] = "Virtual disk",

        // The ones a person editing this repository sees all day.
        ["json"] = "JSON file",
        ["xml"] = "XML file",
        ["yml"] = "YAML file",
        ["yaml"] = "YAML file",
        ["toml"] = "TOML file",
        ["ini"] = "Configuration file",
        ["conf"] = "Configuration file",
        ["log"] = "Log file",
        ["html"] = "HTML document",
        ["htm"] = "HTML document",
        ["css"] = "Stylesheet",
        ["js"] = "JavaScript file",
        ["ts"] = "TypeScript file",
        ["cs"] = "C# source file",
        ["py"] = "Python source file",
        ["rs"] = "Rust source file",
        ["go"] = "Go source file",
        ["c"] = "C source file",
        ["h"] = "C header file",
        ["cpp"] = "C++ source file",
        ["java"] = "Java source file",
        ["sql"] = "SQL script",
        ["db"] = "Database file",
        ["sqlite"] = "Database file",
        ["ttf"] = "Font",
        ["otf"] = "Font",
        ["woff"] = "Font",
        ["woff2"] = "Font",
        ["torrent"] = "Torrent file",
    };

    /// <summary>
    /// The Windows shortcut extension, in the shape
    /// <see cref="FileEntry.Extension"/> hands it out — without its dot.
    ///
    /// Public and platform-blind so the fact lives in one place: the properties
    /// provider asks it too, and two copies would drift into a listing that
    /// says "Shortcut" beside a properties window that says "LNK file".
    /// </summary>
    public static bool IsShortcut(ReadOnlySpan<char> extension)
        => extension.Equals("lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The freedesktop launcher extension, in the same shape and public for the
    /// same reason <see cref="IsShortcut"/> is.
    /// </summary>
    public static bool IsLauncher(ReadOnlySpan<char> extension)
        => extension.Equals("desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a launcher file calls itself, or null for a platform with no such
    /// thing — which is every platform but one.
    ///
    /// **A seam rather than a call, because Core may not reference the Linux
    /// assembly** and the answer is a parse of a freedesktop file. Set once
    /// from the composition root that already chose the platform, exactly the
    /// bargain <see cref="Naming"/> makes for the bin's name; null everywhere
    /// else, so a Windows build never asks and never pays.
    ///
    /// Consulted only for a row whose extension is already .desktop, so the
    /// per-row cost on every other file is one span comparison.
    /// </summary>
    public static Func<string, string?>? LauncherName { get; set; }

    /// <summary>
    /// The name a listing shows: the entry's own, except for a Windows
    /// shortcut, whose extension Explorer never displays.
    ///
    /// Hands back the SAME string instance whenever there is nothing to hide,
    /// which is every row but a handful. This runs once per visible row per
    /// bind, and a substring allocated per row while scrolling is the one cost
    /// this codebase does not pay.
    ///
    /// Cannot return empty: Extension is default when the dot sits at index 0,
    /// so a file literally named ".lnk" is a name rather than an extension and
    /// keeps every character.
    /// </summary>
    public static string DisplayName(FileEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Name) || entry.IsDirectory) return entry.Name ?? "";

        var extension = entry.Extension;

        if (OperatingSystem.IsWindows() && IsShortcut(extension))
            return entry.Name[..^(extension.Length + 1)];

        // **A launcher listed as "org.kde.konsole.desktop".** A .desktop file
        // is an application as far as a person browsing the folder is
        // concerned, and the row showed the file name — which under the
        // reverse-DNS convention KDE files its entries under is an id, and for
        // the rest is a lowercase fragment. The same trade the shortcut above
        // makes, on the other platform's shortcut, and it is the same trade in
        // the same place rather than an appeal to what any other file manager
        // does: nobody here can run one and say.
        //
        // Null from the seam is "no opinion" and keeps the file name: an
        // untrusted launcher, an unreadable one, and one with no Name= at all
        // all arrive that way. Costs one span comparison for every row that is
        // not a launcher — 0.1 µs per row measured — and, for one that is, a
        // file read the first time it is asked about.
        if (IsLauncher(extension) && LauncherName is { } read
            && read(entry.FullPath) is { Length: > 0 } named)
            return named;

        return entry.Name;
    }
}
