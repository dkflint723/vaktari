using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A places.json that could not be read.
///
/// **It was overwritten at startup.** The load answered empty, the import
/// found a GTK bookmark or a Dolphin place, the count changed, and the save
/// replaced the file — every pin and saved search gone before the window had
/// drawn, from a file that may only have been unreadable for a moment. Two
/// rules now, one per half of each test: the import does not write over it,
/// and the first deliberate change keeps a copy before replacing it.
///
/// Plain facts, run on any machine. The bookmark line is <c>file://</c>
/// followed by the folder, which on Linux is the real GTK shape
/// (<c>file:///tmp/...</c>) and on Windows is a path the importer reads
/// back unchanged; what is under test is what happens to the file, not the
/// parsing of the line.
/// </summary>
public sealed class UnreadablePlacesFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-unreadable-places").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    private string State => Path.Combine(_root, "state");

    private string PlacesFile => Path.Combine(State, "places.json");

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private byte[] Unreadable()
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(PlacesFile, "{not json");
        return File.ReadAllBytes(PlacesFile);
    }

    private LinuxPlacesProvider Provider(string home)
        => new(State)
        {
            MountLines = () => [],
            FilesystemDevices = () => [],
            SwapLines = () => [],
            VolumeLabels = () => new Dictionary<string, string>(),
            ImportHome = () => home,
        };

    [Fact]
    public async Task The_import_does_not_write_over_it()
    {
        var original = Unreadable();

        var home = Folder("home");
        var gtk = Directory.CreateDirectory(Path.Combine(home, ".config", "gtk-3.0")).FullName;
        File.WriteAllText(Path.Combine(gtk, "bookmarks"), "file://" + Folder("Projects") + "\n");

        var provider = Provider(home);

        Assert.Equal(1, await provider.ImportExistingAsync(CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(PlacesFile));
    }

    [Fact]
    public async Task The_first_pin_keeps_a_copy_before_replacing_it()
    {
        var original = Unreadable();

        var provider = Provider(Folder("home"));

        await provider.PinAsync(Folder("Later"), null, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(PlacesFile + ".bak"));
        Assert.Contains("Later", File.ReadAllText(PlacesFile), StringComparison.Ordinal);
    }
}
