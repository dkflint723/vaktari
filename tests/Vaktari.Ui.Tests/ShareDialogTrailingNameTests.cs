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

    /// <summary>
    /// **However it is spelled**: through "\\?\", which opens "album " as
    /// itself and which the hand-off rule reads without its prefix, and as the
    /// parent of the folder named — the server would be handed a path that
    /// Win32 folds either way (fix-9 verification).
    /// </summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(@"\\?\", "")]
    [InlineData("", "own-folder")]
    [InlineData(@"\\?\", "own-folder")]
    public async Task On_windows_a_folded_folder_is_refused_however_it_is_spelled(string prefix, string below)
    {
        var (model, shared) = Dialog(prefix + Path.Combine(AlbumBeside(), below));

        Assert.False(model.CanShare);
        Assert.Empty(model.Folders);
        Assert.Contains("\"album \" cannot be handed to another program", model.Status, StringComparison.Ordinal);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Empty(shared);
    }

    /// <summary>
    /// **Forward slashes and the "\\.\" device spelling were served as the
    /// neighbour.** ReachablePath.Exact has no spelling for either, so the
    /// dialog decided the folder was not there, trimmed the text, and shared
    /// "album" (fix-9 verification). Each is read as Win32 reads its names and
    /// refused like any other spelling.
    /// </summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("slashes")]
    [InlineData("device")]
    [InlineData("device-slashes")]
    [InlineData("mixed")]
    public async Task On_windows_a_folded_folder_is_refused_in_a_device_or_slashed_spelling(string spelling)
    {
        var album = AlbumBeside();

        var typed = spelling switch
        {
            "slashes" => album.Replace('\\', '/'),
            "device" => @"\\.\" + album,
            "device-slashes" => "//./" + album.Replace('\\', '/'),
            _ => album[..album.LastIndexOf('\\')] + "/album ",
        };

        var (model, shared) = Dialog(typed);

        Assert.False(model.CanShare);
        Assert.Empty(model.Folders);
        Assert.Contains("\"album \" cannot be handed to another program", model.Status, StringComparison.Ordinal);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Empty(shared);
    }

    /// <summary>Three more spellings Win32 reads the same way: "//?/" and
    /// "\\?/", which are not the literal prefix, and a trailing "/" (fix-9
    /// verification, round 2).</summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("//?/", "")]
    [InlineData(@"\\?/", "")]
    [InlineData("", "/")]
    public async Task On_windows_a_folded_folder_is_refused_behind_a_slashed_prefix_or_before_a_trailing_slash(string prefix, string after)
    {
        var typed = prefix + AlbumBeside().Replace('\\', '/') + after;

        var (model, shared) = Dialog(typed);

        Assert.False(model.CanShare);
        Assert.Empty(model.Folders);
        Assert.Contains("\"album \" cannot be handed to another program", model.Status, StringComparison.Ordinal);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Empty(shared);
    }

    /// <summary>The same spellings of an ordinary folder are shared exactly as
    /// typed, and spaces typed after one are still taken off — "album  " with
    /// two, when no such folder exists, is "album".</summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("slashes", "")]
    [InlineData("device", "")]
    [InlineData("slashes", "  ")]
    [InlineData("device", "  ")]
    public async Task On_windows_an_ordinary_folder_in_those_spellings_is_shared_as_typed(string spelling, string after)
    {
        AlbumBeside();
        var album = Path.Combine(_root, "album");

        var typed = spelling == "slashes" ? album.Replace('\\', '/') : @"\\.\" + album;

        var (model, shared) = Dialog(typed + after);

        Assert.True(model.CanShare);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([typed], shared);
    }

    /// <summary>The folder <paramref name="name"/> in the temp root, typed
    /// with a "." or ".." in the way.</summary>
    private string WithDots(string spelling, string name)
    {
        var leaf = Path.GetFileName(_root);

        return spelling switch
        {
            "dot" => _root + @"\.\" + name,
            "dotdot" => _root + @"\..\" + leaf + @"\" + name,
            "slashes-dot" => (_root + "/./" + name).Replace('\\', '/'),
            "device-dot" => @"\\.\" + _root + @"\.\" + name,
            _ => _root + @"\album\..\" + name,
        };
    }

    /// <summary>
    /// **A "." or ".." in the path shared the neighbour.** "\\?\" takes them
    /// as names, so ReachablePath.Exact had no spelling for the folder, the
    /// dialog decided it was not there, trimmed the text, and the server —
    /// which folds the path the way Win32 does — was handed "album"
    /// (changelog check for 0.11.1). The dots are walked away before the
    /// question, the trailing space is kept, and the folder is refused.
    /// </summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("dot")]
    [InlineData("dotdot")]
    [InlineData("slashes-dot")]
    [InlineData("device-dot")]
    [InlineData("back-in")]
    public async Task On_windows_a_folded_folder_is_refused_with_dots_in_the_path(string spelling)
    {
        AlbumBeside();

        var (model, shared) = Dialog(WithDots(spelling, "album "));

        Assert.False(model.CanShare);
        Assert.Empty(model.Folders);
        Assert.Contains("\"album \" cannot be handed to another program", model.Status, StringComparison.Ordinal);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Empty(shared);
    }

    /// <summary>The same spellings of the ordinary folder beside it are
    /// browsed and shared exactly as typed, with spaces typed after them
    /// still taken off.</summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("dot", "")]
    [InlineData("dotdot", "")]
    [InlineData("slashes-dot", "")]
    [InlineData("device-dot", "")]
    [InlineData("back-in", "")]
    [InlineData("dot", "  ")]
    [InlineData("device-dot", "  ")]
    [InlineData("back-in", "  ")]
    public async Task On_windows_an_ordinary_folder_with_dots_in_the_path_is_shared_as_typed(string spelling, string after)
    {
        AlbumBeside();

        var typed = WithDots(spelling, "album");

        var (model, shared) = Dialog(typed + after);

        Assert.Equal(["neighbours-folder"], model.Folders);
        Assert.True(model.CanShare);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([typed], shared);
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

    /// <summary>On Linux a "." or ".." is the kernel's to walk and nothing
    /// folds: "album " with dots in its path is served as typed.</summary>
    [AvaloniaTheory(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    [InlineData("/./")]
    [InlineData("/album/../")]
    public async Task On_linux_album_space_with_dots_in_the_path_is_served_as_typed(string between)
    {
        AlbumBeside();
        var typed = _root + between + "album ";

        var (model, shared) = Dialog(typed);

        Assert.Equal(["own-folder"], model.Folders);
        Assert.True(model.CanShare);

        await model.ShareCommand.ExecuteAsync(null);

        Assert.Equal([typed], shared);
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

    /// <summary>Up, and into a folder, from a path typed with spaces around
    /// it: both start from the folder the box names, not from the raw text
    /// (fix-9 verification).</summary>
    [AvaloniaFact]
    public void Up_and_in_from_a_folder_typed_with_spaces_around_it_start_from_that_folder()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        Directory.CreateDirectory(Path.Combine(plain, "inside"));

        var (up, _) = Dialog("  " + plain + "  ");
        up.GoUpCommand.Execute(null);

        Assert.Equal(_root, up.Path);
        Assert.Contains("plain", up.Folders);

        var (down, _) = Dialog("  " + plain + "  ");
        down.EnterCommand.Execute("inside");

        Assert.Equal(Path.Combine(plain, "inside"), down.Path);
        Assert.True(down.CanShare);
    }

    /// <summary>A root typed with spaces around it has nowhere to go up to.</summary>
    [AvaloniaFact]
    public void A_root_typed_with_spaces_around_it_has_no_parent()
    {
        var root = Path.GetPathRoot(_root)!;
        var (model, _) = Dialog("  " + root + "  ");

        Assert.True(model.CanShare);
        Assert.False(model.CanGoUp);
    }
}
