using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **"\\.\" is folded by Win32, and was let through as if it were not.**
/// ReachablePath exempted "\\.\" from its trailing space and dot rule along
/// with "\\?\", but only a literal "\\?\" (or "\??\") is opened as written:
/// with "report", "report " and "report." in one folder, the sixth review
/// round's Delete, Move, Copy and Trash of "\\.\C:\…\report " and
/// "\\.\C:\…\report." all acted on "report" — the wrong file, every time.
///
/// Every spelling here is one Win32 folds, and each verb refuses both names
/// with ReachablePath's own sentence; reached through a literal "\\?\" or
/// "\??\", each verb acts on the name it was given and "report" is untouched.
/// All three files live in a temporary folder; the recycler is a recording.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceSpelledTrailingNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-devtrailing").FullName;
    private readonly string _into = Directory.CreateTempSubdirectory("vaktari-devtrailing-into").FullName;
    private readonly List<string> _asked = [];
    private readonly WindowsFileOperations _ops;

    private static readonly Dictionary<string, string> Contents = new()
    {
        ["report"] = "the innocent one",
        ["report "] = "the one with a space",
        ["report."] = "the one with a dot",
    };

    public DeviceSpelledTrailingNameTests()
    {
        foreach (var (name, content) in Contents)
            File.WriteAllText(Extended(Path.Combine(_root, name)), content);

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
        foreach (var folder in new[] { _root, _into })
        {
            if (!Directory.Exists(Extended(folder))) continue;

            foreach (var file in Directory.GetFiles(Extended(folder)))
                File.Delete(file);

            Directory.Delete(Extended(folder), recursive: true);
        }
    }

    private static string Extended(string path) => @"\\?\" + path;

    /// <summary>What is in a folder, by true name, read through the extended
    /// prefix so nothing is folded on the way.</summary>
    private static Dictionary<string, string> Files(string folder)
        => Directory.GetFiles(Extended(folder)).ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

    private static string Spelled(string prefix, string path)
        => prefix.Contains('/') ? prefix + path.Replace('\\', '/') : prefix + path;

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    private static void AssertRefusedByName(IOperationHandle handle)
    {
        var message = handle.Error?.Message ?? Assert.Single(handle.Problems).Error.Message;

        Assert.Contains("Windows cannot open it by name", message, StringComparison.Ordinal);
    }

    [WindowsTheory]
    [InlineData(@"\\.\", "report ")]
    [InlineData(@"\\.\", "report.")]
    [InlineData(@"//./", "report ")]
    [InlineData(@"//./", "report.")]
    [InlineData(@"//?/", "report ")]
    [InlineData(@"//?/", "report.")]
    [InlineData(@"\\?/", "report ")]
    [InlineData(@"/\?\", "report.")]
    [InlineData(@"\/?/", "report ")]
    public async Task A_folded_spelling_of_a_trailing_name_is_refused_by_every_verb(string prefix, string name)
    {
        var path = Spelled(prefix, Path.Combine(_root, name));

        Assert.NotNull(ReachablePath.Refuse(path));

        AssertRefusedByName(await Settled(_ops.Delete([path])));
        AssertRefusedByName(await Settled(_ops.Trash([path])));
        AssertRefusedByName(await Settled(_ops.Move([path], _into, _ => ValueTask.FromResult(ConflictResolution.Skip))));
        AssertRefusedByName(await Settled(_ops.Copy([path], _into, _ => ValueTask.FromResult(ConflictResolution.Skip))));

        Assert.Empty(_asked);
        Assert.Equal(Contents, Files(_root));
        Assert.Empty(Files(_into));
    }

    /// <summary>
    /// **The rule reads the path as Win32 will open it.** A name a ".." right
    /// after it takes away is never opened, so "\\.\X:\x\...\.." — the folder x,
    /// and acted on as x (VolumeRootOnDiskTests) — is not refused, in any
    /// spelling Win32 folds. A trailing name the fold keeps is refused however
    /// it is reached: "report \...\.." opens "report" (measured through
    /// Path.GetFullPath, which is Win32's own fold). Text only.
    /// </summary>
    [WindowsFact]
    public void A_trailing_name_is_refused_when_win32_would_open_it_and_only_then()
    {
        foreach (var prefix in new[] { "", @"\\.\", "//./", "//?/" })
        {
            foreach (var popped in new[] { @"x\...\..", @"x\. \..", @"x\report \.\..", @"report \..\report" })
                Assert.Null(ReachablePath.Refuse(Spelled(prefix, Path.Combine(_root, popped))));

            foreach (var kept in new[] { @"report \...\..", "report ", "report.", @"report \x", @"x\..\report." })
            {
                var path = Spelled(prefix, Path.Combine(_root, kept));

                Assert.True(ReachablePath.Refuse(path) is not null, $"{path} was not refused");
            }
        }

        Assert.Equal(Path.Combine(_root, "report"), Path.GetFullPath(Path.Combine(_root, @"report \...\..")));
    }

    /// <summary>
    /// **Through the literal prefix the name is its own**, so the verb acts on
    /// the file named and on nothing else: "report" and the other trailing
    /// name are where they were, with what they held.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\?\", "report ")]
    [InlineData(@"\\?\", "report.")]
    [InlineData(@"\??\", "report ")]
    [InlineData(@"\??\", "report.")]
    public async Task A_literal_extended_spelling_acts_on_the_name_it_was_given(string prefix, string name)
    {
        var path = prefix + Path.Combine(_root, name);
        var others = Contents.Where(c => c.Key != name).ToDictionary();

        Assert.Null(ReachablePath.Refuse(path));

        await Settled(_ops.Trash([path]));

        Assert.Equal([path], _asked);

        var copy = await Settled(_ops.Copy([path], Extended(_into), _ => ValueTask.FromResult(ConflictResolution.Skip)));

        Assert.Equal(OperationState.Completed, copy.State);
        Assert.Equal(new Dictionary<string, string> { [name] = Contents[name] }, Files(_into));
        Assert.Equal(Contents, Files(_root));

        File.Delete(Extended(Path.Combine(_into, name)));

        var move = await Settled(_ops.Move([path], Extended(_into), _ => ValueTask.FromResult(ConflictResolution.Skip)));

        Assert.Equal(OperationState.Completed, move.State);
        Assert.Equal(new Dictionary<string, string> { [name] = Contents[name] }, Files(_into));
        Assert.Equal(others, Files(_root));

        File.Move(Extended(Path.Combine(_into, name)), Extended(Path.Combine(_root, name)));

        var delete = await Settled(_ops.Delete([path]));

        Assert.Equal(OperationState.Completed, delete.State);
        Assert.Empty(delete.Problems);
        Assert.Equal(others, Files(_root));
    }
}
