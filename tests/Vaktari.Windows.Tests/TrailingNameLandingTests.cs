using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **Where an item lands is asked too, not only the folder it goes into.**
/// From the seventh review round (FINDING 7-B): a source reached through a
/// literal "\\?\" or "\??\" keeps "report " as its own name, and the copy
/// engine composed its target as the destination plus that name — and asked
/// ReachablePath only of the destination FOLDER. Into a plainly spelled
/// destination, Win32 folded the target to "report": when the destination
/// already held "report", the conflict was raised for "dst\report ", Overwrite
/// replaced the destination's own "report" — a file nobody named — and a move
/// then removed the source. A folder's child "x..." landed as "x" the same way.
///
/// Every landing name is now asked before a byte moves, and a name a plain
/// destination cannot hold refuses the operation, naming it; the prompt is
/// never raised about a file it would misname. Into a destination opened
/// through "\\?\" the name lands as it is. The first theory is the round's
/// repro, pasted as given. All in a temporary folder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingNameLandingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-hole7b").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.Delete(@"\\?\" + _root, recursive: true);
    }

    private static string Extended(string path) => @"\\?\" + path;

    [WindowsTheory]
    [InlineData(@"\\?\", "report ", false)]
    [InlineData(@"\??\", "report ", false)]
    [InlineData(@"\??\", "report.", true)]
    public async Task A_trailing_named_source_never_replaces_another_file_in_a_plain_destination(string prefix, string name, bool move)
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(@"\\?\" + Path.Combine(src, name), "the source");
        File.WriteAllText(Path.Combine(dst, "report"), "the destination's own report");

        var ops = new WindowsFileOperations();
        var source = prefix + Path.Combine(src, name);
        var handle = move
            ? ops.Move([source], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite))
            : ops.Copy([source], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite));
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // RED: "the source" is now in dst\report.
        Assert.Equal("the destination's own report", File.ReadAllText(Path.Combine(dst, "report")));
    }

    /// <summary>
    /// **Refused whole, in words naming the file, with nothing asked and
    /// nothing moved**: the prompt is never raised, the source stays, and the
    /// destination holds only its own file. Copy and move, into a destination
    /// spelled plainly and through "\\.\", which Win32 folds the same way.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\?\", "", "report ", false)]
    [InlineData(@"\\?\", "", "report ", true)]
    [InlineData(@"\??\", "", "report.", false)]
    [InlineData(@"\??\", "", "report.", true)]
    [InlineData(@"\\?\", @"\\.\", "report ", true)]
    [InlineData(@"\??\", "//./", "report.", false)]
    public async Task A_name_the_destination_cannot_hold_is_refused_by_name_before_anything_is_asked(
        string prefix, string destinationPrefix, string name, bool move)
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Extended(Path.Combine(src, name)), "the source");
        File.WriteAllText(Path.Combine(dst, "report"), "the destination's own report");

        var asked = new List<FileConflict>();
        var ops = new WindowsFileOperations();
        var source = prefix + Path.Combine(src, name);
        var destination = destinationPrefix.Contains('/') ? destinationPrefix + dst.Replace('\\', '/') : destinationPrefix + dst;

        ValueTask<ConflictResolution> Overwrite(FileConflict conflict)
        {
            asked.Add(conflict);
            return ValueTask.FromResult(ConflictResolution.Overwrite);
        }

        var handle = move ? ops.Move([source], destination, Overwrite) : ops.Copy([source], destination, Overwrite);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(ReachablePath.RefuseLanding(Path.Combine(dst, name)), handle.Error?.Message);
        Assert.Contains($"\"{name}\"", handle.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(asked);
        Assert.Equal(["report"], Directory.GetFiles(Extended(dst)).Select(Path.GetFileName));
        Assert.Equal("the destination's own report", File.ReadAllText(Path.Combine(dst, "report")));
        Assert.Equal("the source", File.ReadAllText(Extended(Path.Combine(src, name))));
    }

    /// <summary>
    /// **A folder's contents land by name too**: an ordinary folder holding
    /// "x..." and "notes " — reached through "\\?\", where they are their own
    /// names — is refused whole into a plain destination, which already holds
    /// the folder's "x"; nothing of it is created there, and on a move the
    /// source keeps all of it.
    /// </summary>
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_folder_whose_contents_a_plain_destination_cannot_name_is_refused_whole(bool move)
    {
        var folder = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(dst, "album"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Extended(Path.Combine(folder, "x...")), "x with dots");
        Directory.CreateDirectory(Extended(Path.Combine(folder, "notes ")));
        File.WriteAllText(Extended(Path.Combine(folder, "notes ", "n.txt")), "n");
        File.WriteAllText(Path.Combine(dst, "album", "x"), "the destination's own x");

        var ops = new WindowsFileOperations();
        var handle = move
            ? ops.Move([Extended(folder)], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite))
            : ops.Copy([Extended(folder)], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite));
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.NotNull(handle.Error);
        Assert.Equal(["x"], Directory.GetFileSystemEntries(Extended(Path.Combine(dst, "album"))).Select(Path.GetFileName));
        Assert.Equal("the destination's own x", File.ReadAllText(Path.Combine(dst, "album", "x")));
        Assert.Equal(3, Directory.GetFileSystemEntries(Extended(folder)).Length);
    }

    /// <summary>
    /// **Into a destination opened through "\\?\", the name is kept**, and
    /// the file already called "report" there is left alone — the copy and the
    /// move land as "report " beside it.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\?\", false)]
    [InlineData(@"\??\", true)]
    public async Task Into_a_literal_extended_destination_the_name_lands_as_it_is(string prefix, bool move)
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Extended(Path.Combine(src, "report ")), "the source");
        File.WriteAllText(Path.Combine(dst, "report"), "the destination's own report");

        var ops = new WindowsFileOperations();
        var source = prefix + Path.Combine(src, "report ");
        var handle = move
            ? ops.Move([source], Extended(dst), _ => ValueTask.FromResult(ConflictResolution.Overwrite))
            : ops.Copy([source], Extended(dst), _ => ValueTask.FromResult(ConflictResolution.Overwrite));
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("the destination's own report", File.ReadAllText(Path.Combine(dst, "report")));
        Assert.Equal("the source", File.ReadAllText(Extended(Path.Combine(dst, "report "))));
        Assert.Equal(!move, File.Exists(Extended(Path.Combine(src, "report "))));
    }

    /// <summary>The landing rule by text: a plain or "\\.\" destination
    /// cannot hold a trailing space or dot, a literal "\\?\" one can, and a
    /// name that ends in neither is never refused. Text only.</summary>
    [WindowsFact]
    public void A_landing_is_refused_only_where_win32_would_rename_it()
    {
        foreach (var target in new[] { @"C:\dst\report ", @"C:\dst\report.", @"\\.\C:\dst\x...", @"C:\dst\notes \n.txt", @"//?/C:/dst/report " })
            Assert.True(ReachablePath.RefuseLanding(target) is not null, $"{target} was allowed");

        foreach (var target in new[] { @"\\?\C:\dst\report ", @"\??\C:\dst\x...", @"C:\dst\report", @"C:\dst\v1.2.txt", @"\\server\share\a b\c" })
            Assert.Null(ReachablePath.RefuseLanding(target));

        Assert.Contains("\"report\"", ReachablePath.RefuseLanding(@"C:\dst\report ")!, StringComparison.Ordinal);
    }
}
