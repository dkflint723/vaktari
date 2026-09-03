using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Taking a mapped network drive off the sidebar.
///
/// **There was no way to.** Its row offered Open, Open in a new tab, Pin and
/// Properties; Eject is for media you take out, and the remote list
/// deliberately holds only the letterless connections Vaktari made itself — so
/// the only way to get rid of Z: was `net use /delete` in a console.
/// </summary>
public sealed class DisconnectPlaceTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    /// <summary>
    /// **A folder that really exists stands in for the drive.** With a made-up
    /// "Z:\\" the pane cannot load it, leaves on its own, and the test that
    /// says "a tab is moved off first" passes whether or not anything moves it
    /// — which is what it did until this was noticed.
    /// </summary>
    private readonly string _mapped = Path.Combine(
        Path.GetTempPath(), "vaktari-mapped-" + Guid.NewGuid().ToString("N")[..8]);

    public DisconnectPlaceTests() => Directory.CreateDirectory(_mapped);

    public override void Dispose()
    {
        try { Directory.Delete(_mapped, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private (ShellViewModel Shell, Redirector Remotes) Shell()
    {
        var remotes = new Redirector();
        var shell = Own(new ShellViewModel(new Inert(), places: new OneMappedDrive(_mapped)));

        shell.UseRemotes(remotes);

        return (shell, remotes);
    }

    private static PlaceItemViewModel Drive(ShellViewModel shell)
        => shell.Sidebar.Groups.SelectMany(g => g.Places).First(p => p.CanDisconnect);

    /// <summary>The whole finding: the row can be given back.</summary>
    [AvaloniaFact]
    public async Task A_mapped_drive_can_be_disconnected()
    {
        var (shell, remotes) = Shell();

        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        var drive = Drive(shell);

        await shell.DisconnectPlaceCommand.ExecuteAsync(drive);

        Assert.Equal([_mapped], remotes.Disconnected);
    }

    /// <summary>
    /// **A tab standing on the drive is moved off it first.** Every tab in both
    /// panes, not just the visible one: a background tab holds its directory
    /// watch open exactly like a visible one does, and an unseen tab vetoing
    /// the disconnect is the least explicable failure of the lot.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tab_standing_on_it_is_moved_off_first()
    {
        var (shell, _) = Shell();

        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        var tab = shell.ActiveTab!;

        await tab.NavigateAsync(_mapped);

        Assert.Equal(_mapped, tab.CurrentPath);

        await shell.DisconnectPlaceCommand.ExecuteAsync(Drive(shell));

        Assert.NotEqual(_mapped, tab.CurrentPath);
    }

    /// <summary>
    /// A place that cannot be disconnected is not disconnected — the row is
    /// hidden, but a command must not depend on a binding to refuse.
    /// </summary>
    [AvaloniaFact]
    public async Task A_local_disk_is_left_alone()
    {
        var (shell, remotes) = Shell();

        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        var local = shell.Sidebar.Groups.SelectMany(g => g.Places).First(p => !p.CanDisconnect);

        await shell.DisconnectPlaceCommand.ExecuteAsync(local);

        Assert.Empty(remotes.Disconnected);
    }

    /// <summary>And it says so, in the pane that asked.</summary>
    [AvaloniaFact]
    public async Task It_says_what_it_did()
    {
        var (shell, _) = Shell();

        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        await shell.DisconnectPlaceCommand.ExecuteAsync(Drive(shell));

        Assert.Contains("disconnected", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// **A refusal is not silence.** Something holding a file open on the share
    /// is the ordinary case, and a row that vanishes from the menu having done
    /// nothing is the worst way to report it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drive_that_will_not_let_go_says_so()
    {
        var (shell, remotes) = Shell();

        remotes.Refuse = true;

        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        await shell.DisconnectPlaceCommand.ExecuteAsync(Drive(shell));

        Assert.Contains("could not disconnect", shell.ActiveTab!.Status);
    }

    /// <summary>The row is in the menu, and only on a drive that has the verb.</summary>
    [Fact]
    public void The_place_menu_offers_it()
    {
        var row = Assert.Single(
            XDocument.Parse(RepoSource.Ui("MainWindow.axaml")).Descendants(Avalonia + "MenuItem"),
            e => ((string?)e.Attribute("Command") ?? "").Contains("DisconnectPlaceCommand"));

        Assert.Equal("Disconnect", (string?)row.Attribute("Header"));
        Assert.Equal("{Binding CanDisconnect}", (string?)row.Attribute("IsVisible"));
        Assert.Equal("{Binding}", (string?)row.Attribute("CommandParameter"));
    }

    /// <summary>A redirector that remembers what it was asked to give back.</summary>
    private sealed class Redirector : IRemoteMounts
    {
        public List<string> Disconnected { get; } = [];

        public bool Refuse { get; set; }

        public Task<bool> DisconnectAsync(string path, CancellationToken ct)
        {
            Disconnected.Add(path);

            return Task.FromResult(!Refuse);
        }

        public bool IsAvailable => true;
        public string AddressPrefill => "";
        public string AddressHint => "";

        public IReadOnlyList<RemoteMount> Discover() => [];

        public Task<bool> UnmountAsync(RemoteMount mount, CancellationToken ct)
            => DisconnectAsync(mount.Path, ct);

        public Task<RemoteMount> MountAsync(string address, CancellationToken ct)
            => throw new NotSupportedException("nothing here mounts");
    }

    /// <summary>One mapped drive and one local disk.</summary>
    private sealed class OneMappedDrive(string mapped) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("DEVICES",
                [
                    new Place
                    {
                        Id = "dev:C",
                        Label = "Local disk (C:)",
                        Path = Path.GetTempPath(),
                        Kind = PlaceKind.Device,
                        Icon = "device-desktop",
                    },
                    new Place
                    {
                        Id = "dev:mapped",
                        Label = "media on nas (Z:)",
                        Path = mapped,
                        Kind = PlaceKind.Network,
                        Icon = "server",
                        CanDisconnect = true,
                    },
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
