using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Core.Places;
using Vaktari.Core.Search;

namespace Vaktari.Core;

/// <summary>
/// Everything the application needs from the operating system, in one object.
///
/// A single composition seam rather than eight separate ones: the UI takes an
/// IPlatform and never names a platform type, so the whole OS-specific surface
/// is chosen in exactly one guarded place. That is also what lets the platform
/// assemblies be annotated Linux-only or Windows-only without the analyser
/// complaining at every call site.
/// </summary>
public interface IPlatform
{
    string Name { get; }

    /// <summary>
    /// What this desktop calls the place deleted files go, as it writes it
    /// itself: "Recycle Bin" on Windows, "trash" on a freedesktop desktop.
    ///
    /// **A platform fact, so it belongs on the platform seam** — the same
    /// reasoning that puts the trash implementation here rather than letting
    /// the UI guess. Windows already said "Recycle Bin" in the sidebar, because
    /// that label comes from WindowsPlacesProvider, while every prompt and
    /// setting around it said "trash". One feature, two names, one window.
    ///
    /// **Capitalised as the platform capitalises it**, which is the part a
    /// find-and-replace gets wrong. "Recycle Bin" is a proper noun and keeps
    /// its capitals mid-sentence; "trash" is a common noun and does not. Both
    /// take a definite article in a sentence — Windows itself asks whether you
    /// want to move a file to *the* Recycle Bin — so <see cref="TheBin"/>
    /// exists rather than every call site guessing.
    /// </summary>
    string BinName { get; }

    IFileSystemProvider FileSystem { get; }
    IFileOperations Operations { get; }
    IApplicationLauncher Launcher { get; }
    IPlacesProvider Places { get; }
    ISearchProvider Search { get; }
    IThumbnailProvider Thumbnails { get; }
    IFileMetadataProvider Metadata { get; }
    IPropertiesProvider Properties { get; }

    /// <summary>Null where the platform exposes nothing editable.</summary>
    IAccessEditor? AccessEditor { get; }

    IScriptRunner Scripts { get; }

    ITemplateProvider Templates { get; }

    /// <summary>Null where no sharing backend is known for this platform.</summary>
    IFileSharing? Sharing { get; }

    /// <summary>Null where the platform exposes no mounted remotes.</summary>
    IRemoteMounts? Remotes { get; }

    /// <summary>Null where the platform has no service-discovery mechanism.</summary>
    INetworkDiscovery? Discovery { get; }

    /// <summary>
    /// The desktop's own context menu for a selection, or null where the
    /// desktop has no such thing — which is every desktop but Windows today.
    /// The freedesktop world has no equivalent to a shell extension, so the
    /// menu offers no entry rather than an empty one.
    /// </summary>
    IShellMenuProvider? ShellMenu => null;

    /// <summary>
    /// Makes this platform's kind of shortcut — a .lnk on Windows, a symlink on
    /// Linux. Null on a platform with no such idea, and the gestures that use
    /// it (Ctrl+Shift+drag, the right-drag menu) simply do not offer it there.
    /// </summary>
    Vaktari.Core.FileSystem.IShortcutMaker? Shortcuts => null;

    /// <summary>
    /// Takes files a drop offers that are not on disk — the contents of an
    /// archive, dragged straight out of 7-Zip or Explorer's zip view. Null
    /// where the desktop has no such notion, which is every desktop but
    /// Windows.
    /// </summary>
    IVirtualFileDrop? VirtualFileDrop => null;

    /// <summary>
    /// Mounts .iso files and their kin, or null where this machine has no way
    /// to. Nullable rather than a no-op, so the menu can omit the entry
    /// entirely instead of offering a verb that cannot work.
    /// </summary>
    Places.IDiskImages? DiskImages => null;

    /// <summary>Null where the desktop exposes no readable theme.</summary>
    IThemeProvider? Theme { get; }

    /// <summary>
    /// The desktop's own icon for one particular file, or null where the
    /// desktop has no such notion. On freedesktop the icon THEME already is
    /// that answer; Windows composes per file, so it needs its own seam.
    /// </summary>
    IFileIconProvider? FileIcons => null;

    /// <summary>Null where the desktop ships no icon theme we can read.</summary>
    IIconThemeProvider? Icons { get; }

    /// <summary>
    /// Null where the platform offers no way to become the default handler for
    /// folders. Nullable rather than a no-op implementation, so the settings
    /// page can omit the control entirely rather than showing one that cannot
    /// work.
    /// </summary>
    IDefaultFileManager? DefaultFileManager { get; }

    /// <summary>
    /// Null where the platform has no trash this application may prune. Kept
    /// separate from <see cref="Operations"/> deliberately: trashing one file
    /// and unattended bulk expiry are different risks.
    /// </summary>
    ITrashMaintenance? TrashMaintenance { get; }
}
