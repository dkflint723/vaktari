using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The bin from a pane opened through "\\?\" failed every time.** The pane
/// hands its rows' paths over as it lists them, and SHFileOperation's own
/// parser answers 124 (DE_INVALIDFILES) for "\\?\", "\??\" and "\\.\" alike —
/// measured with FO_DELETE and no FOF_ALLOWUNDO on a probe's own temporary
/// files, so no bin was involved (batch-0.11.2 notes, shfileop-probe). The
/// person got the shell's refusal, with nothing gone.
///
/// The shell is handed the ordinary spelling when that reaches the same entry,
/// and a name it would not reach — "report " beside "report" — is refused in
/// the hand-off rule's sentence. Never the real Recycle Bin: the recycler here
/// is a recording, and every file is in a temporary folder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrashExtendedSpellingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-trashext").FullName;
    private readonly List<string> _asked = [];
    private readonly WindowsFileOperations _ops;

    public TrashExtendedSpellingTests()
    {
        _ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                _asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(@"\\?\" + _root))
            File.Delete(file);

        Directory.Delete(@"\\?\" + _root, recursive: true);
    }

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    private string Made(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(@"\\?\" + path, name);
        return path;
    }

    [WindowsTheory]
    [InlineData(@"\\?\")]
    [InlineData(@"\??\")]
    public async Task An_extended_spelling_reaches_the_shell_by_its_ordinary_name(string prefix)
    {
        var file = Made("notes.txt");

        var handle = await Settled(_ops.Trash([prefix + file]));

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Empty(handle.Problems);
        Assert.Equal([file], _asked);
    }

    /// <summary>
    /// "report " is reached by "\\?\" alone; its ordinary spelling is
    /// "report", the file beside it, which is what the bin would have taken.
    /// Refused, by name, and both files are where they were.
    /// </summary>
    [WindowsTheory]
    [InlineData("report ")]
    [InlineData("report.")]
    public async Task A_name_the_ordinary_spelling_would_fold_is_refused_with_a_sentence(string name)
    {
        var neighbour = Made("report");
        var row = Made(name);

        var handle = await Settled(_ops.Trash([@"\\?\" + row]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.StartsWith($"\"{name}\" cannot be handed to another program", handle.Error?.Message, StringComparison.Ordinal);
        Assert.Empty(_asked);

        Assert.Equal("report", File.ReadAllText(neighbour));
        Assert.Equal(name, File.ReadAllText(@"\\?\" + row));
    }

    /// <summary>A file named by its volume's GUID has no spelling the shell
    /// parses, so it is refused in a sentence rather than by a number.</summary>
    [WindowsFact]
    public async Task A_volume_guid_spelling_is_refused_with_a_sentence()
    {
        var file = Made("notes.txt");

        var mount = new char[1024];
        Assert.True(Native.GetVolumePathName(file, mount, (uint)mount.Length));
        var mountPoint = new string(mount).TrimEnd('\0');

        var name = new char[64];
        Assert.True(Native.GetVolumeNameForVolumeMountPoint(mountPoint, name, (uint)name.Length));
        var volume = new string(name).TrimEnd('\0');

        var spelled = volume + file[mountPoint.Length..];
        Assert.True(File.Exists(spelled), $"{spelled} does not name the file, so this would prove nothing");

        var handle = await Settled(_ops.Trash([spelled]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(WindowsFileOperations.NoShellSpelling, handle.Error?.Message);
        Assert.Empty(_asked);
    }

    /// <summary>A share, by text alone — no server is asked.</summary>
    [WindowsTheory]
    [InlineData(@"\\?\UNC\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"\??\UNC\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"\\?\C:\x\a.txt", @"C:\x\a.txt")]
    [InlineData(@"\\.\C:\x\a.txt", @"C:\x\a.txt")]
    [InlineData(@"C:\x\a.txt", @"C:\x\a.txt")]
    [InlineData(@"\\server\share\a.txt", @"\\server\share\a.txt")]
    public void The_shell_is_handed_the_ordinary_spelling(string full, string expected)
        => Assert.Equal((expected, (string?)null), WindowsFileOperations.ForTheShell(full));

    [WindowsTheory]
    [InlineData(@"\\?\UNC\server\share\report ")]
    [InlineData(@"\\?\C:\x.\a.txt")]
    public void A_spelling_that_would_fold_has_none(string full)
    {
        var (spelling, refusal) = WindowsFileOperations.ForTheShell(full);

        Assert.Null(spelling);
        Assert.Contains("cannot be handed to another program", refusal, StringComparison.Ordinal);
    }
}
