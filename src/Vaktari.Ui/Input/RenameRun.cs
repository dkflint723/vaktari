using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Input;

/// <summary>
/// Which file Tab moves to when a run of them is being renamed by hand.
///
/// **Renaming several files cost three keystrokes each — Enter, arrow, F2.**
/// Explorer answers Tab, which is how anybody who has tidied a folder of
/// photographs does it. The arrow was the worse half of the three: a rename can
/// re-sort the folder, so "the row under the one just finished" is not the file
/// that was under it a moment ago.
///
/// Pure, and split from the window for that reason: the interesting cases are
/// both ends of the listing and a row that has moved, none of which need a
/// window to describe.
/// </summary>
internal static class RenameRun
{
    /// <summary>
    /// The entry <paramref name="step"/> places from <paramref name="from"/> in
    /// <paramref name="rows"/>, or null at either end.
    ///
    /// **Null rather than wrapping.** A run of renames has a beginning and an
    /// end, and wrapping from the last file back to the first would re-open a
    /// name that has just been settled — with the bar looking exactly as it
    /// does mid-run, so the only sign would be the name in it.
    ///
    /// Matched on the PATH rather than the entry, because the entry the prompt
    /// opened with is the one from before the rename and differs from the
    /// listing's own row in every field the rename touched.
    /// </summary>
    internal static FileEntry? Next(IReadOnlyList<FileEntry> rows, string from, int step)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (!PathRules.Same(rows[i].FullPath, from)) continue;

            var to = i + step;

            return to >= 0 && to < rows.Count ? rows[to] : null;
        }

        return null;
    }
}
