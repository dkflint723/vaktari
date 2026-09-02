using System.Text;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Turns what somebody types in the path bar into a path.
///
/// **%ProgramFiles% is how Windows names that folder**, in its own dialogs, in
/// its documentation and in every script anybody has ever written; Explorer
/// accepts it in the address bar, and typing it here produced "no such
/// directory". The same is true of ~ and $HOME on a desktop where those are the
/// spelling people know.
///
/// Only ever applied to text a person typed. A path already on disk or already
/// in settings is used exactly as it is, because a folder whose real name
/// contains a percent sign is perfectly legal and rewriting it would be a bug.
/// </summary>
public static class PathVariables
{
    /// <summary>
    /// Expands variables, leaving anything it does not recognise alone.
    ///
    /// **An unknown name stays as it was written**, rather than becoming empty.
    /// Silently deleting %NoSuchThing% turns a typo into a different, valid
    /// path — one that could be somebody's whole drive — and the error message
    /// for a folder that does not exist is far more useful when it still
    /// contains what was actually typed.
    /// </summary>
    public static string Expand(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return "";

        var text = typed.Trim();

        // **A quoted path is what Explorer's own "Copy as path" produces**, so
        // it is the likeliest thing to arrive here from a paste — and it failed
        // with a raw Win32 error naming Vaktari's own working directory,
        // because the quotes made it a relative path. Stripped only as a
        // matching pair: a quote in the middle of a name is part of the name.
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];

        // The virtual listings — vaktari:trash and vaktari:recent-files — need
        // no guard here and deliberately do not get one: they carry no %, no $
        // and no leading ~, so every pass below leaves them exactly as they
        // are. A test holds that, so it stays true.
        text = Tilde(text);
        text = Environment.ExpandEnvironmentVariables(text);
        text = Dollar(text);
        text = KnownFolders(text);

        return Rooted(text);
    }

    /// <summary>~ and ~/Documents, which is the shorthand on one platform and
    /// increasingly understood on the other.</summary>
    private static string Tilde(string text)
    {
        if (text.Length == 0 || text[0] != '~') return text;
        if (text.Length > 1 && text[1] is not ('/' or '\\')) return text;

        return Home() + text[1..];
    }

    /// <summary>
    /// $HOME and ${HOME}. Left to a hand-rolled pass because
    /// ExpandEnvironmentVariables only understands the %NAME% spelling, on
    /// every platform, and $NAME is what a person at a Linux desktop types.
    /// </summary>
    private static string Dollar(string text)
    {
        if (!text.Contains('$', StringComparison.Ordinal)) return text;

        var built = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$') { built.Append(text[i]); continue; }

            var start = i + 1;
            var braced = start < text.Length && text[start] == '{';

            if (braced) start++;

            var end = start;

            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;

            var name = text[start..end];

            // A lone $, or ${ with no closing brace: not a variable, and the
            // text stands as written.
            if (name.Length == 0 || (braced && (end >= text.Length || text[end] != '}')))
            {
                built.Append(text[i]);
                continue;
            }

            var value = Environment.GetEnvironmentVariable(name);

            built.Append(value is { Length: > 0 } ? value : text[i..(braced ? end + 1 : end)]);

            i = braced ? end : end - 1;
        }

        return built.ToString();
    }

    /// <summary>
    /// The folders people name that are not environment variables at all.
    ///
    /// %Downloads% and %Documents% read exactly like %ProgramFiles% and are
    /// nothing of the sort — Windows keeps them in the registry, not the
    /// environment, and a localised or relocated Documents is only findable
    /// through the platform. Applied last, so a real environment variable of
    /// the same name always wins.
    /// </summary>
    private static string KnownFolders(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal)) return text;

        foreach (var (name, folder) in Known)
        {
            var token = $"%{name}%";

            if (!text.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;

            var path = folder is null ? Home() : Environment.GetFolderPath(folder.Value);

            if (path.Length > 0) text = Replace(text, token, path);
        }

        return text;
    }

    private static readonly (string Name, Environment.SpecialFolder? Folder)[] Known =
    [
        ("Home", null),
        ("Desktop", Environment.SpecialFolder.DesktopDirectory),
        ("Documents", Environment.SpecialFolder.MyDocuments),
        ("Pictures", Environment.SpecialFolder.MyPictures),
        ("Music", Environment.SpecialFolder.MyMusic),
        ("Videos", Environment.SpecialFolder.MyVideos),
    ];

    private static string Replace(string text, string token, string value)
    {
        var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);

        while (at >= 0)
        {
            text = text[..at] + value + text[(at + token.Length)..];
            at = text.IndexOf(token, at + value.Length, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>
    /// **%SystemDrive% expands to "C:", which is not a folder.** In Windows a
    /// bare drive letter means "wherever this process happens to be on that
    /// drive", so typing it would open the working directory rather than the
    /// root of the disk — the one thing nobody means by C:. The separator is
    /// what makes it the root.
    /// </summary>
    private static string Rooted(string text) =>
        text.Length == 2 && text[1] == ':' && char.IsLetter(text[0])
            ? text + Path.DirectorySeparatorChar
            : text;

    private static string Home() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
