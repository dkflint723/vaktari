using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Every store's temp-and-rename flushes its temp file to the disk before the
/// rename.
///
/// **The writes were atomic against a crash of the process and not against a
/// crash of the machine.** Each store wrote a temp file, flushed it out of the
/// process, and renamed it over the real one. The rename is journaled and the
/// data was not, so after a power cut NTFS can come back with the rename done
/// and the bytes never written: a file of the right length full of zeros, which
/// parses as nothing and reads as amnesia. <c>Flush(flushToDisk: true)</c> is
/// the fix, and nothing a test can do to a running process shows whether it is
/// there — so this reads the source, which is the one place it can be seen.
///
/// A list of files rather than a search, and a count of renames per file: a
/// store added later without the flush is the thing this exists to catch, and
/// a count that stops matching is how a new rename in one of these announces
/// itself.
/// </summary>
public sealed class StoresFlushToDiskTests
{
    /// <summary>Each store, and how many temp-and-rename writes it holds.</summary>
    public static TheoryData<string, int> Stores => new()
    {
        { "Session/JsonSessionStore.cs", 1 },
        { "Settings/JsonSettingsStore.cs", 1 },
        { "Settings/JsonFolderViewStore.cs", 1 },
        { "Settings/JsonRecentStore.cs", 1 },
        { "Settings/JsonSearchHistory.cs", 1 },
        { "Settings/JsonDriveLinkStore.cs", 1 },
        { "../Vaktari.Windows/WindowsPlacesProvider.cs", 1 },
        { "../Vaktari.Linux/LinuxPlacesProvider.cs", 1 },
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public void Every_rename_follows_a_flush_to_the_disk(string file, int renames)
    {
        var source = RepoSource.Ui(file.Split('/'));

        var moves = 0;

        for (var at = source.IndexOf("File.Move(", StringComparison.Ordinal);
             at >= 0;
             at = source.IndexOf("File.Move(", at + 1, StringComparison.Ordinal))
        {
            moves++;

            // Back to the file this rename is moving into place: whatever lies
            // between its creation and the rename is the write.
            var created = Math.Max(
                source.LastIndexOf("File.Create(", at, StringComparison.Ordinal),
                source.LastIndexOf("new FileStream(", at, StringComparison.Ordinal));

            Assert.True(created >= 0, $"{file}: a rename with no temp file created before it");

            Assert.Contains(
                "Flush(flushToDisk: true)",
                source[created..at],
                StringComparison.Ordinal);
        }

        Assert.Equal(renames, moves);
    }
}
