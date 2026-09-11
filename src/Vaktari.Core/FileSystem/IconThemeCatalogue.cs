namespace Vaktari.Core.FileSystem;

/// <summary>
/// A theme Vaktari can fetch and unpack on somebody's behalf.
/// </summary>
/// <param name="Name">What it is called, and the folder it lands in.</param>
/// <param name="Summary">One line, for the row in Settings.</param>
/// <param name="Url">A .tar.gz. Not a .zip: the tar format records symbolic
/// links, and for these themes the links are most of the theme.</param>
/// <param name="Megabytes">Roughly, so nobody starts a hundred-megabyte
/// download without being told it is one.</param>
/// <param name="Licence">Theirs, not ours, and worth saying out loud.</param>
/// <param name="Sha256">What the file at <paramref name="Url"/> hashes to,
/// lower-case hex. The download is refused if it hashes to anything else, so
/// this and the URL move together.</param>
public sealed record IconThemeSource(
    string Name,
    string Summary,
    string Url,
    int Megabytes,
    string Licence,
    string Sha256);

/// <summary>
/// The themes offered in Settings, and where they are put.
///
/// **Short on purpose.** Anything published as a freedesktop icon theme works,
/// and the folder picker beside this takes any of them; what a built-in list
/// adds is that one of them can be had without leaving the window, hitting the
/// symbolic-link wall Windows puts in the way, or knowing that the folder to
/// point at is the one holding index.theme. A long list of entries nobody has
/// checked would add nothing but the chance of one being wrong.
/// </summary>
public static class IconThemeCatalogue
{
    public static IReadOnlyList<IconThemeSource> All { get; } =
    [
        new IconThemeSource(
            "Papirus",
            "Flat, colourful, and the most complete free icon set there is. "
            + "Installs the light and dark variants too.",
            // **A release tag, not a branch.** refs/heads/master changes every
            // day, so what this fetched was whatever the project had committed
            // that morning, and nothing could say whether the bytes that
            // arrived were the bytes anyone had looked at. A tag is one set of
            // bytes with one hash; moving to a newer Papirus is a change to
            // these two lines, made on purpose.
            "https://github.com/PapirusDevelopmentTeam/papirus-icon-theme/archive/refs/tags/20260801.tar.gz",
            110,
            "GPL-3.0",
            "646f622e9e7e9e65eef9d0ab58999d4920ddb33d98e6a75232627cfe3bd508f9"),
    ];

    /// <summary>
    /// Where fetched themes are kept: per user, beside everything else this
    /// application stores, and needing no elevation to write.
    /// </summary>
    public static string InstallRoot => InstallRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vaktari",
        "Icons");

    /// <summary>Where tests install to, so a test of the fetch never writes
    /// into the developer's own icon folder. Null in production.</summary>
    internal static string? InstallRootOverride { get; set; }

    /// <summary>
    /// One folder per pack, holding the themes that came out of it.
    ///
    /// **Grouped rather than flattened**, for two reasons. One download is
    /// commonly several themes — Papirus brings its light and dark variants —
    /// and two packs that happen to share a theme name would otherwise
    /// overwrite each other. And the themes in a pack link to one another by
    /// relative path, so they have to stay siblings to keep working.
    /// </summary>
    public static string FolderFor(IconThemeSource source) =>
        Path.Combine(InstallRoot, source.Name);

    /// <summary>
    /// The themes already on this machine, for the list in Settings.
    ///
    /// **Found rather than remembered.** Nothing records what was installed:
    /// one download produces several themes and cannot say in advance how many
    /// or what they are called, and a folder somebody deleted by hand would
    /// leave a remembered list offering a theme that is not there. A directory
    /// with an index.theme in it is a theme, which is the same rule the reader
    /// itself uses.
    /// </summary>
    public static IReadOnlyList<InstalledIconTheme> Installed()
    {
        var root = InstallRoot;

        if (!Directory.Exists(root)) return [];

        var found = new List<InstalledIconTheme>();

        try
        {
            foreach (var pack in Directory.EnumerateDirectories(root))
            {
                // A pack folder holding themes, which is what fetching one
                // produces...
                foreach (var theme in Directory.EnumerateDirectories(pack))
                    if (File.Exists(Path.Combine(theme, "index.theme")))
                        found.Add(new InstalledIconTheme(
                            Path.GetFileName(theme), Path.GetFileName(pack), theme));

                // ...or a theme sitting directly here, which is what somebody
                // unpacking one into this folder themselves would produce.
                if (File.Exists(Path.Combine(pack, "index.theme")))
                    found.Add(new InstalledIconTheme(
                        Path.GetFileName(pack), Path.GetFileName(pack), pack));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder offers no themes, which is not worth failing
            // the whole settings window over.
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return found;
    }
}

/// <param name="Name">The theme's own name, which is its folder's name.</param>
/// <param name="Pack">The download it came from, for telling two themes of the
/// same name apart.</param>
/// <param name="Folder">What to hand the reader.</param>
public sealed record InstalledIconTheme(string Name, string Pack, string Folder);
