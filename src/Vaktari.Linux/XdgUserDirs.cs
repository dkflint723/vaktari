namespace Vaktari.Linux;

/// <summary>
/// Reads <c>user-dirs.dirs</c>, which records where the user actually keeps
/// Documents, Downloads and the rest.
///
/// Matching on folder *names* would be wrong: a localised setup has
/// "Documentos" or "Téléchargements", and the user is free to point these
/// anywhere. This file is the only authority.
/// </summary>
public static class XdgUserDirs
{
    private static readonly Lazy<Dictionary<string, string>> Entries = new(Load, isThreadSafe: true);

    /// <summary>The path for a key such as <c>XDG_DOWNLOAD_DIR</c>, or null.</summary>
    public static string? Read(string key)
        => Entries.Value.TryGetValue(key, out var path) ? path : null;

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var file = ConfigFile(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home);

        try
        {
            if (!File.Exists(file)) return result;

            return Parse(File.ReadLines(file), home);
        }
        catch
        {
            // No file, or unreadable: callers fall back to conventional names.
            return result;
        }
    }

    /// <summary>
    /// The file's rules, over lines that have already been read.
    ///
    /// Split out because this is now the ONLY parser for this file. The places
    /// provider had a second one that hardcoded ~/.config, matched keys with a
    /// bare StartsWith on an untrimmed line, and did not skip comments; it
    /// delegates here instead, so these rules had better be worth relying on.
    /// </summary>
    internal static Dictionary<string, string> Parse(IEnumerable<string> lines, string home)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim().Trim('"');

            if (!key.StartsWith("XDG_", StringComparison.Ordinal)) continue;

            result[key] = value.Replace("$HOME", home, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Where the file lives: XDG_CONFIG_HOME when the session sets one,
    /// and ~/.config when it does not.</summary>
    internal static string ConfigFile(string? configHome, string home)
        => Path.Combine(
            string.IsNullOrWhiteSpace(configHome) ? Path.Combine(home, ".config") : configHome,
            "user-dirs.dirs");
}
