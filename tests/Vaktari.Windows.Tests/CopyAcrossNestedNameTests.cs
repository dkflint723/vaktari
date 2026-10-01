using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The plan and the engine agree about a name further down a folder.**
/// CopyAcrossPlan asked only each marked row's own name, while the engine
/// asks every item it plans, a folder's contents included — so "report "
/// inside a marked folder of a "\\?\" side passed the plan and the engine
/// then refused the WHOLE copy: "plain.txt" beside the folder was not copied
/// either, and the person heard only afterwards (batch-0.11.2 QA,
/// probe-copy-across-nested, which this repeats).
///
/// The plan now leaves that folder out and the copy it hands the real engine
/// completes, with the plain file there and nothing of the folder. Into a
/// side opened through "\\?\" the folder goes, with its names as they are.
/// All in a temporary folder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CopyAcrossNestedNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-acrossnest").FullName;

    public void Dispose()
    {
        try { Directory.Delete(@"\\?\" + _root, recursive: true); } catch (IOException) { }
    }

    private string Left()
    {
        var src = Path.Combine(_root, "src");

        Directory.CreateDirectory(@"\\?\" + Path.Combine(src, "docs", "deep"));
        File.WriteAllText(@"\\?\" + Path.Combine(src, "docs", "deep", "report "), "nested");
        File.WriteAllText(@"\\?\" + Path.Combine(src, "docs", "fine.txt"), "fine");
        File.WriteAllText(@"\\?\" + Path.Combine(src, "plain.txt"), "plain");

        return src;
    }

    private static async Task<IOperationHandle> Run(CopyAcrossPlan plan)
    {
        var handle = new WindowsFileOperations().Copy(
            plan.Sources, plan.Destination, conflict => ValueTask.FromResult(plan.Decide(conflict)));

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        return handle;
    }

    [WindowsFact]
    public async Task Into_a_plain_side_the_folder_stays_and_the_file_beside_it_goes()
    {
        var src = Left();
        var dst = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;

        var docs = @"\\?\" + Path.Combine(src, "docs");
        var plain = @"\\?\" + Path.Combine(src, "plain.txt");

        var plan = CopyAcrossPlan.From(
            new Dictionary<string, CompareMark>(StringComparer.Ordinal)
            {
                [docs] = CompareMark.OnlyHere,
                [plain] = CompareMark.OnlyHere,
            },
            dst);

        Assert.Equal([new Withheld(docs, WithheldBecause.NameTheOtherSideCannotTake, @"deep\report ")], plan.Withheld);

        var handle = await Run(plan);

        Assert.Null(handle.Error?.Message);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("plain", File.ReadAllText(Path.Combine(dst, "plain.txt")));
        Assert.False(Directory.Exists(Path.Combine(dst, "docs")), "the folder left out was copied anyway");
    }

    [WindowsFact]
    public async Task Into_a_side_opened_through_the_prefix_the_folder_goes_with_its_names()
    {
        var src = Left();
        var dst = @"\\?\" + Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;

        var docs = @"\\?\" + Path.Combine(src, "docs");
        var plain = @"\\?\" + Path.Combine(src, "plain.txt");

        var plan = CopyAcrossPlan.From(
            new Dictionary<string, CompareMark>(StringComparer.Ordinal)
            {
                [docs] = CompareMark.OnlyHere,
                [plain] = CompareMark.OnlyHere,
            },
            dst);

        Assert.Empty(plan.Withheld);

        var handle = await Run(plan);

        Assert.Null(handle.Error?.Message);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("nested", File.ReadAllText(Path.Combine(dst, "docs", "deep", "report ")));
        Assert.Equal("plain", File.ReadAllText(Path.Combine(dst, "plain.txt")));
    }
}
