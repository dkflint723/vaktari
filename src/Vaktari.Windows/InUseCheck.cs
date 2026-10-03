using System.Runtime.InteropServices;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Tells "something has it open" apart from "you may not", for a refusal that
/// arrives as the same error code either way.
///
/// **A folder held from below and a folder this person may not rename both
/// answer ERROR_ACCESS_DENIED.** Measured on Windows 11 (rename-notes,
/// rmprobe/perm.cs and the review's perm case): Directory.Move gave IOException
/// 0x80070005 for a watcher on a subfolder, for a file open deep inside, for a
/// process whose current folder was a subfolder — and for an access-control
/// list denying DELETE on the folder, DELETE_CHILD on its parent, or
/// ADD_SUBDIRECTORY on the parent. Two cheap questions separate them, because
/// each asks for exactly one of the rights a rename needs:
///
/// - the folder opened for DELETE: refused with 5 when the person may not
///   remove it from where it is, refused with 32 when it is itself open
///   without delete sharing (somebody's current folder), granted otherwise;
/// - its parent opened for FILE_ADD_SUBDIRECTORY: refused with 5 when the
///   person may not make it appear under a new name there.
///
/// Neither was refused for any of the held cases, and one of them was refused
/// for every denial. Only error 5 is read as permission (review finding 23);
/// 32 is in use. Not measured: SMB, FAT and ReFS, Controlled Folder Access and
/// cloud-file filters, which also answer 5 and would read as permission here —
/// a wrong sentence, never a wrong action.
/// </summary>
internal static class InUseCheck
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int AccessDenied = unchecked((int)0x80070005);

    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    private const uint DELETE = 0x00010000;
    private const uint FILE_ADD_SUBDIRECTORY = 0x00000004;

    /// <summary>
    /// The failure to throw for <paramref name="refused"/>: an
    /// <see cref="InUseException"/> when something has it open, a plain
    /// UnauthorizedAccessException when the person may not, and the original
    /// otherwise.
    /// </summary>
    internal static Exception Classify(string path, bool isDirectory, Exception refused)
    {
        if (refused is InUseException) return refused;

        var code = refused.HResult;

        if (code is SharingViolation or LockViolation)
        {
            // **Which folder is open is asked, not assumed.** Directory.Move
            // answers 32 when the folder itself is somebody's current folder,
            // but the shell answers 32 for a folder with a file open INSIDE it
            // (review, shellmove) — so the sentence comes from the disk.
            return !isDirectory
                ? new InUseException(path, isDirectory: false, code)
                : new InUseException(path, isDirectory: true, code, itselfOpen: Ask(path) == Answer.ItselfOpen);
        }

        // A file refused with 5 is a real refusal — read-only media, an ACL —
        // because a file open somewhere answers 32, not 5. Only a folder is
        // ambiguous.
        if (code != AccessDenied || !isDirectory) return refused;

        return Ask(path) switch
        {
            Answer.Permission => new UnauthorizedAccessException(refused.Message, refused),
            Answer.ItselfOpen => new InUseException(path, isDirectory: true, SharingViolation, itselfOpen: true),
            _ => new InUseException(path, isDirectory: true, code),
        };
    }

    internal enum Answer { Held, ItselfOpen, Permission }

    /// <summary>The two questions, asked of the disk.</summary>
    internal static Answer Ask(string folder)
    {
        switch (Open(folder, DELETE))
        {
            case ErrorAccessDenied: return Answer.Permission;
            case ErrorSharingViolation: return Answer.ItselfOpen;
        }

        if (PathRules.Parent(folder) is { } parent && Open(parent, FILE_ADD_SUBDIRECTORY) == ErrorAccessDenied)
            return Answer.Permission;

        return Answer.Held;
    }

    /// <summary>Opens and closes at once; answers the Win32 error, or 0.</summary>
    private static int Open(string path, uint access)
    {
        var handle = Native.CreateFile(
            path, access, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return Marshal.GetLastPInvokeError();

        Native.CloseHandle(handle);
        return 0;
    }
}
