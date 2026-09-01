using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Core.Places;
using Vaktari.Core.Search;

namespace Vaktari.Windows;

/// <summary>
/// The Windows composition root. Everything OS-specific is built here, so the UI
/// project holds exactly one reference to a platform type — inside one
/// OperatingSystem.IsWindows() check.
///
/// **All eleven required members are real**, because the UI reads every one of
/// them while constructing the main window: ShellViewModel takes Operations,
/// Launcher, Search, Scripts and Templates alongside the obvious ones, so a
/// throwing stub anywhere here means no window at all. That is why step 3 built
/// more than the two providers WINDOWS.md §7 names.
///
/// **Real does not mean complete.** The open-with list is empty, pending the
/// shell's handler enumeration — documented on the class that does it. Trash
/// and the Recycle Bin view both work now, and tags are gone.
///
/// Four of the seven nullable members still return null, which is the interface
/// working as designed rather than a gap being papered over. The other three —
/// <see cref="Sharing"/>, <see cref="Remotes"/> and <see cref="Discovery"/> —
/// were null because nobody had written them yet, which is a different thing
/// and is no longer true.
/// </summary>
public sealed class WindowsPlatform : IPlatform
{
    private readonly WindowsPropertiesProvider _properties = new();

    public WindowsPlatform(string stateDirectory)
    {
        StateDirectory = stateDirectory;

        // Before anything asks for git. The version-control seam resolves it
        // through PATH, and Git for Windows is routinely installed without
        // being put there — its installer offers exactly that, and GitHub
        // Desktop bundles a private copy instead. The result is a listing with
        // no decoration, which looks the same as a folder with nothing to
        // report, so the failure never announces itself.
        //
        // Here because it is a fact about this operating system, and the
        // composition root is where those are allowed to live. It edits this
        // process's environment only — see GitOnPath.
        if (GitOnPath.Ensure() is { } git)
            Console.Error.WriteLine($"[vaktari] vcs: git not on PATH, found at {git}");
        // Started here rather than in its constructor: the composition root is
        // where a fact about this machine belongs, and it is the only place
        // that knows the provider is the live one rather than a test's.
        var places = new WindowsPlacesProvider(stateDirectory);
        places.Start();
        Places = places;
        Scripts = new WindowsScriptRunner(stateDirectory);

        // Handed the bin so a recycle can be undone: SHFileOperation reports
        // nothing about what it recycled, and reading the bin before and after
        // is what makes Ctrl+Z work here the way it always has on Linux.
        Operations = new WindowsFileOperations { Bin = TrashMaintenance };
    }

    /// <summary>Where this application's own per-user state lives.</summary>
    public string StateDirectory { get; }

    public string Name => "windows";

    /// <summary>
    /// Windows' own name for it, capitals and all. Explorer says "Recycle Bin"
    /// in the shell namespace, in its confirmation dialog and on its context
    /// menu, and WindowsPlacesProvider already labels the sidebar entry that
    /// way — this is what stops the rest of the window disagreeing with it.
    /// </summary>
    public IDefaultFileManager? DefaultFileManager { get; } = new WindowsDefaultFileManager();

    public string BinName => "Recycle Bin";

    // ---- Required. ---------------------------------------------------------

    public IFileSystemProvider FileSystem { get; } = new WindowsFileSystemProvider();
    public IFileOperations Operations { get; }
    public IApplicationLauncher Launcher { get; } = new WindowsLauncher();
    public IPlacesProvider Places { get; }
    public ISearchProvider Search { get; } = new WindowsSearchProvider();
    public IThumbnailProvider Thumbnails { get; } = new WindowsThumbnailProvider();
    public IFileMetadataProvider Metadata { get; } = new WindowsMetadataProvider();

    public IPropertiesProvider Properties => _properties;

    public IScriptRunner Scripts { get; }

    public ITemplateProvider Templates { get; } = new WindowsTemplates();

    // ---- Optional. Null is a legitimate answer, now and possibly forever. --

    /// <summary>
    /// Null, and likely permanently. POSIX modes have no meaning here and NTFS
    /// ACLs are a different model, not a richer version of the same one.
    /// </summary>
    public IAccessEditor? AccessEditor => null;

    /// <summary>
    /// The same copyparty, driven the same way. What used to be null here is
    /// now the shared engine in Core plus <see cref="WindowsCopyparty"/>, which
    /// is only the part that differs: where to look for it, and how to install
    /// it when it is missing.
    /// </summary>
    public IFileSharing? Sharing { get; } = new CopypartyShare(new WindowsCopyparty());

    /// <summary>
    /// The Windows redirector, which is to SMB and WebDAV what gvfs is on
    /// Linux.
    ///
    /// This used to be null on the grounds that mapped network drives already
    /// arrive as drive letters through <see cref="Places"/>. True, and not the
    /// same thing: it left no way to CONNECT to a share from inside Vaktari,
    /// and nothing at all for a UNC path the user had not mapped. Places still
    /// owns the lettered drives — see WindowsRemoteMounts for how the two avoid
    /// listing the same share twice.
    /// </summary>
    public IRemoteMounts? Remotes { get; } = new WindowsRemoteMounts();

    /// <summary>
    /// Null was right about avahi and wrong about the conclusion: Windows has
    /// run its own mDNS responder since 10 version 1703, and DnsServiceBrowse
    /// asks it the same question avahi-browse answers.
    /// </summary>
    public INetworkDiscovery? Discovery { get; } = new WindowsNetworkDiscovery();

    /// <summary>
    /// Light or dark and the system accent, read from the registry and watched
    /// for changes. Everything else in the palette is derived — Windows
    /// publishes only those two facts, unlike kdeglobals, which hands over a
    /// whole scheme.
    /// </summary>
    public IThemeProvider? Theme { get; } = new WindowsThemeProvider();

    /// <summary>
    /// Null, and staying that way for a while. Windows has per-file icons from
    /// the shell rather than a theme of named icons, so there is nothing for
    /// this interface to resolve against. Null falls back to the hand-drawn
    /// glyphs in IconLoader.Fallback and SidebarIcon, which is why they are
    /// drawn rather than themed.
    /// </summary>
    public IIconThemeProvider? Icons => null;

    /// <summary>.lnk files, written the way Explorer writes them.</summary>
    public IShortcutMaker? Shortcuts { get; } = new WindowsShortcuts();

    /// <summary>
    /// The shell's own per-file icons, for people who would rather see their
    /// desktop's set than the one this application ships.
    /// </summary>
    public IFileIconProvider? FileIcons { get; } = new WindowsFileIcons();

    /// <summary>
    /// The shell's own menu — every extension the machine has installed.
    /// </summary>
    public IShellMenuProvider? ShellMenu { get; } = new WindowsShellMenuProvider();

    /// <summary>Takes the contents of an archive dragged straight out of
    /// 7-Zip or Explorer's zip view, which arrive with no location on disk.</summary>
    public IVirtualFileDrop? VirtualFileDrop { get; } = new VirtualFileDrop();

    /// <summary>ISO mounting through the Virtual Disk Service — the same
    /// machinery behind Explorer's own Mount verb, and measured working without
    /// administrator rights.</summary>
    public Vaktari.Core.Places.IDiskImages? DiskImages { get; } = new WindowsDiskImages();

    /// <summary>
    /// **Was null on the grounds that the Recycle Bin needs COM, and that COM
    /// under NativeAOT would fail at runtime rather than at compile time.**
    /// The first half was true and the second was never tested: a
    /// source-generated IShellItem enumeration of the bin works correctly in a
    /// published AOT binary. Measuring it also showed the shell is not the
    /// right tool for this interface's three requirements — the bin's own
    /// metadata carries the original path, the size and the deletion time as
    /// plain fields, which is what <see cref="RecycleBin"/> reads.
    /// </summary>
    public ITrashMaintenance? TrashMaintenance { get; } = new WindowsTrashMaintenance();

    // The same bin the operations engine reads to make a recycle undoable.
    // One instance, so the listing it takes before recycling and the restore
    // afterwards are talking about the same thing.
}
