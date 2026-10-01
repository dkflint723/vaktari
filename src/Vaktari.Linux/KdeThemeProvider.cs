using System.Globalization;
using Vaktari.Core;

namespace Vaktari.Linux;

/// <summary>
/// Reads the Plasma colour scheme and UI font out of <c>kdeglobals</c>.
///
/// Parsed directly rather than through a D-Bus or KConfig binding: the file is
/// a plain INI, it is what every KDE application ultimately reads, and it means
/// no extra dependency and nothing to keep in step with a Plasma version.
/// </summary>
public sealed class KdeThemeProvider : IThemeProvider
{
    private readonly string _path;

    /// <summary>
    /// **One watcher for the whole process, shared by every window** — the
    /// shape WindowsThemeProvider took for its registry threads. Each provider
    /// made a FileSystemWatcher of its own, which on Linux is one inotify
    /// instance, and nothing disposed it: LinuxPlatform builds a provider per
    /// window that builds its own services. Measured (batch-0.11.2 QA, item G),
    /// thirty windows opened and closed with a config folder present took a
    /// test process from 0 inotify instances to 30, every closed window's
    /// watcher still raising — against a per-user ceiling of 128 that a full
    /// Ui run already comes within seven of. The application builds one such
    /// window, so a user never met it; the test suite builds hundreds.
    ///
    /// The file is the user's, not the window's, so one watcher answers for
    /// every window. Started by the first provider that finds the config
    /// folder, under the lock — and tried again by the next one while none
    /// has, as each provider used to — and kept for the life of the process.
    /// <see cref="Changed"/> is the shared event: subscribing through any
    /// provider is subscribing to the one watcher, and a window that closes
    /// takes its handler back off it (MainWindow.OnClosed), so the static
    /// event holds no window that has gone.
    /// </summary>
    private static readonly Lock Gate = new();

    private static EventHandler? _changed;

    private static ConfigFolderWatch? _watcher;

    private static int _watchersStarted;

    /// <summary>How many watchers this process has started: one, however many
    /// providers are made. For the tests.</summary>
    internal static int WatchersStarted => Volatile.Read(ref _watchersStarted);

    /// <summary>Raised on a watcher thread when kdeglobals is written, for
    /// every subscriber of every provider.</summary>
    public event EventHandler? Changed
    {
        add { lock (Gate) _changed += value; }
        remove { lock (Gate) _changed -= value; }
    }

    public KdeThemeProvider() : this(ConfigHome())
    {
    }

    /// <summary>A provider reading kdeglobals in <paramref name="configHome"/>
    /// rather than the session's own config folder. For the tests.</summary>
    internal KdeThemeProvider(string configHome)
    {
        _path = Path.Combine(configHome, "kdeglobals");

        lock (Gate)
        {
            // **Started again once the one there has lapsed** — its folder
            // deleted, and the folder above it not to be watched either; see
            // ConfigFolderWatch. Otherwise a watcher is there and this is all.
            if (_watcher is { Lapsed: false }) return;

            _watcher?.Dispose();
            _watcher = Watch(configHome);
        }
    }

    private static string ConfigHome()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        return string.IsNullOrWhiteSpace(configHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : configHome;
    }

    /// <summary>
    /// A watcher on kdeglobals in <paramref name="directory"/> that tells every
    /// subscriber when it is written, or null when the folder is not there or
    /// cannot be watched. Internal and static so a test can watch a folder of
    /// its own, and dispose of the watcher after; the process's own is made
    /// once, by the first provider.
    ///
    /// Through the process's one inotify instance (Inotify), which the panes
    /// share: a FileSystemWatcher here was an instance of its own, and one that
    /// outlived its folder for as long as the process ran.
    /// </summary>
    internal static ConfigFolderWatch? Watch(string directory)
    {
        var watcher = ConfigFolderWatch.Start(directory, Notify);

        if (watcher is not null) Interlocked.Increment(ref _watchersStarted);

        return watcher;
    }

    /// <summary>
    /// Tells every subscriber that kdeglobals changed — what the watcher does
    /// when the file is written.
    ///
    /// **Each handler on its own, each throw swallowed on its own**, as
    /// WindowsThemeProvider.Notify: with a plain Invoke, one subscriber's
    /// exception skipped every handler after it, so a window could stop
    /// following Plasma because another one had thrown.
    /// </summary>
    internal static void Notify()
    {
        if (Volatile.Read(ref _changed) is not { } changed) return;

        foreach (var handler in changed.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Quiet.Swallowed("theme", ex);
            }
        }
    }

    public ThemePalette? Read()
    {
        var ini = Parse();
        if (ini.Count == 0) return null;

        string? Colour(string section, string key)
        {
            if (!ini.TryGetValue(section, out var entries)) return null;
            if (!entries.TryGetValue(key, out var value)) return null;

            // Stored as "r,g,b" decimal triples.
            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return null;

            return byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b)
                ? $"#{r:X2}{g:X2}{b:X2}"
                : null;
        }

        var colours = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string role, string? value)
        {
            if (value is not null) colours[role] = value;
        }

        Add(ThemeRole.WindowBackground, Colour("Colors:Window", "BackgroundNormal"));
        Add(ThemeRole.WindowText, Colour("Colors:Window", "ForegroundNormal"));
        Add(ThemeRole.ViewBackground, Colour("Colors:View", "BackgroundNormal"));
        Add(ThemeRole.ViewAlternate, Colour("Colors:View", "BackgroundAlternate"));
        Add(ThemeRole.ViewText, Colour("Colors:View", "ForegroundNormal"));
        Add(ThemeRole.ViewDimText, Colour("Colors:View", "ForegroundInactive"));
        Add(ThemeRole.SelectionBackground, Colour("Colors:Selection", "BackgroundNormal"));
        Add(ThemeRole.SelectionText, Colour("Colors:Selection", "ForegroundNormal"));
        Add(ThemeRole.Border, Colour("Colors:Window", "ForegroundInactive"));

        // Plasma 6 keeps the accent separately; the selection colour is the
        // sensible fallback because that is what it tints.
        Add(ThemeRole.Accent,
            Colour("General", "AccentColor") ?? Colour("Colors:Selection", "BackgroundNormal"));

        if (colours.Count == 0) return null;

        var (family, size) = ReadFont(ini);

        return new ThemePalette
        {
            Colours = colours,
            FontFamily = family,
            FontSize = size,
            IsDark = IsDark(colours.GetValueOrDefault(ThemeRole.ViewBackground)),
            IconTheme = ini.GetValueOrDefault("Icons")?.GetValueOrDefault("Theme"),

            // [KDE] SingleClick, the same key Dolphin obeys. Absent means the
            // user never set it, so it stays null.
            SingleClick = ini.GetValueOrDefault("KDE")?.GetValueOrDefault("SingleClick")
                is { } single && bool.TryParse(single, out var parsed) ? parsed : null,
        };
    }

    /// <summary>
    /// Perceived luminance, not a plain average — the eye weights green far
    /// more than blue, and an average misjudges schemes near the middle.
    /// </summary>
    private static bool IsDark(string? hex)
    {
        if (hex is not { Length: 7 }) return true;

        try
        {
            var r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
            var g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
            var b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);

            return (0.2126 * r + 0.7152 * g + 0.0722 * b) < 128;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>font=Family,Size,... — only the first two fields matter here.</summary>
    private static (string? Family, double? Size) ReadFont(
        Dictionary<string, Dictionary<string, string>> ini)
    {
        var value = ini.GetValueOrDefault("General")?.GetValueOrDefault("font");
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        var parts = value.Split(',');
        var family = parts[0].Trim();

        double? size = parts.Length > 1 && double.TryParse(
            parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        return (family.Length > 0 ? family : null, size);
    }

    private Dictionary<string, Dictionary<string, string>> Parse()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(_path)) return result;

            var section = "";

            foreach (var raw in File.ReadLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1];
                    continue;
                }

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                if (!result.TryGetValue(section, out var entries))
                    result[section] = entries = new Dictionary<string, string>(StringComparer.Ordinal);

                entries[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
        }
        catch
        {
            // Unreadable config is the same as no config: fall back to our own.
        }

        return result;
    }
}
