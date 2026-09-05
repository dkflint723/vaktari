using System.Diagnostics;
using Vaktari.Core.FileSystem;

using Vaktari.Core;

namespace Vaktari.Linux;

/// <summary>
/// Reads the freedesktop desktop-entry database to answer "what can open this?".
///
/// There is no command that shows a chooser dialog for us — xdg-open only ever
/// launches the default. So the list is assembled the same way the desktop
/// itself assembles it: MIME type, then mimeinfo.cache, then the .desktop files.
/// </summary>
public static class DesktopEntries
{
    /// <summary>
    /// Every directory the spec says a .desktop file may live in, nearest
    /// first. Internal because the chooser scans the same set the lookup
    /// walks — a scan over a different list would offer applications
    /// <see cref="FindDesktopFile"/> then cannot find again.
    /// </summary>
    internal static IEnumerable<string> ApplicationDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(home, ".local", "share");

        yield return Path.Combine(dataHome, "applications");

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dataDirs))
            dataDirs = "/usr/local/share:/usr/share";

        foreach (var dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "applications");

        yield return "/var/lib/flatpak/exports/share/applications";
        yield return Path.Combine(dataHome, "flatpak", "exports", "share", "applications");
    }

    /// <summary>
    /// Results, so cycling a listing does not re-sniff the same file. Keyed by
    /// path because a file with no extension is classified by its CONTENT —
    /// there is no cheaper key.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        MimeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Hard cap on how many of these may run at once.
    ///
    /// This spawns a process, and it is called PER ROW from the thread pool by
    /// RowIcon. `SharedMimeInfo` classifies anything with a recognisable
    /// extension, so this was meant to be rare — but a folder of extensionless
    /// files (scripts, /usr/bin, or 300 files made by `touch`) makes it
    /// universal. Measured: 300 rows across three layouts queued ~900 spawns,
    /// each parking a pool thread in Task.WaitAll. The pool injected threads to
    /// 83 trying to keep up, and an unrelated navigation that needed one waited
    /// 44 SECONDS to list eight files.
    /// </summary>
    private static readonly SemaphoreSlim Sniffs = new(4, 4);

    /// <summary>
    /// A budget of its own for the sniffs a person is waiting on.
    ///
    /// **Sharing the row-icon budget is what made "Open with" disappear.**
    /// Sniffs is spent by the row icons from the thread pool, four at a time
    /// and up to two seconds each — so a menu asking Wait(0) at the wrong
    /// instant got nothing, and nothing meant ForFile returned an empty list
    /// and the submenu was not drawn at all. Intermittently, which is the worst
    /// way for a menu to be missing.
    ///
    /// Waiting on the same semaphore would not fix it: permits are handed out
    /// unordered, so behind a queue of background spawns any bounded wait times
    /// out just as often. The interactive callers get permits nobody else can
    /// take. Two, because there is one selection and one properties dialog.
    /// </summary>
    private static readonly SemaphoreSlim AskedFor = new(2, 2);

    /// <summary>
    /// How long an interactive caller waits for one of its own two permits.
    /// Long enough to outlast a stuck xdg-mime, which self-kills at two
    /// seconds, and short enough that arrow-keying through extensionless files
    /// cannot park threads indefinitely.
    /// </summary>
    private const int InteractiveWaitMs = 2500;

    /// <summary>Background callers — the row icons. One try, never a wait.</summary>
    public static string QueryMimeType(string path) => QueryMimeType(path, waiting: false);

    /// <summary>
    /// <paramref name="waiting"/> is true for a caller a person is sitting in
    /// front of: the context menu, and the properties dialog. Those spend their
    /// own budget and are willing to wait for it.
    /// </summary>
    internal static string QueryMimeType(string path, bool waiting)
    {
        if (MimeCache.TryGetValue(path, out var cached)) return cached;

        if (SniffOverride is { } stub) return stub(path, waiting);

        // Background: try-acquire, never wait. A thread that blocks here is a
        // pool thread taken out of circulation, which is the whole failure this
        // budget exists for — so when it is spent we return "" and the row
        // shows a generic icon. Deliberately NOT cached: the next pass over
        // that row sniffs it properly, so the listing converges.
        var budget = waiting ? AskedFor : Sniffs;

        if (!budget.Wait(waiting ? InteractiveWaitMs : 0)) return "";

        try
        {
            var info = new ProcessStartInfo("xdg-mime")
            {
                RedirectStandardOutput = true,
                // Redirected and discarded: xdg-mime writes its own complaints
                // to stderr, and without this they land in ours. A broken
                // symlink in a listing produced a line of console noise per
                // file, from a child process, looking like our diagnostics.
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("query");
            info.ArgumentList.Add("filetype");
            info.ArgumentList.Add(path);

            using var process = Process.Start(info);
            if (process is null) return "";

            // Both pipes started before either is awaited. Draining stdout to
            // completion and only THEN stderr does not solve the problem the
            // comment below was written for: if the child fills stderr while we
            // are blocked on stdout, both sides stop, and ReadToEnd cannot time
            // out. xdg-mime writes its own complaints to stderr, so this is the
            // stream that fills.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("desktop-entries", ex); }
                return "";
            }

            return Remember(path, Task.WaitAll(new Task[] { stdout, stderr }, 5_000)
                ? stdout.Result.Trim()
                : "");
        }
        catch
        {
            return "";
        }
        finally
        {
            budget.Release();
        }
    }

    /// <summary>
    /// Caches a real answer, including an empty one — a file we could not
    /// classify will not classify next time either, and re-spawning to learn
    /// that again is exactly the cost being removed. Bounded so a long session
    /// over many folders cannot grow without limit.
    /// </summary>
    private static string Remember(string path, string mime)
    {
        if (MimeCache.Count > 8000) MimeCache.Clear();

        MimeCache[path] = mime;
        return mime;
    }

    /// <summary>
    /// Stands in for the sniff, and records which budget was asked against.
    ///
    /// The states worth testing — the budget spent, and a background caller
    /// that must never wait — cannot be arranged from outside: they need four
    /// other threads sniffing at the instant of the call. Null in the
    /// application.
    /// </summary>
    internal static Func<string, bool, string>? SniffOverride { get; set; }

    public static IReadOnlyList<LaunchOption> ForFile(string path)
    {
        // **The glob database was never consulted for this menu**, though the
        // row icons have read it since they were written — so every right-click
        // on a file with an ordinary extension spawned an xdg-mime process to
        // learn what "*.txt" already says. Reading a text file the desktop
        // installed is free; spawning is not.
        var mime = SharedMimeInfo.ForPath(path);

        // Only what globs cannot answer reaches the sniff, and it waits,
        // because somebody is looking at the menu.
        if (string.IsNullOrEmpty(mime)) mime = QueryMimeType(path, waiting: true);

        if (string.IsNullOrEmpty(mime)) return [];

        // Ordered, deduplicated: the default handler first, then everything
        // else that claims the type.
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id)
        {
            if (id.Length > 0 && seen.Add(id)) ids.Add(id);
        }

        var (preferred, removed) = Resolve(
            MimeAppsLists().Select(path => ReadMimeApps(path, mime)));

        foreach (var id in preferred) Add(id);

        // **mimeinfo.cache went in unfiltered**, so an application taken away
        // under [Removed Associations] came straight back in as an ordinary
        // association — un-choosing it in the desktop's settings moved it down
        // the menu instead of off it. The cache is generated from the .desktop
        // files themselves and knows nothing about anybody's choices, which is
        // why it sits below every mimeapps.list in the spec's order and why
        // every removal any of those files made applies to it.
        foreach (var id in AssociationsFor(mime))
            if (!removed.Contains(id))
                Add(id);

        var options = new List<LaunchOption>();

        foreach (var id in ids)
        {
            if (FindDesktopFile(id) is not { } file) continue;
            var (name, _, _, noDisplay, terminal) = ReadEntry(file);
            if (noDisplay || string.IsNullOrEmpty(name)) continue;

            // Offered only if there is something to run it in. An entry that
            // needs a console, on a machine with no terminal emulator, is a
            // row that can only do nothing.
            if (terminal && Terminals.Count == 0) continue;

            options.Add(new LaunchOption(name, id));
        }

        return options;
    }

    /// <summary>
    /// Everything installed, whatever it claims to open.
    ///
    /// This is the list behind "Choose another app…", and it is deliberately
    /// NOT narrowed by the file: <see cref="ForFile"/> already answers "what
    /// claims this type", and the case the chooser exists for is the type
    /// nothing claims — where a list narrowed by the same rule offers the same
    /// nothing.
    ///
    /// Pure, and given its directories rather than reading them, because the
    /// states worth pinning are all states of the DATABASE — a hidden entry, a
    /// console entry on a machine with no terminal, the same id in two
    /// directories — and none of them can be arranged by asking a real machine
    /// politely. It also makes the scan testable on an agent that has no
    /// desktop at all, which is where this was written.
    /// </summary>
    internal static List<LaunchOption> Scan(
        IEnumerable<string> directories,
        IReadOnlyList<Vaktari.Core.FileSystem.TerminalOption> terminals)
    {
        var found = new Dictionary<string, LaunchOption>(StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            // SafeWalk rather than EnumerateFiles(AllDirectories), which throws
            // from the middle of the sequence on the first unreadable folder
            // and takes every entry after it with it.
            foreach (var entry in Vaktari.Core.FileSystem.SafeWalk.Descend(directory))
            {
                if (!entry.Path.EndsWith(".desktop", StringComparison.Ordinal)) continue;

                var id = IdFor(directory, entry.Path);

                // **The id does not always survive the trip back.**
                // FindDesktopFile looks for a nested entry by turning EVERY
                // dash into a separator, so an entry whose own file name has
                // one is looked for somewhere that does not exist — measured:
                // applications/kde/google-chrome.desktop scans as
                // kde-google-chrome.desktop and resolves to kde/google/chrome.
                // Launch then refuses, and OpenWith answers a refusal by
                // opening the DEFAULT application, which is the same silent
                // wrong answer an Exec-less entry gives. Dropped rather than
                // offered, for that reason.
                var relative = Path.GetRelativePath(directory, entry.Path);

                if (!string.Equals(id, relative, StringComparison.Ordinal)
                    && !string.Equals(id.Replace('-', Path.DirectorySeparatorChar), relative,
                                      StringComparison.Ordinal))
                    continue;

                // **The nearer directory wins, and ApplicationDirs yields the
                // user's own first.** A ~/.local/share/applications entry with
                // the same id as a /usr/share one is an override of it, and
                // taking the system copy would show the name the person
                // replaced.
                if (found.ContainsKey(id)) continue;

                var (name, exec, _, noDisplay, terminal) = ReadEntry(entry.Path);

                if (noDisplay || name.Length == 0) continue;

                // An entry with no Exec= is one Launch already refuses, and a
                // refused launch falls back to the DEFAULT application — so
                // offering it would open the wrong thing rather than nothing,
                // which is worse.
                if (exec.Length == 0) continue;

                // The same rule ForFile applies, for the same reason: an entry
                // that needs a console, on a machine with no terminal
                // emulator, is a row that can only do nothing.
                if (terminal && terminals.Count == 0) continue;

                found[id] = new LaunchOption(name, id);
            }
        }

        // By name, because that is the only thing the list shows and a person
        // scanning it reads it alphabetically. The id breaks ties so that two
        // applications called "Text editor" are ordered by the database rather
        // than by the walk: found is insert-only, so its values come out in
        // walk order, and OrderBy is stable and keeps that order for ties. The
        // walk yields a directory's own files before it descends, so without
        // the tie-break the pair's order would depend on how deep each one sat.
        return [.. found.Values
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The desktop file id for a file found under one of the application
    /// directories: its path relative to that directory, with the separators
    /// turned into dashes.
    ///
    /// **Not the file name.** An entry at applications/kde/konsole.desktop is
    /// "kde-konsole.desktop" to everything that reads the database, and it is
    /// the spelling <see cref="FindDesktopFile"/> undoes to find the file
    /// again — so a scan that reported "konsole.desktop" would produce a row
    /// that could never be launched.
    ///
    /// It does not round-trip in every case — see the guard in
    /// <see cref="Scan"/>.
    /// </summary>
    internal static string IdFor(string directory, string file)
        => Path.GetRelativePath(directory, file)
               .Replace(Path.DirectorySeparatorChar, '-');

    /// <summary>
    /// Every mimeapps.list the spec says to consult, in its precedence order.
    ///
    /// **Two of the six kinds were read and the rest were not.** The
    /// desktop-specific files are where Plasma and GNOME put the choices their
    /// own settings pages write, and the ones under the system data directories
    /// are where a distribution puts its defaults — so "Open with" disagreed
    /// with the desktop's own answer on a machine configured through either.
    ///
    /// $XDG_CURRENT_DESKTOP may name several desktops, colon separated, most
    /// specific first; each gets its own file ahead of the plain one.
    /// </summary>
    internal static IEnumerable<string> MimeAppsLists()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var configHome = Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        var dataHome = Env("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");

        var desktops = (Env("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant())
            .ToList();

        IEnumerable<string> In(string directory)
        {
            foreach (var desktop in desktops)
                yield return Path.Combine(directory, $"{desktop}-mimeapps.list");

            yield return Path.Combine(directory, "mimeapps.list");
        }

        foreach (var path in In(configHome)) yield return path;

        foreach (var dir in (Env("XDG_CONFIG_DIRS") ?? "/etc/xdg")
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var path in In(dir)) yield return path;

        foreach (var path in In(Path.Combine(dataHome, "applications"))) yield return path;

        foreach (var dir in (Env("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share")
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var path in In(Path.Combine(dir, "applications"))) yield return path;
    }

    private static string? Env(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>
    /// What a mimeapps.list says about one type: the applications it prefers,
    /// in order, and the ones it has taken away.
    ///
    /// **The group was not read at all** — any line beginning with the type was
    /// taken as a default, wherever it appeared. So an application listed under
    /// [Removed Associations], which is the file's way of saying "never this
    /// one for this type", was read as the FIRST choice for it. Un-choosing an
    /// application in the desktop's settings made it the default here.
    /// </summary>
    internal static (List<string> Preferred, List<string> Removed) ReadMimeApps(
        string path, string mime)
    {
        var preferred = new List<string>();
        var removed = new List<string>();

        try
        {
            if (!File.Exists(path)) return (preferred, removed);

            List<string>? into = null;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();

                if (line.StartsWith('['))
                {
                    // Added and Default both name applications this file wants
                    // offered; only the group heading tells them apart, and for
                    // an "open with" list the distinction does not change what
                    // is shown.
                    into = line switch
                    {
                        "[Default Applications]" or "[Added Associations]" => preferred,
                        "[Removed Associations]" => removed,
                        _ => null,
                    };

                    continue;
                }

                if (into is null || !line.StartsWith(mime + "=", StringComparison.Ordinal)) continue;

                into.AddRange(line[(mime.Length + 1)..]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("mimeapps", e);
        }

        return (preferred, removed);
    }

    /// <summary>
    /// Folds the files' answers into one list, nearest first, and keeps the
    /// removals it gathered on the way.
    ///
    /// **A removal in a nearer file beats a preference in a further one.** That
    /// is the whole point of [Removed Associations]: the distribution offers
    /// something and the person says no. Removals are gathered AS the walk
    /// goes, so a file only ever overrides the ones after it — a removal
    /// written by a system file cannot veto a choice the person made above it,
    /// which is the same rule read the other way round and the one that would
    /// be silently wrong if the sets were gathered up front.
    ///
    /// The gathered set is handed back rather than dropped because there is
    /// one more source BELOW every file walked here — mimeinfo.cache — and the
    /// same removals have to reach it. Eager rather than an iterator for that
    /// reason: the removals are only complete once the last file has been read,
    /// so there is nothing to hand back until the walk has finished anyway.
    /// </summary>
    internal static (List<string> Preferred, HashSet<string> Removed) Resolve(
        IEnumerable<(List<string> Preferred, List<string> Removed)> files)
    {
        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (preferred, taken) in files)
        {
            foreach (var id in preferred)
                if (!removed.Contains(id) && seen.Add(id))
                    chosen.Add(id);

            foreach (var id in taken) removed.Add(id);
        }

        return (chosen, removed);
    }

    private static IEnumerable<string> AssociationsFor(string mime)
    {
        foreach (var dir in ApplicationDirs())
        {
            var cache = Path.Combine(dir, "mimeinfo.cache");
            if (!File.Exists(cache)) continue;

            foreach (var line in File.ReadLines(cache))
            {
                if (!line.StartsWith(mime + "=", StringComparison.Ordinal)) continue;

                foreach (var id in line[(mime.Length + 1)..]
                             .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return id;
            }
        }
    }

    private static string? FindDesktopFile(string id)
    {
        foreach (var dir in ApplicationDirs())
        {
            var direct = Path.Combine(dir, id);
            if (File.Exists(direct)) return direct;

            // Ids can encode subdirectories with dashes, e.g.
            // kde-konsole.desktop living in applications/kde/konsole.desktop.
            var dashed = id.Replace('-', Path.DirectorySeparatorChar);
            var nested = Path.Combine(dir, dashed);
            if (File.Exists(nested)) return nested;
        }

        return null;
    }

    internal static (string Name, string Exec, string Icon, bool NoDisplay, bool Terminal)
        ReadEntry(string desktopFile)
    {
        string name = "", exec = "", icon = "";
        var noDisplay = false;
        var terminal = false;
        var inMainSection = false;

        try
        {
            foreach (var raw in File.ReadLines(desktopFile))
            {
                var line = raw.Trim();

                if (line.StartsWith('['))
                {
                    // Only the first group describes the application itself;
                    // later [Desktop Action ...] groups are alternate launches.
                    inMainSection = line == "[Desktop Entry]";
                    continue;
                }

                if (!inMainSection) continue;

                if (name.Length == 0 && line.StartsWith("Name=", StringComparison.Ordinal))
                    name = line[5..];
                else if (exec.Length == 0 && line.StartsWith("Exec=", StringComparison.Ordinal))
                    exec = line[5..];
                // **Icon= was never read.** Six keys are in this file and this
                // was the one nobody had needed yet, so a launcher shown as a
                // ROW rather than as a menu entry had no icon of its own to
                // ask for and fell through to the mime answer, which is
                // application/x-desktop for every one of them.
                //
                // First wins, like Name and Exec beside it: the localised
                // Icon[de]= spellings do not match this prefix, and a file that
                // repeats a key is one the spec says to read top-down.
                else if (icon.Length == 0 && line.StartsWith("Icon=", StringComparison.Ordinal))
                    icon = line[5..];
                else if (line.StartsWith("NoDisplay=true", StringComparison.OrdinalIgnoreCase))
                    noDisplay = true;
                else if (line.StartsWith("Hidden=true", StringComparison.OrdinalIgnoreCase))
                    noDisplay = true;
                // **Nothing read this key.** An entry that says it needs a
                // console was launched without one: vim, nano and htop all
                // ship such entries and all register against text/plain, so
                // they appeared in "Open with" for any text file and did
                // nothing visible when chosen.
                else if (line.StartsWith("Terminal=true", StringComparison.OrdinalIgnoreCase))
                    terminal = true;
            }
        }
        catch
        {
            return ("", "", "", true, false);
        }

        return (name, exec, icon, noDisplay, terminal);
    }

    // ---- a launcher shown as a row rather than as a menu entry -------------

    /// <summary>
    /// What one .desktop file says it is called and what picture goes with it,
    /// for a LISTING row that is the file itself.
    ///
    /// **A launcher listed as "org.kde.konsole.desktop" with the generic
    /// unknown-file icon.** Everything else in this class answers "what can
    /// open this?", which builds menu rows out of the database by id; nothing
    /// answered "what is THIS file", so a folder of launchers — the desktop,
    /// ~/.local/share/applications, the folder every KDE application ships its
    /// entry into — read as a column of reverse-DNS file names beside a column
    /// of identical grey pages. The name and the icon were both sitting in the
    /// file, two keys apart.
    ///
    /// Empty for anything this must not or cannot answer for, which the two
    /// callers treat as "no opinion" and fall back exactly as they did before.
    ///
    /// **Only an answer that came out of a file is remembered.** The first
    /// version cached every answer including the empty ones, and two callers
    /// ask before there is anything to read: the bin lists a row under the path
    /// the file will come BACK to, which by definition does not exist yet, and
    /// an untrusted launcher is one whose trust the person can change from this
    /// application's own Properties dialog. Both of those poisoned the entry
    /// for the life of the process — restoring a launcher, or ticking its
    /// execute bit, left the row showing the raw file name until restart.
    /// Measured: 97 µs for the first ask about one launcher on this machine,
    /// and 0.1 µs for every ask after it, which is what a row that is not a
    /// launcher at all costs.
    /// </summary>
    public static (string Name, string Icon) Launcher(string path)
    {
        if (string.IsNullOrEmpty(path)
            || !path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
            return ("", "");

        if (LauncherCache.TryGetValue(path, out var cached)) return cached;

        // Not remembered. A bin row's FullPath is where the file WILL be, so
        // opening the bin asked about every trashed launcher's original path —
        // and this also keeps Executable below off a path that is not there,
        // which was one swallowed FileNotFoundException per bin row.
        if (!File.Exists(path)) return ("", "");

        // Not remembered either: an execute bit is a fact about the file NOW,
        // and Properties can flip it while the folder is showing.
        if (!Trusted(path, ApplicationDirs(), Executable(path))) return ("", "");

        var entry = ReadEntry(path);
        var answer = (entry.Name, entry.Icon);

        // Bounded rather than merely finite, the same treatment IconLoader's
        // two caches get: the key is a PATH, so a folder of ten thousand
        // launchers would otherwise leave ten thousand pairs behind it.
        if (LauncherCache.Count > MaxLaunchers) LauncherCache.Clear();

        LauncherCache[path] = answer;
        return answer;
    }

    /// <summary>
    /// Read launchers, so that scrolling a folder of them does not re-read the
    /// same files. This is called per visible row per bind by the name
    /// converter and per row from the pool by the icon loader, and each miss is
    /// one open-read-close — 97 µs measured here against 0.1 µs for a hit.
    ///
    /// Keyed by path, and therefore stale if the file's CONTENT is edited while
    /// its folder is showing — the same trade <see cref="MimeCache"/> already
    /// makes, for the same reason: the alternative is a stat per row to learn
    /// whether the read is still worth skipping. Its existence and its trust
    /// are not stale, because neither is written here until a file has actually
    /// been read.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, string Icon)>
        LauncherCache = new(StringComparer.Ordinal);

    /// <summary>
    /// How many remembered launchers is too many.
    ///
    /// **Settable, because the alternative way to watch the bound fire is to
    /// write two thousand files** — and a test that does that to prove one
    /// comparison is a test nobody will keep running.
    /// </summary>
    internal static int MaxLaunchers { get; set; } = 2000;

    /// <summary>
    /// Whether a .desktop file's own words may be repeated to the person
    /// looking at the folder.
    ///
    /// **Name and Icon are chosen by whoever wrote the file, and a row is where
    /// a person decides what to double-click.** A .desktop that arrives by
    /// download or by mail can claim to be called "Invoice" and wear a PDF
    /// icon while its Exec runs something else entirely; that is the oldest
    /// trick this format has, and believing every one of these unconditionally
    /// would have been this application's way of falling for it.
    ///
    /// So this is Vaktari's own rule, and here is what it costs. A file under
    /// an application directory got there by an install or by the person
    /// putting it there, and a file carrying an execute bit was marked runnable
    /// by somebody; those two are repeated. Everything else keeps its file name
    /// and its generic icon. The known hole is the second half: a .desktop that
    /// arrives inside an archive can carry the bit already, and unpacking it is
    /// not the same gesture as marking it runnable — that file IS believed
    /// here. The alternative, believing only what an installer put in place,
    /// would refuse the launchers people write for themselves and drop on the
    /// desktop, which is the ordinary case.
    ///
    /// Pure, and given both facts rather than reading them, for the reason
    /// <see cref="Scan"/> gives at length: neither state can be arranged on a
    /// machine by asking politely, and both of them matter.
    /// </summary>
    internal static bool Trusted(string path, IEnumerable<string> applicationDirs, bool executable)
    {
        if (executable) return true;

        foreach (var dir in applicationDirs)
            if (Vaktari.Core.FileSystem.PathRules.Contains(dir, path)) return true;

        return false;
    }

    /// <summary>
    /// The execute bit, for a test — a machine fact this repository cannot
    /// arrange, since the suite runs on Windows where File.GetUnixFileMode
    /// throws and chmod does not exist.
    ///
    /// **The half of the trust rule that decides whether an answer may be
    /// remembered.** Null in the application.
    /// </summary>
    internal static Func<string, bool>? ExecutableOverride { get; set; }

    /// <summary>
    /// The machine fact behind <see cref="Trusted"/>. False off Linux rather
    /// than throwing: File.GetUnixFileMode is a PlatformNotSupportedException
    /// on Windows, and this assembly's tests run there.
    /// </summary>
    private static bool Executable(string path)
    {
        if (ExecutableOverride is { } stub) return stub(path);

        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            const UnixFileMode any = UnixFileMode.UserExecute
                                     | UnixFileMode.GroupExecute
                                     | UnixFileMode.OtherExecute;

            return (File.GetUnixFileMode(path) & any) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("launcher mode", e);
            return false;
        }
    }

    /// <summary>Which terminals this machine has, asked once.</summary>
    internal static IReadOnlyList<Vaktari.Core.FileSystem.TerminalOption> Terminals { get; set; }
        = new LinuxLauncher().Terminals;

    /// <summary>
    /// The argv for running a console application inside a terminal emulator.
    ///
    /// Separate and pure because the flags differ per terminal in ways that
    /// cannot be guessed: gnome-terminal takes "--", kitty takes nothing at
    /// all, and passing "-e" to either runs the wrong thing or nothing.
    /// </summary>
    internal static IReadOnlyList<string> InTerminal(
        Vaktari.Core.FileSystem.TerminalOption terminal, IReadOnlyList<string> command)
        => [terminal.Command, .. terminal.RunArguments, .. command];

    public static bool Launch(string desktopId, string path)
    {
        if (FindDesktopFile(desktopId) is not { } file) return false;

        var (_, exec, _, _, terminal) = ReadEntry(file);
        if (string.IsNullOrEmpty(exec)) return false;

        var parts = SplitExec(exec, path);
        if (parts.Count == 0) return false;

        // **A console application was spawned with no console.** Process.Start
        // returned non-null, so the launch reported success, and nothing ever
        // appeared — vim exits at once off a tty and htop lingers invisibly.
        if (terminal)
        {
            if (Terminals.Count == 0) return false;

            parts = [.. InTerminal(Terminals[0], parts)];
        }

        try
        {
            var info = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
            for (var i = 1; i < parts.Count; i++) info.ArgumentList.Add(parts[i]);

            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Expands an Exec= line. Field codes %f %F %u %U take the file; the rest
    /// (%i %c %k and any unknown) are dropped, per the desktop entry spec.
    /// </summary>
    internal static List<string> SplitExec(string exec, string path)
    {
        var result = new List<string>();
        var substituted = false;

        foreach (var token in Tokens(exec))
        {
            var clean = token;

            switch (clean)
            {
                case "%f" or "%F":
                    result.Add(path);
                    substituted = true;
                    break;

                case "%u" or "%U":
                    result.Add("file://" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)));
                    substituted = true;
                    break;

                case "%i" or "%c" or "%k":
                    break;

                default:
                    // A field code is a token of its own, so anything else
                    // beginning with % is one this build does not know, and the
                    // spec says to drop those.
                    //
                    // **Except "%%", which is a literal percent** — and folding
                    // it in the tokenizer instead would be worse than dropping
                    // it: "%%f" would become "%f" before this switch ran and be
                    // substituted with the FILENAME. Percent is folded last for
                    // that reason, which is the order GLib uses too.
                    if (clean.StartsWith('%')
                        && !clean.StartsWith("%%", StringComparison.Ordinal)) break;

                    result.Add(clean.Replace("%%", "%", StringComparison.Ordinal));
                    break;
            }
        }

        // Some entries declare no field code at all; append the path so the
        // application still receives something to open.
        if (!substituted && result.Count > 0) result.Add(path);

        return result;
    }

    /// <summary>
    /// Splits an Exec= line the way the desktop entry spec says to.
    ///
    /// **It was split on spaces and the quotes were then trimmed off each
    /// piece.** An application installed in a directory with a space in its
    /// name — "/opt/My App/bin/app", which is exactly what a quoted Exec= is
    /// FOR — came out as two arguments, neither of which is a program, so
    /// launching it failed with a message about a file that does not exist.
    ///
    /// Inside quotes the spec escapes with a backslash, and the four characters
    /// it names are the ones a shell would otherwise eat. Quoting only: the
    /// field codes are the caller's, because "%%" has to be folded to a literal
    /// percent AFTER they are read and not before.
    ///
    /// Not a shell: no word splitting after substitution, no globbing, no
    /// variable expansion. The spec is explicit that an Exec= line is not
    /// passed to one, and running it through a shell is how a filename becomes
    /// an argument list.
    /// </summary>
    internal static List<string> Tokens(string exec)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;

        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];

            if (quoted)
            {
                if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '\\' or '`' or '$')
                {
                    current.Append(exec[++i]);
                    continue;
                }

                if (c == '"') { quoted = false; continue; }

                current.Append(c);
                continue;
            }

            if (c == '"') { quoted = true; started = true; continue; }

            if (c is ' ' or '	')
            {
                if (started) { tokens.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }

            current.Append(c);
            started = true;
        }

        if (started) tokens.Add(current.ToString());

        return tokens;
    }
}
