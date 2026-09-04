namespace Vaktari.Core.FileSystem;

/// <summary>
/// What answering Overwrite to a FOLDER that is already there actually costs.
///
/// **The engine never replaces a folder.** The plan lists the folder and every
/// descendant separately, and the Overwrite arm for the folder itself does
/// nothing but let the run continue into <c>Directory.CreateDirectory</c> on a
/// directory that exists — so the two trees are merged: everything already
/// inside stays, everything arriving is added, and only the items whose names
/// collide are decided one at a time. Measured, not assumed:
/// FolderCopyTests.Overwriting_a_folder_merges_it_and_asks_about_each_clash
/// copies one tree over another and counts both the survivors and the prompts.
///
/// A button reading "Overwrite" over that behaviour is a promise the engine
/// does not keep in either direction — it neither wipes the destination nor
/// takes the whole arriving tree unchallenged — so the prompt needs the number
/// this produces to say what is really at stake.
/// </summary>
/// <param name="Clashes">
/// How many items under the arriving folder already have a namesake in the same
/// place under the one that is there. This is exactly the set the engine will
/// raise a further conflict for.
/// </param>
/// <param name="Partial">
/// The walk stopped at its ceiling, so <see cref="Clashes"/> is a floor rather
/// than a total, and anything said about it has to be worded as one.
/// </param>
public readonly record struct FolderMerge(int Clashes, bool Partial)
{
    /// <summary>
    /// The ceiling on entries examined.
    ///
    /// This runs on the UI thread while somebody waits for a prompt to appear,
    /// and each entry costs two stats. ConflictViewModel.Describe already
    /// accepts a thousand entries to count what is in a folder, so a merge over
    /// a very large tree is answered with a floor rather than with a dialog
    /// that takes a visible moment to open.
    /// </summary>
    public const int Ceiling = 1000;

    /// <summary>
    /// Counts the collisions between a folder that is arriving and one of the
    /// same name that is already there.
    ///
    /// SafeWalk, so a symbolic link inside the arriving tree is counted where it
    /// stands and never descended into — the engine's own walk yields links the
    /// same way, and following one would count a folder that is not being
    /// copied at all.
    /// </summary>
    public static FolderMerge Between(string arriving, string alreadyThere, int ceiling = Ceiling)
    {
        // **Asked here, because the two platforms answer it in different
        // places.** A root that is not a folder — one that vanished, or turned
        // into a file, between the conflict and this walk — fails on Windows
        // with IOException "The parameter is incorrect" thrown from
        // FileSystemEnumerator.FindNextEntry on the FIRST MoveNext, which is
        // past SafeWalk's guard because that guard wraps only the call; and on
        // Linux with DirectoryNotFoundException thrown from
        // CreateDirectoryHandle at the call itself, which SafeWalk does catch,
        // so the walk simply yields nothing. Both were measured. Left to the
        // exception, the same folder is a thrown dialog on one platform and a
        // confident "nothing collides" on the other; asked outright, it is a
        // floor of nothing on both.
        if (!Directory.Exists(arriving)) return new FolderMerge(0, Partial: true);

        var clashes = 0;
        var examined = 0;

        try
        {
            foreach (var found in SafeWalk.Descend(arriving))
            {
                if (++examined > ceiling) return new FolderMerge(clashes, Partial: true);

                // GetRelativePath full-paths both of its own arguments, so an
                // unnormalised root needs no help from here.
                var counterpart = Path.Combine(
                    alreadyThere, Path.GetRelativePath(arriving, found.Path));

                if (File.Exists(counterpart) || Directory.Exists(counterpart)) clashes++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A GUARD, and one no mutation of mine reddens: with the root
            // checked above and SafeWalk swallowing every folder it fails to
            // open beneath it, what is left is the root's own enumeration
            // dying mid-flight — a share going away — which cannot be staged
            // in a test on either platform. It stays because this runs inside
            // the constructor of a dialog somebody is waiting for, and what
            // was counted before the failure is a floor.
            return new FolderMerge(clashes, Partial: true);
        }

        return new FolderMerge(clashes, Partial: false);
    }
}
