using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a rename, a move or a bin says when something has the item open.
///
/// **A folder held from below read "Access to the path 'D:\…\Photos' is
/// denied."** Windows answers ERROR_ACCESS_DENIED for a folder with anything
/// open beneath it, and .NET hands that over as a plain IOException, so its
/// message went straight to the status line — reading as a permission problem
/// to somebody who had a document open in the folder (rename-notes, plan §0).
/// Three sentences now, each about what was measured, and the permission
/// sentence only for a real refusal of permission.
/// </summary>
public class InUseWordingTests
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int AccessDenied = unchecked((int)0x80070005);

    [Fact]
    public void A_file_something_else_has_open()
        => Assert.Equal(
            "something else has that file open",
            Failures.Describe(new InUseException(@"C:\x\a.txt", isDirectory: false, SharingViolation), "rename that"));

    [Fact]
    public void A_folder_with_something_open_inside_it()
        => Assert.Equal(
            "something inside that folder is open",
            Failures.Describe(new InUseException(@"C:\x\Photos", isDirectory: true, AccessDenied), "rename that"));

    [Fact]
    public void A_folder_that_is_itself_open()
        => Assert.Equal(
            "something has that folder open",
            Failures.Describe(
                new InUseException(@"C:\x\Photos", isDirectory: true, SharingViolation, itselfOpen: true),
                "rename that"));

    /// <summary>
    /// **Ahead of every arm that reads the code.** An InUseException IS an
    /// IOException, carrying the access-denied code it arrived with, and the
    /// arm for a bare 0x80070005 below it would otherwise answer first.
    /// </summary>
    [Fact]
    public void The_sentence_is_the_exception_s_own_message_too()
    {
        var held = new InUseException(@"C:\x\Photos", isDirectory: true, AccessDenied);

        Assert.Equal(Failures.Describe(held), held.Message);
        Assert.Equal(AccessDenied, held.HResult);
    }

    /// <summary>
    /// A refusal the engine could tell was about permission is still said as
    /// one — with the verb of what was being done, not "do that".
    /// </summary>
    [Fact]
    public void Permission_is_still_permission()
        => Assert.Equal(
            "you do not have permission to rename that",
            Failures.Describe(new UnauthorizedAccessException("Access to the path is denied."), "rename that"));

    /// <summary>
    /// **One the engine could not tell apart says it could not.** Choosing
    /// either sentence for certain would be a guess, and .NET's own message
    /// is the one that read as permission.
    /// </summary>
    [Fact]
    public void An_access_denied_nobody_could_tell_apart_says_both()
    {
        var said = Failures.Describe(
            new IOException("Access to the path 'D:\\x\\Photos' is denied.") { HResult = AccessDenied });

        Assert.Equal(
            "Windows would not let go of that — something may have it open, or you may not have permission",
            said);
    }

    /// <summary>A sharing violation that never went through the engine's
    /// questions keeps the sentence it always had.</summary>
    [Fact]
    public void A_bare_sharing_violation_keeps_its_sentence()
        => Assert.Equal(
            "something else has that file open",
            Failures.Describe(new IOException("in use") { HResult = SharingViolation }));
}
