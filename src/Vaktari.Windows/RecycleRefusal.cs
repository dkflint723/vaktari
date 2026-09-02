using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>What SHFileOperation reported, as it reported it.</summary>
/// <param name="Status">Zero when it worked; otherwise a Win32 error or a DE_*
/// code.</param>
/// <param name="Aborted">fAnyOperationsAborted — the user declined the warning
/// about a file too big for the bin.</param>
internal readonly record struct RecycleResult(int Status, bool Aborted);

/// <summary>
/// Turning the shell's number into something a person can read.
///
/// **A refused recycle said "SHFileOperation returned 32".** No file was named,
/// nothing suggested what to do, and the number is from an API the reader has
/// never heard of — while the very same refusal, arriving through any other
/// route in this application, reads "something else has that file open". The
/// Linux engine has produced plain sentences from its own per-item loop since
/// it was written.
///
/// **Two numbering schemes arrive through one int.** From 0x71 to 0xB7 the
/// shell answers with its own DE_* set, which predates Win32 and collides with
/// it: 0xB7 is DE_ERROR_MAX and also ERROR_ALREADY_EXISTS, 0x81 is
/// DE_FILENAMETOOLONG and also ERROR_WAIT_NO_CHILDREN. Below that range the
/// shell hands up ordinary Win32 codes — 32 is ERROR_SHARING_VIOLATION, and 112
/// is ERROR_DISK_FULL, which lands one short of the range. So the range check
/// is not tidiness; it is the only thing that tells the two apart.
///
/// Where a Win32 code is what arrived, this returns the exception TYPE rather
/// than a message, so <see cref="Failures.Describe"/> supplies the sentence and
/// a denied recycle reads exactly as a denied copy already does.
/// </summary>
internal static class RecycleRefusal
{
    private const int FirstShellCode = 0x71;   // DE_SAMEFILE
    private const int LastShellCode = 0xB7;    // DE_ERROR_MAX
    private const int OnDestination = 0x10000; // ERRORONDEST, ored into the code

    private const int ErrorFileNotFound = 0x02;
    private const int ErrorPathNotFound = 0x03;
    private const int ErrorAccessDenied = 0x05;
    private const int DeOpCancelled = 0x75;
    private const int DeAccessDeniedSrc = 0x78;

    internal static Exception For(int status)
    {
        var code = status & ~OnDestination;

        return code switch
        {
            // The type, not a message: Failures.Describe matches on these and
            // supplies the wording used everywhere else in the application.
            ErrorAccessDenied or DeAccessDeniedSrc => new UnauthorizedAccessException(),
            ErrorFileNotFound => new FileNotFoundException(),
            ErrorPathNotFound => new DirectoryNotFoundException(),
            DeOpCancelled => new OperationCanceledException(),

            >= FirstShellCode and <= LastShellCode => new IOException(Shell(code)),

            // Carries the HRESULT, which is what Failures.Describe reads to
            // recognise a sharing violation or a full disk. Windows' own
            // sentence is the fallback for the codes it does not know.
            _ => new IOException(
                new System.ComponentModel.Win32Exception(code).Message,
                unchecked((int)(0x80070000 | (uint)code))),
        };
    }

    private static string Shell(int code) => code switch
    {
        0x74 => "a drive's root folder cannot be sent to the bin",
        0x79 => "that path is too deep for the bin",
        0x7C => "that path is not one Windows will accept",
        0x81 => "that name is too long for the bin",
        0x85 => "it is too big for the bin",
        0x86 or 0x87 or 0x88 => "it is on a disc, which has no bin",
        _ => "the bin would not take it",
    };
}
