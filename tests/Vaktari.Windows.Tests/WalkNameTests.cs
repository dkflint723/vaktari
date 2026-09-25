using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The shared walk and the duplicate finder, against names Windows rewrites
/// and files with more than one name.
///
/// **A duplicate scan could offer a file's only copy as its own spare.** A
/// folder called "dir." was walked as "dir" — the path rules strip the dot
/// before the call — so dir's files came back twice under the same real path,
/// were compared with themselves, and matched. The same for a FILE called
/// "report ", read as "report". And a hard link, two names for one file, was
/// listed as a copy with space to free that deleting frees none of.
///
/// Names like these are made the only way they can be, through the extended
/// prefix, and removed the same way.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(DuplicateIdentityCollection.Name)]
public sealed class WalkNameTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly List<string> _extended = [];
    private readonly Func<string, (ulong, ulong, ulong)?>? _identityBefore = DuplicateFinder.Identity;

    public void Dispose()
    {
        DuplicateFinder.Identity = _identityBefore;

        // Deepest first, files before folders.
        foreach (var path in _extended.OrderByDescending(p => p.Length))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        _tree.Dispose();
    }

    private string Extended(string relative, string? text = null)
    {
        var path = @"\\?\" + _tree.At(relative.Split('/'));

        if (text is null) Directory.CreateDirectory(path);
        else File.WriteAllText(path, text);

        _extended.Add(path);
        return path;
    }

    /// <summary>
    /// "dir." beside "dir" is not walked as "dir": dir's file comes back once,
    /// and "dir." is named as a folder that could not be read.
    /// </summary>
    [WindowsFact]
    public void A_folder_whose_name_windows_rewrites_is_not_walked_as_its_neighbour()
    {
        var root = _tree.Dir("root");
        _tree.Write("root/dir/file.txt", "a");
        Extended("root/dir.");
        Extended("root/dir./file.txt", "b");

        var unreadable = new List<string>();
        var files = SafeWalk.Descend(root, default, unreadable.Add)
            .Where(f => !f.IsDirectory)
            .Select(f => f.Path)
            .ToList();

        Assert.Single(files, f => f == Path.Combine(root, "dir", "file.txt"));
        Assert.Contains(Path.Combine(root, "dir."), unreadable);

        Assert.Empty(DuplicateFinder.Find(root, null, default).Sets);
    }

    /// <summary>
    /// The size in properties walks the same way and was fooled the same way:
    /// "dir." was opened as "dir", and dir's file was counted twice.
    /// </summary>
    [WindowsFact]
    public async Task Properties_does_not_measure_a_folder_as_its_neighbour()
    {
        var root = _tree.Dir("root");
        _tree.Write("root/dir/file.txt", "a");
        Extended("root/dir.");
        Extended("root/dir./other.txt", "bb");

        var size = await new WindowsPropertiesProvider().MeasureAsync(root, new Progress<SizeProgress>(), default);

        // dir's one byte, once. "dir." is a folder that was not entered.
        Assert.Equal(1, size.Files);
        Assert.Equal(1, size.Bytes);
    }

    /// <summary>
    /// **The space-usage view of "data " is not the view of "data".** It
    /// listed its own top level, outside the walk, by a name Windows rewrote.
    /// </summary>
    [WindowsFact]
    public void Space_usage_does_not_list_a_folder_as_its_neighbour()
    {
        var root = _tree.Dir("root");
        _tree.Write("root/data/file.txt", "a");
        var spaced = Extended("root/data ");
        Extended("root/data /other.txt", "bb");

        var listing = SpaceUsage.Underneath(spaced[4..], null, default);

        Assert.Empty(listing.Rows);
        Assert.Equal(1, listing.Total.Unreadable);
    }

    /// <summary>
    /// **What is not an id says nothing.** Some file systems answer every file
    /// with zero, or all ones; taken at its word every file there was one file.
    /// </summary>
    [WindowsFact]
    public void An_id_of_zero_or_all_ones_is_no_identity()
    {
        Assert.Null(FileIdentity.Checked((7, 0, 0)));
        Assert.Null(FileIdentity.Checked((7, ulong.MaxValue, ulong.MaxValue)));
        Assert.Null(FileIdentity.Checked((7, ulong.MaxValue, 0)));
        Assert.Equal((7UL, 42UL, 0UL), FileIdentity.Checked((7, 42, 0)));
    }

    /// <summary>
    /// A folder "x." beside a FILE "x": listing "x." as "x" throws — measured —
    /// and that failure is the folder's, not the whole walk's.
    /// </summary>
    [WindowsFact]
    public void A_folder_whose_name_resolves_to_a_file_does_not_end_the_walk()
    {
        var root = _tree.Dir("root");
        _tree.Write("root/x", "a file");
        _tree.Write("root/other.txt", "still found");
        Extended("root/x.");

        var unreadable = new List<string>();
        var files = SafeWalk.Descend(root, default, unreadable.Add).Select(f => f.Path).ToList();

        Assert.Contains(Path.Combine(root, "other.txt"), files);
        Assert.Contains(Path.Combine(root, "x."), unreadable);
    }

    /// <summary>
    /// "report " and "report", the same length and different bytes: read by
    /// name, the first opened the second, and the two came out identical.
    /// </summary>
    [WindowsFact]
    public void A_file_whose_name_windows_rewrites_is_not_compared_as_its_neighbour()
    {
        var root = _tree.Dir("root");
        _tree.Write("root/report", "aaaa");
        Extended("root/report ", "bbbb");

        var report = DuplicateFinder.Find(root, null, default);

        Assert.Empty(report.Sets);
        Assert.True(report.Unreadable >= 1, "the name that could not be read was not counted");
    }

    /// <summary>
    /// **Two names for one file are not a copy and its original.** mklink /H
    /// needs no privilege.
    /// </summary>
    [WindowsFact]
    public void Two_hard_links_to_one_file_are_not_duplicates()
    {
        var root = _tree.Dir("root");
        var first = _tree.Write("root/a.txt", "the same bytes, one file");
        var second = Path.Combine(root, "b.txt");

        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/H", second, first])
               { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();

        Assert.True(File.Exists(second), "mklink made no hard link, so this proves nothing");

        DuplicateFinder.Identity = null;
        Assert.Single(DuplicateFinder.Find(root, null, default).Sets);

        DuplicateFinder.Identity = FileIdentity.Of;
        Assert.Empty(DuplicateFinder.Find(root, null, default).Sets);
    }
}
