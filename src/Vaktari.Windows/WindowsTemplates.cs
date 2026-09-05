using Microsoft.Win32;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// One <c>ShellNew</c> key, flattened to the values that decide what the menu
/// says and what the new file gets.
///
/// A record rather than a live <see cref="RegistryKey"/> so that everything
/// below the read is a pure function of it: the walk is a fact about the
/// machine and gets a seam, the interpretation is a rule and gets tests.
/// </summary>
/// <param name="Extension">Including the dot, as the registry spells it.</param>
internal sealed record ShellNewKey(string Extension)
{
    /// <summary>The key's own MenuText, which overrides the type name.</summary>
    internal string? MenuText { get; init; }

    /// <summary>The default value of the ProgID this key hangs off.</summary>
    internal string? TypeName { get; init; }

    /// <summary>A seed file to copy. Absolute, or bare and meant relative to
    /// <c>%SystemRoot%\ShellNew</c>.</summary>
    internal string? FileName { get; init; }

    /// <summary>Literal bytes for the new file.</summary>
    internal byte[]? Data { get; init; }

    /// <summary>Make an empty file.</summary>
    internal bool NullFile { get; init; }

    /// <summary>
    /// The key names a <c>Command</c> or a <c>Handler</c> — code Explorer runs
    /// instead of copying a seed.
    /// </summary>
    internal bool Runs { get; init; }
}

/// <summary>
/// "New file from template" on Windows, which is Explorer's New submenu, which
/// is the per-extension <c>ShellNew</c> keys under HKEY_CLASSES_ROOT.
///
/// **This used to read <c>%APPDATA%\Microsoft\Windows\Templates</c>, and that
/// folder is empty.** The comment that chose it argued ShellNew was awkward —
/// "half its entries carry no template file at all, only a NullFile marker" —
/// and that reading it "needs the registry this project does not yet
/// reference". Neither reason survives measurement. This assembly references
/// the registry in three other places (WindowsDefaultFileManager,
/// WindowsShellThumbnails, WindowsThemeProvider); and of the 24 ShellNew keys
/// on this machine not one carries a bare NullFile — five name a seed file,
/// two carry Data, two pair NullFile with a Handler, two name a Command and
/// thirteen carry nothing at all — while <see cref="FileTemplate.Content"/> now
/// carries bytes, so the entries that name no file are templates like any
/// other. What was left was a menu fed by a directory measured at 0 files on a
/// Windows 11 machine with Office, VMware and Proton Drive installed — every
/// one of which had put an entry in ShellNew.
///
/// **Read once per process, not once per menu.** The context menu calls
/// RefreshTemplates on every right-click, and the XDG provider re-reads there
/// because a template is a file the user just dropped in a folder. A ShellNew
/// key is not: it appears when something is installed. Measured on this machine
/// — 1,104 extension keys, 977 ProgID subkeys under them, 24 ShellNew keys,
/// 111-119 ms per walk over five runs — so re-reading would have put a tenth of
/// a second on the UI thread every time a menu opened.
/// </summary>
public sealed class WindowsTemplates : ITemplateProvider
{
    /// <summary>
    /// The registry walk, replaced by the tests. Null in the application.
    ///
    /// It stands in front of <see cref="_read"/> rather than filling it, so a
    /// test can never leave synthetic keys cached for whatever runs next.
    /// </summary>
    internal static Func<IReadOnlyList<ShellNewKey>>? Override { get; set; }

    private static IReadOnlyList<ShellNewKey>? _read;

    /// <summary>
    /// Where a bare <c>FileName</c> lives. Legacy: every FileName measured on
    /// Windows 11 was absolute, and this folder did not exist at all — but it
    /// is what the bare form has always meant, and an installer that still uses
    /// it would otherwise offer a template that cannot be found.
    /// </summary>
    private static string SeedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew");

    public IReadOnlyList<FileTemplate> Discover()
    {
        try
        {
            return Offer(Override?.Invoke() ?? (_read ??= Read()), SeedFolder);
        }
        catch (Exception ex)
        {
            // A registry this process cannot read is a machine with no
            // templates, not a menu that fails to open.
            Quiet.Swallowed("templates", ex);
            return [];
        }
    }

    /// <summary>The cached walk, and null until one has happened. For the test
    /// that pins the caching, and nothing else — a window on it rather than a
    /// way to set it, so no test can leave synthetic keys behind.</summary>
    internal static IReadOnlyList<ShellNewKey>? Cached => _read;

    /// <summary>Drops the cached walk. For the same test, and nothing else.</summary>
    internal static void Forget() => _read = null;

    /// <summary>
    /// What the menu offers, given what the registry holds.
    ///
    /// **One row per extension, assembled from every key that could seed it.**
    /// Measured: <c>.zip</c> has two — <c>HKCR\.zip\ShellNew</c>, which carries
    /// the 22-byte Data blob of an empty archive, and
    /// <c>HKCR\.zip\CompressedFolder\ShellNew</c>, which is where the name
    /// "Compressed (zipped) Folder" can be reached from. Taking the first key
    /// whole would have offered an unnamed row, and that is not a hypothetical
    /// split: <c>HKCR\.zip</c>'s own default value is empty, so the key
    /// carrying the bytes has no ProgID to be named through at all. So the
    /// seed comes from the first seeding key that produces one and the name
    /// from the first seeding key that has one, and they need not be the same
    /// key.
    ///
    /// **A key whose seed file is gone speaks only for itself.** Nothing on
    /// this machine reaches the fall-through — the two <c>.zip</c> keys both
    /// carry Data and the five <c>FileName</c> keys are all alone on their
    /// extension — but committing to the first seeding key and giving up when
    /// it made nothing would have let one leftover FileName, the shape
    /// <see cref="Copy"/> exists to survive because an uninstaller routinely
    /// leaves the key behind, take down a row another key of the same
    /// extension could still have made. .zip is the only extension with two
    /// keys here, so it is the only row where that could happen — and it is
    /// the one row Windows itself ships.
    /// </summary>
    internal static IReadOnlyList<FileTemplate> Offer(
        IReadOnlyList<ShellNewKey> keys, string seedFolder)
    {
        var offered = new List<FileTemplate>();

        foreach (var group in keys.GroupBy(k => k.Extension, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.Where(Seeds).ToList();

            var label = Label(group.Key, candidates);

            foreach (var seed in candidates)
            {
                if (Make(seed, group.Key, label, seedFolder) is not { } template) continue;

                offered.Add(template);
                break;
            }
        }

        // By name, the way XdgTemplates sorts. The registry's own order is the
        // alphabet of file extensions, which is not the alphabet the menu shows.
        return [.. offered.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Whether this key describes a file Vaktari can make.
    ///
    /// **A Command or a Handler is not a seed.** Measured: <c>.lnk</c> says
    /// Handler <c>{ceefea1b-…}</c> AND NullFile, and <c>.contact</c> and
    /// <c>.mdb</c> say command. Explorer runs the handler — the shortcut wizard
    /// — and never touches the NullFile beside it. Honouring the NullFile here
    /// would have put a 0-byte .lnk in the folder, which is a shortcut nothing
    /// can open, offered under the name Explorer uses for the wizard.
    ///
    /// **A key that says nothing at all is not a seed either.** Measured: 13 of
    /// the 24 ShellNew keys here hold no value whatsoever — eleven VMware types
    /// and two Proton Drive ones, e.g. <c>HKCR\.vmx\VMware.Document\ShellNew</c>
    /// with valueCount 0 — and Explorer offers no New row for any of them.
    /// </summary>
    private static bool Seeds(ShellNewKey key)
    {
        if (key.Runs) return false;

        return key.FileName is not null || key.Data is not null || key.NullFile;
    }

    /// <summary>
    /// **FileName, then Data, then NullFile.** <c>.zip</c> carries NullFile and
    /// Data together, and only Data produces an archive that opens — so the
    /// more specific directive wins and NullFile is what is left when nothing
    /// better was said.
    /// </summary>
    private static FileTemplate? Make(
        ShellNewKey seed, string extension, string label, string seedFolder)
    {
        if (seed.FileName is { } named) return Copy(label, extension, named, seedFolder);

        byte[] content = seed.Data ?? [];

        return new FileTemplate(label, LeafOf(label, extension)) { Content = content };
    }

    /// <summary>
    /// **The seed's own name is the installer's, and it is not the row's.**
    /// Measured on this machine, the five seeded rows point at ACCESS12.ACC,
    /// word.docx, excel12.xlsx, powerpoint.pptx and mspub.pub — so a copy that
    /// let the destination take its leaf from the source made "New > Microsoft
    /// Access Database" produce ACCESS12.ACC, which is neither the row's name
    /// nor the .accdb the row is for. The path says what to copy;
    /// <see cref="FileTemplate.Leaf"/> says what to call it.
    /// </summary>
    private static FileTemplate? Copy(
        string label, string extension, string fileName, string seedFolder)
    {
        var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(seedFolder, fileName);

        // **An uninstaller leaves the key behind.** The seed goes with the
        // program, the ShellNew key under HKLM\Software\Classes often does not,
        // and a menu row that always ends in "that file is not there any more"
        // is worse than no row.
        if (!File.Exists(path)) return null;

        return new FileTemplate(label, path) { Leaf = LeafOf(label, extension) };
    }

    /// <summary>
    /// What the row says.
    ///
    /// **A leading @ is a resource reference, not a name.** Measured, every
    /// MenuText on this machine is one — <c>@shell32.dll,-30318</c> for .lnk,
    /// <c>@C:\Program Files\…\wab32res.dll,-10203</c> for .contact — and so is
    /// every FriendlyTypeName. Resolving them needs SHLoadIndirectString and a
    /// module load per row; the ProgID's plain default value says the same
    /// thing ("Microsoft Word Document", "Compressed (zipped) Folder") and is
    /// already read.
    ///
    /// **Named off the keys that could have made the file, not off every key
    /// with the extension.** The two keys carrying an @-only MenuText here are
    /// <c>.lnk</c>, which names a Handler, and <c>.contact</c>, which names a
    /// lower-case <c>command</c> — both dropped by <see cref="Seeds"/>, so
    /// passing the whole group would have let a row be named after code Vaktari
    /// never runs. It costs nothing on the one extension whose name and seed
    /// live in different keys: both of <c>.zip</c>'s carry the 22-byte Data
    /// blob, so the one that can be named survives the filter.
    /// </summary>
    private static string Label(string extension, IReadOnlyList<ShellNewKey> keys)
    {
        if (Plain(keys.Select(k => k.MenuText)) is { } menu) return menu;

        if (Plain(keys.Select(k => k.TypeName)) is { } type) return type;

        return extension.TrimStart('.').ToUpperInvariant() + " file";
    }

    private static string? Plain(IEnumerable<string?> values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !v.StartsWith('@'));

    /// <summary>
    /// What the new file is called: the row's own name, with the extension the
    /// row is for.
    ///
    /// **The label is registry text, and it lands in a path.** A ProgID default
    /// value is whatever an installer wrote there, so it can hold a separator
    /// or a colon; pasted into NewItemName.Free it would have created the file
    /// somewhere other than the folder the user was looking at.
    /// </summary>
    private static string LeafOf(string label, string extension)
        => string.Concat(label.Split(Path.GetInvalidFileNameChars())) + extension;

    /// <summary>
    /// Every ShellNew key under HKEY_CLASSES_ROOT.
    ///
    /// **Two shapes, both real.** <c>HKCR\.ext\ShellNew</c> is the documented
    /// one and is what .zip, .lnk, .library-ms and .mdb use here.
    /// <c>HKCR\.ext\ProgID\ShellNew</c> is where every installed application
    /// measured on this machine put its entry — Word, Excel, Publisher, Access,
    /// thirteen VMware types, Proton Drive — so reading only the first shape
    /// would have found the four the operating system ships and none of the
    /// nineteen that arrived with software.
    ///
    /// HKEY_CLASSES_ROOT rather than HKLM, so a per-user registration counts
    /// exactly as much as a machine-wide one — the same merged view
    /// WindowsShellThumbnails reads handlers from.
    /// </summary>
    private static IReadOnlyList<ShellNewKey> Read()
    {
        var keys = new List<ShellNewKey>();
        var classes = Registry.ClassesRoot;

        foreach (var extension in classes.GetSubKeyNames())
        {
            if (!extension.StartsWith('.')) continue;

            try
            {
                using var key = classes.OpenSubKey(extension);
                if (key is null) continue;

                // The extension's own ShellNew hangs off the extension's own
                // ProgID, which is the extension key's default value.
                Collect(keys, classes, extension, key, key.GetValue(null) as string);

                foreach (var progId in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(progId);
                    if (sub is null) continue;

                    Collect(keys, classes, extension, sub, progId);
                }
            }
            catch (Exception ex)
            {
                // One unreadable class does not cost the other thousand.
                Quiet.Swallowed("templates", ex);
            }
        }

        return keys;
    }

    /// <summary>
    /// Reads <paramref name="parent"/>'s ShellNew, if it has one, and appends
    /// it.
    ///
    /// **Value names are matched case-insensitively, because the registry is.**
    /// Measured: <c>.mdb</c> spells it <c>Command</c> and <c>.contact</c> spells
    /// it <c>command</c>, so an ordinal comparison would have let one of the two
    /// through as a seed.
    /// </summary>
    private static void Collect(
        List<ShellNewKey> keys,
        RegistryKey classes,
        string extension,
        RegistryKey parent,
        string? progId)
    {
        using var shellNew = parent.OpenSubKey("ShellNew");
        if (shellNew is null) return;

        var names = new HashSet<string>(shellNew.GetValueNames(), StringComparer.OrdinalIgnoreCase);

        keys.Add(new ShellNewKey(extension)
        {
            MenuText = shellNew.GetValue("MenuText") as string,
            TypeName = TypeNameOf(classes, progId),

            // REG_EXPAND_SZ is expanded by GetValue, so a FileName written as
            // %ProgramFiles%\… arrives as a path that File.Exists can answer.
            FileName = shellNew.GetValue("FileName") as string,

            // byte[] only: Data is REG_BINARY wherever it was measured.
            Data = shellNew.GetValue("Data") as byte[],

            NullFile = names.Contains("NullFile"),
            Runs = names.Contains("Command") || names.Contains("Handler"),
        });
    }

    /// <summary>
    /// The ProgID's default value — "Microsoft Word Document" for
    /// Word.Document.12. Empty on a class that has one only for compatibility,
    /// which is why <see cref="Label"/> keeps looking after a blank.
    /// </summary>
    private static string? TypeNameOf(RegistryKey classes, string? progId)
    {
        if (string.IsNullOrEmpty(progId)) return null;

        using var key = classes.OpenSubKey(progId);

        return key?.GetValue(null) as string;
    }
}
