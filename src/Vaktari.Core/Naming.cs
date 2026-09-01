namespace Vaktari.Core;

/// <summary>
/// The words the interface uses for things the desktop names differently.
///
/// **Why a static rather than threading IPlatform through every label.** The
/// bin's name appears in a context menu header, a toolbar button, two prompt
/// sentences, a status line, three settings labels and a paragraph of
/// explanation. Passing a platform object to each of those means giving a dozen
/// unrelated types a constructor argument they otherwise have no use for; the
/// same bargain <c>ThumbnailLoader.RemoteRoots</c> already makes.
///
/// Set once, from the single place a platform type is chosen. The defaults are
/// the freedesktop words rather than nothing, so a code path that reads this
/// before <see cref="Adopt(IPlatform)"/> runs produces an ordinary English
/// sentence rather than an empty gap.
/// </summary>
public static class Naming
{
    /// <summary>
    /// The bin as the platform writes it — "Recycle Bin", "trash". Capitals are
    /// the platform's own: a proper noun keeps them mid-sentence, a common noun
    /// does not.
    /// </summary>
    public static string BinName { get; private set; } = "trash";

    /// <summary>
    /// Which platform the words came from — the stable identifier
    /// (<c>IPlatform.Name</c>), never the display string.
    ///
    /// **Copy that differs by more than a noun has to branch on something, and
    /// this is that something.** The first version of the sweep explanation
    /// tested <c>BinName == "trash"</c>, which reads as harmless and is not: it
    /// couples an English paragraph to the exact spelling of a label, so
    /// recapitalising the label silently swaps the paragraph.
    /// </summary>
    public static string Platform { get; private set; } = "linux";

    /// <summary>
    /// The same inside a sentence: "the Recycle Bin", "the trash". Both
    /// platforms take "the" here — Windows' own confirmation asks whether to
    /// move a file to *the* Recycle Bin — so this is one string rather than a
    /// rule each caller has to remember.
    /// </summary>
    public static string TheBin => $"the {BinName}";

    /// <summary>
    /// For a label or heading that begins with the word: "Recycle Bin",
    /// "Trash". Only the first letter differs from <see cref="BinName"/>, and
    /// only when the platform's own name is not already capitalised.
    /// </summary>
    public static string BinTitle =>
        BinName.Length == 0 ? BinName : char.ToUpperInvariant(BinName[0]) + BinName[1..];

    /// <summary>
    /// What the listing of every drive is called.
    ///
    /// **Explorer's own words on Windows, and not on Linux.** "This PC" is what
    /// a Windows user will type into the location bar and look for in the
    /// sidebar, so using anything else there would be a private name for a
    /// public idea. The freedesktop desktops have no single agreed term —
    /// Dolphin says "Devices", Nautilus "Other Locations" — so "This computer"
    /// is the plain reading, and it is what this application calls it.
    /// </summary>
    public static string ComputerTitle => Platform == "windows" ? "This PC" : "This computer";

    public static void Adopt(IPlatform platform) => Adopt(platform.BinName, platform.Name);

    /// <summary>Separate from the interface overload so a test can set the
    /// words without standing up an entire platform implementation.</summary>
    public static void Adopt(string binName, string platform)
    {
        if (!string.IsNullOrWhiteSpace(binName)) BinName = binName;
        if (!string.IsNullOrWhiteSpace(platform)) Platform = platform;
    }
}
