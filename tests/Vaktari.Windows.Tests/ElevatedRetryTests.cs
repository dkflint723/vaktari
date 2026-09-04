using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// "You do not have permission to copy that", and the route out of it.
///
/// **Elevation was launch-only.** A file could be run as administrator and a
/// terminal opened as one; a copy or a delete that NTFS refused had a retry
/// button that went again as the same person who had just been refused, which
/// fails the same way every time.
///
/// These use a real deny entry on a real directory, which needs no rights to
/// set: a person may always change the permissions on something they own. That
/// is what makes an access-denied failure reachable from a test at all — every
/// other way of making the engine fail produces a sharing violation, which is a
/// different fault with a different answer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedRetryTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    /// <summary>
    /// A deny entry for the person running the test, removed again when the
    /// test is done — without which the temporary tree cannot be cleaned up,
    /// because a deny on a folder denies reading it too.
    /// </summary>
    private sealed class Denied : IDisposable
    {
        private readonly DirectoryInfo? _directory;
        private readonly FileInfo? _file;
        private readonly FileSystemAccessRule _rule;

        private Denied(string path, FileSystemRights rights, bool onAFile)
        {
            // A file's rule carries no inheritance flags: setting them on a
            // leaf raises "This flag may not be set on a leaf object".
            _rule = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, rights,
                onAFile
                    ? InheritanceFlags.None
                    : InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Deny);

            if (onAFile)
            {
                _file = new FileInfo(path);

                var security = _file.GetAccessControl();
                security.AddAccessRule(_rule);
                _file.SetAccessControl(security);
            }
            else
            {
                _directory = new DirectoryInfo(path);

                var security = _directory.GetAccessControl();
                security.AddAccessRule(_rule);
                _directory.SetAccessControl(security);
            }
        }

        public static Denied Everything(string path)
            => new(path, FileSystemRights.FullControl, onAFile: false);

        /// <summary>The same, on one file rather than on a folder.</summary>
        public static Denied EverythingOnFile(string path)
            => new(path, FileSystemRights.FullControl, onAFile: true);

        public static Denied Removal(string path)
            => new(path, FileSystemRights.Delete
                       | FileSystemRights.DeleteSubdirectoriesAndFiles, onAFile: false);

        public void Dispose()
        {
            if (_file is { } file)
            {
                var security = file.GetAccessControl();
                security.RemoveAccessRuleAll(_rule);
                file.SetAccessControl(security);

                return;
            }

            var directory = _directory!;

            var folder = directory.GetAccessControl();
            folder.RemoveAccessRuleAll(_rule);
            directory.SetAccessControl(folder);
        }
    }

    /// <summary>
    /// The whole thing, up to the point a test can follow it: a copy into a
    /// folder NTFS will not let this person write, and an offer to go again
    /// with rights — naming that file, into that folder.
    /// </summary>
    [WindowsFact]
    public async Task A_copy_refused_for_permission_offers_an_administrator_retry()
    {
        using var tree = new TempTree();

        var source = tree.Write("a.txt", "one");
        var into = tree.Dir("protected");

        var ops = new WindowsFileOperations();

        using (Denied.Everything(into))
        {
            var handle = ops.Copy([source], into, Always(ConflictResolution.Overwrite));

            await handle.Completion;

            Assert.NotNull(handle.Retry);

            var elevated = handle.Retry.AsAdministrator;

            Assert.NotNull(elevated);
            Assert.Equal(ElevatedVerb.Copy, elevated.Verb);
            Assert.Equal(into, elevated.Destination);
            Assert.Equal([source], elevated.Sources);
        }
    }

    /// <summary>
    /// **A locked file is offered no such thing.** Closing the program holding
    /// it is the answer; administrator rights are not, and a shielded button
    /// that changes nothing teaches somebody to reach for the consent prompt
    /// when the consent prompt is not the answer.
    /// </summary>
    [WindowsFact]
    public async Task A_locked_file_is_offered_the_plain_retry_and_no_other()
    {
        using var tree = new TempTree();

        var locked = tree.Write("busy.txt", "one");
        var into = tree.Dir("dst");

        var ops = new WindowsFileOperations();

        using var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var handle = ops.Copy([locked], into, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.NotNull(handle.Retry);
        Assert.Null(handle.Retry.AsAdministrator);
    }

    /// <summary>
    /// A delete refused for permission gets one too, with no destination —
    /// there is no second place for a delete to go.
    /// </summary>
    [WindowsFact]
    public async Task A_delete_refused_for_permission_offers_one_with_no_destination()
    {
        using var tree = new TempTree();

        var doomed = tree.Write("keep/it.txt", "one");
        var ops = new WindowsFileOperations();

        using (Denied.Removal(tree.At("keep")))
        {
            var handle = ops.Delete([doomed]);

            await handle.Completion;

            Assert.NotNull(handle.Retry);

            var elevated = handle.Retry.AsAdministrator;

            Assert.NotNull(elevated);
            Assert.Equal(ElevatedVerb.Delete, elevated.Verb);
            Assert.Null(elevated.Destination);
            Assert.Equal([doomed], elevated.Sources);
        }
    }

    /// <summary>
    /// **The elevated offer reaches whole SOURCES only, and this is the limit
    /// somebody will meet.** A denied file INSIDE a copied folder is a plain
    /// access-denied failure with no elevated route: the root's target is the
    /// destination plus its whole relative path, and the elevated side works a
    /// target out as destination plus leaf name, so "dst" plus "secret.txt" is
    /// not "dst\stuff\secret.txt" and the request would land the file
    /// somewhere nobody asked for.
    ///
    /// The ordinary retry still stands, which is why this is a limit and not a
    /// dead end. Widening it means carrying a per-root sub-destination across
    /// the trust boundary — a separate decision, not a detail of this one.
    /// </summary>
    [WindowsFact]
    public async Task A_denied_file_inside_a_copied_folder_is_offered_no_rights()
    {
        using var tree = new TempTree();

        var folder = tree.Dir("stuff");
        var secret = tree.Write("stuff/secret.txt", "one");
        var into = tree.Dir("dst");

        var ops = new WindowsFileOperations();

        using (Denied.EverythingOnFile(secret))
        {
            var handle = ops.Copy([folder], into, Always(ConflictResolution.Overwrite));

            await handle.Completion;

            Assert.NotNull(handle.Retry);
            Assert.Equal(1, handle.Retry.Count);
            Assert.Null(handle.Retry.AsAdministrator);
        }
    }

    /// <summary>
    /// A clean run offers neither. The button is absent rather than present and
    /// doing nothing.
    /// </summary>
    [WindowsFact]
    public async Task A_clean_run_offers_no_administrator_retry()
    {
        using var tree = new TempTree();

        var source = tree.Write("a.txt", "one");
        var into = tree.Dir("dst");

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([source], into, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Null(handle.Retry);
    }
}
