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
    /// not, and a very long one pushes "Delete permanently" off the window,
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

    /// <summary>
    /// Copying what is newer or missing to the other side. **The replacing is
    /// said out loud**, because it is the part that cannot be taken back: an
    /// undo takes the copies away again, and the older files they replaced
    /// are not anywhere to come back from.
    ///
    /// **The destination by its path, not its name.** Comparing a folder with
    /// its backup is the usual case, and the two are called the same, so "copy
    /// 3 items to Photos?" read the same in either direction. Cut in the middle
    /// when long, as a name is, so its start and its own name both survive.
    /// </summary>
    internal static string CopyAcross(CopyAcrossPlan plan)
    {
        var only = plan.Count == 1 ? NameOf(plan.Sources[0]) : null;

        var asked = $"copy {Subject(plan.Count, only)} to {Elide(plan.Destination)}?";

        return plan.Replacing.Count switch
        {
            0 => asked,
            1 when plan.Count == 1 => asked + " it replaces the older one there for good",
            1 => asked + " 1 of them replaces an older file there for good",
            var n => asked + $" {n:N0} of them replace older files there for good",
        };
    }

    /// <summary>What copying across left alone because it had changed after
    /// the prompt, for the operation bar once the copy is done; null when it
    /// left nothing alone.</summary>
    internal static string? LeftAlone(CopyAcrossPlan plan)
    {
        var alone = plan.LeftAlone.ToList();

        if (alone.Count == 0) return null;

        var subject = Subject(alone.Count, alone.Count == 1 ? NameOf(alone[0]) : null);

        return alone.Count == 1
            ? $"left {subject} alone: it changed after the prompt"
            : $"left {subject} alone: they changed after the prompt";
    }

    /// <summary>What copying across leaves out and why, for the status line
    /// while the prompt is up; null when it leaves nothing out.</summary>
    internal static string? LeftOut(CopyAcrossPlan plan)
    {
        var holds = plan.Withheld.Where(w => w.Because == WithheldBecause.HoldsTheOtherSide).ToList();
        var unnamed = plan.Withheld.Where(w => w.Because == WithheldBecause.NameWindowsCannotOpen).ToList();

        var parts = new List<string>(2);

        if (holds.Count > 0) parts.Add($"{Elide(NameOf(holds[0].Path))}, which holds the other side");

        if (unnamed.Count == 1) parts.Add($"\"{Elide(NameOf(unnamed[0].Path))}\", whose name Windows cannot open");
        else if (unnamed.Count > 1) parts.Add($"{unnamed.Count:N0} names Windows cannot open");

        return parts.Count == 0 ? null : "left out of the copy: " + string.Join("; ", parts);
    }

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
