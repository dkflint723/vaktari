using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Core.Places;
using Vaktari.Core.Search;

namespace Vaktari.Linux;

/// <summary>
/// The Linux composition root. Everything OS-specific is built here, so the UI
/// project holds exactly one reference to a platform type — inside one
/// OperatingSystem.IsLinux() check.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    private readonly LinuxPropertiesProvider _properties = new();

    public LinuxPlatform(string stateDirectory)
    {
        FileManagerService = new FreedesktopFileManager(_defaults);

        // Started here rather than in its constructor, for the reason given on
        // the Windows twin.
        var places = new LinuxPlacesProvider(stateDirectory);
        places.Start();
        Places = places;
        Icons = new FreedesktopIconTheme(Theme?.Read()?.IconTheme, naming: new XdgIconNaming());

        // The listing's own name for a .desktop row. Adopted here beside the
        // icon naming, and for the same reason Naming.Adopt is called from the
        // one place a platform is chosen: Core cannot reference this assembly,
        // and a launcher's name is a parse of a freedesktop file.
        FileKind.LauncherName = path => DesktopEntries.Launcher(path).Name;
    }

    public string Name => "linux";

    /// <summary>
    /// Lowercase, because it is a common noun here and the desktop treats it as
    /// one: the freedesktop specification calls it the trash, and Dolphin and
    /// Nautilus write it in running text without capitals.
    /// </summary>
    /// <summary>
    /// Held as a field rather than rebuilt, because the D-Bus service asks it
    /// the same question the settings page does — whether Vaktari is the
    /// desktop's folder handler — and two objects answering it separately is
    /// two answers waiting to disagree.
    /// </summary>
    private readonly LinuxDefaultFileManager _defaults = new();

    public IDefaultFileManager? DefaultFileManager => _defaults;

    /// <summary>Answers other applications' "show this file in its folder".
    /// Built here, dormant until something calls ReconcileAsync.</summary>
    public IFileManagerService? FileManagerService { get; }

    public string BinName => "trash";

    public IFileSystemProvider FileSystem { get; } = new LinuxFileSystemProvider();
    public IFileOperations Operations { get; } = new LinuxFileOperations();
    public IApplicationLauncher Launcher { get; } = new LinuxLauncher();
    public IPlacesProvider Places { get; }
    public ISearchProvider Search { get; } = new LinuxSearchProvider();
    public IThumbnailProvider Thumbnails { get; } = new XdgThumbnailProvider();
    public IFileMetadataProvider Metadata { get; } = new LinuxMetadataProvider();

    public IPropertiesProvider Properties => _properties;

    // The same object serves both — reading and writing permissions share the
    // mode-bit mapping, and splitting them would duplicate it.
    public IAccessEditor? AccessEditor => _properties;

    public IScriptRunner Scripts { get; } = new LinuxScriptRunner();

    public ITemplateProvider Templates { get; } = new XdgTemplates();

    public IFileSharing? Sharing { get; } = new CopypartyShare(new LinuxCopyparty());

    public IRemoteMounts? Remotes { get; } = new LinuxRemoteMounts();

    public INetworkDiscovery? Discovery { get; } = new AvahiDiscovery();

    /// <summary>ISO mounting through udisks2, which needs no rights of our own
    /// — polkit grants loop-setup and mount to an active local session.</summary>
    public Vaktari.Core.Places.IDiskImages? DiskImages { get; } = new LinuxDiskImages();

    public IThemeProvider? Theme { get; } = new KdeThemeProvider();

    /// <summary>
    /// Built from the theme name Plasma reports, so it follows whatever the
    /// user picked in System Settings rather than assuming Breeze.
    /// </summary>
    public IIconThemeProvider? Icons { get; }

    /// <summary>Symbolic links, which are what a shortcut is here.</summary>
    public IShortcutMaker? Shortcuts { get; } = new LinuxShortcuts();

    public ITrashMaintenance? TrashMaintenance { get; } = new XdgTrashMaintenance();
}
