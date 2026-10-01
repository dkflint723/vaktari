using Avalonia.Headless.XUnit;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The share dialog offered the machine's named pipes as a folder to
/// share.** "\\.\pipe\" answers Directory.Exists true, and so does
/// "\\.\mailslot\", so the dialog called either a folder, listed it, and
/// enabled Share (0.11.1 QA). A device path is refused unless it names a
/// drive, a share or a volume — the three forms VolumeRoots reads — in every
/// spelling Win32 reads the same way, and in the dialog's own sentence.
///
/// The share is a recording: nothing is served, and nothing is written
/// anywhere but a temporary folder.
/// </summary>
public sealed class ShareDialogDeviceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-sharedevice").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private static (ShareRequestViewModel Model, List<string> Shared) Dialog(string path)
    {
        var shared = new List<string>();
        var model = new ShareRequestViewModel(path, (p, _) => { shared.Add(p); return Task.CompletedTask; });
        return (model, shared);
    }

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(@"\\.\pipe\")]
    [InlineData(@"//./pipe/")]
    [InlineData(@"\\?\pipe\")]
    [InlineData(@"\??\pipe\")]
    [InlineData(@"\\.\C:\..\pipe\")]
    [InlineData(@"\\.\mailslot\")]
    public async Task A_device_that_is_not_on_a_volume_is_refused_in_the_dialogs_words(string typed)
    {
        // The premise, so this cannot pass on a machine where the device is
        // simply not there: Windows calls it a folder.
        Assert.True(Directory.Exists(typed.Replace("/", @"\", StringComparison.Ordinal).Replace(@"\??\", @"\\?\", StringComparison.Ordinal)),
                    $"{typed} is not answered as a folder here, so the dialog would refuse it anyway");

        var (model, shared) = Dialog(typed);

        Assert.False(model.CanShare);
        Assert.Empty(model.Folders);
        Assert.Equal($"\"{typed}\" is not a folder on a drive, a network share or a volume — only those can be shared",
                     model.Status);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Empty(shared);
    }

    /// <summary>A drive, in either device spelling, is still a place a folder
    /// can be shared from: the rule is about the namespace, not the prefix.</summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(@"\\.\")]
    [InlineData(@"\\?\")]
    public async Task A_folder_on_a_drive_in_a_device_spelling_is_still_shared(string prefix)
    {
        Directory.CreateDirectory(Path.Combine(_root, "plain", "inside"));
        var typed = prefix + Path.Combine(_root, "plain");

        var (model, shared) = Dialog(typed);

        Assert.Equal(["inside"], model.Folders);
        Assert.True(model.CanShare);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([typed], shared);
    }
}
