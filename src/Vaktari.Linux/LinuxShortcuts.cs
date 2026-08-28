using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// A shortcut on Linux is a symbolic link — resolved by the filesystem, so it
/// works in every application rather than only in file managers.
///
/// **Absolute target, deliberately.** A link made by dragging is a pointer to
/// THAT file, and the two ends usually sit in unrelated folders — a relative
/// target would quietly re-point the moment the link was moved. (The copy
/// machinery preserves relative links verbatim for the opposite reason: there
/// the link already existed, and its author chose its spelling.)
/// </summary>
public sealed class LinuxShortcuts : IShortcutMaker
{
    public string CreateShortcut(string target, string destinationFolder)
    {
        var full = Path.GetFullPath(target);
        var landing = Path.Combine(destinationFolder, PathRules.LeafName(full));

        // The platform's own phrasing for a name already taken, same as a
        // conflicted copy — and the kind matters for where the number goes.
        if (File.Exists(landing) || Directory.Exists(landing))
            landing = XdgTrash.Deduplicate(landing, Directory.Exists(full));

        if (Directory.Exists(full)) Directory.CreateSymbolicLink(landing, full);
        else File.CreateSymbolicLink(landing, full);

        return landing;
    }
}
