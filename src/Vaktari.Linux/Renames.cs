using System.Runtime.InteropServices;

namespace Vaktari.Linux;

/// <summary>
/// The rename that will not replace, and the plain one to fall back to.
///
/// **.NET has no rename that refuses a taken name.** File.Move without
/// overwrite looks first and renames after, and the window between the two is
/// not theoretical: measured on 2026-09-12 in WSL Fedora, a file created in
/// that window was replaced in about 17,000 of 30,000 attempts. The undo of a
/// move must never replace something it did not put there, so it asks the
/// kernel to decide in one call instead.
///
/// <c>renameat2</c> with RENAME_NOREPLACE is that call, and it takes every kind
/// in one: files, folders, symbolic links to either, and links whose target has
/// gone. That is worth as much as the atomicity — File.Move and Directory.Move
/// force the caller to know which it has before it asks, and a dangling link is
/// exactly where that guess goes wrong.
///
/// Errnos are returned rather than thrown, because the caller routes on them:
/// EEXIST is a name already taken, which is an answer and not a failure, and
/// EXDEV means the entry has to travel rather than be renamed.
/// </summary>
internal static partial class Renames
{
    /// <summary>Paths are resolved from the working directory, as the string
    /// ones they came from already are.</summary>
    private const int AtFdCwd = -100;

    /// <summary>RENAME_NOREPLACE: refuse rather than replace.</summary>
    private const uint NoReplaceFlag = 1;

    internal const int OperationNotPermitted = 1;

    internal const int NameTaken = 17;

    internal const int NotSameDevice = 18;

    internal const int NotValid = 22;

    internal const int NotImplemented = 38;

    internal const int NotSupported = 95;

    /// <summary>
    /// **Latched for the process the first time libc turns out not to have it.**
    /// glibc has exported renameat2 since 2.28 and musl exports it too, so the
    /// miss is rare — but a miss throws on the call rather than at load, and
    /// catching it on every entry afterwards would pay for the absence again and
    /// again.
    ///
    /// Not a NativeLibrary.TryGetExport probe: a probe that answers wrongly
    /// answers wrongly in the quiet direction, and every rename after it would
    /// silently give up the no-replace guarantee this class exists for. A throw
    /// on the first real call cannot be wrong about anything.
    /// </summary>
    private static bool _absent;

    /// <summary>
    /// **Two ways for it not to be there, and the second is not exotic.**
    /// EntryPointNotFoundException is a libc without the symbol;
    /// DllNotFoundException is no libc at all, which is every run of these
    /// operations on a host that is not Linux. The test suite does exactly that
    /// — CI runs `dotnet test vaktari.slnx` on the Windows runner, so
    /// Vaktari.Linux.Tests executes there, and the plain facts among them drive
    /// this engine against the real filesystem. Catching only the first left
    /// four of them throwing; measured on CI, not here.
    /// </summary>
    private static bool Missing(Exception e)
        => e is EntryPointNotFoundException or DllNotFoundException;

    /// <summary>
    /// Renames <paramref name="from"/> to <paramref name="to"/> only if the name
    /// is free. Zero when it went; otherwise an errno, with
    /// <see cref="NotImplemented"/> standing for a libc that has no renameat2 —
    /// the same answer a filesystem which does not implement the flag gives, and
    /// the caller has one road for both.
    /// </summary>
    internal static int WithoutReplacing(string from, string to)
    {
        if (_absent) return NotImplemented;

        try
        {
            return RenameAt2(AtFdCwd, from, AtFdCwd, to, NoReplaceFlag) == 0
                ? 0
                : Marshal.GetLastPInvokeError();
        }
        catch (Exception e) when (Missing(e))
        {
            _absent = true;
            return NotImplemented;
        }
    }

    /// <summary>
    /// The plain rename, which REPLACES a taken name and is therefore only ever
    /// reached after the caller has looked and found the name free. That look is
    /// the window renameat2 exists to close, and the caller says in its own
    /// comment why it accepts it here: the alternative is refusing to work at
    /// all on a filesystem that does not implement the flag, and /mnt/c is one.
    /// </summary>
    internal static int Plainly(string from, string to)
    {
        if (!_absent)
        {
            try
            {
                return Rename(from, to) == 0 ? 0 : Marshal.GetLastPInvokeError();
            }
            catch (Exception e) when (Missing(e))
            {
                _absent = true;
            }
        }

        // **No libc at all**, so there is no syscall to reach for and the walk
        // still has to work: these operations run against the real filesystem on
        // the Windows CI runner, where what is under test is the walk's own
        // rules rather than the kernel's. .NET's own renames do not replace —
        // Directory.Move has no overwrite and File.Move without one refuses a
        // taken name — so the guarantee holds; what is lost is the atomicity,
        // and the caller has already looked before it gets here.
        try
        {
            if ((File.GetAttributes(from) & FileAttributes.Directory) != 0) Directory.Move(from, to);
            else File.Move(from, to);

            return 0;
        }
        catch (IOException)
        {
            return NameTaken;
        }
        catch (Exception)
        {
            return OperationNotPermitted;
        }
    }

    /// <summary>Whether <paramref name="errno"/> says the flag is not available
    /// here — from the kernel, the filesystem, or libc — rather than that the
    /// rename itself was refused.</summary>
    internal static bool NoFlagHere(int errno)
        => errno is NotValid or NotImplemented or NotSupported or OperationNotPermitted;

    // Source-generated marshalling and UTF-8 strings, the shape Xattrs already
    // uses here and for the same reasons: it survives trimming and the AOT
    // publish, and the kernel takes paths as byte strings encoded the way .NET's
    // own file APIs encode them.

    [LibraryImport("libc", EntryPoint = "renameat2",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(
        int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [LibraryImport("libc", EntryPoint = "rename",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Rename(string oldPath, string newPath);
}
