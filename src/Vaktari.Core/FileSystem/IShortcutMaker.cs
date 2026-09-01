namespace Vaktari.Core.FileSystem;

/// <summary>
/// Creates the platform's idea of a shortcut to a file or folder.
///
/// **A platform fact, like the trash and the shell menu.** On Windows a
/// shortcut is a <c>.lnk</c> file the shell resolves; on Linux it is a symbolic
/// link the filesystem follows. Explorer offers both Ctrl+Shift+drag and
/// "Create shortcuts here" on the right-drag menu, and Vaktari had neither —
/// the gesture did whatever the modifier fell through to instead.
/// </summary>
public interface IShortcutMaker
{
    /// <summary>
    /// Makes a shortcut to <paramref name="target"/> inside
    /// <paramref name="destinationFolder"/>, named the way this platform names
    /// one and stepped aside from any name already taken. Returns the path of
    /// what was created.
    /// </summary>
    string CreateShortcut(string target, string destinationFolder);

    /// <summary>
    /// What a shortcut points at, or null when the path is not one — or when
    /// this platform has no such indirection to read.
    ///
    /// **Defaulted to null because Linux genuinely does not need it.** A
    /// symbolic link to a directory IS a directory to every call that asks, so
    /// following one is not a decision anything has to make. Windows .lnk files
    /// are ordinary files that happen to contain a path, so opening one has to
    /// be told what it means.
    /// </summary>
    string? TargetOf(string path) => null;
}
