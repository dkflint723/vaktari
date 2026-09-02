using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The flags a row gets depending on how it arrived.
///
/// **A row from the watcher had fewer flags than the same row from
/// enumeration.** <c>ToFlags</c> sets Directory, Hidden, Symlink and ReadOnly;
/// <c>GetEntryAsync</c>, a dozen lines below it, worked them out again from
/// scratch and set only the first two. So the same file described two ways was
/// two different values — and FileEntry is a record struct compared by every
/// member, which is what the listing's selection resolves against. A file
/// created while you watched could not be selected onto, and a symlink that
/// appeared was drawn as the thing it points at.
///
/// The property, not the implementation: these compare the two paths against
/// each other rather than asserting a particular flag set, so the two cannot
/// drift apart again whatever either one learns next.
/// </summary>
public sealed class WatchedEntryFlagsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-flags-" + Guid.NewGuid().ToString("N")[..8]);

    public WatchedEntryFlagsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private static readonly LinuxFileSystemProvider Provider = new();

    private async Task<FileEntry> Enumerated(string path)
    {
        await foreach (var batch in Provider.EnumerateAsync(
                           _root, new ListingOptions { IncludeHidden = true }, CancellationToken.None))
            foreach (var entry in batch)
                if (entry.FullPath == path)
                    return entry;

        throw new InvalidOperationException($"{path} was not enumerated");
    }

    private async Task<FileEntry> Watched(string path)
        => await Provider.GetEntryAsync(path, CancellationToken.None)
           ?? throw new InvalidOperationException($"{path} was not described");

    [Fact]
    public async Task An_ordinary_file_is_described_the_same_either_way()
    {
        var path = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(path, "x");

        Assert.Equal((await Enumerated(path)).Flags, (await Watched(path)).Flags);
    }

    /// <summary>The one the watcher lost. A read-only file enumerated carries
    /// ReadOnly; described, it did not.</summary>
    [Fact]
    public async Task A_read_only_file_keeps_its_flag_through_the_watcher()
    {
        var path = Path.Combine(_root, "locked.txt");
        await File.WriteAllTextAsync(path, "x");

        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Assert.Equal((await Enumerated(path)).Flags, (await Watched(path)).Flags);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>And the other one: a symlink enumerated carries Symlink, and
    /// described it did not — so it was drawn as the thing it points at.</summary>
    [PosixFact]
    public async Task A_symlink_keeps_its_flag_through_the_watcher()
    {
        var target = Path.Combine(_root, "real.txt");
        var link = Path.Combine(_root, "link.txt");

        await File.WriteAllTextAsync(target, "x");
        File.CreateSymbolicLink(link, target);

        var enumerated = await Enumerated(link);
        var watched = await Watched(link);

        Assert.True(enumerated.IsSymlink, "enumeration lost the symlink flag");
        Assert.Equal(enumerated.Flags, watched.Flags);
    }

    [Fact]
    public async Task A_hidden_file_agrees_too()
    {
        var path = Path.Combine(_root, ".config");
        await File.WriteAllTextAsync(path, "x");

        var enumerated = await Enumerated(path);

        Assert.True(enumerated.IsHidden);
        Assert.Equal(enumerated.Flags, (await Watched(path)).Flags);
    }
}
