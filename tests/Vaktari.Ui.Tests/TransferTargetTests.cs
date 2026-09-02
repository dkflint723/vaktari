using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The transfer submenus after the menu was regrouped: "Copy to" and "Move to"
/// carry the other pane as their first row, replacing the two extra top-level
/// entries — four flat transfer rows collapsed to two submenus.
/// </summary>
public sealed class TransferTargetTests : OwnedViewModels
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

    /// <summary>
    /// With a split, the other pane leads the list — it is the destination
    /// people reach for most, and burying it under the places would make the
    /// fold a demotion rather than a tidying.
    /// </summary>
    [AvaloniaFact]
    public void In_a_split_the_other_pane_is_the_first_target()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        shell.ToggleSplitCommand.Execute(null);
        Assert.True(shell.IsSplit);

        // The transfer entries only exist for a selection — the sentinel obeys
        // the same gate as the submenu that holds it.
        shell.ActiveTab!.SelectedEntry = new FileEntry(
            "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
            1, DateTimeOffset.UnixEpoch, EntryFlags.None);

        var targets = shell.TransferTargets;

        Assert.NotEmpty(targets);
        Assert.Equal(ShellViewModel.OtherPaneTargetId, targets[0].Id);
        Assert.Equal("The other pane", targets[0].Label);
    }

    /// <summary>Without a split there is no other pane to offer.</summary>
    [AvaloniaFact]
    public void Without_a_split_it_is_absent()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        Assert.False(shell.IsSplit);
        Assert.DoesNotContain(
            shell.TransferTargets, t => t.Id == ShellViewModel.OtherPaneTargetId);
    }
}
