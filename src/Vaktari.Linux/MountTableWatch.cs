using System.Runtime.InteropServices;
using Vaktari.Core;

namespace Vaktari.Linux;

/// <summary>
/// Hears the kernel say the mount table changed, and udev say a device came or
/// went, so the device watch looks then rather than once a second.
///
/// **poll() on /proc/self/mountinfo, not inotify.** inotify on a procfs file
/// hands back a watch that never fires — DeviceWatch's doc records it — because
/// the content is generated at read time. The kernel does make the mount table
/// pollable: a mount or unmount in this namespace wakes poll() with
/// POLLPRI|POLLERR. Measured in a throwaway user namespace mounting a tmpfs:
/// both wake it in under a millisecond, and twenty idle seconds cost 0.2 ms of
/// CPU and one context switch. On a desktop with snap or flatpak it also wakes
/// for each squashfs loop they mount, which is a nudge, a quarter-second settle
/// and one cheap look — still far below a look every second.
///
/// **A FileSystemWatcher on /dev/disk/by-uuid** covers what the mount table
/// cannot: a stick plugged into a desktop that does not automount it arrives as
/// a device, not a mount, and the provider offers such volumes to be mounted.
/// udev makes and removes the links there.
///
/// **poll is bound before anything is opened** (review M6). The Linux tests
/// also run on Windows, where there is no libc at all; asking poll first is
/// what makes that host fail in the "no poll here" branch the fallback exists
/// for, rather than in a file open that would have hidden it.
///
/// **A pipe of its own wakes the thread on Dispose**, so stopping does not wait
/// for a timeout that, with the pipe, the poll no longer needs.
/// </summary>
internal sealed unsafe partial class MountTableWatch : IDisposable
{
    private const short POLLIN = 0x001;
    private const short POLLPRI = 0x002;
    private const short POLLERR = 0x008;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }


    private readonly Action _nudge;
    private readonly SafeHandle _table;
    private readonly int _wakeRead;
    private readonly int _wakeWrite;
    private readonly Thread _thread;
    private readonly FileSystemWatcher? _devices;
    private volatile bool _disposed;

    private MountTableWatch(Action nudge, SafeHandle table, int wakeRead, int wakeWrite, string? devices)
    {
        _nudge = nudge;
        _table = table;
        _wakeRead = wakeRead;
        _wakeWrite = wakeWrite;
        _thread = new Thread(Run) { IsBackground = true, Name = "vaktari-mount-watch" };

        if (devices is not null && Directory.Exists(devices))
        {
            try
            {
                _devices = new FileSystemWatcher(devices);
                _devices.Created += (_, _) => _nudge();
                _devices.Deleted += (_, _) => _nudge();
                _devices.Renamed += (_, _) => _nudge();
                _devices.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // The mount table is still heard; a stick that is not mounted
                // waits for the fallback look.
                Quiet.Swallowed("places", ex);
                _devices?.Dispose();
                _devices = null;
            }
        }
    }

    /// <summary>The thread, for the test that stopping ends it.</summary>
    internal bool Ended => !_thread.IsAlive;

    // ---- one watch for the process ------------------------------------------

    /// <summary>
    /// **One watch for the process, however many providers subscribe.** The
    /// mount table and the device links are facts about the machine, and every
    /// test window builds a platform of its own: a watch each was a thread and
    /// an inotify instance each, and the Ui suite already runs at WSL's
    /// inotify ceiling (memory: 120 of 128). So the first subscriber starts the
    /// one watch and it lives for the process; its nudge reaches every
    /// subscriber. The same bargain the Windows device window makes.
    /// </summary>
    private static readonly Lock Shared = new();

    private static MountTableWatch? _shared;
    private static readonly Dictionary<Subscription, Action> Subscribers = [];

    /// <summary>How many shared watches this process has started — one. For
    /// the tests.</summary>
    internal static int SharedStarted { get; private set; }

    /// <summary>A subscription to the process's watch, or null when this host
    /// cannot watch — <paramref name="failure"/> says which step failed, and
    /// the next subscriber tries again.</summary>
    internal static IDisposable? Subscribe(Action nudge, out string? failure)
    {
        lock (Shared)
        {
            failure = null;

            if (_shared is null)
            {
                _shared = Start(FanOut, out failure);
                if (_shared is null) return null;
                SharedStarted++;
            }

            var subscription = new Subscription();
            Subscribers[subscription] = nudge;
            return subscription;
        }
    }

    private static void FanOut()
    {
        Action[] nudges;

        lock (Shared) nudges = [.. Subscribers.Values];

        foreach (var nudge in nudges)
        {
            try { nudge(); }
            catch (Exception ex) { Quiet.Swallowed("places", ex); }
        }
    }

    private sealed class Subscription : IDisposable
    {
        public void Dispose()
        {
            lock (Shared) Subscribers.Remove(this);
        }
    }

    /// <summary>
    /// A running watch that calls <paramref name="nudge"/> when the mount table
    /// or the device links change, or null when this host cannot, with <paramref name="failure"/> saying why — the caller
    /// then keeps looking every second, as before this existed.
    /// </summary>
    internal static MountTableWatch? Start(
        Action nudge,
        out string? failure,
        string table = "/proc/self/mountinfo",
        string? devices = "/dev/disk/by-uuid")
    {
        failure = null;

        // First, and alone: is there a poll() to call at all?
        try
        {
            if (Poll(null, 0, 0) < 0) return Fail("poll", out failure);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Fail("poll", out failure);
        }

        int* pair = stackalloc int[2];

        if (Pipe(pair) != 0) return Fail("pipe", out failure);

        Microsoft.Win32.SafeHandles.SafeFileHandle handle;

        try
        {
            // Opening is what arms it: the kernel notes the table's event
            // count at open, and poll() reports any change after that. One
            // read as well, so a path that opens but cannot be read fails
            // here rather than in the thread.
            handle = File.OpenHandle(table, FileMode.Open, FileAccess.Read);

            Span<byte> scratch = stackalloc byte[256];
            _ = RandomAccess.Read(handle, scratch, 0);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
            Close(pair[0]);
            Close(pair[1]);
            return Fail("open", out failure);
        }

        var watch = new MountTableWatch(nudge, handle, pair[0], pair[1], devices);
        watch._thread.Start();
        return watch;
    }

    /// <summary>Gives up, saying which step did — "poll", "pipe" or "open" —
    /// so the log, and the test that a host without libc fails at poll, can
    /// tell the branches apart.</summary>
    private static MountTableWatch? Fail(string why, out string? failure)
    {
        failure = why;
        return null;
    }

    private void Run()
    {
        var fds = stackalloc PollFd[2];

        try
        {
            while (!_disposed)
            {
                fds[0] = new PollFd { Fd = (int)_table.DangerousGetHandle(), Events = POLLPRI };
                fds[1] = new PollFd { Fd = _wakeRead, Events = POLLIN };

                var ready = Poll(fds, 2, -1);

                if (_disposed) return;

                if (ready < 0)
                {
                    // EINTR is the only one worth another turn; anything else
                    // is a table that cannot be polled, and the floor stays.
                    if (Marshal.GetLastPInvokeError() == 4) continue;
                    return;
                }

                if ((fds[0].Revents & (POLLPRI | POLLERR)) != 0) _nudge();
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        _devices?.Dispose();

        // One byte down the pipe ends the poll at once.
        byte one = 1;
        _ = Write(_wakeWrite, &one, 1);

        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));

        Close(_wakeRead);
        Close(_wakeWrite);
        _table.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static partial int Pipe(int* fds);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
