using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The undo walk asks the name it moves FROM, not only where it lands.**
/// RunTravel refuses a step when either end is a name Win32 would fold. Every
/// undo-walk test had both ends spelled plainly, so each half of that check hid
/// the other: with the FROM half removed, TrailingChildTests stayed green (fix-7
/// round-3 verification). The half matters when the ends are spelled
/// differently — a move out of a folder opened through "\\?\" into a plainly
/// opened one, undone after "x..." has turned up in the moved folder beside an
/// "x" that cannot go back. The walk would then rename "…\album\x..." — which
/// Win32 opens as "…\album\x" — into the "\\?\" folder, where the name lands
/// exactly: "x"'s bytes arrive as "x...", and "x" itself is gone from where it
/// was left.
///
/// Instead the step waits, the undo says why in the refusal's own words, and
/// every file stays where it was.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingUndoWalkFromTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-undofrom").FullName;

    private static string Extended(string path) => @"\\?\" + path;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Extended(_root), "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.Delete(Extended(_root), recursive: true);
    }

    [WindowsFact]
    public async Task The_undo_walk_never_renames_from_a_folded_name_into_an_extended_folder()
    {
        var album = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(album, "x"), "x's own");

        var ops = new WindowsFileOperations();
        var move = ops.Move([Extended(album)], dst, _ => ValueTask.FromResult(ConflictResolution.Skip));
        await move.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(OperationState.Completed, move.State);

        var moved = Path.Combine(dst, "album");
        var late = Path.Combine(moved, "x...");
        File.WriteAllText(Extended(late), "arrived after the move");

        // The old folder is back, with a file where "x" was: the walk has to go
        // child by child, and "x" cannot go back, so it stays beside "x...".
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "x"), "a new file where x was");

        var undo = await Record.ExceptionAsync(async () => await ops.UndoAsync(CancellationToken.None));

        Assert.NotNull(undo);
        Assert.Equal("x's own", File.ReadAllText(Path.Combine(moved, "x")));
        Assert.Equal("arrived after the move", File.ReadAllText(Extended(late)));
        Assert.False(File.Exists(Extended(Path.Combine(album, "x..."))), "the undo renamed something to \"x...\" in the source");
        Assert.Equal("a new file where x was", File.ReadAllText(Path.Combine(album, "x")));
        Assert.Contains(ReachablePath.Refuse(late)!, undo.Message, StringComparison.Ordinal);
    }
}
