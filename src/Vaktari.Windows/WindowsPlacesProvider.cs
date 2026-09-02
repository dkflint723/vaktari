using System.Text.Json;
using System.Text.Json.Serialization;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;

namespace Vaktari.Windows;

public sealed record PinnedPlace(string Path, string Label);

[JsonSerializable(typeof(List<PinnedPlace>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PinnedPlacesJsonContext : JsonSerializerContext;

/// <summary>
/// The sidebar's data source on Windows, and the place where the "drive letters
/// versus mount points" difference is expressed honestly rather than papered
/// over with a fake common root. Where Linux parses /proc/mounts and filters out
/// loop devices and squashfs images, this asks
/// <see cref="DriveInfo.GetDrives"/> and gets a clean list back.
///
/// **No P/Invoke.** <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// covers the user folders that have a SpecialFolder, which is all of them bar
/// one — see <see cref="Downloads"/>.
/// </summary>
public sealed class WindowsPlacesProvider : IPlacesProvider, IDisposable
{
    private readonly string _pinsPath;
    private DeviceWatch? _watch;
    /// <summary>
    /// **Replaced, never mutated in place.** Building the places list reads
    /// this, and that build moved off the UI thread because enumerating drives
    /// blocks for the SMB timeout on a disconnected mapped drive. Pinning still
    /// happens on the UI thread, so an Add during that enumeration would throw
    /// "collection was modified" from a background task nobody is awaiting.
    ///
    /// Copy-on-write instead of a lock: a reader captures the reference it
    /// started with and finishes against a consistent list, a writer publishes
    /// a new one, and reference assignment is atomic. The lists are a handful
    /// of entries, so copying them costs nothing worth measuring.
    /// </summary>
    private List<PinnedPlace> _pins;

    public event EventHandler? PlacesChanged;

    public WindowsPlacesProvider(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _pinsPath = Path.Combine(stateDirectory, "places.json");
        _pins = LoadPins();
    }

    /// <summary>
    /// Begins watching for drives arriving and leaving, so a stick plugged in
    /// shows up on its own.
    ///
    /// **Deliberately not done in the constructor.** Tests build providers
    /// freely and a background loop started by construction is a leak with a
    /// heartbeat; the composition root is where a fact about this machine — that
    /// it has removable drives someone might plug into — is allowed to live.
    /// Idempotent, so calling it twice does not give the sidebar two watchers.
    /// </summary>
    public void Start()
    {
        if (_watch is not null) return;

        _watch = new DeviceWatch(DriveSet.Snapshot);
        _watch.Changed += (_, _) => PlacesChanged?.Invoke(this, EventArgs.Empty);
        _watch.Start();
    }

    /// <summary>Stops the watch. Nothing calls this in the running application —
    /// the process exiting is the shutdown path — but a test that starts one
    /// must be able to stop it.</summary>
    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
    }

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// **The one user folder with no SpecialFolder value**, so it is assembled
    /// from the profile directory. That is wrong for anyone who has relocated
    /// Downloads — the folder is genuinely movable and its real location lives
    /// in the known-folder table, reachable only through SHGetKnownFolderPath.
    /// The entry is dropped rather than shown broken when the guess is not
    /// there, so a relocated Downloads is missing rather than dead.
    /// </summary>
    private static string Downloads => Path.Combine(Home, "Downloads");

    public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
    {
        var groups = new List<PlaceGroup>
        {
            new("places", BuildUserPlaces()),
        };

        var (devices, network) = BuildDrives();

        if (devices.Count > 0) groups.Add(new PlaceGroup("devices", devices));
        if (network.Count > 0) groups.Add(new PlaceGroup("network", network));

        return ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(groups);
    }

    private List<Place> BuildUserPlaces()
    {
        // Captured once: this runs off the UI thread and pinning runs on it.
        var pins = _pins;

        // Case-insensitively, because C:\Users\flint and c:\users\flint are one
        // folder here — the same reason PathRules.Comparison is platform-dependent.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var places = new List<Place>
        {
            new()
            {
                Id = "home", Label = "Home", Path = Home,
                Kind = PlaceKind.UserFolder, Icon = "home",
            },
        };

        seen.Add(PathRules.Normalise(Home));

        foreach (var (id, path, icon) in new[]
        {
            ("desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "desktop"),
            ("downloads", Downloads, "download"),
            ("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "file-text"),
            ("pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "photo"),
            ("music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "music"),
            ("videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "video"),
        })
        {
            // GetFolderPath answers "" rather than throwing when a folder is not
            // configured, and Downloads is a guess — so existence is checked
            // rather than assumed.
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            if (!seen.Add(PathRules.Normalise(path))) continue;

            places.Add(new Place
            {
                Id = id, Label = PathRules.LeafName(path), Path = path,
                Kind = PlaceKind.UserFolder, Icon = icon,
            });
        }

        foreach (var pin in pins)
        {
            if (!seen.Add(PathRules.Normalise(pin.Path))) continue;

            places.Add(new Place
            {
                Id = "pin:" + pin.Path,
                Label = pin.Label,
                Path = pin.Path,
                Kind = PlaceKind.Bookmark,
                Icon = "bookmark",
                IsUserPinned = true,
                IsAvailable = Directory.Exists(pin.Path),
            });
        }

        // Withheld until it worked, which is the whole reason it is here now.
        // The note this replaces said an entry that opens an empty view and
        // cannot restore anything is worse than no entry, because it looks like
        // the trash is empty rather than like a missing feature. The same
        // virtual path as Linux, because the view behind it is shared and reads
        // through ITrashMaintenance — which Windows now implements.
        places.Add(new Place
        {
            Id = "trash",
            Label = "Recycle Bin",
            Path = "vaktari:trash",
            Kind = PlaceKind.Bookmark,
            Icon = "trash",
        });

        return places;
    }

    private (List<Place> Devices, List<Place> Network) BuildDrives()
    {
        var devices = new List<Place>();
        var network = new List<Place>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return (devices, network); }

        foreach (var drive in drives)
        {
            // A card reader with no card, or an empty optical bay, is a real
            // drive letter with no filesystem behind it. Shown dimmed and in
            // place rather than dropped — a slot that disappears when empty is
            // harder to find than one that is always there.
            var ready = false;
            try { ready = drive.IsReady; }
            catch (IOException) { /* treat as not ready */ }

            var place = BuildDrive(drive, ready);

            if (drive.DriveType == DriveType.Network) network.Add(place);
            else devices.Add(place);
        }

        return (devices, network);
    }

    private static Place BuildDrive(DriveInfo drive, bool ready)
    {
        var root = drive.Name;
        var removable = drive.DriveType is DriveType.Removable or DriveType.CDRom;

        long? capacity = null, free = null;
        string? label = null;

        if (ready)
        {
            // Every one of these throws on a drive that stopped being ready
            // between the check and the read, which a USB stick can do.
            try
            {
                capacity = drive.TotalSize;
                free = drive.AvailableFreeSpace;
                label = drive.VolumeLabel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new Place
        {
            Id = "dev:" + root,
            // "Windows (C:)" — the volume label with its letter, which is how
            // every other Windows file manager names a drive, and unambiguous
            // when two volumes share a label.
            Label = string.IsNullOrWhiteSpace(label)
                ? DefaultLabel(drive.DriveType, root)
                : $"{label} ({root.TrimEnd(Path.DirectorySeparatorChar)})",
            Path = root,
            Kind = drive.DriveType switch
            {
                DriveType.Network => PlaceKind.Network,
                DriveType.Removable or DriveType.CDRom => PlaceKind.RemovableDevice,
                _ => PlaceKind.Device,
            },
            Icon = drive.DriveType switch
            {
                DriveType.Network => "server",
                // Before Removable: an optical drive is removable too, and the
                // first arm that matches wins.
                DriveType.CDRom => "disc",
                DriveType.Removable => "usb",
                _ => "device-desktop",
            },
            CapacityBytes = capacity,
            FreeBytes = free,
            IsAvailable = ready,
            CanEject = removable,
        };
    }

    private static string DefaultLabel(DriveType type, string root)
    {
        var letter = root.TrimEnd(Path.DirectorySeparatorChar);

        return type switch
        {
            DriveType.CDRom => $"Optical drive ({letter})",
            DriveType.Removable => $"Removable disk ({letter})",
            DriveType.Network => $"Network drive ({letter})",
            _ => $"Local disk ({letter})",
        };
    }

    public ValueTask PinAsync(string path, string? label, CancellationToken ct)
    {
        if (_pins.Any(p => PathRules.Same(p.Path, path))) return ValueTask.CompletedTask;

        _pins = [.. _pins, new PinnedPlace(path, label ?? PathRules.LeafName(path))];
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask UnpinAsync(string id, CancellationToken ct)
    {
        var path = id.StartsWith("pin:", StringComparison.Ordinal) ? id[4..] : id;
        _pins = _pins.Where(p => !PathRules.Same(p.Path, path)).ToList();
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(string id, string label, CancellationToken ct)
    {
        var tidy = PlaceNames.Clean(label);
        if (tidy.Length == 0) return ValueTask.CompletedTask;

        var path = id.StartsWith("pin:", StringComparison.Ordinal) ? id[4..] : id;

        // Copy-on-write, never an in-place edit: _pins is handed out to readers
        // on other threads, which is what its own comment requires.
        _pins = _pins
            .Select(p => PathRules.Same(p.Path, path) ? p with { Label = tidy } : p)
            .ToList();

        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
    {
        var order = orderedIds
            .Where(i => i.StartsWith("pin:", StringComparison.Ordinal))
            .Select(i => i[4..])
            .ToList();

        _pins = _pins
            .OrderBy(p => order.FindIndex(o => PathRules.Same(o, p.Path)) is var i && i < 0
                ? int.MaxValue
                : i)
            .ToList();

        SavePins();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Windows mounts and ejects through the shell, and a drive letter that is
    /// present is already mounted — so there is nothing here to do that
    /// <see cref="GetPlacesAsync"/> has not done. Eject needs the shell's own
    /// "safely remove" path, which is COM.
    /// </summary>
    public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Stands in for the real device work, so the rules above it —
    /// which id is refused, and when the sidebar rebuilds — are testable with
    /// no hardware.</summary>
    internal IEjector? EjectorOverride { get; init; }

    /// <summary>
    /// Safely removes the drive behind a place id.
    ///
    /// **Both refusals happen here, before the ejector is constructed**, so
    /// "the system drive is never handed to the device layer" is a fact about
    /// the call graph rather than a promise inside it.
    /// </summary>
    public async ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
    {
        if (Find(id) is not { } place)
            return EjectResult.NotRemovable("that drive is not there any more");

        if (!place.CanEject)
            return EjectResult.NotRemovable($"{place.Label} is not a removable drive");

        var ejector = EjectorOverride ?? new WindowsEjector();
        var result = await ejector.EjectAsync(place.Path, ct).ConfigureAwait(false);

        // **Only a real ejection rebuilds the sidebar.** A row that vanished
        // after a vetoed eject would tell the person the drive is gone while it
        // is still mounted — and the watch will notice a genuine departure on
        // its own within the second regardless.
        if (result.VolumeIsGone) PlacesChanged?.Invoke(this, EventArgs.Empty);

        return result;
    }

    private Place? Find(string id)
        => BuildDrives().Devices.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Imports the shortcut folders Explorer keeps as files.
    ///
    /// **Quick Access is still not among them, and that is the honest half of
    /// this.** It is where a Windows user's real bookmarks live, and it is not
    /// a file — it is a shell namespace extension whose backing store is an OLE
    /// compound jumplist, readable in practice only through COM. So this reads
    /// the two places that ARE files: the Links folder, which is where Explorer
    /// kept Favorites before Quick Access replaced it and which many profiles
    /// still carry, and Network Shortcuts, which is where "Add a network
    /// location" puts things.
    ///
    /// Partial beats nothing here because the alternative was returning 0 at
    /// every startup while Linux imported its user's bookmarks — but it is
    /// worth being clear that a user whose pins are all in Quick Access will
    /// still see no change, and that is waiting on the same COM decision as the
    /// Trash view and the open-with list.
    /// </summary>
    public ValueTask<int> ImportExistingAsync(CancellationToken ct)
    {
        var before = _pins.Count;
        var builtIn = BuiltInPaths();

        ImportShortcuts(Path.Combine(Home, "Links"), builtIn);

        ImportShortcuts(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Network Shortcuts"),
            builtIn);

        // Anything previously imported that duplicates a built-in is dropped
        // too, so an existing places.json is repaired rather than preserved.
        _pins = _pins
            .Where(pin => !builtIn.Contains(PathRules.Normalise(pin.Path)))
            .ToList();

        if (_pins.Count != before)
        {
            SavePins();
            PlacesChanged?.Invoke(this, EventArgs.Empty);
        }

        return ValueTask.FromResult(_pins.Count - before);
    }

    /// <summary>
    /// Every .lnk in a folder that points at a directory, pinned under the
    /// shortcut's own name — "Sync.lnk" becomes "Sync", which is what the user
    /// called it rather than what the target folder happens to be called.
    /// </summary>
    private void ImportShortcuts(string directory, HashSet<string> builtIn)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (var shortcut in Directory.EnumerateFiles(directory, "*.lnk"))
            {
                var target = ShellLink.TargetOf(shortcut);

                // Not a folder, or a virtual location with no path: skip it
                // rather than pinning something that cannot be listed.
                if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) continue;

                if (builtIn.Contains(PathRules.Normalise(target))) continue;
                if (_pins.Any(p => PathRules.Same(p.Path, target))) continue;

                _pins = [.. _pins, new PinnedPlace(
                    target, Path.GetFileNameWithoutExtension(shortcut))];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A shortcuts folder that cannot be read is not worth failing
            // startup over; the rest of the sidebar is unaffected.
            Quiet.Swallowed("places", e);
        }
    }

    /// <summary>
    /// The paths the sidebar already shows without any pin, so importing one
    /// does not put the same folder on screen twice.
    /// </summary>
    private HashSet<string> BuiltInPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PathRules.Normalise(Home) };

        foreach (var path in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        })
        {
            if (!string.IsNullOrEmpty(path)) paths.Add(PathRules.Normalise(path));
        }

        return paths;
    }

    private List<PinnedPlace> LoadPins()
    {
        try
        {
            if (!File.Exists(_pinsPath)) return [];
            using var stream = File.OpenRead(_pinsPath);
            return JsonSerializer.Deserialize(
                stream, PinnedPlacesJsonContext.Default.ListPinnedPlace) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SavePins()
    {
        try
        {
            var temp = _pinsPath + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, _pins, PinnedPlacesJsonContext.Default.ListPinnedPlace);

            File.Move(temp, _pinsPath, overwrite: true);
            PlacesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* a lost pin is not worth interrupting the user over */ }
    }
}
