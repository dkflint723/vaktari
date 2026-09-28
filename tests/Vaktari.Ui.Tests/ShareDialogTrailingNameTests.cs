using Avalonia.Headless.XUnit;
using Vaktari.Core.Sharing;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The share dialog trimmed every path it was given**, so a folder called
/// "album " — an ordinary name on Linux, and on Windows one that arrives
/// through WSL or a share — was shared as "album", its neighbour, and browsed
/// as it too (fix-8 verification). On Linux the folder is now served exactly
/// as named; on Windows, where the server would be handed the neighbour, it is
/// refused with the hand-off sentence and not browsed. What a person types
/// around a path that exists without it is still taken off.
///
/// The share is a recording: nothing is served. The folders are in a
/// temporary folder, the Windows trailing names made through "\\?\".
/// </summary>
public sealed class ShareDialogTrailingNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-sharefold").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Raw(_root), recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private static string Raw(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;

    /// <summary>"album" holding "neighbours-folder", and "album " holding "own-folder".</summary>
    private string AlbumBeside()
    {
        Directory.CreateDirectory(Path.Combine(_root, "album", "neighbours-folder"));
        Directory.CreateDirectory(Raw(Path.Combine(_root, "album ", "own-folder")));
        return Path.Combine(_root, "album ");
    }

    private static (ShareRequestViewModel Model, List<string> Shared) Dialog(string path)
    {
        var shared = new List<string>();
        var model = new ShareRequestViewModel(path, (p, _) => { shared.Add(p); return Task.CompletedTask; });
        return (model, shared);
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task On_windows_a_folded_folder_is_refused_and_not_browsed_as_its_neighbour()
    {
        var (model, shared) = Dialog(AlbumBeside());

        Assert.Empty(model.Folders);
        Assert.False(model.CanShare);
        Assert.Contains("\"album \" cannot be handed to another program", model.Status, StringComparison.Ordinal);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Empty(shared);
    }

    /// <summary>Reached by browsing rather than typing: the parent lists
    /// "album " by its true name, and entering it is refused the same way.</summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public void On_windows_a_folded_folder_entered_by_browsing_is_refused()
    {
        AlbumBeside();
        var (model, _) = Dialog(_root);

        Assert.Contains("album ", model.Folders);

        model.EnterCommand.Execute("album ");

        Assert.Equal(Path.Combine(_root, "album "), model.Path);
        Assert.False(model.CanShare);
        Assert.Empty(model.Folders);
    }

    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task On_linux_a_folder_named_album_space_is_served_as_itself()
    {
        var album = AlbumBeside();
        var (model, shared) = Dialog(album);

        Assert.Equal(["own-folder"], model.Folders);
        Assert.True(model.CanShare);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([album], shared);
    }

    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task On_linux_browsing_into_album_space_serves_it_as_itself()
    {
        var album = AlbumBeside();
        var (model, shared) = Dialog(_root);

        model.EnterCommand.Execute("album ");

        Assert.Equal(["own-folder"], model.Folders);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([album], shared);
    }

    /// <summary>What a person typed around a path that exists without it is
    /// still taken off, on both platforms.</summary>
    [AvaloniaTheory]
    [InlineData("  ", "  ")]
    [InlineData("", "  ")]
    [InlineData("", " ")]
    public async Task Spaces_typed_around_a_folder_are_taken_off(string before, string after)
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        Directory.CreateDirectory(Path.Combine(plain, "inside"));

        var (model, shared) = Dialog(before + plain + after);

        Assert.Equal(["inside"], model.Folders);
        Assert.True(model.CanShare);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([plain], shared);
    }
}
