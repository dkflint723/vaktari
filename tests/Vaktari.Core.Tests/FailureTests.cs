using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What the application says when something goes wrong.
///
/// **It used to speak .NET.** A folder it could not open reported
/// "UnauthorizedAccessException: Access to the path 'D:\x' is denied." in the
/// status bar, while the listing behind it — from the same catch block — said
/// "you do not have permission to open this folder". The readable sentence
/// already existed and only one of the two places used it.
/// </summary>
public class FailureTests
{
    /// <summary>
    /// **The type name is the one thing never worth showing.** It is a fact
    /// about the code rather than about the file, and it is what somebody ends
    /// up pasting into a search engine instead of reading.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryKind))]
    public void No_message_names_a_class(Exception failure)
    {
        var said = Failures.Describe(failure);

        Assert.NotEmpty(said);
        Assert.DoesNotContain("Exception", said, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Exception> EveryKind =>
    [
        new DirectoryNotFoundException("Could not find a part of the path 'D:\\x'."),
        new FileNotFoundException("Could not find file 'D:\\x'."),
        new UnauthorizedAccessException("Access to the path 'D:\\x' is denied."),
        new IOException("The disk is full.") { HResult = unchecked((int)0x80070070) },
        new OperationCanceledException(),
        new ArgumentException("A name cannot be empty."),
    ];

    [Fact]
    public void A_missing_folder_says_so_in_words()
    {
        Assert.Equal(
            "that folder is not there any more",
            Failures.Describe(new DirectoryNotFoundException("Could not find a part of the path.")));
    }

    /// <summary>
    /// **What they were doing goes in the sentence.** "You do not have
    /// permission" on its own leaves somebody wondering to do what — the caller
    /// knows, so it says.
    /// </summary>
    [Fact]
    public void Permission_says_what_was_refused()
    {
        Assert.Equal(
            "you do not have permission to open that folder",
            Failures.Describe(new UnauthorizedAccessException("denied"), "open that folder"));

        Assert.Equal(
            "you do not have permission to do that",
            Failures.Describe(new UnauthorizedAccessException("denied")));
    }

    /// <summary>The two failures anybody actually hits while copying, both of
    /// which arrive as a bare IOException with a code in it.</summary>
    [Theory]
    [InlineData(0x80070020, "something else has that file open")]
    [InlineData(0x80070021, "something else has that file open")]
    [InlineData(0x80070070, "there is not enough room on the disk")]
    [InlineData(0x80070027, "there is not enough room on the disk")]
    public void The_common_io_failures_are_named(long hresult, string expected)
    {
        var failure = new IOException("some win32 text") { HResult = unchecked((int)hresult) };

        Assert.Equal(expected, Failures.Describe(failure));
    }

    /// <summary>
    /// **The exception's own message is the fallback, not the enemy.** Vaktari
    /// raises this one itself, and rewording it would lose the name it carries.
    /// </summary>
    [Fact]
    public void A_message_written_for_people_is_kept()
    {
        Assert.Equal(
            "'notes.txt' already exists here.",
            Failures.Describe(new IOException("'notes.txt' already exists here.")));
    }

    /// <summary>
    /// **A drive that was not there answered in Win32's words.** Opening a
    /// disconnected Z: put "The network path was not found. : 'Z:\\'" in the
    /// status bar — the path handed back with a colon dropped into the middle
    /// of it — and an empty optical drive said "The device is not ready."
    /// </summary>
    [Theory]
    [InlineData(0x80070015, "that drive is not ready")]
    [InlineData(0x80070035, "that network drive is not connected")]
    [InlineData(0x80070043, "that network drive is not connected")]
    [InlineData(0x800704CF, "that network drive is not connected")]
    public void A_drive_that_is_not_there_says_so_in_words(long hresult, string expected)
    {
        var failure = new IOException("The network path was not found. : 'Z:\\'")
        {
            HResult = unchecked((int)hresult),
        };

        Assert.Equal(expected, Failures.Describe(failure, "open that folder"));
    }

    [Fact]
    public void Cancelling_is_not_a_failure_to_explain()
    {
        Assert.Equal("cancelled", Failures.Describe(new OperationCanceledException()));
    }
}
