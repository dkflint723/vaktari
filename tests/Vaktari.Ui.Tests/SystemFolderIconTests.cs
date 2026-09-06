using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Settings;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Use my desktop's icons", for the folders that have something in them.
///
/// **The setting worked for empty folders and not for full ones**, which is a
/// strange enough shape to be worth pinning: every folder with a file in it
/// went on showing Vaktari's own drawn icon, so the listing came out half one
/// icon set and half the other, and the half that obeyed the setting was the
/// half nobody looks at.
///
/// The cause was ordering rather than anything to do with the shell. A folder
/// gets a papers-in-the-folder affordance from a background probe, and that
/// probe repaints — so running it after the shell's icon had been painted put
/// the drawn icon straight back on top. It belongs only where the drawn set is
/// what is on screen.
/// </summary>
public sealed class SystemFolderIconTests : IDisposable
{
    /// <summary>Distinctive pixels, so what ends up on the Image can be
    /// identified by instance rather than guessed at from its type.</summary>
    private static readonly IconPixels Shell =
        new(8, 8, new byte[8 * 8 * 4].Select((_, i) => (byte)(i % 251)).ToArray());

    private sealed class FakeIcons : IFileIconProvider
    {
        public IconPixels? IconFor(string path, bool isDirectory, int size) => Shell;
    }

    private readonly string _folder;
    private readonly Vaktari.Core.Settings.SettingsState _before;

    public SystemFolderIconTests()
    {
        // A real folder with a real file in it: the probe that caused this asks
        // the filesystem, so an empty temp directory would pass either way.
        _folder = Path.Combine(Path.GetTempPath(), "vaktari-icons-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "something.txt"), "x");

        _before = AppSettings.Current;

        IconLoader.Files = new FakeIcons();
        IconLoader.Provider = null;

        AppSettings.Apply(_before with
        {
            General = _before.General with
            {
                UseSystemIcons = true,

                // **The other half of the condition since it stopped being
                // `Provider is null`.** A chosen theme switches the
                // desktop-icons route off outright, and this inherits whatever
                // folder the running settings happen to name — so without it
                // the route is off and every assertion below is about the
                // wrong branch.
                IconThemeFolder = "",
            },
        });
    }

    public void Dispose()
    {
        AppSettings.Apply(_before);
        IconLoader.Files = null;

        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private static FileEntry Entry(string path) =>
        new(Path.GetFileName(path), path, 0, DateTimeOffset.UtcNow, EntryFlags.Directory);

    /// <summary>
    /// Waits for the icon to settle rather than for a fixed time.
    ///
    /// The work is an async void property handler with two hops through the
    /// thread pool, so there is nothing to await. It keeps pumping after the
    /// shell's icon lands, deliberately: the defect was a SECOND paint arriving
    /// afterwards, and a check that stopped at the first would pass against it.
    /// </summary>
    private static async Task Settle(Image image)
    {
        for (var i = 0; i < 200; i++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task A_full_folder_shows_the_desktops_icon_and_keeps_it()
    {
        var image = new Image();

        RowIcon.SetSize(image, 24);
        RowIcon.SetEntry(image, Entry(_folder));

        await Settle(image);

        // Both halves matter. The desktop's icon is what should be there...
        Assert.Same(IconLoader.Draw(Shell), image.Source);

        // ...and the drawn folder-with-papers is what used to replace it. Same
        // call the probe makes, and FileTypeIcon caches by category, so this is
        // the very instance it would have painted.
        Assert.NotSame(
            FileTypeIcon.For(Path.GetFileName(_folder), isDirectory: true, hasContents: true),
            image.Source);
    }

    /// <summary>
    /// The empty case was never broken, and is here so that a fix aimed at the
    /// full one cannot quietly trade the two around — which is precisely what
    /// the original ordering did, in the other direction.
    /// </summary>
    [AvaloniaFact]
    public async Task An_empty_folder_shows_the_desktops_icon_too()
    {
        var empty = Path.Combine(_folder, "nothing-in-here");
        Directory.CreateDirectory(empty);

        var image = new Image();

        RowIcon.SetSize(image, 24);
        RowIcon.SetEntry(image, Entry(empty));

        await Settle(image);

        Assert.Same(IconLoader.Draw(Shell), image.Source);
    }
}
