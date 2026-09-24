using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The checksum button is not offered for something that is not a file.
///
/// **A named pipe's properties and one click froze the application.** Opening
/// a FIFO to read waits until something writes, the open ran on the thread
/// drawing the window, and Stop could not be clicked because nothing could.
/// The provider now says when a path is a FIFO, a socket or a device, and the
/// button is not offered for one; the compute also runs on the pool from its
/// first line.
/// </summary>
public sealed class SpecialFileChecksumTests
{
    [AvaloniaFact]
    public async Task A_fifo_is_not_offered_a_checksum()
    {
        var model = new PropertiesViewModel(new Says(special: true), ["/tmp/pipe"], access: null);

        await model.LoadAsync();

        Assert.False(model.CanChecksum);
    }

    [AvaloniaFact]
    public async Task A_file_still_is()
    {
        var model = new PropertiesViewModel(new Says(special: false), ["/tmp/notes.txt"], access: null);

        await model.LoadAsync();

        Assert.True(model.CanChecksum);
    }

    private sealed class Says(bool special) : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = false,
                IsSpecial = special,
            });

        public ValueTask<SizeProgress> MeasureAsync(
            string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(0, 0, 0));

        public bool ShowSystemDialog(string path) => false;
    }
}
