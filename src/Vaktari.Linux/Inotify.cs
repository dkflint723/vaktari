using System.Runtime.InteropServices;
using System.Text;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// One inotify instance for the whole process, read by one thread, with a
/// watch per folder that every watcher of that folder shares.
///
/// **A watched folder that was deleted kept its inotify instance until the
/// process exited** (batch-0.11.2b QA). .NET 10's FileSystemWatcher on Linux
/// opens an instance per watcher, and when the folder it watches is deleted
/// the kernel drops the watch; StopRaisingEvents then has nothing to wake the
/// blocked reader with, and the descriptor stays open — through Dispose and a
/// full collection alike (measured on 10.0.11: ten watchers, folders deleted,
/// all disposed, ten instances still open). In the application that is one
/// instance for every folder deleted from outside while a pane showed it:
/// twenty such folders, twenty instances, against a per-user ceiling of 128
/// past which every pane falls back to polling and other programs cannot
/// watch anything at all. A full Fedora Ui run peaked at 120.
///
/// So the instance is Vaktari's own. One per process however many folders are
/// watched; a watch per folder (the kernel hands back the same descriptor for
/// a folder already watched, and the watchers of it are counted on it); a
/// reader that waits in poll() on the instance and on an eventfd, so letting
/// it go is a write rather than a wait on a read that may never return. The
/// instance is closed when the last watch is disposed, and opened again by the
/// next one.
///
/// **The events are the ones FileSystemWatcher raised**, configured as
/// LinuxFileSystemProvider configured it — the same mask, so the kernel sends
/// the same events — and mapped to <see cref="FileSystemChange"/> as its
/// handlers mapped them: a create Added, a delete Removed, a write or an
/// attribute change Changed, a rename within the folder Renamed (the two
/// halves paired by their cookie), a move out Removed and a move in Added.
/// Measured against 10.0.11 for each (vaktari-batch-0.11.2c-notes/fswsem).
/// What it could not say it says now: the folder deleted, moved away or
/// unmounted is <see cref="ChangeKind.Gone"/> — FileSystemWatcher raised
/// nothing at all for any of the three — and an overflowed queue is
/// <see cref="ChangeKind.Lost"/>, or Gone when the folder is not there.
///
/// **Callbacks run on the reader thread**, one at a time, as a
/// FileSystemWatcher's ran on its own: a handler that blocks holds up every
/// watch in the process, which is why the ones Vaktari installs only queue
/// and post. One that throws is logged and the reader goes on. A watch
/// disposed while its folder's events are being read hears nothing that is
/// read after; one callback already under way on the reader thread can still
/// finish.
/// </summary>
internal sealed partial class Inotify
{
    // ---- libc ---------------------------------------------------------------

    [LibraryImport("libc", EntryPoint = "inotify_init1", SetLastError = true)]
    private static partial int InitOne(int flags);

    [LibraryImport("libc", EntryPoint = "inotify_add_watch",
                   StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int AddWatch(int descriptor, string path, uint mask);

    [LibraryImport("libc", EntryPoint = "inotify_rm_watch", SetLastError = true)]
    private static partial int RemoveWatch(int descriptor, int watch);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    private static partial int EventFd(uint initial, int flags);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int Poll(PollFd* descriptors, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint Read(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint Write(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Descriptor;
        public short Events;
        public short Returned;
    }

    // The same values on every Linux layout Vaktari ships for: O_NONBLOCK and
    // O_CLOEXEC are the generic ones on x86-64 and aarch64 alike, and inotify,
    // eventfd and poll take them as they are.
    private const int NonBlocking = 0x800;
    private const int CloseOnExec = 0x80000;
    private const short PollIn = 0x1;
    private const short PollError = 0x8;
    private const short PollHangUp = 0x10;
    private const short PollInvalid = 0x20;

    private const int Interrupted = 4;
    private const int NoSuchEntry = 2;
    private const int TryAgain = 11;
    private const int AccessDenied = 13;
    private const int NotADirectory = 20;
    private const int TooManyOpenFiles = 24;
    private const int TooManyOpenInSystem = 23;
    private const int NoSpace = 28;

    internal const uint Modify = 0x2;
    internal const uint Attrib = 0x4;
    internal const uint MovedFrom = 0x40;
    internal const uint MovedTo = 0x80;
    internal const uint Create = 0x100;
    internal const uint Delete = 0x200;
    internal const uint DeleteSelf = 0x400;
    internal const uint MoveSelf = 0x800;
    internal const uint Unmount = 0x2000;
    internal const uint Overflow = 0x4000;
    internal const uint Ignored = 0x8000;
    private const uint OnlyDirectory = 0x1000000;
    private const uint ExcludeUnlinked = 0x4000000;

    /// <summary>
    /// What every folder is watched for. FileSystemWatcher's mask for the
    /// filters LinuxFileSystemProvider gave it (FileName, DirectoryName,
    /// LastWrite, Size): create, delete, modify, attrib and the two halves of
    /// a move, folders only, nothing about a name already unlinked. Plus the
    /// two the folder itself raises when it goes, which it left out. The
    /// symbolic link a folder may be named through is followed, as it was.
    /// </summary>
    internal const uint Mask = Create | Delete | Modify | Attrib | MovedFrom | MovedTo
                               | DeleteSelf | MoveSelf | OnlyDirectory | ExcludeUnlinked;

    // ---- the process's instance ----------------------------------------------

    /// <summary>Every change to which instance is open, which watches it holds
    /// and who listens to them is made under this, as is the look-up the
    /// reader makes for each event. Never held while a callback runs.</summary>
    private static readonly Lock Gate = new();

    private static Inotify? _shared;

    /// <summary>False once libc has turned out to have no inotify: a host
    /// that is not Linux, or a C library without the symbols.</summary>
    private static bool _available = OperatingSystem.IsLinux();

    /// <summary>Whether watching goes through here. Where it does not,
    /// <see cref="FolderWatch"/> takes a FileSystemWatcher as before.</summary>
    internal static bool Available => Volatile.Read(ref _available);

    /// <summary>The instance open now, if any. For the tests.</summary>
    internal static Inotify? Shared
    {
        get { lock (Gate) return _shared; }
    }

    /// <summary>
    /// Watches <paramref name="path"/> through the process's instance,
    /// opening it if none is open. Throws as FileSystemWatcher did when the
    /// folder cannot be watched — an IOException naming the limit when it is
    /// the inotify ceiling — so the caller falls back to polling as before.
    /// Throws <see cref="DllNotFoundException"/> or
    /// <see cref="EntryPointNotFoundException"/> when there is no inotify at
    /// all, after which <see cref="Available"/> is false.
    /// </summary>
    internal static IDisposable Watch(string path, Action<FileSystemChange> onChange)
    {
        lock (Gate)
        {
            Inotify instance;

            try
            {
                instance = _shared ??= Open();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Volatile.Write(ref _available, false);
                throw;
            }

            return instance.AddLocked(path, onChange);
        }
    }

    // ---- one instance ------------------------------------------------------------

    private readonly int _descriptor;
    private readonly int _wake;
    private readonly Dictionary<int, Folder> _folders = [];
    private readonly ManualResetEventSlim _exited = new();
    private int _listeners;
    private bool _closing;

    private Inotify(int descriptor, int wake)
    {
        _descriptor = descriptor;
        _wake = wake;
    }

    /// <summary>
    /// A new instance with its reader running, independent of the shared one.
    /// The application has only the shared one; a test makes its own to count
    /// what it opens and closes.
    /// </summary>
    internal static Inotify Open()
    {
        var descriptor = InitOne(NonBlocking | CloseOnExec);

        if (descriptor < 0) throw Refused(Marshal.GetLastPInvokeError(), null);

        var wake = EventFd(0, NonBlocking | CloseOnExec);

        if (wake < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            Close(descriptor);
            throw Refused(errno, null);
        }

        var instance = new Inotify(descriptor, wake);

        new Thread(instance.Run)
        {
            IsBackground = true,
            Name = "Vaktari inotify",
        }.Start();

        return instance;
    }

    /// <summary>The instance's descriptor, for a test reading its fdinfo.</summary>
    internal int Descriptor => _descriptor;

    /// <summary>Set once the reader has stopped and both descriptors are
    /// closed. For the tests.</summary>
    internal WaitHandle Exited => _exited.WaitHandle;

    /// <summary>How many folders the kernel is watching for this instance.
    /// For the tests.</summary>
    internal int Folders
    {
        get { lock (Gate) return _folders.Count; }
    }

    /// <summary>Watches <paramref name="path"/> through this instance. The
    /// shared instance is reached through <see cref="Watch"/>.</summary>
    internal IDisposable Add(string path, Action<FileSystemChange> onChange)
    {
        lock (Gate) return AddLocked(path, onChange);
    }

    private Listener AddLocked(string path, Action<FileSystemChange> onChange)
    {
        ObjectDisposedException.ThrowIf(_closing, this);

        var watch = AddWatch(_descriptor, path, Mask);

        if (watch < 0)
        {
            var errno = Marshal.GetLastPInvokeError();

            // An instance opened for this watch alone is not kept for nothing.
            if (_listeners == 0) CloseLocked();

            throw Refused(errno, path);
        }

        if (!_folders.TryGetValue(watch, out var folder))
            _folders[watch] = folder = new Folder(watch);

        var listener = new Listener(this, folder, path, onChange);

        folder.Listeners = [.. folder.Listeners, listener];
        _listeners++;

        return listener;
    }

    /// <summary>
    /// What a refusal is thrown as: what FileSystemWatcher threw for a folder
    /// that is not there, and an IOException naming the limit for the two
    /// ceilings, which the pane's message passes on.
    /// </summary>
    internal static Exception Refused(int errno, string? path) => errno switch
    {
        NoSuchEntry or NotADirectory when path is not null
            => new DirectoryNotFoundException($"The directory '{path}' does not exist."),
        AccessDenied when path is not null
            => new UnauthorizedAccessException($"Access to the path '{path}' is denied."),
        NoSpace
            => new IOException("The configured user limit on the number of inotify watches "
                               + "(fs.inotify.max_user_watches) has been reached."),
        TooManyOpenFiles
            => new IOException("The configured user limit on the number of inotify instances "
                               + "(fs.inotify.max_user_instances), or this process's limit on open "
                               + "files, has been reached."),
        TooManyOpenInSystem
            => new IOException("The system limit on open files has been reached."),
        _ => new IOException(Marshal.GetPInvokeErrorMessage(errno) + (path is null ? "" : $" : '{path}'")),
    };

    /// <summary>Lets go of one listener: its folder's watch goes with the
    /// last of them, and this instance with its last listener.</summary>
    private void Remove(Listener listener)
    {
        lock (Gate)
        {
            if (listener.Disposed) return;

            listener.Disposed = true;

            var folder = listener.Folder;

            folder.Listeners = Array.FindAll(folder.Listeners, l => !ReferenceEquals(l, listener));

            if (folder.Listeners.Length == 0
                && _folders.TryGetValue(folder.Watch, out var current)
                && ReferenceEquals(current, folder))
            {
                _folders.Remove(folder.Watch);

                // **EINVAL is expected and ignored**: the kernel has already
                // dropped a watch whose folder was deleted or unmounted, and
                // the IN_IGNORED that says so may not have been read yet.
                if (!_closing) _ = RemoveWatch(_descriptor, folder.Watch);
            }

            if (--_listeners == 0) CloseLocked();
        }
    }

    /// <summary>Stops the reader, which closes the descriptors once it has.
    /// Under <see cref="Gate"/>; nothing calls into the kernel on this
    /// instance's descriptor after it.</summary>
    private void CloseLocked()
    {
        if (_closing) return;

        _closing = true;

        if (ReferenceEquals(_shared, this)) _shared = null;

        Wake();
    }

    private unsafe void Wake()
    {
        ulong one = 1;
        _ = Write(_wake, (byte*)&one, sizeof(ulong));
    }

    // ---- the reader ------------------------------------------------------------

    /// <summary>How long a move out of a folder waits for its other half
    /// before it is taken for a move out of sight. The kernel queues the two
    /// halves one after the other, so they are nearly always read together;
    /// this covers a read that ends between them.</summary>
    private const int PairingWait = 10;

    private const int BufferSize = 64 * 1024;

    private unsafe void Run()
    {
        var buffer = (byte*)NativeMemory.Alloc(BufferSize);
        var fds = stackalloc PollFd[2];
        Pending? held = null;
        var failed = false;

        try
        {
            while (true)
            {
                fds[0] = new PollFd { Descriptor = _descriptor, Events = PollIn };
                fds[1] = new PollFd { Descriptor = _wake, Events = PollIn };

                var ready = Poll(fds, 2, held is null ? -1 : PairingWait);

                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted) continue;

                    failed = true;
                    break;
                }

                if (fds[1].Returned != 0) break;

                if (ready == 0)
                {
                    // Nothing followed a move out: it went out of sight.
                    if (held is { } alone) Deliver(alone.Watch, ChangeKind.Removed, alone.Name, null);
                    held = null;
                    continue;
                }

                if ((fds[0].Returned & PollIn) == 0)
                {
                    if ((fds[0].Returned & (PollError | PollHangUp | PollInvalid)) != 0)
                    {
                        failed = true;
                        break;
                    }

                    continue;
                }

                var length = Read(_descriptor, buffer, BufferSize);

                if (length < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno is Interrupted or TryAgain) continue;

                    failed = true;
                    break;
                }

                Dispatch(new ReadOnlySpan<byte>(buffer, (int)length), ref held);
            }
        }
        catch (Exception e)
        {
            Vaktari.Core.Quiet.Swallowed("watch", e);
            failed = true;
        }
        finally
        {
            NativeMemory.Free(buffer);

            if (failed) Failed();

            Close(_descriptor);
            Close(_wake);
            _exited.Set();
        }
    }

    /// <summary>A move out of a folder, waiting for the move in that would
    /// make it a rename.</summary>
    private readonly record struct Pending(int Watch, uint Cookie, string Name);

    /// <summary>One read's worth of events: a 16-byte header — wd, mask,
    /// cookie, len — and a name padded to <c>len</c>, the same on every
    /// architecture.</summary>
    private void Dispatch(ReadOnlySpan<byte> events, ref Pending? held)
    {
        var at = 0;

        while (at + 16 <= events.Length)
        {
            var watch = MemoryMarshal.Read<int>(events[at..]);
            var mask = MemoryMarshal.Read<uint>(events[(at + 4)..]);
            var cookie = MemoryMarshal.Read<uint>(events[(at + 8)..]);
            var length = (int)MemoryMarshal.Read<uint>(events[(at + 12)..]);

            var raw = events.Slice(at + 16, Math.Min(length, events.Length - at - 16));
            var end = raw.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(end < 0 ? raw : raw[..end]);

            at += 16 + length;

            // The other half of a held move, or a reason to let it go.
            if (held is { } from)
            {
                held = null;

                if ((mask & MovedTo) != 0 && cookie == from.Cookie)
                {
                    if (watch == from.Watch)
                    {
                        Deliver(watch, ChangeKind.Renamed, name, from.Name);
                    }
                    else
                    {
                        Deliver(from.Watch, ChangeKind.Removed, from.Name, null);
                        Deliver(watch, ChangeKind.Added, name, null);
                    }

                    continue;
                }

                Deliver(from.Watch, ChangeKind.Removed, from.Name, null);
            }

            Handle(watch, mask, cookie, name, ref held);
        }
    }

    private void Handle(int watch, uint mask, uint cookie, string name, ref Pending? held)
    {
        if ((mask & Overflow) != 0)
        {
            Overflowed();
            return;
        }

        if ((mask & (DeleteSelf | MoveSelf | Unmount)) != 0)
        {
            Deliver(watch, ChangeKind.Gone, "", null);
            return;
        }

        if ((mask & Ignored) != 0)
        {
            // The kernel has let go of the watch: the folder was deleted or
            // unmounted, or a dispose asked it to. Gone for whoever is still
            // listening, and the descriptor is not asked about again.
            Deliver(watch, ChangeKind.Gone, "", null);

            lock (Gate)
            {
                if (_folders.TryGetValue(watch, out var folder))
                {
                    _folders.Remove(watch);
                    folder.Dropped = true;
                }
            }

            return;
        }

        // Something about the folder itself rather than a name in it — its
        // own mode changed. FileSystemWatcher raised nothing for it either.
        if (name.Length == 0) return;

        if ((mask & MovedFrom) != 0) held = new Pending(watch, cookie, name);
        else if ((mask & MovedTo) != 0) Deliver(watch, ChangeKind.Added, name, null);
        else if ((mask & Create) != 0) Deliver(watch, ChangeKind.Added, name, null);
        else if ((mask & Delete) != 0) Deliver(watch, ChangeKind.Removed, name, null);
        else if ((mask & (Modify | Attrib)) != 0) Deliver(watch, ChangeKind.Changed, name, null);
    }

    /// <summary>Tells everyone listening to <paramref name="watch"/>, each in
    /// the spelling it asked for. Gone is said once to each, and nothing
    /// after it.</summary>
    private void Deliver(int watch, ChangeKind kind, string name, string? oldName)
    {
        Listener[] listeners;

        lock (Gate)
        {
            if (!_folders.TryGetValue(watch, out var folder)) return;

            listeners = folder.Listeners;
        }

        foreach (var listener in listeners)
        {
            if (kind == ChangeKind.Gone)
            {
                listener.Tell(new FileSystemChange(ChangeKind.Gone, listener.Path));
            }
            else
            {
                listener.Tell(new FileSystemChange(
                    kind,
                    Path.Combine(listener.Path, name),
                    oldName is null ? null : Path.Combine(listener.Path, oldName)));
            }
        }
    }

    /// <summary>
    /// The queue overflowed and events were dropped, for every folder alike:
    /// Lost to each listener whose folder is still there, Gone to one whose
    /// folder is not — the question the old Error handler asked.
    /// </summary>
    private void Overflowed()
    {
        foreach (var listener in AllListeners())
            listener.Tell(new FileSystemChange(
                Directory.Exists(listener.Path) ? ChangeKind.Lost : ChangeKind.Gone, listener.Path));
    }

    /// <summary>
    /// The reader could not go on. Nothing more will be heard through this
    /// instance, so every listener is told its listing may be stale — the
    /// reload that follows opens a fresh instance.
    /// </summary>
    private void Failed()
    {
        lock (Gate)
        {
            _closing = true;
            if (ReferenceEquals(_shared, this)) _shared = null;
        }

        foreach (var listener in AllListeners())
            listener.Tell(new FileSystemChange(ChangeKind.Lost, listener.Path));
    }

    private List<Listener> AllListeners()
    {
        lock (Gate) return _folders.Values.SelectMany(f => f.Listeners).ToList();
    }

    /// <summary>One watched folder: the kernel's descriptor for it and who is
    /// listening. Listeners is replaced, never changed, so the reader can
    /// walk a copy without the lock.</summary>
    private sealed class Folder(int watch)
    {
        public int Watch { get; } = watch;

        public Listener[] Listeners { get; set; } = [];

        /// <summary>The kernel has dropped this watch.</summary>
        public bool Dropped { get; set; }
    }

    /// <summary>One watcher, as handed to the caller.</summary>
    private sealed class Listener(Inotify owner, Folder folder, string path, Action<FileSystemChange> onChange)
        : IDisposable
    {
        public Folder Folder { get; } = folder;

        public string Path { get; } = path;

        /// <summary>Written under <see cref="Gate"/>; read by the reader
        /// without it, so that a dispose stops the very next delivery.</summary>
        public bool Disposed
        {
            get => Volatile.Read(ref _disposed);
            set => Volatile.Write(ref _disposed, value);
        }

        private bool _disposed;
        private int _gone;

        public void Tell(FileSystemChange change)
        {
            if (Disposed || Volatile.Read(ref _gone) != 0) return;

            if (change.Kind == ChangeKind.Gone && Interlocked.Exchange(ref _gone, 1) != 0) return;

            try
            {
                onChange(change);
            }
            catch (Exception e)
            {
                Vaktari.Core.Quiet.Swallowed("watch", e);
            }
        }

        public void Dispose() => owner.Remove(this);
    }
}
