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
    private static IEnumerable<string> ApplicationDirs()
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

    public static string QueryMimeType(string path)
    {
        if (MimeCache.TryGetValue(path, out var cached)) return cached;

        // Try-acquire, never wait. A thread that blocks here is a pool thread
        // taken out of circulation, which is the whole failure being fixed —
        // so when the budget is spent we return "" and the row shows a generic
        // icon. Deliberately NOT cached: the next pass over that row sniffs it
        // properly, so the listing converges instead of freezing.
        if (!Sniffs.Wait(0)) return "";

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
            Sniffs.Release();
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

    public static IReadOnlyList<LaunchOption> ForFile(string path)
    {
        var mime = QueryMimeType(path);
        if (string.IsNullOrEmpty(mime)) return [];

        // Ordered, deduplicated: the default handler first, then everything
        // else that claims the type.
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id)
        {
            if (id.Length > 0 && seen.Add(id)) ids.Add(id);
        }

        foreach (var id in DefaultsFor(mime)) Add(id);
        foreach (var id in AssociationsFor(mime)) Add(id);

        var options = new List<LaunchOption>();

        foreach (var id in ids)
        {
            if (FindDesktopFile(id) is not { } file) continue;
            var (name, _, noDisplay, terminal) = ReadEntry(file);
            if (noDisplay || string.IsNullOrEmpty(name)) continue;

            // Offered only if there is something to run it in. An entry that
            // needs a console, on a machine with no terminal emulator, is a
            // row that can only do nothing.
            if (terminal && Terminals.Count == 0) continue;

            options.Add(new LaunchOption(name, id));
        }

        return options;
    }

    private static IEnumerable<string> DefaultsFor(string mime)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(home, ".config");

        foreach (var listPath in new[]
        {
            Path.Combine(configHome, "mimeapps.list"),
            Path.Combine(home, ".local", "share", "applications", "mimeapps.list"),
        })
        {
            if (!File.Exists(listPath)) continue;

            foreach (var line in File.ReadLines(listPath))
            {
                if (!line.StartsWith(mime + "=", StringComparison.Ordinal)) continue;

                foreach (var id in line[(mime.Length + 1)..]
                             .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return id;
            }
        }
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

    internal static (string Name, string Exec, bool NoDisplay, bool Terminal) ReadEntry(string desktopFile)
    {
        string name = "", exec = "";
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
            return ("", "", true, false);
        }

        return (name, exec, noDisplay, terminal);
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

        var (_, exec, _, terminal) = ReadEntry(file);
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
    private static List<string> SplitExec(string exec, string path)
    {
        var result = new List<string>();
        var substituted = false;

        foreach (var token in exec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = token.Trim('"');

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
                    if (!clean.StartsWith('%')) result.Add(clean);
                    break;
            }
        }

        // Some entries declare no field code at all; append the path so the
        // application still receives something to open.
        if (!substituted && result.Count > 0) result.Add(path);

        return result;
    }
}
