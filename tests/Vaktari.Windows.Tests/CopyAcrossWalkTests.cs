using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The walk inside a marked folder goes where the engine goes, and no
/// further** (batch-0.11.2b QA). CopyAcrossPlan now looks down each marked
/// folder for a name the engine would refuse, and claims to walk it as
/// WindowsFileOperations.Descend does: a link planned as itself and never
/// followed, a folder that cannot be read passed over. Each test here builds
/// the shape on disk, asks the plan, and hands the plan to the real engine —
/// so a walk that looked further than the engine (leaving out a folder the
/// copy would have taken) or stopped short of it (offering one the copy then
/// refuses whole) shows as a disagreement between the two.
///
/// Junctions are made by mklink, as TempTree makes them; the denied folder by
/// a deny entry for the person running the test, removed again after.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CopyAcrossWalkTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose()
    {
        // The names ending in a space reach only through "\\?\"; the junctions
        // are removed as links first, so nothing walks through one.
        foreach (var link in _links)
        {
            try
            {
                var info = new DirectoryInfo(link);
                if (!info.Exists) continue;
                info.Attributes &= ~FileAttributes.ReadOnly;
                info.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        try { Directory.Delete(@"\\?\" + _tree.Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        _tree.Dispose();
    }

    private readonly List<string> _links = [];

    private string Junction(string relative, string target)
    {
        var path = _tree.Junction(relative, target);
        _links.Add(path);
        return path;
    }

    /// <summary>A folder elsewhere in the tree holding "report ", which no
    /// plainly spelled path can read.</summary>
    private string Elsewhere()
    {
        var target = _tree.Dir("elsewhere");
        File.WriteAllText(@"\\?\" + Path.Combine(target, "report "), "behind the link");
        return target;
    }

    private static Dictionary<string, CompareMark> OnlyHere(params string[] paths)
        => paths.ToDictionary(p => p, _ => CompareMark.OnlyHere, StringComparer.Ordinal);

    private static async Task<IOperationHandle> Run(CopyAcrossPlan plan)
    {
        var handle = new WindowsFileOperations().Copy(
            plan.Sources, plan.Destination, conflict => ValueTask.FromResult(plan.Decide(conflict)));

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        return handle;
    }

    /// <summary>
    /// **A junction inside a marked folder is planned as itself.** The engine
    /// copies the link and never reads behind it, so "report " behind it is
    /// never asked about; a walk that followed the link would leave the folder
    /// out over a name the copy never meets.
    /// </summary>
    [WindowsFact]
    public async Task A_link_inside_a_marked_folder_is_not_followed()
    {
        var target = Elsewhere();
        var docs = _tree.Dir("src", "docs");
        File.WriteAllText(Path.Combine(docs, "fine.txt"), "fine");
        Junction(@"src\docs\link", target);
        var dst = _tree.Dir("dst");

        var plan = CopyAcrossPlan.From(OnlyHere(docs), dst);

        Assert.Empty(plan.Withheld);
        Assert.Equal([docs], plan.Missing);

        var handle = await Run(plan);
        _links.Add(Path.Combine(dst, "docs", "link"));

        Assert.Null(handle.Error?.Message);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("fine", File.ReadAllText(Path.Combine(dst, "docs", "fine.txt")));
        Assert.True((File.GetAttributes(Path.Combine(dst, "docs", "link")) & FileAttributes.ReparsePoint) != 0,
                    "the link was not copied as a link");
    }

    /// <summary>
    /// **A marked row that is itself a junction is planned as itself**, as
    /// the engine plans a root that is a link: nothing behind it is read.
    /// </summary>
    [WindowsFact]
    public async Task A_marked_link_is_not_looked_inside()
    {
        var target = Elsewhere();
        var link = Junction(@"src\shortcut", target);
        var dst = _tree.Dir("dst");

        var plan = CopyAcrossPlan.From(OnlyHere(link), dst);

        Assert.Empty(plan.Withheld);
        Assert.Equal([link], plan.Missing);

        var handle = await Run(plan);
        _links.Add(Path.Combine(dst, "shortcut"));

        Assert.Null(handle.Error?.Message);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.True((File.GetAttributes(Path.Combine(dst, "shortcut")) & FileAttributes.ReparsePoint) != 0,
                    "the link was not copied as a link");
    }

    /// <summary>
    /// **A folder inside that cannot be read is passed over, not thrown
    /// from** — the engine says which it could not read and copies the rest,
    /// so the plan must not leave the marked folder out over it, nor stop
    /// planning. And the walk goes on past it: a bad name in a readable
    /// sibling is still found.
    /// </summary>
    [WindowsFact]
    public void A_folder_that_cannot_be_read_is_passed_over_and_the_walk_goes_on()
    {
        var docs = _tree.Dir("src", "docs");
        File.WriteAllText(Path.Combine(docs, "fine.txt"), "fine");
        var locked = _tree.Dir("src", "docs", "locked");

        // The walk takes the last folder it listed first, so "z", which
        // cannot be read, is met before "a", which holds the bad name.
        var other = _tree.Dir("src", "other");
        var sealedAway = _tree.Dir("src", "other", "z");
        Directory.CreateDirectory(Path.Combine(other, "a"));
        File.WriteAllText(@"\\?\" + Path.Combine(other, "a", "report."), "dot");

        var dst = _tree.Dir("dst");

        using (Deny(locked))
        using (Deny(sealedAway))
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(locked));

            var plan = CopyAcrossPlan.From(OnlyHere(docs, other), dst);

            Assert.Equal([docs], plan.Missing);
            Assert.Equal([new Withheld(other, WithheldBecause.NameWindowsCannotOpen, @"a\report.")], plan.Withheld);
        }
    }

    private static Denied Deny(string folder) => new(folder);

    /// <summary>A deny entry for the person running the test on one folder,
    /// removed again when done so the tree can be cleaned up.</summary>
    private sealed class Denied : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly FileSystemAccessRule _rule;

        public Denied(string path)
        {
            _directory = new DirectoryInfo(path);
            _rule = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

            var security = _directory.GetAccessControl();
            security.AddAccessRule(_rule);
            _directory.SetAccessControl(security);
        }

        public void Dispose()
        {
            var security = _directory.GetAccessControl();
            security.RemoveAccessRuleAll(_rule);
            _directory.SetAccessControl(security);
        }
    }
}
