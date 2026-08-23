using System.Collections.Concurrent;
namespace Vaktari.Core.FileSystem;

/// <summary>
/// The freedesktop icon theme specification, as far as a file manager needs it.
///
/// Parsed straight from index.theme and the directory tree — the same approach
/// taken for kdeglobals, the XDG trash and the thumbnail cache. No binding to
/// keep in step with a Plasma release, and it works for any theme the user
/// installs, not only Breeze.
///
/// **In Core rather than in the Linux assembly, because the format is not
/// Linux.** Nothing here is a platform call: it reads index.theme files and
/// walks directories. Windows has no icon theme system of its own, but a person
/// who downloads Papirus or Tela has a folder in exactly this layout, and there
/// is no reason they should not be able to point at it. The only part that was
/// ever Linux-specific is where to look, which is now an argument.
/// </summary>
public sealed class FreedesktopIconTheme : IIconThemeProvider
{
    private readonly string[] _roots;
    private readonly List<string> _searchOrder = [];
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> _indexes =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string[]> _specialFolders;
    private readonly IIconNaming? _naming;
    private readonly string? _alsoInherits;

    /// <param name="roots">Where themes live. Null asks for the freedesktop
    /// defaults, which is what a Linux desktop wants; Windows passes the one
    /// folder the user pointed at.</param>
    /// <param name="naming">How this platform names icons for its files, or
    /// null to go by extension. A freedesktop desktop has a mime database that
    /// knows far more than any table of extensions; Windows does not.</param>
    /// <param name="alsoInherits">A theme to fall back on that index.theme does
    /// not name, sitting directly behind this one in the chain. See
    /// <see cref="VariantBase"/> — this exists for variants whose inheritance is
    /// expressed as symbolic links rather than as an Inherits= line.</param>
    public FreedesktopIconTheme(
        string? themeName,
        IReadOnlyList<string>? roots = null,
        IIconNaming? naming = null,
        string? alsoInherits = null)
    {
        _roots = roots is { Count: > 0 } ? [.. roots] : DefaultRoots();
        _naming = naming;
        _alsoInherits = alsoInherits;

        _specialFolders = BuildSpecialFolders();

        Reload(themeName);
    }

    /// <summary>The spec's own search path, in its own order.</summary>
    private static string[] DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (string.IsNullOrWhiteSpace(dataHome)) dataHome = Path.Combine(home, ".local", "share");

        return
        [
            Path.Combine(home, ".icons"),
            Path.Combine(dataHome, "icons"),
            "/usr/local/share/icons",
            "/usr/share/icons",
        ];
    }

    /// <summary>
    /// Where built indexes are kept between launches, or null to keep none.
    ///
    /// A passthrough so that the cache itself stays internal: this is the only
    /// thing outside Core that needs to say anything about it, and where
    /// per-user state lives is not a fact Core is entitled to know.
    /// </summary>
    public static string? IndexCacheFolder
    {
        get => IconIndexCache.Folder;
        set => IconIndexCache.Folder = value;
    }

    /// <summary>
    /// Reads a folder somebody downloaded and extracted, or null if it does not
    /// look like an icon theme.
    ///
    /// **The folder IS the theme, so its name is the theme's name and its
    /// parent is the root.** That is the shape of every theme archive: extract
    /// Papirus and you get a folder called Papirus with index.theme inside it,
    /// which is what a person will point at.
    ///
    /// Builds the index if there is none cached, which takes seconds on a large
    /// theme — see <see cref="FromCache"/> for the launch that must not.
    /// </summary>
    public static FreedesktopIconTheme? FromFolder(string? folder, IIconNaming? naming = null)
        => Read(folder, naming, cachedOnly: false);

    /// <summary>
    /// The same theme, but only when reading it is already paid for — every
    /// directory in its chain present in <see cref="IconIndexCache"/>. Null
    /// otherwise, which is the caller's cue to build it off the UI thread.
    ///
    /// **This is what stops the icons changing under you.** Building the index
    /// takes seconds, so it cannot happen before the window opens; but a launch
    /// that opens on the platform's icons and swaps to the theme a beat later
    /// is visibly wrong for that beat. A cache turns the ordinary launch — every
    /// one after the first — back into something a window can wait for.
    /// </summary>
    public static FreedesktopIconTheme? FromCache(string? folder, IIconNaming? naming = null)
        => Read(folder, naming, cachedOnly: true);

    private static FreedesktopIconTheme? Read(string? folder, IIconNaming? naming, bool cachedOnly)
    {
        // **Nullable, and that is not defensive padding — it shipped as a
        // crash.** A settings file written by an earlier version has no
        // iconThemeFolder key at all, and deserialization does not run property
        // initializers, so the string arrives null rather than empty. TrimEnd
        // then threw a NullReferenceException, which the catch below does not
        // cover, out of the MainWindow constructor: 0.8.0 could not start at
        // all for anybody upgrading.
        if (string.IsNullOrEmpty(folder)) return null;

        try
        {
            var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!Directory.Exists(trimmed)) return null;

            // index.theme is what makes a directory a theme rather than a
            // directory of pictures. Without it there is nothing to read the
            // sizes and inheritance out of.
            if (!File.Exists(Path.Combine(trimmed, "index.theme"))) return null;

            var name = Path.GetFileName(trimmed);
            var parent = Path.GetDirectoryName(trimmed);

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent)) return null;

            // **A variant is built on the theme beside it, and that belongs in
            // the chain whether or not the variant can stand on its own.**
            //
            // Papirus-Dark keeps its own recoloured artwork and links to Papirus
            // for everything else, and Inherits= is no help — it names
            // breeze-dark, which nobody here has. So the base goes behind it:
            // the variant's own icons still win, and Papirus fills the gaps the
            // links used to.
            //
            // Applied always rather than only as a rescue, because the holes are
            // not all-or-nothing. Dark resolves plenty on its own and still had
            // no folder icon at any size a listing asks for — its 48-pixel
            // folder is a link to a link, and following one hop finds a file
            // that is itself only another name. One line of chain covers every
            // depth of that.
            var theme = new FreedesktopIconTheme(name, [parent], naming, VariantBase(parent, name));

            // Every directory in the chain, or none of them. A theme half out
            // of cache would answer for the names its variant covers and stay
            // silent about the rest — icons missing rather than late, and
            // missing for as long as the process runs.
            if (cachedOnly && !theme.WarmFromCache()) return null;

            // **index.theme is still not proof that a theme WORKS.** A
            // structural check would pass a folder that resolves nothing at
            // all, so this asks it to produce an actual icon instead.
            return theme.Resolve(Probe, 48) is null ? null : theme;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names every theme ships for a plain file. A theme that can produce none
    /// of them cannot usefully draw a listing.
    /// </summary>
    private static readonly string[] Probe =
        ["text-x-generic", "text-plain", "application-x-generic"];

    /// <summary>
    /// The theme a variant is built on, found beside it — Papirus-Dark's
    /// Papirus, Tela-dark's Tela.
    ///
    /// By name, which is a convention rather than a rule, but it is the
    /// convention every variant follows and the alternative is asking the user
    /// to know why their theme is empty. Deliberately narrow: the base must be
    /// a real theme in the same folder, and the variant's name must extend it
    /// at a separator, so Papirus-Dark finds Papirus while Papirus never
    /// matches some unrelated Pap.
    /// </summary>
    private static string? VariantBase(string parent, string name)
    {
        string? best = null;

        foreach (var candidate in Directory.EnumerateDirectories(parent))
        {
            var leaf = Path.GetFileName(candidate);

            if (leaf.Length == 0 || leaf.Length >= name.Length) continue;
            if (!name.StartsWith(leaf, StringComparison.OrdinalIgnoreCase)) continue;
            if (name[leaf.Length] is not ('-' or '_' or '.')) continue;
            if (!File.Exists(Path.Combine(candidate, "index.theme"))) continue;

            // Longest wins, so Foo-Dark-Compact prefers Foo-Dark over Foo.
            if (best is null || leaf.Length > best.Length) best = leaf;
        }

        return best;
    }

    public void Reload(string? themeName)
    {
        ThemeName = string.IsNullOrWhiteSpace(themeName) ? "hicolor" : themeName;

        _searchOrder.Clear();
        _cache.Clear();
        _indexes.Clear();

        BuildSearchOrder(ThemeName, depth: 0);

        // Directly behind the theme itself, ahead of anything index.theme
        // names: it is the theme this one is made of, not a distant relative.
        if (_alsoInherits is { Length: > 0 } && !_searchOrder.Contains(_alsoInherits))
            _searchOrder.Insert(Math.Min(1, _searchOrder.Count), _alsoInherits);

        // hicolor is the specified last resort and must always be present in
        // the chain, whether or not a theme names it.
        if (!_searchOrder.Contains("hicolor")) _searchOrder.Add("hicolor");

        Console.Error.WriteLine(
            $"[vaktari] icon theme '{ThemeName}', chain: {string.Join(" > ", _searchOrder)}");
    }

    public string ThemeName { get; private set; } = "hicolor";

    /// <summary>
    /// Themes inherit, sometimes several deep — Breeze Dark inherits Breeze,
    /// which inherits hicolor. Depth-limited because a malformed index.theme
    /// can describe a cycle.
    /// </summary>
    private void BuildSearchOrder(string theme, int depth)
    {
        if (depth > 6 || _searchOrder.Contains(theme)) return;

        _searchOrder.Add(theme);

        foreach (var root in _roots)
        {
            var index = Path.Combine(root, theme, "index.theme");
            if (!File.Exists(index)) continue;

            try
            {
                foreach (var line in File.ReadLines(index))
                {
                    if (!line.StartsWith("Inherits=", StringComparison.Ordinal)) continue;

                    foreach (var parent in line[9..].Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                                StringSplitOptions.TrimEntries))
                        BuildSearchOrder(parent, depth + 1);

                    break;
                }
            }
            catch
            {
                // Unreadable index: the theme still works by directory scan.
            }

            break;
        }
    }

    public string? Resolve(IReadOnlyList<string> names, int size)
    {
        foreach (var name in names)
        {
            var key = $"{name}@{size}";

            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached is not null) return cached;
                continue;
            }

            var found = Search(name, size);
            _cache[key] = found;

            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// Walks themes in inheritance order, and within a theme prefers the
    /// closest size. "scalable" wins ties because an SVG renders correctly at
    /// any size, whereas a fixed raster upscales badly.
    /// </summary>
    /// <summary>
    /// One recursive scan per theme directory, cached. Breeze ships around
    /// thirty thousand files; enumerating it per icon name, per theme in the
    /// chain, per search root — which is what the first version did — is not
    /// slow, it is unusable.
    /// </summary>
    private Dictionary<string, List<string>> IndexOf(string themeDir)
        => _indexes.GetOrAdd(themeDir, dir =>
        {
            // Before the scan, because the scan is the entire cost. See
            // IconIndexCache for what is remembered and how it is known to
            // still be true.
            if (IconIndexCache.Load(dir) is { } remembered) return remembered;

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*",
                             new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                             }))
                {
                    var extension = Path.GetExtension(file);
                    if (extension is not (".svg" or ".png")) continue;

                    var name = Path.GetFileNameWithoutExtension(file);

                    if (!map.TryGetValue(name, out var paths))
                        map[name] = paths = [];

                    paths.Add(file);
                }
            }
            catch
            {
                // Unreadable theme directory: it contributes nothing.
            }

            AddAliases(dir, map);

            IconIndexCache.Save(dir, map);

            return map;
        });

    /// <summary>
    /// Loads every directory this theme searches from cache, or answers false
    /// having loaded none of it.
    ///
    /// The same <c>Path.Combine(root, theme)</c> and the same existence test
    /// <see cref="Search"/> uses, because a key formed any other way would warm
    /// a directory the search never asks for and leave the one it does.
    /// </summary>
    private bool WarmFromCache()
    {
        foreach (var theme in _searchOrder)
        {
            foreach (var root in _roots)
            {
                var themeDir = Path.Combine(root, theme);

                if (!Directory.Exists(themeDir)) continue;

                if (IconIndexCache.Load(themeDir) is not { } remembered) return false;

                _indexes[themeDir] = remembered;
            }
        }

        return true;
    }

    /// <summary>
    /// Folds in what the theme's symbolic links meant.
    ///
    /// **Windows creates no symbolic links without Developer Mode**, so a theme
    /// that was unpacked here has its links written down instead — see
    /// <see cref="IconThemeArchive"/>. Papirus is roughly forty thousand of
    /// them, and without this the theme arrives with holes precisely where
    /// files and folders are.
    ///
    /// Absent for a theme somebody extracted themselves, in which case this does
    /// nothing and the theme reads exactly as it did before.
    /// </summary>
    private static void AddAliases(string dir, Dictionary<string, List<string>> map)
    {
        var index = Path.Combine(dir, IconThemeArchive.AliasIndex);

        if (!File.Exists(index)) return;

        // A theme links whole folders as well as single icons, and the same
        // folder many times over — Papirus-Dark points every size it has at
        // Papirus. Expanded once each.
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // **The whole index first, because an alias may name another
            // alias.** A chained target is not on disk — it is only another
            // line in this file — so checking the filesystem for it finds
            // nothing and the entry used to be dropped in silence.
            //
            // Kora is built this way and it cost the theme entirely: all three
            // names FromFolder probes with are chained, so the theme resolved
            // nothing, was refused as not an icon theme, and never appeared in
            // Settings — while its folder icon, a real file, would have worked
            // perfectly. Measured on Kora 2.0.4: of 13,587 aliases, 2,141 point
            // at another alias rather than at a file.
            var links = new Dictionary<string, string>(PathComparison);
            var pairs = new List<(string From, string To)>();

            foreach (var line in File.ReadLines(index))
            {
                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab <= 0) continue;

                var from = line[..tab];
                var to = line[(tab + 1)..];

                links[Key(dir, from)] = to;
                pairs.Add((from, to));
            }

            foreach (var (alias, target) in pairs)
            {
                if (Follow(dir, target, links) is not { } to) continue;

                var from = Path.GetFullPath(Path.Combine(dir, alias));

                if (File.Exists(to))
                {
                    // The alias is another NAME for that file, which is the
                    // whole of what an icon alias is.
                    Add(map, Path.GetFileNameWithoutExtension(from), to);
                }
                else if (Directory.Exists(to) && folders.Add(to))
                {
                    // A linked folder makes every icon in it reachable here. The
                    // real path is what gets recorded, so the size written in it
                    // is still the size that gets scored.
                    foreach (var file in Directory.EnumerateFiles(to, "*",
                                 new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     IgnoreInaccessible = true,
                                 }))
                    {
                        if (Path.GetExtension(file) is not (".svg" or ".png")) continue;

                        Add(map, Path.GetFileNameWithoutExtension(file), file);
                    }
                }
            }
        }
        catch
        {
            // An unreadable index costs the aliases and nothing else.
        }

        static void Add(Dictionary<string, List<string>> map, string name, string path)
        {
            if (!map.TryGetValue(name, out var paths)) map[name] = paths = [];

            paths.Add(path);
        }
    }

    /// <summary>
    /// Walks an alias to the file or folder it eventually names, or null where
    /// it names nothing.
    ///
    /// **A visited set, not a depth limit, is what guarantees this ends.**
    /// Kora contains six genuine cycles — an icon aliased to another that
    /// aliases back — and a walk bounded only by a number would follow each of
    /// them to that number every time it was asked. The set is also exact:
    /// it stops the moment a chain repeats rather than at an arbitrary point
    /// that has to be guessed high enough.
    ///
    /// Guessing it would have gone wrong here. The chains in Kora need up to
    /// four hops, and thirty-one aliases need the fourth — so a limit picked
    /// from a glance at the common cases would have dropped them silently,
    /// which is the failure this whole method exists to fix.
    /// </summary>
    private static string? Follow(string dir, string target, Dictionary<string, string> links)
    {
        var full = Path.GetFullPath(Path.Combine(dir, target));

        // Five aliases in six name a real file directly. Nothing is allocated
        // for those.
        if (File.Exists(full) || Directory.Exists(full)) return full;

        var seen = new HashSet<string>(PathComparison);
        var cursor = target;

        while (true)
        {
            var key = Key(dir, cursor);

            if (!seen.Add(key)) return null;
            if (!links.TryGetValue(key, out var next)) return null;

            cursor = next;
            full = Path.GetFullPath(Path.Combine(dir, cursor));

            if (File.Exists(full) || Directory.Exists(full)) return full;
        }
    }

    /// <summary>
    /// How a path is named in the alias index: relative to the theme folder,
    /// forward slashes, so a target written by the unpacker and a target
    /// reached by following one match as the same key.
    /// </summary>
    private static string Key(string dir, string relative) =>
        Path.GetRelativePath(dir, Path.GetFullPath(Path.Combine(dir, relative)))
            .Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// The icon a theme chain offers for a name, at about a size.
    ///
    /// **A theme earlier in the chain wins, but not at any size.** This used to
    /// take the first theme that had the name at all: Papirus-Dark keeps a real
    /// 16-pixel folder and gets its larger ones by linking to Papirus, so a
    /// 48-pixel row was drawn with 16-pixel artwork while a perfectly good
    /// 48-pixel icon sat one theme further down the chain.
    ///
    /// So a theme is only allowed to answer if what it has is large enough to
    /// use, and the closest icon anywhere is kept as the answer of last resort —
    /// which is roughly what the specification describes, and the shape every
    /// implementation of it ends up with.
    /// </summary>
    private string? Search(string name, int size)
    {
        string? nearestAnywhere = null;
        var nearestScore = int.MaxValue;

        foreach (var theme in _searchOrder)
        {
            foreach (var root in _roots)
            {
                var themeDir = Path.Combine(root, theme);
                if (!Directory.Exists(themeDir)) continue;

                if (!IndexOf(themeDir).TryGetValue(name, out var candidates)) continue;

                string? best = null;
                var bestScore = int.MaxValue;

                foreach (var candidate in candidates)
                {
                    var found = SizeOf(candidate);
                    var score = Distance(found, size);

                    if (score < nearestScore)
                    {
                        nearestScore = score;
                        nearestAnywhere = candidate;
                    }

                    if (!Usable(found, size) || score >= bestScore) continue;

                    bestScore = score;
                    best = candidate;
                }

                if (best is not null) return best;
            }
        }

        return nearestAnywhere;
    }

    /// <summary>
    /// The size a directory serves, read from its path — themes lay out as
    /// theme/context/22/icon.svg or theme/22x22/context/icon.png, and both
    /// forms put the number in a path segment. Zero for scalable, and -1 where
    /// the path says nothing.
    /// </summary>
    private static int SizeOf(string path)
    {
        // **Both separators, and from the leaf inwards.**
        //
        // Splitting on '/' alone was correct while this was Linux-only and
        // silently broke the moment a Windows path reached it: the whole path
        // came back as one segment and every candidate scored the same.
        //
        // Backwards because the segments before the theme are somebody's
        // folders, not ours. Read forwards, a theme unpacked under a folder
        // called 2024 gave every icon in it a size of 2024.
        var segments = path.Split('/', '\\');

        // Skipping the file name: an icon may perfectly well be called 24.png.
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (segments[i].Equals("scalable", StringComparison.OrdinalIgnoreCase)) return 0;

            var digits = segments[i].Split('x')[0];
            if (int.TryParse(digits, out var found) && found > 0) return found;
        }

        return -1;
    }

    /// <summary>Scalable beats every raster, because an SVG renders correctly at
    /// any size where a fixed one does not.</summary>
    private static int Distance(int found, int wanted) => found switch
    {
        0 => 1,
        < 0 => int.MaxValue - 1,
        _ => Math.Abs(found - wanted) * 2 + 2,
    };

    /// <summary>
    /// Whether an icon is big enough to be worth using, rather than blown up to
    /// fill a row. Larger is always fine — scaling down is what every icon set
    /// is designed for — and a quarter under is close enough not to show.
    /// </summary>
    private static bool Usable(int found, int wanted) => found == 0 || found * 4 >= wanted * 3;

    /// <summary>
    /// Special folders get their own icon names, which is why Dolphin shows a
    /// distinct Documents, Downloads and Music folder while asking for
    /// "inode-directory" everywhere gives one generic folder for all of them.
    /// Names follow the freedesktop icon naming spec.
    /// </summary>
    private IReadOnlyList<string> FolderNames(string path)
    {
        var trimmed = Trim(path);
        if (trimmed.Length == 0) return ["drive-harddisk", "folder-root", "inode-directory", "folder"];

        if (_specialFolders.TryGetValue(trimmed, out var special))
            return [.. special, "inode-directory", "folder"];

        return ["inode-directory", "folder"];
    }

    /// <summary>
    /// The folders that get an icon of their own, by their real paths.
    ///
    /// **Read from the platform, never matched by name.** A localised setup
    /// calls Documents "Documentos", and on Windows somebody may well have
    /// moved Downloads to another drive.
    /// </summary>
    private Dictionary<string, string[]> BuildSpecialFolders()
    {
        var map = new Dictionary<string, string[]>(PathComparison);

        void Add(Environment.SpecialFolder folder, params string[] names)
        {
            var path = Environment.GetFolderPath(folder);

            if (path.Length > 0) map[Trim(path)] = names;
        }

        // Resolved by the runtime on both platforms, so these need no help.
        Add(Environment.SpecialFolder.UserProfile, "user-home");
        Add(Environment.SpecialFolder.Desktop, "user-desktop", "folder-desktop");
        Add(Environment.SpecialFolder.MyDocuments, "folder-documents");
        Add(Environment.SpecialFolder.MyMusic, "folder-music");
        Add(Environment.SpecialFolder.MyPictures, "folder-pictures");
        Add(Environment.SpecialFolder.MyVideos, "folder-videos");

        // Anything the platform knows and the runtime does not — Downloads,
        // Templates, Public — comes from the naming seam.
        foreach (var (path, names) in _naming?.SpecialFolders() ?? [])
            if (path.Length > 0) map[Trim(path)] = names;

        return map;
    }

    private static string Trim(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');

    /// <summary>
    /// Case matters on one platform and not the other, and a folder map that
    /// disagrees with its filesystem misses every special folder it holds.
    /// </summary>
    private static StringComparer PathComparison =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<string> NamesFor(string path, bool isDirectory)
    {
        if (isDirectory) return FolderNames(path);

        // The platform's own answer where it has one: on a freedesktop system
        // that is the shared mime database, which knows far more than any table
        // of extensions ever will.
        if (_naming?.NamesFor(path) is { Count: > 0 } named) return named;

        return ByExtension(path);
    }

    /// <summary>
    /// Icon names from the extension alone, for a platform with no mime
    /// database — which is Windows.
    ///
    /// **Deliberately small.** These are freedesktop names, so what matters is
    /// that the common cases land on names themes actually ship. The generic
    /// fallbacks cover the rest, and a type the theme has nothing for falls
    /// through to the drawn set anyway, which is a reasonable icon rather than
    /// a blank.
    /// </summary>
    private static IReadOnlyList<string> ByExtension(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        var mime = extension switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" or "gif" or "bmp" or "webp" or "tiff" or "svg" => "image/" + extension,
            "mp3" or "flac" or "ogg" or "wav" or "aac" or "opus" => "audio/" + extension,
            "mp4" or "mkv" or "webm" or "avi" or "mov" => "video/" + extension,
            "pdf" => "application/pdf",
            "zip" or "gz" or "bz2" or "xz" or "7z" or "rar" or "tar" => "application/x-archive",
            "exe" or "msi" or "com" or "scr" => "application/x-executable",
            "doc" or "docx" or "odt" or "rtf" => "application/msword",
            "xls" or "xlsx" or "ods" => "application/vnd.ms-excel",
            "ppt" or "pptx" or "odp" => "application/vnd.ms-powerpoint",
            "html" or "htm" => "text/html",
            "cs" or "js" or "ts" or "py" or "rs" or "go" or "c" or "h" or "cpp" or "java"
                or "rb" or "sh" or "ps1" or "bat" or "cmd" => "text/x-script",
            "txt" or "md" or "log" or "csv" or "xml" or "json" or "yaml" or "yml"
                or "toml" or "ini" or "cfg" or "conf" => "text/plain",
            _ => "",
        };

        if (mime.Length == 0) return ["text-x-generic", "application-x-generic"];

        var flat = mime.Replace('/', '-');
        var media = mime.Split('/')[0];

        return [flat, $"{media}-x-generic", "application-x-generic", "text-x-generic"];
    }
}
