using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Sharing;
using Vaktari.Ui.Input;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The QA round's own checks on the streamlining change (qa/streamline).
///
/// **The menu's Share waits for the look too.** ShareGateTests pins the
/// keyboard route (RequestShare); the right-click rows go through ShareAsync,
/// which the background look can also beat — a share that is installed would
/// be answered "copyparty is not installed" from a menu opened in the first
/// moment after startup.
///
/// **An older settings file still loads whole.** Mount and Unmount never had a
/// command id or a setting, and Extract all keeps its id; a file written by an
/// older Vaktari, with a key bound to an id this build does not know and a
/// member this build does not read, must still load every choice it can
/// honour rather than fall back to defaults — the store's catch-all turns any
/// deserialization failure into "defaults", which is the silent version of
/// losing them.
/// </summary>
public sealed class StreamlineQaTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-streamline-qa").FullName;

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

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

    /// <summary>A share backend whose look lands when the test says, and
    /// which records the folder a start was asked for.</summary>
    private sealed class Looking : IFileSharing
    {
        private readonly TaskCompletionSource _look = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _available;

        public int Asked;
        public readonly List<string> Started = [];

        public void Finish(bool available)
        {
            _available = available;
            _look.TrySetResult();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool IsKnown => _look.Task.IsCompleted;
        public bool IsAvailable => IsKnown && _available;
        public string? UnavailableReason => IsAvailable ? null : "copyparty is not installed";
        public IReadOnlyList<ShareSession> Active => [];

        public event EventHandler? Changed;

        public Task EnsureKnownAsync()
        {
            Interlocked.Increment(ref Asked);
            return _look.Task;
        }

        public Task<ShareSession> StartAsync(string path, ShareOptions options, CancellationToken ct)
        {
            lock (Started) Started.Add(path);
            throw new InvalidOperationException("recorded, not started");
        }

        public Task StopAsync(ShareSession session) => Task.CompletedTask;
        public Task StopAllAsync() => Task.CompletedTask;
        public Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct) => Task.FromResult(true);
    }

    [AvaloniaFact]
    public async Task The_menu_share_asked_before_the_look_lands_waits_for_it()
    {
        var sharing = new Looking();
        var shell = Own(new ShellViewModel(new Inert(), sharing: sharing));
        shell.Start(null, _root);
        Dispatcher.UIThread.RunJobs();

        var pane = shell.ActiveTab!;
        var asking = shell.ShareCurrentFolderCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(sharing.Asked > 0, "the menu's share did not ask for the look");
        Assert.NotEqual("copyparty is not installed", pane.Status);
        lock (sharing.Started) Assert.Empty(sharing.Started);

        sharing.Finish(available: true);

        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!asking.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(asking.IsCompleted, "the menu's share never finished");
        lock (sharing.Started) Assert.Equal([_root], sharing.Started);
    }

    [Fact]
    public void An_older_settings_file_with_ids_this_build_does_not_know_still_loads_whole()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "settings")).FullName;

        // Written by this build first, so every member name is the store's
        // own; then the file is made "older": a key bound to ids no build of
        // this one knows, and members nothing here reads.
        new JsonSettingsStore(folder).Save(new SettingsState
        {
            General = new GeneralSettings { ShowTooltips = false },
            Keyboard = new KeyboardSettings
            {
                Bindings = new(StringComparer.Ordinal)
                {
                    ["NewTab"] = ["Ctrl+K"],
                    ["Extract"] = ["Ctrl+Shift+E"],
                },
            },
        });

        var file = Path.Combine(folder, "settings.json");
        var document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();

        var keyboard = document.First(p => p.Key.Equals("keyboard", StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        var bindings = keyboard.First(p => p.Key.Equals("bindings", StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        bindings["MountImage"] = new JsonArray("Ctrl+M");
        bindings["UnmountImage"] = new JsonArray("Ctrl+Shift+M");

        document["diskImages"] = new JsonObject { ["lastMounted"] = @"C:\images\old.iso" };
        document.First(p => p.Key.Equals("general", StringComparison.OrdinalIgnoreCase)).Value!.AsObject()["mountOnOpen"] = true;

        File.WriteAllText(file, document.ToJsonString());

        var store = new JsonSettingsStore(folder);
        var loaded = store.Load();

        Assert.Null(store.ReadOnlyReason);
        Assert.False(loaded.General.ShowTooltips, "the older file was read as defaults");
        Assert.Equal(["Ctrl+K"], loaded.Keyboard.Bindings["NewTab"]);
        Assert.Equal(["Ctrl+Shift+E"], loaded.Keyboard.Bindings["Extract"]);

        // The ids nothing answers to are dropped from the keymap, not thrown,
        // and every known binding is still in force.
        var keys = Keymap.From(loaded.Keyboard);

        Assert.Single(keys.KeysOf("NewTab"));
        Assert.Single(keys.KeysOf("Extract"));
        Assert.Contains(keys.Dropped, d => d.Contains("MountImage", StringComparison.Ordinal));
    }
}
