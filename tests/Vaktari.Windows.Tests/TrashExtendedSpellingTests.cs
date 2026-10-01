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
    [InlineData(@"\\.\UNC\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"\\.\unc\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"\\?\C:\x\a.txt", @"C:\x\a.txt")]
    [InlineData(@"\\.\C:\x\a.txt", @"C:\x\a.txt")]
    [InlineData(@"C:\x\a.txt", @"C:\x\a.txt")]
    [InlineData(@"\\server\share\a.txt", @"\\server\share\a.txt")]
    public void The_shell_is_handed_the_ordinary_spelling(string full, string expected)
        => Assert.Equal((expected, (string?)null), WindowsFileOperations.ForTheShell(full));

    [WindowsTheory]
    [InlineData(@"\\?\UNC\server\share\report ")]
    [InlineData(@"\\.\UNC\server\share\report ")]
    [InlineData(@"\\.\UNC\server\share\x.\a.txt")]
    [InlineData(@"\\?\C:\x.\a.txt")]
    public void A_spelling_that_would_fold_has_none(string full)
    {
        var (spelling, refusal) = WindowsFileOperations.ForTheShell(full);

        Assert.Null(spelling);
        Assert.Contains("cannot be handed to another program", refusal, StringComparison.Ordinal);
    }

    /// <summary>The engine with nothing on disk asked whether a path is a
    /// root: there is no share server here.</summary>
    private WindowsFileOperations Unrooted() => new()
    {
        RootOnDisk = _ => false,
        RecycleOverride = paths =>
        {
            _asked.AddRange(paths);
            return new RecycleResult(0, false);
        },
    };

    /// <summary>
    /// Both extended spellings of a share reach the shell as the share.
    /// Through the recycler seam with the string alone: there is no share
    /// server, nothing on disk is asked whether it is a root, and the real
    /// bin is never reached.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\??\UNC\server\share\x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    public async Task An_extended_spelling_of_a_share_reaches_the_shell_as_the_share(string spelled)
    {
        var handle = await Settled(Unrooted().Trash([spelled]));

        Assert.Null(handle.Error?.Message);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal([@"\\server\share\x"], _asked);
    }

    /// <summary>
    /// **"\\.\UNC\" is refused at the door, by the device-path rule, and not
    /// with the sentence about a volume's device name.** ForTheShell read it
    /// as any other "\\.\" path, "UNC\server\…", and answered
    /// <see cref="WindowsFileOperations.NoShellSpelling"/> — the wrong reason,
    /// for a path the shell takes as "\\server\…" (batch-0.11.2 QA,
    /// probe-for-the-shell); it now reads it as the share (the theory above).
    /// Through Trash that sentence was never reached: every destructive verb
    /// refuses a "\\.\UNC\" spelling first (VolumeRoots: only "\\?\UNC\" and
    /// "\??\UNC\" are read as a share there), saying to open the folder by
    /// its ordinary name. That is what the person sees, and nothing is asked
    /// of the shell.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\UNC\server\share\x")]
    [InlineData(@"//./UNC/server/share/x")]
    public async Task A_dot_spelling_of_a_share_is_refused_as_a_device_path_not_as_a_volume(string spelled)
    {
        var handle = await Settled(Unrooted().Trash([spelled]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.DeviceRefusal, handle.Error?.Message);
        Assert.Empty(_asked);
    }

    /// <summary>
    /// **The shell folds more than Win32 does.** Win32 keeps a trailing space
    /// on a name in the MIDDLE of a path — "…\sp \a.txt" opens the file under
    /// "sp " — so a rule taught Win32's exact folding would hand this path on.
    /// SHFileOperation does not keep it: given that same plain spelling, it
    /// deleted "…\sp\a.txt", the file beside it (FO_DELETE without
    /// FOF_ALLOWUNDO on a probe's own temporary files, so no bin was involved;
    /// batch-0.11.2 QA, probe-middle-fold). The hand-off rule refuses a space
    /// on any name, and that is what keeps the bin off the neighbour here.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_ending_in_a_space_midway_is_refused_because_the_shell_folds_it()
    {
        var neighbour = Path.Combine(_root, "sp", "a.txt");
        var row = Path.Combine(_root, "sp ", "a.txt");

        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "sp"));
        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "sp "));
        File.WriteAllText(@"\\?\" + neighbour, "neighbour");
        File.WriteAllText(@"\\?\" + row, "row");

        var handle = await Settled(_ops.Trash([@"\\?\" + row]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.StartsWith("\"sp \" cannot be handed to another program", handle.Error?.Message, StringComparison.Ordinal);
        Assert.Empty(_asked);

        Assert.Equal("neighbour", File.ReadAllText(@"\\?\" + neighbour));
        Assert.Equal("row", File.ReadAllText(@"\\?\" + row));
    }

    /// <summary>
    /// **One name with no ordinary spelling keeps the whole batch from the
    /// shell**, the ones that have one included: the bin is one call for the
    /// batch, and a delete that went through for some rows and was refused for
    /// one would leave the person to work out which. Nothing is handed over,
    /// and the file that could have gone is where it was.
    /// </summary>
    [WindowsFact]
    public async Task One_name_without_an_ordinary_spelling_keeps_the_whole_batch_from_the_shell()
    {
        var fine = Made("notes.txt");
        Made("report");
        var row = Made("report ");

        var handle = await Settled(_ops.Trash([@"\\?\" + fine, @"\\?\" + row]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.StartsWith("\"report \" cannot be handed to another program", handle.Error?.Message, StringComparison.Ordinal);
        Assert.Empty(_asked);
        Assert.True(File.Exists(fine), "the file with an ordinary spelling went anyway");
    }
}
