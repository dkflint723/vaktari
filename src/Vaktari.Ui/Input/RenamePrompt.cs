using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Input;

/// <summary>What pressing Enter in the rename bar should do.</summary>
public enum RenameVerdict
{
    /// <summary>Zero deliberately: a default decision must never read as "go
    /// ahead and rename".</summary>
    Refused,

    /// <summary>The name did not change, so there is nothing to do and nothing
    /// to complain about.</summary>
    Unchanged,

    Rename,
}

/// <summary>The verdict, the tidied name to use, and the sentence to show.</summary>
public readonly record struct RenameDecision(RenameVerdict Verdict, string Name, string? Reason);

/// <summary>
/// Whether the name in the rename bar can be used, decided BEFORE the bar
/// closes.
///
/// **A refused name closed the editor and reported afterwards.** Typing a
/// colon, or CON, or "..", tore the bar down, sent the name to the file system
/// layer, and surfaced the refusal as a status line a moment later — with the
/// typed name gone, so correcting one character meant F2 and retyping the lot.
///
/// A name that was nothing but spaces was worse: Clean reduced it to empty, the
/// switch arm's length guard failed, no case matched at all, and the rename was
/// dropped in silence. Nothing anywhere said why.
///
/// Explorer keeps the box open with the reason under it, which is the whole
/// difference between a typo and a lost minute. <c>FileNames.Refuse</c> was
/// written for exactly this and had no caller in the UI.
///
/// A pure function rather than three lines inside the handler, for the reason
/// <see cref="RenameSelection"/> gives: MainWindow is never built in a test, so
/// logic living in an event handler cannot be asserted about at all.
/// </summary>
public static class RenamePrompt
{
    /// <summary>What the hint line says when there is nothing wrong.</summary>
    public const string Hint = "enter to confirm · esc to cancel";

    public static RenameDecision Decide(string? typed, string? currentName)
    {
        // Refuse applies Clean itself, so a trailing space that would have been
        // trimmed anyway is not reported as a fault.
        if (FileNames.Refuse(typed) is { } why)
            return new RenameDecision(RenameVerdict.Refused, FileNames.Clean(typed), why);

        var tidy = FileNames.Clean(typed);

        // **Ordinal, matching the operations layer.** Comparing case-insensitively
        // would call "readme.txt" and "README.txt" the same name and quietly
        // swallow a case correction — which is precisely the rename the Windows
        // backend goes out of its way to let through.
        return string.Equals(tidy, currentName, StringComparison.Ordinal)
            ? new RenameDecision(RenameVerdict.Unchanged, tidy, null)
            : new RenameDecision(RenameVerdict.Rename, tidy, null);
    }

    /// <summary>The line under the box: the reason when there is one, and the
    /// ordinary instructions when there is not.</summary>
    public static string HintFor(string? typed, string? currentName)
        => Decide(typed, currentName).Reason is { } why ? $"{why} — esc to cancel" : Hint;
}
