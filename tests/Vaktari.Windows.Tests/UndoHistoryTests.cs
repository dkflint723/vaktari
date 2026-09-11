using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// How far back Undo reaches.
///
/// **The history had no ceiling**, so a long session carried every operation it
/// had ever done until the window closed. It is a hundred deep now, and this
/// pins it from the side a user would notice: the hundred-and-first step back
/// is simply not there.
///
/// Exercised through <c>RecordCreation</c>, the cheapest thing that pushes an
/// entry — undoing one bins the file, and the bin is faked, so the hundred
/// undos below delete a hundred temp files and nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-undodepth-" + Guid.NewGuid().ToString("N")[..8]);

    public UndoHistoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private static WindowsFileOperations Ops()
        => new()
        {
            RecycleOverride = paths =>
            {
                foreach (var path in paths)
                    if (File.Exists(path)) File.Delete(path);

                return new RecycleResult(0, false);
            },
        };

    [WindowsFact]
    public async Task The_hundred_and_first_step_back_is_forgotten()
    {
        var ops = Ops();

        for (var i = 0; i < 101; i++)
        {
            var path = Path.Combine(_root, $"made-{i:000}.txt");
            File.WriteAllText(path, "x");
            ops.RecordCreation(path);
        }

        for (var i = 0; i < 100; i++)
        {
            Assert.True(ops.CanUndo, $"nothing to undo after {i} steps");
            await ops.UndoAsync(CancellationToken.None);
        }

        // A hundred steps took a hundred files back; the first one made is the
        // one the history let go, and it is still on disk.
        Assert.False(ops.CanUndo, "the history reached past its ceiling");
        Assert.True(File.Exists(Path.Combine(_root, "made-000.txt")),
            "the oldest creation was undone, so the ceiling did not hold");
        Assert.False(File.Exists(Path.Combine(_root, "made-100.txt")),
            "the newest creation was not undone");
    }
}
