using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Handing a folder to the copy that is already running.
///
/// **This is the whole of "open with Vaktari" once it is the default file
/// manager.** Every double-clicked folder, and every "show in folder" that
/// reaches us, arrives as a fresh launch — so a launch that cannot hand its
/// path over is a folder that never opens.
///
/// It could not, ever. Dispose deleted the socket file unconditionally, and
/// Program disposes the instance that LOST the lock, immediately before asking
/// it to forward: the launch unlinked the running window's socket and then
/// connected to the path it had just removed. Every handover for the rest of
/// that window's life failed the same way, and nothing said so — the result was
/// discarded and the "handed over" line printed before the attempt.
///
/// Its own runtime directory. These files are per-user, not per-process, and
/// running this against the real pair would delete the socket belonging to
/// whatever copy the author has open — reproducing the bug on their machine to
/// prove it exists.
/// </summary>
public sealed class SingleInstanceHandoverTests : IDisposable
{
    private readonly string _runtime = Path.Combine(
        Path.GetTempPath(), "vaktari-instance-tests-" + Guid.NewGuid().ToString("N")[..12]);

    public SingleInstanceHandoverTests()
    {
        Directory.CreateDirectory(_runtime);
        SingleInstance.RuntimeDirectoryOverride = _runtime;
    }

    /// <summary>
    /// **Cleanup must never be able to fail a test.** This caught IOException
    /// only, and Windows throws UnauthorizedAccessException from
    /// Directory.Delete when something still holds a file in it — which is
    /// exactly the state a just-closed socket is in for a moment. The suite
    /// failed twice that way, both times on the first run after a build when
    /// the file system is busiest, and each failure named a test whose subject
    /// had already passed. This machine currently carries 175 leftover
    /// vaktari-tests directories, so the delete failing is not rare here.
    ///
    /// Every other temp-directory cleanup in this repository already swallows
    /// everything, with the same reasoning written on it: a temp directory is
    /// not worth failing over.
    ///
    /// One retry after a short pause, because the usual cause is a handle being
    /// released rather than anything permanent. Only ever under this class's own
    /// GUID directory.
    /// </summary>
    public void Dispose()
    {
        SingleInstance.RuntimeDirectoryOverride = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(_runtime, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Waits briefly for the socket file rather than demanding it this
    /// instant. Binding creates it, but "created" and "visible to a stat on a
    /// machine with a virus scanner" are not the same moment, and a test that
    /// fails on that gap would be reporting the weather rather than the code.
    /// </summary>
    private static bool SocketAppeared()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (File.Exists(SingleInstance.SocketPath)) return true;
            Thread.Sleep(25);
        }

        return false;
    }

    [Fact]
    public void A_second_launch_does_not_take_the_lock()
    {
        using var first = new SingleInstance();
        Assert.True(first.TryAcquire());

        using var second = new SingleInstance();
        Assert.False(second.TryAcquire());
    }

    /// <summary>
    /// The defect itself: the losing launch is disposed before it forwards, and
    /// that disposal must leave the running instance's channel intact.
    /// </summary>
    [Fact]
    public void Disposing_the_launch_that_lost_leaves_the_socket_alone()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());
        Assert.True(SocketAppeared(), "the listener never bound");

        var launch = new SingleInstance();
        Assert.False(launch.TryAcquire());
        launch.Dispose();

        Assert.True(File.Exists(SingleInstance.SocketPath),
            "the launch deleted the running instance's socket");
    }

    /// <summary>
    /// End to end, in the order Program uses: lose the lock, dispose, forward.
    /// Asserting on TryForward's return rather than on the received paths keeps
    /// this off the dispatcher — delivery raises the event on the UI thread,
    /// which is the window's business and not this channel's.
    ///
    /// **An AvaloniaFact all the same, and so is the next one.** As plain
    /// facts, the running copy's accept loop was the first thing in the run to
    /// ask for Dispatcher.UIThread, from a socket thread, which then owned it:
    /// run as a class on its own on Linux, every AvaloniaFact after them failed
    /// its cleanup with "a different thread owns it". On the headless session,
    /// the dispatcher is the session's before anything is handed over.
    /// </summary>
    [AvaloniaFact]
    public void A_launch_that_lost_can_still_hand_its_paths_over()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        var launch = new SingleInstance();
        Assert.False(launch.TryAcquire());
        launch.Dispose();

        Assert.Equal(SingleInstance.Handover.Handed, SingleInstance.TryForward([Path.GetTempPath()]));
    }

    /// <summary>
    /// **Twice, because once was never the failing case.** The first handover
    /// destroyed the channel and only the second showed it — a test that
    /// forwarded a single path would have passed against the broken code.
    /// </summary>
    [AvaloniaFact]
    public void And_again_after_the_first_one()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        for (var i = 0; i < 3; i++)
        {
            var launch = new SingleInstance();
            launch.TryAcquire();
            launch.Dispose();

            Assert.Equal(SingleInstance.Handover.Handed, SingleInstance.TryForward([Path.GetTempPath()]));
        }
    }

    /// <summary>
    /// With nobody running there is nothing to hand to, and the caller has to
    /// be able to tell — that answer is what decides between forwarding and
    /// opening a window.
    /// </summary>
    [Fact]
    public void With_nothing_running_the_handover_reports_failure()
    {
        Assert.Equal(SingleInstance.Handover.NoAnswer, SingleInstance.TryForward([Path.GetTempPath()]));
    }

    /// <summary>
    /// A lock file that cannot be opened at all — read-only here; owned by
    /// somebody else in the /tmp days — is an answer, not a crash. Program
    /// takes false as "hand over if anyone answers, open a window if not",
    /// and either is better than a start that dies before the window.
    /// </summary>
    [Fact]
    public void A_lock_that_cannot_be_opened_is_reported_not_thrown()
    {
        File.WriteAllText(SingleInstance.LockPath, "");

        if (OperatingSystem.IsWindows())
            File.SetAttributes(SingleInstance.LockPath, FileAttributes.ReadOnly);
        else
            File.SetUnixFileMode(SingleInstance.LockPath, UnixFileMode.UserRead);

        try
        {
            using var launch = new SingleInstance();

            Assert.False(launch.TryAcquire());
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(SingleInstance.LockPath, FileAttributes.Normal);
            else
                File.SetUnixFileMode(SingleInstance.LockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// What the running copy's PathsReceived delivers for one handover, pumped
    /// on the headless dispatcher — delivery is raised on the UI thread — under
    /// a wall-clock ceiling. Null when nothing arrived, which is the failure the
    /// tests below are about, so it is an answer rather than a hang.
    /// </summary>
    private static string[]? Delivered(SingleInstance running, string[] sent)
    {
        string[]? received = null;
        running.PathsReceived += (_, paths) => received = paths;

        Assert.Equal(SingleInstance.Handover.Handed, SingleInstance.TryForward(sent));

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (received is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        return received;
    }

    /// <summary>
    /// **A second launch with no folder raised nothing.** It sends an empty
    /// message, and the running copy stopped at "nothing read" before the event
    /// — so the window stayed buried while the launch printed "raising the
    /// existing window". Empty is still a request, and the handler raises the
    /// window for it.
    /// </summary>
    [AvaloniaFact]
    public void A_launch_with_no_folder_still_reaches_the_window()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        var delivered = Delivered(running, []);

        Assert.NotNull(delivered);
        Assert.Empty(delivered);
    }

    /// <summary>
    /// **One read of 8 KiB was the whole message.** At 120 paths the last one
    /// delivered was "C:\Users\someone\D" — a folder that does not exist, or
    /// worse, one that does. Two hundred long paths is about 30 KB, several
    /// reads' worth, and every one must arrive whole.
    /// </summary>
    [AvaloniaFact]
    public void A_long_handover_arrives_whole()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        var sent = Enumerable.Range(0, 200)
            .Select(i => Path.Combine(Path.GetTempPath(), "a folder with a long name, number " + i,
                                      "and a child folder with a longer name still, ünïcödé " + i))
            .ToArray();

        Assert.Equal(sent, Delivered(running, sent));
    }

    /// <summary>
    /// **A space at either end of a name is part of the name on Linux**, and
    /// the handover trimmed it: a folder called "old " opened "old" — the
    /// wrong one, if there was one. What was sent is what arrives.
    /// </summary>
    [AvaloniaFact]
    public void A_name_keeps_the_spaces_at_its_ends()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        string[] sent = ["/home/me/old ", " /home/me/lead"];

        Assert.Equal(sent, Delivered(running, sent));
    }

    /// <summary>
    /// **A handover over the cap opened a second window.** The running copy
    /// refused it and closed; the launch, still blocked sending into a buffer
    /// far smaller than a megabyte, was reset, took that for "nobody answered",
    /// and started a copy of its own. It is measured before anything is sent
    /// and answered as what it is, and the running window is handed nothing.
    /// </summary>
    [AvaloniaFact]
    public void A_handover_over_the_cap_is_refused_before_it_is_sent()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        var delivered = false;
        running.PathsReceived += (_, _) => delivered = true;

        var sent = Enumerable.Range(0, SingleInstance.MaxHandoverBytes / 200 + 1)
            .Select(i => Path.Combine(Path.GetTempPath(), new string('x', 200) + i))
            .ToArray();

        // Over the cap wherever the temp folder is — "/tmp/" is short enough
        // that a fixture sized on Windows came in under it on Linux.
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(string.Join('\n', sent)) > SingleInstance.MaxHandoverBytes);

        Assert.Equal(SingleInstance.Handover.TooLarge, SingleInstance.TryForward(sent));

        // Long enough for a delivery that was coming to have come.
        var deadline = DateTime.UtcNow.AddMilliseconds(500);

        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.False(delivered, "the running window was handed a message over the cap");
    }

    /// <summary>
    /// The owner does clean up after itself: a socket file left behind would be
    /// connected to by the next launch, which would then believe it had handed
    /// its folder to a process that no longer exists.
    /// </summary>
    [Fact]
    public void The_owner_removes_the_socket_when_it_exits()
    {
        var running = new SingleInstance();
        Assert.True(running.TryAcquire());
        Assert.True(SocketAppeared());

        running.Dispose();

        Assert.False(File.Exists(SingleInstance.SocketPath));
    }
}
