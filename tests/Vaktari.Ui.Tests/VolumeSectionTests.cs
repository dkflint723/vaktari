using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The volume rows reaching the properties window.
///
/// VolumePropertiesTests owns the rows themselves. These are about the wiring:
/// that the window asks at all, where the group lands among the others, and
/// that the question is not put on the thread that has to draw the answer.
/// </summary>
public sealed class VolumeSectionTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;

    public void Dispose()
    {
        VolumeProperties.Reader = null;

        GC.SuppressFinalize(this);
    }

    private static readonly string Drive =
        Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    private static void Reads(VolumeUsage? usage)
        => VolumeProperties.Reader = _ => usage;

    /// <summary>The load is started and not awaited by the window, so a test
    /// waits for the answer the way the window does — on the clock rather than
    /// for a number of turns, because the volume read is a real pool hop.</summary>
    private static async Task Settles(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<PropertiesViewModel> Opened(string path, bool directory)
    {
        var model = new PropertiesViewModel(new Says(directory), [path], access: null);

        _ = model.LoadAsync();

        await Settles(() => model.Groups.Count > 0);

        return model;
    }

    /// <summary>
    /// **Above the platform's own groups, below general.** Where this sits is
    /// part of the same "what am I looking at" question the general rows
    /// answer; permissions and ownership are about the item itself.
    /// </summary>
    [AvaloniaFact]
    public async Task The_volume_group_sits_between_general_and_the_platforms_own()
    {
        Reads(new VolumeUsage(Drive, "Storage", "NTFS", 8 * Gib, 2 * Gib));

        var model = await Opened(Path.Combine(Drive, "somewhere"), directory: true);

        Assert.Equal(["general", "volume", "permissions"],
                     model.Groups.Select(g => g.Label));
    }

    /// <summary>The finding restated at the window: a folder is told how much
    /// room is left, which is what somebody about to copy into it is asking.</summary>
    [AvaloniaFact]
    public async Task A_folders_window_says_how_much_room_is_left()
    {
        Reads(new VolumeUsage(Drive, "Storage", "NTFS", 8 * Gib, 2 * Gib));

        var model = await Opened(Path.Combine(Drive, "somewhere"), directory: true);

        var volume = model.Groups.Single(g => g.Label == "volume");

        Assert.Equal("2 GiB of 8 GiB", volume.Rows.Single(r => r.Label == "free").Value);
    }

    [AvaloniaFact]
    public async Task A_files_window_carries_no_volume_group()
    {
        Reads(new VolumeUsage(Drive, "Storage", "NTFS", 8 * Gib, 2 * Gib));

        var model = await Opened(Path.Combine(Drive, "notes.txt"), directory: false);

        Assert.DoesNotContain(model.Groups, g => g.Label == "volume");
    }

    /// <summary>
    /// **The read is off the UI thread, and not for tidiness.** The Windows
    /// provider's GetAsync returns an already-completed ValueTask, so nothing
    /// before this point in the load has yielded and the continuation is still
    /// on the thread that opened the window — and reading a volume is a stat.
    /// Volumes.MountPoints carries the measurement that made that matter: on
    /// Unix DriveInfo.IsReady is a Directory.Exists, and a stat on a hung NFS
    /// or sshfs mount does not return.
    ///
    /// The reader records which thread asked it, and the assertion is that it
    /// was not the one the dispatcher runs on.
    /// </summary>
    [AvaloniaFact]
    public async Task The_volume_is_not_read_on_the_thread_that_has_to_draw_it()
    {
        var asked = 0;

        VolumeProperties.Reader = _ =>
        {
            asked = Environment.CurrentManagedThreadId;

            return new VolumeUsage(Drive, "Storage", "NTFS", 8 * Gib, 2 * Gib);
        };

        var drawing = Environment.CurrentManagedThreadId;

        var model = await Opened(Path.Combine(Drive, "somewhere"), directory: true);

        Assert.Contains(model.Groups, g => g.Label == "volume");
        Assert.NotEqual(0, asked);
        Assert.NotEqual(drawing, asked);
    }

    /// <summary>Answers immediately and always the same way, so what these
    /// assert is the window's own doing. It carries one platform group, which
    /// is what the volume group has to be ordered against.</summary>
    private sealed class Says(bool directory) : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = directory,
                Kind = directory ? "Folder" : "File",
                Modified = DateTimeOffset.UnixEpoch,
                Groups = [new PropertyGroup("permissions", [new PropertyRow("mode", "rwxr-xr-x")])],
            });

        public ValueTask<SizeProgress> MeasureAsync(
            string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(0, 0, 0));

        public bool ShowSystemDialog(string path) => false;
    }
}
