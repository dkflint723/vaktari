using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// One entry, the whole flow: "Share via Proton Drive" shows for any item
/// inside the drive folder whether or not the CLI exists yet, and the click
/// fetches the tool first when it must. The person asked to share; the steps
/// between are Vaktari's errand, not theirs.
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

    private static ShellViewModel Shell(ProtonDriveLinks links)
    {
        var shell = new ShellViewModel(new Inert());
        shell.Start(null, Path.GetTempPath());
        shell.UseDriveLinks(links, [], _ => { });

        return shell;
    }

    [AvaloniaFact]
    public void The_share_row_shows_before_the_tool_exists()
    {
        var shell = Shell(new ProtonDriveLinks
        {
            LocalRoot = Root,
            LocateOverride = () => null,
        });

        Assert.True(shell.CanLinkShare(Path.Combine(Root, "notes.txt")));
    }

    [AvaloniaFact]
    public void Outside_the_drive_folder_nothing_shows()
    {
        var shell = Shell(new ProtonDriveLinks
        {
            LocalRoot = Root,
            LocateOverride = () => null,
        });

        Assert.False(shell.CanLinkShare(
            Path.Combine(Path.GetTempPath(), "elsewhere.txt")));
    }

    [AvaloniaFact]
    public void While_installing_the_share_row_yields_to_the_busy_row()
    {
        var shell = Shell(new ProtonDriveLinks
        {
            LocalRoot = Root,
            LocateOverride = () => null,
        });

        shell.IsInstallingDriveLinks = true;

        Assert.True(shell.ShowDriveInstallBusy(Path.Combine(Root, "notes.txt")));
        Assert.False(shell.ShowDriveInstallBusy(
            Path.Combine(Path.GetTempPath(), "elsewhere.txt")));
    }

    /// <summary>
    /// The whole click, end to end: no tool → the share fetches it, then
    /// creates the link, then the row is remembered — one gesture from the
    /// user's side.
    /// </summary>
    [AvaloniaFact]
    public async Task Sharing_without_the_tool_installs_it_and_then_shares()
    {
        var tools = Directory.CreateTempSubdirectory("vaktari-share-install").FullName;

        try
        {
            var name = OperatingSystem.IsWindows() ? "proton-drive.exe" : "proton-drive";
            var landed = Path.Combine(tools, name);

            var links = new ProtonDriveLinks
            {
                LocalRoot = Root,
                ToolsDirOverride = tools,
                LocateOverride = () => File.Exists(landed) ? landed : null,
                FetchOverride = (_, destination, _) =>
                    File.WriteAllBytesAsync(destination, [1, 2, 3]),
                RunOverride = (_, _) => Task.FromResult(
                    new ProtonDriveLinks.CliResult(0, """{"url":"https://drive.proton.me/urls/abc"}""", "")),
            };

            var shell = Shell(links);
            var path = Path.Combine(Root, "notes.txt");

            Assert.False(links.IsAvailable);

            await shell.CreateDriveLinkAsync(path);

            Assert.True(links.IsAvailable);
            Assert.True(File.Exists(landed));

            var link = Assert.Single(shell.DriveLinks);
            Assert.Equal("https://drive.proton.me/urls/abc", link.Url);
        }
        finally
        {
            Directory.Delete(tools, recursive: true);
        }
    }

    /// <summary>A dead download stops the flow with the reason on the status
    /// line — and no half-share appears anywhere.</summary>
    [AvaloniaFact]
    public async Task A_failed_install_stops_the_share_and_says_why()
    {
        var tools = Directory.CreateTempSubdirectory("vaktari-share-fail").FullName;

        try
        {
            var links = new ProtonDriveLinks
            {
                LocalRoot = Root,
                ToolsDirOverride = tools,
                LocateOverride = () => null,
                FetchOverride = (_, _, _) => throw new IOException("the network went away"),
            };

            var shell = Shell(links);

            await shell.CreateDriveLinkAsync(Path.Combine(Root, "notes.txt"));

            Assert.Empty(shell.DriveLinks);
            Assert.False(shell.IsInstallingDriveLinks);
        }
        finally
        {
            Directory.Delete(tools, recursive: true);
        }
    }
}
