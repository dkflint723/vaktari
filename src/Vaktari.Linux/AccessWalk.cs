using System.Runtime.InteropServices;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// A recursive permission change that goes by open folders, never by path.
///
/// **A folder swapped for a link mid-walk sent the change out of the tree.**
/// The walk decided a folder was real when it listed the parent, then opened it
/// by path much later, and changed every mode by path — chmod follows a link
/// anywhere along one. So somebody applying "others can read" to a shared,
/// group-writable folder could have another member of the group replace a
/// not-yet-visited subfolder with a link to their home, and the walk went on
/// to make ~/.ssh readable by everyone. Re-checking each popped path does not
/// close it: a link one level further up still reads as a real folder below.
///
/// So nothing below the chosen folder is reached by more than one name at a
/// time. Each entry is opened relative to its already-open parent with
/// O_NOFOLLOW — which opens a link as itself rather than what it points at —
/// its mode changed through that descriptor, and a folder descended into
/// through the descriptor it was opened as. A link found this way is skipped,
/// as before; one that appears after the listing is found the same way.
///
/// The mode is changed through <c>/proc/self/fd</c>, because fchmod refuses the
/// O_PATH descriptor a file that cannot be read is opened with — the route the
/// C library itself takes for the same question.
/// </summary>
internal static partial class AccessWalk
{
    /// <summary>
    /// Called with each folder's path once its names have been read and before
    /// any of them is touched: the one moment a swap has to land in, held open
    /// for a test.
    /// </summary>
    internal static Action<string>? Listed { get; set; }

    private const int AtFdCwd = -100;
    private const int ReadOnly = 0;
    private const int CloseOnExec = 0x80000;
    private const int PathOnly = 0x200000;

    private const int NotADirectory = 20;
    private const int TooManyLinks = 40;

    /// <summary>Not an errno: the name now answers with a different folder.</summary>
    private const int Replaced = -1;

    // The two flags whose values differ between the layouts Vaktari ships for:
    // x86-64 takes the generic ones, aarch64 its own.
    private static int DirectoryOnly => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    [LibraryImport("libc", EntryPoint = "openat",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(int directory, string name, int flags);

    [LibraryImport("libc", EntryPoint = "readlinkat",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ReadLinkAt(int directory, string name, ref byte buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    /// <summary>Whether this process can walk this way at all.</summary>
    internal static bool Available
        => OperatingSystem.IsLinux()
           && Environment.Is64BitProcess
           && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64
           // Both the listing and the change go through it. A container or
           // chroot without /proc takes the walk by path it had before.
           && Directory.Exists("/proc/self/fd");

    /// <summary>
    /// Changes the mode of the folder at <paramref name="rootPath"/>, through
    /// <paramref name="changeRoot"/>, and of everything under it.
    /// <paramref name="modeFor"/> is given whether the entry is a folder and
    /// its mode now, and answers the mode it should have.
    /// </summary>
    internal static AccessOutcome Apply(
        string rootPath,
        Action changeRoot,
        Func<bool, UnixFileMode, UnixFileMode> modeFor,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var done = 0;
        var skipped = 0;
        Exception? first = null;

        void Failed(string path, Exception e)
        {
            skipped++;
            first ??= e;
        }

        // One open folder per level of depth, never one per folder waiting to
        // be visited: a folder holding fifty thousand others would otherwise
        // hold fifty thousand descriptors.
        var stack = new Stack<Frame>();

        // **Listed before its mode changes, as every folder below is.** Reading
        // the names goes back through /proc and checks read permission afresh,
        // so a folder listed after losing its owner's read lists nothing; the
        // descriptor held open is what looks each name up afterwards, and that
        // needs only search. The chosen folder is opened following a link to
        // it: the person chose it.
        var root = OpenAt(AtFdCwd, rootPath, ReadOnly | DirectoryOnly | CloseOnExec);
        var rootNames = root >= 0 ? Names(root, rootPath, Failed) : null;

        try
        {
            changeRoot();
        }
        catch
        {
            if (root >= 0) Close(root);
            throw;
        }

        if (root < 0)
        {
            root = OpenAt(AtFdCwd, rootPath, ReadOnly | DirectoryOnly | CloseOnExec);

            if (root < 0) return new AccessOutcome(1, Error(rootPath, Marshal.GetLastPInvokeError()));

            rootNames = Names(root, rootPath, Failed);
        }

        try
        {
            stack.Push(new Frame(root, rootPath, rootNames!));

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var frame = stack.Peek();

                if (frame.Next >= frame.Names.Count)
                {
                    Close(stack.Pop().Fd);
                    continue;
                }

                var name = frame.Names[frame.Next++];
                var path = Path.Combine(frame.Path, name);

                var opened = Visit(frame.Fd, name, path, modeFor, Failed, out var inside, out var outcome);

                switch (outcome)
                {
                    case Outcome.Link:
                        // A link's own permissions mean nothing on Linux, and
                        // changing them means changing its target's. Counted so
                        // the report says the tree was not uniformly applied.
                        skipped++;
                        continue;

                    case Outcome.Failed failed:
                        Failed(path, failed.Error);
                        break;
                }

                if (++done % 200 == 0) progress?.Report(done);

                if (opened >= 0) stack.Push(new Frame(opened, path, inside!));
            }
        }
        finally
        {
            while (stack.Count > 0) Close(stack.Pop().Fd);
        }

        progress?.Report(done);

        return new AccessOutcome(skipped, first);
    }

    private sealed class Frame(int fd, string path, List<string> names)
    {
        public int Fd { get; } = fd;
        public string Path { get; } = path;
        public List<string> Names { get; } = names;
        public int Next { get; set; }
    }

    private abstract record Outcome
    {
        public sealed record Changed : Outcome;
        public sealed record Link : Outcome;
        public sealed record Failed(Exception Error) : Outcome;
    }

    /// <summary>
    /// One entry: its mode changed, and for a folder, a descriptor to look its
    /// names up by and the names themselves — or -1.
    /// </summary>
    private static int Visit(
        int parent,
        string name,
        string path,
        Func<bool, UnixFileMode, UnixFileMode> modeFor,
        Action<string, Exception> failed,
        out List<string>? names,
        out Outcome outcome)
    {
        names = null;

        // The entry as itself: a link is opened as the link.
        var entry = OpenAt(parent, name, PathOnly | NoFollow | CloseOnExec);

        if (entry < 0)
        {
            outcome = new Outcome.Failed(Error(path, Marshal.GetLastPInvokeError()));
            return -1;
        }

        try
        {
            // Only a link has a target to read, and an empty name asks it of
            // the descriptor itself.
            var probe = new byte[1];
            if (ReadLinkAt(entry, "", ref probe[0], 1) >= 0)
            {
                outcome = new Outcome.Link();
                return -1;
            }

            var through = $"/proc/self/fd/{entry}";

            // **What it is, from the descriptor whose mode will change** — not
            // from a second look by name, which something else may answer by
            // then. Through /proc, stat reaches the object opened, and that is
            // not a link: the line above has said so.
            var self = FileIdentity.Full(through);

            // Opened and listed BEFORE its mode changes, so taking away the
            // owner's own read still reaches what is inside; and again after,
            // for a folder that could not be read until now. By name, not
            // following, and only if it is the same folder as the one opened:
            // whatever answers to this name now, a link or another folder is
            // refused.
            bool isFolder;
            int inside = -1, errno = 0;

            if (self is { } known)
            {
                isFolder = (known.Mode & 0xF000) == 0x4000;

                if (isFolder) (inside, errno) = OpenSame(parent, name, known);
            }
            else
            {
                // A C library that cannot stat: the open's own answer, which
                // is the next best — a file, or a name that has become a link,
                // says "not a folder".
                (inside, errno) = OpenFolder(parent, name);
                isFolder = errno != NotADirectory && errno != TooManyLinks;
            }

            if (inside >= 0) names = Names(inside, path, failed);

            try
            {
                var now = File.GetUnixFileMode(through);
                File.SetUnixFileMode(through, modeFor(isFolder, now));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (inside >= 0) Close(inside);

                outcome = new Outcome.Failed(e);
                return -1;
            }

            if (isFolder && inside < 0)
            {
                (inside, errno) = self is { } again ? OpenSame(parent, name, again) : OpenFolder(parent, name);

                if (inside >= 0) names = Names(inside, path, failed);
            }

            // A folder whose mode changed but whose inside could not be
            // reached is not a tree uniformly applied, and says so.
            outcome = isFolder && inside < 0
                ? new Outcome.Failed(Error(path, errno))
                : new Outcome.Changed();

            return isFolder ? inside : -1;
        }
        finally
        {
            Close(entry);
        }
    }

    /// <summary>
    /// The folder by this name, opened to be read — but only if it is the one
    /// <paramref name="known"/> describes.
    /// </summary>
    private static (int Fd, int Errno) OpenSame(int parent, string name, (ulong Device, ulong Inode, uint Mode) known)
    {
        var (fd, errno) = OpenFolder(parent, name);

        if (fd < 0) return (fd, errno);

        if (FileIdentity.Full($"/proc/self/fd/{fd}") is { } opened
            && opened.Device == known.Device
            && opened.Inode == known.Inode)
            return (fd, 0);

        Close(fd);
        return (-1, Replaced);
    }

    private static (int Fd, int Errno) OpenFolder(int parent, string name)
    {
        var fd = OpenAt(parent, name, ReadOnly | DirectoryOnly | NoFollow | CloseOnExec);

        return (fd, fd < 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    /// <summary>The names in an open folder, read through its descriptor.</summary>
    private static List<string> Names(int fd, string path, Action<string, Exception> failed)
    {
        var names = new List<string>();

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            };

            names.AddRange(new System.IO.Enumeration.FileSystemEnumerable<string>(
                $"/proc/self/fd/{fd}",
                (ref System.IO.Enumeration.FileSystemEntry e) => e.FileName.ToString(),
                options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed(path, e);
        }

        Listed?.Invoke(path);

        return names;
    }

    private static IOException Error(string path, int errno)
        => new(errno == Replaced
            ? $"{path}: was replaced while it was being changed"
            : $"{path}: {Marshal.GetPInvokeErrorMessage(errno)}");
}
