using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// Freedesktop naming: the shared mime database, and the user's own folder
/// names.
///
/// **Split out of the theme reader when that moved to Core, because this is the
/// part that was genuinely Linux.** The reader itself only parses index.theme
/// files and walks directories — nothing platform-specific — which is what lets
/// somebody on Windows point it at a downloaded Papirus and have it work. What
/// could not travel is here: a mime database that classifies by glob and by
/// content sniff, and folder names read from user-dirs.dirs.
/// </summary>
public sealed class XdgIconNaming : IIconNaming
{
    /// <summary>
    /// The user's own names for these come from user-dirs.dirs — a localised
    /// setup has "Documentos", so matching on the folder name would fail.
    ///
    /// Home, Desktop, Documents, Music, Pictures and Videos are handled by the
    /// reader through Environment.SpecialFolder, which resolves them on both
    /// platforms. These are the ones with no such equivalent.
    /// </summary>
    public IReadOnlyList<(string Path, string[] Names)> SpecialFolders()
    {
        var found = new List<(string, string[])>();

        foreach (var (key, names) in new (string, string[])[]
        {
            ("XDG_DOCUMENTS_DIR", ["folder-documents"]),
            ("XDG_DOWNLOAD_DIR",  ["folder-download", "folder-downloads"]),
            ("XDG_MUSIC_DIR",     ["folder-music"]),
            ("XDG_PICTURES_DIR",  ["folder-pictures"]),
            ("XDG_VIDEOS_DIR",    ["folder-videos"]),
            ("XDG_PUBLICSHARE_DIR", ["folder-publicshare", "folder-public"]),
            ("XDG_TEMPLATES_DIR", ["folder-templates"]),
        })
        {
            if (XdgUserDirs.Read(key) is { Length: > 0 } dir) found.Add((dir.TrimEnd('/'), names));
        }

        return found;
    }

    public IReadOnlyList<string> NamesFor(string path)
    {
        // **A launcher asked the mime database what it was.** That answer is
        // application/x-desktop for every one of them, so Konsole, Firefox and
        // Steam all resolved to the same grey page — while the file itself
        // named its icon in a key two lines from the top. Ahead of the mime
        // lookup, because everything below returns: the mime answer is not
        // wrong so much as useless here, and it would win if it went first.
        if (LauncherIcons(path) is { Count: > 0 } launcher) return launcher;

        // The glob database first: one parsed file rather than a process tree
        // per listing. Only a name it cannot classify — no extension, or an
        // unusual pattern — pays for a content sniff.
        var mime = SharedMimeInfo.ForPath(path);

        if (string.IsNullOrEmpty(mime))
        {
            // A dangling symlink lists but does not resolve, so there is nothing
            // to sniff. Spawning a process to be told that, once per entry, is
            // pure waste — and it is worth showing as a broken link rather than
            // as a generic file.
            if (!File.Exists(path) && !Directory.Exists(path))
                return ["inode-symlink", "emblem-symbolic-link", "text-x-generic"];

            mime = DesktopEntries.QueryMimeType(path);
        }

        // Empty rather than a guess: the reader's own extension table is a
        // better answer than "text-x-generic" for everything unclassified.
        if (string.IsNullOrEmpty(mime)) return [];

        // image/png → image-png, then image-x-generic, then the catch-all.
        // Themes name icons after the mime type with the slash replaced, and
        // fall back to the media type when they have nothing more specific.
        var flat = mime.Replace('/', '-');
        var media = mime.Split('/')[0];

        return [flat, $"{media}-x-generic", "application-x-generic", "text-x-generic"];
    }

    /// <summary>
    /// The theme names to try for a .desktop file, most specific first, or
    /// empty for a file that is not one or whose Icon= this must not believe.
    ///
    /// **The spec says the extension "should be omitted" and real entries carry
    /// one anyway.** Icon=firefox.png ships in the wild, and the theme index is
    /// keyed by file name WITHOUT its extension — so the value as written
    /// matched nothing and the launcher kept the generic icon it was already
    /// wearing. Verbatim first all the same, because a dot in an icon name is
    /// not automatically an extension: org.kde.konsole is the naming convention
    /// half of KDE follows, and stripping ".konsole" off it would ask for the
    /// wrong picture.
    ///
    /// The two fallbacks are what the row had before, kept so an entry with no
    /// Icon= at all, or one naming an icon this theme does not ship, lands on
    /// the launcher page rather than on nothing.
    ///
    /// **Case-insensitively, because the index this feeds is.** The first
    /// version pattern-matched the extension ordinally, so Icon=firefox.PNG
    /// kept its extension and matched nothing — while
    /// FreedesktopIconTheme's index is an OrdinalIgnoreCase dictionary keyed on
    /// the file name without its extension, where "firefox" would have hit. And
    /// the length test is not spare: Path.GetExtension(".png") answers ".png",
    /// so a degenerate Icon=.png stripped to the empty string and handed Resolve
    /// a name it could only search for and fail on.
    /// </summary>
    internal static IReadOnlyList<string> LauncherIcons(string path)
    {
        if (DesktopEntries.Launcher(path).Icon is not { Length: > 0 } icon) return [];

        var extension = Path.GetExtension(icon);

        var stripped = icon.Length > extension.Length
                       && (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                           || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
                           || extension.Equals(".xpm", StringComparison.OrdinalIgnoreCase))
            ? icon[..^4]
            : icon;

        return stripped == icon
            ? [icon, "application-x-desktop", "application-x-executable"]
            : [icon, stripped, "application-x-desktop", "application-x-executable"];
    }
}
