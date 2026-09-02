using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The sentence on the confirm bar.
///
/// **A count is not an identification.** "permanently delete 1 item(s)?" asks
/// somebody to approve something irreversible without saying what it is — and
/// the one case where naming it costs nothing is exactly the case the sentence
/// hid. One thing is named; several are counted.
///
/// The parenthesised plural goes with it. A sentence that hedges its own
/// grammar reads as machine output rather than as a question, and the moment
/// you are being asked to destroy something is the wrong moment to sound like
/// a dialog box from 1996.
///
/// Separate from the window because MainWindow needs a real shell, a real
/// platform and a real session to build, so a sentence assembled inside it
/// could never be read back by a test.
/// </summary>
internal static class Confirmations
{
    /// <summary>
    /// **The prompt bar is one horizontal row, and the confirm button sits to
    /// the right of this text.** An unnamed count was always short; a name is
    /// not, and a very long one pushes "delete permanently" off the window,
    /// leaving a question with no way to answer it but the keyboard.
    /// </summary>
    internal const int NameRoom = 48;

    internal static string Delete(IReadOnlyList<FileEntry> chosen)
        => $"permanently delete {Subject(chosen.Count, chosen.Count == 1 ? chosen[0].Name : null)}?"
           + " this cannot be undone";

    internal static string MoveToBin(IReadOnlyList<FileEntry> chosen)
        => $"move {Subject(chosen.Count, chosen.Count == 1 ? chosen[0].Name : null)} to {Naming.TheBin}?";

    internal static string EmptyBin(IReadOnlyList<TrashedItem> held)
        => $"permanently delete {Subject(held.Count, held.Count == 1 ? NameOf(held[0].OriginalPath) : null)}"
           + $" from {Naming.TheBin}? this cannot be undone";

    /// <summary>What is being acted on: the one thing by name, or how many.</summary>
    internal static string Subject(int count, string? only)
    {
        if (count != 1) return $"{count:N0} items";

        var name = only?.Trim();

        return string.IsNullOrEmpty(name) ? "1 item" : Elide(name);
    }

    /// <summary>
    /// Elided in the MIDDLE rather than the end, so the extension survives.
    /// ".pdf" against ".exe" is the part of a long name that changes what
    /// deleting it means, and it is the part a trailing ellipsis eats first.
    /// </summary>
    private static string Elide(string name)
    {
        if (name.Length <= NameRoom) return name;

        var head = (NameRoom - 1) / 2;
        var tail = NameRoom - 1 - head;

        return name[..head] + "…" + name[^tail..];
    }

    /// <summary>
    /// A bin row remembers where it came from, and a folder's original path can
    /// carry a trailing separator — <c>Path.GetFileName</c> answers "" for that,
    /// which would leave an empty gap where the name goes.
    /// </summary>
    private static string NameOf(string path)
        => Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
