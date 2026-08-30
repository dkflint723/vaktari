using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The install offer in the Share submenu: shown exactly where the SHARE row
/// would be if the tool were present — inside the drive folder — and nowhere
/// else. Someone with no Proton Drive never meets the entry; someone with the
/// folder but not the tool gets the one-click path the user asked for.
/// </summary>
public sealed class DriveInstallOfferTests
{
    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "proton-root");

    private static ShellViewModel Shell(bool toolInstalled)
    {
        var links = new ProtonDriveLinks(
            binaryOverride: toolInstalled ? "/fake/proton-drive" : null)
        {
            LocalRoot = Root,
            LocateOverride = () => null,
        };

        var shell = new ShellViewModel(new Inert());
        shell.Start(null, Path.GetTempPath());
        shell.UseDriveLinks(links, [], _ => { });

        return shell;
    }

    [AvaloniaFact]
    public void Inside_the_drive_folder_without_the_tool_the_install_is_offered()
    {
        var shell = Shell(toolInstalled: false);
        var inside = Path.Combine(Root, "notes.txt");

        Assert.True(shell.CanOfferDriveInstall(inside));
        Assert.False(shell.CanLinkShare(inside));
    }

    [AvaloniaFact]
    public void Outside_the_folder_there_is_nothing_to_install_for()
    {
        var shell = Shell(toolInstalled: false);

        Assert.False(shell.CanOfferDriveInstall(
            Path.Combine(Path.GetTempPath(), "elsewhere.txt")));
    }

    [AvaloniaFact]
    public void With_the_tool_present_the_offer_yields_to_the_share_row()
    {
        var shell = Shell(toolInstalled: true);
        var inside = Path.Combine(Root, "notes.txt");

        Assert.False(shell.CanOfferDriveInstall(inside));
        Assert.True(shell.CanLinkShare(inside));
    }

    [AvaloniaFact]
    public void While_installing_the_offer_becomes_the_busy_row()
    {
        var shell = Shell(toolInstalled: false);
        var inside = Path.Combine(Root, "notes.txt");

        shell.IsInstallingDriveLinks = true;

        Assert.False(shell.CanOfferDriveInstall(inside));
        Assert.True(shell.ShowDriveInstallBusy(inside));

        // And never for an item the folder does not cover.
        Assert.False(shell.ShowDriveInstallBusy(
            Path.Combine(Path.GetTempPath(), "elsewhere.txt")));
    }
}
